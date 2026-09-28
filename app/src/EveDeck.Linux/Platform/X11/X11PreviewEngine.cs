using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using EveDeck.Models;

namespace EveDeck.Linux.Platform.X11;

// Where one EVE client's live preview goes.
public sealed record PreviewTileSpec(IntPtr Source, WindowRect Rect);

// Live previews of EVE clients, the Linux counterpart of the Windows app's DWM thumbnails.
//
// How: XComposite keeps each client's contents in an off-screen pixmap even when it is covered or
// off-screen; XRender scales that pixmap into our own tile window on the X server (no CPU readback, no
// GPU context of ours); XDamage says when a client actually drew something, so an idle client costs
// nothing. This is the same mechanism compositing window managers and OBS's window capture use, and it
// reads only what the X server already shows on screen -- never EVE's process or memory.
//
// Threading: Xlib connections are not safe to share, so this engine owns a private connection and one
// thread that does every X call. Other threads talk to it only through SetTiles / Dispose.
public sealed class X11PreviewEngine : IDisposable
{
    private readonly Thread _thread;
    private readonly ConcurrentQueue<IReadOnlyList<PreviewTileSpec>> _pending = new();
    private readonly Dictionary<IntPtr, Tile> _tilesBySource = new();
    private readonly Dictionary<IntPtr, Tile> _tilesByWindow = new();
    private readonly Dictionary<IntPtr, Tile> _tilesByDamage = new();
    private volatile bool _stop;
    private readonly TimeSpan _minFrameInterval;
    private X11WindowService? _x11;
    private int _damageEventBase;
    private IntPtr _tileFormat;

    // Fired on the engine thread when the user clicks a tile (after the engine has already asked the WM
    // to activate that client).
    public event Action<IntPtr>? TileClicked;

    public X11PreviewEngine(int maxFps = 30)
    {
        _minFrameInterval = TimeSpan.FromSeconds(1.0 / Math.Clamp(maxFps, 1, 240));
        _thread = new Thread(Run) { IsBackground = true, Name = "X11 previews" };
    }

    public void Start() => _thread.Start();

    // Replace the whole tile set. Tiles whose source stays are moved/resized in place, not recreated.
    public void SetTiles(IReadOnlyList<PreviewTileSpec> tiles) => _pending.Enqueue(tiles);

    private void Run()
    {
        using var x11 = new X11WindowService();
        _x11 = x11;
        var display = x11.Display;

        if (!Xlib.XCompositeQueryExtension(display, out _, out _))
            throw new InvalidOperationException("X server has no Composite extension; live previews need it.");
        if (!Xlib.XDamageQueryExtension(display, out _damageEventBase, out _))
            throw new InvalidOperationException("X server has no DAMAGE extension; live previews need it.");
        _tileFormat = Xlib.XRenderFindVisualFormat(display, Xlib.XDefaultVisual(display, Xlib.XDefaultScreen(display)));

        var ev = Marshal.AllocHGlobal(192); // sizeof(XEvent) on LP64
        try
        {
            var fd = Xlib.XConnectionNumber(display);
            while (!_stop)
            {
                while (_pending.TryDequeue(out var specs)) ApplyTiles(specs);

                while (Xlib.XPending(display) > 0)
                {
                    Xlib.XNextEvent(display, ev);
                    HandleEvent(ev);
                }

                var now = DateTime.UtcNow;
                var nextDue = TimeSpan.FromMilliseconds(50);
                foreach (var tile in _tilesBySource.Values)
                {
                    if (!tile.Dirty) continue;
                    var since = now - tile.LastDraw;
                    if (since >= _minFrameInterval) Draw(tile, now);
                    else if (_minFrameInterval - since < nextDue) nextDue = _minFrameInterval - since;
                }
                Xlib.XFlush(display);

                // Sleep until the server has something for us or a throttled redraw falls due.
                Libc.WaitReadable(fd, (int)Math.Max(1, nextDue.TotalMilliseconds));
            }
        }
        finally
        {
            foreach (var tile in _tilesBySource.Values.ToList()) DestroyTile(tile);
            Xlib.XSync(display, false);
            Marshal.FreeHGlobal(ev);
            _x11 = null;
        }
    }

    private void ApplyTiles(IReadOnlyList<PreviewTileSpec> specs)
    {
        var wanted = specs.ToDictionary(s => s.Source);
        foreach (var tile in _tilesBySource.Values.ToList())
            if (!wanted.ContainsKey(tile.Source)) DestroyTile(tile);

        foreach (var spec in specs)
        {
            if (_tilesBySource.TryGetValue(spec.Source, out var tile))
            {
                tile.Rect = spec.Rect;
                Xlib.XMoveResizeWindow(_x11!.Display, tile.Window, spec.Rect.X, spec.Rect.Y, (uint)spec.Rect.Width, (uint)spec.Rect.Height);
                UpdateTransform(tile);
                tile.Dirty = true;
            }
            else
            {
                CreateTile(spec);
            }
        }
    }

    private void CreateTile(PreviewTileSpec spec)
    {
        var x11 = _x11!;
        var d = x11.Display;
        var tile = new Tile { Source = spec.Source, Rect = spec.Rect };

        // Automatic redirection is reference-counted by the server and harmless when a compositor has
        // already redirected the window; without a compositor it is what gives the window a pixmap.
        Xlib.XCompositeRedirectWindow(d, spec.Source, Xlib.CompositeRedirectAutomatic);
        Xlib.XSelectInput(d, spec.Source, (IntPtr)Xlib.StructureNotifyMask);
        tile.Damage = Xlib.XDamageCreate(d, spec.Source, Xlib.XDamageReportNonEmpty);

        var attrs = new Xlib.XSetWindowAttributes
        {
            BackgroundPixel = IntPtr.Zero, // black until the first frame lands
            EventMask = (IntPtr)(Xlib.ButtonPressMask | Xlib.ExposureMask),
        };
        tile.Window = Xlib.XCreateWindow(d, x11.Root, spec.Rect.X, spec.Rect.Y, (uint)spec.Rect.Width, (uint)spec.Rect.Height,
            0, 0 /* CopyFromParent */, Xlib.InputOutput, IntPtr.Zero, Xlib.CWBackPixel | Xlib.CWEventMask, ref attrs);
        ConfigureTileWindow(tile);
        Xlib.XMapWindow(d, tile.Window);
        Xlib.XMoveResizeWindow(d, tile.Window, spec.Rect.X, spec.Rect.Y, (uint)spec.Rect.Width, (uint)spec.Rect.Height);
        tile.DestPicture = Xlib.XRenderCreatePicture(d, tile.Window, _tileFormat, 0, IntPtr.Zero);

        BindSource(tile);
        tile.Dirty = true;

        _tilesBySource[tile.Source] = tile;
        _tilesByWindow[tile.Window] = tile;
        _tilesByDamage[tile.Damage] = tile;
    }

    // A preview is a floating, undecorated, always-on-top window that never takes keyboard focus (a
    // click on it must switch clients, not pull focus to us) and stays off the taskbar and pager.
    private void ConfigureTileWindow(Tile tile)
    {
        var x11 = _x11!;
        var d = x11.Display;
        var w = tile.Window;
        var r = tile.Rect;

        Xlib.XStoreName(d, w, "EveDeck preview");
        SetAtoms(w, "_NET_WM_WINDOW_TYPE", "ATOM", "_NET_WM_WINDOW_TYPE_UTILITY");
        SetAtoms(w, "_NET_WM_STATE", "ATOM", "_NET_WM_STATE_ABOVE", "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER", "_NET_WM_STATE_STICKY");

        // _MOTIF_WM_HINTS: flags=decorations, decorations=none.
        var motif = x11.Atom("_MOTIF_WM_HINTS");
        Xlib.XChangeProperty(d, w, motif, motif, 32, Xlib.PropModeReplace, new IntPtr[] { 2, 0, 0, 0, 0 }, 5);

        // WM_HINTS: flags=InputHint, input=False.
        var wmHints = x11.Atom("WM_HINTS");
        Xlib.XChangeProperty(d, w, wmHints, wmHints, 32, Xlib.PropModeReplace, new IntPtr[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 }, 9);

        // WM_NORMAL_HINTS: USPosition|USSize|PPosition|PSize, so the WM keeps our placement instead of
        // running its own smart placement.
        var normal = x11.Atom("WM_NORMAL_HINTS");
        var sizeHints = new IntPtr[18];
        sizeHints[0] = 1 | 2 | 4 | 8;
        sizeHints[1] = r.X; sizeHints[2] = r.Y; sizeHints[3] = r.Width; sizeHints[4] = r.Height;
        Xlib.XChangeProperty(d, w, normal, x11.Atom("WM_SIZE_HINTS"), 32, Xlib.PropModeReplace, sizeHints, sizeHints.Length);
    }

    private void SetAtoms(IntPtr window, string property, string type, params string[] values)
    {
        var x11 = _x11!;
        var data = values.Select(x11.Atom).ToArray();
        Xlib.XChangeProperty(x11.Display, window, x11.Atom(property), x11.Atom(type), 32, Xlib.PropModeReplace, data, data.Length);
    }

    // (Re)acquire the client's backing pixmap. It changes whenever the client is resized or re-mapped,
    // so this runs again on those events. An unmapped client (minimised) has no pixmap: the tile keeps
    // its last frame until the client comes back.
    private void BindSource(Tile tile)
    {
        var d = _x11!.Display;
        ReleaseSource(tile);

        if (Xlib.XGetWindowAttributes(d, tile.Source, out var wa) == 0 || wa.MapState != Xlib.IsViewable) return;
        tile.SourceWidth = wa.Width;
        tile.SourceHeight = wa.Height;
        tile.Pixmap = Xlib.XCompositeNameWindowPixmap(d, tile.Source);
        var format = Xlib.XRenderFindVisualFormat(d, wa.Visual);
        if (tile.Pixmap == IntPtr.Zero || format == IntPtr.Zero) return;
        tile.SourcePicture = Xlib.XRenderCreatePicture(d, tile.Pixmap, format, 0, IntPtr.Zero);
        Xlib.XRenderSetPictureFilter(d, tile.SourcePicture, "bilinear", IntPtr.Zero, 0);
        UpdateTransform(tile);
    }

    private void UpdateTransform(Tile tile)
    {
        if (tile.SourcePicture == IntPtr.Zero || tile.Rect.Width <= 0 || tile.Rect.Height <= 0) return;
        var t = Xlib.XTransform.Scale((double)tile.SourceWidth / tile.Rect.Width, (double)tile.SourceHeight / tile.Rect.Height);
        Xlib.XRenderSetPictureTransform(_x11!.Display, tile.SourcePicture, ref t);
    }

    private void ReleaseSource(Tile tile)
    {
        var d = _x11!.Display;
        if (tile.SourcePicture != IntPtr.Zero) Xlib.XRenderFreePicture(d, tile.SourcePicture);
        if (tile.Pixmap != IntPtr.Zero) Xlib.XFreePixmap(d, tile.Pixmap);
        tile.SourcePicture = IntPtr.Zero;
        tile.Pixmap = IntPtr.Zero;
    }

    private void Draw(Tile tile, DateTime now)
    {
        tile.Dirty = false;
        tile.LastDraw = now;
        if (tile.SourcePicture == IntPtr.Zero) return;
        Xlib.XRenderComposite(_x11!.Display, Xlib.PictOpSrc, tile.SourcePicture, IntPtr.Zero, tile.DestPicture,
            0, 0, 0, 0, 0, 0, (uint)tile.Rect.Width, (uint)tile.Rect.Height);
    }

    private void DestroyTile(Tile tile)
    {
        var d = _x11!.Display;
        ReleaseSource(tile);
        if (tile.DestPicture != IntPtr.Zero) Xlib.XRenderFreePicture(d, tile.DestPicture);
        if (tile.Damage != IntPtr.Zero) Xlib.XDamageDestroy(d, tile.Damage);
        if (tile.Window != IntPtr.Zero) Xlib.XDestroyWindow(d, tile.Window);
        Xlib.XCompositeUnredirectWindow(d, tile.Source, Xlib.CompositeRedirectAutomatic);
        _tilesBySource.Remove(tile.Source);
        _tilesByWindow.Remove(tile.Window);
        _tilesByDamage.Remove(tile.Damage);
    }

    // XEvent field offsets on LP64 (type int, serial, send_event, display, then per-event fields).
    private void HandleEvent(IntPtr ev)
    {
        var type = Marshal.ReadInt32(ev);

        if (type == _damageEventBase + Xlib.XDamageNotify)
        {
            var damage = Marshal.ReadIntPtr(ev, 40);
            if (_tilesByDamage.TryGetValue(damage, out var tile))
            {
                Xlib.XDamageSubtract(_x11!.Display, damage, IntPtr.Zero, IntPtr.Zero);
                tile.Dirty = true;
            }
            return;
        }

        switch (type)
        {
            case Xlib.ButtonPress:
            {
                var window = Marshal.ReadIntPtr(ev, 32);
                var button = Marshal.ReadInt32(ev, 84);
                if (button == 1 && _tilesByWindow.TryGetValue(window, out var tile))
                {
                    _x11!.Activate(tile.Source);
                    TileClicked?.Invoke(tile.Source);
                }
                break;
            }
            case Xlib.ConfigureNotify:
            {
                var window = Marshal.ReadIntPtr(ev, 40);
                var width = Marshal.ReadInt32(ev, 56);
                var height = Marshal.ReadInt32(ev, 60);
                if (_tilesBySource.TryGetValue(window, out var tile) && (width != tile.SourceWidth || height != tile.SourceHeight))
                {
                    BindSource(tile);
                    tile.Dirty = true;
                }
                break;
            }
            case MapNotify:
            {
                var window = Marshal.ReadIntPtr(ev, 40);
                if (_tilesBySource.TryGetValue(window, out var tile))
                {
                    BindSource(tile);
                    tile.Dirty = true;
                }
                break;
            }
            case Xlib.DestroyNotify:
            {
                var window = Marshal.ReadIntPtr(ev, 40);
                if (_tilesBySource.TryGetValue(window, out var tile)) DestroyTile(tile);
                break;
            }
            case Expose:
            {
                var window = Marshal.ReadIntPtr(ev, 32);
                if (_tilesByWindow.TryGetValue(window, out var tile)) tile.Dirty = true;
                break;
            }
        }
    }

    private const int Expose = 12;
    private const int MapNotify = 19;

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class Tile
    {
        public IntPtr Source;
        public WindowRect Rect = new();
        public IntPtr Window;
        public IntPtr DestPicture;
        public IntPtr Damage;
        public IntPtr Pixmap;
        public IntPtr SourcePicture;
        public int SourceWidth;
        public int SourceHeight;
        public bool Dirty;
        public DateTime LastDraw;
    }
}

internal static class Libc
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollFd fds, ulong nfds, int timeout);

    public static void WaitReadable(int fd, int timeoutMs)
    {
        var p = new PollFd { Fd = fd, Events = 1 /* POLLIN */ };
        poll(ref p, 1, timeoutMs);
    }
}

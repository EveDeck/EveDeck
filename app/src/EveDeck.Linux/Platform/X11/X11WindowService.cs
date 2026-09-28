using System.Runtime.InteropServices;
using System.Text;
using EveDeck.Models;

namespace EveDeck.Linux.Platform.X11;

// One EVE client as X11 sees it. Handle is the WM-managed client window from _NET_CLIENT_LIST.
public sealed record X11Client(IntPtr Handle, string Title, string WmClass, WindowRect Bounds);

// Linux counterpart of the Windows app's Win32WindowService: find EVE clients, read monitors, place
// and focus windows. Works on a plain X11 session and on a Wayland session through XWayland, which is
// where EVE under Proton lives either way.
//
// Only EWMH/ICCCM requests go to the window manager (move/resize requests, _NET_ACTIVE_WINDOW as a
// pager, Motif decoration hints). Nothing here touches another process's input or memory.
public sealed class X11WindowService : IDisposable
{
    // EVE under Proton/Wine: WM_CLASS res_name is the Windows image name.
    private const string EveWmClass = "exefile.exe";
    private const string EveTitlePrefix = "EVE - ";

    // Dev/test only: also accept any window titled "EVE - ..." so plain X11 stand-ins (xterm -T "EVE -
    // Test1") can play clients on a machine with no EVE install. Off by default, because a browser tab
    // or chat window can carry the same title.
    public static bool MatchTitleOnly { get; set; } =
        Environment.GetEnvironmentVariable("EVEDECK_MATCH_TITLE") == "1";

    private readonly IntPtr _display;
    private readonly IntPtr _root;
    private readonly IntPtr _netClientList, _netWmName, _utf8String, _netActiveWindow, _motifWmHints;

    // Xlib's default error handler calls exit(). A client closing between our enumeration and a
    // property read raises BadWindow, so without this the whole app dies whenever an EVE client quits.
    // The handler is process-global and Avalonia installs its own, so ours swallows errors on our own
    // connection only and forwards everything else to whatever was installed before.
    private static readonly Xlib.XErrorHandler s_errorHandler = OnXError;
    private static IntPtr s_previousHandler;
    private static IntPtr s_ownDisplay;
    private static int s_errorCount;

    public X11WindowService(string? displayName = null)
    {
        _display = Xlib.XOpenDisplay(displayName);
        if (_display == IntPtr.Zero)
            throw new InvalidOperationException($"Cannot open X display '{displayName ?? Environment.GetEnvironmentVariable("DISPLAY")}'. EveDeck needs X11 or XWayland.");
        _root = Xlib.XDefaultRootWindow(_display);

        s_ownDisplay = _display;
        s_previousHandler = Xlib.XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(s_errorHandler));

        _netClientList = Atom("_NET_CLIENT_LIST");
        _netWmName = Atom("_NET_WM_NAME");
        _utf8String = Atom("UTF8_STRING");
        _netActiveWindow = Atom("_NET_ACTIVE_WINDOW");
        _motifWmHints = Atom("_MOTIF_WM_HINTS");
    }

    private static int OnXError(IntPtr display, IntPtr errorEvent)
    {
        if (display == s_ownDisplay)
        {
            Interlocked.Increment(ref s_errorCount);
            return 0;
        }
        if (s_previousHandler == IntPtr.Zero) return 0;
        var previous = Marshal.GetDelegateForFunctionPointer<Xlib.XErrorHandler>(s_previousHandler);
        return previous(display, errorEvent);
    }

    private IntPtr Atom(string name) => Xlib.XInternAtom(_display, name, false);

    public IReadOnlyList<X11Client> GetEveClients()
    {
        var result = new List<X11Client>();
        foreach (var window in ReadWindowList(_root, _netClientList))
        {
            var title = GetTitle(window);
            var wmClass = GetWmClass(window);
            if (!IsEve(title, wmClass)) continue;
            if (!TryGetBounds(window, out var bounds)) continue; // closed mid-scan
            result.Add(new X11Client(window, title, wmClass, bounds));
        }
        return result;
    }

    private static bool IsEve(string title, string wmClass)
    {
        if (wmClass.Equals(EveWmClass, StringComparison.OrdinalIgnoreCase)) return true;
        return MatchTitleOnly && title.StartsWith(EveTitlePrefix, StringComparison.Ordinal);
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        var ptr = Xlib.XRRGetMonitors(_display, _root, true, out var count);
        if (ptr == IntPtr.Zero) return list;
        try
        {
            var size = Marshal.SizeOf<Xlib.XRRMonitorInfo>();
            for (var i = 0; i < count; i++)
            {
                var m = Marshal.PtrToStructure<Xlib.XRRMonitorInfo>(ptr + i * size);
                var name = AtomName(m.Name) ?? $"monitor-{i}";
                var bounds = new WindowRect { X = m.X, Y = m.Y, Width = m.Width, Height = m.Height };
                list.Add(new MonitorInfo
                {
                    Id = name,
                    DeviceName = name,
                    Bounds = bounds,
                    // Work area (panels excluded) comes from _NET_WORKAREA per desktop, not per monitor;
                    // full bounds is what EVE layouts target anyway, same as the Windows presets.
                    WorkArea = bounds,
                    IsPrimary = m.Primary != 0,
                });
            }
        }
        finally
        {
            Xlib.XRRFreeMonitors(ptr);
        }
        return list;
    }

    // Absolute position of the client area (not the WM frame), which is what a layout slot describes.
    public bool TryGetBounds(IntPtr window, out WindowRect bounds)
    {
        bounds = new WindowRect();
        if (Xlib.XGetGeometry(_display, window, out _, out _, out _, out var w, out var h, out _, out _) == 0)
            return false;
        if (!Xlib.XTranslateCoordinates(_display, window, _root, 0, 0, out var x, out var y, out _))
            return false;
        bounds = new WindowRect { X = x, Y = y, Width = (int)w, Height = (int)h };
        return true;
    }

    // Motif hints are the de-facto "no decorations" request every X11 WM and KWin/Mutter under XWayland
    // honour; the X11 equivalent of stripping WS_CAPTION/WS_THICKFRAME on Windows.
    public void SetBorderless(IntPtr window, bool borderless)
    {
        const long MwmHintsDecorations = 1L << 1;
        var hints = new IntPtr[] { (IntPtr)MwmHintsDecorations, IntPtr.Zero, (IntPtr)(borderless ? 0 : 1), IntPtr.Zero, IntPtr.Zero };
        Xlib.XChangeProperty(_display, window, _motifWmHints, _motifWmHints, 32, Xlib.PropModeReplace, hints, hints.Length);
        Xlib.XFlush(_display);
    }

    // Move only. EVE clients are never resized in preview mode: resizing makes EVE re-flow its UI.
    public void Move(IntPtr window, int x, int y)
    {
        Xlib.XMoveWindow(_display, window, x, y);
        Xlib.XFlush(_display);
    }

    public void MoveResize(IntPtr window, WindowRect rect)
    {
        Xlib.XMoveResizeWindow(_display, window, rect.X, rect.Y, (uint)Math.Max(1, rect.Width), (uint)Math.Max(1, rect.Height));
        Xlib.XFlush(_display);
    }

    // Ask the WM to activate a window, identifying ourselves as a pager (source indication 2): WMs apply
    // focus-stealing prevention to applications (1) but trust pagers and taskbars, which is what a
    // client switcher is.
    public void Activate(IntPtr window)
    {
        var ev = new Xlib.XClientMessageEvent
        {
            Type = Xlib.ClientMessage,
            SendEvent = 1,
            Display = _display,
            Window = window,
            MessageType = _netActiveWindow,
            Format = 32,
            Data0 = 2,
            Data1 = IntPtr.Zero, // CurrentTime
            Data2 = IntPtr.Zero,
        };
        Xlib.XSendEvent(_display, _root, false, (IntPtr)(Xlib.SubstructureRedirectMask | Xlib.SubstructureNotifyMask), ref ev);
        Xlib.XFlush(_display);
    }

    public IntPtr GetActiveWindow() => ReadWindowList(_root, _netActiveWindow).FirstOrDefault();

    // Wait for the server (and, through it, the WM) to catch up, so a read right after a move sees it.
    public void Sync() => Xlib.XSync(_display, false);

    private List<IntPtr> ReadWindowList(IntPtr window, IntPtr property)
    {
        var list = new List<IntPtr>();
        if (Xlib.XGetWindowProperty(_display, window, property, IntPtr.Zero, (IntPtr)4096, false, Xlib.AnyPropertyType,
                out _, out var format, out var nItems, out _, out var data) != Xlib.Success || data == IntPtr.Zero)
            return list;
        try
        {
            if (format == 32)
                for (var i = 0; i < (int)nItems; i++)
                    list.Add(Marshal.ReadIntPtr(data, i * IntPtr.Size)); // LP64: C long, see Xlib.cs
        }
        finally
        {
            Xlib.XFree(data);
        }
        return list;
    }

    private string GetTitle(IntPtr window) =>
        ReadString(window, _netWmName, _utf8String) ?? ReadString(window, Atom("WM_NAME"), Xlib.AnyPropertyType) ?? "";

    private string? ReadString(IntPtr window, IntPtr property, IntPtr type)
    {
        if (Xlib.XGetWindowProperty(_display, window, property, IntPtr.Zero, (IntPtr)1024, false, type,
                out _, out var format, out var nItems, out _, out var data) != Xlib.Success || data == IntPtr.Zero)
            return null;
        try
        {
            if (format != 8 || (int)nItems == 0) return null;
            var bytes = new byte[(int)nItems];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            Xlib.XFree(data);
        }
    }

    private string GetWmClass(IntPtr window)
    {
        if (Xlib.XGetClassHint(_display, window, out var hint) == 0) return "";
        try
        {
            return Marshal.PtrToStringAnsi(hint.ResName) ?? "";
        }
        finally
        {
            if (hint.ResName != IntPtr.Zero) Xlib.XFree(hint.ResName);
            if (hint.ResClass != IntPtr.Zero) Xlib.XFree(hint.ResClass);
        }
    }

    private string? AtomName(IntPtr atom)
    {
        if (atom == IntPtr.Zero) return null;
        var ptr = Xlib.XGetAtomName(_display, atom);
        if (ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringAnsi(ptr); }
        finally { Xlib.XFree(ptr); }
    }

    public void Dispose()
    {
        if (s_ownDisplay == _display) s_ownDisplay = IntPtr.Zero;
        Xlib.XCloseDisplay(_display);
    }
}

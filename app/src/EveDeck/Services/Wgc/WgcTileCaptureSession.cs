using System.Drawing;
using System.Drawing.Imaging;

namespace EveDeck.Services.Wgc;

/// <see cref="ITileCaptureSession"/> backed by Windows.Graphics.Capture for a LOCAL window --
/// an opt-in alternative to the DWM thumbnail path, crisper at small tile sizes.
///
/// Safeguards (see <see cref="WgcCaptureDevice"/> for why): one shared D3D11 device, reused
/// staging texture and output bitmap, a frame-rate cap, and -- critically -- on ANY failure it
/// sets <see cref="Faulted"/> so TileSurfaceWindow tears it down and registers a DWM thumbnail
/// for that tile instead. It never throws out of the interface methods.
internal sealed class WgcTileCaptureSession : ITileCaptureSession
{
    // TWO locks, deliberately. _captureGate covers the expensive half -- the GPU readback and the
    // rescale -- and is taken ONLY by the capture thread and Dispose. _publishGate covers a pointer
    // swap and nothing else, and is the only lock the UI thread ever touches.
    //
    // They used to be one lock, and that was the bug: TextureReadback.CopyToBgra blocks on
    // ID3D11DeviceContext.Map against a device context SHARED by every session, so with five seats
    // the capture threads serialised on the GPU *while holding the lock the UI thread needed to
    // draw*. The window stopped responding, and it got worse with each account added. Nothing on the
    // draw path may take _captureGate.
    private readonly object _captureGate = new();
    private readonly object _publishGate = new();
    private readonly Action<string>? _log;
    private readonly int _minFrameIntervalMs;

    private WindowCaptureSource? _source;
    private TextureReadback? _readback;
    private byte[] _bgra = [];
    private int _w;
    private int _h;
    private int _stride;
    private long _lastFrameTick;
    private Bitmap? _full;
    private bool _dirty;
    private volatile bool _disposed;

    // Last size the surface asked to draw at. Frames arrive on the capture thread, before any draw
    // call, so the readback uses the previous request as its hint -- tile sizes change rarely, and a
    // stale hint only costs one slightly-larger readback.
    // Seeded rather than left at 0: a 0 target means "full resolution", and the frames that arrive
    // before the first draw would each cost a full-size readback for no benefit.
    private volatile int _targetWidth = 640;
    private volatile int _targetHeight = 360;

    // Tile-sized frame, produced on the CAPTURE thread rather than at draw time.
    //
    // The rescale is the expensive half of this path, and doing it inside TryGetResizedFrame put it
    // on the single UI/pump thread for every tile in turn -- so the whole preview pipeline was
    // serialised onto one core no matter how many tiles were live. Each capture session already has
    // its own free-threaded callback, so doing the work there spreads it across cores and leaves the
    // draw call a small copy.
    // Double-buffered tile-sized output. The capture thread draws into _spare, then swaps it with
    // _published under _publishGate -- so handing a finished frame to the UI costs one reference
    // swap, and neither side ever allocates per frame.
    private Bitmap? _published;
    private Bitmap? _spare;

    public bool Faulted { get; private set; }
    public string? FaultReason { get; private set; }

    public WgcTileCaptureSession(nint hwnd, int maxFps, Action<string>? log)
    {
        _log = log;
        _minFrameIntervalMs = maxFps > 0 ? Math.Max(1, 1000 / maxFps) : 0;

        try
        {
            var device = WgcCaptureDevice.Shared; // throws => Faulted below => caller uses DWM
            _readback = new TextureReadback(device.D3DDevice, device.ContextLock, log);
            _source = new WindowCaptureSource(hwnd, device.D3DDevice, device.WinRtDevice, log);
            _source.FrameArrived += OnFrame;
            _source.Closed += () => Fault("capture item closed");
            _source.Start();
        }
        catch (Exception ex)
        {
            Fault($"init: {ex.Message}");
        }
    }

    private void OnFrame(CapturedFrame frame)
    {
        if (_disposed || Faulted) return;
        try
        {
            if (_minFrameIntervalMs > 0)
            {
                var now = Environment.TickCount64;
                if (now - _lastFrameTick < _minFrameIntervalMs) return; // fps cap: drop
                _lastFrameTick = now;
            }

            // The GPU readback and the rescale happen under _captureGate only. The UI thread is not
            // blocked for any of it.
            lock (_captureGate)
            {
                if (_disposed || _readback is null) return;
                _readback.CopyToBgra(frame.Texture, ref _bgra, out _w, out _h, out _stride, _targetWidth);
                if (!BuildScaled()) return;
            }

            // Publish: a swap, nothing more.
            lock (_publishGate)
            {
                if (_disposed) return;
                (_published, _spare) = (_spare, _published);
                _dirty = true;
            }
        }
        catch (Exception ex)
        {
            Fault($"frame: {ex.Message}");
        }
    }

    public bool ConsumeFrameDirty()
    {
        lock (_publishGate)
        {
            if (!_dirty) return false;
            _dirty = false;
            return true;
        }
    }

    // Runs on the capture thread, inside _captureGate. Draws into _spare (capture-thread-owned)
    // and returns whether _spare now holds a frame worth publishing.
    private bool BuildScaled()
    {
        var destWidth = _targetWidth;
        var destHeight = _targetHeight;
        if (destWidth < 1 || destHeight < 1 || _w < 2 || _h < 2) return false;

        if (_full is null || _full.Width != _w || _full.Height != _h)
        {
            _full?.Dispose();
            _full = new Bitmap(_w, _h, PixelFormat.Format32bppArgb);
        }

        var bits = _full.LockBits(new Rectangle(0, 0, _w, _h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var d = (byte*)bits.Scan0;
                fixed (byte* src = _bgra)
                {
                    for (var y = 0; y < _h; y++)
                        Buffer.MemoryCopy(src + (long)y * _stride, d + (long)y * bits.Stride, bits.Stride, _stride);
                }
            }
        }
        finally { _full.UnlockBits(bits); }

        if (_spare is null || _spare.Width != destWidth || _spare.Height != destHeight)
        {
            _spare?.Dispose();
            _spare = new Bitmap(destWidth, destHeight, PixelFormat.Format32bppArgb);
        }

        using var g = Graphics.FromImage(_spare);
        g.InterpolationMode = _w > destWidth * 2
            ? System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear
            : System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        g.DrawImage(_full, new Rectangle(0, 0, destWidth, destHeight));
        return true;
    }

    public Bitmap? TryGetResizedFrame(int destWidth, int destHeight)
    {
        if (destWidth < 1 || destHeight < 1 || Faulted) return null;
        _targetWidth = destWidth;
        _targetHeight = destHeight;

        try
        {
            // Take a tile-sized copy of the published frame under a lock that is never held across
            // anything slow, then release it before doing any drawing. The old slow path rescaled
            // from the full-resolution _bgra buffer here, which meant the draw thread contended with
            // the capture thread for the megapixel buffers; the capture thread produces a
            // tile-sized frame on its own now, so that path is gone.
            Bitmap snapshot;
            lock (_publishGate)
            {
                if (_disposed || _published is null) return null;
                snapshot = new Bitmap(_published);
            }

            // Already the requested size on the overwhelming majority of frames: _targetWidth was
            // set from the last draw, so the capture thread is producing exactly this.
            if (snapshot.Width == destWidth && snapshot.Height == destHeight) return snapshot;

            // Size just changed. Bridge this one frame from the small published bitmap rather than
            // from the full-resolution capture -- the source is already near the tile size, so this
            // is cheap, and the next captured frame will land at the new size.
            using (snapshot)
            {
                var dst = new Bitmap(destWidth, destHeight, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(dst);
                g.InterpolationMode = snapshot.Width > destWidth * 2
                    ? System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear
                    : System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(snapshot, new Rectangle(0, 0, destWidth, destHeight));
                return dst;
            }
        }
        catch (Exception ex)
        {
            Fault($"render: {ex.Message}");
            return null;
        }
    }

    private void Fault(string reason)
    {
        if (Faulted) return;
        Faulted = true;
        FaultReason = reason;
        _log?.Invoke($"wgc tile: {reason} -- falling back to DWM for this tile");
    }

    public void Dispose()
    {
        // Flag first, under the publish lock, so a draw already in flight bails out instead of
        // cloning a bitmap the capture side is about to free. Then take _captureGate, which waits
        // for any in-flight readback to finish -- that wait is on the disposing thread, never on
        // the UI thread's draw path.
        lock (_publishGate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        lock (_captureGate)
        {
            try { if (_source is not null) _source.FrameArrived -= OnFrame; } catch { /* ignore */ }
            try { _source?.Dispose(); } catch { /* ignore */ }
            try { _readback?.Dispose(); } catch { /* ignore */ }
            _source = null;
            _readback = null;
            _full?.Dispose();
            _full = null;
            _spare?.Dispose();
            _spare = null;
        }

        lock (_publishGate)
        {
            _published?.Dispose();
            _published = null;
        }
    }
}

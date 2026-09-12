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
    private readonly object _gate = new();
    private readonly Action<string>? _log;
    private readonly int _minFrameIntervalMs;

    private WindowCaptureSource? _source;
    private TextureReadback? _readback;
    private byte[] _bgra = [];
    private int _w;
    private int _h;
    private int _stride;
    private long _seq;
    private long _renderedSeq = -1;
    private long _lastFrameTick;
    private Bitmap? _full;
    private bool _dirty;
    private bool _disposed;

    // Last size the surface asked to draw at. Frames arrive on the capture thread, before any draw
    // call, so the readback uses the previous request as its hint -- tile sizes change rarely, and a
    // stale hint only costs one slightly-larger readback.
    // Seeded rather than left at 0: a 0 target means "full resolution", and the frames that arrive
    // before the first draw would each cost a full-size readback for no benefit.
    private volatile int _targetWidth = 640;

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

            lock (_gate)
            {
                if (_disposed || _readback is null) return;
                _readback.CopyToBgra(frame.Texture, ref _bgra, out _w, out _h, out _stride, _targetWidth);
                _seq++;
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
        lock (_gate)
        {
            if (!_dirty) return false;
            _dirty = false;
            return true;
        }
    }

    public Bitmap? TryGetResizedFrame(int destWidth, int destHeight)
    {
        if (destWidth < 1 || destHeight < 1 || Faulted) return null;
        _targetWidth = destWidth;

        try
        {
            lock (_gate)
            {
                if (_w < 2 || _h < 2 || _bgra.Length < _stride * _h) return null;

                if (_renderedSeq != _seq)
                {
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
                            fixed (byte* s = _bgra)
                            {
                                for (var y = 0; y < _h; y++)
                                    Buffer.MemoryCopy(s + (long)y * _stride, d + (long)y * bits.Stride, bits.Stride, _stride);
                            }
                        }
                    }
                    finally { _full.UnlockBits(bits); }
                    _renderedSeq = _seq;
                }

                if (_full is null) return null;

                // The GPU has already filtered this down to near the tile size, so the remaining
                // step is short and HighQualityBilinear stays affordable.
                var dst = new Bitmap(destWidth, destHeight, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(dst);
                g.InterpolationMode = _w > destWidth * 2
                    ? System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear
                    : System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(_full, new Rectangle(0, 0, destWidth, destHeight));
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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_source is not null) _source.FrameArrived -= OnFrame; } catch { /* ignore */ }
            try { _source?.Dispose(); } catch { /* ignore */ }
            try { _readback?.Dispose(); } catch { /* ignore */ }
            _source = null;
            _readback = null;
            _full?.Dispose();
            _full = null;
        }
    }
}

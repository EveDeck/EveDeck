using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace EveDeck.Services.Wgc;

/// One captured frame. <see cref="Texture"/> is only valid for the duration of the
/// <see cref="WindowCaptureSource.FrameArrived"/> callback -- copy out of it before returning.
internal readonly struct CapturedFrame(ID3D11Texture2D texture, int width, int height, TimeSpan systemRelativeTime)
{
    public ID3D11Texture2D Texture { get; } = texture;
    public int Width { get; } = width;
    public int Height { get; } = height;
    /// QPC-based capture timestamp (Windows.Graphics.Capture's SystemRelativeTime).
    public TimeSpan SystemRelativeTime { get; } = systemRelativeTime;
}

// Windows.Graphics.Capture session for a single window. Frames arrive on a pool thread as BGRA
// D3D11 textures. The agent owns the D3D11 device; capture is confined to this (secondary) box.
internal sealed class WindowCaptureSource : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private bool _disposed;

    public event Action<CapturedFrame>? FrameArrived;

    /// Raised (once) if the OS closes the capture item, e.g. the target window was closed.
    public event Action? Closed;

    private readonly Windows.Foundation.TypedEventHandler<GraphicsCaptureItem, object> _itemClosed;

    public int Width => _poolSize.Width;
    public int Height => _poolSize.Height;

    public WindowCaptureSource(nint hwnd, ID3D11Device device, IDirect3DDevice winrtDevice, Action<string>? log = null)
    {
        if (!GraphicsCaptureItemInterop.IsSupported)
            throw new NotSupportedException("Windows.Graphics.Capture is not available on this OS build");

        _device = device;
        _winrtDevice = winrtDevice;
        _log = log;
        _item = GraphicsCaptureItemInterop.CreateForWindow(hwnd);
        _poolSize = _item.Size;
        if (_poolSize.Width <= 0 || _poolSize.Height <= 0)
            throw new InvalidOperationException($"capture item has zero size ({_poolSize.Width}x{_poolSize.Height}); is the window minimized?");

        // Keep the handler in a field so Dispose can detach it. An anonymous lambda could never be
        // unsubscribed, so a torn-down session kept reporting "capture item closed" -- which the
        // tile surface read as a genuine fault and used to demote that tile to DWM permanently.
        // The _disposed guard covers the close that disposal itself triggers.
        _itemClosed = (_, _) =>
        {
            if (_disposed) return;
            try { Closed?.Invoke(); } catch { /* ignore */ }
        };
        _item.Closed += _itemClosed;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null) return;

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            _pool.FrameArrived += OnFrameArrived;

            _session = _pool.CreateCaptureSession(_item);
            TrySet(() => _session.IsCursorCaptureEnabled = false, "IsCursorCaptureEnabled");
            // IsBorderRequired needs Windows SDK 10.0.20348; not in this TFM's projection. The
            // capture border only shows on the source window on the secondary box, so leave it.
            _session.StartCapture();
            // OPSEC: _item.DisplayName is the raw window title, which for a logged-in EVE client is
            // "EVE - <character name>". This log goes to EveDeck's own log file, so never emit it.
            _log?.Invoke($"capture: started {_poolSize.Width}x{_poolSize.Height}");
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame;
        try
        {
            frame = sender.TryGetNextFrame();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"capture: TryGetNextFrame failed: {ex.Message}");
            return;
        }
        if (frame is null) return;

        using (frame)
        {
            var content = frame.ContentSize;
            if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
            {
                // Window resized -- resize the pool and skip this frame.
                if (content.Width > 0 && content.Height > 0)
                {
                    _poolSize = content;
                    try
                    {
                        sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
                        _log?.Invoke($"capture: resized to {content.Width}x{content.Height}");
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"capture: pool recreate failed: {ex.Message}");
                    }
                }
                return;
            }

            ID3D11Texture2D? tex = null;
            try
            {
                tex = Direct3DInterop.GetTexture(frame.Surface);
                FrameArrived?.Invoke(new CapturedFrame(tex, content.Width, content.Height, frame.SystemRelativeTime));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"capture: frame handler threw: {ex}");
            }
            finally
            {
                tex?.Dispose();
            }
        }
    }

    private void TrySet(Action set, string name)
    {
        try { set(); }
        catch (Exception ex) { _log?.Invoke($"capture: {name} not settable on this build ({ex.GetType().Name})"); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_pool is not null) _pool.FrameArrived -= OnFrameArrived;
            try { _item.Closed -= _itemClosed; } catch { /* item may already be gone */ }
            try { _session?.Dispose(); } catch { /* ignore */ }
            try { _pool?.Dispose(); } catch { /* ignore */ }
            _session = null;
            _pool = null;
        }
    }
}

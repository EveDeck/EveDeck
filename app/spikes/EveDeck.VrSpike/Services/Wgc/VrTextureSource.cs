using Vortice.Direct3D11;
using Vortice.DXGI;

namespace EveDeck.Services.Wgc;

// Keeps the newest WGC frame in a persistent, shareable D3D11 texture that can be handed straight
// to IVROverlay.SetOverlayTexture.
//
// Why this exists: SetOverlayRaw leaks a resource per call. Measured against SteamVR, every panel
// accepted a fixed number of raw uploads (~50, rising to 112 once event queues were drained) and
// then returned RequestFailed forever. Raw uploads are for static images; dynamic content has to
// go through a texture. Handing over the GPU texture also skips the CPU readback and the BGRA->RGBA
// swizzle the raw path needed, so this is the faster route as well as the working one.
//
// The captured frame's texture is disposed the moment WindowCaptureSource's FrameArrived handler
// returns, so the copy has to happen inside that callback.
internal sealed class VrTextureSource : IDisposable
{
    private readonly WgcCaptureDevice _device;
    private readonly WindowCaptureSource _source;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    private ID3D11Texture2D? _texture;
    private int _width;
    private int _height;
    private bool _hasFrame;
    private bool _disposed;

    public VrTextureSource(nint hwnd, Action<string>? log = null)
    {
        _log = log;
        _device = WgcCaptureDevice.Shared;
        _source = new WindowCaptureSource(hwnd, _device.D3DDevice, _device.WinRtDevice, log);
        _source.FrameArrived += OnFrameArrived;
        _source.Start();
    }

    public bool Faulted { get; private set; }

    private void OnFrameArrived(CapturedFrame frame)
    {
        if (_disposed) return;

        try
        {
            lock (_gate)
            {
                EnsureTexture(frame);
                lock (_device.ContextLock)
                {
                    var context = _device.D3DDevice.ImmediateContext;
                    context.CopyResource(_texture!, frame.Texture);
                    // The compositor reads this from its own device, so the copy has to have
                    // actually landed before the handle is published.
                    context.Flush();
                }
                _hasFrame = true;
            }
        }
        catch (Exception ex)
        {
            Faulted = true;
            _log?.Invoke($"vr texture: copy failed, faulting source: {ex}");
        }
    }

    private void EnsureTexture(CapturedFrame frame)
    {
        if (_texture is not null && _width == frame.Width && _height == frame.Height) return;

        _texture?.Dispose();
        _width = frame.Width;
        _height = frame.Height;

        var srcFormat = frame.Texture.Description.Format;
        _texture = _device.D3DDevice.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = srcFormat == Format.Unknown ? Format.B8G8R8A8_UNorm : srcFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            // vrserver is a separate process, so the surface must be shareable.
            MiscFlags = ResourceOptionFlags.Shared,
        });
        _log?.Invoke($"vr texture: allocated {_width}x{_height} {_texture.Description.Format}");
    }

    // Native ID3D11Texture2D pointer for Texture_t.handle. Null until the first frame lands.
    public bool TryGetTexture(out nint handle, out int width, out int height)
    {
        lock (_gate)
        {
            handle = nint.Zero;
            width = _width;
            height = _height;
            if (_disposed || !_hasFrame || _texture is null) return false;
            handle = _texture.NativePointer;
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _source.FrameArrived -= OnFrameArrived;
        _source.Dispose();

        lock (_gate)
        {
            _texture?.Dispose();
            _texture = null;
        }
    }
}

using Vortice.Direct3D11;
using Windows.Graphics.DirectX.Direct3D11;

namespace EveDeck.Services.Wgc;

/// The ONE shared D3D11 device for all WGC tile capture in this process. Created lazily on first
/// use, never per-tile.
///
/// History: GPU preview capture was removed from this app in v1.22.0 after three escalating
/// failures ended in a full machine hard-lock -- the root cause was a second D3D11 device plus
/// per-frame GPU textures on a machine already at the VRAM ceiling (~5 EVE clients in DX12 with
/// upscaling + frame generation). See the project-wgc-removed-dwm-only memory. This reintroduction
/// is opt-in (a setting), shares one device, reuses staging textures, caps frame rate, and refuses
/// to start when free local VRAM is below <see cref="MinFreeVramBytes"/>. Any failure downstream
/// falls the affected tile back to a DWM thumbnail.
internal sealed class WgcCaptureDevice : IDisposable
{
    /// Don't start GPU capture if estimated free local VRAM is under this.
    public const long MinFreeVramBytes = 512L * 1024 * 1024;

    private static readonly object Gate = new();
    private static WgcCaptureDevice? _shared;

    public ID3D11Device D3DDevice { get; }
    public IDirect3DDevice WinRtDevice { get; }

    /// The D3D11 immediate context is NOT thread-safe and every WgcTileCaptureSession's readback
    /// runs on its own WGC pool thread against this one shared device. Serialise CopyResource/Map
    /// through this -- readback is a few ms and previews are low-fps, so the contention is nil.
    public object ContextLock { get; } = new();

    private WgcCaptureDevice(ID3D11Device d3d, IDirect3DDevice winrt)
    {
        D3DDevice = d3d;
        WinRtDevice = winrt;
    }

    /// Throws NotSupportedException if WGC is unavailable, or InvalidOperationException if VRAM is
    /// too tight. Callers treat any throw as "stay on DWM".
    public static WgcCaptureDevice Shared
    {
        get
        {
            lock (Gate)
            {
                if (_shared is not null) return _shared;

                if (!GraphicsCaptureItemInterop.IsSupported)
                    throw new NotSupportedException("Windows.Graphics.Capture is not available on this OS build");

                var d3d = Direct3DInterop.CreateDevice();
                try
                {
                    if (Direct3DInterop.TryQueryLocalVideoMemory(d3d, out var budget, out var usage) && budget > 0)
                    {
                        var freeMiB = (budget - usage) / (1024 * 1024);
                        if (budget - usage < MinFreeVramBytes)
                            throw new InvalidOperationException(
                                $"only {freeMiB} MiB local VRAM free (need {MinFreeVramBytes / (1024 * 1024)} MiB) -- staying on DWM thumbnails");
                    }

                    var winrt = Direct3DInterop.CreateWinRtDevice(d3d);
                    _shared = new WgcCaptureDevice(d3d, winrt);
                    return _shared;
                }
                catch
                {
                    d3d.Dispose();
                    throw;
                }
            }
        }
    }

    /// Cheap probe used by the settings UI: is a shared device already live, or could one start?
    public static bool ProbeAvailable(out string detail)
    {
        try
        {
            _ = Shared;
            detail = "ready";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    public static void DisposeShared()
    {
        lock (Gate)
        {
            _shared?.Dispose();
            _shared = null;
        }
    }

    public void Dispose()
    {
        try { (WinRtDevice as IDisposable)?.Dispose(); } catch { /* ignore */ }
        try { D3DDevice.Dispose(); } catch { /* ignore */ }
    }
}

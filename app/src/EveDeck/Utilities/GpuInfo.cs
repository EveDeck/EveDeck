using System.Runtime.InteropServices;

namespace EveDeck.Utilities;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel }

// Best-effort desktop GPU identification via EnumDisplayDevices -- no WMI, no DXGI, no deps.
// Used only to tailor a help tip (Performance options), never for any behavioural decision.
public static class GpuInfo
{
    // The most "capable" adapter present: a discrete NVIDIA/AMD wins over an Intel iGPU, which
    // wins over anything unrecognised. Adapters flagged as mirroring drivers are skipped.
    public static GpuVendor DetectVendor()
    {
        var best = GpuVendor.Unknown;
        try
        {
            var dev = new Win32Native.DisplayDevice { cb = Marshal.SizeOf<Win32Native.DisplayDevice>() };
            for (uint i = 0; Win32Native.EnumDisplayDevices(null, i, ref dev, 0); i++, dev.cb = Marshal.SizeOf<Win32Native.DisplayDevice>())
            {
                if ((dev.StateFlags & Win32Native.DisplayDeviceMirroringDriver) != 0) continue;
                var v = Classify(dev.DeviceString);
                if (v == GpuVendor.Nvidia || v == GpuVendor.Amd) return v;  // discrete: done
                if (v == GpuVendor.Intel && best == GpuVendor.Unknown) best = GpuVendor.Intel;
            }
        }
        catch { /* detection is a nicety; fall through to Unknown */ }
        return best;
    }

    private static GpuVendor Classify(string? name)
    {
        var s = (name ?? string.Empty).ToLowerInvariant();
        if (s.Contains("nvidia") || s.Contains("geforce") || s.Contains("quadro") || s.Contains(" rtx") || s.Contains(" gtx")) return GpuVendor.Nvidia;
        if (s.Contains("amd") || s.Contains("radeon") || s.Contains("advanced micro devices") || s.Contains("firepro")) return GpuVendor.Amd;
        if (s.Contains("intel")) return GpuVendor.Intel;
        return GpuVendor.Unknown;
    }
}

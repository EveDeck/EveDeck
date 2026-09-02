using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace EveDeck.Services.Wgc;

// Glue between Vortice's ID3D11Device and the WinRT IDirect3DDevice that
// Direct3D11CaptureFramePool needs, plus pulling an ID3D11Texture2D back out of a captured
// IDirect3DSurface.
internal static class Direct3DInterop
{
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern IntPtr CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice);

    // Real IID of ID3D11Texture2D.
    private static readonly Guid ID3D11Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public static ID3D11Device CreateDevice()
    {
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var hr = D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, flags,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out var device);
        if (hr.Failure)
        {
            // Retry without the video-support flag on adapters that reject it.
            D3D11.D3D11CreateDevice(
                null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out device).CheckError();
        }
        return device!;
    }

    /// Best-effort local (dedicated) video-memory budget vs current usage, in bytes, for the
    /// adapter behind <paramref name="device"/>. Returns false if the runtime doesn't expose it
    /// (pre-IDXGIAdapter3). Used to refuse starting GPU capture when VRAM is already tight.
    public static bool TryQueryLocalVideoMemory(ID3D11Device device, out long budgetBytes, out long usageBytes)
    {
        budgetBytes = 0;
        usageBytes = 0;
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var adapter3 = adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
            if (adapter3 is null) return false;

            var info = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
            budgetBytes = (long)info.Budget;
            usageBytes = (long)info.CurrentUsage;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        var inspectable = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer);
        try
        {
            return MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// Wraps the D3D11 texture behind a captured frame's surface. The returned texture shares the
    /// surface's lifetime -- copy out of it before the frame is disposed.
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = ID3D11Texture2DIid;
        var texPtr = access.GetInterface(ref iid);
        // Vortice's ComObject(IntPtr) ctor takes ownership of this reference.
        return new ID3D11Texture2D(texPtr);
    }
}

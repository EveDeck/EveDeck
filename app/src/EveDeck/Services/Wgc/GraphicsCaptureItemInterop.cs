using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace EveDeck.Services.Wgc;

// Creates a GraphicsCaptureItem for a specific HWND. Windows.Graphics.Capture has no public
// managed API for this -- it only exposes the picker UI -- so we go through the
// IGraphicsCaptureItemInterop activation-factory interface.
internal static class GraphicsCaptureItemInterop
{
    // Real IID of IGraphicsCaptureItem. Do NOT use typeof(GraphicsCaptureItem).GUID: CsWinRT
    // returns a per-type pseudo-GUID there, not the ABI IID the interop call needs.
    private static readonly Guid IGraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    public static GraphicsCaptureItem CreateForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) throw new ArgumentException("null hwnd", nameof(hwnd));

        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();

        var iid = IGraphicsCaptureItemIid;
        var abi = interop.CreateForWindow(hwnd, ref iid);
        if (abi == IntPtr.Zero) throw new InvalidOperationException("CreateForWindow returned null");
        try
        {
            return GraphicsCaptureItem.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi); // FromAbi took its own reference
        }
    }

    /// True when the OS supports window capture at all (Windows 10 1903+ / build 18362).
    public static bool IsSupported => GraphicsCaptureSession.IsSupported();
}

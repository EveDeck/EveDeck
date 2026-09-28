using System.Runtime.InteropServices;

namespace EveDeck.Linux.Platform.X11;

// Raw Xlib / XRandR declarations. Every X11 P/Invoke in the Linux build lives here, the same rule the
// Windows app follows with Utilities/Win32Native.cs.
//
// LP64 trap: format-32 window properties come back as arrays of C `long`, which is 8 bytes on 64-bit
// Linux, not 4. Read them with Marshal.ReadIntPtr(data, i * IntPtr.Size); ReadInt32 silently returns
// garbage for every element after the first.
internal static class Xlib
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXrandr = "libXrandr.so.2";

    public const int Success = 0;
    public const int ClientMessage = 33;
    public const long SubstructureNotifyMask = 1L << 19;
    public const long SubstructureRedirectMask = 1L << 20;
    public static readonly IntPtr AnyPropertyType = IntPtr.Zero;
    public const int PropModeReplace = 0;

    [DllImport(LibX11)] public static extern int XInitThreads();
    [DllImport(LibX11)] public static extern IntPtr XOpenDisplay(string? name);
    [DllImport(LibX11)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] public static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] public static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(LibX11)] public static extern int XFree(IntPtr data);
    [DllImport(LibX11)] public static extern int XFlush(IntPtr display);
    [DllImport(LibX11)] public static extern int XSync(IntPtr display, bool discard);

    [DllImport(LibX11)]
    public static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr offset,
        IntPtr length, bool delete, IntPtr reqType, out IntPtr actualType, out int actualFormat,
        out IntPtr nItems, out IntPtr bytesAfter, out IntPtr prop);

    [DllImport(LibX11)]
    public static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type,
        int format, int mode, IntPtr[] data, int nElements);

    [DllImport(LibX11)] public static extern int XGetClassHint(IntPtr display, IntPtr window, out XClassHint hint);

    [DllImport(LibX11)]
    public static extern int XGetGeometry(IntPtr display, IntPtr drawable, out IntPtr root, out int x, out int y,
        out uint width, out uint height, out uint borderWidth, out uint depth);

    [DllImport(LibX11)]
    public static extern bool XTranslateCoordinates(IntPtr display, IntPtr src, IntPtr dest, int srcX, int srcY,
        out int destX, out int destY, out IntPtr child);

    [DllImport(LibX11)] public static extern int XMoveWindow(IntPtr display, IntPtr window, int x, int y);
    [DllImport(LibX11)] public static extern int XMoveResizeWindow(IntPtr display, IntPtr window, int x, int y, uint width, uint height);

    [DllImport(LibX11)]
    public static extern int XSendEvent(IntPtr display, IntPtr window, bool propagate, IntPtr eventMask, ref XClientMessageEvent ev);

    public delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);
    [DllImport(LibX11)] public static extern IntPtr XSetErrorHandler(IntPtr handler);

    [DllImport(LibXrandr)] public static extern IntPtr XRRGetMonitors(IntPtr display, IntPtr window, bool getActive, out int count);
    [DllImport(LibXrandr)] public static extern void XRRFreeMonitors(IntPtr monitors);
    [DllImport(LibX11)] public static extern IntPtr XGetAtomName(IntPtr display, IntPtr atom);

    [StructLayout(LayoutKind.Sequential)]
    public struct XClassHint
    {
        public IntPtr ResName;
        public IntPtr ResClass;
    }

    // XClientMessageEvent padded to sizeof(XEvent) (24 longs = 192 bytes on LP64): XSendEvent copies a
    // whole XEvent, so a struct shorter than that would have Xlib read past the end of it.
    [StructLayout(LayoutKind.Sequential, Size = 192)]
    public struct XClientMessageEvent
    {
        public int Type;
        public IntPtr Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Window;
        public IntPtr MessageType;
        public int Format;
        public IntPtr Data0;
        public IntPtr Data1;
        public IntPtr Data2;
        public IntPtr Data3;
        public IntPtr Data4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XRRMonitorInfo
    {
        public IntPtr Name;
        public int Primary;
        public int Automatic;
        public int NOutput;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int MWidth;
        public int MHeight;
        public IntPtr Outputs;
    }
}

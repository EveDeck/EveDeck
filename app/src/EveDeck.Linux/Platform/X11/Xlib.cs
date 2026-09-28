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

    // ---- Windows and events (preview tiles) ----
    public const int ButtonPress = 4;
    public const int ConfigureNotify = 22;
    public const int DestroyNotify = 17;
    public const long ButtonPressMask = 1L << 2;
    public const long ExposureMask = 1L << 15;
    public const long StructureNotifyMask = 1L << 17;
    public const int InputOutput = 1;
    public const ulong CWBackPixel = 1UL << 1;
    public const ulong CWBorderPixel = 1UL << 3;
    public const ulong CWEventMask = 1UL << 11;
    public const ulong CWColormap = 1UL << 13;
    public const int IsViewable = 2;

    [DllImport(LibX11)] public static extern IntPtr XDefaultVisual(IntPtr display, int screen);
    [DllImport(LibX11)] public static extern int XDefaultDepth(IntPtr display, int screen);
    [DllImport(LibX11)] public static extern int XDefaultScreen(IntPtr display);

    [DllImport(LibX11)]
    public static extern IntPtr XCreateWindow(IntPtr display, IntPtr parent, int x, int y, uint width, uint height,
        uint borderWidth, int depth, uint windowClass, IntPtr visual, ulong valueMask, ref XSetWindowAttributes attributes);

    [DllImport(LibX11)] public static extern int XDestroyWindow(IntPtr display, IntPtr window);
    [DllImport(LibX11)] public static extern int XMapWindow(IntPtr display, IntPtr window);
    [DllImport(LibX11)] public static extern int XUnmapWindow(IntPtr display, IntPtr window);
    [DllImport(LibX11)] public static extern int XRaiseWindow(IntPtr display, IntPtr window);
    [DllImport(LibX11)] public static extern int XSelectInput(IntPtr display, IntPtr window, IntPtr eventMask);
    [DllImport(LibX11)] public static extern int XPending(IntPtr display);
    [DllImport(LibX11)] public static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
    [DllImport(LibX11)] public static extern int XFreePixmap(IntPtr display, IntPtr pixmap);
    [DllImport(LibX11)] public static extern int XStoreName(IntPtr display, IntPtr window, string name);
    [DllImport(LibX11)] public static extern int XGetWindowAttributes(IntPtr display, IntPtr window, out XWindowAttributes attributes);
    [DllImport(LibX11)] public static extern int XConnectionNumber(IntPtr display);

    [StructLayout(LayoutKind.Sequential)]
    public struct XSetWindowAttributes
    {
        public IntPtr BackgroundPixmap;
        public IntPtr BackgroundPixel;
        public IntPtr BorderPixmap;
        public IntPtr BorderPixel;
        public int BitGravity;
        public int WinGravity;
        public int BackingStore;
        public IntPtr BackingPlanes;
        public IntPtr BackingPixel;
        public int SaveUnder;
        public IntPtr EventMask;
        public IntPtr DoNotPropagateMask;
        public int OverrideRedirect;
        public IntPtr Colormap;
        public IntPtr Cursor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XWindowAttributes
    {
        public int X, Y, Width, Height, BorderWidth, Depth;
        public IntPtr Visual;
        public IntPtr Root;
        public int Class;
        public int BitGravity, WinGravity, BackingStore;
        public IntPtr BackingPlanes, BackingPixel;
        public int SaveUnder;
        public IntPtr Colormap;
        public int MapInstalled;
        public int MapState;
        public IntPtr AllEventMasks, YourEventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public IntPtr Screen;
    }

    // ---- Composite / Render / Damage (live previews) ----
    private const string LibXcomposite = "libXcomposite.so.1";
    private const string LibXrender = "libXrender.so.1";
    private const string LibXdamage = "libXdamage.so.1";

    public const int CompositeRedirectAutomatic = 0;
    public const int PictOpSrc = 1;
    public const int XDamageReportNonEmpty = 3;
    public const int XDamageNotify = 0;

    [DllImport(LibXcomposite)] public static extern bool XCompositeQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [DllImport(LibXcomposite)] public static extern void XCompositeRedirectWindow(IntPtr display, IntPtr window, int update);
    [DllImport(LibXcomposite)] public static extern void XCompositeUnredirectWindow(IntPtr display, IntPtr window, int update);
    [DllImport(LibXcomposite)] public static extern IntPtr XCompositeNameWindowPixmap(IntPtr display, IntPtr window);

    [DllImport(LibXrender)] public static extern IntPtr XRenderFindVisualFormat(IntPtr display, IntPtr visual);
    [DllImport(LibXrender)] public static extern IntPtr XRenderCreatePicture(IntPtr display, IntPtr drawable, IntPtr format, ulong valueMask, IntPtr attributes);
    [DllImport(LibXrender)] public static extern void XRenderFreePicture(IntPtr display, IntPtr picture);
    [DllImport(LibXrender)] public static extern void XRenderSetPictureTransform(IntPtr display, IntPtr picture, ref XTransform transform);
    [DllImport(LibXrender)] public static extern void XRenderSetPictureFilter(IntPtr display, IntPtr picture, string filter, IntPtr parameters, int nParams);

    [DllImport(LibXrender)]
    public static extern void XRenderComposite(IntPtr display, int op, IntPtr src, IntPtr mask, IntPtr dst,
        int srcX, int srcY, int maskX, int maskY, int dstX, int dstY, uint width, uint height);

    [DllImport(LibXdamage)] public static extern bool XDamageQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [DllImport(LibXdamage)] public static extern IntPtr XDamageCreate(IntPtr display, IntPtr drawable, int level);
    [DllImport(LibXdamage)] public static extern void XDamageDestroy(IntPtr display, IntPtr damage);
    [DllImport(LibXdamage)] public static extern void XDamageSubtract(IntPtr display, IntPtr damage, IntPtr repair, IntPtr parts);

    // 3x3 matrix of 16.16 fixed-point values. XRender maps DESTINATION pixels to SOURCE pixels through
    // it, so scaling a big window down into a small tile uses src/dst (a factor > 1), not dst/src.
    [StructLayout(LayoutKind.Sequential)]
    public struct XTransform
    {
        public int M00, M01, M02, M10, M11, M12, M20, M21, M22;

        public static XTransform Scale(double sx, double sy) => new()
        {
            M00 = ToFixed(sx),
            M11 = ToFixed(sy),
            M22 = ToFixed(1.0),
        };

        private static int ToFixed(double v) => (int)Math.Round(v * 65536.0);
    }

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

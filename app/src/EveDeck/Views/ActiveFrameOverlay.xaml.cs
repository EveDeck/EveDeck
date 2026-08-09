using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using EveDeck.Utilities;

namespace EveDeck.Views;

public partial class ActiveFrameOverlay : Window
{
    public ActiveFrameOverlay() => InitializeComponent();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlExStyle,
            Win32Native.GetWindowLongPtr(hwnd, Win32Native.GwlExStyle) | Win32Native.WsExTransparent);
    }

    public void ApplyFrame(int x, int y, int width, int height, int thickness, int glowRadius, bool glowEnabled, Brush brush, string style = "Snapshot")
    {
        // "SnapshotFrame" draws both at once: brackets ride slightly thicker than the plain
        // outline underneath them so they still read clearly at the corners. Pure "Snapshot"
        // shows brackets only; Solid/Dashed/Dotted show the outline only.
        var showBrackets = style.Equals("Snapshot", StringComparison.OrdinalIgnoreCase)
            || style.Equals("SnapshotFrame", StringComparison.OrdinalIgnoreCase);
        var showOutline = !style.Equals("Snapshot", StringComparison.OrdinalIgnoreCase);
        var bracketThickness = showOutline ? thickness + 2 : thickness;

        // The window is padded out beyond the client rect so a glow (when enabled) has room to
        // bloom OUTWARD; the plain outline never blurs, so it only ever needs room for its own
        // stroke. Blur would also smear a dash/dot pattern into a fuzzy near-solid line, which is
        // why Solid/Dashed/Dotted/the outline layer never use it regardless of this toggle.
        var blur = glowEnabled ? Math.Max(2.0, glowRadius) : 0.0;
        var bracketPad = (int)Math.Ceiling(blur * 3) + bracketThickness;
        var outlinePad = thickness;
        var pad = showBrackets ? Math.Max(bracketPad, outlinePad) : outlinePad;

        if (showBrackets)
        {
            FrameBrackets.Visibility = Visibility.Visible;
            FrameBrackets.Stroke = brush;
            FrameBrackets.StrokeThickness = bracketThickness * 2;
            FrameBrackets.StrokeDashArray = null;
            var arm = Math.Clamp(Math.Min(width, height) * 0.14, 18.0, 70.0);
            FrameBrackets.Data = OverlayGeometry.CornerBrackets(pad, pad, width, height, arm);
            if (glowEnabled)
            {
                FrameBlur.Radius = blur;
                FrameBrackets.Effect = FrameBlur;
            }
            else
            {
                FrameBrackets.Effect = null;
            }
        }
        else
        {
            FrameBrackets.Visibility = Visibility.Collapsed;
        }

        if (showOutline)
        {
            FrameOutline.Visibility = Visibility.Visible;
            FrameOutline.Stroke = brush;
            FrameOutline.StrokeThickness = thickness * 2;
            FrameOutline.Effect = null;
            FrameOutline.Data = OverlayGeometry.FullRect(pad, pad, width, height);
            switch (style.ToUpperInvariant())
            {
                case "DASHED":
                    FrameOutline.StrokeDashArray = new DoubleCollection { 3, 2 };
                    FrameOutline.StrokeDashCap = PenLineCap.Flat;
                    break;
                case "DOTTED":
                    FrameOutline.StrokeDashArray = new DoubleCollection { 0, 2 };
                    FrameOutline.StrokeDashCap = PenLineCap.Round;
                    break;
                default: // "Solid" / "SnapshotFrame"
                    FrameOutline.StrokeDashArray = null;
                    break;
            }
        }
        else
        {
            FrameOutline.Visibility = Visibility.Collapsed;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
            // Re-assert HWND_TOPMOST (not SwpNoZOrder) on every reposition: pinned EVE clients and
            // corner tiles are raised into the topmost band each tick, and with SwpNoZOrder the frame
            // stayed wherever it was and got buried behind them -- reading as flicker / disappearing.
            Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost, x - pad, y - pad, width + pad * 2, height + pad * 2,
                Win32Native.SwpNoActivate | Win32Native.SwpShowWindow);
    }

    // Re-raise to the top of the topmost band without moving/resizing. Called each tick while the
    // frame is visible so windows raised after the last ApplyFrame don't leave it covered.
    public void BringToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
            Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost, 0, 0, 0, 0,
                Win32Native.SwpNoMove | Win32Native.SwpNoSize | Win32Native.SwpNoActivate);
    }
}

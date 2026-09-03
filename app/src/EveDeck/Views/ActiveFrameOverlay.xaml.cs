using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using EveDeck.Utilities;

namespace EveDeck.Views;

public partial class ActiveFrameOverlay : Window
{
    // Internal render loop for the animated frame styles (Neon/BreathingGlow/Shimmer/Rainbow/
    // ElectricArc). OnFrameTick in the view-model re-invokes ApplyFrame only when the rect/style
    // changes, so animation has to be driven here.
    private readonly System.Windows.Threading.DispatcherTimer _anim =
        new() { Interval = TimeSpan.FromMilliseconds(33) };   // ~30 fps
    private double _phase;
    private int _fx, _fy, _fw, _fh, _fThickness;
    private Brush _fBrush = System.Windows.Media.Brushes.White;
    private string _fStyle = "Snapshot";

    private static readonly HashSet<string> DynamicStyles = new(StringComparer.OrdinalIgnoreCase)
        { "Neon", "BreathingGlow", "Shimmer", "Rainbow", "ElectricArc" };
    private static bool IsDynamic(string s) => DynamicStyles.Contains(s);
    private static bool IsZigzag(string s) => s.Equals("Zigzag", StringComparison.OrdinalIgnoreCase);

    public ActiveFrameOverlay()
    {
        InitializeComponent();
        _anim.Tick += (_, _) => { _phase += 0.30; RenderDynamic(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) _anim.Stop(); };
        Closed += (_, _) => _anim.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlExStyle,
            Win32Native.GetWindowLongPtr(hwnd, Win32Native.GwlExStyle) | Win32Native.WsExTransparent);
    }

    public void ApplyFrame(int x, int y, int width, int height, int thickness, int glowRadius, bool glowEnabled, Brush brush, string style = "Snapshot")
    {
        if (IsDynamic(style) || IsZigzag(style))
        {
            _fx = x; _fy = y; _fw = width; _fh = height;
            _fThickness = Math.Max(1, thickness); _fBrush = brush; _fStyle = style;

            FrameBrackets.Visibility = Visibility.Collapsed;
            FrameBrackets.Effect = null;

            var dynPad = IsZigzag(style) ? _fThickness + 6 : _fThickness + 26;
            RenderDynamic();                    // draw one frame immediately
            if (IsDynamic(style)) { if (!_anim.IsEnabled) _anim.Start(); }
            else _anim.Stop();

            var h2 = new WindowInteropHelper(this).Handle;
            if (h2 != 0)
                Win32Native.SetWindowPos(h2, Win32Native.HwndTopmost,
                    x - dynPad, y - dynPad, width + dynPad * 2, height + dynPad * 2,
                    Win32Native.SwpNoActivate | Win32Native.SwpShowWindow);
            return;
        }
        _anim.Stop();   // leaving a dynamic style -- make sure the timer is off
        FrameGlow.Visibility = Visibility.Collapsed;   // only the animated styles use it

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

    // Redraw the animated / zigzag frame from the cached _f* fields. Called once from ApplyFrame and
    // then every tick of _anim while a dynamic style is active.
    private void RenderDynamic()
    {
        var pad = IsZigzag(_fStyle) ? _fThickness + 6 : _fThickness + 26;
        double gx = pad, gy = pad;

        // Per-frame defaults; individual branches override as needed.
        FrameOutline.Opacity = 1.0;
        FrameOutline.StrokeStartLineCap = PenLineCap.Flat;
        FrameOutline.StrokeEndLineCap = PenLineCap.Flat;

        switch (_fStyle.ToUpperInvariant())
        {
            case "ZIGZAG":
                FrameGlow.Visibility = Visibility.Collapsed;
                FrameGlow.StrokeDashArray = null;
                FrameGlow.StrokeDashOffset = 0;
                FrameOutline.Visibility = Visibility.Visible;
                FrameOutline.Effect = null;
                FrameOutline.Stroke = _fBrush;
                FrameOutline.StrokeThickness = _fThickness * 2;
                FrameOutline.StrokeDashArray = null;
                FrameOutline.StrokeDashOffset = 0;
                FrameOutline.Data = OverlayGeometry.ZigzagPerimeter(gx, gy, _fw, _fh,
                    Math.Clamp(Math.Min(_fw, _fh) * 0.02, 3, 8));
                break;

            case "NEON":
            {
                var pulse = 0.5 * (1 + Math.Sin(_phase));
                FrameGlow.Visibility = Visibility.Visible;
                FrameGlow.Data = OverlayGeometry.FullRect(gx, gy, _fw, _fh);
                FrameGlow.Stroke = _fBrush;
                FrameGlow.StrokeThickness = _fThickness * 2 + 4;
                FrameGlow.Opacity = 0.35 + 0.4 * pulse;
                FrameGlow.StrokeDashArray = null;
                FrameGlow.StrokeDashOffset = 0;
                FrameGlowBlur.Radius = 8 + 8 * pulse;
                FrameOutline.Visibility = Visibility.Visible;
                FrameOutline.Effect = null;
                FrameOutline.Stroke = _fBrush;
                FrameOutline.StrokeThickness = _fThickness * 2;
                FrameOutline.StrokeDashArray = null;
                FrameOutline.StrokeDashOffset = 0;
                FrameOutline.Data = OverlayGeometry.FullRect(gx, gy, _fw, _fh);
                break;
            }

            case "BREATHINGGLOW":
            {
                var breath = 0.5 * (1 + Math.Sin(_phase * 0.5));
                FrameGlow.Visibility = Visibility.Visible;
                FrameGlow.Data = OverlayGeometry.FullRect(gx, gy, _fw, _fh);
                FrameGlow.Stroke = _fBrush;
                FrameGlow.StrokeThickness = _fThickness * 2 + 2 + 10 * breath;
                FrameGlow.Opacity = 0.15 + 0.5 * breath;
                FrameGlow.StrokeDashArray = null;
                FrameGlow.StrokeDashOffset = 0;
                FrameGlowBlur.Radius = 6 + 16 * breath;
                FrameOutline.Visibility = Visibility.Visible;
                FrameOutline.Effect = null;
                FrameOutline.Stroke = _fBrush;
                FrameOutline.StrokeThickness = _fThickness * 2;
                FrameOutline.StrokeDashArray = null;
                FrameOutline.StrokeDashOffset = 0;
                FrameOutline.Opacity = 0.45 + 0.55 * breath;
                FrameOutline.Data = OverlayGeometry.FullRect(gx, gy, _fw, _fh);
                break;
            }

            case "SHIMMER":
                FrameOutline.Visibility = Visibility.Visible;
                FrameOutline.Effect = null;
                FrameOutline.Stroke = _fBrush;
                FrameOutline.Opacity = 0.30;
                FrameOutline.StrokeThickness = _fThickness * 2;
                FrameOutline.StrokeDashArray = null;
                FrameOutline.StrokeDashOffset = 0;
                FrameOutline.Data = OverlayGeometry.FullRect(gx, gy, _fw, _fh);
                FrameGlow.Visibility = Visibility.Visible;
                FrameGlow.Data = OverlayGeometry.FullRect(gx, gy, _fw, _fh);
                FrameGlow.Stroke = _fBrush;
                FrameGlow.Opacity = 1.0;
                FrameGlow.StrokeThickness = _fThickness * 2;
                FrameGlowBlur.Radius = 3;
                FrameGlow.StrokeDashCap = PenLineCap.Round;
                FrameGlow.StrokeDashArray = new DoubleCollection { 1.5, 10.0 };
                FrameGlow.StrokeDashOffset = -_phase * 6.0;
                break;

            case "RAINBOW":
            {
                var (r, g, b) = HsvToRgb((_phase * 40.0) % 360.0, 0.85, 1.0);
                var rainbow = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
                rainbow.Freeze();
                FrameOutline.Visibility = Visibility.Visible;
                FrameOutline.Effect = null;
                FrameOutline.Stroke = rainbow;
                FrameOutline.Opacity = 1.0;
                FrameOutline.StrokeThickness = _fThickness * 2;
                FrameOutline.StrokeDashArray = null;
                FrameOutline.StrokeDashOffset = 0;
                FrameOutline.Data = OverlayGeometry.FullRect(gx, gy, _fw, _fh);
                FrameGlow.Visibility = Visibility.Visible;
                FrameGlow.Data = OverlayGeometry.FullRect(gx, gy, _fw, _fh);
                FrameGlow.Stroke = rainbow;
                FrameGlow.StrokeThickness = _fThickness * 2 + 4;
                FrameGlow.Opacity = 0.5;
                FrameGlow.StrokeDashArray = null;
                FrameGlow.StrokeDashOffset = 0;
                FrameGlowBlur.Radius = 8;
                break;
            }

            case "ELECTRICARC":
            {
                var amp = Math.Clamp(Math.Min(_fw, _fh) * 0.012, 2.0, 6.0);
                var geo = OverlayGeometry.JaggedPerimeter(gx, gy, _fw, _fh, amp, _phase * 6.0);
                FrameOutline.Visibility = Visibility.Visible;
                FrameOutline.Effect = null;
                FrameOutline.Stroke = _fBrush;
                FrameOutline.Opacity = 1.0;
                FrameOutline.StrokeThickness = _fThickness * 2;
                FrameOutline.StrokeDashArray = null;
                FrameOutline.StrokeDashOffset = 0;
                FrameOutline.StrokeStartLineCap = PenLineCap.Round;
                FrameOutline.StrokeEndLineCap = PenLineCap.Round;
                FrameOutline.Data = geo;
                FrameGlow.Visibility = Visibility.Visible;
                FrameGlow.Data = geo;
                FrameGlow.Stroke = _fBrush;
                FrameGlow.StrokeThickness = _fThickness * 2 + 3;
                FrameGlow.Opacity = 0.55;
                FrameGlow.StrokeDashArray = null;
                FrameGlow.StrokeDashOffset = 0;
                FrameGlowBlur.Radius = 6;
                break;
            }
        }
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = v - c;
        var (r, g, b) = ((int)(h / 60.0)) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
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

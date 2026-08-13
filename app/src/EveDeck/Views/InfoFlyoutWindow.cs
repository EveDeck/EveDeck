using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EveDeck.Utilities;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace EveDeck.Views;

// The compact card shown when a corner preview's "i" badge is clicked (added 2026-07-24). Shows a
// small set of live ESI facts (ship / system / training / jump fatigue) for that seat's character,
// one short monospace line each. Deliberately tiny and non-distracting -- it never activates or steals
// focus, and it pins to a physical-pixel position so it lands correctly on any per-monitor DPI.
//
// The window only renders; the view-model fetches the ESI facts and pushes them in via SetLines. It
// self-sizes to its content, then re-pins to the physical anchor once WPF has measured it.
internal sealed class InfoFlyoutWindow : Window
{
    private const int Gap = 2;

    private readonly StackPanel _lines = new();
    private readonly int _badgeLeft, _badgeTop, _badgeRight, _badgeBottom;
    private readonly double _dpiScale;
    private readonly double _chromeScale;
    private nint _ownerHwnd;

    // Full physical rect of the "i" badge. The card picks its open direction from which quadrant of
    // the badge's own monitor the badge sits in (not from a post-hoc overflow check against a
    // measured width, which is fragile around DPI/timing) -- a badge in a corner tile is very often
    // already hugging a screen edge, so PinPhysical decides up-front to open away from that edge, then
    // clamps as a last-resort safety net so the card can never render partly off-screen.
    public InfoFlyoutWindow(int badgeLeft, int badgeTop, int badgeRight, int badgeBottom, double dpiScale, string title, double chromeScale = 1.0)
    {
        _badgeLeft = badgeLeft;
        _badgeTop = badgeTop;
        _badgeRight = badgeRight;
        _badgeBottom = badgeBottom;
        _dpiScale = dpiScale;
        _chromeScale = chromeScale;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Rough placement for the first (unmeasured) paint -- PinPhysical corrects this to the
        // quadrant-aware, clamped position as soon as content is rendered.
        Left = badgeLeft / dpiScale;
        Top = badgeBottom / dpiScale;

        var header = new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12 * chromeScale,
            Margin = new Thickness(0, 0, 0, 4 * chromeScale),
        };

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(_lines);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0D, 0x11, 0x17)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(OverlayChrome.RadiusMd),
            Padding = new Thickness(OverlayChrome.PadSnugH * chromeScale, OverlayChrome.PadSnugV * chromeScale,
                                    OverlayChrome.PadSnugH * chromeScale, OverlayChrome.PadSnugV * chromeScale),
            Child = panel,
        };

        ContentRendered += (_, _) => PinPhysical();
        // Re-pin on every size change, and this is the fix for cards running off a screen edge.
        // SizeToContent means WPF resizes the window itself whenever the content grows -- and it lays
        // the new size out from the DIP Left/Top set in the constructor, which is the UN-flipped badge
        // anchor. So a card that PinPhysical had correctly flipped leftward would silently snap back
        // to left-aligned and grow rightward off the monitor as soon as the real ESI lines replaced
        // "Loading...". SizeChanged fires after that layout pass with the final measurements, so
        // re-pinning here re-applies the quadrant flip and clamp against a size that is actually correct.
        SizeChanged += (_, _) => PinPhysical();
    }

    // Owner (the label surface, which itself sits above the tile surface). Must be set before Show();
    // the window manager then keeps this card above the overlay surfaces permanently -- without it the
    // overlay's periodic topmost re-assert (MaintainCornerOverlays' ~2s safety net + foreground-change
    // reasserts) buries the card within a couple of seconds, which looked like it "disappeared".
    public void SetOwner(nint ownerHwnd) => _ownerHwnd = ownerHwnd;

    // Replaces the card's body lines (each a short "Label: value" string). Safe to call repeatedly as the async ESI
    // fetches resolve.
    public void SetLines(IEnumerable<string> lines)
    {
        _lines.Children.Clear();
        foreach (var line in lines)
        {
            _lines.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12 * _chromeScale,
                Margin = new Thickness(0, 1, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
            });
        }
        // Content changed size after the first pin; re-pin so the corner stays put. Loaded priority,
        // NOT the default Normal: WPF's layout/measure pass runs at Render priority, which is lower
        // than Normal, so a default BeginInvoke would run BEFORE the window had been re-measured and
        // would pin using the previous (smaller) width -- the card then grew past the clamp.
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(PinPhysical), DispatcherPriority.Loaded);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = Win32Native.GetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex).ToInt64();
        Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex,
            new nint(exStyle | Win32Native.WsExNoActivate | Win32Native.WsExToolWindow));

        // Own to the overlay surface so the WM keeps this card above it in the z-order permanently --
        // same trick LabelSurfaceWindow uses to stay above the tile surface with zero maintenance.
        if (_ownerHwnd != 0)
            Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlpHwndParent, _ownerHwnd);
    }

    // Pin the top-left to the physical anchor at the measured content size, clamped/flipped to stay
    // within the badge's own monitor work area. WPF's DIP Left/Top place a window through the PRIMARY
    // monitor's scale, which misplaces it on a monitor with a different per-monitor DPI; positioning
    // in physical pixels keeps the card glued to its badge.
    private void PinPhysical()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var w = (int)Math.Ceiling(ActualWidth * _dpiScale);
        var h = (int)Math.Ceiling(ActualHeight * _dpiScale);
        var (x, y) = ComputeEdgeAwarePosition(w, h);
        Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost, x, y, w, h,
            Win32Native.SwpNoActivate);
    }

    // Quadrant-first: if the badge sits in the right half of its monitor, open leftward (anchor the
    // card's right edge to the badge's right edge) instead of waiting to see if the default left-aligned
    // placement would overflow. Same for vertical: bottom-half badge opens upward. Finally clamp fully
    // inside the work area regardless, as a hard backstop. The maths now lives in OverlayChrome so the
    // hover tip can share it verbatim -- behaviour here is unchanged.
    private (int x, int y) ComputeEdgeAwarePosition(int w, int h) =>
        OverlayChrome.EdgeAwarePosition(_badgeLeft, _badgeTop, _badgeRight, _badgeBottom, w, h, Gap);
}

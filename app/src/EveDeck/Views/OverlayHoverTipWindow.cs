using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EveDeck.Utilities;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace EveDeck.Views;

// A tiny, click-through, single-line text popup for the jump-status badges' hover text (added
// 2026-07-28). LabelSurfaceWindow is WS_EX_TRANSPARENT end to end, so a WPF ToolTip on a badge drawn
// there would never receive the mouse-enter it needs to fire -- this window is shown/repositioned/
// retexted and hidden directly by MainWindowViewModel's existing tile cursor-poll instead. One shared
// instance is reused across every badge/tile (only one can be hovered at a time).
internal sealed class OverlayHoverTipWindow : Window
{
    // Gap between the badge and the tip, physical pixels.
    private const int Gap = 4;

    private readonly TextBlock _text;
    private readonly double _dpiScale;
    private nint _ownerHwnd;
    // Full physical rect of the badge being hovered. Kept whole (rather than a pre-computed top-left)
    // so PinPhysical can pick an open direction from which quadrant of the monitor the badge sits in.
    private int _badgeLeft, _badgeTop, _badgeRight, _badgeBottom;

    public OverlayHoverTipWindow(double dpiScale, double chromeScale = 1.0)
    {
        _dpiScale = dpiScale;
        _text = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11 * chromeScale,
        };

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0D, 0x11, 0x17)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(OverlayChrome.RadiusMd),
            Padding = new Thickness(OverlayChrome.PadTightH * chromeScale, OverlayChrome.PadTightV * chromeScale,
                                    OverlayChrome.PadTightH * chromeScale, OverlayChrome.PadTightV * chromeScale),
            Child = _text,
        };

        ContentRendered += (_, _) => PinPhysical();
        // Same SizeToContent trap as InfoFlyoutWindow: WPF re-lays the window out from the DIP
        // Left/Top (the un-flipped badge anchor) whenever the text changes, undoing the quadrant flip
        // and pushing the tip off the edge. Re-pin once the new size is real. This tip is reused
        // across badges rather than recreated, so it hits the case more often, not less.
        SizeChanged += (_, _) => PinPhysical();
    }

    // Owner (the label surface). Must be set before the first ShowAt(); keeps this above the overlay
    // surfaces in the z-order permanently, same trick as InfoFlyoutWindow/LabelSurfaceWindow.
    public void SetOwner(nint ownerHwnd) => _ownerHwnd = ownerHwnd;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = Win32Native.GetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex).ToInt64();
        Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex,
            new nint(exStyle | Win32Native.WsExNoActivate | Win32Native.WsExToolWindow | Win32Native.WsExTransparent));
        if (_ownerHwnd != 0)
            Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlpHwndParent, _ownerHwnd);
    }

    // Shows (or moves/retexts, if already visible) the tip next to the given physical badge rect.
    public void ShowAt(int badgePhysX, int badgePhysY, int badgePhysSize, string text)
    {
        _text.Text = text;
        _badgeLeft = badgePhysX;
        _badgeTop = badgePhysY;
        _badgeRight = badgePhysX + badgePhysSize;
        _badgeBottom = badgePhysY + badgePhysSize;
        if (!IsVisible) { Show(); return; } // ContentRendered pins once loaded
        // Loaded priority so this runs AFTER WPF's Render-priority layout pass, not before it with a
        // stale width -- see the matching comment in InfoFlyoutWindow.SetLines.
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(PinPhysical), DispatcherPriority.Loaded);
    }

    // Pin to the physical anchor at the measured content size, opening away from whichever screen
    // edge the badge is hugging and clamped inside that monitor's work area -- these badges live in a
    // corner TILE, so the naive "always directly below, left-aligned" placement ran the tip off-screen
    // for any tile near a monitor edge. WPF's DIP Left/Top would place the window through the PRIMARY
    // monitor's scale, which misplaces it on a differently-scaled monitor; physical pixels keep the
    // tip glued to its badge.
    private void PinPhysical()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var w = (int)Math.Ceiling(ActualWidth * _dpiScale);
        var h = (int)Math.Ceiling(ActualHeight * _dpiScale);
        var (x, y) = OverlayChrome.EdgeAwarePosition(_badgeLeft, _badgeTop, _badgeRight, _badgeBottom, w, h, Gap);
        Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost, x, y, w, h,
            Win32Native.SwpNoActivate);
    }
}

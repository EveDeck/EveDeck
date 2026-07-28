using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
    private readonly TextBlock _text = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 11,
    };
    private readonly double _dpiScale;
    private nint _ownerHwnd;
    private int _physX, _physY;

    public OverlayHoverTipWindow(double dpiScale)
    {
        _dpiScale = dpiScale;

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
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 6, 3),
            Child = _text,
        };

        ContentRendered += (_, _) => PinPhysical();
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

    // Shows (or moves/retexts, if already visible) the tip just below the given physical badge rect.
    public void ShowAt(int badgePhysX, int badgePhysY, int badgePhysSize, string text)
    {
        _text.Text = text;
        _physX = badgePhysX;
        _physY = badgePhysY + badgePhysSize + 4;
        if (!IsVisible) { Show(); return; } // ContentRendered pins once loaded
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(PinPhysical));
    }

    // Pin the top-left to the physical anchor at the measured content size -- WPF's DIP Left/Top
    // place a window through the PRIMARY monitor's scale, which misplaces it on a differently-scaled
    // monitor; positioning in physical pixels keeps the tip glued to its badge.
    private void PinPhysical()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var w = (int)Math.Ceiling(ActualWidth * _dpiScale);
        var h = (int)Math.Ceiling(ActualHeight * _dpiScale);
        Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost, _physX, _physY, w, h,
            Win32Native.SwpNoActivate);
    }
}

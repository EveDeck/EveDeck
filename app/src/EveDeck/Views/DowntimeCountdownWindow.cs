using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EveDeck.Utilities;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace EveDeck.Views;

// The small always-on-top readout shown during the final stretch before EVE downtime (added
// 2026-07-24). One short line ("Downtime in 12:34"), pinned to a chosen corner/edge of a monitor's
// work area (same six anchors as the toast position, so it can be moved off EVE's own top-centre UI),
// never activating or stealing focus. The view-model updates the text every second, and re-asserts
// z-order (SetZ) so the corner-overlay surfaces' periodic topmost re-assert can't bury it.
internal sealed class DowntimeCountdownWindow : Window
{
    private readonly TextBlock _text;
    private readonly int _workX, _workY, _workWidth, _workHeight;
    private readonly double _dpiScale;
    private readonly ToastAnchor _anchor;

    // Deliberately NOT scaled by AppSettings.CornerOverlayChromeScale (2026-08-04). That setting is
    // the badge/popup legibility multiplier -- it exists because a 20px "i" badge on a small high-DPI
    // monitor is unreadable. This card is already a full-size standing readout, so multiplying it too
    // turned a chrome scale of 4.0 into a 52pt banner spanning a fifth of the screen. Fixed size.
    public DowntimeCountdownWindow(int workX, int workY, int workWidth, int workHeight, double dpiScale, ToastAnchor anchor)
    {
        _workX = workX;
        _workY = workY;
        _workWidth = workWidth;
        _workHeight = workHeight;
        _dpiScale = dpiScale;
        _anchor = anchor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _text = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFD, 0xE0, 0x68)), // amber -- reads as "heads up"
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
        };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0D, 0x11, 0x17)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xFD, 0xE0, 0x68)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(OverlayChrome.RadiusMd),
            Padding = new Thickness(OverlayChrome.PadCardH, OverlayChrome.PadCardV,
                                    OverlayChrome.PadCardH, OverlayChrome.PadCardV),
            Child = _text,
        };

        ContentRendered += (_, _) => PinPosition();
    }

    public void SetText(string text)
    {
        _text.Text = text;
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(PinPosition));
    }

    // Re-assert HWND_TOPMOST without moving/resizing -- called by the view-model whenever the corner
    // overlay surfaces re-assert their own topmost, so this card rides back above them.
    public void SetZ()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost, 0, 0, 0, 0,
            Win32Native.SwpNoMove | Win32Native.SwpNoSize | Win32Native.SwpNoActivate);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = Win32Native.GetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex).ToInt64();
        Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex,
            new nint(exStyle | Win32Native.WsExNoActivate | Win32Native.WsExToolWindow));
    }

    // Anchor to the chosen corner/edge of the work area, in physical pixels (WPF's DIP Left/Top would
    // misplace it on a monitor whose DPI differs from the primary's), and re-assert topmost.
    private void PinPosition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var w = (int)Math.Ceiling(ActualWidth * _dpiScale);
        var h = (int)Math.Ceiling(ActualHeight * _dpiScale);
        var margin = (int)(8 * _dpiScale);

        var x = _anchor switch
        {
            ToastAnchor.TopLeft or ToastAnchor.BottomLeft => _workX + margin,
            ToastAnchor.TopRight or ToastAnchor.BottomRight => _workX + _workWidth - w - margin,
            _ => _workX + Math.Max(0, (_workWidth - w) / 2), // *Center
        };
        var isTop = _anchor is ToastAnchor.TopLeft or ToastAnchor.TopCenter or ToastAnchor.TopRight;
        var y = isTop ? _workY + margin : _workY + _workHeight - h - margin;

        Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost, x, y, w, h, Win32Native.SwpNoActivate);
    }
}

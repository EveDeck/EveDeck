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
// 2026-07-24). One short line ("Downtime in 12:34"), pinned to the top-centre of a monitor's work
// area, never activating or stealing focus. The view-model updates the text every second and
// shows/hides the window as the downtime window opens/closes.
internal sealed class DowntimeCountdownWindow : Window
{
    private readonly TextBlock _text;
    private readonly int _workX, _workY, _workWidth;
    private readonly double _dpiScale;

    public DowntimeCountdownWindow(int workX, int workY, int workWidth, double dpiScale)
    {
        _workX = workX;
        _workY = workY;
        _workWidth = workWidth;
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
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6, 12, 6),
            Child = _text,
        };

        ContentRendered += (_, _) => PinTopCentre();
    }

    public void SetText(string text)
    {
        _text.Text = text;
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(PinTopCentre));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = Win32Native.GetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex).ToInt64();
        Win32Native.SetWindowLongPtr(hwnd, Win32Native.GwlExStyleIndex,
            new nint(exStyle | Win32Native.WsExNoActivate | Win32Native.WsExToolWindow));
    }

    // Centre horizontally on the work area, a little below its top edge, in physical pixels (WPF's DIP
    // Left/Top would misplace it on a monitor whose DPI differs from the primary's).
    private void PinTopCentre()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var w = (int)Math.Ceiling(ActualWidth * _dpiScale);
        var h = (int)Math.Ceiling(ActualHeight * _dpiScale);
        var x = _workX + Math.Max(0, (_workWidth - w) / 2);
        var y = _workY + (int)(8 * _dpiScale);
        Win32Native.SetWindowPos(hwnd, Win32Native.HwndTopmost, x, y, w, h, Win32Native.SwpNoActivate);
    }
}

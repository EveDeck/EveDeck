using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private readonly StackPanel _lines = new();
    // Collapsible "Planets" dropdown (added 2026-07-24): rebuilt wholesale by BuildPlanetsSection each
    // SetLines call, same "cheap to fully repaint, no incremental diffing" approach as _lines above.
    private readonly StackPanel _planetsSection = new();
    private readonly int _physX, _physY;
    private readonly double _dpiScale;
    private nint _ownerHwnd;

    public InfoFlyoutWindow(int physX, int physY, double dpiScale, string title)
    {
        _physX = physX;
        _physY = physY;
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
        Left = physX / dpiScale;
        Top = physY / dpiScale;

        var header = new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
        };

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(_lines);
        panel.Children.Add(_planetsSection);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0D, 0x11, 0x17)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = panel,
        };

        ContentRendered += (_, _) => PinPhysical();
    }

    // Owner (the label surface, which itself sits above the tile surface). Must be set before Show();
    // the window manager then keeps this card above the overlay surfaces permanently -- without it the
    // overlay's periodic topmost re-assert (MaintainCornerOverlays' ~2s safety net + foreground-change
    // reasserts) buries the card within a couple of seconds, which looked like it "disappeared".
    public void SetOwner(nint ownerHwnd) => _ownerHwnd = ownerHwnd;

    // Replaces the card's body lines (each a short "Label: value" string), plus the optional collapsible
    // "Planets" dropdown (one row per colony, its soonest extractor countdown -- see
    // MainWindowViewModel.InfoFlyout.cs.PopulateInfoFlyoutAsync). Null/empty omits the dropdown
    // entirely rather than showing an empty "Planets" header. Safe to call repeatedly as the async ESI
    // fetches resolve.
    public void SetLines(IEnumerable<string> lines, IReadOnlyList<string>? planetLines = null)
    {
        _lines.Children.Clear();
        foreach (var line in lines)
        {
            _lines.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 1, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
            });
        }
        BuildPlanetsSection(planetLines);
        // Content changed size after the first pin; re-pin so the corner stays put.
        if (IsLoaded) Dispatcher.BeginInvoke(new Action(PinPhysical));
    }

    // Builds (or removes) the "Planets" dropdown: a clickable header row ("<arrow> Planets (N)") that
    // toggles an indented body of per-colony countdown lines. Built from scratch on every call rather
    // than diffed in place -- the card is small and this only runs once per flyout open, so the
    // simplicity is worth more than the saved allocations. Starts collapsed every time: the window
    // itself is recreated fresh on each badge click (see MainWindowViewModel.InfoFlyout.OnInfoButtonClicked),
    // so there is no prior expanded/collapsed state to preserve.
    private void BuildPlanetsSection(IReadOnlyList<string>? planetLines)
    {
        _planetsSection.Children.Clear();
        if (planetLines is null || planetLines.Count == 0) return;

        var mutedBrush = new SolidColorBrush(Color.FromRgb(0x74, 0x88, 0xA0));
        var textBrush = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));

        var arrow = new TextBlock
        {
            Text = "▸", // ▸ (collapsed)
            Foreground = mutedBrush,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var headerText = new TextBlock
        {
            Text = $"Planets ({planetLines.Count})",
            Foreground = textBrush,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
        };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        headerRow.Children.Add(arrow);
        headerRow.Children.Add(headerText);

        var headerBorder = new Border
        {
            Background = Brushes.Transparent, // makes the whole row (not just glyph/text) hit-testable
            Padding = new Thickness(0, 2, 0, 2),
            Margin = new Thickness(0, 5, 0, 0),
            Cursor = Cursors.Hand,
            Child = headerRow,
        };

        var body = new StackPanel { Margin = new Thickness(14, 2, 0, 0), Visibility = Visibility.Collapsed };
        foreach (var line in planetLines)
        {
            body.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = textBrush,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Margin = new Thickness(0, 1, 0, 1),
            });
        }

        var expanded = false;
        headerBorder.MouseLeftButtonUp += (_, _) =>
        {
            expanded = !expanded;
            arrow.Text = expanded ? "▾" : "▸"; // ▾ expanded / ▸ collapsed
            body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            PinPhysical(); // body's height just changed the window's SizeToContent measurement
        };

        _planetsSection.Children.Add(headerBorder);
        _planetsSection.Children.Add(body);
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

    // Pin the top-left to the physical anchor at the measured content size. WPF's DIP Left/Top place a
    // window through the PRIMARY monitor's scale, which misplaces it on a monitor with a different
    // per-monitor DPI; positioning in physical pixels keeps the card glued to its badge.
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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EveDeck.Services.Intel;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace EveDeck.Views;

/// <summary>
/// A small always-on-top card showing the most recent intel lines and how far away each one is.
///
/// Follows <see cref="DowntimeCountdownWindow"/>'s shape: borderless, transparent, never activated,
/// pinned to a <see cref="ToastAnchor"/> corner of the work area. It deliberately does not draw over
/// the master preview — it sits beside the layout so it never competes with a client's own pixels.
/// </summary>
internal sealed class IntelOverlayWindow : Window
{
    private const int EdgeMargin = 12;

    private static readonly Brush HostileBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));

    private readonly StackPanel _rows;
    private readonly TextBlock _status;
    private readonly int _workX;
    private readonly int _workY;
    private readonly int _workWidth;
    private readonly int _workHeight;
    private readonly ToastAnchor _anchor;
    private readonly double _fontSize;

    public IntelOverlayWindow(
        int workX,
        int workY,
        int workWidth,
        int workHeight,
        ToastAnchor anchor,
        double fontSize,
        double opacity)
    {
        _workX = workX;
        _workY = workY;
        _workWidth = workWidth;
        _workHeight = workHeight;
        _anchor = anchor;
        _fontSize = fontSize;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;

        _status = new TextBlock
        {
            Foreground = MutedBrush,
            FontSize = Math.Max(9.0, fontSize - 2.0),
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };

        _rows = new StackPanel();

        var content = new StackPanel();
        content.Children.Add(_status);
        content.Children.Add(_rows);

        Content = new Border
        {
            CornerRadius = new CornerRadius(OverlayChrome.RadiusMd),
            Padding = new Thickness(
                OverlayChrome.PadCardH,
                OverlayChrome.PadCardV,
                OverlayChrome.PadCardH,
                OverlayChrome.PadCardV),
            Background = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(opacity * 255.0, 0, 255), 0x11, 0x14, 0x1A)),
            Child = content,
        };

        Loaded += (_, _) => PinPosition();
    }

    /// <summary>
    /// Renders the newest entries, oldest first so the freshest line sits at the bottom nearest the
    /// eye, and states what the range is measured from.
    /// </summary>
    public void Update(IReadOnlyList<IntelFeedEntry> entries, FollowedOriginStatus origins, int maxRows)
    {
        _status.Text = DescribeOrigins(origins);
        _status.Foreground = origins.AnyUsable ? MutedBrush : HostileBrush;

        _rows.Children.Clear();

        var visible = entries.Count > maxRows ? entries.Skip(entries.Count - maxRows) : entries;
        foreach (var entry in visible) _rows.Children.Add(BuildRow(entry));

        if (_rows.Children.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "No intel yet.",
                Foreground = MutedBrush,
                FontSize = _fontSize,
            });
        }

        if (IsLoaded) Dispatcher.BeginInvoke(new Action(PinPosition));
    }

    /// <summary>
    /// Says plainly what anchors the range. A followed character in abyssal space has no position in
    /// the stargate graph, so leaving that unsaid would present "no range known" and "nothing nearby"
    /// as the same thing.
    /// </summary>
    private static string DescribeOrigins(FollowedOriginStatus origins)
    {
        if (!origins.AnyUsable)
        {
            return origins.WithoutKspaceLocation.Count > 0
                ? $"Range unavailable — no followed character has a known k-space position ({origins.WithoutKspaceLocation.Count} in abyssal or unknown space)."
                : "Range unavailable — no followed characters selected.";
        }

        var measured = $"Range from {string.Join(", ", origins.Usable)}";
        return origins.WithoutKspaceLocation.Count == 0
            ? measured
            : $"{measured} ({origins.WithoutKspaceLocation.Count} in abyssal or unknown space)";
    }

    private UIElement BuildRow(IntelFeedEntry entry)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };

        row.Children.Add(new TextBlock
        {
            Text = entry.RangeKnown ? $"{entry.JumpsAway}j" : "—",
            Foreground = entry.IsHostile ? HostileBrush : MutedBrush,
            FontSize = _fontSize,
            FontWeight = FontWeights.SemiBold,
            Width = 34,
        });

        row.Children.Add(new TextBlock
        {
            Text = entry.Message.Raw,
            Foreground = entry.IsHostile ? HostileBrush : TextBrush,
            FontSize = _fontSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 380,
        });

        return row;
    }

    private void PinPosition()
    {
        var w = ActualWidth;
        var h = ActualHeight;

        Left = _anchor switch
        {
            ToastAnchor.TopLeft or ToastAnchor.BottomLeft => _workX + EdgeMargin,
            ToastAnchor.TopRight or ToastAnchor.BottomRight => _workX + _workWidth - w - EdgeMargin,
            _ => _workX + Math.Max(0, (_workWidth - w) / 2),
        };

        var isTop = _anchor is ToastAnchor.TopLeft or ToastAnchor.TopCenter or ToastAnchor.TopRight;
        Top = isTop ? _workY + EdgeMargin : _workY + _workHeight - h - EdgeMargin;
    }
}

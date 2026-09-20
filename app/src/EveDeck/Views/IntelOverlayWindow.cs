using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.Services.Intel;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace EveDeck.Views;

/// <summary>
/// A small always-on-top card showing the most recent intel lines and how far away each one is.
///
/// Follows the shape of the removed utility overlays (see <c>TalkerOverlayWindow</c> in git history):
/// borderless, transparent, never activated, dragged into place with <c>DragMove()</c> rather than
/// pinned to a corner. It deliberately does not draw over the master preview — it sits beside the
/// layout so it never competes with a client's own pixels.
/// </summary>
internal sealed class IntelOverlayWindow : Window
{
    /// <summary>Where the card lands the first time, when no position has been saved yet.</summary>
    private const double DefaultLeft = 120d;

    private const double DefaultTop = 120d;

    private static readonly Brush HostileBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));

    private readonly StackPanel _rows;
    private readonly TextBlock _status;
    private readonly TextBlock _dragHint;
    private readonly double _fontSize;
    private readonly Action<int, int>? _onMoved;
    private bool _locked;

    // Portraits and ship icons download asynchronously and land after a row has already been drawn
    // once (blank), so the last render is kept around purely to redraw when one arrives -- the same
    // shape LabelSurfaceWindow uses via PortraitCacheService.Changed, but local to this window rather
    // than routed through the ViewModel.
    private IReadOnlyList<IntelFeedEntry> _lastEntries = [];
    private FollowedOriginStatus _lastOrigins = new([], []);
    private int _lastMaxRows;

    public IntelOverlayWindow(
        int savedX,
        int savedY,
        bool locked,
        double fontSize,
        double opacity,
        Action<int, int>? onMoved = null)
    {
        _fontSize = fontSize;
        _onMoved = onMoved;
        _locked = locked;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        // 0,0 means the card has never been placed; drop it somewhere visible rather than in the
        // very corner where it can end up under other chrome.
        Left = savedX == 0 && savedY == 0 ? DefaultLeft : savedX;
        Top = savedX == 0 && savedY == 0 ? DefaultTop : savedY;

        _status = new TextBlock
        {
            Foreground = MutedBrush,
            FontSize = Math.Max(9.0, fontSize - 2.0),
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };

        _rows = new StackPanel();

        _dragHint = new TextBlock
        {
            Text = "Drag to move · lock it in Options when you are happy with the spot",
            Foreground = MutedBrush,
            FontSize = Math.Max(9.0, fontSize - 3.0),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };

        var content = new StackPanel();
        content.Children.Add(_status);
        content.Children.Add(_rows);
        content.Children.Add(_dragHint);

        var root = new Border
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
        root.MouseLeftButtonDown += OnRootMouseLeftButtonDown;

        Content = root;
        ApplyLock(locked);

        PortraitCacheService.Instance.Changed += OnImageCacheChanged;
        ShipIconCacheService.Instance.Changed += OnImageCacheChanged;
        Closed += (_, _) =>
        {
            PortraitCacheService.Instance.Changed -= OnImageCacheChanged;
            ShipIconCacheService.Instance.Changed -= OnImageCacheChanged;
        };
    }

    private void OnImageCacheChanged() => Update(_lastEntries, _lastOrigins, _lastMaxRows);

    /// <summary>
    /// Locked does two things at once: it stops the card being dragged, and it makes the whole window
    /// click-through so it cannot swallow a click aimed at the client underneath. Unlocked it must be
    /// hit-testable, or there is nothing to grab.
    /// </summary>
    public void ApplyLock(bool locked)
    {
        _locked = locked;
        IsHitTestVisible = !locked;
        Cursor = locked ? null : System.Windows.Input.Cursors.SizeAll;
        _dragHint.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRootMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_locked) return;

        try
        {
            // DragMove blocks until the button is released, so persisting straight after it is safe.
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return; // no active drag (button already released) -- nothing to persist
        }

        _onMoved?.Invoke((int)Left, (int)Top);
    }

    /// <summary>
    /// Renders the newest entries, oldest first so the freshest line sits at the bottom nearest the
    /// eye, and states what the range is measured from.
    /// </summary>
    public void Update(IReadOnlyList<IntelFeedEntry> entries, FollowedOriginStatus origins, int maxRows)
    {
        _lastEntries = entries;
        _lastOrigins = origins;
        _lastMaxRows = maxRows;

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
        var container = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 1, 0, 1) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var iconSize = Math.Max(14.0, _fontSize + 4.0);

        row.Children.Add(new TextBlock
        {
            Text = entry.RangeKnown ? $"{entry.JumpsAway}j" : "—",
            Foreground = entry.IsHostile ? HostileBrush : MutedBrush,
            FontSize = _fontSize,
            FontWeight = FontWeights.SemiBold,
            Width = 34,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Pilot names are parsed text, never ESI-resolved to an id, so the portrait is looked up by
        // name -- it renders once ForName's own lookup resolves, same latency as everywhere else that
        // shows a name-only portrait. Ship icons need no such lookup: the parser already resolved the
        // hull to a typeId against the SDE.
        var pilotName = entry.Message.Players.FirstOrDefault();
        var portrait = pilotName is null ? null : PortraitCacheService.Instance.ForName(pilotName);
        AddIcon(row, portrait?.Image, iconSize);

        if (pilotName is not null)
        {
            var nameForeground = entry.IsHostile ? HostileBrush : TextBrush;
            if (portrait?.CharacterId is long charId and > 0)
            {
                var link = new Hyperlink(new Run(pilotName)) { TextDecorations = null };
                link.Click += (_, _) => OpenUrl($"https://zkillboard.com/character/{charId}/");
                var nameBlock = new TextBlock
                {
                    Foreground = nameForeground,
                    FontSize = _fontSize,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 4, 0),
                };
                nameBlock.Inlines.Add(link);
                row.Children.Add(nameBlock);
            }
            else
            {
                row.Children.Add(new TextBlock
                {
                    Text = pilotName,
                    Foreground = nameForeground,
                    FontSize = _fontSize,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 4, 0),
                });
            }
        }

        var shipTypeId = entry.Message.Ships.FirstOrDefault(s => s.TypeId is > 0)?.TypeId;
        var shipIcon = shipTypeId is int id ? ShipIconCacheService.Instance.ForId(id) : null;
        AddIcon(row, shipIcon?.Image, iconSize);

        row.Children.Add(new TextBlock
        {
            Text = entry.Message.Raw,
            Foreground = entry.IsHostile ? HostileBrush : TextBrush,
            FontSize = _fontSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 380,
            VerticalAlignment = VerticalAlignment.Center,
        });

        container.Children.Add(row);

        if (entry.NearestCharacter is string nearest)
        {
            var nearestRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(38, 0, 0, 0) };
            var nearestFontSize = Math.Max(9.0, _fontSize - 2);

            nearestRow.Children.Add(new TextBlock
            {
                Text = "near ",
                Foreground = MutedBrush,
                FontSize = nearestFontSize,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var nearestPortrait = PortraitCacheService.Instance.ForName(nearest);
            AddIcon(nearestRow, nearestPortrait?.Image, Math.Max(12.0, _fontSize));

            nearestRow.Children.Add(new TextBlock
            {
                Text = nearest,
                Foreground = MutedBrush,
                FontSize = nearestFontSize,
                VerticalAlignment = VerticalAlignment.Center,
            });

            container.Children.Add(nearestRow);
        }

        return container;
    }

    /// <summary>
    /// A small square image slot, or nothing at all rather than a placeholder box -- an icon that
    /// hasn't downloaded yet should not visually claim a pilot/ship was present when the row is really
    /// just terse (a bare "clr", no ship or name in the line at all).
    /// </summary>
    private static void AddIcon(StackPanel row, ImageSource? image, double size)
    {
        if (image is null) return;

        row.Children.Add(new Image
        {
            Source = image,
            Width = size,
            Height = size,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* best-effort; nothing sensible to do if the shell can't open a browser */ }
    }
}

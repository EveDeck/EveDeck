using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Input;
using EveDeck.Models.Intel;
using EveDeck.Services.Intel;
using EveDeck.Utilities;
using EveDeck.Views;
using Application = System.Windows.Application;

namespace EveDeck.ViewModels;

// Intel overlay: an in-app feed of the EVE intel channels this PC's chatlogs already carry, parsed
// in-process by Services\Intel (a C# port of the standalone EveDeck Intel daemon's pipeline). The
// standalone daemon and its Android app are unaffected and remain the option for reading intel on a
// second device; this exists so an EveDeck user does not need a second process to see the same feed.
public sealed partial class MainWindowViewModel
{
    private static readonly TimeSpan IntelToastCooldown = TimeSpan.FromSeconds(45);

    private IntelLogTailer? _intelTailer;
    private IntelFeedService? _intelFeed;
    private Universe? _intelUniverse;
    private IntelOverlayWindow? _intelOverlayWindow;
    private readonly Dictionary<int, DateTimeOffset> _intelLastToastBySystem = [];
    private bool _intelStarting;

    public ICommand RefreshIntelOptionsCommand { get; private set; } = null!;

    private void InitIntel()
    {
        RefreshIntelOptionsCommand = new RelayCommand(_ => RefreshIntelOptions());

        // A second subscriber to the same event the chat-alert path uses. Locations come from Local,
        // which ChatLogWatcherService already tails; the intel tailer deliberately does not duplicate
        // that work.
        _chatLogWatcherService.SystemChanged += (character, system) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => _intelFeed?.UpdateLocation(character, system));
        };

        _settings.IntelChannels.CollectionChanged += OnIntelChannelsChanged;
        _settings.IntelFollowedCharacters.CollectionChanged += OnIntelFollowedChanged;

        RefreshIntelOptions();
        if (_settings.IntelOverlayEnabled) StartIntel();
    }

    public bool IntelOverlayEnabled
    {
        get => _settings.IntelOverlayEnabled;
        set
        {
            if (_settings.IntelOverlayEnabled == value) return;
            _settings.IntelOverlayEnabled = value;
            OnPropertyChanged();
            Save();
            if (value) StartIntel();
            else StopIntel();
        }
    }

    public int IntelOverlayMaxRows
    {
        get => _settings.IntelOverlayMaxRows;
        set
        {
            var v = Math.Clamp(value, 1, 40);
            if (_settings.IntelOverlayMaxRows == v) return;
            _settings.IntelOverlayMaxRows = v;
            OnPropertyChanged();
            Save();
            RefreshIntelOverlay();
        }
    }

    public int IntelHostileToastJumps
    {
        get => _settings.IntelHostileToastJumps;
        set
        {
            var v = Math.Clamp(value, -1, 20);
            if (_settings.IntelHostileToastJumps == v) return;
            _settings.IntelHostileToastJumps = v;
            OnPropertyChanged();
            Save();
        }
    }

    public double IntelOverlayFontSize
    {
        get => _settings.IntelOverlayFontSize;
        set
        {
            var v = Math.Clamp(value, 9.0, 32.0);
            if (Math.Abs(_settings.IntelOverlayFontSize - v) < 0.01) return;
            _settings.IntelOverlayFontSize = v;
            OnPropertyChanged();
            Save();
            RecreateIntelOverlay();
        }
    }

    public double IntelOverlayOpacity
    {
        get => _settings.IntelOverlayOpacity;
        set
        {
            var v = Math.Clamp(value, 0.1, 1.0);
            if (Math.Abs(_settings.IntelOverlayOpacity - v) < 0.01) return;
            _settings.IntelOverlayOpacity = v;
            OnPropertyChanged();
            Save();
            RecreateIntelOverlay();
        }
    }

    public string IntelOverlayAnchor
    {
        get => _settings.IntelOverlayAnchor;
        set
        {
            if (_settings.IntelOverlayAnchor == value) return;
            _settings.IntelOverlayAnchor = value;
            OnPropertyChanged();
            Save();
            RecreateIntelOverlay();
        }
    }

    public ObservableCollection<string> IntelChannels => _settings.IntelChannels;

    public ObservableCollection<string> IntelFollowedCharacters => _settings.IntelFollowedCharacters;

    /// <summary>
    /// Channel names found in the chatlog folder, so the settings UI can offer what this install has
    /// actually seen rather than asking the user to type a name exactly right. Local is excluded: it
    /// is read for character positions and is not an intel channel.
    /// </summary>
    public IReadOnlyList<string> DiscoverIntelChannels()
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs", "Chatlogs");
            if (!Directory.Exists(folder)) return [];

            var cutoff = DateTime.Now.AddDays(-30);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in Directory.EnumerateFiles(folder, "*.txt"))
            {
                try
                {
                    if (File.GetLastWriteTime(path) < cutoff) continue;
                    var parsed = ChatLogFormat.ParseFileName(Path.GetFileName(path));
                    if (parsed is null) continue;
                    if (parsed.Channel.Equals("Local", StringComparison.OrdinalIgnoreCase)) continue;
                    names.Add(parsed.Channel);
                }
                catch { /* unreadable entry -- skip */ }
            }

            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not enumerate intel channels: {ex}");
            return [];
        }
    }

    /// <summary>Discovered channels as tickable rows, reflecting what is already saved.</summary>
    public ObservableCollection<IntelSelectableName> IntelChannelOptions { get; } = [];

    /// <summary>
    /// Characters available to follow. Several can be ticked: range is reported from whichever is
    /// nearest, which is the point for a pilot flying more than one client.
    /// </summary>
    public ObservableCollection<IntelSelectableName> IntelCharacterOptions { get; } = [];

    public void RefreshIntelOptions()
    {
        IntelChannelOptions.Clear();
        foreach (var channel in DiscoverIntelChannels())
        {
            IntelChannelOptions.Add(new IntelSelectableName(
                channel,
                _settings.IntelChannels.Contains(channel, StringComparer.OrdinalIgnoreCase),
                ToggleIntelChannel));
        }

        IntelCharacterOptions.Clear();
        foreach (var character in KnownIntelCharacters())
        {
            IntelCharacterOptions.Add(new IntelSelectableName(
                character,
                _settings.IntelFollowedCharacters.Contains(character, StringComparer.OrdinalIgnoreCase),
                ToggleIntelFollowedCharacter));
        }
    }

    /// <summary>
    /// Every character this install knows about: the ones currently logged in, plus any already
    /// followed, so a selection never silently disappears while that character is offline.
    /// </summary>
    private IReadOnlyList<string> KnownIntelCharacters()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var character in _systemByCharacter.Keys) names.Add(character);
        foreach (var character in _settings.IntelFollowedCharacters) names.Add(character);

        foreach (var assignment in Assignments)
        {
            var running = assignment.RunningCharacterName;
            if (!string.IsNullOrWhiteSpace(running)) names.Add(running);
        }

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ToggleIntelChannel(IntelSelectableName item) =>
        ToggleIntelSelection(_settings.IntelChannels, item);

    private void ToggleIntelFollowedCharacter(IntelSelectableName item) =>
        ToggleIntelSelection(_settings.IntelFollowedCharacters, item);

    private static void ToggleIntelSelection(ObservableCollection<string> target, IntelSelectableName item)
    {
        var existing = target.FirstOrDefault(v => string.Equals(v, item.Name, StringComparison.OrdinalIgnoreCase));

        if (item.IsSelected)
        {
            if (existing is null) target.Add(item.Name);
        }
        else if (existing is not null)
        {
            target.Remove(existing);
        }
    }

    private void OnIntelChannelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _intelTailer?.SetChannels(_settings.IntelChannels);
        Save();
    }

    private void OnIntelFollowedChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _intelFeed?.SetFollowedCharacters(_settings.IntelFollowedCharacters);
        RefreshIntelOverlay();
        Save();
    }

    private void StartIntel()
    {
        if (_intelFeed is not null || _intelStarting) return;
        _intelStarting = true;

        // ~1 MB of universe data: parsed off the UI thread so enabling the overlay never stalls the
        // window, then wired up back on it.
        Task.Run(() =>
            {
                try
                {
                    return Universe.LoadFromEmbeddedResource();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Intel overlay could not load universe data: {ex}");
                    return null;
                }
            })
            .ContinueWith(task =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _intelStarting = false;
                    var universe = task.Result;
                    if (universe is null)
                    {
                        Status = "Intel overlay unavailable: universe data failed to load.";
                        return;
                    }

                    if (!_settings.IntelOverlayEnabled) return;

                    _intelUniverse = universe;
                    _intelTailer = new IntelLogTailer();
                    _intelTailer.ErrorOccurred += msg => Log.Warn(msg);

                    _intelFeed = new IntelFeedService(universe, _intelTailer);
                    _intelFeed.EntryAdded += OnIntelEntryAdded;
                    _intelFeed.SetFollowedCharacters(_settings.IntelFollowedCharacters);

                    // Seed positions already known from Local so range works before the next jump.
                    foreach (var assignment in Assignments)
                    {
                        var character = assignment.RunningCharacterName;
                        if (!string.IsNullOrWhiteSpace(character)) SeedIntelLocation(character);
                    }

                    _intelTailer.SetChannels(_settings.IntelChannels);
                    _intelTailer.Start();

                    RefreshIntelOverlay();
                    Log.Info($"Intel overlay started ({_settings.IntelChannels.Count} channel(s)).");
                });
            });
    }

    private void SeedIntelLocation(string character)
    {
        var system = _systemByCharacter.GetValueOrDefault(character, "");
        if (!string.IsNullOrWhiteSpace(system)) _intelFeed?.UpdateLocation(character, system);
    }

    private void StopIntel()
    {
        if (_intelTailer is not null)
        {
            _intelTailer.Stop();
            _intelTailer.Dispose();
            _intelTailer = null;
        }

        if (_intelFeed is not null)
        {
            _intelFeed.EntryAdded -= OnIntelEntryAdded;
            _intelFeed.Dispose();
            _intelFeed = null;
        }

        _intelUniverse = null;
        _intelLastToastBySystem.Clear();
        HideIntelOverlay();
    }

    private void OnIntelEntryAdded(IntelFeedEntry entry)
    {
        // Raised from the tailer's polling thread; every UI touch below must be marshalled.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RefreshIntelOverlay();
            MaybeToastIntel(entry);
        });
    }

    private void MaybeToastIntel(IntelFeedEntry entry)
    {
        if (!entry.IsHostile) return;

        var threshold = _settings.IntelHostileToastJumps;
        if (threshold < 0) return;

        // No known range is not "in range". Staying silent here is deliberate: the overlay already
        // states that range is unavailable, and guessing would alert on the whole of New Eden.
        if (entry.JumpsAway is not { } jumps || jumps > threshold) return;

        var systemId = entry.Message.SystemIds.FirstOrDefault();
        var now = DateTimeOffset.UtcNow;
        if (_intelLastToastBySystem.TryGetValue(systemId, out var last) && now - last < IntelToastCooldown) return;
        _intelLastToastBySystem[systemId] = now;

        var where = jumps == 0 ? "in system" : $"{jumps} jump{(jumps == 1 ? "" : "s")} out";
        var who = entry.NearestCharacter is null ? "" : $" from {entry.NearestCharacter}";
        ShowToast($"Hostile intel — {where}{who}", entry.Message.Raw, "#F87171");
    }

    private void RefreshIntelOverlay()
    {
        if (_intelFeed is null || !_settings.IntelOverlayEnabled)
        {
            HideIntelOverlay();
            return;
        }

        if (_intelOverlayWindow is null)
        {
            var monitor = Monitors.FirstOrDefault(m => m.Id == LayoutTargetMonitorId) ?? Monitors.FirstOrDefault();
            if (monitor is null) return;

            _intelOverlayWindow = new IntelOverlayWindow(
                monitor.WorkArea.X,
                monitor.WorkArea.Y,
                monitor.WorkArea.Width,
                monitor.WorkArea.Height,
                ParseToastAnchor(_settings.IntelOverlayAnchor),
                _settings.IntelOverlayFontSize,
                _settings.IntelOverlayOpacity);
            _intelOverlayWindow.Show();
        }

        _intelOverlayWindow.Update(_intelFeed.History, _intelFeed.OriginStatus, _settings.IntelOverlayMaxRows);
    }

    private void RecreateIntelOverlay()
    {
        HideIntelOverlay();
        if (_settings.IntelOverlayEnabled) RefreshIntelOverlay();
    }

    private void HideIntelOverlay()
    {
        if (_intelOverlayWindow is null) return;
        try { _intelOverlayWindow.Close(); } catch { /* window may already be gone */ }
        _intelOverlayWindow = null;
    }
}

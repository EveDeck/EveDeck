using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Media;
using System.Windows.Input;
using System.Windows.Threading;
using EveDeck.Models.Intel;
using EveDeck.Services;
using EveDeck.Services.Intel;
using EveDeck.Services.Intel.Wire;
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
    private IntelHttpServer? _intelServer;
    private Universe? _intelUniverse;
    private IntelOverlayWindow? _intelOverlayWindow;
    private readonly Dictionary<int, DateTimeOffset> _intelLastToastBySystem = [];
    private readonly DispatcherTimer _intelOverlayRefreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _intelStarting;

    // Pilot name -> character id for every pilot already announced to LAN clients. The web page only
    // links a name to zKillboard once it holds the id, so this is what makes pilot names clickable.
    private readonly ConcurrentDictionary<string, long> _intelServedCharacters = new(StringComparer.OrdinalIgnoreCase);

    public ICommand RefreshIntelOptionsCommand { get; private set; } = null!;

    private void InitIntel()
    {
        RefreshIntelOptionsCommand = new RelayCommand(_ => RefreshIntelOptions());
        _intelOverlayRefreshTimer.Tick += (_, _) => RefreshIntelOverlay();

        // A second subscriber to the same event the chat-alert path uses. Locations come from Local,
        // which ChatLogWatcherService already tails; the intel tailer deliberately does not duplicate
        // that work.
        _chatLogWatcherService.SystemChanged += (character, system) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => _intelFeed?.UpdateLocation(character, system));
        };

        _settings.IntelChannels.CollectionChanged += OnIntelChannelsChanged;
        _settings.IntelHiddenChannels.CollectionChanged += OnIntelHiddenChannelsChanged;
        _settings.IntelFollowedCharacters.CollectionChanged += OnIntelFollowedChanged;

        RefreshIntelOptions();
        if (IntelFeedEnabled) StartIntel();
    }

    /// <summary>
    /// The feed (chatlog tailing + parsing) runs when either consumer wants it: the overlay card or
    /// the LAN page. Either can be on without the other.
    /// </summary>
    public bool IntelFeedEnabled => _settings.IntelOverlayEnabled || _settings.IntelServerEnabled;

    public bool IntelOverlayEnabled
    {
        get => _settings.IntelOverlayEnabled;
        set
        {
            if (_settings.IntelOverlayEnabled == value) return;
            _settings.IntelOverlayEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IntelFeedEnabled));
            Save();
            if (value)
            {
                StartIntel();
                RefreshIntelOverlay(); // feed may already be running for the LAN page
            }
            else if (IntelFeedEnabled) HideIntelOverlay();
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

    public int IntelFilterMaxJumps
    {
        get => _settings.IntelFilterMaxJumps;
        set
        {
            var v = Math.Clamp(value, 0, 50);
            if (_settings.IntelFilterMaxJumps == v) return;
            _settings.IntelFilterMaxJumps = v;
            OnPropertyChanged();
            Save();
            RefreshIntelFilteredViews();
        }
    }

    public bool IntelFilterHideUnknownRange
    {
        get => _settings.IntelFilterHideUnknownRange;
        set
        {
            if (_settings.IntelFilterHideUnknownRange == value) return;
            _settings.IntelFilterHideUnknownRange = value;
            OnPropertyChanged();
            Save();
            RefreshIntelFilteredViews();
        }
    }

    public bool IntelFilterHideClearStatus
    {
        get => _settings.IntelFilterHideClearStatus;
        set
        {
            if (_settings.IntelFilterHideClearStatus == value) return;
            _settings.IntelFilterHideClearStatus = value;
            OnPropertyChanged();
            Save();
            RefreshIntelFilteredViews();
        }
    }

    public int IntelFilterMaxAgeMinutes
    {
        get => _settings.IntelFilterMaxAgeMinutes;
        set
        {
            var v = Math.Clamp(value, 0, 240);
            if (_settings.IntelFilterMaxAgeMinutes == v) return;
            _settings.IntelFilterMaxAgeMinutes = v;
            OnPropertyChanged();
            Save();
            RefreshIntelFilteredViews();
        }
    }

    public bool IntelAlertSoundEnabled
    {
        get => _settings.IntelAlertSoundEnabled;
        set
        {
            if (_settings.IntelAlertSoundEnabled == value) return;
            _settings.IntelAlertSoundEnabled = value;
            OnPropertyChanged();
            Save();
        }
    }

    public string IntelAlertSoundName
    {
        get => _settings.IntelAlertSoundName;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? "Exclamation" : value.Trim();
            if (_settings.IntelAlertSoundName == v) return;
            _settings.IntelAlertSoundName = v;
            OnPropertyChanged();
            Save();
        }
    }

    public IReadOnlyList<string> IntelAlertSoundOptions { get; } =
        ["Exclamation", "Asterisk", "Beep", "Hand", "Question"];

    public bool IntelAlertsMuted => IntelAlertMute.IsMuted(_settings.IntelAlertsMutedUntil, DateTimeOffset.Now);

    public DateTimeOffset? IntelAlertsMutedUntil => _settings.IntelAlertsMutedUntil;

    public void MuteIntelAlerts(TimeSpan? duration)
    {
        _settings.IntelAlertsMutedUntil = duration is null
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.Now.Add(duration.Value);
        OnPropertyChanged(nameof(IntelAlertsMuted));
        OnPropertyChanged(nameof(IntelAlertsMutedUntil));
        Save();
        RefreshIntelOverlay();
    }

    public void UnmuteIntelAlerts()
    {
        if (_settings.IntelAlertsMutedUntil is null) return;
        _settings.IntelAlertsMutedUntil = null;
        OnPropertyChanged(nameof(IntelAlertsMuted));
        OnPropertyChanged(nameof(IntelAlertsMutedUntil));
        Save();
        RefreshIntelOverlay();
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
            OnPropertyChanged(nameof(IntelOverlayOpacityPercent));
            Save();
            RecreateIntelOverlay();
        }
    }

    /// <summary>Same value as <see cref="IntelOverlayOpacity"/>, as a 10-100 int for the Options slider.</summary>
    public int IntelOverlayOpacityPercent
    {
        get => (int)Math.Round(_settings.IntelOverlayOpacity * 100.0);
        set => IntelOverlayOpacity = value / 100.0;
    }

    /// <summary>
    /// Locked stops the card being dragged; pilot links stay clickable. Applied to the live window
    /// rather than recreating it, so locking never loses the feed that is already on screen.
    /// </summary>
    public bool IntelOverlayLocked
    {
        get => _settings.IntelOverlayLocked;
        set
        {
            if (_settings.IntelOverlayLocked == value) return;
            _settings.IntelOverlayLocked = value;
            OnPropertyChanged();
            Save();
            _intelOverlayWindow?.ApplyLock(value);
        }
    }

    private void OnIntelOverlayMoved(int x, int y)
    {
        _settings.IntelOverlayX = x;
        _settings.IntelOverlayY = y;
        Save();
    }

    /// <summary>
    /// Serves the daemon's endpoints from EveDeck so the tablet app and browser UI can point here
    /// instead of a second process. Opt-in: it opens an unauthenticated LAN socket, same as the
    /// daemon.
    /// </summary>
    public bool IntelServerEnabled
    {
        get => _settings.IntelServerEnabled;
        set
        {
            if (_settings.IntelServerEnabled == value) return;
            _settings.IntelServerEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IntelFeedEnabled));
            Save();
            if (value)
            {
                StartIntel();       // no-op if the overlay already started it
                StartIntelServer(); // no-op until the feed exists; StartIntel brings it up then
            }
            else
            {
                StopIntelServer();
                if (!IntelFeedEnabled) StopIntel();
            }
        }
    }

    public int IntelServerPort
    {
        get => _settings.IntelServerPort;
        set
        {
            var v = Math.Clamp(value, 1024, 65535);
            if (_settings.IntelServerPort == v) return;
            _settings.IntelServerPort = v;
            OnPropertyChanged();
            Save();

            if (!_settings.IntelServerEnabled) return;
            StopIntelServer();
            StartIntelServer();
        }
    }

    public bool IntelServerRequireLogin
    {
        get => _settings.IntelServerRequireLogin;
        set
        {
            if (_settings.IntelServerRequireLogin == value) return;
            _settings.IntelServerRequireLogin = value;
            OnPropertyChanged();
            Save();

            if (!_settings.IntelServerEnabled) return;
            StopIntelServer();
            StartIntelServer();
        }
    }

    private string _intelServerStatus = "Not running.";

    /// <summary>What the server is actually doing, shown in Options so a port clash is never silent.</summary>
    public string IntelServerStatus
    {
        get => _intelServerStatus;
        private set
        {
            if (_intelServerStatus == value) return;
            _intelServerStatus = value;
            OnPropertyChanged();
        }
    }

    private void StartIntelServer()
    {
        if (_intelServer is not null) return;

        // The server answers with the feed's own state, so it cannot run before the feed exists.
        if (_intelFeed is null)
        {
            IntelServerStatus = "Starting the intel feed...";
            return;
        }

        var cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EveDeck", "cache", "intel-images");

        var server = new IntelHttpServer(
            _settings.IntelServerPort,
            cacheFolder,
            BuildIntelSnapshot,
            _settings.IntelServerRequireLogin,
            AllowedIntelLoginCharacterIds,
            OnClientSetChannels,
            OnClientSetDisplay,
            msg => Log.Info(msg));

        if (!server.TryStart(out var error))
        {
            IntelServerStatus = error ?? "Could not start.";
            Log.Warn($"Intel server did not start: {error}");
            Status = IntelServerStatus;
            return;
        }

        _intelServer = server;
        var loginStatus = _settings.IntelServerRequireLogin && AllowedIntelLoginCharacterIds().Count == 0
            ? " No ESI-linked characters -- link one in EveDeck first."
            : "";
        IntelServerStatus = $"Listening on port {_settings.IntelServerPort}. Open http://<this-pc>:{_settings.IntelServerPort}/ on your tablet or phone.{loginStatus}";

        PortraitCacheService.Instance.Changed += OnPortraitCacheChangedForIntel;
        PublishIntelCharacters(IntelHistoryPlayers());
    }

    // Called from the server's network threads, but Assignments is a UI-thread collection: read it
    // there, or a seat edit mid-enumeration throws (the LogService cross-thread bug's shape).
    private IReadOnlySet<long> AllowedIntelLoginCharacterIds()
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess()
            ? ReadAllowedIntelLoginCharacterIds()
            : dispatcher.Invoke(ReadAllowedIntelLoginCharacterIds);
    }

    private IReadOnlySet<long> ReadAllowedIntelLoginCharacterIds() =>
        Assignments
            .SelectMany(a => a.EsiCharacters)
            .Select(c => c.CharacterId)
            .Where(id => id > 0)
            .ToHashSet();

    private void StopIntelServer()
    {
        var server = _intelServer;
        _intelServer = null;
        if (server is null) return;

        PortraitCacheService.Instance.Changed -= OnPortraitCacheChangedForIntel;
        _intelServedCharacters.Clear();

        _ = server.DisposeAsync();
        IntelServerStatus = "Not running.";
    }

    private WireServerMessage.Snapshot BuildIntelSnapshot()
    {
        var feed = _intelFeed;
        var history = feed?.History ?? [];
        var discovered = IntelChannelDiscovery.Discover();
        var now = DateTimeOffset.UtcNow;

        return new WireServerMessage.Snapshot
        {
            Messages = FilterIntelEntries(history, now).Select(e => e.Message.ToWire()).ToList(),
            Locations = (feed?.Locations ?? []).Select(l => l.ToWire()).ToList(),
            ScopeRegionIds = feed?.ScopeRegionIds ?? [],
            ServerTimeMillis = now.ToUnixTimeMilliseconds(),
            Channels = new WireChannels(
                discovered
                    .Select(c => new WireChannelInfo(c.Name, c.FileCount, c.LastActivityMillis, c.Reserved))
                    .ToList(),
                _settings.IntelChannels.ToList()),
            DisplayValue = new WireDisplaySettings(),

            // Ids only: corp/alliance resolution was not ported, so clients get links and portraits
            // but no tickers. See the note on IntelServerEnabled in AppSettings.
            CharactersValue = _intelServedCharacters
                .Select(kv => new WireCharacterInfo(kv.Key, kv.Value))
                .ToList(),
        };
    }

    private IntelFeedFilterSettings CurrentIntelFilterSettings() => new(
        _settings.IntelFilterMaxJumps,
        _settings.IntelFilterHideUnknownRange,
        _settings.IntelFilterHideClearStatus,
        _settings.IntelFilterMaxAgeMinutes,
        _settings.IntelHiddenChannels.ToHashSet(StringComparer.OrdinalIgnoreCase));

    private IReadOnlyList<IntelFeedEntry> FilterIntelEntries(IReadOnlyList<IntelFeedEntry> entries, DateTimeOffset now)
    {
        var filter = CurrentIntelFilterSettings();
        return entries.Where(e => IntelFeedFilter.ShouldShow(e, filter, now)).ToList();
    }

    private bool IntelEntryPassesFilters(IntelFeedEntry entry, DateTimeOffset now) =>
        IntelFeedFilter.ShouldShow(entry, CurrentIntelFilterSettings(), now);

    private void RefreshIntelFilteredViews()
    {
        RefreshIntelOverlay();
        if (_intelServer is not null) _ = _intelServer.BroadcastAsync(BuildIntelSnapshot());
    }

    private IEnumerable<string> IntelHistoryPlayers() =>
        (_intelFeed?.History ?? []).SelectMany(e => e.Message.Players);

    // UI thread: raised whenever a pending name lookup lands, so retry anything still unresolved.
    private void OnPortraitCacheChangedForIntel() => PublishIntelCharacters(IntelHistoryPlayers());

    /// <summary>
    /// Resolves pilot names through the shared portrait cache (the same lookup the overlay's links use)
    /// and pushes any newly known ids to connected clients. An unresolved name starts an ESI lookup;
    /// <see cref="OnPortraitCacheChangedForIntel"/> brings it back here once it lands. UI thread only.
    /// </summary>
    private void PublishIntelCharacters(IEnumerable<string> names)
    {
        var server = _intelServer;
        if (server is null) return;

        var fresh = new List<WireCharacterInfo>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_intelServedCharacters.ContainsKey(name)) continue;

            var id = PortraitCacheService.Instance.ForName(name)?.CharacterId ?? 0;
            if (id <= 0) continue;

            _intelServedCharacters[name] = id;
            fresh.Add(new WireCharacterInfo(name, id));
        }

        if (fresh.Count > 0)
            _ = server.BroadcastAsync(new WireServerMessage.Characters { CharactersValue = fresh });
    }

    /// <summary>A client picked a different channel set; treat it as authoritative and persist it.</summary>
    private void OnClientSetChannels(IReadOnlyList<string> channels)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var wanted = channels
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .ToList();

            if (_settings.IntelChannels.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase)) return;

            _settings.IntelChannels.Clear();
            foreach (var channel in wanted) _settings.IntelChannels.Add(channel);

            RefreshIntelOptions();
            _ = BroadcastChannelsAsync();
        });
    }

    private void OnClientSetDisplay(WireDisplaySettings settings) =>
        _ = _intelServer?.BroadcastAsync(new WireServerMessage.Display { Settings = settings });

    private async Task BroadcastChannelsAsync()
    {
        var server = _intelServer;
        if (server is null) return;

        var discovered = IntelChannelDiscovery.Discover();
        await server.BroadcastAsync(new WireServerMessage.ChannelsMessage
        {
            Available = discovered
                .Select(c => new WireChannelInfo(c.Name, c.FileCount, c.LastActivityMillis, c.Reserved))
                .ToList(),
            Selected = _settings.IntelChannels.ToList(),
        });
    }

    public ObservableCollection<string> IntelChannels => _settings.IntelChannels;

    public ObservableCollection<string> IntelFollowedCharacters => _settings.IntelFollowedCharacters;

    /// <summary>
    /// Channel names the settings UI can offer, newest activity first. Reserved channels (Local) are
    /// dropped here but still reported to connected clients, which show them greyed out.
    /// </summary>
    public IReadOnlyList<string> DiscoverIntelChannels()
    {
        try
        {
            return IntelChannelDiscovery.Discover()
                .Where(c => !c.Reserved)
                .Select(c => c.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not enumerate intel channels: {ex}");
            return [];
        }
    }

    /// <summary>Discovered channels as tickable rows, reflecting what is already saved.</summary>
    public ObservableCollection<IntelSelectableName> IntelChannelOptions { get; } = [];

    /// <summary>Watched channels as visible/hidden rows; hidden channels are still tailed for range.</summary>
    public ObservableCollection<IntelSelectableName> IntelChannelVisibilityOptions { get; } = [];

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

        IntelChannelVisibilityOptions.Clear();
        foreach (var channel in _settings.IntelChannels.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            IntelChannelVisibilityOptions.Add(new IntelSelectableName(
                channel,
                !_settings.IntelHiddenChannels.Contains(channel, StringComparer.OrdinalIgnoreCase),
                ToggleIntelChannelVisibility));
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

    private void ToggleIntelChannelVisibility(IntelSelectableName item)
    {
        var existing = _settings.IntelHiddenChannels.FirstOrDefault(v => string.Equals(v, item.Name, StringComparison.OrdinalIgnoreCase));

        if (item.IsSelected)
        {
            if (existing is not null) _settings.IntelHiddenChannels.Remove(existing);
        }
        else if (existing is null)
        {
            _settings.IntelHiddenChannels.Add(item.Name);
        }
    }

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
        RefreshIntelOptions();
        RefreshIntelFilteredViews();
        Save();
    }

    private void OnIntelHiddenChannelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshIntelFilteredViews();
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

                    if (!IntelFeedEnabled) return;

                    _intelUniverse = universe;
                    _intelTailer = new IntelLogTailer();
                    _intelTailer.ErrorOccurred += msg => Log.Warn(msg);

                    _intelFeed = new IntelFeedService(universe, _intelTailer);
                    _intelFeed.EntryAdded += OnIntelEntryAdded;
                    _intelFeed.LocationUpdated += OnIntelLocationUpdated;
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
                    Log.Info($"Intel feed started ({_settings.IntelChannels.Count} channel(s); overlay {(_settings.IntelOverlayEnabled ? "on" : "off")}, LAN page {(_settings.IntelServerEnabled ? "on" : "off")}).");

                    // The server serves the feed's state, so it can only come up once the feed has.
                    if (_settings.IntelServerEnabled) StartIntelServer();
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
        StopIntelServer();

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
            PublishIntelCharacters(entry.Message.Players);
            if (IntelEntryPassesFilters(entry, DateTimeOffset.UtcNow))
                _ = _intelServer?.BroadcastAsync(new WireServerMessage.Intel { Message = entry.Message.ToWire() });
        });
    }

    private void OnIntelLocationUpdated(CharacterLocation location) =>
        _ = _intelServer?.BroadcastAsync(new WireServerMessage.Location { LocationValue = location.ToWire() });

    private void MaybeToastIntel(IntelFeedEntry entry)
    {
        // The toast range lives in the overlay's settings, so it follows the overlay switch.
        if (!_settings.IntelOverlayEnabled || !entry.IsHostile) return;

        var threshold = _settings.IntelHostileToastJumps;
        if (threshold < 0) return;

        // No known range is not "in range". Staying silent here is deliberate: the overlay already
        // states that range is unavailable, and guessing would alert on the whole of New Eden.
        if (entry.JumpsAway is not { } jumps || jumps > threshold) return;

        var systemId = entry.Message.SystemIds.FirstOrDefault();
        var now = DateTimeOffset.UtcNow;
        if (_intelLastToastBySystem.TryGetValue(systemId, out var last) && now - last < IntelToastCooldown) return;
        if (IntelAlertMute.IsMuted(_settings.IntelAlertsMutedUntil, DateTimeOffset.Now)) return;
        _intelLastToastBySystem[systemId] = now;

        var where = jumps == 0 ? "in system" : $"{jumps} jump{(jumps == 1 ? "" : "s")} out";
        var who = entry.NearestCharacter is null ? "" : $" from {entry.NearestCharacter}";
        if (_settings.IntelAlertSoundEnabled) PlayIntelAlertSound();
        ShowToast($"Hostile intel — {where}{who}", entry.Message.Raw, "#F87171");
    }

    private void PlayIntelAlertSound()
    {
        switch (_settings.IntelAlertSoundName)
        {
            case "Asterisk":
                SystemSounds.Asterisk.Play();
                break;
            case "Beep":
                SystemSounds.Beep.Play();
                break;
            case "Hand":
                SystemSounds.Hand.Play();
                break;
            case "Question":
                SystemSounds.Question.Play();
                break;
            default:
                SystemSounds.Exclamation.Play();
                break;
        }
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
            _intelOverlayWindow = new IntelOverlayWindow(
                _settings.IntelOverlayX,
                _settings.IntelOverlayY,
                _settings.IntelOverlayLocked,
                _settings.IntelOverlayFontSize,
                _settings.IntelOverlayOpacity,
                OnIntelOverlayMoved,
                MuteIntelAlerts,
                UnmuteIntelAlerts,
                () => IntelAlertMute.IsMuted(_settings.IntelAlertsMutedUntil, DateTimeOffset.Now));
            _intelOverlayWindow.Show();
            _intelOverlayWindow.SetZ(ResolveForegroundOwner() == ForegroundOwner.Eve);
        }

        if (!_intelOverlayRefreshTimer.IsEnabled) _intelOverlayRefreshTimer.Start();

        _intelOverlayWindow.Update(
            FilterIntelEntries(_intelFeed.History, DateTimeOffset.UtcNow),
            _intelFeed.OriginStatus,
            _settings.IntelOverlayMaxRows,
            IntelAlertStatusText());
    }

    private string? IntelAlertStatusText()
    {
        var mutedUntil = _settings.IntelAlertsMutedUntil;
        if (!IntelAlertMute.IsMuted(mutedUntil, DateTimeOffset.Now)) return null;
        var until = mutedUntil.GetValueOrDefault();
        return until == DateTimeOffset.MaxValue
            ? "Alerts muted"
            : $"Alerts muted until {until:HH:mm}";
    }

    private void RecreateIntelOverlay()
    {
        HideIntelOverlay();
        if (_settings.IntelOverlayEnabled) RefreshIntelOverlay();
    }

    private void HideIntelOverlay()
    {
        _intelOverlayRefreshTimer.Stop();
        if (_intelOverlayWindow is null) return;
        try { _intelOverlayWindow.Close(); } catch { /* window may already be gone */ }
        _intelOverlayWindow = null;
    }
}

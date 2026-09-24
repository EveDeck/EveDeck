using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using MessageBox = System.Windows.MessageBox;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.Utilities;
using EveDeck.Views;
using Microsoft.Win32;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ConfigService _configService = new();
    private readonly Win32WindowService _windowService = new();
    private readonly AppSettings _settings;
    private readonly ClientLaunchService _clientLaunchService = new();
    private readonly ChatLogWatcherService _chatLogWatcherService = new();
    private readonly GameLogWatcherService _gameLogWatcherService = new();
    private CancellationTokenSource? _launchGroupCts;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _hoverPeekTimer = new() { IsEnabled = false };
    // Debounce auto-apply so all clients have time to launch and settle before we move them.
    // Must stay LONGER than _refreshTimer's interval (5s): new-client detection only runs once per
    // refresh poll, so a debounce shorter than the poll interval can fire before the next poll has
    // a chance to see a straggler that logged in a couple of seconds late -- exactly what happens
    // during a mass multi-account login, where the auto-apply then only parks/sizes whichever
    // subset had appeared by that premature pass, leaving later clients unparked until a manual
    // reapply catches everyone in one clean pass.
    private readonly DispatcherTimer _autoApplyTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    // Re-checks linked characters' on-disk portraits against the cache TTL hourly, on top of the
    // per-character check PortraitCacheService.ForId already does whenever a surface asks for one.
    private readonly DispatcherTimer _portraitSweepTimer = new() { Interval = TimeSpan.FromHours(1) };

    private readonly Dictionary<int, nint> _lastFocusedHandle = new();

    // Window handles of assigned EVE clients seen at the previous refresh, used to detect
    // newly-launched clients so the active profile can be auto-applied. Keyed by HWND rather than
    // title because EVE transiently drops the " - Character" suffix while the in-game ESC menu is
    // open, which a title-keyed baseline read as a client disappearing and relaunching.
    private readonly HashSet<nint> _knownAssignedClientHandles = new();

    // Signature (window handles + monitor count) of the last detection result actually written to the
    // log, so Refresh only logs a line when something really changed. See the use site in Refresh.
    private string _lastLoggedDetection = "";
    private bool _clientBaselineInitialized;

    // 1a — Session-level style snapshots keyed by HWND (not persisted, resets on restart).
    private readonly Dictionary<nint, StyleSnapshot> _sessionSnapshots = new();

    // 1b — Last known frame handle/rect to skip redundant overlay repositions.
    private nint _lastFrameHandle;
    private WindowRect? _lastFrameRect;
    private string _lastFrameStyle = "";
    private bool _lastFrameGlowEnabled = true;

    // 1c — Guard against re-entrant profile apply.
    private bool _applyInProgress;

    // 2d — Profile search filter text.
    private string _profileSearchText = "";

    // 2h — Log level filter.
    private string _logFilterLevel = "All";

    // 3b — Window rects captured just before the last profile apply (for undo).
    private Dictionary<string, WindowRect>? _undoRects;

    private bool _configResetBannerDismissed;
    private IReadOnlyList<string> _hotkeyConflicts = Array.Empty<string>();
    private bool _hotkeyConflictBannerDismissed;
    private ActiveFrameOverlay? _frameOverlay;
    private Brush _frameBrush = Brushes.Orange;
    private EveWindowInfo? _selectedWindow;
    private SlotAssignment? _selectedAssignment;
    private LayoutProfile? _selectedProfile;
    private HotkeyBinding? _selectedHotkey;
    private HotkeyBinding? _capturingHotkey;
    private string _status = "Ready.";
    private string _lastUpdatedText = "Not refreshed yet";

    public MainWindowViewModel()
    {
        _settings = _configService.Load();
        Log = new LogService(_configService.LogsFolder);
        // Surface overlay diagnostics (failed thumbnail registrations etc.) in the Logs tab.
        Views.TileSurfaceWindow.Log = msg => Log.Warn(msg);
        Win32WindowService.LogWarn = msg => Log.Warn(msg);
        Assignments = _settings.Assignments;
        Profiles = _settings.Profiles;
        Hotkeys = _settings.Hotkeys;
        Logs = Log.Entries;

        // Migrate the former global master seat onto each profile (masters are now per-profile). Profiles
        // that have never stored one inherit the old global value; once a profile records its own (>0) it
        // keeps that choice across launches.
        foreach (var p in Profiles)
            if (p.MasterSeat == 0) p.MasterSeat = _settings.MasterSlotNumber;

        // ── Commands ──────────────────────────────────────────────
        RefreshCommand = new RelayCommand(Refresh);
        RefreshPortraitsCommand = new RelayCommand(() =>
        {
            PortraitCacheService.Instance.RefreshAll();
            Log.Info("Refreshing character portraits from the image server.");
        });
        AssignSelectedCommand = new RelayCommand(AssignSelected, () => SelectedWindow is not null && SelectedAssignment is not null);
        AssignWindowToSlotCommand = new RelayCommand(AssignWindowToSlot, _ => SelectedWindow is not null);
        RemoveWindowFromSlotCommand = new RelayCommand(RemoveWindowFromSlot);
        ClearAssignmentCommand = new RelayCommand(() =>
        {
            if (SelectedAssignment is null) return;
            ClearAssignment(SelectedAssignment);
        });
        ClearSlotCommand = new RelayCommand(parameter =>
        {
            if (parameter is SlotAssignment assignment) ClearAssignment(assignment);
        });
        FocusSlotCommand = new RelayCommand(parameter =>
        {
            if (parameter is SlotAssignment assignment) FocusSlot(assignment.SlotNumber);
        });
        AddSlotCommand = new RelayCommand(AddSlot);
        DeleteSelectedSlotCommand = new RelayCommand(DeleteSelectedSlot, () => SelectedAssignment is not null && Assignments.Count > 1);
        AutoAssignAllCommand = new RelayCommand(AutoAssignAll);  // 2e
        ApplyProfileCommand = new RelayCommand(() => ApplyActiveProfile());
        CaptureProfileCommand = new RelayCommand(CaptureAssignedWindows);
        UndoLastApplyCommand = new RelayCommand(UndoLastApply, () => _undoRects is not null && !_applyInProgress);  // 3b
        SaveCommand = new RelayCommand(Save);
        RestoreSelectedStyleCommand = new RelayCommand(RestoreSelectedStyle, () => SelectedWindow is not null);
        ToggleSelectedBorderlessCommand = new RelayCommand(ToggleSelectedBorderless, () => SelectedWindow is not null);
        NewProfileCommand = new RelayCommand(NewProfile);
        EditLayoutCommand = new RelayCommand(EditLayoutOnMonitor);
        RenameProfileCommand = new RelayCommand(RenameProfile, () => SelectedProfile is not null && !SelectedProfile.IsBuiltIn);
        DuplicateProfileCommand = new RelayCommand(DuplicateProfile, () => SelectedProfile is not null);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedProfile is not null && !SelectedProfile.IsBuiltIn && Profiles.Count > 1);
        ImportProfileCommand = new RelayCommand(ImportProfile);
        ExportProfileCommand = new RelayCommand(ExportProfile, () => SelectedProfile is not null);
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
        CaptureHotkeyCommand = new RelayCommand(BeginHotkeyCapture, parameter => parameter is HotkeyBinding || SelectedHotkey is not null);
        ClearHotkeyCommand = new RelayCommand(ClearHotkey, parameter => parameter is HotkeyBinding || SelectedHotkey is not null);
        ResetHotkeysCommand = new RelayCommand(ResetHotkeysToDefaults);  // 2c
        SetMasterSlotCommand = new RelayCommand(parameter => SetMasterSlot(parameter, sortToTop: true));
        AddEsiCharacterCommand = new RelayCommand(AddEsiCharacter);
        // No CanExecute guard: RelayCommand doesn't requery, so a stale "disabled" would stick once the
        // main changes. The seat card hides the button on the current main and SetMainCharacter no-ops.
        SetMainCharacterCommand = new RelayCommand(SetMainCharacter);
        RemoveEsiCharacterCommand = new RelayCommand(RemoveEsiCharacter);
        ReauthEsiCharacterCommand = new RelayCommand(ReauthEsiCharacter);
        RestoreBackupCommand = new RelayCommand(() => RestoreSelectedBackup(), () => SelectedBackup is not null);
        DismissUpdateBannerCommand = new RelayCommand(() => { _updateBannerDismissed = true; OnPropertyChanged(nameof(ShowUpdateBanner)); });
        DismissConfigResetBannerCommand = new RelayCommand(() => { _configResetBannerDismissed = true; OnPropertyChanged(nameof(ShowConfigResetBanner)); });
        DismissHotkeyConflictBannerCommand = new RelayCommand(() => { _hotkeyConflictBannerDismissed = true; OnPropertyChanged(nameof(ShowHotkeyConflictBanner)); });
        InstallUpdateCommand = new RelayCommand(() => _ = InstallUpdateAsync());
        CheckForUpdateCommand = new RelayCommand(() => _ = CheckForUpdateAsync(manual: true));
        SpawnTestWindowsCommand = new RelayCommand(SpawnTestWindows);
        AutoSelectBestProfileCommand = new RelayCommand(AutoSelectBestProfile);
        SetMasterResolutionCommand = new RelayCommand(ExecuteSetMasterResolution, () => SelectedMasterResolution is not null && SelectedProfile is not null);
        ClearMasterResolutionCommand = new RelayCommand(ExecuteClearMasterResolution, () => SelectedProfile is not null && SelectedProfile.MasterResolutionWidth > 0);
        ToggleTopmostCommand = new RelayCommand(parameter =>
        {
            if (parameter is not SlotAssignment seat) return;
            // IsTopmost is already updated by the TwoWay binding before this command fires.
            if (seat.IsTopmost)
            {
                // Pinned: raise now only if EVE is currently focused; the foreground hook keeps it in sync.
                var eveForeground = IsEveWindowForeground();
                foreach (var w in FindAssignedWindows(seat))
                    _windowService.SetWindowTopmost(w.Handle, eveForeground);
            }
            else
            {
                // Un-pinned: drop to normal z-order immediately, regardless of what's focused.
                foreach (var w in FindAssignedWindows(seat))
                    _windowService.SetWindowTopmost(w.Handle, false);
            }
            _lastEveForeground = null; // pinned set changed; re-evaluate on the next foreground change
            Save();
            Log.Info($"Seat {seat.SlotNumber} ({seat.Label}) above-while-EVE-focused: {seat.IsTopmost}.");
        });

        SwitchCharacterSetCommand = new RelayCommand(parameter =>
        {
            if (parameter is Models.CharacterSet set)
                SwitchToCharacterSet(set.Id);
        });

        AddCharacterSetCommand = new RelayCommand(_ => AddCharacterSet());

        DeleteCharacterSetCommand = new RelayCommand(parameter =>
        {
            var set = parameter as Models.CharacterSet ?? ActiveCharacterSet;
            if (set is not null) DeleteCharacterSet(set);
        }, _ => _settings.CharacterSets.Count > 1);

        LaunchGroupCommand = new RelayCommand(LaunchGroup, parameter => parameter is Models.CharacterSet);
        AddGameEventRuleCommand = new RelayCommand(AddGameEventRule);
        RemoveGameEventRuleCommand = new RelayCommand(RemoveGameEventRule, parameter => parameter is GameEventRule);
        AddOverlayAllowedAppCommand = new RelayCommand(AddOverlayAllowedApp);
        RemoveOverlayAllowedAppCommand = new RelayCommand(RemoveOverlayAllowedApp, parameter => parameter is Models.OverlayAllowedApp);
        AddPreviewableAppCommand = new RelayCommand(AddPreviewableApp);
        RemovePreviewableAppCommand = new RelayCommand(RemovePreviewableApp, parameter => parameter is Models.PreviewableApp);

        // ── Views ─────────────────────────────────────────────────
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfileId) ?? Profiles.FirstOrDefault();
        // Covers the no-profiles case, where the setter above short-circuits and never refreshes.
        RefreshCharacterSetLayoutNames();

        ProfilesView = CollectionViewSource.GetDefaultView(Profiles);
        ProfilesView.SortDescriptions.Add(new SortDescription(nameof(LayoutProfile.GroupOrder), ListSortDirection.Ascending));
        ProfilesView.SortDescriptions.Add(new SortDescription(nameof(LayoutProfile.Name), ListSortDirection.Ascending));
        ProfilesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LayoutProfile.Category)));

        WindowsView = new CollectionViewSource { Source = Windows }.View;
        WindowsView.Filter = o => o is EveWindowInfo w && !IsWindowAssigned(w);

        // Seat list view for the Clients tab. Its own view instance (not the default view of
        // Assignments) so the mini-map and everything else keep seeing the raw manual order.
        AssignmentsView = new ListCollectionView(Assignments) { CustomSort = new MasterFirstSeatComparer(this) };

        LogsView = CollectionViewSource.GetDefaultView(Logs);  // 2h

        // ── Timers ────────────────────────────────────────────────
        _refreshTimer.Tick += (_, _) => Refresh();
        _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); Save(); };
        _autoApplyTimer.Tick += (_, _) =>
        {
            _autoApplyTimer.Stop();
            if (_applyInProgress) return;
            Log.Info("Detected newly-launched EVE client(s); re-applying active profile automatically.");
            ApplyActiveProfile();
        };
        if (_settings.AutoRefresh) _refreshTimer.Start();

        SyncMasterSlot();
        UpdatePositionCodes();
        RaiseIdentityDependents();
        SubscribeToAssignmentChanges();
        SubscribeToHotkeyChanges();

        // A portrait finishing its download, or a running character's name resolving to an id, can
        // change what RunningPortrait/EsiCharacter.Portrait resolve to for surfaces that aren't
        // data-bound to the shared CharacterPortrait directly (the corner-overlay label window).
        PortraitCacheService.Instance.Changed += OnPortraitCacheChanged;
        PortraitCacheService.Instance.Warm(Assignments.SelectMany(a => a.EsiCharacters).Select(c => c.CharacterId));
        _portraitSweepTimer.Tick += (_, _) =>
            PortraitCacheService.Instance.Warm(Assignments.SelectMany(a => a.EsiCharacters).Select(c => c.CharacterId));
        _portraitSweepTimer.Start();

        _frameBrush = ParseFrameBrush(_settings.ActiveFrameColor);
        _frameTimer.Tick += OnFrameTick;
        _jumpStatusTimer.Tick += OnJumpStatusTick;
        _jumpDisplayTimer.Tick += OnJumpDisplayTick;
        _seatHealthTimer.Tick += OnSeatHealthTick;
        if (_settings.SeatHealthAlertPodded || _settings.SeatHealthAlertDisconnected) _seatHealthTimer.Start();
        _hoverPeekTimer.Tick += OnHoverPeekTimerTick;
        if (_settings.ActiveFrameEnabled) StartFrameOverlay();

        Refresh();
        LoadMasterResolutions();

        // 2g — Apply startup profile if configured and windows are detected.
        if (_settings.ApplyProfileOnStartup && !string.IsNullOrWhiteSpace(_settings.StartupProfileId) && Windows.Count > 0)
        {
            var startupProfile = Profiles.FirstOrDefault(p => p.Id == _settings.StartupProfileId);
            if (startupProfile is not null)
            {
                SelectedProfile = startupProfile;
                ApplyActiveProfile();
                Log.Info($"Applied startup profile: {startupProfile.Name}.");

                // The Refresh() above already armed the auto-apply debounce timer, since it saw
                // these already-running clients as "newly appeared" on the first scan. We just
                // applied for them directly above, so cancel the pending re-apply to avoid doing
                // it twice.
                _autoApplyTimer.Stop();
            }
        }

        InitLaunchGroups();
        InitChatAlerts();
        InitConfigProfiles();
        InitCharacterRoster();
        InitDowntime();
        // After InitChatAlerts: the intel feed seeds its starting positions from the character/system
        // map that the chat-alert path populates while the chatlog watcher primes.
        InitIntel();

        // After InitConfigProfiles (which wires the commands) and after the startup LAYOUT profile
        // above: a config profile can select a different layout, so it must get the last word.
        ApplyStartupConfigProfile();

        Log.Info("EveDeck started.");
        _ = CheckForUpdateAsync();
        InitProfileCopy();
    }

    partial void InitProfileCopy();

    // ── Observables ────────────────────────────────────────────────────────────

    public LogService Log { get; }
    public ObservableCollection<EveWindowInfo> Windows { get; } = new();
    public ObservableCollection<MonitorInfo> Monitors { get; } = new();
    public ObservableCollection<SlotAssignment> Assignments { get; }
    public ObservableCollection<LayoutProfile> Profiles { get; }
    public ICollectionView ProfilesView { get; }
    public ICollectionView WindowsView { get; }

    // Display-only ordering of the seat list: the master seat is shown first, everything else keeps
    // the user's manual order underneath. Deliberately a VIEW and not a reorder of Assignments --
    // physically moving the master to index 0 reshuffled the manual seat order on every master change
    // and every centred tile (the "seat order won't stick" bug; see SetMasterSlot). Re-sorted only
    // when the user explicitly picks a master, so a hover-peek or tile click never shuffles the list.
    public ICollectionView AssignmentsView { get; }

    private sealed class MasterFirstSeatComparer : System.Collections.IComparer
    {
        private readonly MainWindowViewModel _viewModel;
        public MasterFirstSeatComparer(MainWindowViewModel viewModel) => _viewModel = viewModel;

        public int Compare(object? x, object? y)
        {
            if (x is not SlotAssignment a || y is not SlotAssignment b) return 0;
            var master = _viewModel.ActiveMasterSeat;
            var aIsMaster = a.SlotNumber == master;
            var bIsMaster = b.SlotNumber == master;
            if (aIsMaster != bIsMaster) return aIsMaster ? -1 : 1;
            // Otherwise fall back to the underlying manual order so drag-drop and Ctrl+Up/Down still read
            // exactly as the user arranged them.
            return _viewModel.Assignments.IndexOf(a).CompareTo(_viewModel.Assignments.IndexOf(b));
        }
    }
    public ObservableCollection<HotkeyBinding> Hotkeys { get; }
    public ObservableCollection<LogEntry> Logs { get; }
    public ICollectionView LogsView { get; }  // 2h
    public ObservableCollection<Models.CharacterSet> CharacterSets => _settings.CharacterSets;
    public ObservableCollection<LayoutSlotPreview> LayoutPreviewSlots { get; } = new();
    public ObservableCollection<MiniMapSlot> MiniMapSlots { get; } = new();
    public ObservableCollection<MonitorPreviewItem> MonitorPreviewItems { get; } = new();
    public event EventHandler? HotkeysChanged;
    public event Action? OptionsOpenRequested;

    // ── Commands ───────────────────────────────────────────────────────────────

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RefreshPortraitsCommand { get; }
    public RelayCommand AssignSelectedCommand { get; }
    public RelayCommand AssignWindowToSlotCommand { get; }
    public RelayCommand RemoveWindowFromSlotCommand { get; }
    public RelayCommand ClearAssignmentCommand { get; }
    public RelayCommand ClearSlotCommand { get; }
    public RelayCommand FocusSlotCommand { get; }
    public RelayCommand AddSlotCommand { get; }
    public RelayCommand DeleteSelectedSlotCommand { get; }
    public RelayCommand AutoAssignAllCommand { get; }      // 2e
    public RelayCommand ApplyProfileCommand { get; }
    public RelayCommand CaptureProfileCommand { get; }
    public RelayCommand UndoLastApplyCommand { get; }      // 3b
    public RelayCommand SaveCommand { get; }
    public RelayCommand RestoreSelectedStyleCommand { get; }
    public RelayCommand ToggleSelectedBorderlessCommand { get; }
    public RelayCommand NewProfileCommand { get; }
    public RelayCommand EditLayoutCommand { get; }
    public RelayCommand RenameProfileCommand { get; }
    public RelayCommand DuplicateProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand ImportProfileCommand { get; }
    public RelayCommand ExportProfileCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }
    public RelayCommand CaptureHotkeyCommand { get; }
    public RelayCommand ClearHotkeyCommand { get; }
    public RelayCommand ResetHotkeysCommand { get; }       // 2c
    public RelayCommand SetMasterSlotCommand { get; }
    public RelayCommand AddEsiCharacterCommand { get; }
    public RelayCommand SetMainCharacterCommand { get; }
    public RelayCommand RemoveEsiCharacterCommand { get; }
    public RelayCommand ReauthEsiCharacterCommand { get; }
    public RelayCommand RestoreBackupCommand { get; }
    public RelayCommand SpawnTestWindowsCommand { get; }
    public RelayCommand AutoSelectBestProfileCommand { get; }
    public RelayCommand SetMasterResolutionCommand { get; }
    public RelayCommand ClearMasterResolutionCommand { get; }
    public RelayCommand ToggleTopmostCommand { get; }
    public RelayCommand AddCharacterSetCommand { get; }
    public RelayCommand DeleteCharacterSetCommand { get; }
    public RelayCommand SwitchCharacterSetCommand { get; }
    public RelayCommand LaunchGroupCommand { get; }
    public RelayCommand AddGameEventRuleCommand { get; }
    public RelayCommand RemoveGameEventRuleCommand { get; }
    public RelayCommand AddOverlayAllowedAppCommand { get; }
    public RelayCommand RemoveOverlayAllowedAppCommand { get; }
    public RelayCommand AddPreviewableAppCommand { get; }
    public RelayCommand RemovePreviewableAppCommand { get; }

    // ── Read-only computed ─────────────────────────────────────────────────────

    public string VersionText
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "dev" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public bool ShowConfigResetBanner => _configService.WasResetFromCorruption && !_configResetBannerDismissed;
    public ICommand DismissConfigResetBannerCommand { get; }

    public bool ShowHotkeyConflictBanner => _hotkeyConflicts.Count > 0 && !_hotkeyConflictBannerDismissed;
    public string HotkeyConflictMessage => _hotkeyConflicts.Count == 1
        ? $"1 hotkey could not be registered: {_hotkeyConflicts[0]}"
        : $"{_hotkeyConflicts.Count} hotkeys could not be registered: {string.Join("; ", _hotkeyConflicts)}";
    public ICommand DismissHotkeyConflictBannerCommand { get; }

    // Called after every HotkeyService.RegisterAll so the banner reflects the current conflict set.
    public void SetHotkeyConflicts(IReadOnlyList<string> failures)
    {
        _hotkeyConflicts = failures;
        _hotkeyConflictBannerDismissed = false;
        OnPropertyChanged(nameof(ShowHotkeyConflictBanner));
        OnPropertyChanged(nameof(HotkeyConflictMessage));
    }

    public int WindowCount => Windows.Count;
    public int UnassignedWindowCount => Windows.Count(w => !IsWindowAssigned(w));
    public int MonitorCount => Monitors.Count;
    public bool HasNoWindows => WindowCount == 0;
    public bool AllWindowsAssigned => WindowCount > 0 && UnassignedWindowCount == 0;
    public string DetectionStateText => WindowCount > 0 ? "EVE detected" : "No EVE windows";
    public string DetectionStateColor => WindowCount > 0 ? "#22C55E" : "#F59E0B";

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        set => SetProperty(ref _lastUpdatedText, value);
    }

    public int MasterSlotNumber => ActiveMasterSeat;

    // Transient promotion set by EnsureValidMasterSeat when the persisted master seat's client isn't
    // running, so the master geometry slot gets filled by a logged-in seat during a partial session.
    // NEVER persisted — the user's chosen master (SelectedProfile.MasterSeat) is untouched, so it
    // snaps back automatically on the next apply once the real master's client is running again.
    private int? _promotedMasterSeat;

    // The master seat of the ACTIVE profile (per-profile so each activity can center a different main).
    // Falls back to the geometric center slot when the profile hasn't designated one (new/migrated).
    // A live transient promotion (partial session) wins over the persisted value for placement/labels.
    internal int ActiveMasterSeat
    {
        get
        {
            if (_promotedMasterSeat is int promoted) return promoted;
            if (SelectedProfile is null) return _settings.MasterSlotNumber;
            return SelectedProfile.MasterSeat > 0 ? SelectedProfile.MasterSeat : CenterSlotNumber;
        }
        set
        {
            _promotedMasterSeat = null;   // an explicit master change supersedes any transient promotion
            if (SelectedProfile is not null) SelectedProfile.MasterSeat = value;
            else _settings.MasterSlotNumber = value;
            OnPropertyChanged(nameof(MasterSlotNumber));
            OnPropertyChanged(nameof(MasterSeatSummary));
            RaiseIdentityDependents();
        }
    }

    // The master seat the user actually CONFIGURED, ignoring any transient promotion. ActiveMasterSeat
    // deliberately hides that distinction (a stand-in wins for placement/labels), so the readout below
    // needs this to tell "you chose this" apart from "this is filling in".
    internal int DesignatedMasterSeat
        => SelectedProfile is null
            ? _settings.MasterSlotNumber
            : SelectedProfile.MasterSeat > 0 ? SelectedProfile.MasterSeat : CenterSlotNumber;

    // One authoritative answer to "which seat is master, and why". Master can change from the mini-map
    // centre cell, a seat's Master button, or an automatic stand-in when the designated seat's client
    // isn't running -- and nothing in the UI previously reported which of those was in effect.
    public string MasterSeatSummary
    {
        get
        {
            var designated = DesignatedMasterSeat;
            if (_promotedMasterSeat is not int promoted || promoted == designated)
                return $"Master seat: {designated} - {SeatLabelFor(designated)}";

            return $"Master seat: {promoted} - {SeatLabelFor(promoted)}   (standing in - seat "
                 + $"{designated} - {SeatLabelFor(designated)} isn't logged in; not saved)";
        }
    }

    private string SeatLabelFor(int slotNumber)
        => Assignments.FirstOrDefault(a => a.SlotNumber == slotNumber)?.DisplayLabel ?? $"Slot {slotNumber}";

    // EVE will not run its client window below roughly this size; it clamps or ignores the resize.
    private const int MinUsableClientWidth = 1024;
    private const int MinUsableClientHeight = 768;

    // THE decision: does this profile render as live previews, or as real windows?
    //
    // Every gate that used to read _settings.CornerOverlaysEnabled directly now goes through here,
    // so the global toggle and the per-profile override can never disagree about which apply path
    // ran. A layout with no override behaves exactly as it did before this existed.
    internal bool PreviewModeFor(LayoutProfile? profile)
    {
        // No override can conjure previews out of a layout whose slots all sit at one position --
        // there is nothing to surround a centre rect with. That constraint is geometric, not a
        // preference, so it is checked first.
        if (profile is null || !profile.SupportsCornerGrid) return false;

        return profile.PreviewModeOverride switch
        {
            "Windows"  => false,
            "Previews" => true,
            _          => _settings.CornerOverlaysEnabled
        };
    }

    public bool PreviewModeActive => PreviewModeFor(SelectedProfile);

    // Bound to the Layouts tab picker. Stored as a string on the profile rather than an enum so the
    // settings JSON stays readable and an unknown value degrades to Auto instead of throwing.
    public string SelectedProfilePreviewMode
    {
        get => SelectedProfile?.PreviewModeOverride switch
        {
            "Windows"  => "Real windows",
            "Previews" => "Live previews",
            _          => "Follow global setting"
        };
        set
        {
            if (SelectedProfile is null) return;
            var stored = value switch
            {
                "Real windows"  => "Windows",
                "Live previews" => "Previews",
                _               => ""
            };
            if (SelectedProfile.PreviewModeOverride == stored) return;
            SelectedProfile.PreviewModeOverride = stored;
            OnPropertyChanged();
            RaiseLayoutModeDependents();
            Save();

            // Rebuild the overlay surfaces to match the new decision immediately -- StartCornerOverlays
            // tears down first and then bails when this profile is now flat, which is exactly the
            // teardown a switch to "Real windows" needs.
            if (CornerOverlaysLive) StartCornerOverlays();
            Log.Info($"Layout '{SelectedProfile.Name}' render mode: {value}.");
        }
    }

    public IReadOnlyList<string> PreviewModeOptions { get; } =
        new[] { "Follow global setting", "Live previews", "Real windows" };

    internal void RaiseLayoutModeDependents()
    {
        OnPropertyChanged(nameof(PreviewModeActive));
        OnPropertyChanged(nameof(SelectedProfilePreviewMode));
        OnPropertyChanged(nameof(LayoutModeSummary));
        OnPropertyChanged(nameof(LayoutModeWarning));
        OnPropertyChanged(nameof(HasLayoutModeWarning));
    }

    // One authoritative answer to "will this profile show live previews or real windows, and why".
    // Which of the two apply paths runs (ApplyCornerOverlayLayout vs ApplyLayout) was previously
    // invisible in the UI -- it is decided at apply time from SupportsCornerGrid plus the global
    // toggle, so someone picking a dense Grid had no way to tell that all but one of their clients
    // would be thumbnails rather than live windows.
    public string LayoutModeSummary
    {
        get
        {
            if (SelectedProfile is null || SelectedProfile.Slots.Count == 0) return "";
            var n = SelectedProfile.Slots.Count;

            if (!SelectedProfile.SupportsCornerGrid)
                return $"Flat mode - all {n} clients render live. Previews need slots at two or more "
                     + "distinct positions; every slot in this layout sits at the same one.";

            if (!PreviewModeActive)
                return SelectedProfile.PreviewModeOverride == "Windows"
                    ? $"Flat mode - all {n} clients render live as real windows, each resized to its own "
                    + "slot. This layout is set to \"Real windows\", so the global live-preview setting "
                    + "is ignored for it."
                    : $"Flat mode - all {n} clients render live, each resized to its own slot. Turn on "
                    + $"\"Enable live previews\" on the Previews tab, or set this layout to \"Live "
                    + $"previews\", to render the master live and the other {n - 1} as thumbnails instead.";

            return $"Preview mode - the master seat renders live in slot {CenterSlotNumber}; the other "
                 + $"{n - 1} are live preview thumbnails, and their clients park off-screen at master "
                 + "resolution. This is the EVE-O Preview style arrangement.";
        }
    }

    // Flat mode resizes each client to its OWN slot rect, so a dense flat layout quietly asks for
    // windows smaller than EVE will accept -- the clients clamp, overlap, and the layout looks broken
    // with nothing explaining why. Preview mode is immune: every client sits at master resolution.
    public string LayoutModeWarning
    {
        get
        {
            if (SelectedProfile is null || SelectedProfile.Slots.Count == 0) return "";

            // Per-slot RenderMode decides this now, not the profile mode alone: a preview-mode layout
            // can still contain real windows, and a flat layout can contain previews. Only slots that
            // will actually hold a CLIENT are subject to EVE's minimum size -- a 480x350 preview tile
            // is perfectly fine, and warning about it was the thing standing between the user and a
            // layout that mixes the two.
            var windowSlots = SelectedProfile.Slots.Where(SlotRendersAsWindow).ToList();
            if (windowSlots.Count == 0) return "";

            var tooSmall = windowSlots
                .Select(ResolvePlacementRect)
                .Where(r => r.Width < MinUsableClientWidth || r.Height < MinUsableClientHeight)
                .ToList();
            if (tooSmall.Count == 0) return "";

            var smallest = tooSmall.OrderBy(r => (long)r.Width * r.Height).First();
            return $"Warning: {tooSmall.Count} of this layout's {windowSlots.Count} real-window slots are "
                 + $"smaller than EVE's minimum window size ({MinUsableClientWidth}x{MinUsableClientHeight}); "
                 + $"the smallest is {smallest.Width}x{smallest.Height}. Those clients are resized to fit "
                 + "and EVE will clamp them, so they will overlap. Give those slots more room, set them "
                 + "to Live preview individually (a preview tile has no minimum size), switch the whole "
                 + "layout to live previews, or use a layout with fewer slots.";
        }
    }

    public bool HasLayoutModeWarning => LayoutModeWarning.Length > 0;

    // Options for the slot table's "Renders as" column; see LayoutSlot.RenderModeDisplay.
    public string[] SlotRenderModeOptions { get; } = ["Follow layout", "Real window", "Live preview"];

    public ObservableCollection<LayoutSlot>? ActiveProfileSlots => SelectedProfile?.Slots;
    public bool SelectedProfileIsBuiltIn => SelectedProfile?.IsBuiltIn == true;
    public bool SelectedProfileIsFamilyTemplate => SelectedProfile?.IsFamilyTemplate == true;

    // Resolution/account-count dropdown options for whichever family is selected.
    public IReadOnlyList<DisplayModeOption> AvailableFamilyResolutions => SelectedProfile?.Category switch
    {
        "Grid" => PresetFactory.GridResolutionOptions,
        "Center Master" => PresetFactory.CenterMasterResolutionOptions,
        "Whammy Board" => PresetFactory.WhammyResolutionOptions,
        "Side Stack" => PresetFactory.SideStackResolutionOptions,
        "Twin Stack" => PresetFactory.TwinStackResolutionOptions,
        _ => Array.Empty<DisplayModeOption>(),
    };

    public IReadOnlyList<int> AvailableFamilyCounts => SelectedProfile?.Category switch
    {
        "Grid" => PresetFactory.GridCountOptions,
        "Center Master" => PresetFactory.CenterMasterCountOptions,
        "Whammy Board" => PresetFactory.WhammyCountOptions,
        "Side Stack" => PresetFactory.SideStackCountOptions,
        "Twin Stack" => PresetFactory.TwinStackCountOptions,
        _ => Array.Empty<int>(),
    };

    // Side (Left/Right/Top/Bottom) dropdown — only the Side Stack family has one.
    public bool SelectedProfileHasFamilySide =>
        SelectedProfile?.IsFamilyTemplate == true && SelectedProfile.Category == "Side Stack";

    public IReadOnlyList<string> AvailableFamilySides => PresetFactory.SideStackSideOptions;

    public string? SelectedFamilySide
    {
        get => SelectedProfile is null ? null
            : AvailableFamilySides.FirstOrDefault(s => s == SelectedProfile.TemplateSide) ?? AvailableFamilySides[0];
        set
        {
            if (SelectedProfile is null || value is null || SelectedProfile.TemplateSide == value) return;
            SelectedProfile.TemplateSide = value;
            PresetFactory.RegenerateFamilySlots(SelectedProfile);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveProfileSlots));
            UpdatePositionCodes();
            RebuildLayoutPreview();
            Save();
        }
    }

    public DisplayModeOption? SelectedFamilyResolution
    {
        get => SelectedProfile is null ? null
            : AvailableFamilyResolutions.FirstOrDefault(o => o.Width == SelectedProfile.TemplateWidth && o.Height == SelectedProfile.TemplateHeight);
        set
        {
            if (SelectedProfile is null || value is null) return;
            if (SelectedProfile.TemplateWidth == value.Width && SelectedProfile.TemplateHeight == value.Height) return;
            SelectedProfile.TemplateWidth = value.Width;
            SelectedProfile.TemplateHeight = value.Height;
            PresetFactory.RegenerateFamilySlots(SelectedProfile);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveProfileSlots));
            UpdatePositionCodes();
            RebuildLayoutPreview();
            Save();
        }
    }

    public int SelectedFamilyCount
    {
        get => SelectedProfile?.TemplateCount ?? 0;
        set
        {
            if (SelectedProfile is null || SelectedProfile.TemplateCount == value) return;
            SelectedProfile.TemplateCount = value;
            PresetFactory.RegenerateFamilySlots(SelectedProfile);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveProfileSlots));
            UpdatePositionCodes();
            RebuildLayoutPreview();
            Save();
        }
    }

    // Per-profile "Avoid taskbar" — fits THIS profile into the monitor work area at apply time.
    // Persisted on the profile itself so full-screen and taskbar-aware variants can coexist.
    public bool SelectedProfileAvoidTaskbar
    {
        get => SelectedProfile?.AvoidTaskbar == true;
        set
        {
            if (SelectedProfile is null || SelectedProfile.AvoidTaskbar == value) return;
            SelectedProfile.AvoidTaskbar = value;
            OnPropertyChanged();
            Save();
            ApplyActiveProfile();
        }
    }

    public bool IsCapturingHotkey => _capturingHotkey is not null;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    // ── Settings-backed properties ─────────────────────────────────────────────

    public bool UsePhysicalPixels
    {
        get => _settings.UsePhysicalPixels;
        set
        {
            if (_settings.UsePhysicalPixels == value) return;
            _settings.UsePhysicalPixels = value;
            OnPropertyChanged();
            Log.Warn(value
                ? "Using physical pixels. Coordinates should match screenshots and Win32 window rectangles."
                : "Using scaled logical coordinates. Windows scaling may change coordinate behavior.");
            Save();
        }
    }

    public bool IncludeNotepadTestWindows
    {
        get => _settings.IncludeNotepadTestWindows;
        set
        {
            if (_settings.IncludeNotepadTestWindows == value) return;
            _settings.IncludeNotepadTestWindows = value;
            OnPropertyChanged();
            Save();
            Refresh();
        }
    }

    public bool AutoRefresh
    {
        get => _settings.AutoRefresh;
        set
        {
            if (_settings.AutoRefresh == value) return;
            _settings.AutoRefresh = value;
            OnPropertyChanged();
            if (value) _refreshTimer.Start(); else _refreshTimer.Stop();
            Save();
        }
    }

    public string LayoutTargetMonitorId
    {
        get => _settings.LayoutTargetMonitorId;
        set
        {
            if (_settings.LayoutTargetMonitorId == value) return;
            _settings.LayoutTargetMonitorId = value;
            OnPropertyChanged();
            LoadMasterResolutions();
            OnPropertyChanged(nameof(MasterResolutionStatus));
            OnPropertyChanged(nameof(MasterResolutionStatusSeverity));
            Save();
        }
    }

    public bool UseMonitorWorkArea
    {
        get => _settings.UseMonitorWorkArea;
        set
        {
            if (_settings.UseMonitorWorkArea == value) return;
            _settings.UseMonitorWorkArea = value;
            OnPropertyChanged();
            Save();
        }
    }

    // 2a — Minimize to system tray instead of taskbar.
    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set
        {
            if (_settings.MinimizeToTray == value) return;
            _settings.MinimizeToTray = value;
            OnPropertyChanged();
            Save();
        }
    }

    // 2g — Apply a specified profile on startup.
    public bool ApplyProfileOnStartup
    {
        get => _settings.ApplyProfileOnStartup;
        set
        {
            if (_settings.ApplyProfileOnStartup == value) return;
            _settings.ApplyProfileOnStartup = value;
            OnPropertyChanged();
            Save();
        }
    }

    public string StartupProfileId
    {
        get => _settings.StartupProfileId;
        set
        {
            if (_settings.StartupProfileId == value) return;
            _settings.StartupProfileId = value;
            OnPropertyChanged();
            Save();
        }
    }

    // Re-apply the active profile automatically when assigned clients (re)appear.
    public bool AutoApplyOnClientLaunch
    {
        get => _settings.AutoApplyOnClientLaunch;
        set
        {
            if (_settings.AutoApplyOnClientLaunch == value) return;
            _settings.AutoApplyOnClientLaunch = value;
            OnPropertyChanged();
            if (!value) _autoApplyTimer.Stop();
            Save();
        }
    }

    // 2j — Launch EveDeck with Windows via Run registry key.
    // Run-at-login. In the MSIX (Store) build this is owned by the manifest's StartupTask extension
    // and managed by Windows' own Startup Apps settings -- the HKCU Run key is virtualized inside the
    // package, so writing it would appear to work and then do nothing. Report false and ignore
    // writes there rather than lying to the user with a checkbox that silently has no effect; the
    // Options UI hides the checkbox and points at Windows Settings instead.
    public bool CanManageLaunchWithWindows => !Utilities.PackagedAppInfo.IsPackaged;

    public bool LaunchWithWindows
    {
        get
        {
            if (Utilities.PackagedAppInfo.IsPackaged) return false;
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("EveDeck") is not null;
        }
        set
        {
            if (Utilities.PackagedAppInfo.IsPackaged) return;
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;
            if (value)
            {
                var exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                key.SetValue("EveDeck", $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue("EveDeck", throwOnMissingValue: false);
            }
            OnPropertyChanged();
            Log.Info(value ? "Added EveDeck to Windows startup." : "Removed EveDeck from Windows startup.");
        }
    }

    // 2h — Log level filter ("All", "Info", "Warn", "Error").
    public string LogFilterLevel
    {
        get => _logFilterLevel;
        set
        {
            if (SetProperty(ref _logFilterLevel, value))
            {
                LogsView.Filter = value == "All"
                    ? null
                    : o => o is LogEntry e && e.Level.Equals(value, StringComparison.OrdinalIgnoreCase);
                LogsView.Refresh();
            }
        }
    }

    // 2d — Profile name search filter.
    public string ProfileSearchText
    {
        get => _profileSearchText;
        set
        {
            if (SetProperty(ref _profileSearchText, value))
            {
                ProfilesView.Filter = string.IsNullOrWhiteSpace(value)
                    ? null
                    : o => o is LayoutProfile p && p.Name.Contains(value, StringComparison.OrdinalIgnoreCase);
                ProfilesView.Refresh();
            }
        }
    }

    // Last main-window position; persisted by the view on close and restored on launch.
    public double? WindowLeft
    {
        get => _settings.WindowLeft;
        set => _settings.WindowLeft = value;
    }

    public double? WindowTop
    {
        get => _settings.WindowTop;
        set => _settings.WindowTop = value;
    }

    // True when a managed EVE (or test) window currently owns the foreground. Used by the hotkey
    // service to decide whether gated hotkeys should be registered with the OS right now.
    public bool IsEveWindowForeground()
    {
        var fg = _windowService.GetForegroundWindowHandle();
        return fg != 0 && Windows.Any(w => w.Handle == fg);
    }

    public double UiScale
    {
        get => _settings.UiScale;
        set
        {
            var clamped = Math.Round(Math.Clamp(value, 0.5, 3.0), 2);
            if (Math.Abs(_settings.UiScale - clamped) < 0.001) return;
            _settings.UiScale = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    // ── Selection properties ───────────────────────────────────────────────────

    public EveWindowInfo? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (SetProperty(ref _selectedWindow, value))
                RaiseCommandStates();
        }
    }

    public SlotAssignment? SelectedAssignment
    {
        get => _selectedAssignment;
        set
        {
            if (SetProperty(ref _selectedAssignment, value))
                RaiseCommandStates();
        }
    }

    public LayoutProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value) && value is not null)
            {
                _settings.ActiveProfileId = value.Id;
                OnPropertyChanged(nameof(ActiveProfileSlots));
                OnPropertyChanged(nameof(SelectedProfileIsBuiltIn));
                OnPropertyChanged(nameof(SelectedProfileIsFamilyTemplate));
                OnPropertyChanged(nameof(AvailableFamilyResolutions));
                OnPropertyChanged(nameof(AvailableFamilyCounts));
                OnPropertyChanged(nameof(SelectedFamilyResolution));
                OnPropertyChanged(nameof(SelectedFamilyCount));
                OnPropertyChanged(nameof(SelectedProfileHasFamilySide));
                OnPropertyChanged(nameof(SelectedFamilySide));
                OnPropertyChanged(nameof(SelectedProfileAvoidTaskbar));
                OnPropertyChanged(nameof(MasterResolutionStatus));
                OnPropertyChanged(nameof(MasterResolutionStatusSeverity));
                SetMasterResolutionCommand.RaiseCanExecuteChanged();
                ClearMasterResolutionCommand.RaiseCanExecuteChanged();
                LoadMasterResolutions();
                // Master is per-profile: re-point the master badge + corner baseline at the new profile's.
                EnsureValidMasterSeat();
                SyncMasterSlot();
                ResetCornerOccupancy();
                OnPropertyChanged(nameof(MasterSlotNumber));
                UpdatePositionCodes();
                RebuildLayoutPreview();
                RaiseCommandStates();
                RefreshCharacterSetLayoutNames();
                RaiseLayoutModeDependents();
                OnPropertyChanged(nameof(ActiveSetLayout));
            }
        }
    }

    public HotkeyBinding? SelectedHotkey
    {
        get => _selectedHotkey;
        set
        {
            if (SetProperty(ref _selectedHotkey, value))
            {
                CaptureHotkeyCommand.RaiseCanExecuteChanged();
                ClearHotkeyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // ── Core operations ────────────────────────────────────────────────────────

    public void Refresh()
    {
        try
        {
            Windows.Clear();
            var extraProcessNames = _settings.PreviewableApps.Where(a => a.Enabled).Select(a => a.ProcessName).ToList();
            foreach (var window in _windowService.FindEveWindows(IncludeNotepadTestWindows, extraProcessNames))
                Windows.Add(window);

            // Only rebuild the Monitors collection when the monitor set actually changed. Clearing
            // and re-adding it every refresh (5s) momentarily empties the ItemsSource behind the
            // "target monitor" ComboBox, whose two-way SelectedValue binding then writes null back to
            // LayoutTargetMonitorId; Refresh immediately resets it to the primary monitor's id, so the
            // value oscillated null <-> "\\.\DISPLAY1" twice per refresh. Each toggle called Save()
            // (the setter persists), producing ~2 disk writes of the whole 140KB+ settings.json every
            // 5s on the UI thread -- a real source of switching jank, and long enough on some writes
            // to starve the overlay's UpdateLayeredWindow push so previews briefly blanked. Monitors
            // change only on a real display topology change, so this rebuild almost never runs now.
            var freshMonitors = _windowService.GetMonitors();
            if (!MonitorsMatch(Monitors, freshMonitors))
            {
                Monitors.Clear();
                foreach (var monitor in freshMonitors)
                    Monitors.Add(monitor);
            }

            if (string.IsNullOrWhiteSpace(LayoutTargetMonitorId) || Monitors.All(m => m.Id != LayoutTargetMonitorId))
                LayoutTargetMonitorId = Monitors.FirstOrDefault(m => m.IsPrimary)?.Id ?? Monitors.FirstOrDefault()?.Id ?? "";

            RebindRestartedWindows();
            DetectNewlyLaunchedClients();
            UpdateLiveSeatCharacters();
            LastUpdatedText = $"Last refresh {DateTime.Now:HH:mm:ss}";
            Status = $"Detected {Windows.Count} EVE/test windows and {Monitors.Count} monitors.";
            // Log this only when the detected set actually CHANGES. At a 5s poll the unconditional
            // version wrote ~17k identical lines a day -- easily the largest single source of noise in
            // the log, and it buried the handful of lines that matter when diagnosing something. The
            // signature is built from real handles rather than the count, so a client closing while
            // another opens in the same interval still gets logged. The status bar itself, and
            // LastUpdatedText above, still update on every refresh.
            var detection = string.Join(",", Windows.Select(w => w.Handle).OrderBy(h => h)) + $"|{Monitors.Count}";
            if (detection != _lastLoggedDetection)
            {
                _lastLoggedDetection = detection;
                Log.Info(Status);
            }
            OnPropertyChanged(nameof(WindowCount));
            OnPropertyChanged(nameof(UnassignedWindowCount));
            OnPropertyChanged(nameof(MonitorCount));
            OnPropertyChanged(nameof(HasNoWindows));
            OnPropertyChanged(nameof(AllWindowsAssigned));
            OnPropertyChanged(nameof(DetectionStateText));
            OnPropertyChanged(nameof(DetectionStateColor));
            RebuildMonitorPreview();

            // Live running-character labels changed with the detected windows above: refresh the
            // surfaces that snapshot them (they are not data-bound to DisplayLabel directly).
            RebuildMiniMap();
            RaiseIdentityDependents();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Log.Error($"Refresh failed: {ex.Message}");
        }
    }

    // True when the freshly-enumerated monitors are equivalent to what's already in the Monitors
    // collection -- so Refresh can skip the Clear()+re-add that would otherwise churn the ComboBox
    // ItemsSource (see the call site). Compares the fields the UI and layout math actually depend on.
    private static bool MonitorsMatch(IReadOnlyList<MonitorInfo> current, IReadOnlyList<MonitorInfo> fresh)
    {
        if (current.Count != fresh.Count) return false;
        for (var i = 0; i < current.Count; i++)
        {
            var a = current[i];
            var b = fresh[i];
            // WindowRect is a mutable class with no value equality, so compare its fields explicitly
            // rather than relying on Equals (which would be reference equality -- always false for the
            // freshly-enumerated monitors, defeating the whole point of this check).
            if (a.Id != b.Id || a.IsPrimary != b.IsPrimary || a.DpiX != b.DpiX || a.DpiY != b.DpiY
                || !RectsEqual(a.Bounds, b.Bounds) || !RectsEqual(a.WorkArea, b.WorkArea))
                return false;
        }
        return true;
    }

    private static bool RectsEqual(WindowRect a, WindowRect b)
        => a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;

    public void Save()
    {
        try
        {
            // Keep the active character set's stored seats in lockstep with the live Assignments before
            // serialising. All editing (rename, add/delete seat, per-seat font/colour) happens on the live
            // top-level Assignments; without this the set keeps a stale copy that resurfaces on a set
            // switch or reload -- the cause of labels/seats not persisting consistently.
            SnapshotLiveToActiveSet();
            // ConfigService skips the disk write when nothing changed (the periodic refresh loop calls
            // Save() far more often than settings actually change); only surface a "saved" line for a
            // real write, so the log/status isn't spammed with no-op saves.
            if (_configService.Save(_settings))
            {
                Status = $"Saved settings to {_configService.ConfigPath}.";
                Log.Info(Status);
            }
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Log.Error($"Save failed: {ex.Message}");
        }
    }

    private void OnPortraitCacheChanged()
    {
        RaiseIdentityDependents();
        if (PreviewModeActive && CornerOverlaysLive) RefreshAllPills();
    }

    public void Cleanup()
    {
        PortraitCacheService.Instance.Changed -= OnPortraitCacheChanged;
        // Stop the periodic timers first. OnClosed runs Cleanup(), Save() and a residual-window
        // sweep in sequence, so a Tick landing mid-sequence would run Refresh() or a second Save()
        // against half-torn-down state -- the same shape as the zombie process that used to
        // survive tray > Exit.
        _refreshTimer.Stop();
        _autoSaveTimer.Stop();
        _portraitSweepTimer.Stop();
        _frameTimer.Stop();
        _seatHealthTimer.Stop();
        _autoApplyTimer.Stop();
        _launchGroupCts?.Cancel();
        StopChatAlerts();
        StopIntel();
        StopCornerOverlays();
        _downtimeTimer.Stop();
        HideDowntimeWindow();
        if (_frameOverlay is not null)
        {
            _frameOverlay.Close();
            _frameOverlay = null;
        }
        // Exiting must not leave any client parked on efficiency cores / below-normal priority.
        RestoreAllProcessPriorities();
    }

    private void RaiseCommandStates()
    {
        AssignSelectedCommand.RaiseCanExecuteChanged();
        AssignWindowToSlotCommand.RaiseCanExecuteChanged();
        RestoreSelectedStyleCommand.RaiseCanExecuteChanged();
        ToggleSelectedBorderlessCommand.RaiseCanExecuteChanged();
        RenameProfileCommand.RaiseCanExecuteChanged();
        DuplicateProfileCommand.RaiseCanExecuteChanged();
        DeleteProfileCommand.RaiseCanExecuteChanged();
        DeleteSelectedSlotCommand.RaiseCanExecuteChanged();
        ExportProfileCommand.RaiseCanExecuteChanged();
    }

    private void ScheduleAutoSave()
    {
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    // Write immediately if a debounced auto-save is pending -- e.g. when the window hides to the tray --
    // so a just-typed label isn't stuck in the 1s debounce if the process goes away before it fires.
    internal void FlushPendingSave()
    {
        if (_autoSaveTimer.IsEnabled) { _autoSaveTimer.Stop(); Save(); }
    }

    // ── Static helpers ─────────────────────────────────────────────────────────

    internal static uint ToHotkeyModifiers(ModifierKeys modifiers)
    {
        var flags = 0u;
        if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= HotkeyDefaults.ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) flags |= HotkeyDefaults.ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= HotkeyDefaults.ModShift;
        return flags;
    }

    internal static string FormatGesture(uint modifiers, Key key)
    {
        var parts = new List<string>();
        if ((modifiers & HotkeyDefaults.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & HotkeyDefaults.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & HotkeyDefaults.ModShift) != 0) parts.Add("Shift");
        parts.Add(KeyToText(key));
        return string.Join("+", parts);
    }

    internal static string KeyToText(Key key) => key switch
    {
        Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4",
        Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
        Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down",
        _ => key.ToString()
    };

    internal static Brush ParseFrameBrush(string color)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(color);
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Orange;
        }
    }
}

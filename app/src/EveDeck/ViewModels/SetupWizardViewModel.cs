using System.Collections.ObjectModel;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.Utilities;

namespace EveDeck.ViewModels;

// Drives the first-run setup wizard: client count → target monitor → seat/ESI assignment → summary.
// Self-contained so it can be shown modally without touching the main view-model until Finish.
public sealed class SetupWizardViewModel : ObservableObject
{
    private const double Ratio16By9 = 16.0 / 9.0;

    private readonly EsiAuthService _esiAuth = new();
    private readonly EsiTokenStore _tokenStore = new(ConfigService.DefaultAppDataFolder);

    // Characters already linked to a seat elsewhere in the app (passed in when the wizard is
    // re-run from Settings, not just on first run) -- checked alongside WizardSlots so re-adding
    // an already-linked character is rejected with feedback instead of silently vanishing when
    // MainWindow merges the wizard result back in.
    private readonly HashSet<long> _existingCharacterIds;

    // Slots trimmed off by PopulateWizardSlots when the user reduces ClientCount, keyed by slot
    // number, so their linked characters come back if the user raises the count again instead of
    // being silently dropped.
    private readonly Dictionary<int, SlotAssignment> _trimmedSlotCache = new();

    public ObservableCollection<int> ClientCountOptions { get; } = new(Enumerable.Range(1, 50));
    public ObservableCollection<MonitorInfo> Monitors { get; }
    public ObservableCollection<SlotAssignment> WizardSlots { get; } = new();

    public RelayCommand AddWizardEsiCharacterCommand { get; }
    public RelayCommand RemoveWizardEsiCharacterCommand { get; }

    public SetupWizardViewModel(IEnumerable<MonitorInfo> monitors, IEnumerable<long>? existingLinkedCharacterIds = null)
    {
        Monitors = new ObservableCollection<MonitorInfo>(monitors);
        _selectedMonitor = Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
        _existingCharacterIds = existingLinkedCharacterIds is null ? new HashSet<long>() : new HashSet<long>(existingLinkedCharacterIds);
        AddWizardEsiCharacterCommand = new RelayCommand(AddWizardEsiCharacter);
        RemoveWizardEsiCharacterCommand = new RelayCommand(RemoveWizardEsiCharacter);
    }

    // ── Step navigation ──────────────────────────────────────────────────────────

    private int _step;
    public int Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                if (value == 2) PopulateWizardSlots();
                RaiseStepDependents();
            }
        }
    }

    public bool IsClientStep => Step == 0;
    public bool IsMonitorStep => Step == 1;
    public bool IsEsiStep => Step == 2;
    public bool IsSummaryStep => Step == 3;
    public bool IsLastStep => Step == 3;
    public bool CanGoBack => Step > 0;
    public bool CanGoNext => Step != 1 || SelectedMonitor is not null;
    public string NextButtonText => IsLastStep ? "Finish" : "Next";
    public string StepIndicator => $"Step {Step + 1} of 4";

    public string StepTitle => Step switch
    {
        0 => "Welcome to EveDeck",
        1 => "Choose your display",
        2 => "Link your characters",
        3 => "Ready to apply",
        _ => "Setup"
    };

    public void Next() { if (Step < 3 && CanGoNext) Step++; }
    public void Back() { if (Step > 0) Step--; }

    private void RaiseStepDependents()
    {
        OnPropertyChanged(nameof(IsClientStep));
        OnPropertyChanged(nameof(IsMonitorStep));
        OnPropertyChanged(nameof(IsEsiStep));
        OnPropertyChanged(nameof(IsSummaryStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(StepIndicator));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(SummaryText));
    }

    // The wizard slot that received the FIRST linked character — becomes the app master at finish.
    // 0 = no characters linked yet (the main view-model falls back to its layout default).
    public int MasterSeatNumber { get; private set; }

    private void UpdateMasterFlags()
    {
        foreach (var s in WizardSlots)
            s.IsMaster = s.SlotNumber == MasterSeatNumber;
    }

    // ── Choices ──────────────────────────────────────────────────────────────────

    private int _clientCount = 5;
    public int ClientCount
    {
        get => _clientCount;
        set
        {
            if (!SetProperty(ref _clientCount, value)) return;
            OnPropertyChanged(nameof(IsCenterMaster));
            OnPropertyChanged(nameof(ClientCountDescription));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(MonitorWarning));
            OnPropertyChanged(nameof(HasMonitorWarning));
        }
    }

    private MonitorInfo? _selectedMonitor;
    public MonitorInfo? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (!SetProperty(ref _selectedMonitor, value)) return;
            OnPropertyChanged(nameof(MonitorWarning));
            OnPropertyChanged(nameof(HasMonitorWarning));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(CanGoNext));
        }
    }

    private bool _focusPreviewOnClick = true;
    public bool FocusPreviewOnClick
    {
        get => _focusPreviewOnClick;
        set { if (SetProperty(ref _focusPreviewOnClick, value)) OnPropertyChanged(nameof(SummaryText)); }
    }

    // ── ESI login feedback ──────────────────────────────────────────────────────

    private bool _isEsiLoginInProgress;
    public bool IsEsiLoginInProgress
    {
        get => _isEsiLoginInProgress;
        private set { if (SetProperty(ref _isEsiLoginInProgress, value)) OnPropertyChanged(nameof(CanAddEsiCharacter)); }
    }

    // Bound to each "+ Add via ESI" button's IsEnabled -- only one login flow runs at a time.
    public bool CanAddEsiCharacter => !IsEsiLoginInProgress;

    // Transient feedback for a cancelled/timed-out/duplicate ESI login attempt. Empty = no message
    // shown. Cleared at the start of every new attempt.
    private string _esiStatusMessage = "";
    public string EsiStatusMessage
    {
        get => _esiStatusMessage;
        private set { if (SetProperty(ref _esiStatusMessage, value)) OnPropertyChanged(nameof(HasEsiStatusMessage)); }
    }
    public bool HasEsiStatusMessage => !string.IsNullOrEmpty(EsiStatusMessage);

    public bool IsCenterMaster => ClientCount >= 4 && ClientCount <= 15;

    public bool NoMonitors => Monitors.Count == 0;

    public string ClientCountDescription
    {
        get
        {
            if (ClientCount == 1) return "One client filling the monitor.";
            if (IsCenterMaster) return ClientCount.ToString() + " clients in a ring with a master client centered on top.";
            if (ClientCount <= 15) return ClientCount.ToString() + " clients arranged in a grid across the monitor.";
            return ClientCount.ToString() + " clients. Design a custom layout in the Layouts tab.";
        }
    }

    public string MonitorWarning
    {
        get
        {
            var m = SelectedMonitor;
            if (m is null) return "";

            var w = m.Bounds.Width;
            var h = m.Bounds.Height;
            if (w <= 0 || h <= 0) return "";

            var aspect = (double)w / h;
            var notWidescreen = Math.Abs(aspect - Ratio16By9) > 0.06;

            var parts = new List<string>();
            if (notWidescreen)
            {
                parts.Add(IsCenterMaster
                    ? $"This monitor is {w}×{h} ({RatioText(w, h)}), not 16:9. The center-master layout assumes 16:9; tiles may appear stretched. You can proceed or choose a grid layout instead."
                    : $"This monitor is {w}×{h} ({RatioText(w, h)}). The grid will fit any aspect ratio.");
            }
            if (m.ScalePercent != 100)
                parts.Add($"Display scaling is {m.ScalePercent:0}%. EveDeck accounts for this automatically.");

            return string.Join("\n\n", parts);
        }
    }

    public bool HasMonitorWarning => !string.IsNullOrEmpty(MonitorWarning);

    public string SummaryText
    {
        get
        {
            var m = SelectedMonitor;
            var monLabel = m is null ? "the primary monitor" : $"{m.DeviceName} ({m.Bounds.Width}×{m.Bounds.Height})";
            var w = m?.Bounds.Width ?? 2560;
            var h = m?.Bounds.Height ?? 1440;
            var profile = PresetFactory.BestProfileName(ClientCount, w, h) ?? "a built-in layout";

            var lines = new List<string>
            {
                $"• {ClientCount} EVE client{(ClientCount == 1 ? "" : "s")} on {monLabel}",
                $"• Layout preset: {profile}",
            };
            if (IsCenterMaster)
            {
                lines.Add($"• Center-master layout with slot {ClientCount} as master");
                lines.Add($"• Click a preview to swap it to center: {(FocusPreviewOnClick ? "enabled" : "disabled")}");
            }

            var totalChars = WizardSlots.Sum(s => s.EsiCharacters.Count);
            var assignedSeats = WizardSlots.Count(s => s.EsiCharacters.Count > 0);
            lines.Add(totalChars > 0
                ? $"• Characters linked: {totalChars} across {assignedSeats} seat(s)"
                : "• No characters linked yet (you can add them later)");

            var masterSlot = WizardSlots.FirstOrDefault(s => s.SlotNumber == MasterSeatNumber);
            if (masterSlot is not null)
                lines.Add($"• Master account (centered at rest): {masterSlot.Label}");

            lines.Add("");
            lines.Add("After finishing, assign your EVE clients to seats in the Clients tab, then Apply (Ctrl+Alt+A).");
            return string.Join("\n", lines);
        }
    }

    // ── Wizard seat slots ────────────────────────────────────────────────────────

    private void PopulateWizardSlots()
    {
        // Trim excess slots if the user changed client count and went Back. Cache them (rather than
        // discarding) so linked characters come back if the count is raised again instead of being
        // silently lost.
        while (WizardSlots.Count > ClientCount)
        {
            var last = WizardSlots[^1];
            _trimmedSlotCache[last.SlotNumber] = last;
            WizardSlots.RemoveAt(WizardSlots.Count - 1);
        }
        // Add missing slots, restoring a cached one for that slot number if we trimmed it earlier.
        while (WizardSlots.Count < ClientCount)
        {
            var slotNumber = WizardSlots.Count + 1;
            if (_trimmedSlotCache.Remove(slotNumber, out var cached))
                WizardSlots.Add(cached);
            else
                WizardSlots.Add(new SlotAssignment { SlotNumber = slotNumber, Label = $"Slot {slotNumber}" });
        }
        UpdateMasterFlags();
    }

    private async void AddWizardEsiCharacter(object? parameter)
    {
        if (parameter is not SlotAssignment slot) return;
        if (slot.EsiCharacters.Count >= 3) return;
        if (IsEsiLoginInProgress) return;

        IsEsiLoginInProgress = true;
        EsiStatusMessage = "";
        try
        {
            var token = await _esiAuth.AuthorizeAsync(CancellationToken.None);
            var characterId = token.CharacterId;
            var characterName = token.CharacterName;

            if (WizardSlots.Any(s => s.EsiCharacters.Any(c => c.CharacterId == characterId))
                || _existingCharacterIds.Contains(characterId))
            {
                EsiStatusMessage = $"{characterName} is already linked to another seat.";
                return;
            }

            _tokenStore.Put(token);

            // First character linked anywhere designates this slot as the app master.
            var isFirstEver = WizardSlots.Sum(s => s.EsiCharacters.Count) == 0;

            slot.EsiCharacters.Add(new EsiCharacter { CharacterId = characterId, CharacterName = characterName });
            if (slot.EsiCharacters.Count == 1) slot.Label = characterName;

            if (isFirstEver)
            {
                MasterSeatNumber = slot.SlotNumber;
                UpdateMasterFlags();
            }
            OnPropertyChanged(nameof(SummaryText));
        }
        catch (TimeoutException)
        {
            EsiStatusMessage = "ESI login timed out after 5 minutes — try again.";
        }
        catch
        {
            EsiStatusMessage = "Login cancelled or failed — try again.";
        }
        finally { IsEsiLoginInProgress = false; }
    }

    private void RemoveWizardEsiCharacter(object? parameter)
    {
        if (parameter is not EsiCharacter character) return;
        var slot = WizardSlots.FirstOrDefault(s => s.EsiCharacters.Contains(character));
        if (slot is null) return;
        slot.EsiCharacters.Remove(character);
        if (slot.EsiCharacters.Count > 0 && slot.Label.Equals(character.CharacterName, StringComparison.OrdinalIgnoreCase))
            slot.Label = slot.EsiCharacters[0].CharacterName;
        else if (slot.EsiCharacters.Count == 0)
            slot.Label = $"Slot {slot.SlotNumber}";

        // If the master slot was emptied, hand the master badge to the next slot that still has a character.
        if (MasterSeatNumber == slot.SlotNumber && slot.EsiCharacters.Count == 0)
        {
            MasterSeatNumber = WizardSlots.FirstOrDefault(s => s.EsiCharacters.Count > 0)?.SlotNumber ?? 0;
            UpdateMasterFlags();
        }
        OnPropertyChanged(nameof(SummaryText));
    }

    private static string RatioText(int w, int h)
    {
        var g = Gcd(w, h);
        return g == 0 ? $"{w}:{h}" : $"{w / g}:{h / g}";
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) { (a, b) = (b, a % b); }
        return Math.Abs(a);
    }
}

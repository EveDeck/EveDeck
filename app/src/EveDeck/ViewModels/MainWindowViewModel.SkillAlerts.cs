using System.Threading;
using System.Windows.Threading;
using EveDeck.Services;
using EveDeck.Utilities;

namespace EveDeck.ViewModels;

// Skill-queue alerts (added 2026-07-24): polls every linked character's skill queue on a slow cadence
// and raises a notification when a skill finishes, a queue empties, or a queue is about to run dry.
// The detection/dedup logic lives in the unit-tested SkillQueueService; this partial owns the timer,
// the settings-bound toggles, character/skill name resolution, and firing the notifications.
public sealed partial class MainWindowViewModel
{
    // ── Shared ESI plumbing (also used by PI and, later, the preview info flyout) ────────────────────
    // Exactly ONE EsiClient app-wide: its per-character refresh mutex is what stops two features
    // refreshing the same token concurrently and clobbering EVE's rotated refresh token. Lazily built.
    private EsiClient? _esiClient;
    private EsiTypeCache? _esiTypeCache;
    private CharacterInfoService? _characterInfo;
    private EsiClient EsiClientShared => _esiClient ??= new EsiClient(_esiAuth, TokenStore);
    private EsiTypeCache EsiTypeCacheShared => _esiTypeCache ??= new EsiTypeCache(_configService.AppDataFolder);
    private CharacterInfoService CharacterInfoShared => _characterInfo ??= new CharacterInfoService(EsiClientShared);

    private SkillQueueService? _skillQueueService;
    private readonly DispatcherTimer _skillPollTimer = new();
    private bool _skillPollInProgress;

    private void InitSkillAlerts()
    {
        _skillPollTimer.Tick += async (_, _) => await PollSkillQueuesAsync();
        if (SkillAlertsEnabled) StartSkillAlerts();
    }

    // ── Settings-bound toggles ──────────────────────────────────────────────────────────────────────

    public bool SkillAlertsEnabled
    {
        get => _settings.SkillAlertsEnabled;
        set
        {
            if (_settings.SkillAlertsEnabled == value) return;
            _settings.SkillAlertsEnabled = value;
            OnPropertyChanged();
            Save();
            if (value) StartSkillAlerts(); else StopSkillAlerts();
        }
    }

    public bool SkillAlertCompleted
    {
        get => _settings.SkillAlertCompleted;
        set { if (_settings.SkillAlertCompleted == value) return; _settings.SkillAlertCompleted = value; OnPropertyChanged(); Save(); }
    }

    public bool SkillAlertQueueEmpty
    {
        get => _settings.SkillAlertQueueEmpty;
        set { if (_settings.SkillAlertQueueEmpty == value) return; _settings.SkillAlertQueueEmpty = value; OnPropertyChanged(); Save(); }
    }

    public bool SkillAlertQueueLow
    {
        get => _settings.SkillAlertQueueLow;
        set { if (_settings.SkillAlertQueueLow == value) return; _settings.SkillAlertQueueLow = value; OnPropertyChanged(); Save(); }
    }

    public int SkillAlertLowThresholdHours
    {
        get => _settings.SkillAlertLowThresholdHours;
        set { var v = System.Math.Max(1, value); if (_settings.SkillAlertLowThresholdHours == v) return; _settings.SkillAlertLowThresholdHours = v; OnPropertyChanged(); Save(); }
    }

    public int SkillQueueRefreshMinutes
    {
        get => _settings.SkillQueueRefreshMinutes;
        set
        {
            var v = System.Math.Max(5, value);
            if (_settings.SkillQueueRefreshMinutes == v) return;
            _settings.SkillQueueRefreshMinutes = v;
            OnPropertyChanged();
            _skillPollTimer.Interval = System.TimeSpan.FromMinutes(v);
            Save();
        }
    }

    // ── Timer control ───────────────────────────────────────────────────────────────────────────────

    private void StartSkillAlerts()
    {
        _skillQueueService ??= new SkillQueueService(CharacterInfoShared);
        _skillPollTimer.Interval = System.TimeSpan.FromMinutes(System.Math.Max(5, _settings.SkillQueueRefreshMinutes));
        _skillPollTimer.Start();
        _ = PollSkillQueuesAsync();
    }

    private void StopSkillAlerts() => _skillPollTimer.Stop();

    // ── Poll + notify ───────────────────────────────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task PollSkillQueuesAsync()
    {
        if (_skillPollInProgress || !SkillAlertsEnabled) return;
        _skillPollInProgress = true;
        try
        {
            // Only characters whose stored token actually granted the skillqueue scope -- polling one
            // that didn't just burns ESI's error budget on guaranteed 403s.
            var characterIds = Assignments
                .SelectMany(a => a.EsiCharacters.Select(c => c.CharacterId))
                .Distinct()
                .Where(id => TokenStore.Get(id)?.HasScope(EsiAuthService.ScopeSkillQueue) == true)
                .ToList();
            if (characterIds.Count == 0) return;

            var threshold = System.TimeSpan.FromHours(System.Math.Max(1, _settings.SkillAlertLowThresholdHours));
            var alerts = await _skillQueueService!.PollAsync(characterIds, threshold, m => Log.Info(m), CancellationToken.None);
            foreach (var alert in alerts)
                await NotifySkillAlertAsync(alert);
        }
        catch (System.Exception ex)
        {
            Log.Warn($"Skill-queue poll failed: {ex}");
        }
        finally
        {
            _skillPollInProgress = false;
        }
    }

    private async System.Threading.Tasks.Task NotifySkillAlertAsync(SkillAlert alert)
    {
        var enabled = alert.Kind switch
        {
            SkillAlertKind.Completed => _settings.SkillAlertCompleted,
            SkillAlertKind.QueueEmpty => _settings.SkillAlertQueueEmpty,
            SkillAlertKind.QueueLow => _settings.SkillAlertQueueLow,
            _ => false,
        };
        if (!enabled) return;

        var who = SkillAlertCharacterName(alert.CharacterId);
        string title, message;
        switch (alert.Kind)
        {
            case SkillAlertKind.Completed:
                var skill = await EsiTypeCacheShared.GetTypeAsync(alert.SkillId, CancellationToken.None);
                title = "Skill trained";
                message = $"{who} finished {skill.Name} {RomanLevel(alert.FinishedLevel)}.";
                break;
            case SkillAlertKind.QueueEmpty:
                title = "Skill queue empty";
                message = $"{who} has no skill training.";
                break;
            default: // QueueLow
                title = "Skill queue low";
                message = $"{who}'s skill queue runs dry within {System.Math.Max(1, _settings.SkillAlertLowThresholdHours)}h.";
                break;
        }
        NativeNotificationService.Show(title, message);
    }

    private string SkillAlertCharacterName(long characterId)
        => Assignments.SelectMany(a => a.EsiCharacters).FirstOrDefault(c => c.CharacterId == characterId)?.CharacterName
           ?? $"Character {characterId}";

    private static string RomanLevel(int level) => level switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => level.ToString(),
    };
}

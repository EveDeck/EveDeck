using System.Threading;
using System.Windows.Threading;
using EveDeck.Models;
using EveDeck.Services;

namespace EveDeck.ViewModels;

// Seat health alerts (added 2026-07-28): toast notifications for two failure modes that are easy to
// miss on a parked/idle alt -- getting podded, and a client that silently disconnected while its
// window stayed open. Independent of corner overlays (toasts don't need the overlay surfaces at all,
// see ShowToast), so this partial owns its own timer rather than piggybacking on the jump-status one.
public sealed partial class MainWindowViewModel
{
    // ESI type ids for a character's ship being a capsule -- verified live against ESI, 2026-07-28
    // (POST /universe/ids/ for "Capsule" -> 670; the golden-clone "Genolution Capsule 8G" -> 33328,
    // confirmed via GET /universe/types/33328/ since the ids/ search endpoint silently drops it).
    private static readonly HashSet<int> CapsuleTypeIds = new() { 670, 33328 };

    // How far back to ask zKillboard for a loss record when a seat's ship just became a capsule.
    // Generous enough to cover ESI/zKill propagation lag against this timer's own 30s poll interval.
    private const int PoddedLossLookbackSeconds = 900;

    private readonly DispatcherTimer _seatHealthTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<int, int> _lastKnownShipTypeId = new();
    private readonly Dictionary<int, bool> _lastKnownOnline = new();

    public bool SeatHealthAlertPodded
    {
        get => _settings.SeatHealthAlertPodded;
        set
        {
            if (_settings.SeatHealthAlertPodded == value) return;
            _settings.SeatHealthAlertPodded = value;
            OnPropertyChanged();
            Save();
            UpdateSeatHealthTimerState();
        }
    }

    public bool SeatHealthAlertDisconnected
    {
        get => _settings.SeatHealthAlertDisconnected;
        set
        {
            if (_settings.SeatHealthAlertDisconnected == value) return;
            _settings.SeatHealthAlertDisconnected = value;
            OnPropertyChanged();
            Save();
            UpdateSeatHealthTimerState();
        }
    }

    private void UpdateSeatHealthTimerState()
    {
        if (_settings.SeatHealthAlertPodded || _settings.SeatHealthAlertDisconnected) _seatHealthTimer.Start();
        else
        {
            _seatHealthTimer.Stop();
            _lastKnownShipTypeId.Clear();
            _lastKnownOnline.Clear();
        }
    }

    private async void OnSeatHealthTick(object? sender, EventArgs e)
    {
        foreach (var assignment in Assignments.ToList())
        {
            var seat = assignment.SlotNumber;
            var character = ResolveSeatCharacter(seat);
            if (character is null || TokenStore.Get(character.CharacterId) is null) continue;

            if (_settings.SeatHealthAlertPodded)
                await CheckPoddedAsync(seat, character.CharacterId, character.CharacterName, assignment);

            // Only meaningful when the seat still has a live window -- no window is already the
            // ordinary/expected offline state, not a surprise silent disconnect.
            if (_settings.SeatHealthAlertDisconnected && FindSeatWindow(seat) is not null)
                await CheckDisconnectedAsync(seat, character.CharacterId, character.CharacterName, assignment);
        }
    }

    private async System.Threading.Tasks.Task CheckPoddedAsync(int seat, long characterId, string characterName, SlotAssignment assignment)
    {
        try
        {
            var ship = await CharacterInfoShared.GetShipAsync(characterId, forceRefresh: false, CancellationToken.None);
            if (ship is null) return;

            var wasCapsule = _lastKnownShipTypeId.TryGetValue(seat, out var prevType) && CapsuleTypeIds.Contains(prevType);
            var isCapsule = CapsuleTypeIds.Contains(ship.ShipTypeId);
            var hadPriorObservation = _lastKnownShipTypeId.ContainsKey(seat);
            _lastKnownShipTypeId[seat] = ship.ShipTypeId;

            // Only fire on a genuine transition INTO a capsule, and never on the first observation
            // after (re)linking/rebuild -- otherwise a character already sitting in a pod at startup
            // would falsely alert as "just podded".
            if (!hadPriorObservation || wasCapsule || !isCapsule) return;

            var loc = await CharacterInfoShared.GetLocationAsync(characterId, forceRefresh: false, CancellationToken.None);
            if (loc?.StationId is not null || loc?.StructureId is not null)
                return; // docked -- an ordinary ship swap in the hangar, not a death

            var confirmed = await ZkillSystemActivityService.HasRecentLossAsync(characterId, PoddedLossLookbackSeconds, CancellationToken.None);
            if (confirmed)
                ShowToast(characterName, "Podded -- confirmed via zKillboard", "#EF4444", assignment);
            else
                ShowToast(characterName, "Now in a capsule (no matching zKillboard loss yet -- may be a deliberate bare-pod flight)", "#F59E0B", assignment);
        }
        catch (Exception ex)
        {
            Log.Warn($"Seat health (podded check) failed for {characterName}: {ex}");
        }
    }

    private async System.Threading.Tasks.Task CheckDisconnectedAsync(int seat, long characterId, string characterName, SlotAssignment assignment)
    {
        try
        {
            var online = await CharacterInfoShared.GetOnlineAsync(characterId, forceRefresh: false, CancellationToken.None);
            if (online is null) return; // e.g. 403 -- character linked before the online scope existed

            var hadPriorObservation = _lastKnownOnline.TryGetValue(seat, out var wasOnline);
            _lastKnownOnline[seat] = online.Online;

            // Edge-triggered on true -> false only: never alert on the first observation (avoids a
            // false positive right after startup/relink), and never repeat while it stays offline.
            if (hadPriorObservation && wasOnline && !online.Online)
                ShowToast(characterName, "Window is open but the game session isn't logged in", "#F59E0B", assignment);
        }
        catch (Exception ex)
        {
            Log.Warn($"Seat health (disconnected check) failed for {characterName}: {ex}");
        }
    }
}

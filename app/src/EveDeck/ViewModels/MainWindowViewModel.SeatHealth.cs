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

    // -- Not polling ESI while EVE is down -------------------------------------------------------
    // ESI goes down with the cluster at downtime, so every seat's podded check fails with a 502/504
    // and logs a warning: 55 of them in a five-minute stretch on an ordinary day. (The disconnected
    // check is naturally quiet then -- it only runs for a seat with a live window, and the clients are
    // closed.) Two layers handle it: a scheduled skip for the configured DT window, and an automatic
    // backoff for everything else, because a patch day can keep ESI away for an hour or more and no
    // fixed window would cover that without also being wrong every normal day.
    private bool _seatHealthSkippingForDowntime;

    // Inside the downtime window the checks do not simply go dark for the whole 30 minutes: an
    // ordinary restart is over in about five, and waiting out the rest would drop real podded
    // coverage exactly when everyone logs back in. So probe on this interval, and the first probe
    // that gets an answer resumes normal 30s checking for the remainder of the window. A probe that
    // fails is silent -- it is the expected result while EVE is still down.
    private static readonly TimeSpan DowntimeProbeInterval = TimeSpan.FromMinutes(3);
    private DateTimeOffset _nextDowntimeProbe = DateTimeOffset.MinValue;
    private bool _esiAnsweredInsideDowntimeWindow;

    private const int SeatHealthMaxBackoffMinutes = 30;
    private DateTimeOffset _seatHealthBackoffUntil = DateTimeOffset.MinValue;
    private int _seatHealthOutageTicks;
    private bool _tickSawEsiOutage;
    private bool _tickSawEsiSuccess;

    // "ESI is not answering" as opposed to "this request was wrong". A missing StatusCode is a
    // transport-level failure (DNS/socket), which is what the cluster going away looks like from here.
    internal static bool IsEsiUnavailable(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException hre => hre.StatusCode is null
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout
            or System.Net.HttpStatusCode.RequestTimeout,
        TaskCanceledException => true,   // HttpClient's own timeout
        _ => false,
    };

    // Called from the per-seat check's catch. True means "this was an outage, not a real error", so
    // the caller skips the warning -- one line per tick beats one per seat per tick.
    private bool NoteEsiFailure(Exception ex)
    {
        if (!IsEsiUnavailable(ex)) return false;
        _tickSawEsiOutage = true;
        return true;
    }

    private void NoteEsiSuccess() => _tickSawEsiSuccess = true;

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
        var nowUtc = DateTimeOffset.UtcNow;

        // Scheduled: EVE's daily downtime.
        var inDowntime = IsWithinDowntimeWindow(nowUtc, _settings.DowntimeUtcTime, _settings.SeatHealthDowntimeSkipMinutes);
        if (inDowntime != _seatHealthSkippingForDowntime)
        {
            _seatHealthSkippingForDowntime = inDowntime;
            if (inDowntime)
            {
                _esiAnsweredInsideDowntimeWindow = false;
                // Not immediately: ESI is reliably gone the moment downtime starts, so the first
                // probe is worth nothing. Wait one interval before asking.
                _nextDowntimeProbe = nowUtc + DowntimeProbeInterval;
                Log.Info($"Downtime: pausing seat health checks (probing every {DowntimeProbeInterval.TotalMinutes:0} minutes, up to {_settings.SeatHealthDowntimeSkipMinutes}).");
            }
            else
            {
                _esiAnsweredInsideDowntimeWindow = false;
                Log.Info("Downtime window over; resuming seat health checks.");
            }
        }

        // Quiet inside the window, except for a periodic probe -- until one answers, after which
        // normal checking resumes for whatever is left of the window.
        if (inDowntime && !_esiAnsweredInsideDowntimeWindow)
        {
            if (nowUtc < _nextDowntimeProbe) return;
            _nextDowntimeProbe = nowUtc + DowntimeProbeInterval;
        }

        // Unscheduled: ESI still away (extended downtime, patch day, an ESI incident).
        if (nowUtc < _seatHealthBackoffUntil) return;

        _tickSawEsiOutage = false;
        _tickSawEsiSuccess = false;

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

        // One backoff step per TICK, not per failed seat -- five seats failing together is one
        // outage, not five. Any success in the tick means ESI is reachable and clears the backoff.
        if (_tickSawEsiSuccess)
        {
            if (inDowntime && !_esiAnsweredInsideDowntimeWindow)
            {
                _esiAnsweredInsideDowntimeWindow = true;
                Log.Info("ESI is answering again; resuming seat health checks before the downtime window ends.");
            }
            if (_seatHealthOutageTicks > 0)
            {
                _seatHealthOutageTicks = 0;
                _seatHealthBackoffUntil = DateTimeOffset.MinValue;
                Log.Info("ESI is responding again; seat health checks resumed.");
            }
        }
        // A failed probe inside the window is the expected state, not news -- the window is already
        // governing the retry cadence, so it must not also trip the generic outage backoff.
        else if (_tickSawEsiOutage && !inDowntime)
        {
            _seatHealthOutageTicks++;
            var minutes = Math.Min(SeatHealthMaxBackoffMinutes, 5 * _seatHealthOutageTicks);
            _seatHealthBackoffUntil = DateTimeOffset.UtcNow.AddMinutes(minutes);
            if (_seatHealthOutageTicks == 1)
                Log.Info($"ESI is not responding (EVE restarting?); pausing seat health checks, retrying in {minutes} minutes.");
        }
    }

    private async System.Threading.Tasks.Task CheckPoddedAsync(int seat, long characterId, string characterName, SlotAssignment assignment)
    {
        try
        {
            var ship = await CharacterInfoShared.GetShipAsync(characterId, forceRefresh: false, CancellationToken.None);
            NoteEsiSuccess();   // the call completed, so ESI is reachable -- clears any outage backoff
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
            // An outage is reported once for the whole tick by OnSeatHealthTick, not once per seat.
            if (NoteEsiFailure(ex)) return;
            Log.Warn($"Seat health (podded check) failed for {characterName}: {ex}");
        }
    }

    private async System.Threading.Tasks.Task CheckDisconnectedAsync(int seat, long characterId, string characterName, SlotAssignment assignment)
    {
        try
        {
            var online = await CharacterInfoShared.GetOnlineAsync(characterId, forceRefresh: false, CancellationToken.None);
            NoteEsiSuccess();
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
            if (NoteEsiFailure(ex)) return;
            Log.Warn($"Seat health (disconnected check) failed for {characterName}: {ex}");
        }
    }
}

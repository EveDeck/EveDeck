using System.Threading;
using System.Windows.Threading;
using EveDeck.Models;
using EveDeck.Views;

namespace EveDeck.ViewModels;

// Jump status for each corner preview (added 2026-07-28): fatigue ("F", amber) and jump-reactivation
// cooldown ("R", cyan) countdowns, shown only while a linked seat's value is actually running.
// 2026-08-14 they moved OUT of two chips in the tile's top-left corner and INTO the character pill,
// on a second line under the name/location row. This partial owns the settings-bound toggle, the
// periodic ESI poll that computes badge state (fatigue is already fetched elsewhere for the info
// flyout, but that's fetch-on-open only -- these badges need it kept fresh in the background), and
// the reactivation-timer formula. Rendering lives on LabelSurfaceWindow (see SetJumpLine), which
// owns placement now that the countdowns are part of the pill.
//
// 2026-08-14 rework -- the poll used to store PRE-RENDERED text and paint straight from the fetch,
// which made everything downstream only as fresh as the last 30s tick: a badge could survive up to
// half a minute past expiry and hover text was frozen at whatever the last fetch computed. State is
// now the DEADLINES, and a separate one-second display tick renders from them locally. That makes
// the countdown exact and live while REDUCING ESI traffic rather than increasing it, and it means an
// ESI outage degrades gracefully -- fatigue keeps ticking down correctly on screen from the last
// known deadline, because real-world time is all that is needed to keep counting.
public sealed partial class MainWindowViewModel
{
    // GET /characters/{id}/skills/ type id for Jump Drive Calibration -- verified 2026-07-28 via a
    // live ESI /universe/ids/ call (POST {"names":["Jump Drive Calibration"]} -> inventory_types
    // id 21611). Each trained level shaves a minute off the base 10-minute reactivation cooldown.
    private const int JumpDriveCalibrationTypeId = 21611;

    // How often to ASK ESI. CharacterInfoService caches for 60s, so this is really "how fast a brand
    // new jump is noticed", not how fast the badge updates -- the display tick below owns that.
    private readonly DispatcherTimer _jumpStatusTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    // Local render tick. Costs no network at all: it re-renders the countdowns from cached deadlines.
    private readonly DispatcherTimer _jumpDisplayTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Deadlines, not text -- see the class comment. Null means "not active".
    private sealed record JumpBadgeState(DateTimeOffset? FatigueUntil, DateTimeOffset? ReactivationUntil)
    {
        public bool FatigueActive(DateTimeOffset now) => FatigueUntil is { } u && u > now;
        public bool ReactivationActive(DateTimeOffset now) => ReactivationUntil is { } u && u > now;
    }
    private readonly Dictionary<int, JumpBadgeState> _jumpStatusByPosition = new();

    // Jump Drive Calibration level, cached FAR longer than CharacterInfoService's generic 60s TTL.
    // The old code called GetSkillsAsync on every poll -- a full skills fetch per character per
    // minute, forever, to read one number that changes on the order of days. Caching it stale is
    // also the SAFE direction: a skill level only ever goes up, and a level that is too low yields a
    // longer cooldown, so a stale value can only ever over-report time remaining, never under-report.
    private static readonly TimeSpan JdcLevelTtl = TimeSpan.FromHours(6);
    private readonly Dictionary<long, (int Level, DateTimeOffset FetchedAt)> _jdcLevelCache = new();

    // Outage handling, mirroring MainWindowViewModel.SeatHealth's pattern but deliberately simpler:
    // a missed jump-badge refresh is cosmetic (the countdown keeps running locally), so this only
    // needs to stop the log flood and the pointless traffic, not preserve alert coverage. Reuses
    // SeatHealthDowntimeSkipMinutes as the shared "how long EVE is down" figure rather than adding a
    // second setting that would mean the same thing.
    private const int JumpStatusMaxBackoffMinutes = 30;
    private DateTimeOffset _jumpStatusBackoffUntil = DateTimeOffset.MinValue;
    private int _jumpStatusOutageTicks;
    private bool _jumpStatusSkippingForDowntime;

    public bool CornerOverlayShowJumpBadges
    {
        get => _settings.CornerOverlayShowJumpBadges;
        set
        {
            if (_settings.CornerOverlayShowJumpBadges == value) return;
            _settings.CornerOverlayShowJumpBadges = value;
            OnPropertyChanged();
            Save();
            // Rebuild so the label surface (which the badges draw on) gets created if it didn't
            // already exist purely for labels/the info button -- same reasoning as the
            // CornerOverlayInfoButtonEnabled toggle.
            if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    // Starts both timers and takes one immediate reading -- DispatcherTimer does not fire on Start(),
    // so without this the badges stayed blank for the first 30 seconds of every overlay rebuild
    // (and rebuilds happen on nearly every settings tweak).
    private void StartJumpStatus()
    {
        if (!_settings.CornerOverlayShowJumpBadges) return;
        _jumpStatusTimer.Start();
        _jumpDisplayTimer.Start();
        OnJumpStatusTick(null, EventArgs.Empty);
    }

    // Stops the polls and discards all cached/displayed badge state -- called from StopCornerOverlays
    // (both on rebuild and on app shutdown), since the surfaces the badges draw on are about to be
    // destroyed and any cached state refers to positions that may no longer mean the same seat.
    private void StopJumpStatus()
    {
        _jumpStatusTimer.Stop();
        _jumpDisplayTimer.Stop();
        _jumpStatusByPosition.Clear();
        // _jdcLevelCache is deliberately NOT cleared: it is keyed by character, not by position, and
        // survives an overlay rebuild perfectly well. Clearing it here would refetch every skill
        // sheet on every settings tweak, which is exactly the traffic this cache exists to avoid.
    }

    // -- Formatting -------------------------------------------------------------------------------

    // Compact enough to sit on the pill's second line next to its sibling countdown. Widest output is
    // "9d23h" (5 chars, plus the glyph and a space). Days are capped rather than wrapped: fatigue can
    // technically run to weeks, and "9d+" reads better on a narrow pill than a number that keeps
    // growing and pushes the reactivation countdown out of the tile.
    private static string FormatBadgeCountdown(TimeSpan left)
    {
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        if (left.TotalDays >= 10) return "9d+";
        if (left.TotalHours >= 24) return $"{left.Days}d{left.Hours:0}h";
        if (left.TotalMinutes >= 60) return $"{(int)left.TotalHours}h{left.Minutes:00}";
        return $"{left.Minutes}:{left.Seconds:00}";
    }

    // -- Geometry / visibility --------------------------------------------------------------------

    // Every rect that can carry jump badges: the corner tiles, plus each group's master/center rect in
    // layouts where that area is the real EVE window instead of a preview tile (Center Master family),
    // which therefore has no _cornerRects entry. Badges are shown on the master too (2026-08-14 --
    // previously skipped, on the info badge's "you're already flying that one" reasoning, but fatigue
    // and jump reactivation are exactly the numbers you want on the character you ARE flying).
    private IEnumerable<(int Position, WindowRect Rect)> JumpBadgeTargets()
    {
        foreach (var (position, rect) in _cornerRects) yield return (position, rect);
        foreach (var (position, rect) in _groupCenterRects)
            if (!_cornerRects.ContainsKey(position)) yield return (position, rect);
    }

    // A master rect with no tile has no _cornerSourceHandles entry to read liveness from, so ask the
    // seat directly there; corner tiles keep using the handle, which also accounts for a tile
    // deliberately blanked (HideActiveSeatTile, login screen) rather than merely absent.
    private bool JumpBadgeTargetLive(int position) =>
        _cornerRects.ContainsKey(position)
            ? _cornerSourceHandles.GetValueOrDefault(position) != 0
            : FindSeatWindow(OccupantAtPosition(position)) is not null;

    // Restores whichever jump badges a seat's live state calls for -- used after a tile stops being
    // hover-zoomed (mirrors RefreshInfoBadgeVisibility), since ExecuteHoverPeek's Zoom branch hides
    // both badges unconditionally while magnified.
    private void RefreshJumpBadgeVisibility(int position)
    {
        if (_labelSurface is null) return;
        if (!_cornerRects.TryGetValue(position, out var rect)
            && !_groupCenterRects.TryGetValue(position, out rect)) return;
        PaintJumpBadges(position, rect, DateTimeOffset.UtcNow);
    }

    // `rect` is unused now that the countdowns live inside the pill (which owns its own placement),
    // but every caller already has it and the parameter keeps this callable from the same places if
    // the rendering ever moves back out to tile-anchored chrome.
    private void PaintJumpBadges(int position, WindowRect rect, DateTimeOffset now)
    {
        if (_labelSurface is null) return;
        var live = JumpBadgeTargetLive(position);
        _jumpStatusByPosition.TryGetValue(position, out var state);

        var fatigue = live && state?.FatigueActive(now) == true;
        var reactivation = live && state?.ReactivationActive(now) == true;
        _labelSurface.SetJumpLine(position,
            fatigue ? $"F {FormatBadgeCountdown(state!.FatigueUntil!.Value - now)}" : null,
            reactivation ? $"R {FormatBadgeCountdown(state!.ReactivationUntil!.Value - now)}" : null);
    }

    // -- Local display tick -----------------------------------------------------------------------

    // Pure local re-render: no ESI, no allocation beyond the two label strings, and it is what makes
    // a badge disappear the exact second its timer expires instead of at the next 30s poll.
    private void OnJumpDisplayTick(object? sender, EventArgs e)
    {
        if (_labelSurface is null || _jumpStatusByPosition.Count == 0) return;
        var now = DateTimeOffset.UtcNow;

        foreach (var (position, rect) in JumpBadgeTargets())
            PaintJumpBadges(position, rect, now);
    }

    // -- ESI poll ---------------------------------------------------------------------------------

    private async void OnJumpStatusTick(object? sender, EventArgs e)
    {
        if (_labelSurface is null) return;
        var now = DateTimeOffset.UtcNow;

        // Scheduled: EVE's daily downtime. ESI dies with the cluster, so every fetch in that window
        // would fail and log. Unlike seat health this does not probe to resume early -- there is no
        // alert coverage being lost, and the on-screen countdowns keep running from cached deadlines.
        var inDowntime = IsWithinDowntimeWindow(now, _settings.DowntimeUtcTime, _settings.SeatHealthDowntimeSkipMinutes);
        if (inDowntime != _jumpStatusSkippingForDowntime)
        {
            _jumpStatusSkippingForDowntime = inDowntime;
            Log.Info(inDowntime
                ? "Downtime: pausing jump-status refresh (badges keep counting down locally)."
                : "Downtime window over; resuming jump-status refresh.");
        }
        if (inDowntime) return;

        // Unscheduled outage (patch day, network down). Same shape as seat health's backoff.
        if (now < _jumpStatusBackoffUntil) return;

        var sawOutage = false;
        var sawSuccess = false;

        // Materialised up front: the loop awaits, and the underlying rect dictionaries can be
        // rebuilt (layout re-apply, seat swap) while a fetch is in flight.
        foreach (var (position, rect) in JumpBadgeTargets().ToList())
        {
            var seat = OccupantAtPosition(position);
            var character = ResolveSeatCharacter(seat);
            if (character is null || TokenStore.Get(character.CharacterId) is null)
            {
                _jumpStatusByPosition.Remove(position);
                _labelSurface.SetJumpLine(position, null, null);
                continue;
            }

            try
            {
                var fatigue = await CharacterInfoShared.GetFatigueAsync(character.CharacterId, forceRefresh: false, CancellationToken.None);
                sawSuccess = true;

                DateTimeOffset? fatigueUntil = fatigue?.JumpFatigueExpireDate;
                DateTimeOffset? reactivationUntil = null;
                if (fatigue?.LastJumpDate is { } lastJump)
                {
                    var level = await GetJumpDriveCalibrationLevelAsync(character.CharacterId, now);
                    reactivationUntil = lastJump + TimeSpan.FromMinutes(10 - level);
                }

                _jumpStatusByPosition[position] = new JumpBadgeState(fatigueUntil, reactivationUntil);
                PaintJumpBadges(position, rect, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                // An outage is reported once for the whole tick, not once per seat -- the flood this
                // avoids is the exact one that buried seat health's real errors (see 2026-08-12).
                if (IsEsiUnavailable(ex)) { sawOutage = true; continue; }
                Log.Warn($"Jump-status ESI fetch failed for {character.CharacterName}: {ex}");
            }
        }

        if (sawSuccess)
        {
            if (_jumpStatusOutageTicks > 0)
            {
                _jumpStatusOutageTicks = 0;
                _jumpStatusBackoffUntil = DateTimeOffset.MinValue;
                Log.Info("ESI is responding again; jump-status refresh resumed.");
            }
        }
        else if (sawOutage)
        {
            _jumpStatusOutageTicks++;
            var minutes = Math.Min(JumpStatusMaxBackoffMinutes, 5 * _jumpStatusOutageTicks);
            _jumpStatusBackoffUntil = DateTimeOffset.UtcNow.AddMinutes(minutes);
            if (_jumpStatusOutageTicks == 1)
                Log.Info($"ESI is not responding; pausing jump-status refresh, retrying in {minutes} minutes.");
        }
    }

    // Skill level 0 is assumed whenever it cannot be resolved (never linked with the skills scope, a
    // failed fetch, or genuinely untrained) -- the full 10-minute cooldown -- so this never
    // UNDER-reports how long is actually left. A failure here is cached briefly as level 0 rather
    // than retried every tick, for the same traffic reason the cache exists.
    private async Task<int> GetJumpDriveCalibrationLevelAsync(long characterId, DateTimeOffset now)
    {
        if (_jdcLevelCache.TryGetValue(characterId, out var hit) && now - hit.FetchedAt < JdcLevelTtl)
            return hit.Level;

        var skills = await CharacterInfoShared.GetSkillsAsync(characterId, forceRefresh: false, CancellationToken.None);
        var level = skills?.Skills?.FirstOrDefault(s => s.SkillId == JumpDriveCalibrationTypeId)?.ActiveSkillLevel ?? 0;
        _jdcLevelCache[characterId] = (level, now);
        return level;
    }
}

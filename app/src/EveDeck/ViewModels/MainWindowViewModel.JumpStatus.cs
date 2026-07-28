using System.Threading;
using System.Windows.Threading;
using EveDeck.Views;

namespace EveDeck.ViewModels;

// Jump-status badges on each corner preview (added 2026-07-28): small "F" (fatigue, amber) and "R"
// (jump-reactivation cooldown, cyan) badges, shown only while a linked seat's active value is
// nonzero; hover for the exact remaining time. This partial owns the settings-bound toggle, the
// periodic ESI poll that computes badge state (fatigue is already fetched elsewhere for the info
// flyout, but that's fetch-on-open only -- these badges need it kept fresh in the background), and
// the reactivation-timer formula. Badge geometry/rendering live on LabelSurfaceWindow
// (see OverlayJumpBadge); hover-to-text hooks into MainWindowViewModel.CornerOverlays.cs's existing
// tile cursor-poll, which reads the state cache this partial maintains.
public sealed partial class MainWindowViewModel
{
    // GET /characters/{id}/skills/ type id for Jump Drive Calibration -- verified 2026-07-28 via a
    // live ESI /universe/ids/ call (POST {"names":["Jump Drive Calibration"]} -> inventory_types
    // id 21611). Each trained level shaves a minute off the base 10-minute reactivation cooldown.
    private const int JumpDriveCalibrationTypeId = 21611;

    private readonly DispatcherTimer _jumpStatusTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private OverlayHoverTipWindow? _jumpHoverTip;
    private bool _jumpHoverActive;

    private sealed record JumpBadgeState(bool FatigueActive, string FatigueText, bool ReactivationActive, string ReactivationText);
    private readonly Dictionary<int, JumpBadgeState> _jumpStatusByPosition = new();

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

    // Stops the poll and discards all cached/displayed badge state -- called from StopCornerOverlays
    // (both on rebuild and on app shutdown), since the surfaces the badges draw on are about to be
    // destroyed and any cached state refers to positions that may no longer mean the same seat.
    private void StopJumpStatus()
    {
        _jumpStatusTimer.Stop();
        _jumpStatusByPosition.Clear();
        _jumpHoverActive = false;
        try { _jumpHoverTip?.Close(); } catch { /* window may already be closed */ }
        _jumpHoverTip = null;
    }

    private void ShowJumpHoverTip(int physX, int physY, int physSize, string text)
    {
        if (_jumpHoverTip is null)
        {
            _jumpHoverTip = new OverlayHoverTipWindow(_overlayDpiScale);
            _jumpHoverTip.SetOwner(_labelSurface?.Handle ?? _tileSurface?.Handle ?? 0);
        }
        _jumpHoverTip.ShowAt(physX, physY, physSize, text);
    }

    private void HideJumpHoverTip()
    {
        if (_jumpHoverTip is null) return;
        try { _jumpHoverTip.Hide(); } catch { /* window may already be closed */ }
    }

    // Restores whichever jump badges a seat's live state calls for -- used after a tile stops being
    // hover-zoomed (mirrors RefreshInfoBadgeVisibility), since ExecuteHoverPeek's Zoom branch hides
    // both badges unconditionally while magnified.
    private void RefreshJumpBadgeVisibility(int position)
    {
        if (_labelSurface is null || !_cornerRects.TryGetValue(position, out var rect)) return;
        var live = _cornerSourceHandles.GetValueOrDefault(position) != 0;
        _jumpStatusByPosition.TryGetValue(position, out var state);
        _labelSurface.SetFatigueBadge(position, rect, live && state?.FatigueActive == true);
        _labelSurface.SetReactivationBadge(position, rect, live && state?.ReactivationActive == true);
    }

    private async void OnJumpStatusTick(object? sender, EventArgs e)
    {
        if (_labelSurface is null) return;
        var now = DateTimeOffset.UtcNow;

        foreach (var position in _cornerRects.Keys.ToList())
        {
            // The centered/master character is the one actively being played right now -- same
            // reasoning the info badge already uses to skip this position (see StartCornerOverlays).
            if (IsGroupCenterPosition(position)) continue;
            if (!_cornerRects.TryGetValue(position, out var rect)) continue;

            var seat = OccupantAtPosition(position);
            var character = ResolveSeatCharacter(seat);
            if (character is null || TokenStore.Get(character.CharacterId) is null)
            {
                _jumpStatusByPosition.Remove(position);
                _labelSurface.SetFatigueBadge(position, rect, false);
                _labelSurface.SetReactivationBadge(position, rect, false);
                continue;
            }

            try
            {
                var fatigue = await CharacterInfoShared.GetFatigueAsync(character.CharacterId, forceRefresh: false, CancellationToken.None);
                var fatigueActive = fatigue?.JumpFatigueExpireDate is { } expire && expire > now;
                var fatigueText = fatigueActive ? $"Jump fatigue: {HumanizeDuration(fatigue!.JumpFatigueExpireDate!.Value - now)}" : "";

                var reactivationActive = false;
                var reactivationText = "";
                if (fatigue?.LastJumpDate is { } lastJump)
                {
                    // Skill not trained (never linked with the skills scope, or genuinely 0) is treated
                    // as level 0 -- the full 10-minute cooldown -- so this never UNDER-reports how long
                    // is actually left.
                    var skills = await CharacterInfoShared.GetSkillsAsync(character.CharacterId, forceRefresh: false, CancellationToken.None);
                    var level = skills?.Skills?.FirstOrDefault(s => s.SkillId == JumpDriveCalibrationTypeId)?.ActiveSkillLevel ?? 0;
                    var deadline = lastJump + TimeSpan.FromMinutes(10 - level);
                    reactivationActive = deadline > now;
                    if (reactivationActive) reactivationText = $"Jump reactivation: {HumanizeDuration(deadline - now)}";
                }

                _jumpStatusByPosition[position] = new JumpBadgeState(fatigueActive, fatigueText, reactivationActive, reactivationText);
                _labelSurface.SetFatigueBadge(position, rect, fatigueActive);
                _labelSurface.SetReactivationBadge(position, rect, reactivationActive);
            }
            catch (Exception ex)
            {
                Log.Warn($"Jump-status ESI fetch failed for {character.CharacterName}: {ex}");
            }
        }
    }
}

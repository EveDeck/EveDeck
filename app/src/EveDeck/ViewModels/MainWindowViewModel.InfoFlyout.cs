using System.Threading;
using EveDeck.Models;
using EveDeck.Services;
using EveDeck.Utilities;
using EveDeck.Views;

namespace EveDeck.ViewModels;

// Preview info flyout (added 2026-07-24): the small "i" badge on each corner preview opens a compact
// card of live ESI facts for that seat's character (ship / system / training / jump fatigue). This
// partial owns the settings-bound toggles, the badge-click handler, and the async ESI fetch that fills
// the card. The badge geometry + hit-testing live across TileSurfaceWindow/LabelSurfaceWindow (see
// OverlayInfoButton); this file just reacts to InfoButtonClicked and drives InfoFlyoutWindow.
public sealed partial class MainWindowViewModel
{
    private InfoFlyoutWindow? _infoFlyout;
    private int _infoFlyoutPosition = -1;
    private double _overlayDpiScale = 1.0;

    private ZkillSystemActivityService? _zkillActivity;
    private ZkillSystemActivityService ZkillActivityShared => _zkillActivity ??= new ZkillSystemActivityService();

    // ── Settings-bound toggles ──────────────────────────────────────────────────────────────────────

    public bool CornerOverlayInfoButtonEnabled
    {
        get => _settings.CornerOverlayInfoButtonEnabled;
        set
        {
            if (_settings.CornerOverlayInfoButtonEnabled == value) return;
            _settings.CornerOverlayInfoButtonEnabled = value;
            OnPropertyChanged();
            Save();
            // Rebuild the overlay so badges appear/disappear immediately (the badge lives on the label
            // surface, whose existence now also depends on this toggle -- see StartCornerOverlays).
            if (_settings.CornerOverlaysEnabled && CornerOverlaysLive) StartCornerOverlays();
        }
    }

    public bool InfoFlyoutShowWallet
    {
        get => _settings.InfoFlyoutShowWallet;
        set { if (_settings.InfoFlyoutShowWallet == value) return; _settings.InfoFlyoutShowWallet = value; OnPropertyChanged(); Save(); }
    }

    public bool InfoFlyoutShowShip
    {
        get => _settings.InfoFlyoutShowShip;
        set { if (_settings.InfoFlyoutShowShip == value) return; _settings.InfoFlyoutShowShip = value; OnPropertyChanged(); Save(); }
    }

    public bool InfoFlyoutShowLocation
    {
        get => _settings.InfoFlyoutShowLocation;
        set { if (_settings.InfoFlyoutShowLocation == value) return; _settings.InfoFlyoutShowLocation = value; OnPropertyChanged(); Save(); }
    }

    public bool InfoFlyoutShowFatigue
    {
        get => _settings.InfoFlyoutShowFatigue;
        set { if (_settings.InfoFlyoutShowFatigue == value) return; _settings.InfoFlyoutShowFatigue = value; OnPropertyChanged(); Save(); }
    }

    public bool InfoFlyoutShowSkill
    {
        get => _settings.InfoFlyoutShowSkill;
        set { if (_settings.InfoFlyoutShowSkill == value) return; _settings.InfoFlyoutShowSkill = value; OnPropertyChanged(); Save(); }
    }

    public bool InfoFlyoutShowDanger
    {
        get => _settings.InfoFlyoutShowDanger;
        set { if (_settings.InfoFlyoutShowDanger == value) return; _settings.InfoFlyoutShowDanger = value; OnPropertyChanged(); Save(); }
    }

    public bool InfoFlyoutShowSp
    {
        get => _settings.InfoFlyoutShowSp;
        set { if (_settings.InfoFlyoutShowSp == value) return; _settings.InfoFlyoutShowSp = value; OnPropertyChanged(); Save(); }
    }

    public bool InfoFlyoutShowPlanets
    {
        get => _settings.InfoFlyoutShowPlanets;
        set { if (_settings.InfoFlyoutShowPlanets == value) return; _settings.InfoFlyoutShowPlanets = value; OnPropertyChanged(); Save(); }
    }

    // ── Badge click ─────────────────────────────────────────────────────────────────────────────────

    // Fired from TileSurfaceWindow on the UI thread (same as OnCornerTileClicked). Toggles the card
    // closed if it's already showing this tile, otherwise opens it and fills it asynchronously.
    private async void OnInfoButtonClicked(int position)
    {
        if (_infoFlyout is not null && _infoFlyoutPosition == position) { CloseInfoFlyout(); return; }
        CloseInfoFlyout();

        // Settle any active hover-zoom/peek first so the card opens over a static preview, the badge
        // returns to the tile's un-zoomed corner, and the zoom pump's topmost re-assert can't cover
        // the card. MaintainCornerOverlays then keeps hover suppressed while the card stays open.
        if (_cursorOverPosition >= 0) { OnCornerTileHoverLeft(_cursorOverPosition); _cursorOverPosition = -1; }

        if (!_cornerRects.TryGetValue(position, out var rect)) return;

        var seat = OccupantAtPosition(position);
        var character = ResolveSeatCharacter(seat);
        var title = StreamSafe ? $"Alt {seat}" : (character?.CharacterName ?? SeatLabel(seat));

        // Anchor the card just below the badge's bottom edge.
        var badge = OverlayInfoButton.RectFor(new System.Drawing.Rectangle(rect.X, rect.Y, rect.Width, rect.Height));
        var flyout = new InfoFlyoutWindow(badge.X, badge.Bottom + 2, _overlayDpiScale, title);
        // Own it to the label surface (which sits above the tile surface) so the overlay's periodic
        // topmost re-assert can't bury the card. The badge only exists when the label surface does.
        flyout.SetOwner(_labelSurface?.Handle ?? (_tileSurface?.Handle ?? 0));
        _infoFlyout = flyout;
        _infoFlyoutPosition = position;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_infoFlyout, flyout)) { _infoFlyout = null; _infoFlyoutPosition = -1; }
        };
        flyout.SetLines(new[] { "Loading…" });
        flyout.Show();

        if (character is null)
        {
            flyout.SetLines(new[] { "No linked character on this seat." });
            return;
        }
        if (TokenStore.Get(character.CharacterId) is null)
        {
            flyout.SetLines(new[] { $"{title} is not linked to ESI." });
            return;
        }

        await PopulateInfoFlyoutAsync(flyout, position, character.CharacterId);
    }

    private async System.Threading.Tasks.Task PopulateInfoFlyoutAsync(InfoFlyoutWindow flyout, int position, long characterId)
    {
        var lines = new List<string>();
        List<string>? planetLines = null;
        var now = System.DateTimeOffset.UtcNow;
        var ct = CancellationToken.None;
        try
        {
            // Wallet sits directly under the character name (the flyout title). Hidden in stream-safe mode.
            if (_settings.InfoFlyoutShowWallet && !StreamSafe)
            {
                var isk = await CharacterInfoShared.GetWalletAsync(characterId, ct);
                if (isk.HasValue) lines.Add($"Wallet: {FormatIsk(isk.Value)}");
            }

            if (_settings.InfoFlyoutShowShip)
            {
                var ship = await CharacterInfoShared.GetShipAsync(characterId, forceRefresh: false, ct);
                if (ship is not null)
                {
                    var type = await EsiTypeCacheShared.GetTypeAsync(ship.ShipTypeId, ct);
                    lines.Add($"Ship: {type.Name}");
                }
            }

            // Location is needed by both the System/Docked lines and the danger line -- fetch once.
            Models.EsiCharacterLocation? loc = null;
            if (_settings.InfoFlyoutShowLocation || _settings.InfoFlyoutShowDanger)
                loc = await CharacterInfoShared.GetLocationAsync(characterId, forceRefresh: false, ct);

            // System + docked reveal WHERE the character is, so both are suppressed in stream-safe mode.
            // The danger line below stays (it's just kill counts, no location name).
            if (_settings.InfoFlyoutShowLocation && !StreamSafe && loc is not null)
            {
                var sys = await EsiTypeCacheShared.GetSystemInfoAsync(loc.SolarSystemId, ct);
                var region = string.IsNullOrEmpty(sys.RegionName) ? "" : $" · {sys.RegionName}";
                lines.Add($"System: {sys.Name} {FormatSecurity(sys.Security)}{region}");

                if (loc.StationId is long stationId)
                {
                    var station = await EsiTypeCacheShared.GetStationNameAsync(stationId, ct);
                    lines.Add(string.IsNullOrEmpty(station) ? "Docked" : $"Docked: {station}");
                }
                else if (loc.StructureId is long structureId)
                {
                    var structure = await CharacterInfoShared.GetStructureNameAsync(structureId, characterId, ct);
                    lines.Add(string.IsNullOrEmpty(structure) ? "Docked at structure" : $"Docked: {structure}");
                }
            }

            if (_settings.InfoFlyoutShowDanger && loc is not null)
            {
                var (kills1h, kills24h) = await ZkillActivityShared.GetActivityAsync(loc.SolarSystemId, ct);
                var s1 = kills1h < 0 ? "?" : kills1h.ToString();
                var s24 = kills24h < 0 ? "?" : kills24h.ToString();
                // "in system" so it's unmistakably the whole system's activity, not the character's own kills.
                lines.Add($"Kills in system: {s1} (1h) · {s24} (24h)");
            }

            if (_settings.InfoFlyoutShowSkill)
            {
                var queue = await CharacterInfoShared.GetSkillQueueAsync(characterId, forceRefresh: false, ct);
                var pending = queue?.Where(e => e.FinishDate.HasValue && e.FinishDate.Value > now)
                    .OrderBy(e => e.QueuePosition).ToList() ?? new System.Collections.Generic.List<Models.EsiSkillQueueEntry>();
                var active = pending.FirstOrDefault();
                if (active is null)
                {
                    lines.Add("Training: none");
                }
                else
                {
                    var skill = await EsiTypeCacheShared.GetTypeAsync(active.SkillId, ct);
                    lines.Add($"Training: {skill.Name} {RomanLevel(active.FinishedLevel)} - {HumanizeDuration(active.FinishDate!.Value - now)}");
                    // Queue summary directly under the currently-training line.
                    var endFinish = pending.Max(e => e.FinishDate!.Value);
                    lines.Add($"Queue: {pending.Count} skill{(pending.Count == 1 ? "" : "s")} · {HumanizeDuration(endFinish - now)} left");
                }
            }

            if (_settings.InfoFlyoutShowSp)
            {
                var skills = await CharacterInfoShared.GetSkillsAsync(characterId, forceRefresh: false, ct);
                if (skills is not null)
                {
                    var sp = $"SP: {FormatSp(skills.TotalSp)}";
                    if (skills.UnallocatedSp is > 0) sp += $" (+{FormatSp(skills.UnallocatedSp.Value)} unallocated)";
                    lines.Add(sp);
                }
            }

            if (_settings.InfoFlyoutShowFatigue)
            {
                var fatigue = await CharacterInfoShared.GetFatigueAsync(characterId, forceRefresh: false, ct);
                var expire = fatigue?.JumpFatigueExpireDate;
                lines.Add(expire.HasValue && expire.Value > now
                    ? $"Jump fatigue: {HumanizeDuration(expire.Value - now)}"
                    : "Jump fatigue: none");
            }

            // Planets dropdown -- rendered as its own collapsible section by InfoFlyoutWindow, not a
            // flat line, so it's gathered separately from `lines`. Colonies with no extractor pins are
            // already filtered out by FetchExtractionSummaryAsync; an empty result here just means the
            // section doesn't appear (no colonies, or the planets scope isn't granted on this character).
            if (_settings.InfoFlyoutShowPlanets)
            {
                EnsurePiServices();
                var extractions = await _piService!.FetchExtractionSummaryAsync(characterId, ct);
                if (extractions.Count > 0)
                {
                    planetLines = extractions
                        .OrderBy(e => e.NextExpiry ?? System.DateTimeOffset.MaxValue)
                        .Select(e => $"{e.Title}: {e.FormatCountdown(now)}")
                        .ToList();
                }
            }
        }
        catch (System.Exception ex)
        {
            Log.Warn($"Info flyout ESI fetch failed: {ex}");
        }

        if (lines.Count == 0 && planetLines is null)
        {
            // Distinguish "every toggle is off" (a config nudge) from "toggles are on but nothing came
            // back" (e.g. Planets enabled on a character with no colonies) -- pre-existing code only
            // checked emptiness, which misreported the latter as the former.
            var anyEnabled = _settings.InfoFlyoutShowWallet || _settings.InfoFlyoutShowShip
                || _settings.InfoFlyoutShowLocation || _settings.InfoFlyoutShowDanger
                || _settings.InfoFlyoutShowSkill || _settings.InfoFlyoutShowSp
                || _settings.InfoFlyoutShowFatigue || _settings.InfoFlyoutShowPlanets;
            lines.Add(anyEnabled ? "No data available yet." : "All info lines are turned off.");
        }
        // Only apply if this is still the active card for this tile (the user may have clicked away).
        if (ReferenceEquals(_infoFlyout, flyout) && _infoFlyoutPosition == position)
            flyout.SetLines(lines, planetLines);
    }

    // The character to show for a seat: whoever is actually logged into the seat's window right now
    // (title-parsed, matched to a linked character), falling back to the seat's primary linked
    // character. Mirrors how the pills use RunningPortrait rather than the configured main, so the card
    // follows a seat that currently has a different alt logged into it.
    private EsiCharacter? ResolveSeatCharacter(int seat)
    {
        var assignment = Seat(seat);
        if (assignment is null || assignment.EsiCharacters.Count == 0) return null;

        var window = FindSeatWindow(seat);
        if (window is not null)
        {
            var running = CharacterNameFromTitle(window.Title);
            var match = assignment.EsiCharacters.FirstOrDefault(
                c => c.CharacterName.Equals(running, System.StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return assignment.EsiCharacters[0];
    }

    private void CloseInfoFlyout()
    {
        var flyout = _infoFlyout;
        _infoFlyout = null;
        _infoFlyoutPosition = -1;
        try { flyout?.Close(); } catch { /* closing a window that's already gone must not throw */ }
    }

    // System security to one decimal, EVE-style (highsec 1.0..0.5, lowsec 0.4..0.1, null/wh <= 0.0).
    private static string FormatSecurity(double security) => security.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    // ISK as "1.24b ISK" / "45.3m ISK" / "950k ISK" -- compact so a big balance stays one short line.
    private static string FormatIsk(double isk)
    {
        if (isk >= 1_000_000_000_000d) return $"{isk / 1_000_000_000_000d:0.##}t ISK";
        if (isk >= 1_000_000_000d) return $"{isk / 1_000_000_000d:0.##}b ISK";
        if (isk >= 1_000_000d) return $"{isk / 1_000_000d:0.#}m ISK";
        if (isk >= 1_000d) return $"{isk / 1_000d:0.#}k ISK";
        return $"{isk:0} ISK";
    }

    // Skill points as "45.2m" / "950k" / "1.2b" -- compact, one decimal.
    private static string FormatSp(long sp)
    {
        if (sp >= 1_000_000_000) return $"{sp / 1_000_000_000.0:0.#}b";
        if (sp >= 1_000_000) return $"{sp / 1_000_000.0:0.#}m";
        if (sp >= 1_000) return $"{sp / 1_000.0:0.#}k";
        return sp.ToString();
    }

    // "2d 3h" / "3h 12m" / "12m" / "<1m" -- coarse, compact, human-readable remaining time.
    private static string HumanizeDuration(System.TimeSpan span)
    {
        if (span <= System.TimeSpan.Zero) return "<1m";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return "<1m";
    }
}

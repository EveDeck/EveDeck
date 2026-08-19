using System.Collections.ObjectModel;
using System.Media;
using System.Windows.Threading;
using EveDeck.Models;
using EveDeck.Services;
using Application = System.Windows.Application;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    public ObservableCollection<GameEventRule> GameEventRules => _settings.GameEventRules;

    public bool AbyssModeEnabled
    {
        get => _settings.AbyssModeEnabled;
        set
        {
            if (_settings.AbyssModeEnabled == value) return;
            _settings.AbyssModeEnabled = value;
            OnPropertyChanged();
            Save();
        }
    }

    public bool AbyssModeAutoArm
    {
        get => _settings.AbyssModeAutoArm;
        set
        {
            if (_settings.AbyssModeAutoArm == value) return;
            _settings.AbyssModeAutoArm = value;
            OnPropertyChanged();
            Save();
        }
    }

    public int CombatToastCooldownSeconds
    {
        get => _settings.CombatToastCooldownSeconds;
        set
        {
            var clamped = Math.Clamp(value, CombatAlertThrottle.MinCooldownSeconds, CombatAlertThrottle.MaxCooldownSeconds);
            if (_settings.CombatToastCooldownSeconds == clamped) return;
            _settings.CombatToastCooldownSeconds = clamped;
            OnPropertyChanged();
            Save();
        }
    }

    // Abyss Mode is on when the user pinned it on, OR while auto-arm is enabled and somebody is in
    // unnamed space. Derived on read rather than stored, so there is no separate piece of state to
    // keep in sync with _unnamedSpaceByCharacter -- entering and leaving the abyss already maintains
    // that dictionary for the location pill.
    private bool AbyssSuppressionActive =>
        _settings.AbyssModeEnabled || (_settings.AbyssModeAutoArm && _unnamedSpaceByCharacter.Count > 0);

    public bool ToastsAboveOverlays
    {
        get => _settings.ToastsAboveOverlays;
        set
        {
            if (_settings.ToastsAboveOverlays == value) return;
            _settings.ToastsAboveOverlays = value;
            OnPropertyChanged();
            Save();
        }
    }

    // Current solar system per character name (from Local chatlog "Channel changed to Local"
    // lines). Character names come from EVE's own logs, so exact-name matching is reliable.
    // Only ever holds REAL system names -- see LocalUnnamedSystem below.
    private readonly Dictionary<string, string> _systemByCharacter = new(StringComparer.OrdinalIgnoreCase);

    // In abyssal space EVE writes the Local channel's system as literally "Unknown" instead of a
    // name. This is NOT a system name -- storing it as one would read "<character> . Unknown" on the
    // pill AND throw away the system they are about to pop back into, since a filament always returns
    // you to the system you launched it from.
    //
    // Measured against 7156 real Local chatlogs spanning 2024-05 to 2026-08 before writing this:
    //   * The abyss is the ONLY thing that produces it. Wormholes log their real J-signature and
    //     Pochven logs its real system names (18 visits across the sample); neither is ever
    //     "Unknown". Both are named in the client's UI, and this string is simply what EVE writes
    //     when it has no name to write.
    //   * Every one of the 81 measurable "Unknown" stretches in the last two months of that sample
    //     fit inside the abyss's 20-minute filament timer: min 2.6, mean 14.1, max 20.2 minutes.
    // That is why the neutral tag below exists anyway: "only thing observed" is not "only thing
    // possible", and ResolveUnnamedSpaceAsync degrades to a real system name for anything else.
    private const string LocalUnnamedSystem = "Unknown";

    // What gets appended to that retained system while EVE refuses to name the location. "Abyss" is
    // used only once ESI has CONFIRMED it (ResolveUnnamedSpaceAsync); until then -- and permanently,
    // for a character that was never ESI-linked -- the neutral tag states what is actually known,
    // which is that the location is unreported. Any other instanced space EVE might one day report
    // this way therefore degrades to a neutral marker rather than being mislabelled as abyss.
    private const string AbyssSpaceTag = "Abyss";
    private const string UnnamedSpaceTag = "?";

    // Character name -> one of the tags above, present only while that character is in unnamed space.
    private readonly Dictionary<string, string> _unnamedSpaceByCharacter = new(StringComparer.OrdinalIgnoreCase);

    // EVE's abyssal solar systems occupy their own id block, verified live against ESI 2026-08-14:
    // 32000001 resolves to "AD001" (constellation ADC01, region ADR01) and 32000480 is already past
    // the end ("System not found"). Ordinary k-space is 30xxxxxx and wormholes are 31xxxxxx, so
    // testing the whole 32xxxxxx block is stable against CCP adding pockets, unlike a system list.
    internal static bool IsAbyssalSystemId(int systemId) => systemId is >= 32_000_000 and <= 32_999_999;

    private void InitChatAlerts()
    {
        _chatLogWatcherService.ErrorOccurred += msg => Log.Warn(msg);
        _chatLogWatcherService.SystemChanged += (character, system) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => OnCharacterSystemChanged(character, system));
        };
        _chatLogWatcherService.Start();

        _gameLogWatcherService.RulesProvider = () => _settings.GameEventRules;
        _gameLogWatcherService.ErrorOccurred += msg => Log.Warn(msg);
        _gameLogWatcherService.EventMatched += (rule, character, line) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => OnGameEventMatched(rule, character, line));
        };
        _gameLogWatcherService.Start();
    }

    private void OnGameEventMatched(GameEventRule rule, string character, string line)
    {
        // "Being shot" gating: EVE logs both your outgoing fire and the damage you take as (combat)
        // lines. Only incoming ones ("... from <attacker> ...") mean this character is under fire, so
        // a combat line that isn't incoming damage raises no alert at all -- otherwise every shot you
        // fire would flash your own tiles.
        if (line.IndexOf("(combat)", StringComparison.OrdinalIgnoreCase) >= 0 && !IsIncomingDamage(line))
            return;

        var seat = FindSeatByCharacter(character);

        if (rule.SuppressWhenFocused && seat is not null)
        {
            var window = FindAssignedWindows(seat).FirstOrDefault();
            if (window is not null && window.Handle == _windowService.GetForegroundWindowHandle())
                return; // that client is already on screen — no alert needed
        }

        Log.Info($"Game event '{rule.Name}' for {(character.Length > 0 ? character : "unknown character")}: {line}");

        if (rule.FlashOnTile)
        {
            // "Something is happening to this character right now" (combat by default) — pulse the
            // seat's own tile/master rect on the overlay for real-time visibility, and queue a bundled
            // toast (throttled, see QueueCombatAlertToast) as the persistent record of what happened.
            // Abyss Mode keeps the visual glow but silences the sound for rules that opt into that
            // (SuppressSoundInAbyss, true by default) -- Abyssal Deadspace can put up to three
            // characters under continuous, expected damage simultaneously. A rule can opt out (e.g.
            // Warp scramble) to stay audible even mid-run, since that's a rare, high-stakes event.
            if (rule.PlaySound && !(rule.SuppressSoundInAbyss && AbyssSuppressionActive)) SystemSounds.Exclamation.Play();
            if (seat is not null)
            {
                // Glow first and unconditionally: it is the per-event, real-time signal and is never
                // throttled. Only the toast goes through the rate limiter.
                TriggerCombatGlow(seat);
                QueueCombatAlertToast(seat, rule.Name);
            }
        }
        else
        {
            if (rule.PlaySound) SystemSounds.Exclamation.Play();
            ShowToast(rule.Name, character.Length > 0 ? character : "", "#F59E0B", seat);
        }
    }

    // EVE combat lines read "<amount> from <attacker> - ..." for damage taken and "<amount> to
    // <target> - ..." for damage dealt. Given the line is already known to be a (combat) line, the
    // "from" direction word (word-bounded, tolerant of EVE's colour/font tags around it) reliably
    // marks incoming damage.
    private static bool IsIncomingDamage(string line)
        => System.Text.RegularExpressions.Regex.IsMatch(
            line, @"\bfrom\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Seat currently running the given character: live window title first (RunningCharacterName),
    // then the seat's configured main-character Label as a fallback for logged-off clients.
    private SlotAssignment? FindSeatByCharacter(string? character)
    {
        if (string.IsNullOrWhiteSpace(character)) return null;
        return Assignments.FirstOrDefault(a => character.Equals(a.RunningCharacterName, StringComparison.OrdinalIgnoreCase))
            ?? Assignments.FirstOrDefault(a => character.Equals(a.Label, StringComparison.OrdinalIgnoreCase));
    }

    private void OnCharacterSystemChanged(string character, string system)
    {
        // Entering space EVE won't name: KEEP the last real system (it is where a filament will spit
        // them back out) and hang a tag off it. The tag starts neutral and is upgraded to "Abyss"
        // only if ESI confirms it, so nothing is asserted before it is actually known.
        if (string.Equals(system, LocalUnnamedSystem, StringComparison.OrdinalIgnoreCase))
        {
            if (_unnamedSpaceByCharacter.ContainsKey(character)) return;
            _unnamedSpaceByCharacter[character] = UnnamedSpaceTag;
            RefreshSystemPills();
            _ = ResolveUnnamedSpaceAsync(character);
            return;
        }

        // Leaving it is a pill change even when the system name itself is unchanged, which is the
        // normal case: a filament drops you back exactly where you started, so only the tag moves.
        var wasUnnamed = _unnamedSpaceByCharacter.Remove(character);
        if (!wasUnnamed && _systemByCharacter.GetValueOrDefault(character) == system) return;
        _systemByCharacter[character] = system;
        RefreshSystemPills();
    }

    private void RefreshSystemPills()
    {
        NoteAbyssAutoArmState();
        if (_settings.CornerOverlayShowSystem && CornerOverlaysLive) RefreshAllPills();
    }

    // Auto-arm is silent state that changes what alerts do, so each transition is logged once --
    // "why did the chime stop" is otherwise unanswerable from the log. Every mutation of
    // _unnamedSpaceByCharacter already refreshes the pills, so this rides along with that.
    private bool _abyssAutoArmed;

    private void NoteAbyssAutoArmState()
    {
        var armed = _settings.AbyssModeAutoArm && _unnamedSpaceByCharacter.Count > 0;
        if (armed == _abyssAutoArmed) return;
        _abyssAutoArmed = armed;
        Log.Info(armed
            ? $"Abyss Mode auto-armed ({_unnamedSpaceByCharacter.Count} character(s) in unnamed space): glow-event sounds suppressed."
            : "Abyss Mode auto-disarmed: no character in unnamed space.");
    }

    // Asks ESI where a character in unnamed space actually is. Fires once per entry, not on a timer,
    // so the cost is one location call per abyss run per character -- and only for characters that
    // are ESI-linked anyway for the info card and the jump timers.
    //
    // ESI's own location data lags the client by a few seconds, so a first reading can still name the
    // system they just left. That case is indistinguishable from "ESI knows a real system EVE simply
    // didn't announce in Local", so it counts as inconclusive and is retried rather than trusted:
    // believing it would drop the tag and claim they are still flying around in normal space.
    private async Task ResolveUnnamedSpaceAsync(string character)
    {
        var seat = FindSeatByCharacter(character);
        var linked = seat?.EsiCharacters.FirstOrDefault(
            c => c.CharacterName.Equals(character, StringComparison.OrdinalIgnoreCase));
        if (linked is null || TokenStore.Get(linked.CharacterId) is null) return; // stays neutral

        var lastKnown = _systemByCharacter.GetValueOrDefault(character, "");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(5));
            // They may already be back in named space, or logged off, while this was waiting.
            if (!_unnamedSpaceByCharacter.ContainsKey(character)) return;

            try
            {
                var loc = await CharacterInfoShared.GetLocationAsync(linked.CharacterId, forceRefresh: true, CancellationToken.None);
                if (loc is null) continue;

                if (IsAbyssalSystemId(loc.SolarSystemId))
                {
                    _unnamedSpaceByCharacter[character] = AbyssSpaceTag;
                    RefreshSystemPills();
                    return;
                }

                var sys = await EsiTypeCacheShared.GetSystemInfoAsync(loc.SolarSystemId, CancellationToken.None);
                if (string.IsNullOrEmpty(sys.Name) || sys.Name.Equals(lastKnown, StringComparison.OrdinalIgnoreCase))
                    continue; // stale reading of the system they left -- inconclusive, ask again

                // ESI names a system EVE declined to announce in Local. A real name beats any tag.
                _unnamedSpaceByCharacter.Remove(character);
                _systemByCharacter[character] = sys.Name;
                RefreshSystemPills();
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not resolve unnamed location for {character}: {ex}");
                return; // the neutral tag is already correct; don't retry into a failing endpoint
            }
        }
    }

    // Current solar system for the character seated at the given seat, or "" when unknown. A
    // character in unnamed space reads "<last system> (Abyss)": the system they left, which is also
    // the one a filament returns them to, plus what state they are in.
    private string SeatSystemName(int seat)
    {
        if (!_settings.CornerOverlayShowSystem) return "";
        var a = Seat(seat);
        if (a is null) return "";

        // Whoever is actually logged into the seat right now wins over its configured main, but only
        // if we know anything at all about them -- otherwise fall back to the seat label, exactly as
        // this did before the unnamed-space tag existed.
        var running = a.RunningCharacterName;
        var character = !string.IsNullOrWhiteSpace(running)
                        && (_systemByCharacter.ContainsKey(running) || _unnamedSpaceByCharacter.ContainsKey(running))
            ? running
            : a.Label;
        if (string.IsNullOrEmpty(character)) return "";

        var system = _systemByCharacter.GetValueOrDefault(character, "");
        if (!_unnamedSpaceByCharacter.TryGetValue(character, out var tag)) return system;
        // No last-known system to hang the tag off (EveDeck was started while they were already
        // inside) -- show the bare tag rather than nothing at all.
        return system.Length > 0 ? $"{system} ({tag})" : tag;
    }

    // Rapid-fire FlashOnTile events (sustained incoming damage can log several hits a second, often
    // across multiple seats at once) collapse into one toast per short window instead of spamming a
    // toast per hit -- the glow still pulses per-event for real-time feedback; only the toast is
    // throttled. The bundle window alone was not enough on a real Abyssal run: see CombatAlertThrottle
    // for the duplicate-row and repeat-forever failures it fixes.
    private readonly CombatAlertThrottle _combatAlertThrottle = new();
    private readonly HashSet<SlotAssignment> _pendingCombatSeats = new();
    private DispatcherTimer? _combatAlertBundleTimer;
    private static readonly TimeSpan CombatAlertBundleWindow = TimeSpan.FromSeconds(2);

    private void QueueCombatAlertToast(SlotAssignment seat, string ruleName)
    {
        _combatAlertThrottle.Cooldown = TimeSpan.FromSeconds(_settings.CombatToastCooldownSeconds);
        if (!_combatAlertThrottle.TryQueue(ruleName, $"#{seat.SlotNumber}", $"{ruleName} — {seat.Label}", DateTime.UtcNow))
            return; // already shown for this seat inside the cooldown -- the glow already fired

        _pendingCombatSeats.Add(seat);
        if (_combatAlertBundleTimer is not null) return; // a window is already open; this alert rides along

        var timer = new DispatcherTimer { Interval = CombatAlertBundleWindow };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _combatAlertBundleTimer = null;
            var messages = _combatAlertThrottle.Flush();
            // Only attribute the card to a seat when the whole bundle came from that one seat -- a
            // multi-seat bundle (a fleet getting hit at once) has no single face or click target,
            // so it falls back to the plain accent card.
            var seats = _pendingCombatSeats.ToList();
            _pendingCombatSeats.Clear();
            if (messages.Count == 0) return;
            var title = messages.Count == 1 ? "Combat alert" : $"Combat alert ({messages.Count})";
            ShowToast(title, string.Join("\n", messages), "#EF4444", seats.Count == 1 ? seats[0] : null);
        };
        _combatAlertBundleTimer = timer;
        timer.Start();
    }

    private void AddGameEventRule()
    {
        _settings.GameEventRules.Add(new GameEventRule { Name = "Custom", Pattern = "" });
        Save();
    }

    private void RemoveGameEventRule(object? parameter)
    {
        if (parameter is not GameEventRule rule) return;
        _settings.GameEventRules.Remove(rule);
        Save();
    }

    private void StopChatAlerts()
    {
        _chatLogWatcherService.Stop();
        _gameLogWatcherService.Stop();
    }
}

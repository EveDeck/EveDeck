using EveDeck.Utilities;

namespace EveDeck.Models;

// One structured game-event alert: a substring matched against new lines in EVE's own
// Gamelogs files (Documents\EVE\logs\Gamelogs). Passive log-tailing model -- plain file I/O
// over logs EVE writes itself, never game input.
public sealed class GameEventRule : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    // Display name shown in Options and in alert log lines (e.g. "Combat", "Fleet invite").
    private string _name = "";
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    // Case-insensitive substring matched against each new gamelog line. Editable because Fenris
    // Creations' wording can change between patches and localised clients log localised text.
    private string _pattern = "";
    public string Pattern
    {
        get => _pattern;
        set => SetProperty(ref _pattern, value);
    }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    private bool _playSound = true;
    public bool PlaySound
    {
        get => _playSound;
        set => SetProperty(ref _playSound, value);
    }

    // Skip the alert when the matching character's own window is foreground -- you can already
    // see that client, so e.g. combat on the ACTIVE window shouldn't chime.
    private bool _suppressWhenFocused = true;
    public bool SuppressWhenFocused
    {
        get => _suppressWhenFocused;
        set => SetProperty(ref _suppressWhenFocused, value);
    }

    // When true, a match pulses a glow around that seat's current tile/master rect on the overlay
    // instead of a toast notification -- reserved for "something is happening TO this character
    // right now" events (combat) where a toast per line would be constant spam. Everything else
    // (chat keywords, all other game events) is a toast + sound. See AppSettings.AbyssModeEnabled
    // for the related sound-suppression toggle.
    private bool _flashOnTile;
    public bool FlashOnTile
    {
        get => _flashOnTile;
        set => SetProperty(ref _flashOnTile, value);
    }

    // Whether Abyss Mode's sound suppression (AppSettings.AbyssModeEnabled) applies to THIS rule.
    // True (the historical, still-default behavior) for continuous/expected noise like Combat, where
    // Abyssal Deadspace can put several characters under simultaneous damage and a sound per hit
    // would be constant. False for rare, high-stakes events (Warp scramble) that are worth hearing
    // even mid-run -- being tackled and unable to escape matters more during an Abyss run, not less.
    private bool _suppressSoundInAbyss = true;
    public bool SuppressSoundInAbyss
    {
        get => _suppressSoundInAbyss;
        set => SetProperty(ref _suppressSoundInAbyss, value);
    }

    // "Warp scramble" added 2026-07-24 from a real Abyss combat-log sample -- the generic Combat rule
    // already gates on incoming-only lines (see MainWindowViewModel.ChatAlerts.IsIncomingDamage), but
    // a scramble attempt is a distinct "you may not be able to escape" event worth its own rule with
    // sound that survives Abyss Mode, rather than being buried in Combat's glow-only, sound-silenced
    // stream of hits. "Warp disruption" is the long-point sibling module (Warp Disruptor vs Warp
    // Scrambler); EVE logs it as "Warp disruption attempt" (verified against the real archive, 2026-09,
    // ~7.7k lines; the earlier "warp disruptor attempt" guess matched zero). Scram and disrupt log
    // near-identical lines -- you only tell them apart in-game by whether your MWD is cut -- so the two
    // rules exist to cover both wordings, not to distinguish the modules.
    //
    // Every Pattern here was match-counted against ~1.58M real gamelog lines (see
    // GameEventRuleDefaultsRealLogTests). Two are deliberately loose and kept by user preference:
    // "Asteroid depleted" (Pattern "depleted") in practice matches the "(mining) Additional N units
    // depleted from asteroid as residue" cycle line, and "Mining crystal" (Pattern "crystal") matches
    // almost nothing in gamelogs -- both are silent (PlaySound = false), so a stray match only adds a
    // line to the alert log. "Conversation" was "wants to talk" (zero matches) before 2026-09.
    public static IEnumerable<GameEventRule> Defaults() => new[]
    {
        new GameEventRule { Name = "Combat",            Pattern = "(combat)", FlashOnTile = true },
        new GameEventRule { Name = "Warp scramble",     Pattern = "warp scramble attempt", FlashOnTile = true, SuppressSoundInAbyss = false },
        new GameEventRule { Name = "Warp disruption",   Pattern = "warp disruption attempt", FlashOnTile = true, SuppressSoundInAbyss = false },
        new GameEventRule { Name = "Asteroid depleted", Pattern = "depleted", PlaySound = false },
        new GameEventRule { Name = "Mining crystal",    Pattern = "crystal",  PlaySound = false },
        new GameEventRule { Name = "Fleet invite",      Pattern = "join their fleet", SuppressWhenFocused = false },
        new GameEventRule { Name = "Conversation",      Pattern = "is inviting you to a conversation", SuppressWhenFocused = false },
    };
}

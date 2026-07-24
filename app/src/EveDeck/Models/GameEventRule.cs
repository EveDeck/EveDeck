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
    // Scrambler) -- its pattern is inferred by analogy to the verified "warp scramble attempt" wording
    // rather than confirmed against a real log line, since EVE's own wording for it wasn't in the
    // sample. Both patterns are plain user-editable text (see the Pattern property doc) if either
    // turns out to not match the client's actual logged wording.
    public static IEnumerable<GameEventRule> Defaults() => new[]
    {
        new GameEventRule { Name = "Combat",            Pattern = "(combat)", FlashOnTile = true },
        new GameEventRule { Name = "Warp scramble",     Pattern = "warp scramble attempt", FlashOnTile = true, SuppressSoundInAbyss = false },
        new GameEventRule { Name = "Warp disruption",   Pattern = "warp disruptor attempt", FlashOnTile = true, SuppressSoundInAbyss = false },
        new GameEventRule { Name = "Asteroid depleted", Pattern = "depleted", PlaySound = false },
        new GameEventRule { Name = "Mining crystal",    Pattern = "crystal",  PlaySound = false },
        new GameEventRule { Name = "Fleet invite",      Pattern = "join their fleet", SuppressWhenFocused = false },
        new GameEventRule { Name = "Conversation",      Pattern = "wants to talk", SuppressWhenFocused = false },
    };
}

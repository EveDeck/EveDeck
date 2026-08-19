namespace EveDeck.Services;

// Rate limiter for the bundled combat-alert toast. Pure in-memory state, no timers and no UI --
// the caller owns the bundle timer and calls Flush when it fires, which is what makes this
// deterministic to unit test.
//
// Two separate problems, both observed on a real Abyssal run with three clients:
//
//   1. DUPLICATE ROWS. Sustained incoming damage logs ~20 hits a second per character, and every
//      one of them used to append its own "Combat -- <seat>" row to the pending list. A 2 second
//      bundle therefore produced a toast titled "Combat alert (40)" listing the same row forty
//      times. Identical rows carry no information, so a row is queued at most once per bundle.
//
//   2. UNBOUNDED REPEATS. Even deduplicated, a seat under continuous fire re-toasts every bundle
//      window for the whole run -- one card every couple of seconds for fifteen minutes. The
//      cooldown holds a given (rule, seat) pair to one toast per Cooldown; the tile glow is
//      untouched and still pulses per hit, so real-time feedback stays per-event and only the
//      persistent card is throttled.
//
// Cooldown is keyed per rule AND per seat so a rare high-stakes event (Warp scramble) is never
// swallowed by the cooldown that constant Combat damage just started.
public sealed class CombatAlertThrottle
{
    public const int MinCooldownSeconds = 0;
    public const int MaxCooldownSeconds = 300;

    private readonly Dictionary<string, DateTime> _lastAcceptedByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pending = new();

    private TimeSpan _cooldown = TimeSpan.FromSeconds(15);

    public TimeSpan Cooldown
    {
        get => _cooldown;
        set => _cooldown = TimeSpan.FromSeconds(
            Math.Clamp(value.TotalSeconds, MinCooldownSeconds, MaxCooldownSeconds));
    }

    public int PendingCount => _pending.Count;

    // True when the alert was accepted into the current bundle, i.e. the caller should also record
    // the seat and (if this was the first one) start the bundle timer. False means it was folded
    // into an alert the user has already been shown.
    public bool TryQueue(string ruleName, string seatKey, string message, DateTime now)
    {
        var key = $"{ruleName}{seatKey}";

        // A pair still inside its cooldown is dropped outright rather than merely deduplicated:
        // a bundle firing 2 seconds after the last one would otherwise re-raise the same card.
        if (_lastAcceptedByKey.TryGetValue(key, out var last) && now - last < _cooldown) return false;

        // Defensive: with a zero cooldown the check above never fires, and the same hit arriving
        // twice inside one bundle window would still duplicate a row.
        if (_pending.Contains(message)) return false;

        _lastAcceptedByKey[key] = now;
        _pending.Add(message);
        return true;
    }

    // Takes the bundled rows and resets for the next window. Cooldown state deliberately survives:
    // it is what stops the NEXT bundle repeating this one.
    public IReadOnlyList<string> Flush()
    {
        var messages = _pending.ToList();
        _pending.Clear();
        return messages;
    }

    // Called when alerting stops entirely (feature switched off, overlays torn down) so a seat is
    // not still holding a stale cooldown when it comes back.
    public void Reset()
    {
        _pending.Clear();
        _lastAcceptedByKey.Clear();
    }
}

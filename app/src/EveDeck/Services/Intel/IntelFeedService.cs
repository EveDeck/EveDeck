using EveDeck.Models.Intel;

namespace EveDeck.Services.Intel;

/// <summary>One intel line as the overlay shows it, with range worked out at the time it arrived.</summary>
public sealed record IntelFeedEntry(
    IntelMessage Message,
    bool IsHostile,
    int? JumpsAway,
    string? NearestCharacter)
{
    public bool RangeKnown => JumpsAway is not null;
}

/// <summary>
/// How usable the followed-character set currently is.
///
/// Reported rather than inferred, because "no range" and "in range" must never look alike: a pilot
/// running characters in abyssal space has no k-space position at all, so those characters cannot
/// anchor a distance. If every followed character is unusable the overlay has to say so, not quietly
/// present every line as out of range.
/// </summary>
public sealed record FollowedOriginStatus(
    IReadOnlyList<string> Usable,
    IReadOnlyList<string> WithoutKspaceLocation)
{
    public bool AnyUsable => Usable.Count > 0;
}

/// <summary>
/// Tail → parse → dedup → range. The in-process equivalent of the standalone daemon's pipeline,
/// minus the WebSocket: the overlay reads these events directly.
/// </summary>
public sealed class IntelFeedService : IDisposable
{
    /// <summary>
    /// Bounded on purpose. Both the dedup set and the history grew without limit in the daemon
    /// before this was capped, which is a soak hazard over an evening's play rather than a crash.
    /// </summary>
    private const int MaxHistory = 500;

    private const int MaxDedupEntries = 4000;

    /// <summary>
    /// EVE's chat logs carry only second-precision timestamps, each stamped locally by the client that
    /// received the line. The same broadcast intel report can therefore land in two characters' logs
    /// one second apart, which gives the strict id (hashing timestamp|author|text, ported byte-for-byte
    /// from the daemon) two different values for what is really one report. The daemon has this same
    /// gap; it just shows up more here because this PC runs several of the followed characters at once.
    /// This near-duplicate window is a deliberate addition on top of the ported id check, not a port of
    /// anything in Intel.kt/IntelPipeline.kt.
    /// </summary>
    private static readonly TimeSpan NearDuplicateWindow = TimeSpan.FromSeconds(3);

    private readonly Universe _universe;
    private readonly IntelLogTailer _tailer;
    private readonly Lock _gate = new();

    private readonly Dictionary<string, IntelParser> _parserByChannel = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlySet<int>> _scopeByChannel = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CharacterLocation> _locationByCharacter = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _followed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenMessageIds = [];
    private readonly Queue<string> _dedupOrder = new();
    private readonly Dictionary<string, long> _lastSeenByAuthorText = new(StringComparer.Ordinal);
    private readonly Queue<string> _authorTextOrder = new();
    private readonly List<IntelFeedEntry> _history = [];

    private IReadOnlyDictionary<int, int> _distances = new Dictionary<int, int>();
    private Dictionary<string, IReadOnlyDictionary<int, int>> _distancesByCharacter = new(StringComparer.OrdinalIgnoreCase);

    public event Action<IntelFeedEntry>? EntryAdded;

    public event Action<CharacterLocation>? LocationUpdated;

    public IntelFeedService(Universe universe, IntelLogTailer tailer)
    {
        _universe = universe;
        _tailer = tailer;
        _tailer.MessageRead += OnMessageRead;
    }

    public IReadOnlyList<IntelFeedEntry> History
    {
        get { lock (_gate) return [.._history]; }
    }

    /// <summary>Every known character position, including those with no k-space system.</summary>
    public IReadOnlyList<CharacterLocation> Locations
    {
        get { lock (_gate) return _locationByCharacter.Values.ToList(); }
    }

    /// <summary>
    /// The union of every watched channel's MOTD region scope, which is what a connected client uses
    /// to decide how much of the map is worth drawing.
    /// </summary>
    public IReadOnlyList<int> ScopeRegionIds
    {
        get
        {
            lock (_gate)
            {
                return _scopeByChannel.Values.SelectMany(s => s).Distinct().ToList();
            }
        }
    }

    public FollowedOriginStatus OriginStatus
    {
        get
        {
            lock (_gate)
            {
                var usable = new List<string>();
                var unusable = new List<string>();
                foreach (var character in _followed.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
                {
                    if (_locationByCharacter.TryGetValue(character, out var location) && location.SystemId > 0)
                        usable.Add(character);
                    else
                        unusable.Add(character);
                }

                return new FollowedOriginStatus(usable, unusable);
            }
        }
    }

    public void SetFollowedCharacters(IEnumerable<string> characters)
    {
        lock (_gate)
        {
            _followed.Clear();
            foreach (var character in characters)
            {
                if (!string.IsNullOrWhiteSpace(character)) _followed.Add(character.Trim());
            }
        }

        RecomputeDistances();
    }

    /// <summary>
    /// Records where a character is, from their Local channel. An unresolvable name is not an error:
    /// abyssal space is not in the stargate graph, so it is stored with no system id and simply
    /// cannot anchor a jump distance.
    /// </summary>
    public void UpdateLocation(string character, string systemName)
    {
        if (string.IsNullOrWhiteSpace(character) || string.IsNullOrWhiteSpace(systemName)) return;

        var system = _universe.SystemByName(systemName.Trim());
        var location = new CharacterLocation(
            character.Trim(),
            system?.Id ?? 0,
            system?.Name ?? systemName.Trim(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        bool relevant;
        lock (_gate)
        {
            _locationByCharacter[location.CharacterName] = location;
            relevant = _followed.Contains(location.CharacterName);
        }

        if (relevant) RecomputeDistances();
        LocationUpdated?.Invoke(location);
    }

    /// <summary>
    /// Applies a channel's MOTD region scope. Abbreviations resolve against the channel's own regions
    /// first, which is what lets a short form name exactly one system.
    /// </summary>
    public void SetChannelRegions(string channel, IEnumerable<string> regionNames)
    {
        var ids = new HashSet<int>();
        foreach (var name in regionNames)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0) continue;
            if (_universe.RegionIdsByLowerName.TryGetValue(trimmed.ToLowerInvariant(), out var id)) ids.Add(id);
        }

        lock (_gate)
        {
            _scopeByChannel[channel] = ids;
            _parserByChannel.Remove(channel);
        }
    }

    private void OnMessageRead(string channel, string? listener, ChatLogFormat.RawMessage raw)
    {
        try
        {
            var motd = ChatLogFormat.ParseChannelMotd(raw);
            if (motd is not null)
            {
                SetChannelRegions(channel, motd.Split("//", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                return;
            }

            IntelParser parser;
            lock (_gate)
            {
                if (!_parserByChannel.TryGetValue(channel, out var existing))
                {
                    _scopeByChannel.TryGetValue(channel, out var scope);
                    existing = new IntelParser(_universe, scope);
                    _parserByChannel[channel] = existing;
                }

                parser = existing;
            }

            var message = parser.Parse(channel, raw);
            if (!message.IsIntel()) return;

            lock (_gate)
            {
                // The same line lands in one log file per logged-in character.
                if (!_seenMessageIds.Add(message.Id)) return;
                _dedupOrder.Enqueue(message.Id);
                while (_dedupOrder.Count > MaxDedupEntries) _seenMessageIds.Remove(_dedupOrder.Dequeue());

                // Catches the same report logged a second apart in two characters' files, where the
                // strict id above (timestamp-sensitive) does not match. Author+text is enough on its
                // own here: two different genuine reports with identical text from the same author
                // within three seconds of each other are not a real scenario worth telling apart.
                var authorTextKey = $"{message.Author}\u0000{message.Raw}";
                if (_lastSeenByAuthorText.TryGetValue(authorTextKey, out var lastMillis) &&
                    Math.Abs(message.TimestampMillis - lastMillis) <= NearDuplicateWindow.TotalMilliseconds)
                {
                    return;
                }

                _lastSeenByAuthorText[authorTextKey] = message.TimestampMillis;
                _authorTextOrder.Enqueue(authorTextKey);
                while (_authorTextOrder.Count > MaxDedupEntries) _lastSeenByAuthorText.Remove(_authorTextOrder.Dequeue());
            }

            var (jumps, nearest) = NearestRange(message);
            var entry = new IntelFeedEntry(message, message.IsHostile(), jumps, nearest);

            lock (_gate)
            {
                _history.Add(entry);
                if (_history.Count > MaxHistory) _history.RemoveRange(0, _history.Count - MaxHistory);
            }

            EntryAdded?.Invoke(entry);
        }
        catch
        {
            // A single malformed line must never take the feed down.
        }
    }

    private (int? Jumps, string? Nearest) NearestRange(IntelMessage message)
    {
        IReadOnlyDictionary<int, int> distances;
        lock (_gate) distances = _distances;

        if (distances.Count == 0) return (null, null);

        int? best = null;
        foreach (var systemId in message.SystemIds)
        {
            if (!distances.TryGetValue(systemId, out var jumps)) continue;
            if (best is null || jumps < best) best = jumps;
        }

        return best is null ? (null, null) : (best, NearestCharacterAt(best.Value, message));
    }

    /// <summary>
    /// Which followed character the reported range belongs to. Read from the per-character traversals
    /// computed at jump time, so the label names a real pilot rather than being guessed from the
    /// merged map.
    /// </summary>
    private string? NearestCharacterAt(int jumps, IntelMessage message)
    {
        Dictionary<string, IReadOnlyDictionary<int, int>> byCharacter;
        lock (_gate) byCharacter = _distancesByCharacter;

        foreach (var (character, distances) in byCharacter)
        {
            foreach (var systemId in message.SystemIds)
            {
                if (distances.TryGetValue(systemId, out var d) && d == jumps) return character;
            }
        }

        return null;
    }

    private void RecomputeDistances()
    {
        List<CharacterLocation> origins;
        lock (_gate)
        {
            origins = _followed
                .Select(c => _locationByCharacter.GetValueOrDefault(c))
                .Where(l => l is not null && l.SystemId > 0)
                .Select(l => l!)
                .ToList();
        }

        // One traversal seeded with every followed character, so a hit reports its distance from the
        // nearest of them rather than from a nominated one. The per-character maps exist only to
        // attribute that distance to a pilot, and are recomputed here rather than per intel line.
        var merged = _universe.DistancesFrom(origins.Select(o => o.SystemId).Distinct());
        var byCharacter = new Dictionary<string, IReadOnlyDictionary<int, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in origins)
            byCharacter[origin.CharacterName] = _universe.DistancesFrom(origin.SystemId);

        lock (_gate)
        {
            _distances = merged;
            _distancesByCharacter = byCharacter;
        }
    }

    public void Dispose()
    {
        _tailer.MessageRead -= OnMessageRead;
    }
}

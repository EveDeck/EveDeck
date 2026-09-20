namespace EveDeck.Models.Intel;

/// <summary>How a numeric count relates to what was already reported.</summary>
public enum CountModifier
{
    /// <summary>A bare number, e.g. `Cyclone 3`.</summary>
    Exact,

    /// <summary>`+3` — that many more on top of the last report.</summary>
    Plus,

    /// <summary>`4=` or `=4` — the total is now exactly this.</summary>
    Total,

    /// <summary>`x3` or `3x` — a multiplier form, treated as a plain count.</summary>
    Times,
}

/// <summary>Intel keywords recognised in channel traffic. Surface forms live in <see cref="IntelVocabulary"/>.</summary>
public enum IntelKeyword
{
    Clear,
    Status,
    NoVisual,
    Spike,
    GateCamp,
    Bubbles,
    Wormhole,
    Cyno,
    Ess,
    Skyhook,
    CombatProbes,
    Docked,

    /// <summary>Seen on directional scan. How the sighting was made, not a threat level in itself.</summary>
    DScan,

    /// <summary>A pilot who is not blue. In nullsec that is a threat until proven otherwise.</summary>
    Neutral,

    /// <summary>Explicitly red / hostile standing.</summary>
    Hostile,
}

/// <summary>A request for intel rather than a report of it: `location?`, `ship?`, `status`.</summary>
public enum QuestionKind
{
    Location,
    ShipType,
    Status,
    Count,
}

public abstract record IntelToken
{
    public required string Text { get; init; }

    /// <summary>
    /// A resolved solar system. <see cref="Text"/> is what was typed, <see cref="Name"/> the canonical SDE name.
    ///
    /// <see cref="Inferred"/> marks a system nobody in this message actually typed: the intel pipeline
    /// borrowed it from the channel's most recent explicit report because this line was a terse
    /// follow-up (`nv`, a bare ship name) that carries no location of its own. The UI must show the
    /// difference rather than silently presenting a guess as a stated fact.
    /// </summary>
    public sealed record SystemToken : IntelToken
    {
        public required int SystemId { get; init; }
        public required string Name { get; init; }
        public bool Linked { get; init; }
        public bool Inferred { get; init; }
    }

    /// <summary>A probable character name. Not validated against ESI.</summary>
    public sealed record PlayerToken : IntelToken
    {
        public bool Linked { get; init; }
    }

    public sealed record ShipToken : IntelToken
    {
        public int? TypeId { get; init; }
        public required string Name { get; init; }
        public int? Count { get; init; }
        public bool Linked { get; init; }
    }

    public sealed record CountToken : IntelToken
    {
        public required int Value { get; init; }
        public required CountModifier Modifier { get; init; }
    }

    public sealed record KeywordToken : IntelToken
    {
        public required IntelKeyword Keyword { get; init; }
    }

    public sealed record QuestionToken : IntelToken
    {
        public required QuestionKind Kind { get; init; }
    }

    public sealed record UrlToken : IntelToken;

    /// <summary>Anything not otherwise classified.</summary>
    public sealed record WordToken : IntelToken;
}

/// <summary>
/// One parsed line of intel.
///
/// <see cref="Id"/> is stable across characters: the same message seen in five clients' logs produces
/// the same id, so the tablet shows it once.
/// </summary>
public sealed record IntelMessage(
    string Id,
    string Channel,
    string Author,
    long TimestampMillis,
    string Raw,
    IReadOnlyList<IntelToken> Tokens)
{
    public IReadOnlyList<int> SystemIds =>
        Tokens.OfType<IntelToken.SystemToken>().Select(t => t.SystemId).Distinct().ToList();

    public IReadOnlyList<IntelKeyword> Keywords =>
        Tokens.OfType<IntelToken.KeywordToken>().Select(t => t.Keyword).Distinct().ToList();

    public IReadOnlyList<string> Players =>
        Tokens.OfType<IntelToken.PlayerToken>()
            // The same pilot is often linked twice in one line -- bare, and again with their
            // ship ("Nhar*  Nhar (Stork)*") -- so this normalises off the link marker before
            // deduping, matching SystemIds/Keywords/Questions below.
            .Select(t => t.Text.EndsWith('*') ? t.Text[..^1] : t.Text)
            .Distinct()
            .ToList();

    public IReadOnlyList<IntelToken.ShipToken> Ships =>
        Tokens.OfType<IntelToken.ShipToken>().ToList();

    public IReadOnlyList<QuestionKind> Questions =>
        Tokens.OfType<IntelToken.QuestionToken>().Select(t => t.Kind).Distinct().ToList();

    /// <summary>Total hostile count reported, if the message carried any ship counts.</summary>
    public int? ReportedCount
    {
        get
        {
            var shipCounts = Ships.Where(s => s.Count.HasValue).Select(s => s.Count!.Value);
            var bare = Tokens.OfType<IntelToken.CountToken>().Select(t => t.Value);
            var all = shipCounts.Concat(bare).ToList();
            return all.Count == 0 ? null : all.Sum();
        }
    }
}

/// <summary>Where one of the watched characters currently is, derived from their Local channel log.</summary>
public sealed record CharacterLocation(
    string CharacterName,
    int SystemId,
    string SystemName,
    long SinceMillis);

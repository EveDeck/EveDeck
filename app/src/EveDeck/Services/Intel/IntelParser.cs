using System.Text.RegularExpressions;
using EveDeck.Models.Intel;

namespace EveDeck.Services.Intel;

/// <summary>
/// Turns an intel channel line into typed tokens.
///
/// The key observation from real channel traffic is that two or more spaces separate entities, so
/// the parser works segment by segment, trying to match each segment as a whole before falling back
/// to scanning inside it. That single convention removes most of the ambiguity that would otherwise
/// need a character-name lookup.
///
/// Ported from EveDeck-Intel shared/parse/IntelParser.kt. The rules carrying a comment below were
/// each found by replaying real traffic; changing one regresses classification before anything else.
/// </summary>
public sealed partial class IntelParser
{
    private const string LinkMarker = "*";
    private const int MinAbbreviation = 3;
    private const int MinAlphaAbbreviation = 4;

    /// <summary>Faction hull suffixes people shorten by dropping the trailing "Issue".</summary>
    private static readonly string[] IssueSuffixes = [" navy", " fleet"];

    private static readonly char[] TrimChars =
        [',', '.', '!', '?', ':', ';', '(', ')', '[', ']', '"', '\'', ' '];

    [GeneratedRegex(@"\A(.+?)\s+\((.+)\)(\*?)\z")]
    private static partial Regex PlayerShipPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SegmentSplitPattern();

    [GeneratedRegex(@"\Ahttps?://\S+\z")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\A\+\s?(\d{1,4})\z")]
    private static partial Regex CountPlusPattern();

    [GeneratedRegex(@"\A(?:(\d{1,4})=|=(\d{1,4}))\z")]
    private static partial Regex CountTotalPattern();

    [GeneratedRegex(@"\A(?:(\d{1,4})[xX]|[xX](\d{1,4}))\z")]
    private static partial Regex CountTimesPattern();

    [GeneratedRegex(@"\A\d{1,4}\z")]
    private static partial Regex CountBarePattern();

    private readonly Universe _universe;
    private readonly IReadOnlySet<int> _scopeRegionIds;
    private readonly Dictionary<string, int> _shipTypeIdByLowerName;
    private readonly Dictionary<string, string> _canonicalShipNameByLower;
    private readonly List<SolarSystem> _scopedSystems;
    private readonly int _maxShipWords;
    private readonly int _maxKeywordWords;

    /// <param name="scopeRegionIds">
    /// Regions the channel covers, read from its own MOTD. Abbreviations are resolved against these
    /// first, which is what makes short forms unambiguous. Empty means the whole universe.
    /// </param>
    public IntelParser(Universe universe, IReadOnlySet<int>? scopeRegionIds = null)
    {
        _universe = universe;
        _scopeRegionIds = scopeRegionIds ?? new HashSet<int>();

        _shipTypeIdByLowerName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _canonicalShipNameByLower = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ship in universe.Ships)
        {
            _shipTypeIdByLowerName[ship.Name] = ship.Id;
            _canonicalShipNameByLower[ship.Name] = ship.Name;
        }

        _maxShipWords = universe.Ships.Count == 0
            ? 1
            : universe.Ships.Max(s => s.Name.Count(c => c == ' ') + 1);

        _maxKeywordWords = IntelVocabulary.Keywords.Count == 0
            ? 1
            : IntelVocabulary.Keywords.Keys.Max(k => k.Count(c => c == ' ') + 1);

        _scopedSystems = (_scopeRegionIds.Count == 0
                ? universe.Systems
                : universe.SystemsInRegions(_scopeRegionIds))
            .OrderBy(s => s.Name.Length)
            .ToList();
    }

    public IntelMessage Parse(string channel, ChatLogFormat.RawMessage raw) => new(
        Id: MessageId(raw),
        Channel: channel,
        Author: raw.Author,
        TimestampMillis: raw.TimestampMillis,
        Raw: raw.Message,
        Tokens: Tokenise(raw.Message));

    public IReadOnlyList<IntelToken> Tokenise(string message)
    {
        var tokens = new List<IntelToken>();
        foreach (var segment in SegmentSplitPattern().Split(message))
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0) continue;
            ParseSegment(trimmed, tokens);
        }

        return AttachCountsToShips(tokens);
    }

    private void ParseSegment(string segment, List<IntelToken> output)
    {
        // A segment is usually exactly one entity, so try the whole thing first.
        var whole = MatchEntity(segment);
        if (whole is not null)
        {
            output.Add(whole);
            return;
        }

        // A dragged/targeted ship link ("<pilot> (Stork)*") names a pilot and their hull in one
        // segment. Handled before the generic word loop below, which would otherwise split it into
        // an unmatched word plus a ship -- and since the same pilot is usually ALSO linked bare
        // elsewhere in the line, that leftover became a second, phantom Player token: the same
        // person shown twice in the feed.
        var playerWithShip = MatchPlayerWithShip(segment);
        if (playerWithShip is not null)
        {
            output.AddRange(playerWithShip);
            return;
        }

        var words = segment.Split(' ').Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
        var index = 0;
        var pendingName = new List<string>();

        void FlushName()
        {
            if (pendingName.Count == 0) return;
            var joined = string.Join(' ', pendingName);
            output.Add(LooksLikeCharacterName(joined)
                ? new IntelToken.PlayerToken
                {
                    Text = joined,
                    Linked = pendingName.Any(w => w.EndsWith(LinkMarker, StringComparison.Ordinal)),
                }
                : new IntelToken.WordToken { Text = joined });
            pendingName.Clear();
        }

        while (index < words.Count)
        {
            var matched = false;
            var maxWindow = Math.Min(Math.Max(_maxShipWords, _maxKeywordWords), words.Count - index);

            for (var n = maxWindow; n >= 1; n--)
            {
                var phrase = string.Join(' ', words.GetRange(index, n));
                var token = MatchEntity(phrase);
                if (token is null) continue;

                // A pilot's own name can coincide with ship/system shorthand (a handle beginning
                // "Mega" when "mega" aliases to Megathron): a bare single word is the only kind of
                // match this ambiguous, since a real multi-word phrase or an abbreviation long
                // enough to need the minimum length was clearly typed on purpose. Trust it only
                // when the rest of the segment doesn't independently stand on its own either -- if
                // the following words also resolve to something (the ordinary "system then ship"
                // shape, or a ship followed by a count), this was never a name to begin with.
                if (n == 1 && token is IntelToken.ShipToken or IntelToken.SystemToken)
                {
                    var j = index + 1;
                    while (j < words.Count && MatchEntity(words[j]) is null) j++;
                    if (j > index + 1)
                    {
                        var nameWords = words.GetRange(index, j - index);
                        var candidate = string.Join(' ', nameWords);
                        if (LooksLikeCharacterName(candidate))
                        {
                            FlushName();
                            output.Add(new IntelToken.PlayerToken
                            {
                                Text = candidate,
                                Linked = nameWords.Any(w => w.EndsWith(LinkMarker, StringComparison.Ordinal)),
                            });
                            index = j;
                            matched = true;
                            break;
                        }
                    }
                }

                FlushName();
                output.Add(token);
                index += n;
                matched = true;
                break;
            }

            if (!matched)
            {
                pendingName.Add(words[index]);
                index++;
            }
        }

        FlushName();
    }

    private IntelToken? MatchEntity(string phrase)
    {
        var raw = phrase.Trim();
        if (raw.Length == 0) return null;

        // Questions are matched before punctuation is stripped, so "status?" (a request) stays
        // distinct from "status" (a report).
        if (IntelVocabulary.Questions.TryGetValue(raw, out var questionKind))
            return new IntelToken.QuestionToken { Text = raw, Kind = questionKind };

        var stripped = raw.Trim(TrimChars);
        if (stripped.Length == 0) return null;

        var linked = stripped.EndsWith(LinkMarker, StringComparison.Ordinal);
        var core = (linked ? stripped[..^LinkMarker.Length] : stripped).Trim(TrimChars);
        if (core.Length == 0) return null;

        if (UrlPattern().IsMatch(core)) return new IntelToken.UrlToken { Text = core };

        if (IntelVocabulary.Keywords.TryGetValue(core, out var keyword))
            return new IntelToken.KeywordToken { Text = core, Keyword = keyword };

        var ship = MatchShip(core);
        if (ship is not null)
        {
            return new IntelToken.ShipToken
            {
                Text = core,
                TypeId = ship.Value.TypeId,
                Name = ship.Value.Name,
                Linked = linked,
            };
        }

        // Tried before MatchSystem: a count shape ("+10", "5=", "5x", a bare number) can look like a
        // digit-based system abbreviation to the prefix scan, which would silently swallow a fleet-size
        // report as a phantom system instead. A count never collides the other way -- it only fires on
        // a fully numeric core, and no real system name is purely digits.
        var count = MatchCount(core);
        if (count is not null) return count;

        var system = MatchSystem(core);
        if (system is not null)
        {
            return new IntelToken.SystemToken
            {
                Text = core,
                SystemId = system.Id,
                Name = system.Name,
                Linked = linked,
            };
        }

        return null;
    }

    private (int? TypeId, string Name)? MatchShip(string text)
    {
        if (_shipTypeIdByLowerName.TryGetValue(text, out var exactId))
            return (exactId, CanonicalShipName(text));

        if (IntelVocabulary.ShipAliases.TryGetValue(text, out var alias))
        {
            _shipTypeIdByLowerName.TryGetValue(alias, out var aliasId);
            return (_shipTypeIdByLowerName.ContainsKey(alias) ? aliasId : null, alias);
        }

        // "caracal navy", "stabber fleet": nobody types the trailing "Issue". A rule beats listing
        // them, because the SDE carries dozens of faction hulls and a hand-written list would rot.
        foreach (var suffix in IssueSuffixes)
        {
            if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var full = $"{text} issue";
            if (_shipTypeIdByLowerName.TryGetValue(full, out var fullId))
                return (fullId, CanonicalShipName(full));
        }

        // Plural hull report ("2 sabres", "caracal navy issues"): strip a trailing "s" and retry
        // once, rather than listing every hull's plural by hand.
        if (text.Length > 1 && text.EndsWith('s'))
        {
            var singular = text[..^1];
            if (_shipTypeIdByLowerName.TryGetValue(singular, out var singularId))
                return (singularId, CanonicalShipName(singular));

            if (IntelVocabulary.ShipAliases.TryGetValue(singular, out var singularAlias))
            {
                _shipTypeIdByLowerName.TryGetValue(singularAlias, out var singularAliasId);
                return (_shipTypeIdByLowerName.ContainsKey(singularAlias) ? singularAliasId : null, singularAlias);
            }
        }

        return MatchChineseShip(text);
    }

    /// <summary>A pilot name plus the hull they are currently flying, linked as one segment.</summary>
    private List<IntelToken>? MatchPlayerWithShip(string segment)
    {
        var match = PlayerShipPattern().Match(segment);
        if (!match.Success) return null;

        var namePart = match.Groups[1].Value.Trim();
        var shipPart = match.Groups[2].Value.Trim();
        var linked = match.Groups[3].Value == LinkMarker;

        if (!LooksLikeCharacterName(namePart)) return null;

        var ship = MatchShip(shipPart);
        if (ship is null) return null;

        return
        [
            new IntelToken.PlayerToken { Text = namePart, Linked = linked },
            new IntelToken.ShipToken
            {
                Text = shipPart,
                TypeId = ship.Value.TypeId,
                Name = ship.Value.Name,
                Linked = linked,
            },
        ];
    }

    /// <summary>Chinese-client hull report. See <see cref="IntelVocabulary.ShipAliasesZh"/>.</summary>
    private (int? TypeId, string Name)? MatchChineseShip(string text)
    {
        if (IntelVocabulary.ShipAliasesZh.TryGetValue(text, out var baseName))
        {
            _shipTypeIdByLowerName.TryGetValue(baseName, out var baseId);
            return (_shipTypeIdByLowerName.ContainsKey(baseName) ? baseId : null, baseName);
        }

        foreach (var (zhSuffix, enSuffix) in IntelVocabulary.ZhIssueSuffixes)
        {
            if (!text.EndsWith(zhSuffix, StringComparison.Ordinal)) continue;
            var stem = $"{text[..^zhSuffix.Length]}级";
            if (!IntelVocabulary.ShipAliasesZh.TryGetValue(stem, out var zhBase)) continue;
            var full = $"{zhBase} {enSuffix}";
            if (_shipTypeIdByLowerName.TryGetValue(full, out var fullId))
                return (fullId, full);
        }

        return null;
    }

    private string CanonicalShipName(string lower) =>
        _canonicalShipNameByLower.GetValueOrDefault(lower, lower);

    /// <summary>
    /// Resolves a system name or abbreviation.
    ///
    /// Exact names match anywhere in the universe. Abbreviations containing a digit or hyphen
    /// (nullsec names always do) resolve against the channel's own regions first, widening to the
    /// whole universe if that is unambiguous — that single rule keeps ordinary English words from
    /// turning into systems.
    ///
    /// Real traffic turned up a second shorthand: pilots routinely drop the digit/hyphen suffix and
    /// type only the leading letters. That form is purely alphabetic so it cannot reuse the
    /// digit/hyphen gate, and it gets a stricter rule instead of loosening that one — a higher
    /// minimum length, and never widened past the channel's own regions, because a bare word
    /// coincidentally prefixing some system name elsewhere in New Eden is a real risk.
    /// </summary>
    public SolarSystem? MatchSystem(string text)
    {
        var trimmed = text.Trim().Trim(TrimChars);
        if (trimmed.EndsWith(LinkMarker, StringComparison.Ordinal))
            trimmed = trimmed[..^LinkMarker.Length];
        var core = trimmed.Trim(TrimChars);

        if (core.Length == 0 || IntelVocabulary.SystemStopwords.Contains(core)) return null;

        var exact = _universe.SystemByName(core);
        if (exact is not null) return exact;

        var hasDigit = core.Any(char.IsDigit);
        var hasHyphen = core.Contains('-');
        var isAlphaShorthand = !hasDigit && !hasHyphen;

        if (isAlphaShorthand && core.Length < MinAlphaAbbreviation) return null;
        if (!isAlphaShorthand && core.Length < MinAbbreviation) return null;

        var needle = Alphanumeric(core);
        if (needle.Length == 0) return null;

        var candidates = _scopedSystems
            .Where(s => Alphanumeric(s.Name).StartsWith(needle, StringComparison.Ordinal))
            .Take(2)
            .ToList();

        if (candidates.Count == 1) return candidates[0];
        if (isAlphaShorthand) return null;

        if (candidates.Count == 0 && _scopeRegionIds.Count > 0)
        {
            // Nothing in the channel's regions -- fall back to the whole universe, but only when
            // the answer is unambiguous.
            var wide = _universe.Systems
                .Where(s => Alphanumeric(s.Name).StartsWith(needle, StringComparison.Ordinal))
                .Take(2)
                .ToList();
            return wide.Count == 1 ? wide[0] : null;
        }

        return null;
    }

    private static string Alphanumeric(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static IntelToken.CountToken? MatchCount(string core)
    {
        var plus = CountPlusPattern().Match(core);
        if (plus.Success)
        {
            return new IntelToken.CountToken
            {
                Text = core,
                Value = int.Parse(plus.Groups[1].Value),
                Modifier = CountModifier.Plus,
            };
        }

        var total = CountTotalPattern().Match(core);
        if (total.Success && FirstDigits(total) is { } totalDigits)
        {
            return new IntelToken.CountToken
            {
                Text = core,
                Value = totalDigits,
                Modifier = CountModifier.Total,
            };
        }

        var times = CountTimesPattern().Match(core);
        if (times.Success && FirstDigits(times) is { } timesDigits)
        {
            return new IntelToken.CountToken
            {
                Text = core,
                Value = timesDigits,
                Modifier = CountModifier.Times,
            };
        }

        if (CountBarePattern().IsMatch(core))
        {
            return new IntelToken.CountToken
            {
                Text = core,
                Value = int.Parse(core),
                Modifier = CountModifier.Exact,
            };
        }

        return null;
    }

    private static int? FirstDigits(Match match)
    {
        for (var i = 1; i < match.Groups.Count; i++)
        {
            var value = match.Groups[i].Value;
            if (value.Length > 0) return int.Parse(value);
        }

        return null;
    }

    /// <summary>
    /// Folds a count that directly follows a ship into that ship, leaving standalone counts
    /// ("4=  exeq navy") alone for the UI to interpret.
    /// </summary>
    private static List<IntelToken> AttachCountsToShips(List<IntelToken> tokens)
    {
        var output = new List<IntelToken>(tokens.Count);
        var i = 0;
        while (i < tokens.Count)
        {
            var current = tokens[i];
            var next = i + 1 < tokens.Count ? tokens[i + 1] : null;

            if (current is IntelToken.ShipToken { Count: null } shipToken &&
                next is IntelToken.CountToken countToken)
            {
                output.Add(shipToken with { Count = countToken.Value });
                i += 2;
            }
            else
            {
                output.Add(current);
                i++;
            }
        }

        return output;
    }

    /// <summary>EVE character names: one to three capitalised words, letters plus <c>' - .</c></summary>
    private static bool LooksLikeCharacterName(string text)
    {
        var source = text.EndsWith(LinkMarker, StringComparison.Ordinal)
            ? text[..^LinkMarker.Length]
            : text;

        var words = source.Split(' ').Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
        if (words.Count == 0 || words.Count > 3) return false;

        foreach (var word in words)
        {
            var clean = word.Trim(TrimChars);
            if (clean.EndsWith(LinkMarker, StringComparison.Ordinal))
                clean = clean[..^LinkMarker.Length];

            if (clean.Length == 0) return false;
            if (!clean.All(c => char.IsLetterOrDigit(c) || c is '\'' or '-' or '.')) return false;

            // Ordinary title-cased pilot name, or a stylised handle with a digit swapped in for a
            // letter -- real chatter never mixes letters and digits like that, and a bare number is
            // already claimed as a count before a word ever reaches here.
            if (!char.IsUpper(clean[0]) && !clean.Any(char.IsDigit)) return false;
        }

        return true;
    }

    /// <summary>
    /// Stable id for a message, so the same line seen in several characters' logs is shown once.
    /// FNV-1a over the UTF-16 code units, matching the Kotlin implementation byte for byte.
    /// </summary>
    public static string MessageId(ChatLogFormat.RawMessage raw)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var input = $"{raw.TimestampMillis}|{raw.Author}|{raw.Message}";
        var hash = offsetBasis;
        foreach (var ch in input)
        {
            hash ^= ch;
            hash *= prime;
        }

        return hash.ToString("x16");
    }
}

public static class IntelMessageClassification
{
    /// <summary>
    /// True if the line carries something worth showing on an intel display.
    ///
    /// A named system is what separates a report from conversation: a line mentioning only a ship is
    /// chatter, while one naming a system is not. The exception is a short question- or keyword-only
    /// line, which is a follow-up to the system named in the line before.
    /// </summary>
    public static bool IsIntel(this IntelMessage message)
    {
        if (string.Equals(message.Author, ChatLogFormat.EveSystemAuthor, StringComparison.Ordinal))
            return false;

        if (message.SystemIds.Count > 0) return true;

        var unmatchedWords = message.Tokens.Count(t => t is IntelToken.WordToken);
        return unmatchedWords == 0 && (message.Questions.Count > 0 || message.Keywords.Count > 0);
    }

    /// <summary>
    /// Intel that implies the system is currently hostile, rather than a "clear" report.
    ///
    /// A named ship, pilot or count is the usual evidence. But a line can carry none of those and
    /// still be the most urgent thing in the channel (a system reported camped or spiked), so the
    /// hostile keywords count too. Those lines previously reached the feed and never alerted.
    /// </summary>
    public static bool IsHostile(this IntelMessage message)
    {
        if (message.Keywords.Contains(IntelKeyword.Clear)) return false;
        if (message.Ships.Count > 0 || message.Players.Count > 0 || message.ReportedCount is not null) return true;
        return message.Keywords.Any(IntelVocabulary.HostileKeywords.Contains);
    }
}

using System.Collections.Frozen;

namespace EveDeck.Models.Intel;

/// <summary>
/// Surface forms used in intel channels.
///
/// Written from observed channel traffic rather than copied from any existing tool. Add to these
/// freely — they are the main thing that wants tuning against your own alliance's habits.
/// </summary>
public static class IntelVocabulary
{
    /// <summary>Multi-word forms are matched first, so `no visual` wins over a bare `no`.</summary>
    public static readonly FrozenDictionary<string, IntelKeyword> Keywords = new Dictionary<string, IntelKeyword>(StringComparer.OrdinalIgnoreCase)
    {
        ["clr"] = IntelKeyword.Clear,
        ["clear"] = IntelKeyword.Clear,
        ["cleared"] = IntelKeyword.Clear,

        ["status"] = IntelKeyword.Status,
        ["stat"] = IntelKeyword.Status,
        ["stats"] = IntelKeyword.Status,

        ["nv"] = IntelKeyword.NoVisual,
        ["no visual"] = IntelKeyword.NoVisual,
        ["novisual"] = IntelKeyword.NoVisual,
        ["no vis"] = IntelKeyword.NoVisual,

        ["spike"] = IntelKeyword.Spike,
        ["spiked"] = IntelKeyword.Spike,
        ["spiking"] = IntelKeyword.Spike,

        ["camp"] = IntelKeyword.GateCamp,
        ["camped"] = IntelKeyword.GateCamp,
        ["gatecamp"] = IntelKeyword.GateCamp,
        ["gate camp"] = IntelKeyword.GateCamp,

        ["bubble"] = IntelKeyword.Bubbles,
        ["bubbles"] = IntelKeyword.Bubbles,
        ["bubbled"] = IntelKeyword.Bubbles,
        ["drag bubble"] = IntelKeyword.Bubbles,

        ["wh"] = IntelKeyword.Wormhole,
        ["wormhole"] = IntelKeyword.Wormhole,

        ["cyno"] = IntelKeyword.Cyno,
        ["cynos"] = IntelKeyword.Cyno,

        ["ess"] = IntelKeyword.Ess,
        ["skyhook"] = IntelKeyword.Skyhook,

        ["probes"] = IntelKeyword.CombatProbes,
        ["combat probes"] = IntelKeyword.CombatProbes,

        ["docked"] = IntelKeyword.Docked,
        ["dockedup"] = IntelKeyword.Docked,
        ["docked up"] = IntelKeyword.Docked,

        // Added from a --validate pass over real traffic, where "dscan" was the commonest
        // unrecognised word by a distance.
        ["dscan"] = IntelKeyword.DScan,
        ["d-scan"] = IntelKeyword.DScan,
        ["dscanned"] = IntelKeyword.DScan,
        ["on dscan"] = IntelKeyword.DScan,

        ["neut"] = IntelKeyword.Neutral,
        ["neuts"] = IntelKeyword.Neutral,
        ["neutral"] = IntelKeyword.Neutral,
        ["neutrals"] = IntelKeyword.Neutral,

        ["red"] = IntelKeyword.Hostile,
        ["reds"] = IntelKeyword.Hostile,
        ["hostile"] = IntelKeyword.Hostile,
        ["hostiles"] = IntelKeyword.Hostile,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Keywords that on their own mean the system is not safe.
    ///
    /// A report can name no ship and no pilot and still be the most urgent line in the channel:
    /// `&lt;system&gt; camped` or `&lt;system&gt; spiked` is exactly what should raise an alert. Without
    /// this those lines were classified as intel, shown in the feed, and then never alerted on.
    ///
    /// Deliberately excluded: <see cref="IntelKeyword.DScan"/> (how something was seen, not what),
    /// <see cref="IntelKeyword.Docked"/> (a threat standing down), and <see cref="IntelKeyword.NoVisual"/>
    /// / <see cref="IntelKeyword.Status"/> (requests and negatives).
    /// </summary>
    public static readonly FrozenSet<IntelKeyword> HostileKeywords = new HashSet<IntelKeyword>
    {
        IntelKeyword.Spike,
        IntelKeyword.GateCamp,
        IntelKeyword.Bubbles,
        IntelKeyword.Cyno,
        IntelKeyword.CombatProbes,
        IntelKeyword.Neutral,
        IntelKeyword.Hostile,
    }.ToFrozenSet();

    /// <summary>
    /// Requests for intel rather than reports of it. Observed heavily in real channel traffic:
    /// `&lt;character&gt; location? ship?`, `&lt;character&gt; loc?`.
    /// </summary>
    public static readonly FrozenDictionary<string, QuestionKind> Questions = new Dictionary<string, QuestionKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["location?"] = QuestionKind.Location,
        ["loc?"] = QuestionKind.Location,
        ["location"] = QuestionKind.Location,
        ["loc"] = QuestionKind.Location,
        ["where?"] = QuestionKind.Location,

        ["ship?"] = QuestionKind.ShipType,
        ["ships?"] = QuestionKind.ShipType,
        ["ship types?"] = QuestionKind.ShipType,
        ["shiptypes?"] = QuestionKind.ShipType,
        ["type?"] = QuestionKind.ShipType,
        ["types?"] = QuestionKind.ShipType,

        ["status?"] = QuestionKind.Status,
        ["stat?"] = QuestionKind.Status,
        ["status"] = QuestionKind.Status,

        ["how many?"] = QuestionKind.Count,
        ["number?"] = QuestionKind.Count,
        ["count?"] = QuestionKind.Count,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Shorthand for ship names. The key is what people type, the value must match an SDE `typeName`
    /// exactly (case-insensitively).
    /// </summary>
    public static readonly FrozenDictionary<string, string> ShipAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["exeq"] = "Exequror",
        ["exeq navy"] = "Exequror Navy Issue",
        ["navy exeq"] = "Exequror Navy Issue",
        ["eni"] = "Exequror Navy Issue",
        ["cerb"] = "Cerberus",
        ["sabre"] = "Sabre",
        ["hic"] = "Broadsword",
        ["loki"] = "Loki",
        ["prot"] = "Proteus",
        ["legion"] = "Legion",
        ["tengu"] = "Tengu",
        ["hurr"] = "Hurricane",
        ["cane"] = "Hurricane",
        ["cyclone"] = "Cyclone",
        ["drek"] = "Drekavac",
        ["muninn"] = "Muninn",
        ["eagle"] = "Eagle",
        ["ishtar"] = "Ishtar",
        ["jackdaw"] = "Jackdaw",
        ["hecate"] = "Hecate",
        ["confessor"] = "Confessor",
        ["svipul"] = "Svipul",
        ["ceptor"] = "Malediction",
        ["dictor"] = "Sabre",
        ["rokh"] = "Rokh",
        ["mega"] = "Megathron",
        ["apoc"] = "Apocalypse",
        ["nag"] = "Naglfar",
        ["dread"] = "Naglfar",
        ["carrier"] = "Thanatos",
        ["fax"] = "Apostle",
        ["titan"] = "Erebus",
        ["super"] = "Nyx",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Chinese-client hull names, keyed by the base "X级" form the way CCP's own zh locale writes
    /// it (confirmed via ESI's `?language=zh` on each typeId, then cross-checked against real
    /// traffic in the archive — every key here was actually seen in a live intel report).
    ///
    /// The Russian client does not translate ship names at all (ESI's `ru` name is byte-identical
    /// to `en` for every hull checked), so there is no equivalent Russian alias table — there is
    /// nothing to alias. Only base forms are listed here.
    /// </summary>
    public static readonly FrozenDictionary<string, string> ShipAliasesZh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["狞獾级"] = "Caracal",
        ["洛基级"] = "Loki",
        ["维德马克级"] = "Vedmak",
        ["海神级"] = "Proteus",
        ["送葬者级"] = "Exequror",
        ["秃鹫级"] = "Condor",
        ["剑齿虎级"] = "Sabre",
        ["刺客级"] = "Stabber",
        ["苍鹭级"] = "Heron",
        ["咒灭级"] = "Malediction",
        ["圣卒级"] = "Legion",
        ["黑鸦级"] = "Crow",
        ["流浪级"] = "Vagabond",
        ["太阳神级"] = "Helios",
        ["多米尼克斯级"] = "Dominix",
        ["飓风级"] = "Cyclone",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Chinese equivalents of the English "navy"/"fleet" suffix rule.</summary>
    public static readonly FrozenDictionary<string, string> ZhIssueSuffixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["级海军型"] = "Navy Issue",
        ["级舰队型"] = "Fleet Issue",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Short words that must never resolve as a system abbreviation. They clear the length floor
    /// and would otherwise turn ordinary chatter into system reports.
    /// </summary>
    public static readonly FrozenSet<string> SystemStopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "on", "in", "at", "to", "is", "it", "no", "ok", "up", "gf", "o7",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}

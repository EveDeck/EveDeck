using System.Globalization;
using System.Text.RegularExpressions;

namespace EveDeck.Services;

// One rolling readout for a character. Everything except mining is a per-SECOND rate; mining is
// per-MINUTE because that is the timescale a mining cycle actually operates on -- a per-second ore
// figure spends most of its life at zero between cycles and reads as noise.
//
// Mining is in UNITS per minute, not m3. EVE's gamelog records units mined and names the ore, but
// not its volume, so m3 would require a static per-ore volume table covering every ore, ice and
// compressed variant -- a table that silently goes wrong every time CCP/Fenris adds an ore. Units is
// what the log actually states, so units is what this reports.
public readonly record struct DpsReading(
    double DamageOut,
    double DamageIn,
    double Logistics,
    double LogisticsIn,
    double CapTransferOut,
    double CapTransferIn,
    double NeutOut,
    double NeutIn,
    double MiningM3PerMinute)
{
    public bool IsIdle =>
        DamageOut <= 0 && DamageIn <= 0 && Logistics <= 0 && LogisticsIn <= 0
        && CapTransferOut <= 0 && CapTransferIn <= 0 && NeutOut <= 0 && NeutIn <= 0
        && MiningM3PerMinute <= 0;
}

internal enum MeterCategory
{
    DamageOut,
    DamageIn,
    LogisticsOut,
    LogisticsIn,
    CapTransferOut,
    CapTransferIn,
    NeutOut,
    NeutIn,
    Mining,
}

// Pure in-memory rolling aggregator for EVE gamelog lines. No file I/O, no timers, no threading --
// the caller feeds lines via Ingest and expires the window via Tick. Keeping this side-effect-free
// is what makes it deterministic to unit test.
//
// EVERY REGEX BELOW WAS VALIDATED AGAINST A REAL 1.08-MILLION-LINE GAMELOG ARCHIVE (2025-03 through
// 2026-08) BEFORE BEING COMMITTED. That is not ceremony: patterns lifted from PELD's source looked
// perfectly reasonable and two of them matched NOTHING against real logs. Anything added here later
// must be counted against real logs the same way -- a regex that silently matches zero lines is
// indistinguishable from a working one until someone notices the readout never moves.
public sealed class DpsMeterService
{
    private const int MinWindowSeconds = 2;
    private const int MaxWindowSeconds = 120;
    private const int MaxSamplesPerCharacter = 4096;

    private const string TimestampFormat = "yyyy.MM.dd HH:mm:ss";

    // "[ 2026.07.10 04:01:23 ] (combat) ..."
    private static readonly Regex TimestampRegex = new(
        @"^\[\s*(\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2})\s*\]",
        RegexOptions.Compiled);

    // ONE parse for every combat line. EVE encodes DIRECTION IN THE COLOUR TAG and the effect in the
    // trailing phrase, so colour+phrase is the actual grammar -- matching on the phrase alone with a
    // wildcard colour (what the upstream patterns did, and what this used to do) cannot tell "I neuted
    // them" from "they neuted me", because both say "energy neutralized".
    //
    // Note the optional " GJ" suffix: capacitor amounts are written "<b>263 GJ</b>", not "<b>263</b>",
    // and the whitespace before the trailing colour tag differs between damage and remote-effect lines.
    // Both cost real matches when unaccounted for.
    private static readonly Regex CombatLineRegex = new(
        @"\(combat\) <color=(0x[0-9a-fA-F]+)><b>([+\-]?)(\d+)(?: GJ)?</b>\s*(?:<[^>]*>)*\s*<font size=10>\s*([^<]*?)\s*</font>",
        RegexOptions.Compiled);

    // Colour codes, verified by counting a real 1,046,175-line classified archive:
    //   ffcc0000 damage in 315060 | ff00ffff damage out 306960
    //   ffccff66 remote assistance, direction from the "to"/"by" in the phrase
    //   ffe57f7f energy neutralized AGAINST you 6229 | ff7fffff your own neut 134 / nos 887
    private const string ColorDamageIn = "0xffcc0000";
    private const string ColorDamageOut = "0xff00ffff";
    private const string ColorRemoteAssist = "0xffccff66";
    private const string ColorCapWarfareIn = "0xffe57f7f";
    private const string ColorCapWarfareOut = "0xff7fffff";

    // Mining yield. PELD's published pattern matches ZERO current lines: it ends with "(.+?)<",
    // requiring a closing tag after the ore name, but EVE ends the line at the ore name with nothing
    // following it. This rewrite drops the ore-name capture entirely (the readout needs the volume,
    // not the ore) and anchors on "You mined", which also excludes the residue line -- "Additional N
    // units depleted from asteroid as residue" is asteroid waste, not yield, and counting it would
    // overstate the rate. "You mined an additional N" (a critical mining success) IS yield and is
    // included. Verified as an exact partition of the archive: 17019 yield + 1533 residue = 18552
    // total (mining) lines, none double-counted, none missed.
    // Group 1 is the unit count, group 2 the ore name. The name capture stops at the next '<' rather
    // than running to end-of-line: some lines continue past the ore with markup ("Argil Kylixium<...>
    // with a lost residue of 0 units"), and a greedy capture swallows all of it.
    private static readonly Regex MiningRegex = new(
        @"\(mining\).*?You mined(?: an additional)?.*?>([0-9]+)<.*?> units of (?:<[^>]*>)*([^<\r\n]+)",
        RegexOptions.Compiled);

    // Cubic metres per UNIT. EVE's log gives units and the ore's name but never its volume, so the
    // conversion has to live here. Keyed by the mineral word (see OreKey), which is stable across
    // every yield variant -- "Glistening Zeolites", "Brimful Zeolites" and plain "Zeolites" are the
    // same rock at different richness and share a volume.
    //
    // Values are the long-stable published volumes. If a number here is wrong the readout is
    // confidently wrong, which is worse than being absent -- so anything not listed is NOT silently
    // guessed at; it falls back to DefaultOreVolume and its name is recorded in UnknownOres so it can
    // be surfaced and corrected rather than quietly skewing the figure.
    private static readonly Dictionary<string, double> OreVolumes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Classic asteroid ores
        ["Veldspar"] = 0.1,     ["Scordite"] = 0.15,   ["Pyroxeres"] = 0.3,
        ["Plagioclase"] = 0.35, ["Omber"] = 0.6,       ["Kernite"] = 1.2,
        ["Jaspet"] = 2.0,       ["Hemorphite"] = 3.0,  ["Hedbergite"] = 3.0,
        ["Gneiss"] = 5.0,       ["Ochre"] = 8.0,       // "Dark Ochre"
        ["Crokite"] = 16.0,     ["Spodumain"] = 16.0,  ["Bistot"] = 16.0,
        ["Arkonor"] = 16.0,     ["Mercoxit"] = 40.0,

        // Moon ores. Every rarity tier (ubiquitous through exceptional) shares the same unit volume.
        ["Bitumens"] = 0.1,     ["Coesite"] = 0.1,     ["Sylvite"] = 0.1,     ["Zeolites"] = 0.1,
        ["Cobaltite"] = 0.1,    ["Euxenite"] = 0.1,    ["Scheelite"] = 0.1,   ["Titanite"] = 0.1,
        ["Chromite"] = 0.1,     ["Otavite"] = 0.1,     ["Sperrylite"] = 0.1,  ["Vanadinite"] = 0.1,
        ["Carnotite"] = 0.1,    ["Cinnabar"] = 0.1,    ["Pollucite"] = 0.1,   ["Zircon"] = 0.1,
        ["Loparite"] = 0.1,     ["Monazite"] = 0.1,    ["Xenotime"] = 0.1,    ["Ytterbite"] = 0.1,
    };

    // Used for any ore not in the table above. 0.1 is the single most common unit volume in EVE (every
    // moon ore plus Veldspar), so it is the least-wrong stand-in -- but a fallback is a guess, which is
    // why the ore's name goes into UnknownOres for the caller to report.
    private const double DefaultOreVolume = 0.1;

    // Ore names seen that had no table entry. Read by the view-model so it can log them once; a wrong
    // or missing volume is otherwise completely invisible in the finished number.
    private readonly HashSet<string> _unknownOres = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> UnknownOres => _unknownOres;

    // "Omber III-Grade" -> Omber (the grade is a quality tier, not a different mineral).
    // "Glistening Zeolites" / "Argil Kylixium" -> the mineral is the last word; anything before it is
    // a yield-variant adjective.
    internal static string OreKey(string oreName)
    {
        var parts = (oreName ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        return parts[^1].EndsWith("-Grade", StringComparison.OrdinalIgnoreCase) ? parts[0] : parts[^1];
    }

    private readonly Dictionary<string, List<Sample>> _samplesByCharacter =
        new(StringComparer.OrdinalIgnoreCase);

    private int _windowSeconds;

    public DpsMeterService(int windowSeconds = 10) => WindowSeconds = windowSeconds;

    public int WindowSeconds
    {
        get => _windowSeconds;
        set => _windowSeconds = Math.Clamp(value, MinWindowSeconds, MaxWindowSeconds);
    }

    public void Ingest(string character, string line, DateTime now)
    {
        if (string.IsNullOrEmpty(character) || string.IsNullOrEmpty(line)) return;

        var combat = line.Contains("(combat)", StringComparison.Ordinal);
        var mining = !combat && line.Contains("(mining)", StringComparison.Ordinal);
        if (!combat && !mining) return;

        if (!TryClassify(line, combat, out var category, out var amount)) return;

        var sampleTime = ResolveSampleTime(line, now);

        // Clock-skew guard: a wildly future timestamp is untrustworthy, but the line itself is still
        // real data -- fall back to arrival time rather than losing it. A stale timestamp (older than
        // the window) is genuinely expired history and is dropped outright, which is what keeps a
        // resync delivering a batch of old lines from spiking the readout.
        if (sampleTime > now.AddSeconds(60)) sampleTime = now;
        else if (sampleTime < now.AddSeconds(-WindowSeconds)) return;

        var samples = GetOrCreateList(character);
        samples.Add(new Sample(sampleTime, amount, category));
        if (samples.Count > MaxSamplesPerCharacter)
            samples.RemoveRange(0, samples.Count - MaxSamplesPerCharacter);
    }

    // Colour decides direction; the phrase decides which meter. A line is only ever counted once, and
    // an unrecognised colour/phrase pair is dropped rather than guessed at -- a miscategorised line is
    // worse than a missing one, because it silently inflates a number the user is reading as truth.
    // Instance rather than static: mining classification records any ore it could not find a volume
    // for, so the caller can surface it (see UnknownOres).
    private bool TryClassify(string line, bool combat, out MeterCategory category, out double amount)
    {
        category = default;
        amount = 0;

        if (!combat)
        {
            var mineMatch = MiningRegex.Match(line);
            if (!mineMatch.Success) return false;
            if (!double.TryParse(mineMatch.Groups[1].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var units))
                return false;

            // Converted to m3 here, at ingest, rather than at read time: the ore name is only
            // available on the line itself, and a window can mix ores with different volumes, so a
            // single conversion applied to the summed units afterwards would be wrong.
            var key = OreKey(mineMatch.Groups[2].Value);
            if (!OreVolumes.TryGetValue(key, out var volume))
            {
                volume = DefaultOreVolume;
                if (key.Length > 0) _unknownOres.Add(key);
            }

            category = MeterCategory.Mining;
            amount = units * volume;
            return true;
        }

        var match = CombatLineRegex.Match(line);
        if (!match.Success) return false;
        if (!double.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out amount))
            return false;

        var color = match.Groups[1].Value;
        var phrase = match.Groups[4].Value;

        switch (color.ToLowerInvariant())
        {
            case ColorDamageOut:
                // The bare phrase "to" / "from" is the whole direction token on a damage line.
                category = MeterCategory.DamageOut;
                return phrase.Equals("to", StringComparison.OrdinalIgnoreCase);

            case ColorDamageIn:
                category = MeterCategory.DamageIn;
                return phrase.Equals("from", StringComparison.OrdinalIgnoreCase);

            // One green for every remote effect. "... to" is you assisting someone else, "... by" is
            // someone assisting you; counting "by" as your own output was the original direction bug.
            case ColorRemoteAssist:
                var isCap = phrase.Contains("capacitor transmitted", StringComparison.OrdinalIgnoreCase);
                if (phrase.EndsWith(" to", StringComparison.OrdinalIgnoreCase))
                {
                    category = isCap ? MeterCategory.CapTransferOut : MeterCategory.LogisticsOut;
                    return true;
                }
                if (phrase.EndsWith(" by", StringComparison.OrdinalIgnoreCase))
                {
                    // Cap someone sends YOU is assistance. It gets its own bucket rather than being
                    // folded into the neut meter, where incoming help would read as incoming danger.
                    category = isCap ? MeterCategory.CapTransferIn : MeterCategory.LogisticsIn;
                    return true;
                }
                return false;

            case ColorCapWarfareOut:
                // Your own neut, and nos energy you pulled off a target (written "+123 GJ").
                category = MeterCategory.NeutOut;
                return true;

            case ColorCapWarfareIn:
                // Cap being taken off YOU by a neut or nos. 6229 lines in the reference archive and
                // previously invisible, which is why a client being capped out showed an empty panel.
                category = MeterCategory.NeutIn;
                return true;

            default:
                return false;
        }
    }

    public void Tick(DateTime now)
    {
        var cutoff = now.AddSeconds(-WindowSeconds);
        foreach (var samples in _samplesByCharacter.Values)
        {
            var firstLive = samples.FindIndex(s => s.Time >= cutoff);
            if (firstLive < 0) samples.Clear();
            else if (firstLive > 0) samples.RemoveRange(0, firstLive);
        }
    }

    public DpsReading GetReading(string character)
    {
        if (string.IsNullOrEmpty(character) || !_samplesByCharacter.TryGetValue(character, out var samples))
            return default;

        double outSum = 0, inSum = 0, logiOutSum = 0, logiInSum = 0;
        double capToSum = 0, capBySum = 0, neutOutSum = 0, neutInSum = 0, mineSum = 0;
        foreach (var sample in samples)
        {
            switch (sample.Category)
            {
                case MeterCategory.DamageOut:      outSum     += sample.Amount; break;
                case MeterCategory.DamageIn:       inSum      += sample.Amount; break;
                case MeterCategory.LogisticsOut:   logiOutSum += sample.Amount; break;
                case MeterCategory.LogisticsIn:    logiInSum  += sample.Amount; break;
                case MeterCategory.CapTransferOut: capToSum   += sample.Amount; break;
                case MeterCategory.CapTransferIn:  capBySum   += sample.Amount; break;
                case MeterCategory.NeutOut:        neutOutSum += sample.Amount; break;
                case MeterCategory.NeutIn:         neutInSum  += sample.Amount; break;
                case MeterCategory.Mining:         mineSum    += sample.Amount; break;
            }
        }

        double window = WindowSeconds;
        return new DpsReading(
            outSum / window,
            inSum / window,
            logiOutSum / window,
            logiInSum / window,
            capToSum / window,
            capBySum / window,
            neutOutSum / window,
            neutInSum / window,
            mineSum / window * 60.0);
    }

    public void Reset(string character)
    {
        if (string.IsNullOrEmpty(character)) return;
        _samplesByCharacter.Remove(character);
    }

    public void ResetAll() => _samplesByCharacter.Clear();

    private List<Sample> GetOrCreateList(string character)
    {
        if (!_samplesByCharacter.TryGetValue(character, out var samples))
        {
            samples = new List<Sample>();
            _samplesByCharacter[character] = samples;
        }
        return samples;
    }

    private static DateTime ResolveSampleTime(string line, DateTime now)
    {
        var match = TimestampRegex.Match(line);
        if (!match.Success) return now;

        return DateTime.TryParseExact(
            match.Groups[1].Value,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : now;
    }

    private readonly record struct Sample(DateTime Time, double Amount, MeterCategory Category);
}

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
    double Capacitor,
    double MiningUnitsPerMinute)
{
    public bool IsIdle =>
        DamageOut <= 0 && DamageIn <= 0 && Logistics <= 0 && Capacitor <= 0 && MiningUnitsPerMinute <= 0;
}

internal enum MeterCategory
{
    DamageOut,
    DamageIn,
    Logistics,
    Capacitor,
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

    // Damage. These match because EVE emits the direction as its own tagged token -- "<font
    // size=10>to</font>" and "<font size=10>from</font>" -- which is what supplies the ">to<" /
    // ">from<" the patterns pivot on. Archive hits: out 224715, in 274086.
    private static readonly Regex DamageOutRegex = new(
        @"\(combat\) <.*?><b>([0-9]+).*>to<", RegexOptions.Compiled);

    private static readonly Regex DamageInRegex = new(
        @"\(combat\) <.*?><b>([0-9]+).*>from<", RegexOptions.Compiled);

    // Outgoing logistics only ("repaired to", not "repaired by" -- the latter is someone repairing
    // YOU and would otherwise inflate your own logi figure). Archive hits: armor 193780, shield 9733.
    // Remote HULL repair never occurs in the archive, so its pattern is unproven by counting; it is
    // kept because it is structurally identical to the two that do validate at scale.
    private static readonly Regex[] LogisticsRegexes =
    {
        new(@"\(combat\) <.*?><b>([0-9]+).*> remote armor repaired to <", RegexOptions.Compiled),
        new(@"\(combat\) <.*?><b>([0-9]+).*> remote shield boosted to <", RegexOptions.Compiled),
        new(@"\(combat\) <.*?><b>([0-9]+).*> remote hull repaired to <", RegexOptions.Compiled),
    };

    // Capacitor warfare, following PELD's grouping: cap you sent out, energy you neutralized, and
    // energy you drained off a target with a nos. Archive hits: transmit-out 168, neut 79, nos 331.
    private static readonly Regex[] CapacitorRegexes =
    {
        new(@"\(combat\) <.*?><b>([0-9]+).*> remote capacitor transmitted to <", RegexOptions.Compiled),
        new(@"\(combat\) <.*?ff7fffff><b>([0-9]+).*> energy neutralized <", RegexOptions.Compiled),
        new(@"\(combat\) <.*?><b>\+([0-9]+).*> energy drained from <", RegexOptions.Compiled),
    };

    // Mining yield. PELD's published pattern matches ZERO current lines: it ends with "(.+?)<",
    // requiring a closing tag after the ore name, but EVE ends the line at the ore name with nothing
    // following it. This rewrite drops the ore-name capture entirely (the readout needs the volume,
    // not the ore) and anchors on "You mined", which also excludes the residue line -- "Additional N
    // units depleted from asteroid as residue" is asteroid waste, not yield, and counting it would
    // overstate the rate. "You mined an additional N" (a critical mining success) IS yield and is
    // included. Verified as an exact partition of the archive: 17019 yield + 1533 residue = 18552
    // total (mining) lines, none double-counted, none missed.
    private static readonly Regex MiningRegex = new(
        @"\(mining\).*?You mined(?: an additional)?.*?>([0-9]+)<.*?> units of ", RegexOptions.Compiled);

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

    // Order matters: damage is tested first because it is by far the most common line, and the
    // logistics/capacitor patterns are mutually exclusive with it anyway (they pivot on their own
    // literal phrases). A line is only ever counted once.
    private static bool TryClassify(string line, bool combat, out MeterCategory category, out double amount)
    {
        category = default;
        amount = 0;

        if (!combat)
        {
            var mineMatch = MiningRegex.Match(line);
            if (!mineMatch.Success) return false;
            category = MeterCategory.Mining;
            return TryAmount(mineMatch, out amount);
        }

        var match = DamageOutRegex.Match(line);
        if (match.Success)
        {
            category = MeterCategory.DamageOut;
            return TryAmount(match, out amount);
        }

        match = DamageInRegex.Match(line);
        if (match.Success)
        {
            category = MeterCategory.DamageIn;
            return TryAmount(match, out amount);
        }

        foreach (var regex in LogisticsRegexes)
        {
            match = regex.Match(line);
            if (!match.Success) continue;
            category = MeterCategory.Logistics;
            return TryAmount(match, out amount);
        }

        foreach (var regex in CapacitorRegexes)
        {
            match = regex.Match(line);
            if (!match.Success) continue;
            category = MeterCategory.Capacitor;
            return TryAmount(match, out amount);
        }

        return false;
    }

    private static bool TryAmount(Match match, out double amount) =>
        double.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out amount);

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

        double outSum = 0, inSum = 0, logiSum = 0, capSum = 0, mineSum = 0;
        foreach (var sample in samples)
        {
            switch (sample.Category)
            {
                case MeterCategory.DamageOut:  outSum  += sample.Amount; break;
                case MeterCategory.DamageIn:   inSum   += sample.Amount; break;
                case MeterCategory.Logistics:  logiSum += sample.Amount; break;
                case MeterCategory.Capacitor:  capSum  += sample.Amount; break;
                case MeterCategory.Mining:     mineSum += sample.Amount; break;
            }
        }

        double window = WindowSeconds;
        return new DpsReading(
            outSum / window,
            inSum / window,
            logiSum / window,
            capSum / window,
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

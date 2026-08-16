using EveDeck.Services;
using Xunit;

namespace EveDeck.Tests;

// Companion to DpsMeterServiceTests. That suite builds MINIMAL lines that satisfy the regexes; this
// one uses the VERBATIM markup EVE actually writes, captured from a real 1.08-million-line gamelog
// archive and then stripped of character/corp/system names per the project's OPSEC rule (ship types,
// module names and public ore names are generic and are kept, since the parser keys off them).
//
// Both suites are needed and neither replaces the other. A synthetic line proves the arithmetic; only
// a real line proves the pattern matches the game. The mining pattern published by PELD passes a
// hand-written test and matches ZERO real lines -- it requires a closing tag after the ore name that
// EVE does not emit -- which is precisely the failure mode these tests exist to catch.
public class DpsMeterRealLogShapeTests
{
    private static readonly DateTime BaseTime = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

    private static string Stamp(DateTime t) => $"[ {t:yyyy.MM.dd HH:mm:ss} ] ";

    // -- Real line shapes ---------------------------------------------------------------------

    private static string DamageOut(int amount, DateTime t) => Stamp(t) +
        $"(combat) <color=0xff00ffff><b>{amount}</b> <color=0x77ffffff><font size=10>to</font> " +
        "<b><color=0xffffffff>Target Frigate</b><font size=10><color=0x77ffffff> - Hits";

    private static string DamageIn(int amount, DateTime t) => Stamp(t) +
        $"(combat) <color=0xffcc0000><b>{amount}</b> <color=0x77ffffff><font size=10>from</font> " +
        "<b><color=0xffffffff>Hostile Cruiser</b><font size=10><color=0x77ffffff> - Hits";

    private static string ArmorRepairedTo(int amount, DateTime t) => Stamp(t) +
        $"(combat) <color=0xffccff66><b>{amount}</b><color=0x77ffffff><font size=10> remote armor repaired to " +
        "</font><b><color=0xffffffff><font size=12>Retribution</font size> Wingmate</b>" +
        "<color=0x77ffffff><font size=10> - Coreli A-Type Small Remote Armor Repairer</font>";

    // The inverse direction: someone repairing YOU. Must never be counted as your own logistics.
    private static string ArmorRepairedBy(int amount, DateTime t) => Stamp(t) +
        $"(combat) <color=0xffccff66><b>{amount}</b><color=0x77ffffff><font size=10> remote armor repaired by " +
        "</font><b><color=0xffffffff><font size=12>Deacon</font size> Wingmate</b>" +
        "<color=0x77ffffff><font size=10> - Coreli A-Type Small Remote Armor Repairer</font>";

    private static string ShieldBoostedTo(int amount, DateTime t) => Stamp(t) +
        $"(combat) <color=0xffccff66><b>{amount}</b><color=0x77ffffff><font size=10> remote shield boosted to " +
        "</font><b><color=0xffffffff><font size=12>Basilisk</font size> Wingmate</b>" +
        "<color=0x77ffffff><font size=10> - Large Remote Shield Booster II</font>";

    private static string CapacitorTransmittedTo(int amount, DateTime t) => Stamp(t) +
        $"(combat) <color=0xffccff66><b>{amount}</b><color=0x77ffffff><font size=10> remote capacitor transmitted to " +
        "</font><b><color=0xffffffff><font size=12>Inquisitor</font size> Wingmate</b>" +
        "<color=0x77ffffff><font size=10> - Small Remote Capacitor Transmitter II</font>";

    private static string CapacitorTransmittedBy(int amount, DateTime t) => Stamp(t) +
        $"(combat) <color=0xffccff66><b>{amount}</b><color=0x77ffffff><font size=10> remote capacitor transmitted by " +
        "</font><b><color=0xffffffff><font size=12>Inquisitor</font size> Wingmate</b>" +
        "<color=0x77ffffff><font size=10> - Small Remote Capacitor Transmitter II</font>";

    // Cap amounts carry a " GJ" suffix that damage amounts do not, and OUTGOING cap warfare is
    // ff7fffff while INCOMING is ffe57f7f. Both details cost real matches when missed.
    private static string EnergyNeutralized(int amount, DateTime t) => Stamp(t) +
        $"(combat) <color=0xff7fffff><b>{amount} GJ</b><color=0x77ffffff><font size=10> energy neutralized " +
        "</font><b><color=0xffffffff>Target Frigate</b><color=0x77ffffff><font size=10> - Small Energy Neutralizer II</font>";

    private static string EnergyDrained(int amount, DateTime t) => Stamp(t) +
        $"(combat) <color=0xff7fffff><b>+{amount} GJ</b><color=0x77ffffff><font size=10> energy drained from " +
        "</font><b><color=0xffffffff>Target Frigate</b><color=0x77ffffff><font size=10> - Small Nosferatu II</font>";

    // Someone neutralizing YOU. Says the exact same "energy neutralized" as the outgoing line above;
    // only the colour differs, which is why this was invisible before 2026-08-16.
    private static string EnergyNeutralizedAgainstMe(int amount, DateTime t) => Stamp(t) +
        $"(combat) <color=0xffe57f7f><b>{amount} GJ</b><color=0x77ffffff><font size=10> energy neutralized " +
        "</font><b><color=0xffffffff>Hostile Kikimora</b><color=0x77ffffff><font size=10> - Small Energy Neutralizer II</font>";

    private static string MinedUnits(int units, DateTime t) => MinedOre(units, "Veldspar", t);

    private static string MinedOre(int units, string ore, DateTime t) => Stamp(t) +
        $"(mining) <color=0x77ffffff>You mined <font size=12><color=#ff8dc169>{units}" +
        $"<color=0x77ffffff><font size=10> units of <color=0xffffffff><font size=12>{ore}";

    // Some ore lines carry trailing markup after the name. A name capture that runs to end-of-line
    // swallows it and turns the ore into an unrecognisable string.
    private static string MinedOreWithResidueSuffix(int units, string ore, DateTime t) => Stamp(t) +
        $"(mining) <color=0x77ffffff>You mined <font size=12><color=#ff8dc169>{units}" +
        $"<color=0x77ffffff><font size=10> units of <color=0xffffffff><font size=12>{ore}" +
        "<color=0x77ffffff><font size=10> with a lost residue of <font size=12><color=0xffaaaa00>0" +
        "<color=0x77ffffff><font size=10> units";

    private static string MinedCritical(int units, DateTime t) => Stamp(t) +
        "(mining) <color=#fff0ff45>Critical mining success!<color=0x77ffffff><font size=10> You mined an " +
        $"additional <color=#fff0ff45><font size=12>{units}<color=0x77ffffff><font size=10> units of " +
        "<color=0xffffffff><font size=12>Veldspar";

    // Asteroid waste, NOT yield. Counting it would overstate the mining rate.
    private static string MiningResidue(int units, DateTime t) => Stamp(t) +
        $"(mining) <color=0x77ffffff>Additional <font size=12><color=#ffff454b>{units}" +
        "<color=0x77ffffff><font size=10> units depleted from asteroid as residue";

    // -- Damage -------------------------------------------------------------------------------

    [Fact]
    public void RealDamageLinesParse()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", DamageOut(500, BaseTime), BaseTime);
        svc.Ingest("Pilot", DamageIn(250, BaseTime), BaseTime);

        var r = svc.GetReading("Pilot");
        Assert.Equal(50.0, r.DamageOut);
        Assert.Equal(25.0, r.DamageIn);
    }

    // -- Logistics ----------------------------------------------------------------------------

    [Fact]
    public void RealLogisticsLinesParseAndCombine()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", ArmorRepairedTo(300, BaseTime), BaseTime);
        svc.Ingest("Pilot", ShieldBoostedTo(200, BaseTime), BaseTime);

        var r = svc.GetReading("Pilot");
        Assert.Equal(50.0, r.Logistics);
        Assert.Equal(0.0, r.DamageOut);
        Assert.Equal(0.0, r.DamageIn);
    }

    // "repaired by" is someone repairing YOU. It must never inflate your own logi output, but it is
    // real information in its own right, so it lands in the received bucket rather than being dropped.
    [Fact]
    public void RepairedByCountsAsReceivedNotAsOutgoingLogistics()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", ArmorRepairedBy(500, BaseTime), BaseTime);

        var r = svc.GetReading("Pilot");
        Assert.Equal(0.0, r.Logistics);
        Assert.Equal(50.0, r.LogisticsIn);
        Assert.False(r.IsIdle);
    }

    [Fact]
    public void OutgoingAndReceivedLogisticsStaySeparate()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", ArmorRepairedTo(300, BaseTime), BaseTime);
        svc.Ingest("Pilot", ArmorRepairedBy(700, BaseTime), BaseTime);

        var r = svc.GetReading("Pilot");
        Assert.Equal(30.0, r.Logistics);
        Assert.Equal(70.0, r.LogisticsIn);
    }

    // -- Incoming cap warfare: the bug a client being capped out showed nothing ------------------

    [Fact]
    public void BeingNeutralizedIsCountedAsIncomingNotOutgoing()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", EnergyNeutralizedAgainstMe(280, BaseTime), BaseTime);

        var r = svc.GetReading("Pilot");
        Assert.Equal(28.0, r.NeutIn);
        Assert.Equal(0.0, r.NeutOut);
        Assert.False(r.IsIdle);
    }

    // The exact scenario from the field report: one client neuting another. The attacker's panel
    // must show outgoing cap and the victim's must show incoming -- never the same number on both.
    [Fact]
    public void NeuterAndVictimReadOppositeDirections()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Attacker", EnergyNeutralized(280, BaseTime), BaseTime);
        svc.Ingest("Victim", EnergyNeutralizedAgainstMe(280, BaseTime), BaseTime);

        var attacker = svc.GetReading("Attacker");
        var victim = svc.GetReading("Victim");

        Assert.Equal(28.0, attacker.NeutOut);
        Assert.Equal(0.0, attacker.NeutIn);
        Assert.Equal(28.0, victim.NeutIn);
        Assert.Equal(0.0, victim.NeutOut);
    }

    // -- Capacitor ----------------------------------------------------------------------------

    // Transferring cap and neuting are opposite effects, so they must not share a bucket: a fleet-mate
    // topping you up would otherwise cancel a hostile draining you and the pair would read as nothing.
    [Fact]
    public void CapTransferAndNeutAreSeparateOutgoingBuckets()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", CapacitorTransmittedTo(100, BaseTime), BaseTime);
        svc.Ingest("Pilot", EnergyNeutralized(60, BaseTime), BaseTime);
        svc.Ingest("Pilot", EnergyDrained(40, BaseTime), BaseTime);

        var r = svc.GetReading("Pilot");
        Assert.Equal(10.0, r.CapTransferOut);   // the transmitter only
        Assert.Equal(10.0, r.NeutOut);          // neut 60 + nos 40
    }

    [Fact]
    public void CapacitorTransmittedByIsReceivedTransferNotNeutPressure()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", CapacitorTransmittedBy(500, BaseTime), BaseTime);

        var r = svc.GetReading("Pilot");
        Assert.Equal(50.0, r.CapTransferIn);
        Assert.Equal(0.0, r.CapTransferOut);
        Assert.Equal(0.0, r.NeutIn);   // being HELPED must never read as being drained
        Assert.Equal(0.0, r.NeutOut);
    }

    [Fact]
    public void AllFourCapDirectionsStaySeparate()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", CapacitorTransmittedTo(100, BaseTime), BaseTime);
        svc.Ingest("Pilot", CapacitorTransmittedBy(200, BaseTime), BaseTime);
        svc.Ingest("Pilot", EnergyNeutralized(300, BaseTime), BaseTime);
        svc.Ingest("Pilot", EnergyNeutralizedAgainstMe(400, BaseTime), BaseTime);

        var r = svc.GetReading("Pilot");
        Assert.Equal(10.0, r.CapTransferOut);
        Assert.Equal(20.0, r.CapTransferIn);
        Assert.Equal(30.0, r.NeutOut);
        Assert.Equal(40.0, r.NeutIn);
    }

    // -- Mining -------------------------------------------------------------------------------

    // Veldspar is 0.1 m3 per unit, so 1000 units is 100 m3. Compared to a tolerance because the rate
    // round-trips through a divide and a multiply and lands a hair off exact.
    [Fact]
    public void RealMiningLineConvertsUnitsToCubicMetres()
    {
        var svc = new DpsMeterService(60);
        svc.Ingest("Pilot", MinedUnits(1000, BaseTime), BaseTime);

        Assert.Equal(100.0, svc.GetReading("Pilot").MiningM3PerMinute, 6);
    }

    // The whole reason conversion happens per line rather than on the summed units: a window can mix
    // ores whose volumes differ by two orders of magnitude.
    [Fact]
    public void MixedOresConvertAtTheirOwnVolumes()
    {
        var svc = new DpsMeterService(60);
        svc.Ingest("Pilot", MinedOre(1000, "Veldspar", BaseTime), BaseTime);   // 0.1  -> 100 m3
        svc.Ingest("Pilot", MinedOre(10, "Arkonor", BaseTime), BaseTime);      // 16.0 -> 160 m3

        Assert.Equal(260.0, svc.GetReading("Pilot").MiningM3PerMinute, 6);
    }

    // Variant adjectives and grade suffixes are richness tiers, not different minerals.
    [Theory]
    [InlineData("Veldspar", 0.1)]
    [InlineData("Concentrated Veldspar", 0.1)]
    [InlineData("Glistening Zeolites", 0.1)]
    [InlineData("Brimful Sylvite", 0.1)]
    [InlineData("Twinkling Euxenite", 0.1)]
    [InlineData("Arkonor", 16.0)]
    [InlineData("Dark Ochre", 8.0)]
    [InlineData("Omber III-Grade", 0.6)]
    [InlineData("Kernite II-Grade", 1.2)]
    public void OreVariantsResolveToTheirBaseVolume(string ore, double m3PerUnit)
    {
        var svc = new DpsMeterService(60);
        svc.Ingest("Pilot", MinedOre(100, ore, BaseTime), BaseTime);

        Assert.Equal(100 * m3PerUnit, svc.GetReading("Pilot").MiningM3PerMinute, 6);
    }

    // An ore with no volume on record must still produce a figure, and must announce itself so the
    // estimate is visible rather than silently baked into a number read as exact.
    [Fact]
    public void UnknownOreFallsBackAndIsReported()
    {
        var svc = new DpsMeterService(60);
        svc.Ingest("Pilot", MinedOre(100, "Argil Kylixium", BaseTime), BaseTime);

        Assert.Equal(10.0, svc.GetReading("Pilot").MiningM3PerMinute, 6);
        Assert.Contains("Kylixium", svc.UnknownOres);
    }

    [Fact]
    public void KnownOreIsNotReportedAsUnknown()
    {
        var svc = new DpsMeterService(60);
        svc.Ingest("Pilot", MinedOre(100, "Glistening Zeolites", BaseTime), BaseTime);

        Assert.Empty(svc.UnknownOres);
    }

    // Trailing markup after the ore name must not be absorbed into it -- otherwise a perfectly known
    // ore parses as gibberish, misses the volume table, and silently converts at the fallback rate.
    [Fact]
    public void TrailingResidueMarkupDoesNotCorruptTheOreName()
    {
        var svc = new DpsMeterService(60);
        svc.Ingest("Pilot", MinedOreWithResidueSuffix(100, "Arkonor", BaseTime), BaseTime);

        Assert.Equal(1600.0, svc.GetReading("Pilot").MiningM3PerMinute, 6);
        Assert.Empty(svc.UnknownOres);
    }

    [Fact]
    public void CriticalMiningSuccessCountsAsYield()
    {
        var svc = new DpsMeterService(60);
        svc.Ingest("Pilot", MinedCritical(500, BaseTime), BaseTime);

        Assert.Equal(50.0, svc.GetReading("Pilot").MiningM3PerMinute, 6);
    }

    [Fact]
    public void MiningResidueIsExcludedFromYield()
    {
        var svc = new DpsMeterService(60);
        svc.Ingest("Pilot", MiningResidue(9999, BaseTime), BaseTime);

        Assert.Equal(0.0, svc.GetReading("Pilot").MiningM3PerMinute);
        Assert.True(svc.GetReading("Pilot").IsIdle);
    }

    [Fact]
    public void MiningAndResidueInterleavedCountsOnlyYield()
    {
        var svc = new DpsMeterService(60);
        svc.Ingest("Pilot", MinedUnits(600, BaseTime), BaseTime);
        svc.Ingest("Pilot", MiningResidue(9999, BaseTime), BaseTime);
        svc.Ingest("Pilot", MinedCritical(400, BaseTime), BaseTime);

        // 600 + 400 units of Veldspar at 0.1 m3 = 100 m3; the 9999-unit residue line contributes none.
        Assert.Equal(100.0, svc.GetReading("Pilot").MiningM3PerMinute, 6);
    }

    // -- Cross-category -----------------------------------------------------------------------

    [Fact]
    public void EachLineIsCountedInExactlyOneCategory()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", DamageOut(100, BaseTime), BaseTime);
        svc.Ingest("Pilot", ArmorRepairedTo(100, BaseTime), BaseTime);
        svc.Ingest("Pilot", CapacitorTransmittedTo(100, BaseTime), BaseTime);

        var r = svc.GetReading("Pilot");
        Assert.Equal(10.0, r.DamageOut);
        Assert.Equal(10.0, r.Logistics);
        Assert.Equal(10.0, r.CapTransferOut);
        Assert.Equal(0.0, r.NeutOut);
        Assert.Equal(0.0, r.DamageIn);
    }

    [Fact]
    public void MissAndNonDamageCombatLinesAreIgnored()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", Stamp(BaseTime) + "(combat) Hostile Cruiser misses you completely", BaseTime);
        svc.Ingest("Pilot", Stamp(BaseTime) + "(combat) Your group of Small Focused Beam Laser II misses Target Frigate completely - Small Focused Beam Laser II", BaseTime);
        svc.Ingest("Pilot", Stamp(BaseTime) + "(notify) Reactive Armor Hardener requires 10.5 units of charge. The capacitor has only 3.6 units.", BaseTime);

        Assert.True(svc.GetReading("Pilot").IsIdle);
    }
}

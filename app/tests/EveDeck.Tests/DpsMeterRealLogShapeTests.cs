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

    private static string EnergyNeutralized(int amount, DateTime t) => Stamp(t) +
        $"(combat) <color=0xff7fffff><b>{amount}</b><color=0x77ffffff><font size=10> energy neutralized " +
        "<b><color=0xffffffff>Target Frigate</b><font size=10> - Small Energy Neutralizer II</font>";

    private static string EnergyDrained(int amount, DateTime t) => Stamp(t) +
        $"(combat) <color=0xffccff66><b>+{amount}</b><color=0x77ffffff><font size=10> energy drained from " +
        "<b><color=0xffffffff>Target Frigate</b><font size=10> - Small Nosferatu II</font>";

    private static string MinedUnits(int units, DateTime t) => Stamp(t) +
        $"(mining) <color=0x77ffffff>You mined <font size=12><color=#ff8dc169>{units}" +
        "<color=0x77ffffff><font size=10> units of <color=0xffffffff><font size=12>Veldspar";

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

    [Fact]
    public void RepairedByIsNotCountedAsOutgoingLogistics()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", ArmorRepairedBy(9999, BaseTime), BaseTime);

        var r = svc.GetReading("Pilot");
        Assert.Equal(0.0, r.Logistics);
        Assert.True(r.IsIdle);
    }

    // -- Capacitor ----------------------------------------------------------------------------

    [Fact]
    public void RealCapacitorLinesParseAndCombine()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", CapacitorTransmittedTo(100, BaseTime), BaseTime);
        svc.Ingest("Pilot", EnergyNeutralized(60, BaseTime), BaseTime);
        svc.Ingest("Pilot", EnergyDrained(40, BaseTime), BaseTime);

        var r = svc.GetReading("Pilot");
        Assert.Equal(20.0, r.Capacitor);
    }

    [Fact]
    public void CapacitorTransmittedByIsNotCountedAsOutgoing()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", CapacitorTransmittedBy(9999, BaseTime), BaseTime);

        Assert.True(svc.GetReading("Pilot").IsIdle);
    }

    // -- Mining -------------------------------------------------------------------------------

    [Fact]
    public void RealMiningLineParsesAsUnitsPerMinute()
    {
        var svc = new DpsMeterService(60);
        svc.Ingest("Pilot", MinedUnits(1000, BaseTime), BaseTime);

        // 1000 units over a 60s window, reported per minute, is 1000/min. Compared to a tolerance
        // because the rate round-trips through a divide and a multiply and lands a hair off exact.
        Assert.Equal(1000.0, svc.GetReading("Pilot").MiningUnitsPerMinute, 6);
    }

    [Fact]
    public void CriticalMiningSuccessCountsAsYield()
    {
        var svc = new DpsMeterService(60);
        svc.Ingest("Pilot", MinedCritical(500, BaseTime), BaseTime);

        Assert.Equal(500.0, svc.GetReading("Pilot").MiningUnitsPerMinute, 6);
    }

    [Fact]
    public void MiningResidueIsExcludedFromYield()
    {
        var svc = new DpsMeterService(60);
        svc.Ingest("Pilot", MiningResidue(9999, BaseTime), BaseTime);

        Assert.Equal(0.0, svc.GetReading("Pilot").MiningUnitsPerMinute);
        Assert.True(svc.GetReading("Pilot").IsIdle);
    }

    [Fact]
    public void MiningAndResidueInterleavedCountsOnlyYield()
    {
        var svc = new DpsMeterService(60);
        svc.Ingest("Pilot", MinedUnits(600, BaseTime), BaseTime);
        svc.Ingest("Pilot", MiningResidue(9999, BaseTime), BaseTime);
        svc.Ingest("Pilot", MinedCritical(400, BaseTime), BaseTime);

        Assert.Equal(1000.0, svc.GetReading("Pilot").MiningUnitsPerMinute, 6);
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
        Assert.Equal(10.0, r.Capacitor);
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

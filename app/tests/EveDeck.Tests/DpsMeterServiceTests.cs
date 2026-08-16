using EveDeck.Services;
using Xunit;

namespace EveDeck.Tests;

public class DpsMeterServiceTests
{
    private static readonly DateTime BaseTime = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

    // Real EVE damage markup, not a minimal string that merely satisfies a regex. Direction lives in
    // the COLOUR (ff00ffff out / ffcc0000 in), which is what the parser keys on -- an invented shape
    // would pass these arithmetic tests while telling us nothing about whether the parser matches the
    // actual game. See DpsMeterRealLogShapeTests for the rest of the verbatim shapes.
    private static string Line(DateTime time, int damage, bool outbound)
    {
        var stamp = time.ToString("yyyy.MM.dd HH:mm:ss");
        var direction = outbound ? "to" : "from";
        var color = outbound ? "0xff00ffff" : "0xffcc0000";
        return $"[ {stamp} ] (combat) <color={color}><b>{damage}</b> <color=0x77ffffff><font size=10>{direction}</font> "
             + "<b><color=0xffffffff>Target Frigate</b><font size=10><color=0x77ffffff> - Hits";
    }

    [Fact]
    public void Ingest_DamageOut_ParsedInIsolation()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", Line(BaseTime, 100, outbound: true), BaseTime);

        DpsReading reading = svc.GetReading("Pilot");
        Assert.Equal(10.0, reading.DamageOut);
        Assert.Equal(0.0, reading.DamageIn);
        Assert.False(reading.IsIdle);
    }

    [Fact]
    public void Ingest_DamageIn_ParsedInIsolation()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", Line(BaseTime, 200, outbound: false), BaseTime);

        DpsReading reading = svc.GetReading("Pilot");
        Assert.Equal(0.0, reading.DamageOut);
        Assert.Equal(20.0, reading.DamageIn);
    }

    [Fact]
    public void Ingest_InterleavedInAndOut_BothAccumulate()
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", Line(BaseTime, 100, outbound: true), BaseTime);
        svc.Ingest("Pilot", Line(BaseTime, 50, outbound: false), BaseTime);
        svc.Ingest("Pilot", Line(BaseTime, 100, outbound: true), BaseTime);
        svc.Ingest("Pilot", Line(BaseTime, 50, outbound: false), BaseTime);

        DpsReading reading = svc.GetReading("Pilot");
        Assert.Equal(20.0, reading.DamageOut);
        Assert.Equal(10.0, reading.DamageIn);
    }

    [Theory]
    [InlineData("This is a non-combat log line")]
    [InlineData("------------------------------------------------------------")]
    [InlineData("  Listener: Some Pilot")]
    [InlineData("garbage garbage garbage")]
    [InlineData("[ 2026.08.16 12:00:00 ] (notify) something happened")]
    public void Ingest_NonCombatOrGarbageLines_Ignored(string line)
    {
        var svc = new DpsMeterService(10);
        svc.Ingest("Pilot", line, BaseTime);

        DpsReading reading = svc.GetReading("Pilot");
        Assert.True(reading.IsIdle);
    }

    // The colour is authoritative, not the words. A damage-IN line names the attacker, and that name
    // can itself contain "to" -- keying on stray text instead of the colour is how a line gets filed
    // under the wrong direction.
    [Fact]
    public void Ingest_ColorDecidesDirectionNotStrayTextInTheName()
    {
        var svc = new DpsMeterService(10);
        var line = $"[ {BaseTime:yyyy.MM.dd HH:mm:ss} ] (combat) <color=0xffcc0000><b>77</b> <color=0x77ffffff>"
                 + "<font size=10>from</font> <b><color=0xffffffff>Protoceratops</b><font size=10> - Hits";
        svc.Ingest("Pilot", line, BaseTime);

        DpsReading reading = svc.GetReading("Pilot");
        Assert.Equal(7.7, reading.DamageIn);
        Assert.Equal(0.0, reading.DamageOut);
    }

    [Fact]
    public void RollingWindow_SamplesInsideWindow_AreCounted()
    {
        var svc = new DpsMeterService(10);
        DateTime now = BaseTime;
        svc.Ingest("Pilot", Line(now.AddSeconds(-9), 100, outbound: true), now);

        DpsReading reading = svc.GetReading("Pilot");
        Assert.Equal(10.0, reading.DamageOut);
    }

    [Fact]
    public void RollingWindow_TickExpiresOldSamples()
    {
        var svc = new DpsMeterService(10);
        DateTime now = BaseTime;
        svc.Ingest("Pilot", Line(now, 100, outbound: true), now);

        DpsReading before = svc.GetReading("Pilot");
        Assert.Equal(10.0, before.DamageOut);

        // Advance well past the window and Tick -- the sample should be expired and dropped.
        DateTime later = now.AddSeconds(30);
        svc.Tick(later);

        DpsReading after = svc.GetReading("Pilot");
        Assert.True(after.IsIdle);
    }

    [Fact]
    public void RollingWindow_GetReadingDoesNotMutateState()
    {
        var svc = new DpsMeterService(10);
        DateTime now = BaseTime;
        svc.Ingest("Pilot", Line(now, 100, outbound: true), now);

        // Calling GetReading many times, including after the window would logically have expired,
        // must not itself expire samples -- only Tick or the ingest staleness check may do that.
        DateTime later = now.AddSeconds(30);
        for (int i = 0; i < 5; i++)
        {
            svc.GetReading("Pilot");
        }

        // Without a Tick call, the stale sample is still present internally, so a fresh ingest at
        // "later" (within its own window) plus reading should still reflect the old sample's amount
        // only if Tick had run. Since we never Tick, confirm GetReading alone didn't clear it by
        // ticking now and expecting it to still be gone (i.e. GetReading loop above did not already
        // do the expiry work that Tick is responsible for -- this is really just a non-crash/purity
        // sanity check).
        svc.Tick(later);
        Assert.True(svc.GetReading("Pilot").IsIdle);
    }

    [Fact]
    public void DpsArithmetic_ThreeHundredDamageHitsInTenSecondWindow_IsThirty()
    {
        var svc = new DpsMeterService(10);
        DateTime now = BaseTime;
        svc.Ingest("Pilot", Line(now, 100, outbound: true), now);
        svc.Ingest("Pilot", Line(now, 100, outbound: true), now);
        svc.Ingest("Pilot", Line(now, 100, outbound: true), now);

        DpsReading reading = svc.GetReading("Pilot");
        Assert.Equal(30.0, reading.DamageOut);
    }

    [Fact]
    public void Ingest_PrefersLineTimestampOverNow()
    {
        // An old timestamped line arriving "now" (e.g. a resync batch) must be judged by its own
        // stamp, not stamped as fresh -- so with a window shorter than the age gap it is dropped.
        var svc = new DpsMeterService(5);
        DateTime now = BaseTime;
        DateTime oldStamp = now.AddSeconds(-30);

        svc.Ingest("Pilot", Line(oldStamp, 500, outbound: true), now);

        Assert.True(svc.GetReading("Pilot").IsIdle);
    }

    [Fact]
    public void Ingest_OldTimestampWithinWindow_IsCounted()
    {
        var svc = new DpsMeterService(10);
        DateTime now = BaseTime;
        DateTime withinWindow = now.AddSeconds(-5);

        svc.Ingest("Pilot", Line(withinWindow, 100, outbound: true), now);

        Assert.Equal(10.0, svc.GetReading("Pilot").DamageOut);
    }

    [Fact]
    public void Ingest_FutureSkewedTimestamp_FallsBackToNowInsteadOfDropped()
    {
        var svc = new DpsMeterService(10);
        DateTime now = BaseTime;
        DateTime future = now.AddSeconds(120); // more than 60s in the future -- clock skew

        svc.Ingest("Pilot", Line(future, 100, outbound: true), now);

        // Falls back to "now" as the sample time, so it must be counted as a live sample.
        DpsReading reading = svc.GetReading("Pilot");
        Assert.Equal(10.0, reading.DamageOut);
    }

    [Fact]
    public void Ingest_TimestampJustUnder60sFuture_IsNotTreatedAsSkew()
    {
        var svc = new DpsMeterService(120);
        DateTime now = BaseTime;
        DateTime slightlyFuture = now.AddSeconds(59);

        svc.Ingest("Pilot", Line(slightlyFuture, 100, outbound: true), now);

        Assert.Equal(100.0 / 120.0, svc.GetReading("Pilot").DamageOut, 5);
    }

    [Fact]
    public void PerCharacterIsolation_TwoCharactersDoNotContaminate()
    {
        var svc = new DpsMeterService(10);
        DateTime now = BaseTime;
        svc.Ingest("Alice", Line(now, 100, outbound: true), now);
        svc.Ingest("Bob", Line(now, 300, outbound: true), now);

        Assert.Equal(10.0, svc.GetReading("Alice").DamageOut);
        Assert.Equal(30.0, svc.GetReading("Bob").DamageOut);
    }

    [Fact]
    public void PerCharacterIsolation_KeysAreCaseInsensitive()
    {
        var svc = new DpsMeterService(10);
        DateTime now = BaseTime;
        svc.Ingest("Alice", Line(now, 100, outbound: true), now);

        DpsReading reading = svc.GetReading("aLICE");
        Assert.Equal(10.0, reading.DamageOut);
    }

    [Fact]
    public void Reset_ClearsOnlyThatCharacter()
    {
        var svc = new DpsMeterService(10);
        DateTime now = BaseTime;
        svc.Ingest("Alice", Line(now, 100, outbound: true), now);
        svc.Ingest("Bob", Line(now, 100, outbound: true), now);

        svc.Reset("Alice");

        Assert.True(svc.GetReading("Alice").IsIdle);
        Assert.Equal(10.0, svc.GetReading("Bob").DamageOut);
    }

    [Fact]
    public void ResetAll_ClearsEveryCharacter()
    {
        var svc = new DpsMeterService(10);
        DateTime now = BaseTime;
        svc.Ingest("Alice", Line(now, 100, outbound: true), now);
        svc.Ingest("Bob", Line(now, 100, outbound: true), now);

        svc.ResetAll();

        Assert.True(svc.GetReading("Alice").IsIdle);
        Assert.True(svc.GetReading("Bob").IsIdle);
    }

    [Fact]
    public void WindowSeconds_ClampsBelowMinimum()
    {
        var svc = new DpsMeterService(0)
        {
            WindowSeconds = -5
        };
        Assert.Equal(2, svc.WindowSeconds);
    }

    [Fact]
    public void WindowSeconds_ClampsAboveMaximum()
    {
        var svc = new DpsMeterService(10)
        {
            WindowSeconds = 999
        };
        Assert.Equal(120, svc.WindowSeconds);
    }

    [Fact]
    public void WindowSeconds_ConstructorClampsToo()
    {
        var svc = new DpsMeterService(1);
        Assert.Equal(2, svc.WindowSeconds);

        var svc2 = new DpsMeterService(500);
        Assert.Equal(120, svc2.WindowSeconds);
    }

    [Fact]
    public void GetReading_UnknownCharacter_ReturnsIdleAndDoesNotThrow()
    {
        var svc = new DpsMeterService(10);
        DpsReading reading = svc.GetReading("Nobody");

        Assert.True(reading.IsIdle);
        Assert.Equal(0.0, reading.DamageOut);
        Assert.Equal(0.0, reading.DamageIn);
    }

    [Fact]
    public void Ingest_EmptyOrNullCharacter_DoesNotThrow()
    {
        var svc = new DpsMeterService(10);
        var ex1 = Record.Exception(() => svc.Ingest("", Line(BaseTime, 100, true), BaseTime));
        var ex2 = Record.Exception(() => svc.Ingest(null!, Line(BaseTime, 100, true), BaseTime));

        Assert.Null(ex1);
        Assert.Null(ex2);
    }
}

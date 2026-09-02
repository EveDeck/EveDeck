using EveDeck.Services;
using Xunit;

namespace EveDeck.Tests;

// Pins the GPU-reset backoff contract used by TileSurfaceWindow: a burst of DWM composition-change
// broadcasts (a driver TDR) must stop preview re-registration for a cool-off, since re-registering
// into a recovering driver was observed causing a second reset (2026-09-02).
public class CompositionStormGuardTests
{
    private static CompositionStormGuard New() =>
        new(stormCount: 3, window: TimeSpan.FromSeconds(10), backoff: TimeSpan.FromSeconds(30));

    private static readonly DateTime T0 = new(2026, 9, 2, 12, 35, 0, DateTimeKind.Utc);

    [Fact]
    public void TwoRefreshesInsideWindow_NotAStorm()
    {
        var g = New();
        Assert.False(g.RegisterRefreshAndDetectStorm(T0));
        Assert.False(g.RegisterRefreshAndDetectStorm(T0.AddSeconds(1)));
        Assert.False(g.InBackoff(T0.AddSeconds(1)));
    }

    [Fact]
    public void ThirdRefreshInsideWindow_TripsStormAndStartsBackoff()
    {
        var g = New();
        g.RegisterRefreshAndDetectStorm(T0);
        g.RegisterRefreshAndDetectStorm(T0.AddSeconds(1));
        Assert.True(g.RegisterRefreshAndDetectStorm(T0.AddSeconds(2)));   // storm
        Assert.True(g.InBackoff(T0.AddSeconds(2)));
        Assert.True(g.InBackoff(T0.AddSeconds(31)));                      // still cooling off
        Assert.False(g.InBackoff(T0.AddSeconds(33)));                     // 30s elapsed
    }

    [Fact]
    public void DuringBackoff_EveryRefreshIsSuppressed()
    {
        var g = New();
        g.RegisterRefreshAndDetectStorm(T0);
        g.RegisterRefreshAndDetectStorm(T0.AddSeconds(1));
        g.RegisterRefreshAndDetectStorm(T0.AddSeconds(2));               // storm -> backoff until T0+32s
        Assert.True(g.RegisterRefreshAndDetectStorm(T0.AddSeconds(5)));
        Assert.True(g.RegisterRefreshAndDetectStorm(T0.AddSeconds(20)));
    }

    [Fact]
    public void AfterBackoffExpires_CounterHasReset_NextBurstStartsFresh()
    {
        var g = New();
        g.RegisterRefreshAndDetectStorm(T0);
        g.RegisterRefreshAndDetectStorm(T0.AddSeconds(1));
        g.RegisterRefreshAndDetectStorm(T0.AddSeconds(2));               // storm, backoff to T0+32s
        var afterCooloff = T0.AddSeconds(40);
        Assert.False(g.RegisterRefreshAndDetectStorm(afterCooloff));     // count 1, not a storm
        Assert.False(g.RegisterRefreshAndDetectStorm(afterCooloff.AddSeconds(1)));  // count 2
        Assert.True(g.RegisterRefreshAndDetectStorm(afterCooloff.AddSeconds(2)));   // count 3 -> storm again
    }

    [Fact]
    public void RefreshesSpreadBeyondTheWindow_NeverStorm()
    {
        var g = New();
        for (var i = 0; i < 10; i++)
            Assert.False(g.RegisterRefreshAndDetectStorm(T0.AddSeconds(i * 6))); // one every 6s, window is 10s
    }
}

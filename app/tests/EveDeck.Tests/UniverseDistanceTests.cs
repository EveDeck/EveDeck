using EveDeck.Services.Intel;
using Xunit;

namespace EveDeck.Tests;

// Ported from EveDeck-Intel daemon/src/test/kotlin/dev/eveintel/daemon/UniverseDistanceTest.kt.
//
// Multi-origin jump distance.
//
// This is what the tablet's alert radius is measured with: a pilot running several clients wants
// "how close is this to *any* of mine", not a distance from one nominated character. Getting it
// wrong is not a visible crash -- it silently alerts on the wrong things -- so it is pinned here.
//
// Systems are Providence and Catch, deliberately nowhere near anybody's real home.
public class UniverseDistanceTests
{
    private static readonly Universe SharedUniverse = Universe.LoadFromEmbeddedResource();

    private static int Id(string name)
    {
        var system = SharedUniverse.SystemByName(name);
        Assert.NotNull(system);
        return system!.Id;
    }

    [Fact]
    public void ASingleOriginMatchesTheSingleArgumentOverload()
    {
        var origin = Id("9UY4-H");
        Assert.Equal(SharedUniverse.DistancesFrom(origin), SharedUniverse.DistancesFrom(new[] { origin }));
    }

    [Fact]
    public void DistanceIsTakenFromTheNearestOrigin()
    {
        var near = Id("9UY4-H");
        var far = Id("KBP7-G");
        var target = Id("KBP7-G");

        // Four jumps from one, zero from the other; the pair must report the closer.
        Assert.Equal(4, SharedUniverse.DistancesFrom(near)[target]);
        Assert.Equal(0, SharedUniverse.DistancesFrom(new[] { near, far })[target]);
    }

    [Fact]
    public void EveryOriginIsItsOwnZero()
    {
        var origins = new[] { Id("9UY4-H"), Id("KBP7-G") };
        var distances = SharedUniverse.DistancesFrom(origins);
        foreach (var origin in origins)
        {
            Assert.Equal(0, distances[origin]);
        }
    }

    [Fact]
    public void AddingAnOriginNeverIncreasesADistance()
    {
        var first = Id("9UY4-H");
        var second = Id("F4R2-Q");
        var one = SharedUniverse.DistancesFrom(new[] { first });
        var both = SharedUniverse.DistancesFrom(new[] { first, second });
        foreach (var (system, jumps) in one)
        {
            Assert.True(both.TryGetValue(system, out var merged), "reachable system disappeared when adding an origin");
            Assert.True(merged <= jumps, $"distance to {system} grew from {jumps} to {merged}");
        }
    }

    [Fact]
    public void NoOriginsMeansNoDistancesRatherThanEverythingAtZero()
    {
        // The caller reads an empty map as "range unknown". Returning distances-from-nowhere, or
        // treating it as reachable, would turn an unknown range into a false in-range alert.
        Assert.Empty(SharedUniverse.DistancesFrom(Array.Empty<int>()));
    }

    [Fact]
    public void UnknownOriginsAreIgnoredRatherThanPoisoningTheResult()
    {
        var real = Id("9UY4-H");
        Assert.Equal(
            SharedUniverse.DistancesFrom(new[] { real }),
            SharedUniverse.DistancesFrom(new[] { real, -1 }));
        Assert.Empty(SharedUniverse.DistancesFrom(new[] { -1 }));
    }
}

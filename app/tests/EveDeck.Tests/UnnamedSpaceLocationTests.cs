using EveDeck.ViewModels;
using Xunit;

namespace EveDeck.Tests;

// In abyssal space EVE writes the Local channel's system as literally "Unknown" rather than a name,
// which used to land on the pill verbatim and overwrite the real system underneath it. The fix keeps
// the last real system and hangs a state tag off it ("<system> (Abyss)"). These pin the two pieces of
// that which are pure functions: which system ids count as abyssal, and the pill colour staying with
// the system rather than following the tag.
public class UnnamedSpaceLocationTests
{
    // Verified live against ESI 2026-08-14: 32000001 is "AD001" (region ADR01) and 32000480 already
    // returns "System not found", so the pockets sit inside 32xxxxxx with room to spare. K-space is
    // 30xxxxxx and wormholes are 31xxxxxx, and neither may ever be claimed as abyssal.
    [Theory]
    [InlineData(32_000_001, true)]   // AD001, the first abyssal pocket
    [InlineData(32_000_100, true)]   // AD100
    [InlineData(32_000_479, true)]   // last pocket in use
    [InlineData(32_999_999, true)]   // headroom for pockets CCP has not added yet
    [InlineData(30_000_142, false)]  // Jita
    [InlineData(31_000_001, false)]  // wormhole space
    [InlineData(0, false)]
    public void IsAbyssalSystemId_MatchesOnlyTheAbyssalIdBlock(int systemId, bool expected)
        => Assert.Equal(expected, MainWindowViewModel.IsAbyssalSystemId(systemId));

    // A filament always returns you to the system you launched it from, so a character in the abyss
    // has NOT split off from the fleet's system -- their pill must keep that system's colour. The
    // palette exists to spot a straggler at a glance, and a colour flip on entering the abyss would
    // read as exactly that.
    [Theory]
    [InlineData("<system>", "<system> (Abyss)")]
    [InlineData("<system>", "<system> (?)")]
    [InlineData("Jita", "Jita (Abyss)")]
    public void SystemColorHex_IgnoresTheStateTag(string plain, string tagged)
        => Assert.Equal(MainWindowViewModel.SystemColorHex(plain), MainWindowViewModel.SystemColorHex(tagged));

    // The tag must not be the only thing separating two systems' colours, or the strip above would be
    // hiding a real difference rather than ignoring noise.
    [Fact]
    public void SystemColorHex_StillSeparatesDifferentSystems()
        => Assert.NotEqual(MainWindowViewModel.SystemColorHex("<system>"), MainWindowViewModel.SystemColorHex("Jita"));

    [Fact]
    public void SystemColorHex_IsEmptyForNoSystem()
        => Assert.Equal("", MainWindowViewModel.SystemColorHex(""));
}

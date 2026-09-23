using System.IO;
using Xunit;
using EveDeck.Utilities;

namespace EveDeck.Tests;

public class DiagnosticsRedactorTests
{
    [Fact]
    public void Redact_WholeWordOnly_DoesNotTouchSubstringMatch()
    {
        var result = DiagnosticsRedactor.Redact("Bob and Bobcat were online", new[] { "Bob" }, Array.Empty<string>(), Array.Empty<string>());

        Assert.Equal("<character> and Bobcat were online", result);
    }

    [Fact]
    public void Redact_IsCaseInsensitive()
    {
        var result = DiagnosticsRedactor.Redact("BOB logged in", new[] { "Bob" }, Array.Empty<string>(), Array.Empty<string>());

        Assert.Equal("<character> logged in", result);
    }

    [Fact]
    public void Redact_LongestNameFirst_LeavesNoRemainder()
    {
        var result = DiagnosticsRedactor.Redact("Bob Alt logged in", new[] { "Bob Alt", "Bob" }, Array.Empty<string>(), Array.Empty<string>());

        Assert.Equal("<character> logged in", result);
    }

    [Fact]
    public void Redact_ApostropheAndHyphenNames_AreReplaced()
    {
        var result = DiagnosticsRedactor.Redact("O'Neil-Test undocked", new[] { "O'Neil-Test" }, Array.Empty<string>(), Array.Empty<string>());

        Assert.Equal("<character> undocked", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Redact_EmptyOrWhitespaceNames_AreIgnored(string name)
    {
        var result = DiagnosticsRedactor.Redact("Test Pilot undocked in Jita", new[] { name }, Array.Empty<string>(), Array.Empty<string>());

        Assert.Equal("Test Pilot undocked in Jita", result);
    }

    [Fact]
    public void Redact_SystemNames_BecomeSystemPlaceholder()
    {
        var result = DiagnosticsRedactor.Redact("Fleet is in Jita now", Array.Empty<string>(), new[] { "Jita" }, Array.Empty<string>());

        Assert.Equal("Fleet is in <system> now", result);
    }

    [Fact]
    public void Redact_UserNames_BecomeUserPlaceholder()
    {
        var result = DiagnosticsRedactor.Redact("Config for levi on DESKTOP", Array.Empty<string>(), Array.Empty<string>(), new[] { "levi", "DESKTOP" });

        Assert.Equal("Config for <user> on <user>", result);
    }

    [Fact]
    public void RedactPath_ReplacesUserProfilePrefix()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(profile, "AppData", "Roaming", "EveDeck", "config.json");

        var result = DiagnosticsRedactor.RedactPath(path);

        Assert.StartsWith("%USERPROFILE%", result);
        Assert.DoesNotContain(profile, result);
    }

    [Fact]
    public void RedactPath_LeavesOtherPathsUnchanged()
    {
        const string path = @"D:\EveDeck-App\current\EveDeck.exe";

        var result = DiagnosticsRedactor.RedactPath(path);

        Assert.Equal(path, result);
    }
}

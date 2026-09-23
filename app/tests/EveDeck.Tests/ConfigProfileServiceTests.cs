using Xunit;
using EveDeck.Models;
using EveDeck.Services;
using System.Reflection;

namespace EveDeck.Tests;

public class ConfigProfileServiceTests
{
    private static string[] Whitelist() =>
        (string[])typeof(ConfigProfileService)
            .GetField("AppearanceProperties", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    // Properties() silently drops a name that isn't a public read/write property, so a setting
    // whose setter goes private would quietly stop being saved in config profiles. Catch it here.
    [Fact]
    public void EveryWhitelistedName_IsAPublicReadWriteProperty()
    {
        foreach (var name in Whitelist())
        {
            var p = typeof(AppSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(p is { CanRead: true, CanWrite: true }, $"{name} is not a public read/write property");
            Assert.True(p!.SetMethod!.IsPublic, $"{name} has a non-public setter");
        }
    }

    [Fact]
    public void CaptureThenApply_RestoresChangedValues()
    {
        var original = new AppSettings
        {
            CornerOverlaysEnabled = false,
            CornerOverlayLabelFontSize = 13.5,
            ActiveFrameColor = "#123456",
        };
        var profile = new ConfigProfile();
        ConfigProfileService.Capture(profile, original);

        var target = new AppSettings();
        var applied = ConfigProfileService.ApplyAppearance(profile, target);

        Assert.Equal(Whitelist().Length, applied);
        Assert.False(target.CornerOverlaysEnabled);
        Assert.Equal(13.5, target.CornerOverlayLabelFontSize);
        Assert.Equal("#123456", target.ActiveFrameColor);
    }

    [Fact]
    public void Apply_SkipsStaleAndMalformedKeys()
    {
        var profile = new ConfigProfile();
        profile.Appearance["SettingRemovedInSomeLaterVersion"] = "true";
        profile.Appearance[nameof(AppSettings.CornerOverlayLabelFontSize)] = "\"not a number\"";
        profile.Appearance[nameof(AppSettings.ActiveFrameColor)] = "\"#ABCDEF\"";

        var target = new AppSettings();
        var before = target.CornerOverlayLabelFontSize;
        var applied = ConfigProfileService.ApplyAppearance(profile, target);

        Assert.Equal(1, applied);
        Assert.Equal("#ABCDEF", target.ActiveFrameColor);
        Assert.Equal(before, target.CornerOverlayLabelFontSize);
    }

    [Fact]
    public void Capture_ReplacesPreviousSnapshot()
    {
        var profile = new ConfigProfile();
        profile.Appearance["Leftover"] = "1";

        ConfigProfileService.Capture(profile, new AppSettings());

        Assert.False(profile.Appearance.ContainsKey("Leftover"));
        Assert.Equal(Whitelist().Length, profile.Appearance.Count);
    }
}

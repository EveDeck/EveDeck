using Xunit;
using EveDeck.Services;
using EveDeck.Utilities;
using System.Text.Json;

namespace EveDeck.Tests;

// The release parser is the only thing standing between a GitHub response and running an
// installer, so every fail-closed rule gets a case.
public class UpdateCheckServiceTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Release(
        string tag = "v1.60.0",
        bool draft = false,
        bool prerelease = false,
        string? assetName = null,
        string? assetUrl = null,
        string? digest = "sha256:" + Sha,
        bool includeAsset = true)
    {
        var version = tag.TrimStart('v');
        var name = assetName ?? $"EveDeck-Setup-v{version}.exe";
        var url = assetUrl ?? $"https://github.com/EveDeck/EveDeck/releases/download/{tag}/{name}";
        var assets = new List<object>
        {
            new { name = "EveDeck-win-Portable.zip", browser_download_url = $"https://github.com/EveDeck/EveDeck/releases/download/{tag}/EveDeck-win-Portable.zip", digest = "sha256:" + Sha },
        };
        if (includeAsset) assets.Add(new { name, browser_download_url = url, digest });
        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft,
            prerelease,
            html_url = "https://evil.example/should-not-be-used",
            assets,
        });
    }

    [Fact]
    public void NewerRelease_WithVerifiedInstaller_CanAutoInstall()
    {
        var info = UpdateCheckService.ParseRelease(Release(), "1.57.0");

        Assert.NotNull(info);
        Assert.Equal("1.60.0", info!.Version);
        Assert.True(info.CanAutoInstall);
        Assert.Equal("https://github.com/EveDeck/EveDeck/releases/download/v1.60.0/EveDeck-Setup-v1.60.0.exe", info.InstallerUrl);
        Assert.Equal(Sha, info.InstallerSha256);
    }

    [Fact]
    public void ReleasePage_IsRebuiltFromPinnedRepo_NotReadFromResponse()
    {
        var info = UpdateCheckService.ParseRelease(Release(), "1.57.0");
        Assert.Equal("https://github.com/EveDeck/EveDeck/releases/tag/v1.60.0", info!.ReleasePageUrl);
    }

    [Theory]
    [InlineData("1.60.0")]
    [InlineData("1.61.0")]
    public void SameOrOlderRelease_IsNotAnUpdate(string current)
    {
        Assert.Null(UpdateCheckService.ParseRelease(Release(), current));
    }

    [Fact]
    public void Draft_IsIgnored() => Assert.Null(UpdateCheckService.ParseRelease(Release(draft: true), "1.0.0"));

    [Fact]
    public void Prerelease_IsIgnored() => Assert.Null(UpdateCheckService.ParseRelease(Release(prerelease: true), "1.0.0"));

    [Theory]
    [InlineData("1.60.0")]
    [InlineData("v1.60")]
    [InlineData("v1.60.0-beta")]
    [InlineData("intel-v0.3.2")]
    [InlineData("v1.60.0/../../x")]
    public void MalformedTag_IsIgnored(string tag)
    {
        Assert.Null(UpdateCheckService.ParseRelease(Release(tag: tag), "1.0.0"));
    }

    [Fact]
    public void MissingDigest_FallsBackToReleasePage()
    {
        var info = UpdateCheckService.ParseRelease(Release(digest: null), "1.57.0");
        Assert.NotNull(info);
        Assert.False(info!.CanAutoInstall);
    }

    [Theory]
    [InlineData("sha1:0123456789abcdef0123456789abcdef01234567")]
    [InlineData("sha256:0123")]
    [InlineData("sha256:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void MalformedDigest_FallsBackToReleasePage(string digest)
    {
        Assert.False(UpdateCheckService.ParseRelease(Release(digest: digest), "1.57.0")!.CanAutoInstall);
    }

    [Theory]
    [InlineData("https://evil.example/EveDeck-Setup-v1.60.0.exe")]
    [InlineData("http://github.com/EveDeck/EveDeck/releases/download/v1.60.0/EveDeck-Setup-v1.60.0.exe")]
    [InlineData("https://github.com/SomeoneElse/EveDeck/releases/download/v1.60.0/EveDeck-Setup-v1.60.0.exe")]
    [InlineData("https://github.com/EveDeck/EveDeck/releases/download/v1.59.0/EveDeck-Setup-v1.60.0.exe")]
    public void UnexpectedDownloadUrl_FallsBackToReleasePage(string url)
    {
        Assert.False(UpdateCheckService.ParseRelease(Release(assetUrl: url), "1.57.0")!.CanAutoInstall);
    }

    [Fact]
    public void InstallerForADifferentVersion_IsNotUsed()
    {
        var json = Release(assetName: "EveDeck-Setup-v1.59.0.exe");
        Assert.False(UpdateCheckService.ParseRelease(json, "1.57.0")!.CanAutoInstall);
    }

    [Fact]
    public void NoInstallerAsset_FallsBackToReleasePage()
    {
        Assert.False(UpdateCheckService.ParseRelease(Release(includeAsset: false), "1.57.0")!.CanAutoInstall);
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\EveDeck\", @"C:\Users\x\AppData\Local\Programs\EveDeck", true)]
    [InlineData(@"c:\users\x\appdata\local\programs\evedeck", @"C:\Users\x\AppData\Local\Programs\EveDeck\", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\EveDeck", @"D:\EveDeck-Portable", false)]
    public void InstallKind_SameDirectory(string a, string b, bool expected)
    {
        Assert.Equal(expected, InstallKind.SameDirectory(a, b));
    }
}

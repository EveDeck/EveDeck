using Xunit;
using EveDeck.Services;
using System.IO;

namespace EveDeck.Tests;

// Profile Sync overwrites real EVE settings files on every selected alt, so the file-handling
// paths here are the ones worth pinning down: which files count, and that a copy never destroys
// the target without leaving a backup behind.
public class EveSettingsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly EveSettingsService _service = new();

    public EveSettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Touch(string name, string content = "x", DateTime? mtimeUtc = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        if (mtimeUtc is { } t) File.SetLastWriteTimeUtc(path, t);
        return path;
    }

    [Theory]
    [InlineData("core_char_12345.dat", "12345")]
    [InlineData("CORE_CHAR_9.DAT", "9")]
    [InlineData("core_char__.dat", null)]
    [InlineData("core_char_12345_evedeck_backup_20260101_120000.dat", null)]
    [InlineData("core_user_12345.dat", null)]
    public void GetCharacterId_MatchesOnlyRealCharFiles(string name, string? expected)
    {
        Assert.Equal(expected, EveSettingsService.GetCharacterId(Path.Combine(_dir, name)));
    }

    [Theory]
    [InlineData("core_user_777.dat", "777")]
    [InlineData("core_user_777_evedeck_backup_20260101_120000.dat", null)]
    [InlineData("core_char_777.dat", null)]
    public void GetUserId_MatchesOnlyRealUserFiles(string name, string? expected)
    {
        Assert.Equal(expected, EveSettingsService.GetUserId(Path.Combine(_dir, name)));
    }

    [Fact]
    public void GetCharacterFiles_ExcludesBackupsAndUserFiles()
    {
        Touch("core_char_2.dat");
        Touch("core_char_1.dat");
        Touch("core_char_1_evedeck_backup_20260101_120000.dat");
        Touch("core_user_5.dat");

        var files = _service.GetCharacterFiles(_dir).Select(Path.GetFileName).ToList();

        Assert.Equal(new[] { "core_char_1.dat", "core_char_2.dat" }, files);
    }

    [Fact]
    public void GetUserFiles_ExcludesBackupsAndCharFiles()
    {
        Touch("core_user_5.dat");
        Touch("core_user_5_evedeck_backup_20260101_120000.dat");
        Touch("core_char_1.dat");

        var files = _service.GetUserFiles(_dir).Select(Path.GetFileName).ToList();

        Assert.Equal(new[] { "core_user_5.dat" }, files);
    }

    [Fact]
    public void GetFiles_MissingFolder_ReturnsEmpty()
    {
        var missing = Path.Combine(_dir, "nope");
        Assert.Empty(_service.GetCharacterFiles(missing));
        Assert.Empty(_service.GetUserFiles(missing));
    }

    [Fact]
    public void GetFolderDisplayName_NamesServerAndResolution()
    {
        var path = Path.Combine("C:", "CCP", "EVE", "c_eve_sharedcache_tq_tranquility", "settings_Default");
        Assert.Equal("Tranquility · Default", EveSettingsService.GetFolderDisplayName(path));
    }

    [Fact]
    public void CopyProfile_OverwritesTargetAndBacksUpOriginal()
    {
        var source = Touch("core_char_1.dat", "source");
        var target = Touch("core_char_2.dat", "original");

        Assert.Null(_service.CopyProfile(source, target));

        Assert.Equal("source", File.ReadAllText(target));
        var backup = Assert.Single(Directory.GetFiles(_dir, "core_char_2_evedeck_backup_*.dat"));
        Assert.Equal("original", File.ReadAllText(backup));
        Assert.Equal("source", File.ReadAllText(source));
    }

    [Fact]
    public void CopyProfile_NoExistingTarget_CopiesWithoutBackup()
    {
        var source = Touch("core_char_1.dat", "source");
        var target = Path.Combine(_dir, "core_char_2.dat");

        Assert.Null(_service.CopyProfile(source, target));

        Assert.Equal("source", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(_dir, "*_evedeck_backup_*"));
    }

    [Fact]
    public void CopyProfile_TwiceInOneSecond_KeepsBothBackups()
    {
        var source = Touch("core_char_1.dat", "source");
        var target = Touch("core_char_2.dat", "first");

        Assert.Null(_service.CopyProfile(source, target));
        File.WriteAllText(target, "second");
        Assert.Null(_service.CopyProfile(source, target));

        var backups = Directory.GetFiles(_dir, "core_char_2_evedeck_backup_*.dat")
            .Select(File.ReadAllText).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "first", "second" }, backups);
    }

    [Fact]
    public void CopyProfile_MissingSource_ReturnsErrorAndLeavesTargetIntact()
    {
        var target = Touch("core_char_2.dat", "original");

        var error = _service.CopyProfile(Path.Combine(_dir, "core_char_404.dat"), target);

        Assert.NotNull(error);
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Fact]
    public void PairCharactersToAccounts_PicksClosestUserFileWithinTolerance()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var charA = Touch("core_char_1.dat", mtimeUtc: t0);
        var charB = Touch("core_char_2.dat", mtimeUtc: t0.AddHours(1));
        var charC = Touch("core_char_3.dat", mtimeUtc: t0.AddDays(3));
        var userX = Touch("core_user_10.dat", mtimeUtc: t0.AddSeconds(5));
        var userY = Touch("core_user_20.dat", mtimeUtc: t0.AddHours(1).AddSeconds(-3));

        var map = EveSettingsService.PairCharactersToAccounts(new[] { charA, charB, charC }, new[] { userX, userY });

        Assert.Equal("10", map["1"]);
        Assert.Equal("20", map["2"]);
        Assert.False(map.ContainsKey("3")); // nothing within 60s -- left unmapped, not guessed
    }

    [Fact]
    public void PairCharactersToAccounts_NoUserFiles_ReturnsEmpty()
    {
        var charA = Touch("core_char_1.dat");
        Assert.Empty(EveSettingsService.PairCharactersToAccounts(new[] { charA }, Array.Empty<string>()));
    }
}

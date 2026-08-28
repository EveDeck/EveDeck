using Xunit;
using EveDeck.Services;
using System.IO;

namespace EveDeck.Tests;

public class EsiTokenStoreTests : IDisposable
{
    private readonly string _tempDir;

    public EsiTokenStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static EsiToken Sample(long id = 90000001) => new()
    {
        CharacterId = id,
        CharacterName = "Test Pilot",
        RefreshToken = "refresh-secret-abc",
        AccessToken = "access-jwt-xyz",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20),
        Scopes = new() { "publicData", EsiAuthService.ScopeSkills },
    };

    [Fact]
    public void Put_ThenGet_RoundTripsInMemory()
    {
        var store = new EsiTokenStore(_tempDir);
        store.Put(Sample());

        var got = store.Get(90000001);
        Assert.NotNull(got);
        Assert.Equal("refresh-secret-abc", got!.RefreshToken);
        Assert.True(got.HasScope(EsiAuthService.ScopeSkills));
    }

    [Fact]
    public void Tokens_PersistAcrossInstances_Encrypted()
    {
        new EsiTokenStore(_tempDir).Put(Sample());

        // A fresh instance reads the DPAPI-encrypted file from disk.
        var reopened = new EsiTokenStore(_tempDir);
        Assert.True(reopened.Has(90000001));
        Assert.Equal("Test Pilot", reopened.Get(90000001)!.CharacterName);
    }

    [Fact]
    public void OnDiskFile_DoesNotContainPlaintextSecret()
    {
        var store = new EsiTokenStore(_tempDir);
        store.Put(Sample());

        var bytes = File.ReadAllBytes(store.Path);
        var asText = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("refresh-secret-abc", asText);
    }

    [Fact]
    public void Remove_DeletesToken()
    {
        var store = new EsiTokenStore(_tempDir);
        store.Put(Sample());
        store.Remove(90000001);
        Assert.False(store.Has(90000001));
        Assert.Null(new EsiTokenStore(_tempDir).Get(90000001));
    }

    [Fact]
    public void UnreadableFile_IsMovedAside_NotDeleted()
    {
        var store = new EsiTokenStore(_tempDir);
        store.Put(Sample());
        // Simulate the post-reinstall case: a tokens file this DPAPI key cannot open.
        File.WriteAllBytes(store.Path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var reopened = new EsiTokenStore(_tempDir);

        Assert.True(reopened.WasUnreadable);
        Assert.False(reopened.Has(90000001));
        Assert.False(File.Exists(store.Path));
        Assert.NotNull(reopened.QuarantinedPath);
        Assert.True(File.Exists(reopened.QuarantinedPath!));
    }

    [Fact]
    public void Export_ThenImport_RestoresGrantsOnAFreshStore()
    {
        var source = new EsiTokenStore(_tempDir);
        source.Put(Sample());
        source.Put(Sample(90000002));

        var exportPath = Path.Combine(_tempDir, "links.edtok");
        Assert.Equal(2, source.Export(exportPath, "correct horse battery"));

        // A different folder stands in for a different machine/profile.
        var targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(targetDir);
        var target = new EsiTokenStore(targetDir);
        Assert.Equal(2, target.Import(exportPath, "correct horse battery"));

        Assert.Equal("refresh-secret-abc", target.Get(90000002)!.RefreshToken);
        // And it survives a restart on the new machine, i.e. it was re-encrypted at rest.
        Assert.True(new EsiTokenStore(targetDir).Has(90000001));
    }

    [Fact]
    public void ExportFile_DoesNotContainPlaintextSecret()
    {
        var store = new EsiTokenStore(_tempDir);
        store.Put(Sample());
        var exportPath = Path.Combine(_tempDir, "links.edtok");
        store.Export(exportPath, "correct horse battery");

        var asText = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(exportPath));
        Assert.DoesNotContain("refresh-secret-abc", asText);
    }

    [Fact]
    public void Import_WithWrongPassphrase_Throws_AndLeavesStoreUntouched()
    {
        var source = new EsiTokenStore(_tempDir);
        source.Put(Sample());
        var exportPath = Path.Combine(_tempDir, "links.edtok");
        source.Export(exportPath, "correct horse battery");

        var targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(targetDir);
        var target = new EsiTokenStore(targetDir);

        Assert.Throws<InvalidDataException>(() => target.Import(exportPath, "wrong passphrase"));
        Assert.False(target.Has(90000001));
    }

    [Fact]
    public void Import_RejectsAFileThatIsNotAnExport()
    {
        var notAnExport = Path.Combine(_tempDir, "notes.edtok");
        File.WriteAllText(notAnExport, "just some text, definitely not a token export");

        var store = new EsiTokenStore(_tempDir);
        Assert.Throws<InvalidDataException>(() => store.Import(notAnExport, "correct horse battery"));
    }

    [Fact]
    public void Export_WithNoLinkedCharacters_WritesNothing()
    {
        var store = new EsiTokenStore(_tempDir);
        var exportPath = Path.Combine(_tempDir, "links.edtok");

        Assert.Equal(0, store.Export(exportPath, "correct horse battery"));
        Assert.False(File.Exists(exportPath));
    }

    [Fact]
    public void IsExpired_HonoursThirtySecondSkew()
    {
        var almostExpired = Sample();
        almostExpired.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(10); // inside the 30s buffer
        Assert.True(almostExpired.IsExpired);

        var fresh = Sample();
        fresh.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        Assert.False(fresh.IsExpired);
    }
}

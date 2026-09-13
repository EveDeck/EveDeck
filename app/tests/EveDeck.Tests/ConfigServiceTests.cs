using Xunit;
using EveDeck.Services;
using EveDeck.Models;
using System.IO;
using System.Text.Json;

namespace EveDeck.Tests;

public class ConfigServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ConfigService _configService;

    public ConfigServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configService = new ConfigService(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void Load_NoFile_CreatesDefaultsAndPersists()
    {
        var settings = _configService.Load();

        Assert.Equal(8, settings.Assignments.Count);
        Assert.NotEmpty(settings.Profiles);

        var gridProfile = settings.Profiles.FirstOrDefault(p => p.Category == "Grid");
        Assert.NotNull(gridProfile);

        var centerMasterProfile = settings.Profiles.FirstOrDefault(p => p.Category == "Center Master");
        Assert.NotNull(centerMasterProfile);

        Assert.NotEmpty(settings.ActiveProfileId);
        Assert.Contains(settings.Profiles, p => p.Id == settings.ActiveProfileId);

        Assert.True(File.Exists(_configService.ConfigPath));
    }

    [Fact]
    public void Load_CorruptJson_BacksUpBakAndStartsFresh()
    {
        Directory.CreateDirectory(_tempDir);
        var corruptContent = "{ this is not valid json }";
        File.WriteAllText(_configService.ConfigPath, corruptContent);

        var settings = _configService.Load();
        Assert.Equal(8, settings.Assignments.Count);

        Assert.True(File.Exists(_configService.ConfigPath + ".bak"));
        Assert.Equal(corruptContent, File.ReadAllText(_configService.ConfigPath + ".bak"));
    }

    [Fact]
    public void Load_MigratesSingleWindowAssignmentToList()
    {
        var settings = _configService.Load();

        var firstAssignment = settings.Assignments[0];
        firstAssignment.AssignedWindowTitle = "Test Window";
        firstAssignment.LastProcessId = 1234;
        firstAssignment.LastHandleHex = "deadbeef";

        _configService.Save(settings);

        var configService2 = new ConfigService(_tempDir);
        var reloadedSettings = configService2.Load();

        var reloadedAssignment = reloadedSettings.Assignments[0];
        Assert.NotNull(reloadedAssignment.AssignedWindows);
        Assert.NotEmpty(reloadedAssignment.AssignedWindows);

        var windowEntry = reloadedAssignment.AssignedWindows.First();
        Assert.Equal("Test Window", windowEntry.Title);
        Assert.Null(reloadedAssignment.AssignedWindowTitle);
    }

    [Fact]
    public void Load_WrapsLegacySettingsIntoDefaultCharacterSet()
    {
        var settings = _configService.Load();

        Assert.NotEmpty(settings.CharacterSets);
        var defaultSet = settings.CharacterSets.FirstOrDefault(cs => cs.Name == "Default");
        Assert.NotNull(defaultSet);

        Assert.NotEmpty(defaultSet.Assignments);
        Assert.NotEmpty(defaultSet.Hotkeys);
    }

    [Fact]
    public void CreateBackup_SkipsWhenSettingsCorrupt()
    {
        _configService.Load();

        var backupsBefore = Directory.Exists(_configService.BackupsFolder)
            ? Directory.GetFiles(_configService.BackupsFolder, "settings_backup_*.json").Length
            : 0;

        File.WriteAllText(_configService.ConfigPath, "corrupt json");

        _configService.CreateBackup();

        var backupsAfter = Directory.Exists(_configService.BackupsFolder)
            ? Directory.GetFiles(_configService.BackupsFolder, "settings_backup_*.json").Length
            : 0;

        Assert.Equal(backupsBefore, backupsAfter);
    }

    [Fact]
    public void RestoreBackup_ThrowsOnCorruptBackup()
    {
        var corruptBackupPath = Path.Combine(_tempDir, "corrupt_backup.json");
        File.WriteAllText(corruptBackupPath, "not valid json");

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            _configService.RestoreBackup(corruptBackupPath);
        });

        Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsProfiles()
    {
        var settings = _configService.Load();

        var customProfile = new LayoutProfile
        {
            Name = "Custom Test Profile",
            Category = "Custom"
        };
        customProfile.Slots.Add(new LayoutSlot { SlotNumber = 1, X = 0, Y = 0, Width = 100, Height = 100 });

        settings.Profiles.Add(customProfile);
        _configService.Save(settings);

        var configService2 = new ConfigService(_tempDir);
        var reloadedSettings = configService2.Load();

        var foundProfile = reloadedSettings.Profiles.FirstOrDefault(p => p.Name == "Custom Test Profile");
        Assert.NotNull(foundProfile);
        Assert.Single(foundProfile.Slots);
        Assert.Equal(1, foundProfile.Slots[0].SlotNumber);
    }

    [Fact]
    public void Load_DefaultsOfflinePillTimeoutSecondsToZero()
    {
        var settings = _configService.Load();
        Assert.Equal(0, settings.OfflinePillTimeoutSeconds);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsOfflinePillTimeoutSeconds()
    {
        var settings = _configService.Load();
        settings.OfflinePillTimeoutSeconds = 30;
        _configService.Save(settings);

        var configService2 = new ConfigService(_tempDir);
        var reloadedSettings = configService2.Load();

        Assert.Equal(30, reloadedSettings.OfflinePillTimeoutSeconds);
    }

    [Fact]
    public void Load_DefaultsCornerOverlayLabelFontMasterProperties()
    {
        var settings = _configService.Load();
        Assert.Equal("Michroma", settings.CornerOverlayLabelFontFamilyMaster);
        Assert.Equal(27.0, settings.CornerOverlayLabelFontSizeMaster);
        Assert.Equal("#E5E7EB", settings.CornerOverlayLabelColorMaster);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsCornerOverlayLabelFontMasterProperties()
    {
        var settings = _configService.Load();
        settings.CornerOverlayLabelFontFamilyMaster = "Arial";
        settings.CornerOverlayLabelFontSizeMaster = 28.0;
        settings.CornerOverlayLabelColorMaster = "#FF0000";
        _configService.Save(settings);

        var configService2 = new ConfigService(_tempDir);
        var reloadedSettings = configService2.Load();

        Assert.Equal("Arial", reloadedSettings.CornerOverlayLabelFontFamilyMaster);
        Assert.Equal(28.0, reloadedSettings.CornerOverlayLabelFontSizeMaster);
        Assert.Equal("#FF0000", reloadedSettings.CornerOverlayLabelColorMaster);
    }

    // The bundled default label font was "Acens" until it was replaced with Michroma on licensing
    // grounds (personal/non-commercial only, so not redistributable under EveDeck's GPL-3.0).
    // Settings written before the swap still name it and it is no longer shipped, so Load() rewrites
    // it -- otherwise the Options font picker shows a font that is not installed.
    [Fact]
    public void Load_MigratesRetiredAcensFontToBundledDefault()
    {
        var settings = _configService.Load();
        settings.CornerOverlayLabelFontFamily = "Acens";
        settings.CornerOverlayLabelFontFamilyMaster = "Acens";
        _configService.Save(settings);

        var reloaded = new ConfigService(_tempDir).Load();

        Assert.Equal("Michroma", reloaded.CornerOverlayLabelFontFamily);
        Assert.Equal("Michroma", reloaded.CornerOverlayLabelFontFamilyMaster);
    }

    // A font the user deliberately chose must survive Load() untouched -- the migration above is
    // scoped to the one retired name, not a general reset to the default.
    [Fact]
    public void Load_LeavesUserChosenFontAlone()
    {
        var settings = _configService.Load();
        settings.CornerOverlayLabelFontFamilyMaster = "Consolas";
        _configService.Save(settings);

        Assert.Equal("Consolas", new ConfigService(_tempDir).Load().CornerOverlayLabelFontFamilyMaster);
    }

    // Save() writes through a reused MemoryStream + Utf8JsonWriter rather than serializing to a
    // string, to keep ~250KB allocations off the Large Object Heap. Indentation then comes from the
    // writer's options instead of the serializer's, so this pins the on-disk bytes to exactly what
    // the plain string path would have produced -- a silent format change here would rewrite every
    // user's settings.json on first launch.
    [Fact]
    public void Save_WritesBytesIdenticalToIndentedStringSerialization()
    {
        var settings = _configService.Load();
        settings.CornerOverlayLabelFontFamilyMaster = "Arial";
        _configService.Save(settings);

        var onDisk = File.ReadAllBytes(_configService.ConfigPath);
        var expected = System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(expected, onDisk);
    }

    // The skip-write short-circuit compares a SHA-256 of the fresh JSON against the last written
    // hash. Save() is called far more often than settings change, so this is what keeps the periodic
    // refresh loop from doing a temp-write + File.Replace every few seconds.
    [Fact]
    public void Save_Unchanged_SkipsWrite_ThenWritesAgainOnChange()
    {
        // Load() persists on the way out, so the skip-write hash is already primed here.
        var settings = _configService.Load();

        Assert.False(_configService.Save(settings));  // unchanged since Load's own write -> skipped

        settings.CornerOverlayLabelFontSizeMaster = 31.0;
        Assert.True(_configService.Save(settings));   // changed -> written
        Assert.False(_configService.Save(settings));  // unchanged again -> skipped
    }

    // An externally deleted settings.json must be recreated even when nothing changed in memory,
    // otherwise the skip-write hash would leave the user with no config file at all.
    [Fact]
    public void Save_Unchanged_StillRewritesWhenFileDeleted()
    {
        var settings = _configService.Load();
        _configService.Save(settings);
        File.Delete(_configService.ConfigPath);

        Assert.True(_configService.Save(settings));
        Assert.True(File.Exists(_configService.ConfigPath));
    }

    // RestoreBackup stages to a temp file and atomically replaces, and must clear the skip-write
    // hash: the file on disk no longer matches what Save() last wrote, so an unchanged in-memory
    // AppSettings would otherwise suppress the next save and silently re-overwrite the restore.
    [Fact]
    public void RestoreBackup_ReplacesConfigAndForcesNextSave()
    {
        var settings = _configService.Load();
        settings.CornerOverlayLabelFontFamilyMaster = "Arial";
        _configService.Save(settings);
        _configService.CreateBackup();

        var backup = _configService.GetBackups().First();

        settings.CornerOverlayLabelFontFamilyMaster = "Consolas";
        _configService.Save(settings);

        _configService.RestoreBackup(backup.Path);

        Assert.Equal("Arial", new ConfigService(_tempDir).Load().CornerOverlayLabelFontFamilyMaster);
        Assert.False(File.Exists(_configService.ConfigPath + ".restore.tmp"));
        // In-memory settings are unchanged since the last Save(), but the restore invalidated the
        // hash, so this must still write rather than short-circuit.
        Assert.True(_configService.Save(settings));
    }
}

using System.IO;
using System.Text.Json;
using EveDeck.Models;
using EveDeck.Services;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    private SettingsBackup? _selectedBackup;

    public IReadOnlyList<SettingsBackup> AvailableBackups => _configService.GetBackups();

    public SettingsBackup? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (SetProperty(ref _selectedBackup, value))
            {
                RestoreBackupCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(HasSelectedBackup));
            }
        }
    }

    public bool HasSelectedBackup => _selectedBackup is not null;

    public void RefreshBackups() => OnPropertyChanged(nameof(AvailableBackups));

    public void CreateBackupNow()
    {
        _configService.CreateBackup();
        OnPropertyChanged(nameof(AvailableBackups));
        Log.Info("Settings backup created.");
        Status = "Backup created.";
    }

    // Returns null on success, or an error message to show the user.
    public string? RestoreSelectedBackup()
    {
        if (_selectedBackup is null) return null;
        try
        {
            _configService.RestoreBackup(_selectedBackup.Path);
            Log.Info($"Restored settings from {_selectedBackup.DisplayName}. Restarting...");
            RestartApp();
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Restore failed: {ex.Message}");
            return ex.Message;
        }
    }

    public void ExportSettings(string destPath)
    {
        try
        {
            File.Copy(_configService.ConfigPath, destPath, overwrite: true);
            Log.Info($"Settings exported to {destPath}.");
            Status = "Settings exported.";
        }
        catch (Exception ex) { Log.Error($"Export failed: {ex.Message}"); }
    }

    public void ImportSettings(string sourcePath)
    {
        try
        {
            // Validate JSON before overwriting.
            JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(sourcePath), new JsonSerializerOptions());
            _configService.CreateBackup(); // snapshot current before overwrite
            File.Copy(sourcePath, _configService.ConfigPath, overwrite: true);
            Log.Info($"Settings imported from {sourcePath}. Restarting...");
            RestartApp();
        }
        catch (Exception ex) { Log.Error($"Import failed: {ex.Message}"); }
    }

    private static void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }
}

using System.IO;
using System.Reflection;
using System.Windows.Input;
using EveDeck.Services;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    private UpdateCheckService.UpdateInfo? _availableUpdate;
    private bool _updateBannerDismissed;
    private bool _isCheckingForUpdate;

    private async Task CheckForUpdateAsync(bool manual = false)
    {
        // The Store build never self-updates -- a packaged app cannot rewrite its own install
        // directory, and Windows already keeps Store apps current. Say so rather than offering a
        // check that could only ever end in a download the app is not permitted to apply.
        if (Utilities.PackagedAppInfo.IsPackaged)
        {
            if (manual)
            {
                UpdateCheckStatusText = "Updates for the Microsoft Store version are delivered by the Store itself.";
                OnPropertyChanged(nameof(UpdateCheckStatusText));
            }
            return;
        }

        if (manual)
        {
            _isCheckingForUpdate = true;
            UpdateCheckStatusText = "Checking for updates...";
            OnPropertyChanged(nameof(UpdateCheckStatusText));
            OnPropertyChanged(nameof(IsCheckingForUpdate));
        }

        UpdateCheckService.UpdateInfo? info = null;
        var current = Assembly.GetExecutingAssembly().GetName().Version;
        if (current is not null)
        {
            var currentStr = $"{current.Major}.{current.Minor}.{current.Build}";
            info = await new UpdateCheckService(Log).CheckAsync(currentStr);
            if (info is not null) _availableUpdate = info;
        }

        App.Current.Dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(ShowUpdateBanner));
            OnPropertyChanged(nameof(UpdateVersionText));
            OnPropertyChanged(nameof(UpdateButtonText));
            OnPropertyChanged(nameof(UpdateButtonToolTip));

            // Only the automatic startup check offers the changelog (a manual "Check for updates"
            // click from Options shouldn't pop a window on top of the user), and only once per run
            // so it doesn't re-trigger if the user re-checks later in the same session.
            if (!manual && info is not null && !_updateChangelogOfferedThisSession)
            {
                _updateChangelogOfferedThisSession = true;
                UpdateBecameAvailable?.Invoke(info.Version);
            }

            if (!manual) return;
            _isCheckingForUpdate = false;
            UpdateCheckStatusText = info is not null ? $"EveDeck {info.Version} is available" : "You're up to date.";
            OnPropertyChanged(nameof(UpdateCheckStatusText));
            OnPropertyChanged(nameof(IsCheckingForUpdate));
        });
    }

    // Raised once the verified installer is running, so the window exits the same way tray > Exit
    // does (restoring windows, saving settings) and frees the install folder for the installer.
    public event Action? ExitForUpdateRequested;

    private bool _isApplyingUpdate;
    private bool _autoUpdateFailed; // after a failed install, the button falls back to the release page
    private string? _updateProgressText;

    // Only the Inno install updates itself; the portable build only ever opens the release page.
    // Everything this uses comes from UpdateCheckService, which reads GitHub Releases only and pins
    // both the download URL and a SHA-256 -- see the header comment there. No Velopack: its stubs
    // were the ONLY thing VirusTotal ever flagged, and plain Inno re-install needs none.
    private bool CanAutoInstallUpdate =>
        !_autoUpdateFailed && Utilities.InstallKind.IsInnoInstall && _availableUpdate is { CanAutoInstall: true };

    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is not { } update || _isApplyingUpdate) return;

        if (!CanAutoInstallUpdate)
        {
            OpenReleasePage(update);
            return;
        }

        _isApplyingUpdate = true;
        SetUpdateProgress($"Downloading EveDeck {update.Version}...");
        try
        {
            await using var installer = await new UpdateCheckService(Log).DownloadVerifiedInstallerAsync(update);
            Log.Info($"Update {update.Version} downloaded and matched GitHub's SHA-256; starting the installer.");
            SetUpdateProgress($"Installing EveDeck {update.Version}...");

            var logPath = Path.Combine(Path.GetTempPath(), "EveDeck-update", "install.log");
            var start = new System.Diagnostics.ProcessStartInfo(installer.Name) { UseShellExecute = false };
            // /SILENT still shows a progress window, so the user sees the update happen. The
            // installer's own [Run] entry relaunches EveDeck; /NORESTARTAPPLICATIONS stops the
            // Restart Manager from starting a second copy on top of that.
            foreach (var arg in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS", $"/LOG={logPath}" })
                start.ArgumentList.Add(arg);
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex)
        {
            Log.Error($"Automatic update failed, nothing was installed: {ex}");
            _isApplyingUpdate = false;
            _autoUpdateFailed = true;
            SetUpdateProgress($"Update failed. Use Download to get EveDeck {update.Version} manually.");
            return;
        }

        App.Current.Dispatcher.Invoke(() => ExitForUpdateRequested?.Invoke());
    }

    private void OpenReleasePage(UpdateCheckService.UpdateInfo update)
    {
        // ReleasePageUrl is rebuilt from the pinned repo in UpdateCheckService, never read from a
        // response, so it is always an https://github.com/... page -- still, hand the shell nothing
        // else: UseShellExecute runs a local path rather than browsing it.
        if (!Uri.TryCreate(update.ReleasePageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Log.Error($"Refusing to open update link: {update.ReleasePageUrl}");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open the download page: {ex}");
        }
    }

    private void SetUpdateProgress(string? text)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            _updateProgressText = text;
            OnPropertyChanged(nameof(UpdateVersionText));
            OnPropertyChanged(nameof(UpdateButtonText));
            OnPropertyChanged(nameof(UpdateButtonToolTip));
            OnPropertyChanged(nameof(IsUpdateButtonEnabled));
        });
    }

    public bool ShowUpdateBanner => _availableUpdate is not null && !_updateBannerDismissed;
    public string UpdateVersionText => _updateProgressText
        ?? (_availableUpdate is not null ? $"EveDeck {_availableUpdate.Version} is available" : "");
    public string UpdateButtonText => CanAutoInstallUpdate && _updateProgressText is null ? "Update now" : "Download";
    public string UpdateButtonToolTip => CanAutoInstallUpdate
        ? "Download from GitHub, verify, install and restart EveDeck"
        : "Open the release page on GitHub";
    public bool IsUpdateButtonEnabled => !_isApplyingUpdate;
    public ICommand DismissUpdateBannerCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public ICommand CheckForUpdateCommand { get; }
    public string UpdateCheckStatusText { get; private set; } = "";
    public bool IsCheckingForUpdate => _isCheckingForUpdate;

    // ── "What's New" changelog window ───────────────────────────────────────────

    // Raised (once per app run) the first time the automatic startup update check finds a newer
    // version -- MainWindow uses this to pop the changelog window alongside the normal update
    // banner so the user can see what's actually in it before deciding to update.
    public event Action<string>? UpdateBecameAvailable;
    private bool _updateChangelogOfferedThisSession;

    public string CurrentVersionString
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    // True when the changelog hasn't been shown for the version currently running. Gated on
    // SetupCompleted so a genuinely fresh install (where the wizard owns first-run) never pops
    // this before the user has done anything -- MarkChangelogSeen() seeds the baseline right after
    // setup finishes. An existing settings.json from before this field existed also deserializes
    // LastSeenChangelogVersion as empty, which is exactly what makes this fire once for upgrading
    // users (SetupCompleted is already true for them), not just fresh installs.
    public bool NeedsChangelogCheck
    {
        get
        {
            if (!_settings.SetupCompleted) return false;
            var current = CurrentVersionString;
            return !string.IsNullOrEmpty(current) && _settings.LastSeenChangelogVersion != current;
        }
    }

    public void MarkChangelogSeen()
    {
        var current = CurrentVersionString;
        if (string.IsNullOrEmpty(current)) return;
        _settings.LastSeenChangelogVersion = current;
        Save();
    }
}

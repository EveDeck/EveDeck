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

    // EveDeck does not apply its own updates. The portable track ships as a plain zip the user
    // extracts over their copy, so the most this can do is open the download page. The Velopack
    // updater stubs that used to do this in-process were the ONLY thing VirusTotal ever flagged in
    // the portable download -- the app's own binary scores 0/71 and the installer, whose payload is
    // those same app files without a stub, scores 0/68. Dropping them removed the detection and a
    // whole class of update-path risk with it.
    private Task InstallUpdateAsync()
    {
        if (_availableUpdate is not { } update) return Task.CompletedTask;
        if (update.DownloadUrl is not { } url) return Task.CompletedTask;

        // Only ever hand a web address to the shell. UseShellExecute launches whatever the string
        // names -- a local path or a file:// URL gets executed, not browsed -- and this string
        // arrives from a remote API response (Services/UpdateCheckService reads evedeck.space's
        // /api/version). Checking the scheme is what keeps a spoofed or compromised manifest from
        // turning the update prompt into arbitrary execution: the same shape of hole that was
        // removed in v1.53.3, and now the only update path left, so it carries more weight.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Log.Error($"Refusing to open update link with an unexpected scheme: {url}");
            return Task.CompletedTask;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open the download page: {ex}");
        }

        return Task.CompletedTask;
    }

    public bool ShowUpdateBanner => _availableUpdate is not null && !_updateBannerDismissed;
    public string UpdateVersionText => _availableUpdate is not null ? $"EveDeck {_availableUpdate.Version} is available" : "";
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

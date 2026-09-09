using Velopack;
using Velopack.Sources;

namespace EveDeck.Services;

public enum InstallKind { Velopack, Unknown }

/// <summary>
/// Detects how the running copy of EveDeck got onto the machine and applies an update via
/// Velopack's own update manager. Portable is the only distribution track (see
/// TRADEMARKS.md/release history — the Inno installer track was dropped and its silent
/// re-exec-of-a-server-supplied-URL path was removed with it: it downloaded and ran whatever
/// URL the update-check API returned with no signature or hash verification).
/// </summary>
public sealed class UpdateApplyService
{
    private const string GithubRepoUrl = "https://github.com/EveDeck/EveDeck";

    private readonly LogService? _log;

    public UpdateApplyService(LogService? log = null)
    {
        _log = log;
    }

    public InstallKind DetectInstallKind()
    {
        var velopackManager = TryCreateVelopackManager();
        return velopackManager is { IsInstalled: true } ? InstallKind.Velopack : InstallKind.Unknown;
    }

    /// <summary>Checks, downloads, and applies via Velopack, restarting the app when done.</summary>
    public async Task ApplyVelopackUpdateAsync()
    {
        var mgr = TryCreateVelopackManager();
        if (mgr is not { IsInstalled: true }) return;

        var info = await mgr.CheckForUpdatesAsync();
        if (info is null) return;

        await mgr.DownloadUpdatesAsync(info);
        mgr.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    private UpdateManager? TryCreateVelopackManager()
    {
        try
        {
            return new UpdateManager(new GithubSource(GithubRepoUrl, null, false));
        }
        catch (Exception ex)
        {
            _log?.Warn($"Velopack UpdateManager unavailable: {ex.Message}");
            return null;
        }
    }
}

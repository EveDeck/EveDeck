using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Velopack;
using Velopack.Sources;

namespace EveDeck.Services;

public enum InstallKind { Velopack, Inno, Unknown }

/// <summary>
/// Detects how the running copy of EveDeck got onto the machine and applies an update using the
/// matching mechanism: Velopack's own update manager for Velopack-managed installs (portable or
/// Velopack-installed), or a silent re-run of the Inno Setup installer for Inno-managed installs.
/// Anything else (a raw zip extracted before this feature existed) has no mechanism and is the
/// caller's job to fall back to a manual download link.
/// </summary>
public sealed class UpdateApplyService
{
    private const string GithubRepoUrl = "https://github.com/EveDeck/EveDeck";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly LogService? _log;

    public UpdateApplyService(LogService? log = null)
    {
        _log = log;
    }

    public InstallKind DetectInstallKind()
    {
        var velopackManager = TryCreateVelopackManager();
        if (velopackManager is { IsInstalled: true })
            return InstallKind.Velopack;

        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "unins000.exe")))
            return InstallKind.Inno;

        return InstallKind.Unknown;
    }

    /// <summary>
    /// Downloads the given Setup.exe and re-runs it silently over the existing (fixed-AppId,
    /// per-user) install.
    ///
    /// VESTIGIAL as of the 2026-09 move to private development: EveDeck no longer publishes an
    /// installer, so a running Inno-kind install can never see an InstallerUrl and this method is
    /// not reached. Kept minimal (a plain launch, no PowerShell shim -- an earlier
    /// <c>powershell -ExecutionPolicy Bypass</c> shim here got the shipped binary flagged by
    /// Defender ML) rather than deleted, so an old Inno install could still be pointed at a
    /// hand-built Setup.exe if ever needed.
    /// </summary>
    public async Task ApplyInnoUpdateAsync(string installerUrl, Action<string, double?>? onProgress = null)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"EveDeck-Setup-{Guid.NewGuid():N}.exe");

        onProgress?.Invoke("Downloading update...", null);
        using (var response = await Http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            await using var httpStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(tempPath);
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;
                if (totalBytes is > 0)
                    onProgress?.Invoke("Downloading update...", 100.0 * totalRead / totalBytes.Value);
            }
        }

        onProgress?.Invoke("Installing update...", null);

        Process.Start(new ProcessStartInfo(tempPath)
        {
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
            UseShellExecute = true,
        });

        System.Windows.Application.Current.Shutdown();
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

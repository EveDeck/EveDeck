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
    /// Downloads the given Setup.exe (reporting progress via <paramref name="onProgress"/>) and
    /// re-runs it over the existing (fixed-AppId, per-user) install using /SILENT rather than
    /// /VERYSILENT -- both are equally unattended (no wizard pages, no "close applications"
    /// prompt), but /SILENT keeps Inno's small install-progress window visible so the update
    /// stays noticeable through the file-copy phase instead of the app just vanishing.
    ///
    /// The installer is NOT launched directly. Earlier this method spawned Setup.exe and then
    /// immediately called Application.Shutdown() -- which races App.OnExit's Environment.Exit
    /// hard-kill against Inno's Restart-Manager + solid-archive copy phase. On v1.52.0 that race
    /// dropped the large memory-mapped EveDeck.dll (while the tiny .exe/.deps.json/.runtimeconfig
    /// landed), leaving an install whose apphost had nothing to run. Instead we hand the install
    /// to a detached PowerShell shim that blocks on Wait-Process until THIS pid is gone, then
    /// starts Setup against a fully unlocked directory -- so /CLOSEAPPLICATIONS and its
    /// Restart-Manager dance are not needed at all. Relaunch after install is the installer's own
    /// [Run] section (EveDeck.iss); /RESTARTAPPLICATIONS alone did not reliably relaunch a silent
    /// install and risks racing the single-instance mutex into an "already running" popup.
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

        LaunchInstallerAfterExit(tempPath);
        System.Windows.Application.Current.Shutdown();
    }

    // Start a detached shim that waits for THIS process to exit, then runs the silent installer
    // against an unlocked install directory. See ApplyInnoUpdateAsync's summary for why the
    // installer must not run while EveDeck is still alive.
    private void LaunchInstallerAfterExit(string installerPath)
    {
        var pid = Environment.ProcessId;
        var escaped = installerPath.Replace("'", "''");
        var script =
            $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; "
            + $"Start-Process -FilePath '{escaped}' "
            + "-ArgumentList '/SILENT','/SUPPRESSMSGBOXES','/NORESTART'";

        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            // PowerShell missing / blocked (rare). Fall back to a direct launch with
            // /CLOSEAPPLICATIONS so an update still happens -- this is the old racy path, but a
            // best-effort update beats none.
            _log?.Warn($"Update shim via PowerShell failed ({ex.Message}); launching installer directly.");
            Process.Start(new ProcessStartInfo(installerPath)
            {
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                UseShellExecute = true,
            });
        }
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

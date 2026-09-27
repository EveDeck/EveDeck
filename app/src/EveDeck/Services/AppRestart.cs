using System.Diagnostics;

namespace EveDeck.Services;

internal static class AppRestart
{
    // Run before constructing WPF or acquiring the single-instance mutex. A bounded wait
    // leaves the existing instance in control if shutdown fails.
    internal static bool WaitForParent(string[] args, int timeoutMilliseconds = 30_000)
    {
        if (args.Length != 2 || args[0] != "--restart-parent") return true;
        if (!int.TryParse(args[1], out var pid) || pid <= 0 || pid == Environment.ProcessId) return false;
        try
        {
            using var parent = Process.GetProcessById(pid);
            return parent.WaitForExit(timeoutMilliseconds);
        }
        catch (ArgumentException) { return true; } // Parent already exited.
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}

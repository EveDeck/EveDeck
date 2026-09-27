using System.Diagnostics;
using EveDeck.Services;
using Xunit;

namespace EveDeck.Tests;

public class AppRestartTests
{
    [Fact]
    public void NormalLaunch_DoesNotWait() => Assert.True(AppRestart.WaitForParent([]));

    [Fact]
    public void Restart_DoesNotWaitForItself() =>
        Assert.False(AppRestart.WaitForParent(["--restart-parent", Environment.ProcessId.ToString()], 0));

    [Fact]
    public void Restart_WaitsForParentExit()
    {
        // Keep a child alive without relying on sleeps or network connectivity.
        using var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/d /q")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        })!;
        try
        {
            var args = new[] { "--restart-parent", parent.Id.ToString() };
            Assert.False(AppRestart.WaitForParent(args, 0));
            parent.StandardInput.WriteLine("exit");
            parent.StandardInput.Flush();
            Assert.True(AppRestart.WaitForParent(args, 5000));
        }
        finally { if (!parent.HasExited) parent.Kill(); }
    }
}

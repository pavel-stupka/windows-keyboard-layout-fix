using System.Diagnostics;
using System.Runtime.Versioning;

namespace KbFix.Platform.Install;

/// <summary>
/// Spawns a detached watcher child process from the installer. Uses direct
/// <c>CreateProcess</c> (via <see cref="Process.Start"/> with
/// <see cref="ProcessStartInfo.UseShellExecute"/> false) with no console
/// inheritance and no redirected standard streams, so closing the installer's
/// terminal does not terminate the watcher.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WatcherLauncher
{
    public static void SpawnDetached(string stagedBinaryPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = stagedBinaryPath,
            Arguments = "--watch",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = Path.GetDirectoryName(stagedBinaryPath) ?? Environment.CurrentDirectory,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to spawn watcher process from '{stagedBinaryPath}'.");
        // Do not call WaitForExit. Disposing the Process handle here releases
        // the parent's reference to the child; the OS keeps the child alive.
    }
}

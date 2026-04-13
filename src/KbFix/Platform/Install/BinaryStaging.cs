using System.Runtime.Versioning;
using KbFix.Watcher;

namespace KbFix.Platform.Install;

/// <summary>
/// Filesystem operations for staging the kbfix binary under
/// <c>%LOCALAPPDATA%\KbFix\</c>. All methods are per-user, no admin rights.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class BinaryStaging
{
    public static void EnsureStagingDirectory()
    {
        Directory.CreateDirectory(WatcherInstallation.StagingDirectory);
    }

    public static void CopyBinaryToStaged(string sourcePath)
    {
        EnsureStagingDirectory();
        File.Copy(sourcePath, WatcherInstallation.DefaultStagedBinaryPath, overwrite: true);
    }

    /// <summary>
    /// Returns true if the staged binary was deleted, false if it was already
    /// absent. Does not throw; filesystem errors are swallowed and reported as
    /// false so the caller can continue with the rest of the uninstall flow.
    /// </summary>
    public static bool DeleteStagedBinary()
    {
        try
        {
            if (!File.Exists(WatcherInstallation.DefaultStagedBinaryPath))
            {
                return false;
            }
            File.Delete(WatcherInstallation.DefaultStagedBinaryPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Uninstall from the staged binary is the one case where <see cref="DeleteStagedBinary"/>
    /// cannot work — Windows forbids deleting the executable of a running process.
    /// However, Windows DOES allow renaming a running executable. This method
    /// moves the staged binary to a unique file under <c>%TEMP%</c>, then schedules
    /// that temp copy for deletion at the next reboot via
    /// <c>MoveFileEx(..., MOVEFILE_DELAY_UNTIL_REBOOT)</c>. After this call the
    /// staging directory no longer contains the running executable, so the
    /// caller can delete the directory cleanly.
    /// </summary>
    /// <returns>True if the move succeeded. False if anything went wrong — in
    /// that case the caller should fall back to leaving the binary in place.</returns>
    public static bool MoveRunningBinaryToTempForRebootDelete()
    {
        try
        {
            var staged = WatcherInstallation.DefaultStagedBinaryPath;
            if (!File.Exists(staged))
            {
                return false;
            }

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"kbfix-uninstalled-{Guid.NewGuid():N}.exe");

            File.Move(staged, tempPath);

            // Schedule the temp copy for deletion at next reboot. Best-effort;
            // if this fails the file just sits in %TEMP% until the OS cleans it.
            try
            {
                Win32Interop.MoveFileEx(tempPath, null, Win32Interop.MOVEFILE_DELAY_UNTIL_REBOOT);
            }
            catch
            {
                // swallow
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void DeleteStagingDirectory()
    {
        try
        {
            // Clean up pid + log files before removing the directory.
            foreach (var path in new[]
            {
                WatcherInstallation.PidFilePath,
                WatcherInstallation.LogFilePath,
                WatcherInstallation.LogFileRotatedPath,
            })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // best-effort
                }
            }

            if (Directory.Exists(WatcherInstallation.StagingDirectory))
            {
                if (!Directory.EnumerateFileSystemEntries(WatcherInstallation.StagingDirectory).Any())
                {
                    Directory.Delete(WatcherInstallation.StagingDirectory);
                }
            }
        }
        catch
        {
            // best-effort
        }
    }
}

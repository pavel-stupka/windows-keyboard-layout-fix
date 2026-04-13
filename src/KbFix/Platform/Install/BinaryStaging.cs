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

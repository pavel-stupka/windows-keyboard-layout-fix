using System.Runtime.Versioning;
using System.Threading;
using KbFix.Watcher;

namespace KbFix.Platform.Install;

/// <summary>
/// Filesystem operations for staging the kbfix binary under
/// <c>%LOCALAPPDATA%\KbFix\</c>. All methods are per-user, no admin rights.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class BinaryStaging
{
    /// <summary>Outcome of <see cref="DeleteStagedBinary"/>.</summary>
    public enum DeleteOutcome
    {
        /// <summary>File was present and successfully deleted.</summary>
        Deleted,

        /// <summary>File was not present to begin with — nothing to do.</summary>
        NotPresent,

        /// <summary>File delete failed (handle still held, most commonly by
        /// this process or a watcher that has not yet fully released its
        /// executable mapping), but the file was successfully renamed out of
        /// the staging directory into <c>%TEMP%</c> and marked for deletion
        /// at next reboot. The staging directory is now empty.</summary>
        MovedForRebootDelete,

        /// <summary>Delete AND rename-to-temp both failed. The file remains
        /// in the staging directory. The uninstall step has partially failed.</summary>
        Failed,
    }

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
    /// Attempt to remove the staged binary. Handles three scenarios:
    /// <list type="number">
    /// <item>File is present and not held → <see cref="File.Delete(string)"/> succeeds on the first try.</item>
    /// <item>File is present but held transiently (a watcher just exited and Windows has not
    /// released the executable mapping yet) → we retry for up to ~2 seconds with 100 ms backoff.</item>
    /// <item>File is held by the currently-running process (uninstall invoked from the staged
    /// copy itself) → retries will keep failing, so we fall back to the Windows rename-trick:
    /// move the running .exe into <c>%TEMP%</c> and mark it for deletion at next reboot via
    /// <c>MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)</c>. After the rename the staging directory
    /// is empty, so the caller's directory-delete step will succeed normally.</item>
    /// </list>
    /// </summary>
    public static DeleteOutcome DeleteStagedBinary()
    {
        var path = WatcherInstallation.DefaultStagedBinaryPath;
        if (!File.Exists(path))
        {
            return DeleteOutcome.NotPresent;
        }

        const int MaxAttempts = 20;
        const int DelayMs = 100;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                File.Delete(path);
                return DeleteOutcome.Deleted;
            }
            catch (UnauthorizedAccessException)
            {
                // File handle still held — retry after a short wait.
            }
            catch (IOException)
            {
                // Typically "file in use" — retry.
            }
            catch
            {
                break;
            }

            if (attempt < MaxAttempts - 1)
            {
                Thread.Sleep(DelayMs);
            }
        }

        // Last resort: rename the file out of the staging directory and let
        // Windows delete it at the next reboot. Works for the self-uninstall
        // case and for any other persistent file-lock scenario.
        if (TryMoveRunningBinaryToTempForRebootDelete())
        {
            return DeleteOutcome.MovedForRebootDelete;
        }

        return DeleteOutcome.Failed;
    }

    private static bool TryMoveRunningBinaryToTempForRebootDelete()
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

    /// <summary>
    /// Remove the pid file, log files, and (if now empty) the staging directory.
    /// Each file delete retries briefly in case a watcher process has just
    /// exited and its handles are still in the OS close queue. Best-effort
    /// throughout — never throws.
    /// </summary>
    public static void DeleteStagingDirectory()
    {
        TryDeleteWithRetry(WatcherInstallation.PidFilePath);
        TryDeleteWithRetry(WatcherInstallation.LogFilePath);
        TryDeleteWithRetry(WatcherInstallation.LogFileRotatedPath);

        try
        {
            if (Directory.Exists(WatcherInstallation.StagingDirectory)
                && !Directory.EnumerateFileSystemEntries(WatcherInstallation.StagingDirectory).Any())
            {
                Directory.Delete(WatcherInstallation.StagingDirectory);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static void TryDeleteWithRetry(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            catch
            {
                return;
            }

            Thread.Sleep(100);
        }
    }
}

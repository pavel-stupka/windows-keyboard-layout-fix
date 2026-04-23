using System.Runtime.Versioning;
using System.Threading;
using KbFix.Watcher;
using Microsoft.Win32;

namespace KbFix.Platform.Install;

/// <summary>
/// Probes the per-user installed state of the KbFix watcher and writes it
/// into a <see cref="WatcherInstallation"/> record. Read-only with the
/// exception of <see cref="SignalStopEvent"/> and <see cref="ForceKillWatcher"/>,
/// which are the cooperative and forceful stop paths used by <c>--uninstall</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WatcherDiscovery
{
    public static WatcherInstallation Probe()
    {
        var stagedPath = WatcherInstallation.DefaultStagedBinaryPath;
        var stagedExists = File.Exists(stagedPath);

        var (autostartPresent, autostartTarget) = ReadRunKey();
        var autostartPointsAtStaged = autostartPresent
            && autostartTarget is not null
            && RunKeyValueMatches(autostartTarget, stagedPath);

        var watcherRunning = IsWatcherRunning();
        var watcherPid = watcherRunning ? TryReadPidFile() : null;

        // --- 004 probe surface ---
        ScheduledTaskEntry taskEntry;
        try
        {
            taskEntry = ScheduledTaskRegistry.Query();
        }
        catch
        {
            taskEntry = ScheduledTaskEntry.Absent;
        }

        var runKeyApproved = StartupApprovedProbe.IsRunKeyApproved();

        LastExitReason? lastExit = LastExitReasonStore.Read();

        // Build the base record first, then derive the two classifiers against
        // the fully-populated snapshot (they need the ScheduledTask field).
        var baseInstallation = new WatcherInstallation(
            StagedBinaryPath: stagedPath,
            StagedBinaryExists: stagedExists,
            AutostartEntryPresent: autostartPresent,
            AutostartEntryTarget: autostartTarget,
            AutostartEntryPointsAtStaged: autostartPointsAtStaged,
            WatcherRunning: watcherRunning,
            WatcherPid: watcherPid)
        {
            ScheduledTask = taskEntry,
            LastExitReason = lastExit,
        };

        return baseInstallation with
        {
            SupervisorState = SupervisorDecision.ClassifySupervisor(baseInstallation),
            AutostartEffectiveness = SupervisorDecision.ClassifyAutostart(baseInstallation, runKeyApproved),
        };
    }

    /// <summary>
    /// Signals the watcher's stop event and waits up to <paramref name="waitForShutdown"/>
    /// for the instance mutex to be released. Returns true on cooperative shutdown,
    /// false on timeout (or if the event did not exist to begin with).
    /// </summary>
    public static bool SignalStopEvent(TimeSpan waitForShutdown)
    {
        if (!EventWaitHandle.TryOpenExisting(WatcherInstallation.StopEventName, out var evt))
        {
            return false;
        }

        using (evt)
        {
            evt.Set();
        }

        var deadline = DateTimeOffset.UtcNow + waitForShutdown;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsWatcherRunning())
            {
                return true;
            }
            Thread.Sleep(100);
        }

        return !IsWatcherRunning();
    }

    /// <summary>
    /// Best-effort forced termination. Verifies the target PID's module path
    /// matches the expected staged binary before killing — this protects
    /// against acting on a stale PID file that now belongs to a different
    /// process.
    /// </summary>
    public static bool ForceKillWatcher(int pid, string expectedModulePath)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            string? modulePath;
            try
            {
                modulePath = proc.MainModule?.FileName;
            }
            catch
            {
                modulePath = null;
            }

            if (modulePath is null)
            {
                return false;
            }

            if (!string.Equals(
                    Path.GetFullPath(modulePath),
                    Path.GetFullPath(expectedModulePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            proc.Kill(entireProcessTree: false);
            proc.WaitForExit(3000);
            return proc.HasExited;
        }
        catch (ArgumentException)
        {
            // Process with that PID no longer exists — already gone.
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (bool Present, string? Value) ReadRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WatcherInstallation.RunKeySubKey, writable: false);
            if (key is null)
            {
                return (false, null);
            }

            var value = key.GetValue(WatcherInstallation.RunKeyValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return (false, null);
            }

            return (true, value);
        }
        catch
        {
            return (false, null);
        }
    }

    private static bool RunKeyValueMatches(string runKeyValue, string stagedPath)
    {
        // Expected form: "<stagedPath>" --watch
        var expected = $"\"{stagedPath}\" --watch";
        return string.Equals(runKeyValue, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWatcherRunning()
    {
        if (!Mutex.TryOpenExisting(WatcherInstallation.InstanceMutexName, out var mutex))
        {
            return false;
        }
        using (mutex)
        {
            return true;
        }
    }

    private static int? TryReadPidFile()
    {
        try
        {
            var path = WatcherInstallation.PidFilePath;
            if (!File.Exists(path))
            {
                return null;
            }
            var text = File.ReadAllText(path).Trim();
            if (int.TryParse(text, out var pid) && pid > 0)
            {
                return pid;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}

using System.Runtime.Versioning;
using System.Text;
using KbFix.Platform.Install;
using KbFix.Watcher;

namespace KbFix.Cli;

/// <summary>
/// Formats the stdout report for <c>--install</c>, <c>--uninstall</c>, and
/// <c>--status</c> per <c>specs/003-background-watcher/contracts/cli.md</c>.
/// Pure formatter; no I/O.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InstallReporter
{
    public static string FormatInstall(
        WatcherInstallation before,
        IReadOnlyList<InstallExecutor.StepResult> results,
        WatcherInstallation after,
        bool quiet)
    {
        var sb = new StringBuilder();
        sb.AppendLine("KbFix installer");

        if (!quiet)
        {
            var stagedLine = GetStagedLine(results, after);
            var autostartLine = GetAutostartLine(results, after);
            var taskLine = GetTaskLine(results, after);
            var watcherLine = GetWatcherLine(results, before, after);

            sb.AppendLine($"  staged:    {stagedLine}");
            sb.AppendLine($"  autostart: {autostartLine}");
            sb.AppendLine($"  task:      {taskLine}");
            sb.AppendLine($"  watcher:   {watcherLine}");
            sb.AppendLine();
        }

        var noChange = results.Count == 0;
        var taskFailed = results.Any(r => r.Step is CreateScheduledTaskStep && !r.Succeeded);
        if (noChange)
        {
            sb.AppendLine("Already installed.");
        }
        else if (taskFailed)
        {
            sb.AppendLine("Installed (degraded — no scheduled task). The Run-key autostart will fire at next logon, but the watcher cannot auto-restart after a crash.");
        }
        else
        {
            sb.AppendLine("Installed. The watcher will also start automatically at your next Windows login.");
        }

        return sb.ToString();
    }

    public static string FormatUninstall(
        WatcherInstallation before,
        IReadOnlyList<InstallExecutor.StepResult> results,
        bool quiet)
    {
        var sb = new StringBuilder();
        sb.AppendLine("KbFix uninstaller");

        if (!quiet)
        {
            sb.AppendLine($"  watcher:   {FormatUninstallWatcherLine(before, results)}");
            sb.AppendLine($"  autostart: {FormatUninstallAutostartLine(before, results)}");
            sb.AppendLine($"  task:      {FormatUninstallTaskLine(before, results)}");
            sb.AppendLine($"  staged:    {FormatUninstallStagedLine(before, results)}");
            sb.AppendLine();
        }

        var nothingToDo = results.Count == 0;
        if (nothingToDo)
        {
            sb.AppendLine("Nothing to uninstall.");
        }
        else
        {
            sb.AppendLine("Uninstalled.");
        }

        return sb.ToString();
    }

    public static string FormatStatus(WatcherInstallation state, bool quiet)
    {
        var sb = new StringBuilder();
        sb.AppendLine("KbFix status");

        if (!quiet)
        {
            sb.AppendLine($"  watcher:   {(state.WatcherRunning ? $"running (pid {state.WatcherPid?.ToString() ?? "?"})" : "not running")}");
            sb.AppendLine($"  autostart: {FormatAutostartForStatus(state)}");
            sb.AppendLine($"  staged:    {(state.StagedBinaryExists ? $"present      ({state.StagedBinaryPath})" : "not present")}");
            if (state.StagedBinaryExists || state.WatcherRunning || Directory.Exists(WatcherInstallation.StagingDirectory))
            {
                sb.AppendLine($"  log:       {WatcherInstallation.LogFilePath}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"State: {state.Classify()}");
        return sb.ToString();
    }

    // -------- install lines --------

    private static string GetStagedLine(IReadOnlyList<InstallExecutor.StepResult> results, WatcherInstallation after)
    {
        var copied = results.Any(r => r.Step is CopyBinaryToStagedStep);
        if (copied)
        {
            return after.StagedBinaryPath;
        }
        return $"{after.StagedBinaryPath}  (unchanged)";
    }

    private static string GetAutostartLine(IReadOnlyList<InstallExecutor.StepResult> results, WatcherInstallation after)
    {
        var wrote = results.Any(r => r.Step is WriteRunKeyStep);
        var target = AutostartRegistry.FormatRunKeyValue(after.StagedBinaryPath);
        if (wrote)
        {
            return $"HKCU\\Run\\{WatcherInstallation.RunKeyValueName} = {target}";
        }
        return $"HKCU\\Run\\{WatcherInstallation.RunKeyValueName}  (unchanged)";
    }

    private static string GetWatcherLine(
        IReadOnlyList<InstallExecutor.StepResult> results,
        WatcherInstallation before,
        WatcherInstallation after)
    {
        var spawned = results.Any(r => r.Step is SpawnWatcherStep);
        var stopped = results.Any(r => r.Step is SignalStopEventStep);
        var pidDisplay = after.WatcherPid?.ToString() ?? "?";

        if (stopped && spawned)
        {
            return $"restarted (pid {pidDisplay})";
        }
        if (spawned)
        {
            return $"started (pid {pidDisplay})";
        }
        if (before.WatcherRunning)
        {
            return $"already running (pid {pidDisplay})";
        }
        return "not started";
    }

    // -------- uninstall lines --------

    private static string FormatUninstallWatcherLine(
        WatcherInstallation before,
        IReadOnlyList<InstallExecutor.StepResult> results)
    {
        if (!before.WatcherRunning)
        {
            return "not running";
        }

        var signalResult = results.FirstOrDefault(r => r.Step is SignalStopEventStep);
        var killResult = results.FirstOrDefault(r => r.Step is ForceKillWatcherStep);
        var pidText = before.WatcherPid?.ToString() ?? "?";

        if (killResult is not null && killResult.Note == "forced")
        {
            return $"stopped (pid was {pidText}, forced after timeout)";
        }

        if (signalResult is not null && (signalResult.Note?.Contains("cooperative") == true))
        {
            return $"stopped (pid was {pidText}, cooperative shutdown)";
        }

        return $"stopped (pid was {pidText})";
    }

    private static string FormatUninstallAutostartLine(
        WatcherInstallation before,
        IReadOnlyList<InstallExecutor.StepResult> results)
    {
        var removed = results.Any(r => r.Step is DeleteRunKeyStep);
        if (!removed)
        {
            return "not registered";
        }

        if (before.AutostartEntryPresent && !before.AutostartEntryPointsAtStaged)
        {
            return $"HKCU\\Run\\{WatcherInstallation.RunKeyValueName}  (was stale: pointed at {before.AutostartEntryTarget}; removed)";
        }

        return $"HKCU\\Run\\{WatcherInstallation.RunKeyValueName}  (removed)";
    }

    private static string FormatUninstallStagedLine(
        WatcherInstallation before,
        IReadOnlyList<InstallExecutor.StepResult> results)
    {
        if (!before.StagedBinaryExists)
        {
            return "not present";
        }

        var deleteResult = results.FirstOrDefault(r => r.Step is DeleteStagedBinaryStep);
        if (deleteResult is null)
        {
            return before.StagedBinaryPath;
        }

        return deleteResult.Note switch
        {
            InstallExecutor.NoteDeleted => $"{before.StagedBinaryPath}  (deleted)",
            InstallExecutor.NoteMovedForRebootDelete => $"{before.StagedBinaryPath}  (moved to %TEMP% — Windows will remove it at the next reboot)",
            InstallExecutor.NoteNotPresent => "not present",
            InstallExecutor.NoteDeleteFailed => $"{before.StagedBinaryPath}  (delete FAILED — file still present; try running --uninstall again from a different location)",
            _ => before.StagedBinaryPath,
        };
    }

    // -------- 004 scheduled-task lines --------

    private static string GetTaskLine(IReadOnlyList<InstallExecutor.StepResult> results, WatcherInstallation after)
    {
        var taskResult = results.FirstOrDefault(r => r.Step is CreateScheduledTaskStep);
        if (taskResult is null)
        {
            if (after.ScheduledTask is { Present: true })
            {
                return $"\\{WatcherInstallation.ScheduledTaskName}  (unchanged)";
            }
            return "not installed";
        }
        if (!taskResult.Succeeded)
        {
            return $"NOT installed — schtasks denied creation ({Trim(taskResult.Note)})";
        }
        return $"\\{WatcherInstallation.ScheduledTaskName}  (At logon + Restart on failure)";
    }

    private static string FormatUninstallTaskLine(
        WatcherInstallation before,
        IReadOnlyList<InstallExecutor.StepResult> results)
    {
        var r = results.FirstOrDefault(s => s.Step is DeleteScheduledTaskStep);
        if (r is null)
        {
            return before.ScheduledTask is { Present: true }
                ? $"\\{WatcherInstallation.ScheduledTaskName}  (NOT removed)"
                : "not present";
        }
        if (!r.Succeeded)
        {
            return $"\\{WatcherInstallation.ScheduledTaskName}  (delete FAILED: {Trim(r.Note)})";
        }
        return r.Note == "deleted"
            ? $"\\{WatcherInstallation.ScheduledTaskName}  (removed)"
            : "not present";
    }

    private static string Trim(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var first = s.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return first.Length > 120 ? first.Substring(0, 120) : first;
    }

    // -------- status lines --------

    private static string FormatAutostartForStatus(WatcherInstallation state)
    {
        if (!state.AutostartEntryPresent)
        {
            return "not registered";
        }
        var target = state.AutostartEntryTarget ?? "(unknown)";
        if (!state.AutostartEntryPointsAtStaged)
        {
            return $"registered  ({target})  STALE";
        }
        return $"registered  ({target})";
    }
}

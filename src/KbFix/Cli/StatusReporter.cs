using System.Runtime.Versioning;
using System.Text;
using KbFix.Watcher;

namespace KbFix.Cli;

/// <summary>
/// Formats the stdout report for <c>--status</c> per
/// <c>specs/003-background-watcher/contracts/cli.md</c> and
/// <c>specs/004-watcher-resilience/contracts/cli.md</c>. Pure formatter;
/// no I/O.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StatusReporter
{
    private const int VerboseLogTailLines = 40;

    public static string Format(WatcherInstallation state, bool quiet)
    {
        var sb = new StringBuilder();
        sb.AppendLine("KbFix status");

        if (!quiet)
        {
            sb.AppendLine($"  watcher:    {FormatWatcher(state)}");
            sb.AppendLine($"  autostart:  {FormatAutostart(state)}");
            sb.AppendLine($"  staged:     {FormatStaged(state)}");
            sb.AppendLine($"  task:       {FormatTask(state)}");
            sb.AppendLine($"  supervisor: {FormatSupervisor(state)}");
            sb.AppendLine($"  last exit:  {FormatLastExit(state)}");
            sb.AppendLine($"  effective:  {FormatEffective(state)}");
            if (state.Classify() != InstalledState.NotInstalled)
            {
                sb.AppendLine($"  log:        {WatcherInstallation.LogFilePath}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"State: {state.Classify()}");
        return sb.ToString();
    }

    /// <summary>
    /// Formats <c>--status --verbose</c>. Extends the default output with
    /// three delimited blocks for pasting into a bug report: the last
    /// <see cref="VerboseLogTailLines"/> lines of <c>watcher.log</c>, the
    /// stored scheduled-task XML, and the pretty-printed last-exit.json.
    /// </summary>
    public static string FormatVerbose(
        WatcherInstallation state,
        string? logTail,
        string? taskXml,
        LastExitReason? lastExit)
    {
        var sb = new StringBuilder();
        sb.Append(Format(state, quiet: false));
        sb.AppendLine();

        sb.AppendLine("----- watcher.log (tail) -----");
        sb.AppendLine(string.IsNullOrEmpty(logTail) ? "(empty or absent)" : logTail.TrimEnd('\r', '\n'));
        sb.AppendLine("----- end -----");
        sb.AppendLine();

        sb.AppendLine("----- scheduled-task.xml -----");
        sb.AppendLine(string.IsNullOrEmpty(taskXml) ? "(absent)" : taskXml.TrimEnd('\r', '\n'));
        sb.AppendLine("----- end -----");
        sb.AppendLine();

        sb.AppendLine("----- last-exit.json -----");
        sb.AppendLine(lastExit is null ? "(absent)" : LastExitReasonStore.ToPrettyJson(lastExit));
        sb.AppendLine("----- end -----");
        return sb.ToString();
    }

    /// <summary>Reads the last N lines of <c>watcher.log</c> for the verbose snapshot. Tolerates missing file.</summary>
    public static string? ReadLogTail(string? path = null)
    {
        var effective = path ?? WatcherInstallation.LogFilePath;
        try
        {
            if (!File.Exists(effective))
            {
                return null;
            }
            var all = File.ReadAllLines(effective);
            var start = Math.Max(0, all.Length - VerboseLogTailLines);
            return string.Join(Environment.NewLine, all, start, all.Length - start);
        }
        catch
        {
            return null;
        }
    }

    // ---------- individual lines ----------

    private static string FormatWatcher(WatcherInstallation state)
    {
        if (!state.WatcherRunning) return "not running";
        var pid = state.WatcherPid?.ToString() ?? "?";
        return $"running (pid {pid})";
    }

    private static string FormatAutostart(WatcherInstallation state)
    {
        if (!state.AutostartEntryPresent) return "not registered";
        var target = state.AutostartEntryTarget ?? "(unknown)";
        if (!state.AutostartEntryPointsAtStaged)
        {
            return $"registered  ({target})  STALE";
        }
        return $"registered  ({target})";
    }

    private static string FormatStaged(WatcherInstallation state)
    {
        if (!state.StagedBinaryExists) return "not present";
        return $"present      ({state.StagedBinaryPath})";
    }

    private static string FormatTask(WatcherInstallation state)
    {
        var t = state.ScheduledTask;
        if (t is null || !t.Present) return "not installed";
        if (!t.Enabled) return $"\\{WatcherInstallation.ScheduledTaskName}  (Disabled)";
        var next = t.NextRunTime is null ? "" : $", Next Run: {t.NextRunTime:yyyy-MM-dd HH:mm}";
        return $"\\{WatcherInstallation.ScheduledTaskName}  ({t.Status}{next})";
    }

    private static string FormatSupervisor(WatcherInstallation state) => state.SupervisorState switch
    {
        SupervisorState.Healthy => "healthy",
        SupervisorState.RestartPending => "restart pending",
        SupervisorState.GaveUp => "gave up (re-run --install to re-arm)",
        SupervisorState.Disabled => "disabled",
        SupervisorState.Absent => "absent (no scheduled task)",
        _ => "unknown",
    };

    private static string FormatLastExit(WatcherInstallation state)
    {
        var lx = state.LastExitReason;
        if (lx is null) return "(none)";
        var details = string.IsNullOrWhiteSpace(lx.Detail) ? "" : $"  detail={lx.Detail}";
        return $"{lx.Reason}  at  {lx.TimestampUtc}  (pid {lx.Pid}){details}";
    }

    private static string FormatEffective(WatcherInstallation state) => state.AutostartEffectiveness switch
    {
        AutostartEffectiveness.Effective => "yes",
        AutostartEffectiveness.Degraded => "no (both mechanisms disabled)",
        AutostartEffectiveness.NotRegistered => "not registered",
        _ => "unknown",
    };

    /// <summary>
    /// Maps an <see cref="InstalledState"/> to the exit code the CLI contract
    /// defines for <c>--status</c>. Covers both 003 (10–14) and 004 (15–17)
    /// assignments.
    /// </summary>
    public static int ExitCodeFor(InstalledState state) => state switch
    {
        InstalledState.InstalledHealthy => Diagnostics.ExitCodes.Success,
        InstalledState.NotInstalled => Diagnostics.ExitCodes.NotInstalled,
        InstalledState.InstalledNotRunning => Diagnostics.ExitCodes.InstalledNotRunning,
        InstalledState.RunningWithoutAutostart => Diagnostics.ExitCodes.RunningWithoutAutostart,
        InstalledState.StalePath => Diagnostics.ExitCodes.StalePath,
        InstalledState.MixedOrCorrupt => Diagnostics.ExitCodes.MixedOrCorrupt,
        InstalledState.SupervisorBackingOff => Diagnostics.ExitCodes.SupervisorBackingOff,
        InstalledState.SupervisorGaveUp => Diagnostics.ExitCodes.SupervisorGaveUp,
        InstalledState.AutostartDegraded => Diagnostics.ExitCodes.AutostartDegraded,
        _ => Diagnostics.ExitCodes.Failure,
    };
}

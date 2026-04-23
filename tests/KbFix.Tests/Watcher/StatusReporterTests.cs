using KbFix.Cli;
using KbFix.Diagnostics;
using KbFix.Watcher;
using Xunit;

namespace KbFix.Tests.Watcher;

public class StatusReporterTests
{
    private const string StagedPath = @"C:\Users\alice\AppData\Local\KbFix\kbfix.exe";

    private static WatcherInstallation NotInstalled() => new(
        StagedBinaryPath: StagedPath,
        StagedBinaryExists: false,
        AutostartEntryPresent: false,
        AutostartEntryTarget: null,
        AutostartEntryPointsAtStaged: false,
        WatcherRunning: false,
        WatcherPid: null);

    private static WatcherInstallation InstalledHealthy(int pid = 12345) => new(
        StagedBinaryPath: StagedPath,
        StagedBinaryExists: true,
        AutostartEntryPresent: true,
        AutostartEntryTarget: $"\"{StagedPath}\" --watch",
        AutostartEntryPointsAtStaged: true,
        WatcherRunning: true,
        WatcherPid: pid);

    private static WatcherInstallation InstalledNotRunning() => new(
        StagedBinaryPath: StagedPath,
        StagedBinaryExists: true,
        AutostartEntryPresent: true,
        AutostartEntryTarget: $"\"{StagedPath}\" --watch",
        AutostartEntryPointsAtStaged: true,
        WatcherRunning: false,
        WatcherPid: null);

    private static WatcherInstallation RunningWithoutAutostart() => new(
        StagedBinaryPath: StagedPath,
        StagedBinaryExists: true,
        AutostartEntryPresent: false,
        AutostartEntryTarget: null,
        AutostartEntryPointsAtStaged: false,
        WatcherRunning: true,
        WatcherPid: 9999);

    private static WatcherInstallation StalePath() => new(
        StagedBinaryPath: StagedPath,
        StagedBinaryExists: false,
        AutostartEntryPresent: true,
        AutostartEntryTarget: @"""C:\old\path\kbfix.exe"" --watch",
        AutostartEntryPointsAtStaged: false,
        WatcherRunning: false,
        WatcherPid: null);

    [Fact]
    public void NotInstalled_reports_expected_block()
    {
        var s = StatusReporter.Format(NotInstalled(), quiet: false);

        Assert.Contains("KbFix status", s);
        Assert.Contains("watcher:    not running", s);
        Assert.Contains("autostart:  not registered", s);
        Assert.Contains("staged:     not present", s);
        Assert.Contains("task:       not installed", s);
        Assert.Contains("supervisor: absent", s);
        Assert.Contains("last exit:  (none)", s);
        Assert.Contains("effective:  not registered", s);
        Assert.DoesNotContain("log:", s);
        Assert.Contains("State: NotInstalled", s);
    }

    [Fact]
    public void InstalledHealthy_reports_pid_and_log_path()
    {
        var s = StatusReporter.Format(InstalledHealthy(pid: 12345), quiet: false);

        Assert.Contains("watcher:    running (pid 12345)", s);
        Assert.Contains("autostart:  registered", s);
        Assert.Contains($"\"{StagedPath}\" --watch", s);
        Assert.DoesNotContain("STALE", s);
        Assert.Contains("staged:     present", s);
        Assert.Contains(StagedPath, s);
        Assert.Contains("log:", s);
        Assert.Contains(WatcherInstallation.LogFilePath, s);
        Assert.Contains("State: InstalledHealthy", s);
    }

    [Fact]
    public void InstalledNotRunning_marks_watcher_not_running_but_autostart_registered()
    {
        var s = StatusReporter.Format(InstalledNotRunning(), quiet: false);

        Assert.Contains("watcher:    not running", s);
        Assert.Contains("autostart:  registered", s);
        Assert.Contains("State: InstalledNotRunning", s);
    }

    [Fact]
    public void RunningWithoutAutostart_reports_expected_state()
    {
        var s = StatusReporter.Format(RunningWithoutAutostart(), quiet: false);

        Assert.Contains("watcher:    running (pid 9999)", s);
        Assert.Contains("autostart:  not registered", s);
        Assert.Contains("State: RunningWithoutAutostart", s);
    }

    [Fact]
    public void StalePath_marks_autostart_as_STALE()
    {
        var s = StatusReporter.Format(StalePath(), quiet: false);

        Assert.Contains("autostart:  registered", s);
        Assert.Contains("STALE", s);
        Assert.Contains("State: StalePath", s);
    }

    [Fact]
    public void Quiet_suppresses_indented_lines_but_keeps_state_line()
    {
        var s = StatusReporter.Format(InstalledHealthy(), quiet: true);

        Assert.DoesNotContain("  watcher:", s);
        Assert.DoesNotContain("  autostart:", s);
        Assert.DoesNotContain("  staged:", s);
        Assert.DoesNotContain("  task:", s);
        Assert.DoesNotContain("  supervisor:", s);
        Assert.DoesNotContain("  log:", s);
        Assert.Contains("State: InstalledHealthy", s);
    }

    [Fact]
    public void ExitCodeFor_maps_each_state_to_contract_code()
    {
        Assert.Equal(ExitCodes.Success, StatusReporter.ExitCodeFor(InstalledState.InstalledHealthy));
        Assert.Equal(ExitCodes.NotInstalled, StatusReporter.ExitCodeFor(InstalledState.NotInstalled));
        Assert.Equal(ExitCodes.InstalledNotRunning, StatusReporter.ExitCodeFor(InstalledState.InstalledNotRunning));
        Assert.Equal(ExitCodes.RunningWithoutAutostart, StatusReporter.ExitCodeFor(InstalledState.RunningWithoutAutostart));
        Assert.Equal(ExitCodes.StalePath, StatusReporter.ExitCodeFor(InstalledState.StalePath));
        Assert.Equal(ExitCodes.MixedOrCorrupt, StatusReporter.ExitCodeFor(InstalledState.MixedOrCorrupt));

        // --- 004 additions ---
        Assert.Equal(ExitCodes.SupervisorBackingOff, StatusReporter.ExitCodeFor(InstalledState.SupervisorBackingOff));
        Assert.Equal(ExitCodes.SupervisorGaveUp, StatusReporter.ExitCodeFor(InstalledState.SupervisorGaveUp));
        Assert.Equal(ExitCodes.AutostartDegraded, StatusReporter.ExitCodeFor(InstalledState.AutostartDegraded));
    }

    // ---------- 004: supervisor state + last exit + effective lines ----------

    [Fact]
    public void Task_and_supervisor_report_healthy_when_004_fields_populated()
    {
        var state = InstalledHealthy() with
        {
            ScheduledTask = new ScheduledTaskEntry(
                Present: true,
                Enabled: true,
                Status: ScheduledTaskStatus.Running,
                ExecutablePath: StagedPath,
                PointsAtStaged: true),
            SupervisorState = SupervisorState.Healthy,
            AutostartEffectiveness = AutostartEffectiveness.Effective,
        };

        var s = StatusReporter.Format(state, quiet: false);

        Assert.Contains("task:       \\KbFix\\KbFixWatcher  (Running", s);
        Assert.Contains("supervisor: healthy", s);
        Assert.Contains("effective:  yes", s);
    }

    [Fact]
    public void Supervisor_gave_up_line_points_user_at_reinstall()
    {
        var state = InstalledNotRunning() with
        {
            SupervisorState = SupervisorState.GaveUp,
        };

        var s = StatusReporter.Format(state, quiet: false);

        Assert.Contains("supervisor: gave up", s);
        Assert.Contains("--install", s);
    }

    [Fact]
    public void Last_exit_line_renders_reason_timestamp_and_pid()
    {
        var state = InstalledNotRunning() with
        {
            LastExitReason = new LastExitReason(
                "CrashedUnhandled", 1, "2026-04-23T09:14:05Z", 11234, "NullReferenceException: x was null"),
        };

        var s = StatusReporter.Format(state, quiet: false);

        Assert.Contains("last exit:  CrashedUnhandled  at  2026-04-23T09:14:05Z  (pid 11234)", s);
        Assert.Contains("NullReferenceException", s);
    }

    [Fact]
    public void Effective_degraded_reports_both_disabled()
    {
        var state = InstalledHealthy() with { AutostartEffectiveness = AutostartEffectiveness.Degraded };

        var s = StatusReporter.Format(state, quiet: false);

        Assert.Contains("effective:  no (both mechanisms disabled)", s);
    }

    // ---------- 004: --verbose snapshot ----------

    [Fact]
    public void Verbose_appends_three_delimited_sections()
    {
        var state = InstalledHealthy();

        var verbose = StatusReporter.FormatVerbose(
            state,
            logTail: "2026-04-23T12:00:00Z INFO start\n2026-04-23T12:00:01Z INFO reconcile-applied count=1",
            taskXml: "<Task version=\"1.2\">...</Task>",
            lastExit: new LastExitReason("CooperativeShutdown", 0, "2026-04-23T09:14:05Z", 11234, null));

        Assert.Contains("----- watcher.log (tail) -----", verbose);
        Assert.Contains("reconcile-applied count=1", verbose);
        Assert.Contains("----- scheduled-task.xml -----", verbose);
        Assert.Contains("<Task version=\"1.2\">", verbose);
        Assert.Contains("----- last-exit.json -----", verbose);
        Assert.Contains("CooperativeShutdown", verbose);
        Assert.Contains("----- end -----", verbose);
    }

    [Fact]
    public void Verbose_tolerates_missing_log_and_xml_and_last_exit()
    {
        var state = NotInstalled();
        var verbose = StatusReporter.FormatVerbose(state, logTail: null, taskXml: null, lastExit: null);

        Assert.Contains("(empty or absent)", verbose);
        Assert.Contains("(absent)", verbose);
    }
}

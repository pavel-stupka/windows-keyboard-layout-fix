using KbFix.Watcher;
using Xunit;

namespace KbFix.Tests.Watcher;

public class InstallDecisionTests
{
    private const string StagedPath = @"C:\Users\alice\AppData\Local\KbFix\kbfix.exe";
    private const string DownloadsPath = @"C:\Users\alice\Downloads\kbfix.exe";

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

    // ------------------------------ Classify() ------------------------------

    [Fact]
    public void Classify_reports_NotInstalled_for_empty_state()
    {
        Assert.Equal(InstalledState.NotInstalled, NotInstalled().Classify());
    }

    [Fact]
    public void Classify_reports_InstalledHealthy_when_everything_in_place()
    {
        Assert.Equal(InstalledState.InstalledHealthy, InstalledHealthy().Classify());
    }

    [Fact]
    public void Classify_reports_InstalledNotRunning_when_autostart_is_registered_but_watcher_is_dead()
    {
        Assert.Equal(InstalledState.InstalledNotRunning, InstalledNotRunning().Classify());
    }

    [Fact]
    public void Classify_reports_RunningWithoutAutostart_when_watcher_is_alive_but_run_key_is_gone()
    {
        Assert.Equal(InstalledState.RunningWithoutAutostart, RunningWithoutAutostart().Classify());
    }

    [Fact]
    public void Classify_reports_StalePath_when_run_key_points_at_missing_binary()
    {
        Assert.Equal(InstalledState.StalePath, StalePath().Classify());
    }

    // --------------------------- Install decisions -------------------------

    [Fact]
    public void Install_from_scratch_stages_copies_writes_key_and_spawns()
    {
        var steps = InstallDecision.ComputeInstallSteps(NotInstalled(), DownloadsPath);

        Assert.Collection(
            steps,
            s => Assert.IsType<EnsureStagingDirectoryStep>(s),
            s => Assert.Equal(DownloadsPath, Assert.IsType<CopyBinaryToStagedStep>(s).SourcePath),
            s => Assert.Equal(StagedPath, Assert.IsType<WriteRunKeyStep>(s).StagedPath),
            s => Assert.Equal(StagedPath, Assert.IsType<SpawnWatcherStep>(s).StagedPath),
            // 004: Scheduled-Task registration is always appended.
            s => Assert.IsType<ExportScheduledTaskXmlStep>(s),
            s => Assert.IsType<CreateScheduledTaskStep>(s));
    }

    [Fact]
    public void Install_from_already_healthy_003_state_appends_task_install_steps_for_upgrade()
    {
        // 003-era InstalledHealthy has no task. Re-running --install under 004
        // must therefore append the Export+Create pair — this is the upgrade path.
        var steps = InstallDecision.ComputeInstallSteps(InstalledHealthy(), StagedPath);

        Assert.Collection(
            steps,
            s => Assert.IsType<ExportScheduledTaskXmlStep>(s),
            s => Assert.IsType<CreateScheduledTaskStep>(s));
    }

    [Fact]
    public void Install_from_healthy_but_different_invoking_path_replaces_binary_and_respawns()
    {
        var steps = InstallDecision.ComputeInstallSteps(InstalledHealthy(pid: 4242), DownloadsPath);

        Assert.Collection(
            steps,
            s => Assert.Equal(3000, Assert.IsType<SignalStopEventStep>(s).TimeoutMs),
            s => Assert.Equal(4242, Assert.IsType<ForceKillWatcherStep>(s).Pid),
            s => Assert.IsType<EnsureStagingDirectoryStep>(s),
            s => Assert.Equal(DownloadsPath, Assert.IsType<CopyBinaryToStagedStep>(s).SourcePath),
            s => Assert.Equal(StagedPath, Assert.IsType<SpawnWatcherStep>(s).StagedPath),
            // 004: appended on every install path.
            s => Assert.IsType<ExportScheduledTaskXmlStep>(s),
            s => Assert.IsType<CreateScheduledTaskStep>(s));
    }

    [Fact]
    public void Install_from_stale_path_rewrites_run_key_and_re_stages()
    {
        var steps = InstallDecision.ComputeInstallSteps(StalePath(), DownloadsPath);

        // Stale path => !AutostartEntryPointsAtStaged, so WriteRunKey is included.
        Assert.Contains(steps, s => s is EnsureStagingDirectoryStep);
        Assert.Contains(steps, s => s is CopyBinaryToStagedStep c && c.SourcePath == DownloadsPath);
        Assert.Contains(steps, s => s is WriteRunKeyStep w && w.StagedPath == StagedPath);
        Assert.Contains(steps, s => s is SpawnWatcherStep sp && sp.StagedPath == StagedPath);
    }

    [Fact]
    public void Install_when_watcher_running_without_autostart_adds_run_key()
    {
        var steps = InstallDecision.ComputeInstallSteps(RunningWithoutAutostart(), StagedPath);

        // invoking == staged, so no stop/kill/copy. Just the missing Run key.
        Assert.Contains(steps, s => s is WriteRunKeyStep w && w.StagedPath == StagedPath);
        Assert.DoesNotContain(steps, s => s is SignalStopEventStep);
        Assert.DoesNotContain(steps, s => s is CopyBinaryToStagedStep);
    }

    [Fact]
    public void Install_when_installed_not_running_respawns_without_restaging()
    {
        var steps = InstallDecision.ComputeInstallSteps(InstalledNotRunning(), StagedPath);

        // invoking == staged, autostart is already correct → respawn + task.
        Assert.Collection(
            steps,
            s => Assert.Equal(StagedPath, Assert.IsType<SpawnWatcherStep>(s).StagedPath),
            s => Assert.IsType<ExportScheduledTaskXmlStep>(s),
            s => Assert.IsType<CreateScheduledTaskStep>(s));
    }

    // -------------------------- Uninstall decisions -------------------------

    [Fact]
    public void Uninstall_when_nothing_installed_emits_no_steps()
    {
        var steps = InstallDecision.ComputeUninstallSteps(NotInstalled(), DownloadsPath);
        Assert.Empty(steps);
    }

    [Fact]
    public void Uninstall_from_healthy_stops_watcher_and_removes_everything()
    {
        // 003-era InstalledHealthy has no task, so the Scheduled-Task delete
        // step is skipped (SupervisorDecision.AppendUninstallSteps only
        // emits when state.ScheduledTask.Present == true).
        var steps = InstallDecision.ComputeUninstallSteps(InstalledHealthy(pid: 777), DownloadsPath);

        Assert.Collection(
            steps,
            s => Assert.Equal(3000, Assert.IsType<SignalStopEventStep>(s).TimeoutMs),
            s => Assert.Equal(777, Assert.IsType<ForceKillWatcherStep>(s).Pid),
            s => Assert.IsType<DeleteRunKeyStep>(s),
            s => Assert.IsType<DeleteStagedBinaryStep>(s),
            s => Assert.IsType<DeleteStagingDirectoryStep>(s));
    }

    [Fact]
    public void Uninstall_from_healthy_004_state_also_deletes_the_scheduled_task()
    {
        var state = InstalledHealthy(pid: 777) with
        {
            ScheduledTask = new ScheduledTaskEntry(Present: true, Enabled: true, Status: ScheduledTaskStatus.Ready),
        };
        var steps = InstallDecision.ComputeUninstallSteps(state, DownloadsPath);

        Assert.Collection(
            steps,
            s => Assert.IsType<SignalStopEventStep>(s),
            s => Assert.IsType<ForceKillWatcherStep>(s),
            s => Assert.IsType<DeleteRunKeyStep>(s),
            s => Assert.IsType<DeleteScheduledTaskStep>(s),
            s => Assert.IsType<DeleteStagedBinaryStep>(s),
            s => Assert.IsType<DeleteStagingDirectoryStep>(s));
    }

    [Fact]
    public void Uninstall_from_staged_binary_still_emits_full_cleanup_sequence()
    {
        // User is running `--uninstall` from the staged copy itself. The
        // executor handles the "cannot delete running exe" constraint
        // internally (rename-to-temp + reboot-delete), so the decision
        // function emits the same steps as a clean uninstall from Downloads.
        var steps = InstallDecision.ComputeUninstallSteps(InstalledHealthy(pid: 123), StagedPath);

        Assert.Contains(steps, s => s is DeleteRunKeyStep);
        Assert.Contains(steps, s => s is DeleteStagedBinaryStep);
        Assert.Contains(steps, s => s is DeleteStagingDirectoryStep);
    }

    [Fact]
    public void Uninstall_from_stale_path_removes_run_key_even_without_running_watcher()
    {
        var steps = InstallDecision.ComputeUninstallSteps(StalePath(), DownloadsPath);

        Assert.DoesNotContain(steps, s => s is SignalStopEventStep);
        Assert.DoesNotContain(steps, s => s is ForceKillWatcherStep);
        Assert.Contains(steps, s => s is DeleteRunKeyStep);
        // StagedBinaryExists == false, so no delete-binary step.
        Assert.DoesNotContain(steps, s => s is DeleteStagedBinaryStep);
    }

    [Fact]
    public void Uninstall_from_running_without_autostart_stops_watcher_but_skips_run_key()
    {
        var steps = InstallDecision.ComputeUninstallSteps(RunningWithoutAutostart(), DownloadsPath);

        Assert.Contains(steps, s => s is SignalStopEventStep);
        Assert.Contains(steps, s => s is ForceKillWatcherStep);
        Assert.DoesNotContain(steps, s => s is DeleteRunKeyStep);
    }

    // ---------------------------- Status decisions --------------------------

    [Fact]
    public void Status_emits_a_single_report_step_regardless_of_state()
    {
        foreach (var state in new[] { NotInstalled(), InstalledHealthy(), InstalledNotRunning(), RunningWithoutAutostart(), StalePath() })
        {
            var steps = InstallDecision.ComputeStatusSteps(state);
            Assert.Collection(steps, s => Assert.IsType<ReportStatusStep>(s));
        }
    }
}

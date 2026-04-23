using KbFix.Watcher;
using Xunit;

namespace KbFix.Tests.Watcher;

public class SupervisorDecisionTests
{
    private const string StagedPath = @"C:\Users\alice\AppData\Local\KbFix\kbfix.exe";
    private const string InvokingDownloads = @"C:\Users\alice\Downloads\kbfix.exe";

    // ---------- Fixture helpers ----------

    private static WatcherInstallation FreshMachine() => new(
        StagedBinaryPath: StagedPath,
        StagedBinaryExists: false,
        AutostartEntryPresent: false,
        AutostartEntryTarget: null,
        AutostartEntryPointsAtStaged: false,
        WatcherRunning: false,
        WatcherPid: null)
    {
        ScheduledTask = ScheduledTaskEntry.Absent,
    };

    private static WatcherInstallation ThreeInstalledWatcherAlive(int pid = 1111) => new(
        StagedBinaryPath: StagedPath,
        StagedBinaryExists: true,
        AutostartEntryPresent: true,
        AutostartEntryTarget: $"\"{StagedPath}\" --watch",
        AutostartEntryPointsAtStaged: true,
        WatcherRunning: true,
        WatcherPid: pid)
    {
        ScheduledTask = ScheduledTaskEntry.Absent,
    };

    private static WatcherInstallation FullyHealthy(int pid = 2222) =>
        ThreeInstalledWatcherAlive(pid) with
        {
            ScheduledTask = new ScheduledTaskEntry(
                Present: true,
                Enabled: true,
                Status: ScheduledTaskStatus.Running,
                ExecutablePath: StagedPath,
                PointsAtStaged: true),
            AutostartEffectiveness = AutostartEffectiveness.Effective,
            SupervisorState = SupervisorState.Healthy,
        };

    private static ScheduledTaskEntry ReadyTask(DateTimeOffset? nextRun) =>
        new(Present: true, Enabled: true, Status: ScheduledTaskStatus.Ready,
            NextRunTime: nextRun, ExecutablePath: StagedPath, PointsAtStaged: true);

    private static ScheduledTaskEntry DisabledTask() =>
        new(Present: true, Enabled: false, Status: ScheduledTaskStatus.Disabled,
            ExecutablePath: StagedPath, PointsAtStaged: true);

    // ---------- AppendInstallSteps ----------

    [Fact]
    public void AppendInstallSteps_emits_export_and_create_for_fresh_machine()
    {
        var steps = new List<InstallStep>();
        SupervisorDecision.AppendInstallSteps(steps, FreshMachine(), InvokingDownloads);

        Assert.Collection(steps,
            s => Assert.IsType<ExportScheduledTaskXmlStep>(s),
            s => Assert.IsType<CreateScheduledTaskStep>(s));
    }

    [Fact]
    public void AppendInstallSteps_targets_staged_path_not_invoking_path()
    {
        var steps = new List<InstallStep>();
        SupervisorDecision.AppendInstallSteps(steps, FreshMachine(), InvokingDownloads);

        var export = Assert.IsType<ExportScheduledTaskXmlStep>(steps[0]);
        Assert.Equal(StagedPath, export.StagedPath);
        Assert.DoesNotContain("Downloads", export.StagedPath);
    }

    [Fact]
    public void AppendInstallSteps_is_idempotent_on_repeat_call()
    {
        // 003-upgrade-case-A: run key present, task absent, watcher running.
        var state = ThreeInstalledWatcherAlive();
        var steps = new List<InstallStep>();

        SupervisorDecision.AppendInstallSteps(steps, state, StagedPath);
        SupervisorDecision.AppendInstallSteps(steps, state, StagedPath);

        // Two calls produce four steps but /F semantics on the executor side
        // make each a no-op after the first — and our contract says the
        // decision function emits the same list every time, which it does.
        Assert.Equal(4, steps.Count);
        Assert.All(steps, s => Assert.True(s is ExportScheduledTaskXmlStep or CreateScheduledTaskStep));
    }

    [Fact]
    public void AppendUninstallSteps_skips_delete_when_task_absent()
    {
        var steps = new List<InstallStep>();
        SupervisorDecision.AppendUninstallSteps(steps, FreshMachine());

        Assert.Empty(steps);
    }

    [Fact]
    public void AppendUninstallSteps_emits_delete_when_task_present()
    {
        var steps = new List<InstallStep>();
        var state = FreshMachine() with
        {
            ScheduledTask = new ScheduledTaskEntry(Present: true, Enabled: true, Status: ScheduledTaskStatus.Ready),
        };
        SupervisorDecision.AppendUninstallSteps(steps, state);

        Assert.Single(steps);
        Assert.IsType<DeleteScheduledTaskStep>(steps[0]);
    }

    // ---------- ClassifySupervisor ----------

    [Fact]
    public void ClassifySupervisor_returns_Absent_when_task_null()
    {
        var state = FreshMachine();

        Assert.Equal(SupervisorState.Absent, SupervisorDecision.ClassifySupervisor(state));
    }

    [Fact]
    public void ClassifySupervisor_returns_Disabled_when_task_disabled()
    {
        var state = FreshMachine() with { ScheduledTask = DisabledTask() };

        Assert.Equal(SupervisorState.Disabled, SupervisorDecision.ClassifySupervisor(state));
    }

    [Fact]
    public void ClassifySupervisor_returns_Healthy_when_watcher_running()
    {
        var state = FullyHealthy();

        Assert.Equal(SupervisorState.Healthy, SupervisorDecision.ClassifySupervisor(state));
    }

    [Fact]
    public void ClassifySupervisor_returns_RestartPending_when_watcher_dead_but_next_run_scheduled()
    {
        var state = FreshMachine() with
        {
            WatcherRunning = false,
            ScheduledTask = ReadyTask(DateTimeOffset.UtcNow.AddMinutes(1)),
        };

        Assert.Equal(SupervisorState.RestartPending, SupervisorDecision.ClassifySupervisor(state));
    }

    [Fact]
    public void ClassifySupervisor_returns_GaveUp_when_watcher_dead_and_no_next_run()
    {
        var state = FreshMachine() with
        {
            WatcherRunning = false,
            ScheduledTask = ReadyTask(nextRun: null),
        };

        Assert.Equal(SupervisorState.GaveUp, SupervisorDecision.ClassifySupervisor(state));
    }

    [Fact]
    public void ClassifySupervisor_returns_Healthy_when_task_status_Running_but_watcher_check_missed()
    {
        // Edge case: schtasks says Running but our mutex probe missed it
        // (racy). The Task Scheduler signal wins.
        var state = FreshMachine() with
        {
            WatcherRunning = false,
            ScheduledTask = new ScheduledTaskEntry(Present: true, Enabled: true,
                Status: ScheduledTaskStatus.Running, ExecutablePath: StagedPath, PointsAtStaged: true),
        };

        Assert.Equal(SupervisorState.Healthy, SupervisorDecision.ClassifySupervisor(state));
    }

    // ---------- ClassifyAutostart ----------

    [Fact]
    public void ClassifyAutostart_returns_NotRegistered_when_nothing_exists()
    {
        var state = FreshMachine();

        Assert.Equal(AutostartEffectiveness.NotRegistered,
            SupervisorDecision.ClassifyAutostart(state, runKeyApproved: true));
    }

    [Fact]
    public void ClassifyAutostart_returns_Effective_when_run_key_only()
    {
        var state = ThreeInstalledWatcherAlive();

        Assert.Equal(AutostartEffectiveness.Effective,
            SupervisorDecision.ClassifyAutostart(state, runKeyApproved: true));
    }

    [Fact]
    public void ClassifyAutostart_returns_Effective_when_task_only()
    {
        var state = FreshMachine() with { ScheduledTask = ReadyTask(DateTimeOffset.UtcNow.AddMinutes(1)) };

        Assert.Equal(AutostartEffectiveness.Effective,
            SupervisorDecision.ClassifyAutostart(state, runKeyApproved: true));
    }

    [Fact]
    public void ClassifyAutostart_returns_Effective_when_task_enabled_but_run_key_disabled()
    {
        var state = ThreeInstalledWatcherAlive() with { ScheduledTask = ReadyTask(DateTimeOffset.UtcNow.AddMinutes(1)) };

        Assert.Equal(AutostartEffectiveness.Effective,
            SupervisorDecision.ClassifyAutostart(state, runKeyApproved: false));
    }

    [Fact]
    public void ClassifyAutostart_returns_Degraded_when_run_key_disabled_and_task_disabled()
    {
        var state = ThreeInstalledWatcherAlive() with { ScheduledTask = DisabledTask() };

        Assert.Equal(AutostartEffectiveness.Degraded,
            SupervisorDecision.ClassifyAutostart(state, runKeyApproved: false));
    }

    [Fact]
    public void ClassifyAutostart_returns_Degraded_when_only_run_key_present_but_toggle_off()
    {
        var state = ThreeInstalledWatcherAlive();

        Assert.Equal(AutostartEffectiveness.Degraded,
            SupervisorDecision.ClassifyAutostart(state, runKeyApproved: false));
    }
}

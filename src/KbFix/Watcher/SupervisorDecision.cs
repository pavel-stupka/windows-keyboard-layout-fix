using System.Runtime.Versioning;

namespace KbFix.Watcher;

/// <summary>
/// Pure decision function for the per-user Scheduled Task layer introduced
/// by feature 004. Appends task-specific steps to an existing step list
/// produced by <see cref="InstallDecision"/>. Also exposes the two
/// classifier helpers the probe uses to populate
/// <see cref="WatcherInstallation.SupervisorState"/> and
/// <see cref="WatcherInstallation.AutostartEffectiveness"/>.
///
/// No I/O, no registry access, no process management — every branch is a
/// decision over the immutable <see cref="WatcherInstallation"/> record.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SupervisorDecision
{
    /// <summary>
    /// Appends the Scheduled-Task install/repair steps after the existing
    /// 003 steps (Run key + staging). Always emits
    /// <see cref="ExportScheduledTaskXmlStep"/> + <see cref="CreateScheduledTaskStep"/>
    /// because <c>schtasks /Create /F</c> is idempotent on its own and
    /// guarantees the emitted XML matches the current staged path on every
    /// invocation.
    /// </summary>
    public static void AppendInstallSteps(
        List<InstallStep> steps,
        WatcherInstallation state,
        string invokingBinaryPath)
    {
        // The task always launches the *staged* binary — that is the stable
        // path that survives the invoking kbfix.exe being moved or deleted.
        var stagedPath = state.StagedBinaryPath;
        steps.Add(new ExportScheduledTaskXmlStep(
            DestPath: WatcherInstallation.ScheduledTaskXmlPath,
            StagedPath: stagedPath));
        steps.Add(new CreateScheduledTaskStep(
            XmlPath: WatcherInstallation.ScheduledTaskXmlPath));
    }

    /// <summary>Appends the Scheduled-Task uninstall step only when the task is registered. Preserves the 003 "nothing installed → zero steps" invariant.</summary>
    public static void AppendUninstallSteps(List<InstallStep> steps, WatcherInstallation state)
    {
        if (state.ScheduledTask is { Present: true })
        {
            steps.Add(new DeleteScheduledTaskStep());
        }
    }

    /// <summary>
    /// Classifies supervisor state per <c>data-model.md §3</c>. Pure — the
    /// classifier takes a fully-populated <see cref="WatcherInstallation"/>
    /// and returns the enum value. Combine with the watcher-alive check
    /// from <see cref="WatcherInstallation.WatcherRunning"/>.
    /// </summary>
    public static SupervisorState ClassifySupervisor(WatcherInstallation state)
    {
        var task = state.ScheduledTask;
        if (task is null || !task.Present)
        {
            return SupervisorState.Absent;
        }
        if (task.Status == ScheduledTaskStatus.Disabled)
        {
            return SupervisorState.Disabled;
        }
        if (state.WatcherRunning || task.Status == ScheduledTaskStatus.Running)
        {
            return SupervisorState.Healthy;
        }
        if (task.NextRunTime is not null)
        {
            return SupervisorState.RestartPending;
        }
        // Ready / not-alive / no next run time → supervisor has given up
        // after exhausting its Restart-on-failure budget for this logon.
        return SupervisorState.GaveUp;
    }

    /// <summary>
    /// Classifies autostart effectiveness per <c>data-model.md §4</c>.
    /// Combines the Run-key presence + Startup-Approved probe with the
    /// Scheduled-Task presence + enabled state. <paramref name="runKeyApproved"/>
    /// is true when the Startup-Apps toggle has NOT disabled the entry
    /// (or the entry is absent, which counts as "not overridden").
    /// </summary>
    public static AutostartEffectiveness ClassifyAutostart(
        WatcherInstallation state,
        bool runKeyApproved)
    {
        var runEnabled = state.AutostartEntryPresent && runKeyApproved;
        var taskEnabled = state.ScheduledTask is { Present: true, Enabled: true };

        if (runEnabled || taskEnabled)
        {
            return AutostartEffectiveness.Effective;
        }
        var anyRegistered = state.AutostartEntryPresent
            || (state.ScheduledTask is not null && state.ScheduledTask.Present);
        if (anyRegistered)
        {
            return AutostartEffectiveness.Degraded;
        }
        return AutostartEffectiveness.NotRegistered;
    }
}

using System.Runtime.Versioning;

namespace KbFix.Watcher;

/// <summary>
/// A snapshot of the per-user installed state for the KbFix watcher. Produced
/// by <c>WatcherDiscovery.Probe()</c> and consumed by the three install/uninstall/
/// status commands via <see cref="InstallDecision"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed record WatcherInstallation(
    string StagedBinaryPath,
    bool StagedBinaryExists,
    bool AutostartEntryPresent,
    string? AutostartEntryTarget,
    bool AutostartEntryPointsAtStaged,
    bool WatcherRunning,
    int? WatcherPid)
{
    // --- 004-watcher-resilience additions ---
    // Init-only so the existing 003 positional constructor stays unchanged
    // for every pre-existing call site (including tests).

    /// <summary>Snapshot of the per-user Scheduled Task at <c>\KbFix\KbFixWatcher</c>. Null when no probe has populated it.</summary>
    public ScheduledTaskEntry? ScheduledTask { get; init; }

    /// <summary>Derived verdict on whether autostart will be effective at the next logon.</summary>
    public AutostartEffectiveness AutostartEffectiveness { get; init; } = AutostartEffectiveness.NotRegistered;

    /// <summary>Derived classification of the supervisor (Task Scheduler) state.</summary>
    public SupervisorState SupervisorState { get; init; } = SupervisorState.Absent;

    /// <summary>The previous watcher's recorded exit reason, read from <c>last-exit.json</c>. Null when the file is absent or unreadable.</summary>
    public LastExitReason? LastExitReason { get; init; }

    /// <summary>
    /// Canonical per-user staging directory. Always
    /// <c>%LOCALAPPDATA%\KbFix\</c>.
    /// </summary>
    public static string StagingDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KbFix");

    /// <summary>
    /// Canonical staging location for the installed binary. Always
    /// <c>%LOCALAPPDATA%\KbFix\kbfix.exe</c>.
    /// </summary>
    public static string DefaultStagedBinaryPath { get; } =
        Path.Combine(StagingDirectory, "kbfix.exe");

    public static string PidFilePath => Path.Combine(StagingDirectory, "watcher.pid");
    public static string LogFilePath => Path.Combine(StagingDirectory, "watcher.log");
    public static string LogFileRotatedPath => Path.Combine(StagingDirectory, "watcher.log.1");

    // --- 004 file-surface constants ---
    public static string LastExitFilePath => Path.Combine(StagingDirectory, "last-exit.json");
    public static string ScheduledTaskXmlPath => Path.Combine(StagingDirectory, "scheduled-task.xml");

    public const string InstanceMutexName = @"Local\KbFixWatcher.Instance";
    public const string StopEventName = @"Local\KbFixWatcher.StopEvent";

    public const string RunKeySubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunKeyValueName = "KbFixWatcher";

    // --- 004 autostart / scheduled-task constants ---
    public const string ScheduledTaskName = @"KbFix\KbFixWatcher";
    public const string StartupApprovedSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>
    /// Derived classification used by <c>--status</c> exit codes and by
    /// <see cref="InstallDecision"/> for test-table lookups. 004 extends
    /// the priority order documented in <c>data-model.md §5</c>.
    ///
    /// Backwards-compatible with 003 inputs: when the 004 fields sit at
    /// their defaults (<see cref="ScheduledTask"/> = null,
    /// <see cref="AutostartEffectiveness"/> = NotRegistered,
    /// <see cref="SupervisorState"/> = Absent), the classifier reduces
    /// to the 003 behaviour and no new state is synthesised.
    /// </summary>
    public InstalledState Classify()
    {
        // Priority 1: stale Run-key path — always the most actionable.
        if (AutostartEntryPresent && !AutostartEntryPointsAtStaged)
        {
            return InstalledState.StalePath;
        }

        var runKeyHealthy = AutostartEntryPresent && AutostartEntryPointsAtStaged;
        var taskHealthy = ScheduledTask is { Present: true, Enabled: true };
        var anyAutostart = AutostartEntryPresent || (ScheduledTask?.Present == true);

        // Priority 9 (last): truly nothing installed.
        if (!anyAutostart && !WatcherRunning && !StagedBinaryExists)
        {
            return InstalledState.NotInstalled;
        }

        // Priority 2: autostart mechanisms registered but all effectively disabled.
        // Only fires when the probe explicitly said Degraded (not the default).
        if (AutostartEffectiveness == AutostartEffectiveness.Degraded)
        {
            return InstalledState.AutostartDegraded;
        }

        // Watcher alive branch — supervisor state only matters when watcher is dead.
        if (WatcherRunning)
        {
            if (!anyAutostart)
            {
                return InstalledState.RunningWithoutAutostart;
            }
            if (StagedBinaryExists && (runKeyHealthy || taskHealthy))
            {
                return InstalledState.InstalledHealthy;
            }
            return InstalledState.MixedOrCorrupt;
        }

        // Watcher not alive.
        if (SupervisorState == SupervisorState.GaveUp)
        {
            return InstalledState.SupervisorGaveUp;
        }
        if (SupervisorState == SupervisorState.RestartPending)
        {
            return InstalledState.SupervisorBackingOff;
        }

        if (anyAutostart)
        {
            return InstalledState.InstalledNotRunning;
        }

        return InstalledState.MixedOrCorrupt;
    }
}

/// <summary>
/// Snapshot of the per-user Scheduled Task at <c>\KbFix\KbFixWatcher</c> as
/// observed by <c>schtasks.exe /Query</c>. All fields are null / default when
/// <see cref="Present"/> is false.
/// </summary>
internal sealed record ScheduledTaskEntry(
    bool Present,
    bool Enabled = false,
    ScheduledTaskStatus Status = ScheduledTaskStatus.Unknown,
    DateTimeOffset? LastRunTime = null,
    int? LastResult = null,
    DateTimeOffset? NextRunTime = null,
    string? Principal = null,
    string? ExecutablePath = null,
    bool PointsAtStaged = false)
{
    public static ScheduledTaskEntry Absent { get; } = new(Present: false);
}

/// <summary>Three-valued <c>Status:</c> field returned by <c>schtasks /Query /V /FO LIST</c>.</summary>
internal enum ScheduledTaskStatus
{
    Unknown,
    Ready,
    Running,
    Disabled,
}

/// <summary>Derived supervisor state — see <c>data-model.md §3</c>.</summary>
internal enum SupervisorState
{
    Healthy,
    RestartPending,
    GaveUp,
    Disabled,
    Absent,
    Unknown,
}

/// <summary>Derived autostart effectiveness — see <c>data-model.md §4</c>.</summary>
internal enum AutostartEffectiveness
{
    Effective,
    Degraded,
    NotRegistered,
}

internal enum InstalledState
{
    NotInstalled,
    InstalledHealthy,
    InstalledNotRunning,
    RunningWithoutAutostart,
    StalePath,
    MixedOrCorrupt,

    // --- 004-watcher-resilience additions ---

    /// <summary>Watcher not alive; Scheduled-Task Restart-on-failure has a pending retry.</summary>
    SupervisorBackingOff,

    /// <summary>Watcher not alive; Scheduled-Task Restart-on-failure budget exhausted. Re-run --install.</summary>
    SupervisorGaveUp,

    /// <summary>Autostart mechanisms are registered but all effectively disabled (Startup-Apps toggle off AND task disabled).</summary>
    AutostartDegraded,
}

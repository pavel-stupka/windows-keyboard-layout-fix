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

    public const string InstanceMutexName = @"Local\KbFixWatcher.Instance";
    public const string StopEventName = @"Local\KbFixWatcher.StopEvent";

    public const string RunKeySubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunKeyValueName = "KbFixWatcher";

    /// <summary>
    /// Derived classification used by <c>--status</c> exit codes and by
    /// <see cref="InstallDecision"/> for test-table lookups.
    /// </summary>
    public InstalledState Classify()
    {
        if (!AutostartEntryPresent && !WatcherRunning && !StagedBinaryExists)
        {
            return InstalledState.NotInstalled;
        }

        if (AutostartEntryPresent && !AutostartEntryPointsAtStaged)
        {
            return InstalledState.StalePath;
        }

        if (AutostartEntryPresent && WatcherRunning && AutostartEntryPointsAtStaged && StagedBinaryExists)
        {
            return InstalledState.InstalledHealthy;
        }

        if (AutostartEntryPresent && !WatcherRunning)
        {
            return InstalledState.InstalledNotRunning;
        }

        if (!AutostartEntryPresent && WatcherRunning)
        {
            return InstalledState.RunningWithoutAutostart;
        }

        return InstalledState.MixedOrCorrupt;
    }
}

internal enum InstalledState
{
    NotInstalled,
    InstalledHealthy,
    InstalledNotRunning,
    RunningWithoutAutostart,
    StalePath,
    MixedOrCorrupt,
}

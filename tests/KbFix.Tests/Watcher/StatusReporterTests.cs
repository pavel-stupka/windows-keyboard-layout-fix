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
        Assert.Contains("watcher:   not running", s);
        Assert.Contains("autostart: not registered", s);
        Assert.Contains("staged:    not present", s);
        Assert.DoesNotContain("log:", s);
        Assert.Contains("State: NotInstalled", s);
    }

    [Fact]
    public void InstalledHealthy_reports_pid_and_log_path()
    {
        var s = StatusReporter.Format(InstalledHealthy(pid: 12345), quiet: false);

        Assert.Contains("watcher:   running (pid 12345)", s);
        Assert.Contains("autostart: registered", s);
        Assert.Contains($"\"{StagedPath}\" --watch", s);
        Assert.DoesNotContain("STALE", s);
        Assert.Contains("staged:    present", s);
        Assert.Contains(StagedPath, s);
        Assert.Contains("log:", s);
        Assert.Contains(WatcherInstallation.LogFilePath, s);
        Assert.Contains("State: InstalledHealthy", s);
    }

    [Fact]
    public void InstalledNotRunning_marks_watcher_not_running_but_autostart_registered()
    {
        var s = StatusReporter.Format(InstalledNotRunning(), quiet: false);

        Assert.Contains("watcher:   not running", s);
        Assert.Contains("autostart: registered", s);
        Assert.Contains("State: InstalledNotRunning", s);
    }

    [Fact]
    public void RunningWithoutAutostart_reports_expected_state()
    {
        var s = StatusReporter.Format(RunningWithoutAutostart(), quiet: false);

        Assert.Contains("watcher:   running (pid 9999)", s);
        Assert.Contains("autostart: not registered", s);
        Assert.Contains("State: RunningWithoutAutostart", s);
    }

    [Fact]
    public void StalePath_marks_autostart_as_STALE()
    {
        var s = StatusReporter.Format(StalePath(), quiet: false);

        Assert.Contains("autostart: registered", s);
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
    }
}

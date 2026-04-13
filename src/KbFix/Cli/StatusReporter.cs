using System.Runtime.Versioning;
using System.Text;
using KbFix.Watcher;

namespace KbFix.Cli;

/// <summary>
/// Formats the stdout report for <c>--status</c> per
/// <c>specs/003-background-watcher/contracts/cli.md</c>. Pure formatter;
/// no I/O.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StatusReporter
{
    public static string Format(WatcherInstallation state, bool quiet)
    {
        var sb = new StringBuilder();
        sb.AppendLine("KbFix status");

        if (!quiet)
        {
            sb.AppendLine($"  watcher:   {FormatWatcher(state)}");
            sb.AppendLine($"  autostart: {FormatAutostart(state)}");
            sb.AppendLine($"  staged:    {FormatStaged(state)}");
            if (state.Classify() != InstalledState.NotInstalled)
            {
                sb.AppendLine($"  log:       {WatcherInstallation.LogFilePath}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"State: {state.Classify()}");
        return sb.ToString();
    }

    private static string FormatWatcher(WatcherInstallation state)
    {
        if (!state.WatcherRunning)
        {
            return "not running";
        }
        var pid = state.WatcherPid?.ToString() ?? "?";
        return $"running (pid {pid})";
    }

    private static string FormatAutostart(WatcherInstallation state)
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

    private static string FormatStaged(WatcherInstallation state)
    {
        if (!state.StagedBinaryExists)
        {
            return "not present";
        }
        return $"present      ({state.StagedBinaryPath})";
    }

    /// <summary>
    /// Maps an <see cref="InstalledState"/> to the exit code the CLI contract
    /// defines for <c>--status</c>.
    /// </summary>
    public static int ExitCodeFor(InstalledState state) => state switch
    {
        InstalledState.InstalledHealthy => Diagnostics.ExitCodes.Success,
        InstalledState.NotInstalled => Diagnostics.ExitCodes.NotInstalled,
        InstalledState.InstalledNotRunning => Diagnostics.ExitCodes.InstalledNotRunning,
        InstalledState.RunningWithoutAutostart => Diagnostics.ExitCodes.RunningWithoutAutostart,
        InstalledState.StalePath => Diagnostics.ExitCodes.StalePath,
        InstalledState.MixedOrCorrupt => Diagnostics.ExitCodes.MixedOrCorrupt,
        _ => Diagnostics.ExitCodes.Failure,
    };
}

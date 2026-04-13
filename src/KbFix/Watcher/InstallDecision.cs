using System.Runtime.Versioning;

namespace KbFix.Watcher;

/// <summary>
/// Pure decision function. Given an observed <see cref="WatcherInstallation"/>
/// and the path of the currently-running binary, emits the ordered list of
/// <see cref="InstallStep"/>s the executor should apply.
///
/// This type contains NO I/O, NO registry access, NO process management. It
/// exists so that the entire install/uninstall/status branching can be unit
/// tested without touching Windows. The concrete executor lives in
/// <c>Platform/Install/InstallExecutor.cs</c>.
/// </summary>
internal static class InstallDecision
{
    public static IReadOnlyList<InstallStep> ComputeInstallSteps(
        WatcherInstallation state,
        string invokingBinaryPath)
    {
        var stagedPath = state.StagedBinaryPath;
        var invokingIsStaged = PathsEqual(invokingBinaryPath, stagedPath);

        var steps = new List<InstallStep>();

        // If the invoking binary is different from the staged copy (e.g. user
        // ran a newer version from Downloads), we must stop the old watcher
        // before overwriting its executable.
        if (!invokingIsStaged && state.WatcherRunning)
        {
            steps.Add(new SignalStopEventStep(3000));
            if (state.WatcherPid is int pid)
            {
                steps.Add(new ForceKillWatcherStep(pid));
            }
        }

        // Stage the binary unless the invoking copy IS the staged copy.
        if (!invokingIsStaged)
        {
            steps.Add(new EnsureStagingDirectoryStep());
            steps.Add(new CopyBinaryToStagedStep(invokingBinaryPath));
        }
        else if (!state.StagedBinaryExists)
        {
            // Edge case: invoking path matches the canonical staged path but
            // the file isn't seen by the probe (racy / deleted mid-flight).
            // Ensure the directory and note the staged file; no copy needed.
            steps.Add(new EnsureStagingDirectoryStep());
        }

        // Register or repair the Run key.
        if (!state.AutostartEntryPresent || !state.AutostartEntryPointsAtStaged)
        {
            steps.Add(new WriteRunKeyStep(stagedPath));
        }

        // Spawn a watcher if none is running, OR if we just stopped the old one.
        if (!state.WatcherRunning || !invokingIsStaged)
        {
            steps.Add(new SpawnWatcherStep(stagedPath));
        }

        return steps;
    }

    public static IReadOnlyList<InstallStep> ComputeUninstallSteps(
        WatcherInstallation state,
        string invokingBinaryPath)
    {
        var steps = new List<InstallStep>();

        if (state.WatcherRunning)
        {
            steps.Add(new SignalStopEventStep(3000));
            if (state.WatcherPid is int pid)
            {
                steps.Add(new ForceKillWatcherStep(pid));
            }
        }

        if (state.AutostartEntryPresent)
        {
            steps.Add(new DeleteRunKeyStep());
        }

        if (state.StagedBinaryExists)
        {
            // Always emit both steps. If the invoking binary IS the staged
            // copy, InstallExecutor handles the Windows "cannot delete
            // running executable" constraint by renaming the file to %TEMP%
            // and scheduling it for reboot-delete, which leaves the staging
            // directory empty and the DeleteStagingDirectoryStep can then
            // proceed normally.
            steps.Add(new DeleteStagedBinaryStep());
            steps.Add(new DeleteStagingDirectoryStep());
        }

        return steps;
    }

    public static IReadOnlyList<InstallStep> ComputeStatusSteps(WatcherInstallation state)
    {
        return new InstallStep[] { new ReportStatusStep() };
    }

    private static bool PathsEqual(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(a),
                Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Base type for install/uninstall/status steps.</summary>
internal abstract record InstallStep;

internal sealed record EnsureStagingDirectoryStep : InstallStep;
internal sealed record CopyBinaryToStagedStep(string SourcePath) : InstallStep;
internal sealed record WriteRunKeyStep(string StagedPath) : InstallStep;
internal sealed record DeleteRunKeyStep : InstallStep;
internal sealed record SignalStopEventStep(int TimeoutMs) : InstallStep;
internal sealed record ForceKillWatcherStep(int Pid) : InstallStep;
internal sealed record SpawnWatcherStep(string StagedPath) : InstallStep;
internal sealed record DeleteStagedBinaryStep : InstallStep;
internal sealed record DeleteStagingDirectoryStep : InstallStep;
internal sealed record ReportStatusStep : InstallStep;

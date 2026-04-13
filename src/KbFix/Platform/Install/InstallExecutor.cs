using System.Runtime.Versioning;
using KbFix.Watcher;

namespace KbFix.Platform.Install;

/// <summary>
/// Executes the ordered list of <see cref="InstallStep"/>s produced by
/// <see cref="InstallDecision"/>. Each step is applied via the corresponding
/// platform helper (<see cref="BinaryStaging"/>, <see cref="AutostartRegistry"/>,
/// <see cref="WatcherLauncher"/>, <see cref="WatcherDiscovery"/>) and the
/// result is collected so the caller can render a user-facing report.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class InstallExecutor
{
    public sealed record StepResult(InstallStep Step, bool Succeeded, string? Note);

    public IReadOnlyList<StepResult> Apply(
        IEnumerable<InstallStep> steps,
        string invokingBinaryPath)
    {
        var results = new List<StepResult>();
        var invokingIsStaged = PathsEqual(invokingBinaryPath, WatcherInstallation.DefaultStagedBinaryPath);
        var skipDirectoryDelete = false;

        foreach (var step in steps)
        {
            var result = step switch
            {
                EnsureStagingDirectoryStep => ApplyEnsureStagingDirectory(step),
                CopyBinaryToStagedStep c => ApplyCopyBinary(step, c),
                WriteRunKeyStep w => ApplyWriteRunKey(step, w),
                DeleteRunKeyStep => ApplyDeleteRunKey(step),
                SignalStopEventStep s => ApplySignalStopEvent(step, s),
                ForceKillWatcherStep f => ApplyForceKill(step, f),
                SpawnWatcherStep sp => ApplySpawnWatcher(step, sp),
                DeleteStagedBinaryStep => ApplyDeleteStagedBinary(step, invokingIsStaged, ref skipDirectoryDelete),
                DeleteStagingDirectoryStep => ApplyDeleteStagingDirectory(step, skipDirectoryDelete),
                ReportStatusStep => new StepResult(step, true, null),
                _ => new StepResult(step, false, $"unknown step type: {step.GetType().Name}"),
            };
            results.Add(result);
        }

        return results;
    }

    private static StepResult ApplyEnsureStagingDirectory(InstallStep step)
    {
        try
        {
            BinaryStaging.EnsureStagingDirectory();
            return new StepResult(step, true, null);
        }
        catch (Exception ex)
        {
            return new StepResult(step, false, ex.Message);
        }
    }

    private static StepResult ApplyCopyBinary(InstallStep step, CopyBinaryToStagedStep c)
    {
        try
        {
            BinaryStaging.CopyBinaryToStaged(c.SourcePath);
            return new StepResult(step, true, null);
        }
        catch (Exception ex)
        {
            return new StepResult(step, false, ex.Message);
        }
    }

    private static StepResult ApplyWriteRunKey(InstallStep step, WriteRunKeyStep w)
    {
        try
        {
            AutostartRegistry.WriteRunKey(w.StagedPath);
            return new StepResult(step, true, null);
        }
        catch (Exception ex)
        {
            return new StepResult(step, false, ex.Message);
        }
    }

    private static StepResult ApplyDeleteRunKey(InstallStep step)
    {
        var existed = AutostartRegistry.DeleteRunKey();
        return new StepResult(step, true, existed ? null : "already absent");
    }

    private static StepResult ApplySignalStopEvent(InstallStep step, SignalStopEventStep s)
    {
        var stopped = WatcherDiscovery.SignalStopEvent(TimeSpan.FromMilliseconds(s.TimeoutMs));
        return new StepResult(step, true, stopped ? "cooperative shutdown" : "timeout or no watcher");
    }

    private static StepResult ApplyForceKill(InstallStep step, ForceKillWatcherStep f)
    {
        // Re-probe: if the cooperative stop already took effect, skip.
        var stillRunning = WatcherDiscovery.Probe().WatcherRunning;
        if (!stillRunning)
        {
            return new StepResult(step, true, "skipped (already stopped)");
        }

        var killed = WatcherDiscovery.ForceKillWatcher(f.Pid, WatcherInstallation.DefaultStagedBinaryPath);
        return new StepResult(step, killed, killed ? "forced" : "kill failed");
    }

    private static StepResult ApplySpawnWatcher(InstallStep step, SpawnWatcherStep sp)
    {
        try
        {
            WatcherLauncher.SpawnDetached(sp.StagedPath);
            return new StepResult(step, true, null);
        }
        catch (Exception ex)
        {
            return new StepResult(step, false, ex.Message);
        }
    }

    private static StepResult ApplyDeleteStagedBinary(InstallStep step, bool invokingIsStaged, ref bool skipDirectoryDelete)
    {
        if (invokingIsStaged)
        {
            // Windows forbids deleting the executable of a running process, but
            // it permits renaming it. Move ourselves out of the staging dir
            // so the dir can be cleaned up, then mark the moved copy for
            // deletion at next reboot.
            var moved = BinaryStaging.MoveRunningBinaryToTempForRebootDelete();
            if (moved)
            {
                return new StepResult(step, true, "moved to %TEMP% (cleaned up at next reboot)");
            }

            // Move failed — fall back to leaving the binary in place and
            // skipping the directory cleanup so we don't leave an orphan file
            // inside a half-deleted dir.
            skipDirectoryDelete = true;
            return new StepResult(step, true, "skipped (currently running and rename failed)");
        }

        var deleted = BinaryStaging.DeleteStagedBinary();
        return new StepResult(step, true, deleted ? null : "not present");
    }

    private static StepResult ApplyDeleteStagingDirectory(InstallStep step, bool skipDirectoryDelete)
    {
        if (skipDirectoryDelete)
        {
            return new StepResult(step, true, "skipped (binary still in use)");
        }

        BinaryStaging.DeleteStagingDirectory();
        return new StepResult(step, true, null);
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

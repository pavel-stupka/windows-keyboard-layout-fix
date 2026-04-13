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

    // Step result notes used by the reporter to format user-visible text.
    // Kept as constants to avoid stringly-typed matching scattered across files.
    public const string NoteDeleted = "deleted";
    public const string NoteNotPresent = "not present";
    public const string NoteMovedForRebootDelete = "moved to %TEMP% (cleaned up at next reboot)";
    public const string NoteDeleteFailed = "delete failed — file still in staging directory";

    public IReadOnlyList<StepResult> Apply(
        IEnumerable<InstallStep> steps,
        string invokingBinaryPath)
    {
        var results = new List<StepResult>();

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
                DeleteStagedBinaryStep => ApplyDeleteStagedBinary(step),
                DeleteStagingDirectoryStep => ApplyDeleteStagingDirectory(step),
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

    private static StepResult ApplyDeleteStagedBinary(InstallStep step)
    {
        // DeleteStagedBinary handles every case internally: first-try delete,
        // retry-with-backoff for transient file-handle holds (common when a
        // watcher process just exited and Windows has not yet released the
        // .exe mapping), and fallback to rename-to-%TEMP%+reboot-delete for
        // the self-uninstall case where the file handle is held permanently
        // by THIS process.
        return BinaryStaging.DeleteStagedBinary() switch
        {
            BinaryStaging.DeleteOutcome.Deleted => new StepResult(step, true, NoteDeleted),
            BinaryStaging.DeleteOutcome.NotPresent => new StepResult(step, true, NoteNotPresent),
            BinaryStaging.DeleteOutcome.MovedForRebootDelete => new StepResult(step, true, NoteMovedForRebootDelete),
            BinaryStaging.DeleteOutcome.Failed => new StepResult(step, false, NoteDeleteFailed),
            _ => new StepResult(step, false, "unknown"),
        };
    }

    private static StepResult ApplyDeleteStagingDirectory(InstallStep step)
    {
        BinaryStaging.DeleteStagingDirectory();
        return new StepResult(step, true, null);
    }
}

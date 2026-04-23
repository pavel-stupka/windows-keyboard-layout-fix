namespace KbFix.Watcher;

internal enum WatcherExitReason
{
    /// <summary>Loop exited because the stop event was signaled.</summary>
    StopSignaled,

    /// <summary>Loop exited because the persisted configuration remained unreadable beyond the grace period.</summary>
    ConfigUnrecoverable,

    // --- 004-watcher-resilience additions ---

    /// <summary>Equivalent to <see cref="StopSignaled"/> but named to match the JSON contract of <c>last-exit.json</c>. Used by <c>WatcherMain</c> when persisting the exit reason.</summary>
    CooperativeShutdown,

    /// <summary>An unhandled exception escaped the watcher's main thread. Written by the <c>AppDomain.UnhandledException</c> handler before the runtime terminates the process.</summary>
    CrashedUnhandled,

    /// <summary>Initialization failed before the poll loop could start (mutex creation, log init, staging-directory unwriteable, etc.).</summary>
    StartupFailed,

    /// <summary>The previous watcher was observed missing at the current watcher's startup — an external termination (TerminateProcess, antivirus kill) happened and no in-process write path could record it.</summary>
    SupervisorObservedDead,
}

/// <summary>
/// The background watcher's main poll-and-reconcile loop. Pure orchestration —
/// no Win32, no filesystem, no registry. All side-effecting dependencies are
/// injected so the loop can be unit tested deterministically.
/// </summary>
internal sealed class WatcherLoop
{
    private readonly ISessionReconciler _reconciler;
    private readonly FlapDetector _flapDetector;
    private readonly IWatcherLog _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, bool> _waitForStop;

    private static readonly TimeSpan FastPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MidPoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowPoll = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConfigGracePeriod = TimeSpan.FromSeconds(60);

    private const int NoOpsBeforeMid = 5;
    private const int NoOpsBeforeSlow = 15;

    public WatcherLoop(
        ISessionReconciler reconciler,
        FlapDetector flapDetector,
        IWatcherLog log,
        Func<DateTimeOffset> clock,
        Func<TimeSpan, bool> waitForStop)
    {
        _reconciler = reconciler;
        _flapDetector = flapDetector;
        _log = log;
        _clock = clock;
        _waitForStop = waitForStop;
    }

    public WatcherExitReason Run()
    {
        _log.Start();
        try
        {
            var consecutiveNoOps = 0;
            DateTimeOffset? configFailureStart = null;

            while (true)
            {
                var now = _clock();

                // Paused by flap detector: sleep one fast-poll tick and re-check.
                if (_flapDetector.IsPaused(now))
                {
                    if (_waitForStop(FastPoll))
                    {
                        return WatcherExitReason.StopSignaled;
                    }
                    continue;
                }

                ReconcileResult result;
                try
                {
                    result = _reconciler.ReconcileOnce();
                }
                catch (Exception ex)
                {
                    _log.ReconcileFailed(ex.Message);
                    if (_waitForStop(FastPoll))
                    {
                        return WatcherExitReason.StopSignaled;
                    }
                    continue;
                }

                if (result.Outcome == ReconcileOutcome.ConfigReadFailed)
                {
                    _log.ConfigReadFailed(result.FailureReason ?? "(no reason)");
                    configFailureStart ??= now;
                    if (now - configFailureStart.Value > ConfigGracePeriod)
                    {
                        return WatcherExitReason.ConfigUnrecoverable;
                    }
                    if (_waitForStop(FastPoll))
                    {
                        return WatcherExitReason.StopSignaled;
                    }
                    continue;
                }

                configFailureStart = null;

                switch (result.Outcome)
                {
                    case ReconcileOutcome.Refused:
                        _log.SessionEmptyRefused();
                        consecutiveNoOps = 0;
                        break;

                    case ReconcileOutcome.NoOp:
                        _log.ReconcileNoOp();
                        consecutiveNoOps++;
                        break;

                    case ReconcileOutcome.Applied:
                        _log.ReconcileApplied(result.ActionsApplied);
                        consecutiveNoOps = 0;
                        _flapDetector.Record(_clock());
                        if (_flapDetector.IsPaused(_clock()))
                        {
                            _log.FlapBackoff();
                        }
                        break;

                    case ReconcileOutcome.Failed:
                        _log.ReconcileFailed(result.FailureReason ?? "(no reason)");
                        consecutiveNoOps = 0;
                        break;
                }

                var interval = consecutiveNoOps switch
                {
                    >= NoOpsBeforeSlow => SlowPoll,
                    >= NoOpsBeforeMid => MidPoll,
                    _ => FastPoll,
                };

                if (_waitForStop(interval))
                {
                    return WatcherExitReason.StopSignaled;
                }
            }
        }
        finally
        {
            _log.Stop();
        }
    }
}

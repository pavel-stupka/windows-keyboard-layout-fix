namespace KbFix.Watcher;

/// <summary>
/// Seam over the existing one-shot reconciliation pipeline so the
/// <see cref="WatcherLoop"/> can be unit tested without touching Win32.
/// </summary>
internal interface ISessionReconciler : IDisposable
{
    ReconcileResult ReconcileOnce();
}

internal readonly record struct ReconcileResult(
    ReconcileOutcome Outcome,
    int ActionsApplied,
    string? FailureReason);

internal enum ReconcileOutcome
{
    /// <summary>Session already matched persisted config — nothing to do.</summary>
    NoOp,

    /// <summary>Reconciliation ran and applied one or more actions successfully.</summary>
    Applied,

    /// <summary>Planner refused to reconcile (e.g. would empty the session layout set).</summary>
    Refused,

    /// <summary>Reconciliation attempted but at least one action failed.</summary>
    Failed,

    /// <summary>The persisted configuration could not be read at all (e.g. HKCU hive unavailable).</summary>
    ConfigReadFailed,
}

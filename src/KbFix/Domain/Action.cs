namespace KbFix.Domain;

internal enum ActionKind
{
    SwitchActive,
    Deactivate,
}

/// <summary>One mutation the reconciliation plan intends to perform.</summary>
internal sealed record PlannedAction(ActionKind Kind, LayoutId LayoutId);

/// <summary>The result of attempting (or simulating) one <see cref="PlannedAction"/>.</summary>
internal sealed record AppliedAction(PlannedAction Planned, bool Succeeded, string? Failure);

/// <summary>Final outcome of a single <c>kbfix</c> run.</summary>
internal enum Outcome
{
    Success,
    NoOp,
    Refused,
    Failed,
}

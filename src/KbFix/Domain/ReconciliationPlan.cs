namespace KbFix.Domain;

/// <summary>
/// Pure diff between persisted (desired) and session (actual) layout state,
/// plus the ordered actions needed to converge them. This is the unit-tested
/// core of the utility — no Windows interop, no I/O, no time.
/// </summary>
internal sealed class ReconciliationPlan
{
    public IReadOnlyList<LayoutId> ToRemove { get; }
    public LayoutId? MustSwitchFirst { get; }
    public bool NoOp { get; }
    public bool Refused { get; }
    public string? RefuseReason { get; }
    public IReadOnlyList<PlannedAction> Actions { get; }

    private ReconciliationPlan(
        IReadOnlyList<LayoutId> toRemove,
        LayoutId? mustSwitchFirst,
        bool noOp,
        bool refused,
        string? refuseReason)
    {
        ToRemove = toRemove;
        MustSwitchFirst = mustSwitchFirst;
        NoOp = noOp;
        Refused = refused;
        RefuseReason = refuseReason;

        if (refused || noOp)
        {
            Actions = Array.Empty<PlannedAction>();
            return;
        }

        var actions = new List<PlannedAction>(toRemove.Count + 1);
        if (mustSwitchFirst is { } switchTarget)
        {
            actions.Add(new PlannedAction(ActionKind.SwitchActive, switchTarget));
        }

        foreach (var id in toRemove)
        {
            actions.Add(new PlannedAction(ActionKind.Deactivate, id));
        }

        Actions = actions;
    }

    public static ReconciliationPlan Build(PersistedConfig persisted, SessionState session)
    {
        if (persisted is null)
        {
            throw new ArgumentNullException(nameof(persisted));
        }

        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        // Edge case: persisted set is empty — refuse, never reconcile to zero.
        if (persisted.Layouts.Count == 0)
        {
            return new ReconciliationPlan(
                Array.Empty<LayoutId>(),
                mustSwitchFirst: null,
                noOp: false,
                refused: true,
                refuseReason: "persisted layout set is empty; refusing to reconcile to zero layouts");
        }

        // Compute removals deterministically.
        var toRemove = session.Layouts.Difference(persisted.Layouts).Sorted().ToArray();

        if (toRemove.Length == 0)
        {
            return new ReconciliationPlan(
                toRemove,
                mustSwitchFirst: null,
                noOp: true,
                refused: false,
                refuseReason: null);
        }

        // If the foreground layout would be removed, find a survivor to switch to first.
        LayoutId? switchFirst = null;
        if (Array.IndexOf(toRemove, session.ActiveLayout) >= 0)
        {
            // First persisted layout that is also currently in the session, sorted.
            switchFirst = persisted.Layouts
                .Sorted()
                .Cast<LayoutId?>()
                .FirstOrDefault(id => session.Layouts.Contains(id!.Value));

            if (switchFirst is null)
            {
                return new ReconciliationPlan(
                    Array.Empty<LayoutId>(),
                    mustSwitchFirst: null,
                    noOp: false,
                    refused: true,
                    refuseReason: "session has no persisted layout to fall back to; refusing");
            }
        }

        return new ReconciliationPlan(
            toRemove,
            switchFirst,
            noOp: false,
            refused: false,
            refuseReason: null);
    }
}

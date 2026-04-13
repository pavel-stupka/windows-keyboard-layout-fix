using KbFix.Domain;
using Xunit;

namespace KbFix.Tests.Domain;

public class ReconciliationPlanTests
{
    private static readonly LayoutId Cs       = LayoutId.Create(0x0405, "00000405");
    private static readonly LayoutId CsQwertz = LayoutId.Create(0x0405, "00010405");
    private static readonly LayoutId En       = LayoutId.Create(0x0409, "00000409");
    private static readonly LayoutId EnIntl   = LayoutId.Create(0x0409, "00020409");

    private static PersistedConfig Persisted(params LayoutId[] ids)
        => new(new LayoutSet(ids), DateTime.UnixEpoch);

    private static SessionState Session(LayoutId active, params LayoutId[] ids)
        => new(new LayoutSet(ids), active, DateTime.UnixEpoch);

    [Fact]
    public void NoOp_when_session_already_matches_persisted()
    {
        var plan = ReconciliationPlan.Build(
            Persisted(Cs, En),
            Session(active: Cs, Cs, En));

        Assert.True(plan.NoOp);
        Assert.False(plan.Refused);
        Assert.Empty(plan.Actions);
        Assert.Empty(plan.ToRemove);
        Assert.Null(plan.MustSwitchFirst);
    }

    [Fact]
    public void Simple_removal_when_active_layout_is_persisted()
    {
        var plan = ReconciliationPlan.Build(
            Persisted(Cs, En),
            Session(active: Cs, Cs, CsQwertz, En, EnIntl));

        Assert.False(plan.NoOp);
        Assert.False(plan.Refused);
        Assert.Null(plan.MustSwitchFirst);
        Assert.Equal(new[] { CsQwertz, EnIntl }, plan.ToRemove);
        Assert.Equal(2, plan.Actions.Count);
        Assert.All(plan.Actions, a => Assert.Equal(ActionKind.Deactivate, a.Kind));
    }

    [Fact]
    public void Switch_first_when_active_layout_is_in_removal_set()
    {
        var plan = ReconciliationPlan.Build(
            Persisted(Cs, En),
            Session(active: CsQwertz, Cs, CsQwertz, En));

        Assert.False(plan.NoOp);
        Assert.False(plan.Refused);
        Assert.NotNull(plan.MustSwitchFirst);
        // Cs is the first persisted layout (sorted) also in the session.
        Assert.Equal(Cs, plan.MustSwitchFirst!.Value);

        Assert.Equal(2, plan.Actions.Count);
        Assert.Equal(ActionKind.SwitchActive, plan.Actions[0].Kind);
        Assert.Equal(Cs, plan.Actions[0].LayoutId);
        Assert.Equal(ActionKind.Deactivate, plan.Actions[1].Kind);
        Assert.Equal(CsQwertz, plan.Actions[1].LayoutId);
    }

    [Fact]
    public void Refused_when_persisted_set_is_empty()
    {
        var plan = ReconciliationPlan.Build(
            new PersistedConfig(LayoutSet.Empty, DateTime.UnixEpoch),
            Session(active: CsQwertz, CsQwertz));

        Assert.True(plan.Refused);
        Assert.False(plan.NoOp);
        Assert.NotNull(plan.RefuseReason);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Refused_when_no_persisted_fallback_in_session()
    {
        // Persisted has Cs+En but session has neither (only the unwanted CsQwertz),
        // and the active layout is in the removal set, so there's nowhere to switch to.
        var plan = ReconciliationPlan.Build(
            Persisted(Cs, En),
            Session(active: CsQwertz, CsQwertz));

        Assert.True(plan.Refused);
        Assert.False(plan.NoOp);
        Assert.NotNull(plan.RefuseReason);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Idempotent_when_replayed_after_simulated_apply()
    {
        var persisted = Persisted(Cs, En);
        var first = ReconciliationPlan.Build(
            persisted,
            Session(active: Cs, Cs, CsQwertz, En));
        Assert.False(first.NoOp);

        // Simulate applying: remove CsQwertz from the session, active stays Cs.
        var afterApply = Session(active: Cs, Cs, En);
        var second = ReconciliationPlan.Build(persisted, afterApply);

        Assert.True(second.NoOp);
        Assert.Empty(second.Actions);
    }

    [Fact]
    public void Action_ordering_is_deterministic()
    {
        var plan = ReconciliationPlan.Build(
            Persisted(Cs, En),
            Session(active: Cs, EnIntl, CsQwertz, Cs, En));

        // Sorted by (langId, klid): CsQwertz (0x0405,00010405) before EnIntl (0x0409,00020409)
        Assert.Equal(new[] { CsQwertz, EnIntl }, plan.ToRemove);
    }
}

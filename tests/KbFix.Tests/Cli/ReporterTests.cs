using KbFix.Cli;
using KbFix.Domain;
using Xunit;

namespace KbFix.Tests.Cli;

public class ReporterTests
{
    private static readonly LayoutId Cs       = LayoutId.Create(0x0405, "00000405");
    private static readonly LayoutId CsQwertz = LayoutId.Create(0x0405, "00010405");
    private static readonly LayoutId En       = LayoutId.Create(0x0409, "00000409");

    private static PersistedConfig Persisted(params LayoutId[] ids)
        => new(new LayoutSet(ids), DateTime.UnixEpoch);

    private static SessionState Session(LayoutId active, params LayoutId[] ids)
        => new(new LayoutSet(ids), active, DateTime.UnixEpoch);

    [Fact]
    public void NoOp_report_contains_all_four_sections_and_success_line()
    {
        var report = new Reporter().FormatReport(
            Persisted(Cs, En),
            Session(Cs, Cs, En),
            Array.Empty<AppliedAction>(),
            Outcome.NoOp,
            dryRun: false,
            quiet: false);

        Assert.Contains("Persisted layouts:", report);
        Assert.Contains("Session layouts:", report);
        Assert.Contains("Actions:", report);
        Assert.Contains("(none)", report);
        Assert.Contains("Result: NO-OP", report);
    }

    [Fact]
    public void Success_report_with_one_deactivate_lists_action_and_success_line()
    {
        var actions = new[]
        {
            new AppliedAction(new PlannedAction(ActionKind.Deactivate, CsQwertz), Succeeded: true, Failure: null),
        };

        var report = new Reporter().FormatReport(
            Persisted(Cs, En),
            Session(Cs, Cs, CsQwertz, En),
            actions,
            Outcome.Success,
            dryRun: false,
            quiet: false);

        Assert.Contains("Deactivate", report);
        Assert.Contains("0405 00010405", report);
        Assert.Contains(": OK", report);
        Assert.Contains("Result: SUCCESS — removed 1 session-only layout(s)", report);
        Assert.Contains("session-only", report); // session marker on the extra layout
    }

    [Fact]
    public void Success_report_with_switch_then_deactivate()
    {
        var actions = new[]
        {
            new AppliedAction(new PlannedAction(ActionKind.SwitchActive, Cs), Succeeded: true, Failure: null),
            new AppliedAction(new PlannedAction(ActionKind.Deactivate,   CsQwertz), Succeeded: true, Failure: null),
        };

        var report = new Reporter().FormatReport(
            Persisted(Cs, En),
            Session(CsQwertz, Cs, CsQwertz, En),
            actions,
            Outcome.Success,
            dryRun: false,
            quiet: false);

        var switchIdx = report.IndexOf("SwitchActive", StringComparison.Ordinal);
        var deactivateIdx = report.IndexOf("Deactivate", StringComparison.Ordinal);
        Assert.True(switchIdx >= 0 && deactivateIdx >= 0);
        Assert.True(switchIdx < deactivateIdx, "SwitchActive must appear before Deactivate in the report");
    }

    [Fact]
    public void Dry_run_prefixes_actions_and_result()
    {
        var actions = new[]
        {
            new AppliedAction(new PlannedAction(ActionKind.Deactivate, CsQwertz), Succeeded: true, Failure: null),
        };

        var report = new Reporter().FormatReport(
            Persisted(Cs, En),
            Session(Cs, Cs, CsQwertz, En),
            actions,
            Outcome.Success,
            dryRun: true,
            quiet: false);

        Assert.Contains("(dry-run) Deactivate", report);
        Assert.Contains("Result: DRY-RUN: would remove 1 session-only layout(s)", report);
    }

    [Fact]
    public void Quiet_omits_persisted_and_session_sections_but_keeps_actions_and_result()
    {
        var actions = new[]
        {
            new AppliedAction(new PlannedAction(ActionKind.Deactivate, CsQwertz), Succeeded: true, Failure: null),
        };

        var report = new Reporter().FormatReport(
            Persisted(Cs, En),
            Session(Cs, Cs, CsQwertz, En),
            actions,
            Outcome.Success,
            dryRun: false,
            quiet: true);

        Assert.DoesNotContain("Persisted layouts:", report);
        Assert.DoesNotContain("Session layouts:", report);
        Assert.Contains("Actions:", report);
        Assert.Contains("Result: SUCCESS", report);
    }

    [Fact]
    public void Failed_outcome_includes_failing_action_in_result_line()
    {
        var actions = new[]
        {
            new AppliedAction(
                new PlannedAction(ActionKind.Deactivate, CsQwertz),
                Succeeded: false,
                Failure: "COMException 0x80004005: simulated"),
        };

        var report = new Reporter().FormatReport(
            Persisted(Cs, En),
            Session(Cs, Cs, CsQwertz, En),
            actions,
            Outcome.Failed,
            dryRun: false,
            quiet: false);

        Assert.Contains("FAILED — COMException", report);
        Assert.Contains("Result: FAILED at Deactivate 0405 00010405", report);
    }
}

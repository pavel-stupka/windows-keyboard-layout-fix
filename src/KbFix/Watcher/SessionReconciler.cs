using System.Runtime.Versioning;
using KbFix.Domain;
using KbFix.Platform;

namespace KbFix.Watcher;

/// <summary>
/// Concrete <see cref="ISessionReconciler"/> that composes the existing
/// one-shot reconciliation pipeline: <see cref="PersistedConfigReader"/>,
/// <see cref="SessionLayoutGateway"/>, and <see cref="ReconciliationPlan"/>.
/// Reusable by both the one-shot CLI and the background watcher loop.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SessionReconciler : ISessionReconciler
{
    private SessionLayoutGateway? _gateway;

    public SessionReconciler()
    {
        _gateway = new SessionLayoutGateway();
    }

    public ReconcileResult ReconcileOnce()
    {
        var gateway = _gateway;
        if (gateway is null)
        {
            return new ReconcileResult(ReconcileOutcome.Failed, 0, "disposed");
        }

        IReadOnlyList<(ushort LangId, string Klid)> rawPersisted;
        try
        {
            rawPersisted = PersistedConfigReader.ReadRaw();
        }
        catch (Exception ex)
        {
            return new ReconcileResult(ReconcileOutcome.ConfigReadFailed, 0, ex.Message);
        }

        try
        {
            var resolved = gateway.ResolvePersisted(rawPersisted);
            var persisted = new PersistedConfig(resolved.Hkls, DateTime.UtcNow);
            var session = gateway.ReadSession();
            var plan = ReconciliationPlan.Build(persisted, session);

            if (plan.Refused)
            {
                return new ReconcileResult(ReconcileOutcome.Refused, 0, plan.RefuseReason);
            }

            if (plan.NoOp)
            {
                return new ReconcileResult(ReconcileOutcome.NoOp, 0, null);
            }

            var appliedCount = 0;
            foreach (var action in plan.Actions)
            {
                var applied = gateway.Apply(action);
                if (!applied.Succeeded)
                {
                    return new ReconcileResult(
                        ReconcileOutcome.Failed,
                        appliedCount,
                        applied.Failure ?? "action failed");
                }
                appliedCount++;
            }

            if (!gateway.VerifyConverged(resolved.Hkls))
            {
                return new ReconcileResult(
                    ReconcileOutcome.Failed,
                    appliedCount,
                    "session did not converge to persisted state");
            }

            return new ReconcileResult(ReconcileOutcome.Applied, appliedCount, null);
        }
        catch (Exception ex)
        {
            return new ReconcileResult(ReconcileOutcome.Failed, 0, ex.Message);
        }
    }

    public void Dispose()
    {
        _gateway?.Dispose();
        _gateway = null;
    }
}

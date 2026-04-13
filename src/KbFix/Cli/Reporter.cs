using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using KbFix.Domain;
using Microsoft.Win32;

namespace KbFix.Cli;

[SupportedOSPlatform("windows")]
internal sealed class Reporter
{
    public string FormatReport(
        PersistedConfig persisted,
        SessionState session,
        IReadOnlyList<AppliedAction> actions,
        Outcome outcome,
        bool dryRun,
        bool quiet,
        IReadOnlyDictionary<LayoutId, string>? displayKlids = null,
        IReadOnlyList<(ushort LangId, string Klid)>? userFacingPersisted = null,
        string? errorDetail = null)
    {
        var sb = new StringBuilder();
        displayKlids ??= EmptyDisplay;

        if (!quiet)
        {
            sb.AppendLine("Persisted layouts:");
            foreach (var (langId, klid) in SortedUserFacing(persisted, userFacingPersisted))
            {
                sb.Append("  - ").AppendLine(FormatUserFacingLine(langId, klid));
            }
            sb.AppendLine();

            sb.AppendLine("Session layouts:");
            foreach (var id in session.Layouts.Sorted())
            {
                var marker = persisted.Layouts.Contains(id) ? "" : "    <-- session-only";
                sb.Append("  - ").Append(FormatLayoutLine(id, displayKlids)).AppendLine(marker);
            }
            sb.AppendLine();
        }

        sb.AppendLine("Actions:");
        if (actions.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var action in actions)
            {
                var prefix = dryRun ? "(dry-run) " : "";
                var status = action.Succeeded ? "OK" : $"FAILED — {action.Failure}";
                sb.Append("  - ")
                  .Append(prefix)
                  .Append(action.Planned.Kind == ActionKind.SwitchActive ? "SwitchActive " : "Deactivate   ")
                  .Append(action.Planned.LayoutId.ToString())
                  .Append(": ")
                  .AppendLine(status);
            }
        }
        sb.AppendLine();

        // Final result line.
        var failedAction = FindFirstFailure(actions);
        sb.Append("Result: ").Append(FormatResult(outcome, dryRun, actions, failedAction, errorDetail));
        sb.AppendLine();

        return sb.ToString();
    }

    public string FormatRefusal(
        PersistedConfig persisted,
        SessionState? session,
        string reason,
        bool quiet,
        IReadOnlyDictionary<LayoutId, string>? displayKlids = null,
        IReadOnlyList<(ushort LangId, string Klid)>? userFacingPersisted = null)
    {
        var sb = new StringBuilder();
        displayKlids ??= EmptyDisplay;
        if (!quiet)
        {
            sb.AppendLine("Persisted layouts:");
            var ufp = SortedUserFacing(persisted, userFacingPersisted);
            if (ufp.Count == 0)
            {
                sb.AppendLine("  (none — empty persisted set)");
            }
            else
            {
                foreach (var (langId, klid) in ufp)
                {
                    sb.Append("  - ").AppendLine(FormatUserFacingLine(langId, klid));
                }
            }
            sb.AppendLine();

            sb.AppendLine("Session layouts:");
            if (session is null)
            {
                sb.AppendLine("  (not read)");
            }
            else
            {
                foreach (var id in session.Layouts.Sorted())
                {
                    sb.Append("  - ").AppendLine(FormatLayoutLine(id, displayKlids));
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("Actions:");
        sb.AppendLine("  (none)");
        sb.AppendLine();
        sb.Append("Result: REFUSED — ").AppendLine(reason);
        return sb.ToString();
    }

    private static string FormatResult(
        Outcome outcome,
        bool dryRun,
        IReadOnlyList<AppliedAction> actions,
        AppliedAction? failed,
        string? errorDetail)
    {
        if (outcome == Outcome.Failed)
        {
            if (failed is not null)
            {
                return $"FAILED at {failed.Planned.Kind} {failed.Planned.LayoutId}: {failed.Failure}";
            }
            return errorDetail is null ? "FAILED" : $"FAILED — {errorDetail}";
        }

        var removed = actions.Count(a => a.Planned.Kind == ActionKind.Deactivate);

        if (dryRun)
        {
            if (outcome == Outcome.NoOp || removed == 0)
            {
                return "DRY-RUN: no changes needed.";
            }
            return $"DRY-RUN: would remove {removed} session-only layout(s); persisted set unchanged.";
        }

        if (outcome == Outcome.NoOp)
        {
            return "NO-OP — session already matches persisted set.";
        }

        return $"SUCCESS — removed {removed} session-only layout(s); persisted set unchanged.";
    }

    private static readonly IReadOnlyDictionary<LayoutId, string> EmptyDisplay
        = new Dictionary<LayoutId, string>();

    private string FormatLayoutLine(LayoutId id, IReadOnlyDictionary<LayoutId, string> displayKlids)
    {
        // The Klid field stores an HKL hex value in the new direct-HKL flow.
        // If we have a display KLID for this LayoutId, use it for the human-
        // readable column AND for the Layout Text registry lookup.
        var displayKlid = displayKlids.TryGetValue(id, out var k) ? k : id.Klid;
        var name = ResolveLayoutName(displayKlid);
        return string.Create(CultureInfo.InvariantCulture,
            $"{id.LangId:X4}  {displayKlid}  {name}");
    }

    private string FormatUserFacingLine(ushort langId, string klid)
    {
        var name = ResolveLayoutName(klid);
        return string.Create(CultureInfo.InvariantCulture,
            $"{langId:X4}  {klid}  {name}");
    }

    /// <summary>
    /// Returns the user-facing list of persisted layouts (one entry per User
    /// Profile registry value), de-duplicated and sorted. Falls back to
    /// rendering the internal HKL set if no user-facing list was supplied.
    /// </summary>
    private static IReadOnlyList<(ushort LangId, string Klid)> SortedUserFacing(
        PersistedConfig persisted,
        IReadOnlyList<(ushort LangId, string Klid)>? userFacing)
    {
        if (userFacing is { Count: > 0 })
        {
            return userFacing
                .Distinct()
                .OrderBy(x => x.LangId)
                .ThenBy(x => x.Klid, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return persisted.Layouts.Sorted()
            .Select(x => (x.LangId, x.Klid))
            .ToArray();
    }

    private static AppliedAction? FindFirstFailure(IReadOnlyList<AppliedAction> actions)
    {
        foreach (var a in actions)
        {
            if (!a.Succeeded)
            {
                return a;
            }
        }
        return null;
    }

    /// <summary>
    /// Best-effort human-readable name lookup. Returns an empty string on any
    /// failure — the column is purely advisory.
    /// </summary>
    private string ResolveLayoutName(string klid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{klid}",
                writable: false);
            return key?.GetValue("Layout Text") as string ?? "";
        }
        catch
        {
            return "";
        }
    }
}

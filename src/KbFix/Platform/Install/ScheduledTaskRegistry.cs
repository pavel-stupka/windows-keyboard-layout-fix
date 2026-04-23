using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using KbFix.Watcher;

namespace KbFix.Platform.Install;

/// <summary>
/// Per-user Scheduled-Task management for the KbFix watcher at
/// <see cref="WatcherInstallation.ScheduledTaskName"/>. Wraps
/// <c>schtasks.exe</c> with a narrow, idempotent surface.
///
/// No elevation required — the task registers under the user's own
/// namespace (<c>\KbFix\KbFixWatcher</c>) with
/// <see cref="TaskRunLevel.LeastPrivilege"/> and
/// <see cref="TaskLogonType.InteractiveToken"/>. See
/// <c>specs/004-watcher-resilience/research.md</c> §R2 / §R3.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ScheduledTaskRegistry
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Returns the SID of the current interactive user. Throws if unavailable (should not happen on a logged-on desktop).</summary>
    public static string CurrentUserSid()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Could not determine the current user's SID.");
        return sid;
    }

    /// <summary>
    /// Builds the full Task Scheduler XML for the watcher task. Pure — no I/O.
    /// Includes the At-logon trigger, Restart-on-failure settings, and the
    /// LeastPrivilege principal per <c>research.md §R1</c>.
    /// </summary>
    public static string BuildTaskXml(string stagedBinaryPath, string userSid)
    {
        // Using a verbatim string builder rather than an XmlWriter keeps the
        // output byte-for-byte predictable, which makes unit testing the XML
        // content straightforward. The schema is the Task Scheduler 1.2 format
        // documented on MSDN.
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n");
        sb.Append("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n");
        sb.Append("  <RegistrationInfo>\r\n");
        sb.Append("    <Author>KbFix</Author>\r\n");
        sb.Append("    <Description>Background watcher for kbfix; keeps the Windows session's keyboard layouts in sync with the user's HKCU configuration.</Description>\r\n");
        sb.Append("    <URI>\\").Append(WatcherInstallation.ScheduledTaskName).Append("</URI>\r\n");
        sb.Append("  </RegistrationInfo>\r\n");
        sb.Append("  <Triggers>\r\n");
        sb.Append("    <LogonTrigger>\r\n");
        sb.Append("      <Enabled>true</Enabled>\r\n");
        sb.Append("      <UserId>").Append(userSid).Append("</UserId>\r\n");
        sb.Append("    </LogonTrigger>\r\n");
        sb.Append("  </Triggers>\r\n");
        sb.Append("  <Principals>\r\n");
        sb.Append("    <Principal id=\"Author\">\r\n");
        sb.Append("      <UserId>").Append(userSid).Append("</UserId>\r\n");
        sb.Append("      <LogonType>InteractiveToken</LogonType>\r\n");
        sb.Append("      <RunLevel>LeastPrivilege</RunLevel>\r\n");
        sb.Append("    </Principal>\r\n");
        sb.Append("  </Principals>\r\n");
        sb.Append("  <Settings>\r\n");
        sb.Append("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n");
        sb.Append("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n");
        sb.Append("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n");
        sb.Append("    <AllowHardTerminate>true</AllowHardTerminate>\r\n");
        sb.Append("    <StartWhenAvailable>true</StartWhenAvailable>\r\n");
        sb.Append("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n");
        sb.Append("    <IdleSettings>\r\n");
        sb.Append("      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n");
        sb.Append("      <RestartOnIdle>false</RestartOnIdle>\r\n");
        sb.Append("    </IdleSettings>\r\n");
        sb.Append("    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n");
        sb.Append("    <Enabled>true</Enabled>\r\n");
        sb.Append("    <Hidden>false</Hidden>\r\n");
        sb.Append("    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n");
        sb.Append("    <WakeToRun>false</WakeToRun>\r\n");
        sb.Append("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n");
        sb.Append("    <Priority>7</Priority>\r\n");
        sb.Append("    <RestartOnFailure>\r\n");
        sb.Append("      <Interval>PT1M</Interval>\r\n");
        sb.Append("      <Count>3</Count>\r\n");
        sb.Append("    </RestartOnFailure>\r\n");
        sb.Append("  </Settings>\r\n");
        sb.Append("  <Actions Context=\"Author\">\r\n");
        sb.Append("    <Exec>\r\n");
        sb.Append("      <Command>").Append(XmlEscape(stagedBinaryPath)).Append("</Command>\r\n");
        sb.Append("      <Arguments>--watch</Arguments>\r\n");
        sb.Append("    </Exec>\r\n");
        sb.Append("  </Actions>\r\n");
        sb.Append("</Task>\r\n");
        return sb.ToString();
    }

    private static string XmlEscape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");

    /// <summary>
    /// Creates or replaces the task using <paramref name="xmlPath"/> (already
    /// written to disk). Uses <c>schtasks /Create /XML &lt;path&gt; /F</c>.
    /// Throws on non-zero exit.
    /// </summary>
    public static void Create(string xmlPath)
    {
        var (exit, _, err) = RunSchtasks(
            "/Create",
            "/TN", WatcherInstallation.ScheduledTaskName,
            "/XML", xmlPath,
            "/F");
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"schtasks /Create exited {exit}: {Trim(err)}");
        }
    }

    /// <summary>
    /// Deletes the task if present. Idempotent — "task not found" is reported
    /// as success. Returns true if the task existed and was removed, false if
    /// it was absent.
    /// </summary>
    public static bool Delete()
    {
        var (exit, _, err) = RunSchtasks(
            "/Delete",
            "/TN", WatcherInstallation.ScheduledTaskName,
            "/F");
        if (exit == 0)
        {
            return true;
        }
        // schtasks returns non-zero when the task does not exist. Detect via the
        // error message being invariant across languages is unreliable, so we
        // also probe explicitly with /Query and treat both "exit+not-found" and
        // "query-says-absent" as successful idempotent deletes.
        if (!Exists())
        {
            return false;
        }
        throw new InvalidOperationException(
            $"schtasks /Delete exited {exit}: {Trim(err)}");
    }

    /// <summary>Returns true if the task is registered.</summary>
    public static bool Exists()
    {
        var (exit, _, _) = RunSchtasks(
            "/Query",
            "/TN", WatcherInstallation.ScheduledTaskName);
        return exit == 0;
    }

    /// <summary>
    /// Queries the task's current runtime state via
    /// <c>schtasks /Query /V /FO LIST</c> plus the stored XML at
    /// <see cref="WatcherInstallation.ScheduledTaskXmlPath"/> for structural
    /// fields. Returns <see cref="ScheduledTaskEntry.Absent"/> when the task
    /// is not registered.
    /// </summary>
    public static ScheduledTaskEntry Query()
    {
        if (!Exists())
        {
            return ScheduledTaskEntry.Absent;
        }

        var verbose = QueryVerbose();
        var (principal, executablePath) = ReadStoredXmlFields();

        var stagedPath = WatcherInstallation.DefaultStagedBinaryPath;
        var pointsAtStaged = executablePath is not null
            && string.Equals(
                Path.GetFullPath(executablePath),
                Path.GetFullPath(stagedPath),
                StringComparison.OrdinalIgnoreCase);

        return new ScheduledTaskEntry(
            Present: true,
            Enabled: verbose.Status != ScheduledTaskStatus.Disabled,
            Status: verbose.Status,
            LastRunTime: verbose.LastRunTime,
            LastResult: verbose.LastResult,
            NextRunTime: verbose.NextRunTime,
            Principal: principal,
            ExecutablePath: executablePath,
            PointsAtStaged: pointsAtStaged);
    }

    /// <summary>Raw stored task XML (our own export), or null if absent.</summary>
    public static string? QueryXml()
    {
        try
        {
            var path = WatcherInstallation.ScheduledTaskXmlPath;
            if (!File.Exists(path))
            {
                return null;
            }
            return File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }

    private sealed record VerboseFields(
        ScheduledTaskStatus Status,
        DateTimeOffset? LastRunTime,
        int? LastResult,
        DateTimeOffset? NextRunTime);

    private static VerboseFields QueryVerbose()
    {
        var (_, stdout, _) = RunSchtasks(
            "/Query",
            "/TN", WatcherInstallation.ScheduledTaskName,
            "/V",
            "/FO", "LIST");

        ScheduledTaskStatus status = ScheduledTaskStatus.Unknown;
        DateTimeOffset? lastRun = null;
        int? lastResult = null;
        DateTimeOffset? nextRun = null;

        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var idx = trimmed.IndexOf(':');
            if (idx < 0) continue;
            var key = trimmed.Substring(0, idx).Trim();
            var value = trimmed.Substring(idx + 1).Trim();

            // Key matching is English-locale; schtasks output is localised in
            // some Windows builds, so we also match the Czech variant seen in
            // this project's dev environment and fall back silently when a key
            // is not recognised.
            if (Matches(key, "Status", "Stav"))
            {
                status = ParseStatus(value);
            }
            else if (Matches(key, "Last Run Time", "Čas posledního spuštění"))
            {
                lastRun = ParseDate(value);
            }
            else if (Matches(key, "Last Result", "Poslední výsledek"))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
                {
                    lastResult = r;
                }
            }
            else if (Matches(key, "Next Run Time", "Čas dalšího spuštění"))
            {
                nextRun = ParseDate(value);
            }
        }

        return new VerboseFields(status, lastRun, lastResult, nextRun);
    }

    private static bool Matches(string actual, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.Equals(actual, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static ScheduledTaskStatus ParseStatus(string v)
    {
        // Canonical English strings. Czech variants ("Připraveno", "Spuštěno",
        // "Zakázáno") are covered too.
        if (v.Contains("Running", StringComparison.OrdinalIgnoreCase)
            || v.Contains("Spuštěno", StringComparison.OrdinalIgnoreCase))
        {
            return ScheduledTaskStatus.Running;
        }
        if (v.Contains("Disabled", StringComparison.OrdinalIgnoreCase)
            || v.Contains("Zakázán", StringComparison.OrdinalIgnoreCase))
        {
            return ScheduledTaskStatus.Disabled;
        }
        if (v.Contains("Ready", StringComparison.OrdinalIgnoreCase)
            || v.Contains("Připraveno", StringComparison.OrdinalIgnoreCase))
        {
            return ScheduledTaskStatus.Ready;
        }
        return ScheduledTaskStatus.Unknown;
    }

    private static DateTimeOffset? ParseDate(string v)
    {
        if (string.IsNullOrWhiteSpace(v)
            || v.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            || v.Equals("Nikdy", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
        {
            return dto;
        }
        if (DateTimeOffset.TryParse(v, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto))
        {
            return dto;
        }
        return null;
    }

    private static (string? Principal, string? ExecutablePath) ReadStoredXmlFields()
    {
        try
        {
            var path = WatcherInstallation.ScheduledTaskXmlPath;
            if (!File.Exists(path))
            {
                return (null, null);
            }
            var xml = File.ReadAllText(path);
            // Tiny hand-parse — avoids bringing in XmlReader for two fields.
            var principal = ExtractBetween(xml, "<Principal id=\"Author\">", "</Principal>");
            var principalSid = ExtractBetween(principal ?? "", "<UserId>", "</UserId>");
            var command = ExtractBetween(xml, "<Command>", "</Command>");
            return (principalSid, command);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? ExtractBetween(string s, string start, string end)
    {
        var i = s.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return null;
        var j = s.IndexOf(end, i + start.Length, StringComparison.Ordinal);
        if (j < 0) return null;
        return s.Substring(i + start.Length, j - i - start.Length).Trim();
    }

    private static (int ExitCode, string Stdout, string Stderr) RunSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // OEM console codepage so localised schtasks output decodes correctly.
            StandardOutputEncoding = Console.OutputEncoding,
            StandardErrorEncoding = Console.OutputEncoding,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start schtasks.exe.");

        if (!proc.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"schtasks.exe timed out after {ProcessTimeout.TotalSeconds} s.");
        }

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        return (proc.ExitCode, stdout, stderr);
    }

    private static string Trim(string s)
    {
        var first = s.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return first.Length > 160 ? first.Substring(0, 160) : first;
    }
}

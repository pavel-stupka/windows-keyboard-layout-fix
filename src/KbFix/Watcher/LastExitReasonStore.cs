using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KbFix.Watcher;

/// <summary>
/// The single-record JSON document at <c>%LOCALAPPDATA%\KbFix\last-exit.json</c>.
/// Documented in <c>specs/004-watcher-resilience/data-model.md §1</c>. Field
/// names match the JSON property names and are part of the external contract.
/// </summary>
internal sealed record LastExitReason(
    string Reason,
    int ExitCode,
    string TimestampUtc,
    int Pid,
    string? Detail);

/// <summary>
/// Pure-ish reader/writer for <see cref="LastExitReason"/>. The only I/O
/// surface is a single file (default path
/// <see cref="WatcherInstallation.LastExitFilePath"/>); callers pass an
/// override for testability. Writes are atomic via write-to-tmp + rename.
/// </summary>
internal static class LastExitReasonStore
{
    private const int MaxDetailBytes = 200;

    /// <summary>Reads the record at <paramref name="path"/>. Returns null on any error or validation failure — never throws.</summary>
    public static LastExitReason? Read(string? path = null)
    {
        var effective = path ?? WatcherInstallation.LastExitFilePath;
        try
        {
            if (!File.Exists(effective))
            {
                return null;
            }
            var json = File.ReadAllText(effective);
            var record = JsonSerializer.Deserialize(json, LastExitReasonJsonContext.Default.LastExitReason);
            if (record is null || !IsValid(record))
            {
                return null;
            }
            return record;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="record"/> atomically. On failure, never throws —
    /// logging is the caller's responsibility, and the file surface of last
    /// resort is decorative diagnostic, not functional.
    /// </summary>
    public static bool Write(LastExitReason record, string? path = null)
    {
        var effective = path ?? WatcherInstallation.LastExitFilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(effective)!);
            var sanitized = Sanitize(record);
            var json = JsonSerializer.Serialize(sanitized, LastExitReasonJsonContext.Default.LastExitReason);
            var tmp = effective + ".tmp";
            File.WriteAllText(tmp, json);
            // File.Move with overwrite is atomic on a single volume on Windows.
            File.Move(tmp, effective, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Clips <see cref="LastExitReason.Detail"/> to <c>MaxDetailBytes</c> UTF-8 bytes; passes all other fields through unchanged. Truncation is by whole codepoint so invalid UTF-8 sequences cannot be produced.</summary>
    public static LastExitReason Sanitize(LastExitReason record)
    {
        var detail = record.Detail;
        if (detail is not null && Encoding.UTF8.GetByteCount(detail) > MaxDetailBytes)
        {
            var sb = new StringBuilder();
            var totalBytes = 0;
            Span<byte> buf = stackalloc byte[4];
            foreach (var rune in detail.EnumerateRunes())
            {
                var n = rune.EncodeToUtf8(buf);
                if (totalBytes + n > MaxDetailBytes)
                {
                    break;
                }
                sb.Append(rune.ToString());
                totalBytes += n;
            }
            detail = sb.ToString();
        }
        return record with { Detail = detail };
    }

    private static bool IsValid(LastExitReason record)
    {
        if (!IsKnownReason(record.Reason))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(record.TimestampUtc))
        {
            return false;
        }
        // Cooperative is the only zero-exit; every other reason is a failure.
        var isCooperative = string.Equals(record.Reason, "CooperativeShutdown", StringComparison.Ordinal);
        if (isCooperative && record.ExitCode != 0)
        {
            return false;
        }
        if (!isCooperative && record.ExitCode == 0)
        {
            return false;
        }
        if (record.Detail is not null && Encoding.UTF8.GetByteCount(record.Detail) > MaxDetailBytes)
        {
            return false;
        }
        return true;
    }

    private static bool IsKnownReason(string? reason) => reason switch
    {
        "CooperativeShutdown" => true,
        "ConfigUnrecoverable" => true,
        "CrashedUnhandled" => true,
        "StartupFailed" => true,
        "SupervisorObservedDead" => true,
        _ => false,
    };

    /// <summary>Renders the JSON for display (<c>--status --verbose</c>).</summary>
    public static string ToPrettyJson(LastExitReason record)
    {
        return JsonSerializer.Serialize(record, LastExitReasonJsonContext.IndentedDefault.LastExitReason);
    }
}

/// <summary>Source-generated JSON context to keep trimming viable (no reflection).</summary>
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(LastExitReason))]
internal partial class LastExitReasonJsonContext : JsonSerializerContext
{
    private static LastExitReasonJsonContext? s_indented;
    public static LastExitReasonJsonContext IndentedDefault =>
        s_indented ??= new LastExitReasonJsonContext(new JsonSerializerOptions
        {
            TypeInfoResolver = Default,
            WriteIndented = true,
        });
}

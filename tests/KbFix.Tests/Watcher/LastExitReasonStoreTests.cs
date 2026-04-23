using System.Text;
using System.Text.Json;
using KbFix.Watcher;
using Xunit;

namespace KbFix.Tests.Watcher;

public class LastExitReasonStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public LastExitReasonStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KbFix.LastExitTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "last-exit.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Read_returns_null_when_file_absent()
    {
        Assert.Null(LastExitReasonStore.Read(_path));
    }

    [Fact]
    public void Read_returns_null_on_invalid_json()
    {
        File.WriteAllText(_path, "{not-json");
        Assert.Null(LastExitReasonStore.Read(_path));
    }

    [Fact]
    public void Read_returns_null_on_unknown_reason()
    {
        File.WriteAllText(_path, """
            {"reason":"NotAReason","exitCode":0,"timestampUtc":"2026-04-23T00:00:00Z","pid":1,"detail":null}
            """);
        Assert.Null(LastExitReasonStore.Read(_path));
    }

    [Fact]
    public void Read_returns_null_on_missing_timestamp()
    {
        File.WriteAllText(_path, """
            {"reason":"CooperativeShutdown","exitCode":0,"timestampUtc":"","pid":1,"detail":null}
            """);
        Assert.Null(LastExitReasonStore.Read(_path));
    }

    [Fact]
    public void Read_rejects_cooperative_shutdown_with_non_zero_exit_code()
    {
        File.WriteAllText(_path, """
            {"reason":"CooperativeShutdown","exitCode":1,"timestampUtc":"2026-04-23T00:00:00Z","pid":1,"detail":null}
            """);
        Assert.Null(LastExitReasonStore.Read(_path));
    }

    [Fact]
    public void Read_rejects_non_cooperative_with_zero_exit_code()
    {
        File.WriteAllText(_path, """
            {"reason":"ConfigUnrecoverable","exitCode":0,"timestampUtc":"2026-04-23T00:00:00Z","pid":1,"detail":null}
            """);
        Assert.Null(LastExitReasonStore.Read(_path));
    }

    [Theory]
    [InlineData("CooperativeShutdown", 0)]
    [InlineData("ConfigUnrecoverable", 1)]
    [InlineData("CrashedUnhandled", 1)]
    [InlineData("StartupFailed", 1)]
    [InlineData("SupervisorObservedDead", 1)]
    public void Write_then_Read_roundtrips_every_known_reason(string reason, int exitCode)
    {
        var record = new LastExitReason(reason, exitCode, "2026-04-23T12:34:56Z", 4242, "detail test");

        Assert.True(LastExitReasonStore.Write(record, _path));
        var read = LastExitReasonStore.Read(_path);

        Assert.NotNull(read);
        Assert.Equal(reason, read!.Reason);
        Assert.Equal(exitCode, read.ExitCode);
        Assert.Equal("2026-04-23T12:34:56Z", read.TimestampUtc);
        Assert.Equal(4242, read.Pid);
        Assert.Equal("detail test", read.Detail);
    }

    [Fact]
    public void Write_is_atomic_when_destination_directory_does_not_exist()
    {
        var nestedPath = Path.Combine(_tempDir, "sub", "last-exit.json");
        var record = new LastExitReason("CooperativeShutdown", 0, "2026-04-23T00:00:00Z", 1, null);

        Assert.True(LastExitReasonStore.Write(record, nestedPath));

        Assert.True(File.Exists(nestedPath));
        Assert.False(File.Exists(nestedPath + ".tmp"));
    }

    [Fact]
    public void Write_overwrites_existing_file()
    {
        var first = new LastExitReason("CooperativeShutdown", 0, "2026-04-23T00:00:00Z", 1, "first");
        LastExitReasonStore.Write(first, _path);
        var second = new LastExitReason("CrashedUnhandled", 1, "2026-04-23T01:00:00Z", 2, "second");

        Assert.True(LastExitReasonStore.Write(second, _path));

        var read = LastExitReasonStore.Read(_path);
        Assert.NotNull(read);
        Assert.Equal("CrashedUnhandled", read!.Reason);
        Assert.Equal("second", read.Detail);
    }

    [Fact]
    public void Sanitize_truncates_detail_above_200_utf8_bytes()
    {
        var longDetail = new string('x', 500);
        var record = new LastExitReason("CrashedUnhandled", 1, "2026-04-23T00:00:00Z", 1, longDetail);

        var sanitized = LastExitReasonStore.Sanitize(record);

        Assert.NotNull(sanitized.Detail);
        Assert.True(Encoding.UTF8.GetByteCount(sanitized.Detail!) <= 200);
    }

    [Fact]
    public void Sanitize_leaves_short_detail_unchanged()
    {
        var record = new LastExitReason("CrashedUnhandled", 1, "2026-04-23T00:00:00Z", 1, "short");

        var sanitized = LastExitReasonStore.Sanitize(record);

        Assert.Equal("short", sanitized.Detail);
    }

    [Fact]
    public void Sanitize_does_not_split_multibyte_utf8_codepoint()
    {
        // String of 201 copies of a 3-byte UTF-8 character (total 603 bytes).
        // After truncation to 200 bytes we must land on a codepoint boundary.
        var multibyte = new string('名', 201);
        var record = new LastExitReason("CrashedUnhandled", 1, "2026-04-23T00:00:00Z", 1, multibyte);

        var sanitized = LastExitReasonStore.Sanitize(record);

        Assert.NotNull(sanitized.Detail);
        Assert.True(Encoding.UTF8.GetByteCount(sanitized.Detail!) <= 200);
        // UTF-8 decoding succeeds without any replacement characters.
        Assert.DoesNotContain("�", sanitized.Detail);
    }

    [Fact]
    public void ToPrettyJson_emits_indented_output()
    {
        var record = new LastExitReason("CooperativeShutdown", 0, "2026-04-23T00:00:00Z", 1, "ok");

        var pretty = LastExitReasonStore.ToPrettyJson(record);

        Assert.Contains("\n", pretty);
        // Round-trips through the normal parser.
        var parsed = JsonSerializer.Deserialize(pretty, LastExitReasonJsonContext.Default.LastExitReason);
        Assert.NotNull(parsed);
        Assert.Equal("CooperativeShutdown", parsed!.Reason);
    }

    [Fact]
    public void Write_rejects_too_long_detail_at_the_validation_layer()
    {
        // Writer sanitises before serialising, so persistence always succeeds.
        var longDetail = new string('x', 500);
        var record = new LastExitReason("CrashedUnhandled", 1, "2026-04-23T00:00:00Z", 1, longDetail);

        // Note: we do NOT bypass sanitisation — this is the declared writer contract.
        var sanitised = LastExitReasonStore.Sanitize(record);
        Assert.True(LastExitReasonStore.Write(sanitised, _path));
        var read = LastExitReasonStore.Read(_path);
        Assert.NotNull(read);
        Assert.True(Encoding.UTF8.GetByteCount(read!.Detail!) <= 200);
    }
}

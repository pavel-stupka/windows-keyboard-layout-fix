using System.Globalization;

namespace KbFix.Watcher;

internal interface IWatcherLog
{
    void Start();
    void Stop();
    void ReconcileNoOp();
    void ReconcileApplied(int count);
    void ReconcileFailed(string reason);
    void FlapBackoff();
    void ConfigReadFailed(string reason);
    void SessionEmptyRefused();

    // --- 004-watcher-resilience additions ---

    /// <summary>Logged at every watcher process startup — includes the previous run's exit reason when <c>last-exit.json</c> was readable.</summary>
    void ProcessStartup(string previousReason);

    /// <summary>Logged when the current startup detects the previous watcher was killed externally (no clean exit record).</summary>
    void SupervisorObservedDead(int previousPid);
}

/// <summary>
/// Log verbosity levels. <see cref="Info"/> is the default and records only
/// meaningful events (start, stop, applied reconciliations, failures, refusals,
/// flap backoff). <see cref="Debug"/> additionally records every poll-cycle
/// no-op, which is chatty and is only useful while troubleshooting.
/// Enabled via the <c>KBFIX_DEBUG=1</c> environment variable.
/// </summary>
internal enum WatcherLogLevel
{
    Info = 0,
    Debug = 1,
}

/// <summary>
/// Size-bounded text log for the background watcher. Writes ISO-8601 UTC
/// timestamps to <see cref="WatcherInstallation.LogFilePath"/>, rotates once
/// to <see cref="WatcherInstallation.LogFileRotatedPath"/> when the file
/// exceeds <see cref="_rotateBytes"/> (default 64 KB), and never throws from
/// a logging method — a failing log must not crash the watcher.
/// </summary>
internal sealed class WatcherLog : IWatcherLog
{
    private readonly string _path;
    private readonly string _rotatedPath;
    private readonly long _rotateBytes;
    private readonly WatcherLogLevel _level;
    private readonly object _lock = new();

    public WatcherLog(
        string path,
        string rotatedPath,
        WatcherLogLevel level = WatcherLogLevel.Info,
        long rotateBytes = 64 * 1024)
    {
        _path = path;
        _rotatedPath = rotatedPath;
        _level = level;
        _rotateBytes = rotateBytes;
    }

    public void Start() => Write("INFO", "start");
    public void Stop() => Write("INFO", "stop");

    public void ReconcileNoOp()
    {
        // No-ops happen on every quiet poll cycle. At INFO level this would
        // grow the log by tens of lines per minute for no useful signal.
        // Only record them when the user has explicitly asked for debug output.
        if (_level < WatcherLogLevel.Debug)
        {
            return;
        }
        Write("DEBUG", "reconcile-noop");
    }

    public void ReconcileApplied(int count) => Write("INFO", $"reconcile-applied count={count}");
    public void ReconcileFailed(string reason) => Write("WARN", $"reconcile-failed reason={Sanitize(reason)}");
    public void FlapBackoff() => Write("WARN", "flap-backoff");
    public void ConfigReadFailed(string reason) => Write("ERROR", $"config-read-failed reason={Sanitize(reason)}");
    public void SessionEmptyRefused() => Write("WARN", "session-empty-refused");
    public void ProcessStartup(string previousReason) => Write("INFO", $"process-startup previous-exit={Sanitize(previousReason)}");
    public void SupervisorObservedDead(int previousPid) => Write("WARN", $"supervisor-observed-dead previous-pid={previousPid}");

    private void Write(string level, string message)
    {
        lock (_lock)
        {
            try
            {
                var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                var line = $"{ts} {level} {message}{Environment.NewLine}";
                RotateIfNeeded();
                File.AppendAllText(_path, line);
            }
            catch
            {
                // Swallow — logging failure must never crash the watcher.
            }
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var info = new FileInfo(_path);
            if (info.Length < _rotateBytes)
            {
                return;
            }

            if (File.Exists(_rotatedPath))
            {
                File.Delete(_rotatedPath);
            }
            File.Move(_path, _rotatedPath);
        }
        catch
        {
            // Rotation is best-effort.
        }
    }

    private static string Sanitize(string s)
    {
        return s.Replace('\n', ' ').Replace('\r', ' ');
    }
}

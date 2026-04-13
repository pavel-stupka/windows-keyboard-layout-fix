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
    private readonly object _lock = new();

    public WatcherLog(string path, string rotatedPath, long rotateBytes = 64 * 1024)
    {
        _path = path;
        _rotatedPath = rotatedPath;
        _rotateBytes = rotateBytes;
    }

    public void Start() => Write("INFO", "start");
    public void Stop() => Write("INFO", "stop");
    public void ReconcileNoOp() => Write("DEBUG", "reconcile-noop");
    public void ReconcileApplied(int count) => Write("INFO", $"reconcile-applied count={count}");
    public void ReconcileFailed(string reason) => Write("WARN", $"reconcile-failed reason={Sanitize(reason)}");
    public void FlapBackoff() => Write("WARN", "flap-backoff");
    public void ConfigReadFailed(string reason) => Write("ERROR", $"config-read-failed reason={Sanitize(reason)}");
    public void SessionEmptyRefused() => Write("WARN", "session-empty-refused");

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

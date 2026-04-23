using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Threading;
using KbFix.Diagnostics;
using KbFix.Platform;

namespace KbFix.Watcher;

/// <summary>
/// Entry point for the <c>--watch</c> mode. Acquires the per-session
/// single-instance mutex, writes the PID file, constructs the reconciler
/// and loop, and runs until the stop event is signaled or the configuration
/// becomes unrecoverable.
///
/// Feature 004 additions:
/// - Reads <c>last-exit.json</c> before starting and logs the previous exit
///   reason on the new run's first log line.
/// - Installs an <see cref="AppDomain.UnhandledException"/> handler that
///   persists <c>CrashedUnhandled</c> to <c>last-exit.json</c> before the
///   runtime tears the process down.
/// - On every controllable exit path (cooperative shutdown,
///   ConfigUnrecoverable, startup failure) persists the corresponding
///   <see cref="LastExitReason"/>.
/// - When the prior record's PID is dead (external-kill path) writes a new
///   <c>SupervisorObservedDead</c> record from the fresh watcher.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WatcherMain
{
    public static int Run()
    {
        // Defensively detach from any inherited console.
        try { Win32Interop.FreeConsole(); } catch { /* ignore */ }

        // 004: read previous exit record BEFORE anything, so we can log it.
        var previousExit = LastExitReasonStore.Read();
        TryDetectSupervisorObservedDead(previousExit);

        // 004: install a last-ditch unhandled-exception handler. If anything
        // below escapes the main thread's exception handling, write the
        // crash reason before the CLR terminates the process.
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        Mutex? mutex = null;
        var mutexOwned = false;
        try
        {
            mutex = new Mutex(initiallyOwned: true, WatcherInstallation.InstanceMutexName, out var createdNew);
            mutexOwned = createdNew;

            if (!createdNew)
            {
                // Another watcher instance already holds it — exit cleanly.
                TryLog(log => log.ReconcileFailed("already-running"));
                return ExitCodes.Success;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous owner crashed without releasing. We now own it.
            mutexOwned = true;
        }
        catch (Exception ex)
        {
            WriteLastExitReason("StartupFailed", 1, Detail("mutex-create", ex));
            TryLog(log => log.ReconcileFailed($"mutex-create: {ex.Message}"));
            mutex?.Dispose();
            return ExitCodes.Failure;
        }

        WatcherExitReason? exitReason = null;
        try
        {
            Directory.CreateDirectory(WatcherInstallation.StagingDirectory);
            try
            {
                File.WriteAllText(
                    WatcherInstallation.PidFilePath,
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                // PID file is decorative; failing to write it is not fatal.
            }

            using var stopEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset,
                WatcherInstallation.StopEventName);

            var logLevel = Environment.GetEnvironmentVariable("KBFIX_DEBUG") == "1"
                ? WatcherLogLevel.Debug
                : WatcherLogLevel.Info;
            var log = new WatcherLog(
                WatcherInstallation.LogFilePath,
                WatcherInstallation.LogFileRotatedPath,
                logLevel);

            // 004: process-startup line always records the previous-exit reason.
            log.ProcessStartup(previousExit?.Reason ?? "none");

            var flapDetector = FlapDetector.CreateDefault();
            using var reconciler = new SessionReconciler();

            // Cooperative shutdown hooks — let Ctrl+C and process-exit signal the event.
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; try { stopEvent.Set(); } catch { } };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { stopEvent.Set(); } catch { } };

            var loop = new WatcherLoop(
                reconciler,
                flapDetector,
                log,
                clock: () => DateTimeOffset.UtcNow,
                waitForStop: ts => stopEvent.WaitOne(ts));

            var reason = loop.Run();
            exitReason = reason;
            return reason == WatcherExitReason.StopSignaled ? ExitCodes.Success : ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            WriteLastExitReason("StartupFailed", 1, Detail("watcher-main", ex));
            TryLog(log => log.ReconcileFailed($"watcher-main: {ex.Message}"));
            return ExitCodes.Failure;
        }
        finally
        {
            // 004: persist the exit reason on every controllable exit path.
            // Startup failures already wrote their own record; only the
            // loop's exit paths remain to be covered here.
            PersistControllableExitReason(exitReason);

            try { File.Delete(WatcherInstallation.PidFilePath); } catch { }
            if (mutex is not null)
            {
                if (mutexOwned)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
                mutex.Dispose();
            }
        }
    }

    // ---------- 004 helpers ----------

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Handler runs on the thread that threw, and the runtime will
        // terminate the process immediately after. Write is best-effort.
        var detail = e.ExceptionObject is Exception ex
            ? $"{ex.GetType().Name}: {FirstLine(ex.Message)}"
            : "unknown";
        WriteLastExitReason("CrashedUnhandled", 1, detail);
    }

    private static void TryDetectSupervisorObservedDead(LastExitReason? previous)
    {
        if (previous is null)
        {
            return;
        }
        if (previous.Pid == Environment.ProcessId)
        {
            // Can't happen in practice, but defensive.
            return;
        }
        if (IsProcessAlive(previous.Pid))
        {
            // Previous watcher is still alive. Nothing to report.
            return;
        }
        // Every non-cooperative prior reason already carries a meaningful
        // cause — don't overwrite CrashedUnhandled with a less-informative
        // SupervisorObservedDead.
        if (previous.Reason is "CrashedUnhandled" or "ConfigUnrecoverable" or "StartupFailed")
        {
            TryLog(log => log.SupervisorObservedDead(previous.Pid));
            return;
        }
        // Cooperative prior shutdown + we're now starting = normal autostart.
        // No supervisor signal to record.
        if (previous.Reason == "CooperativeShutdown")
        {
            return;
        }
        // Unknown prior reason — treat as dead.
        WriteLastExitReason(
            "SupervisorObservedDead",
            1,
            $"previous-pid={previous.Pid} previous-reason={previous.Reason}");
        TryLog(log => log.SupervisorObservedDead(previous.Pid));
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void PersistControllableExitReason(WatcherExitReason? reason)
    {
        if (reason is null)
        {
            return;
        }
        var (jsonReason, code) = reason.Value switch
        {
            WatcherExitReason.StopSignaled => ("CooperativeShutdown", 0),
            WatcherExitReason.ConfigUnrecoverable => ("ConfigUnrecoverable", 1),
            _ => (null, 1),
        };
        if (jsonReason is null)
        {
            return;
        }
        WriteLastExitReason(jsonReason, code, null);
    }

    private static void WriteLastExitReason(string reason, int exitCode, string? detail)
    {
        try
        {
            var record = new LastExitReason(
                Reason: reason,
                ExitCode: exitCode,
                TimestampUtc: DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Pid: Environment.ProcessId,
                Detail: detail);
            LastExitReasonStore.Write(LastExitReasonStore.Sanitize(record));
        }
        catch
        {
            // Never let logging-of-last-resort crash the process.
        }
    }

    private static string Detail(string where, Exception ex) =>
        $"{where}: {ex.GetType().Name}: {FirstLine(ex.Message)}";

    private static string FirstLine(string s) =>
        s.Split('\n').FirstOrDefault()?.Trim() ?? "";

    private static void TryLog(Action<IWatcherLog> action)
    {
        try
        {
            Directory.CreateDirectory(WatcherInstallation.StagingDirectory);
            var log = new WatcherLog(WatcherInstallation.LogFilePath, WatcherInstallation.LogFileRotatedPath);
            action(log);
        }
        catch
        {
            // Never throw from the log path of last resort.
        }
    }
}

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
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WatcherMain
{
    public static int Run()
    {
        // Defensively detach from any inherited console.
        try { Win32Interop.FreeConsole(); } catch { /* ignore */ }

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
            TryLog(log => log.ReconcileFailed($"mutex-create: {ex.Message}"));
            mutex?.Dispose();
            return ExitCodes.Failure;
        }

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

            var log = new WatcherLog(WatcherInstallation.LogFilePath, WatcherInstallation.LogFileRotatedPath);
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
            return reason == WatcherExitReason.StopSignaled ? ExitCodes.Success : ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            TryLog(log => log.ReconcileFailed($"watcher-main: {ex.Message}"));
            return ExitCodes.Failure;
        }
        finally
        {
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

using System.Collections.Generic;
using KbFix.Watcher;
using Xunit;

namespace KbFix.Tests.Watcher;

public class WatcherLoopTests
{
    private sealed class FakeReconciler : ISessionReconciler
    {
        public Queue<ReconcileResult> Script { get; } = new();
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }

        public ReconcileResult ReconcileOnce()
        {
            Calls++;
            if (Script.Count == 0)
            {
                return new ReconcileResult(ReconcileOutcome.NoOp, 0, null);
            }
            return Script.Dequeue();
        }

        public Exception? ThrowOnNextCall { get; set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class ThrowingReconciler : ISessionReconciler
    {
        public int Calls { get; private set; }
        public int ThrowFirstN { get; set; } = 1;

        public ReconcileResult ReconcileOnce()
        {
            Calls++;
            if (Calls <= ThrowFirstN)
            {
                throw new InvalidOperationException($"boom-{Calls}");
            }
            return new ReconcileResult(ReconcileOutcome.NoOp, 0, null);
        }

        public void Dispose() { }
    }

    private sealed class RecordingLog : IWatcherLog
    {
        public List<string> Events { get; } = new();
        public void Start() => Events.Add("start");
        public void Stop() => Events.Add("stop");
        public void ReconcileNoOp() => Events.Add("noop");
        public void ReconcileApplied(int count) => Events.Add($"applied:{count}");
        public void ReconcileFailed(string reason) => Events.Add($"failed:{reason}");
        public void FlapBackoff() => Events.Add("flap");
        public void ConfigReadFailed(string reason) => Events.Add($"config-fail:{reason}");
        public void SessionEmptyRefused() => Events.Add("refused");
    }

    private sealed class VirtualClock
    {
        private DateTimeOffset _now = new(2026, 4, 13, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Now() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    /// <summary>
    /// Builds a waitForStop delegate that advances a virtual clock by the
    /// requested interval and stops the loop after <paramref name="stopAfterTicks"/>
    /// waits.
    /// </summary>
    private static Func<TimeSpan, bool> MakeWait(VirtualClock clock, int stopAfterTicks)
    {
        var ticks = 0;
        return ts =>
        {
            ticks++;
            clock.Advance(ts);
            return ticks >= stopAfterTicks;
        };
    }

    private static FlapDetector DefaultFlap() =>
        new(TimeSpan.FromSeconds(60), threshold: 10, TimeSpan.FromMinutes(5));

    [Fact]
    public void Loop_applies_reconciliation_each_tick_and_logs_start_stop()
    {
        var clock = new VirtualClock();
        var reconciler = new FakeReconciler();
        reconciler.Script.Enqueue(new ReconcileResult(ReconcileOutcome.NoOp, 0, null));
        reconciler.Script.Enqueue(new ReconcileResult(ReconcileOutcome.NoOp, 0, null));

        var log = new RecordingLog();
        var loop = new WatcherLoop(reconciler, DefaultFlap(), log, clock.Now, MakeWait(clock, stopAfterTicks: 2));

        var reason = loop.Run();

        Assert.Equal(WatcherExitReason.StopSignaled, reason);
        Assert.Equal(2, reconciler.Calls);
        Assert.Equal("start", log.Events[0]);
        Assert.Equal("stop", log.Events[^1]);
        Assert.Contains("noop", log.Events);
    }

    [Fact]
    public void Consecutive_noops_stretch_the_poll_interval()
    {
        var clock = new VirtualClock();
        var reconciler = new FakeReconciler();

        // Drive many no-ops, recording the wait interval each call.
        var intervals = new List<TimeSpan>();
        var stopAfter = 20;
        var ticks = 0;
        Func<TimeSpan, bool> wait = ts =>
        {
            ticks++;
            intervals.Add(ts);
            clock.Advance(ts);
            return ticks >= stopAfter;
        };

        var loop = new WatcherLoop(reconciler, DefaultFlap(), new RecordingLog(), clock.Now, wait);
        loop.Run();

        // First few polls should be at the 2-second (fast) cadence.
        Assert.Equal(TimeSpan.FromSeconds(2), intervals[0]);
        // After >= NoOpsBeforeMid (5) consecutive noops the interval should grow.
        Assert.Contains(TimeSpan.FromSeconds(5), intervals);
        // After >= NoOpsBeforeSlow (15) consecutive noops it should grow again.
        Assert.Contains(TimeSpan.FromSeconds(10), intervals);
    }

    [Fact]
    public void Non_noop_resets_the_interval_to_fast()
    {
        var clock = new VirtualClock();
        var reconciler = new FakeReconciler();

        // Five noops, then one applied, then more noops. After the applied event,
        // the interval should snap back to the fast cadence.
        for (var i = 0; i < 6; i++)
        {
            reconciler.Script.Enqueue(new ReconcileResult(ReconcileOutcome.NoOp, 0, null));
        }
        reconciler.Script.Enqueue(new ReconcileResult(ReconcileOutcome.Applied, 1, null));
        for (var i = 0; i < 3; i++)
        {
            reconciler.Script.Enqueue(new ReconcileResult(ReconcileOutcome.NoOp, 0, null));
        }

        var intervals = new List<TimeSpan>();
        var ticks = 0;
        Func<TimeSpan, bool> wait = ts =>
        {
            ticks++;
            intervals.Add(ts);
            clock.Advance(ts);
            return ticks >= 10;
        };

        var loop = new WatcherLoop(reconciler, DefaultFlap(), new RecordingLog(), clock.Now, wait);
        loop.Run();

        // The interval immediately after the Applied call must be FastPoll (2s).
        // Applied is the 7th reconcile → 7th wait. Index 6.
        Assert.Equal(TimeSpan.FromSeconds(2), intervals[6]);
    }

    [Fact]
    public void Flap_threshold_triggers_backoff_and_loop_logs_flap_event()
    {
        var clock = new VirtualClock();
        var reconciler = new FakeReconciler();

        // 10 back-to-back applied events should trip the default FlapDetector (threshold 10).
        for (var i = 0; i < 10; i++)
        {
            reconciler.Script.Enqueue(new ReconcileResult(ReconcileOutcome.Applied, 1, null));
        }

        var log = new RecordingLog();
        var loop = new WatcherLoop(reconciler, DefaultFlap(), log, clock.Now, MakeWait(clock, stopAfterTicks: 12));
        loop.Run();

        Assert.Contains("flap", log.Events);
    }

    [Fact]
    public void Stop_signal_exits_loop_promptly()
    {
        var clock = new VirtualClock();
        var reconciler = new FakeReconciler();
        var loop = new WatcherLoop(reconciler, DefaultFlap(), new RecordingLog(), clock.Now, MakeWait(clock, stopAfterTicks: 1));
        var reason = loop.Run();
        Assert.Equal(WatcherExitReason.StopSignaled, reason);
        Assert.Equal(1, reconciler.Calls);
    }

    [Fact]
    public void Exception_in_reconciler_is_logged_and_loop_continues()
    {
        var clock = new VirtualClock();
        var reconciler = new ThrowingReconciler { ThrowFirstN = 2 };
        var log = new RecordingLog();
        var loop = new WatcherLoop(reconciler, DefaultFlap(), log, clock.Now, MakeWait(clock, stopAfterTicks: 4));

        loop.Run();

        Assert.True(reconciler.Calls >= 3, $"expected the loop to keep calling after throws, got {reconciler.Calls}");
        Assert.Contains(log.Events, e => e.StartsWith("failed:"));
    }

    [Fact]
    public void Repeated_config_read_failures_beyond_grace_period_exit_with_unrecoverable()
    {
        var clock = new VirtualClock();
        var reconciler = new FakeReconciler();

        // Script 200 config failures — the loop should exit with ConfigUnrecoverable
        // as soon as the grace period (60s) elapses against the virtual clock.
        for (var i = 0; i < 200; i++)
        {
            reconciler.Script.Enqueue(new ReconcileResult(ReconcileOutcome.ConfigReadFailed, 0, "HKCU unreadable"));
        }

        var log = new RecordingLog();
        // Do NOT stop the loop externally — we want the config grace period to end it.
        Func<TimeSpan, bool> wait = ts =>
        {
            clock.Advance(ts);
            return false;
        };

        var loop = new WatcherLoop(reconciler, DefaultFlap(), log, clock.Now, wait);
        var reason = loop.Run();

        Assert.Equal(WatcherExitReason.ConfigUnrecoverable, reason);
        Assert.Contains(log.Events, e => e.StartsWith("config-fail:"));
    }
}

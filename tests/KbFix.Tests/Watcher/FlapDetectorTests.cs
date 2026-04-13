using KbFix.Watcher;
using Xunit;

namespace KbFix.Tests.Watcher;

public class FlapDetectorTests
{
    private static FlapDetector NewDetector() => new(
        window: TimeSpan.FromSeconds(60),
        threshold: 10,
        pauseDuration: TimeSpan.FromMinutes(5));

    [Fact]
    public void Below_threshold_never_pauses()
    {
        var detector = NewDetector();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 9; i++)
        {
            detector.Record(now.AddSeconds(i));
        }

        Assert.False(detector.IsPaused(now.AddSeconds(9)));
    }

    [Fact]
    public void Exactly_threshold_within_window_triggers_pause()
    {
        var detector = NewDetector();
        var start = DateTimeOffset.UtcNow;

        for (var i = 0; i < 10; i++)
        {
            detector.Record(start.AddSeconds(i));
        }

        Assert.True(detector.IsPaused(start.AddSeconds(10)));
    }

    [Fact]
    public void Pause_expires_after_pause_duration()
    {
        var detector = NewDetector();
        var start = DateTimeOffset.UtcNow;

        for (var i = 0; i < 10; i++)
        {
            detector.Record(start.AddSeconds(i));
        }

        Assert.True(detector.IsPaused(start.AddMinutes(1)));
        Assert.False(detector.IsPaused(start.AddMinutes(6)));
    }

    [Fact]
    public void After_pause_expires_detector_accepts_fresh_events_and_can_pause_again()
    {
        var detector = NewDetector();
        var start = DateTimeOffset.UtcNow;

        for (var i = 0; i < 10; i++)
        {
            detector.Record(start.AddSeconds(i));
        }
        Assert.True(detector.IsPaused(start.AddSeconds(10)));

        var resume = start.AddMinutes(6);
        Assert.False(detector.IsPaused(resume));

        for (var i = 0; i < 9; i++)
        {
            detector.Record(resume.AddSeconds(i));
        }
        Assert.False(detector.IsPaused(resume.AddSeconds(9)));

        detector.Record(resume.AddSeconds(9));
        Assert.True(detector.IsPaused(resume.AddSeconds(10)));
    }

    [Fact]
    public void Events_older_than_window_are_evicted()
    {
        var detector = NewDetector();
        var start = DateTimeOffset.UtcNow;

        // Space 9 events across 70 seconds (wider than the 60-second window).
        for (var i = 0; i < 9; i++)
        {
            detector.Record(start.AddSeconds(i * 8));
        }

        // None of the early events should still be in the window now.
        var later = start.AddSeconds(9 * 8 + 60);
        Assert.False(detector.IsPaused(later));

        // Add 10 events in a tight burst — should now trigger a fresh pause,
        // proving evicted events don't contaminate the new window.
        for (var i = 0; i < 10; i++)
        {
            detector.Record(later.AddMilliseconds(i * 10));
        }
        Assert.True(detector.IsPaused(later.AddSeconds(1)));
    }

    [Fact]
    public void Repeated_record_at_same_timestamp_does_not_crash_and_still_counts()
    {
        var detector = NewDetector();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 10; i++)
        {
            detector.Record(now);
        }

        Assert.True(detector.IsPaused(now));
    }

    [Fact]
    public void Record_during_active_pause_is_a_noop()
    {
        var detector = NewDetector();
        var start = DateTimeOffset.UtcNow;

        for (var i = 0; i < 10; i++)
        {
            detector.Record(start.AddSeconds(i));
        }
        Assert.True(detector.IsPaused(start.AddSeconds(10)));

        // Records during pause are ignored.
        detector.Record(start.AddSeconds(11));
        Assert.Equal(0, detector.CurrentEventCount);
    }
}

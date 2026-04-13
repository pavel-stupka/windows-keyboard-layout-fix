namespace KbFix.Watcher;

/// <summary>
/// Sliding-window counter used by the watcher to avoid fighting a misbehaving
/// layout-injector. If the watcher applies more than <see cref="_threshold"/>
/// non-no-op reconciliations within <see cref="_window"/>, it enters a
/// <see cref="_pauseDuration"/> backoff and stops reconciling until the pause
/// expires.
///
/// Pure class with an injected clock via the <see cref="DateTimeOffset"/>
/// arguments on every method — no internal time source, no threading, fully
/// unit-testable.
/// </summary>
internal sealed class FlapDetector
{
    private readonly TimeSpan _window;
    private readonly int _threshold;
    private readonly TimeSpan _pauseDuration;
    private readonly Queue<DateTimeOffset> _events;
    private DateTimeOffset? _pausedUntil;

    public FlapDetector(TimeSpan window, int threshold, TimeSpan pauseDuration)
    {
        if (threshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }
        _window = window;
        _threshold = threshold;
        _pauseDuration = pauseDuration;
        _events = new Queue<DateTimeOffset>(threshold + 1);
    }

    public static FlapDetector CreateDefault() =>
        new(TimeSpan.FromSeconds(60), threshold: 10, TimeSpan.FromMinutes(5));

    /// <summary>
    /// Returns true if the detector is currently paused. If the pause window
    /// has expired, this call clears the paused state as a side effect so the
    /// detector is ready to accumulate fresh events.
    /// </summary>
    public bool IsPaused(DateTimeOffset now)
    {
        if (_pausedUntil is { } until && now >= until)
        {
            _pausedUntil = null;
        }
        return _pausedUntil is not null;
    }

    /// <summary>
    /// Records a non-no-op reconciliation event at <paramref name="now"/>.
    /// If the event is the Nth within the window (N = threshold), the
    /// detector enters a pause.
    /// </summary>
    public void Record(DateTimeOffset now)
    {
        if (IsPaused(now))
        {
            return;
        }

        while (_events.Count > 0 && now - _events.Peek() > _window)
        {
            _events.Dequeue();
        }

        _events.Enqueue(now);

        if (_events.Count >= _threshold)
        {
            _pausedUntil = now + _pauseDuration;
            _events.Clear();
        }
    }

    internal int CurrentEventCount => _events.Count;
}

namespace Rigbay.PredatorSense.Sensors;

/// <summary>
/// Calls <see cref="SensorPoller.PollOnce"/> on a timer: every <see cref="FastInterval"/> while something looks at the
/// readings, every <see cref="IdleInterval"/> otherwise. The next poll is armed after the previous one finished, so polls
/// never overlap or pile up. Exceptions are caught here; a module-started thread must never throw.
/// </summary>
public sealed class PollScheduler : IDisposable
{
    private readonly Action _poll;
    private readonly Action<Exception> _onError;
    private readonly object _lock = new();
    private Timer? _timer;
    private bool _stopped = true;
    private bool _running;
    private bool _watched = true;
    private TimeSpan? _background;
    private TimeSpan _fast;
    private TimeSpan _idle;

    /// <summary>Creates the scheduler (not started).</summary>
    public PollScheduler(SensorPoller poller, TimeSpan fastInterval, TimeSpan idleInterval, Action<Exception> onError)
        : this(() => poller.PollOnce(), fastInterval, idleInterval, onError) { }

    /// <summary>
    /// Creates a scheduler for any poll action. An <paramref name="idleInterval"/> of <see cref="Timeout.InfiniteTimeSpan"/> means "do not
    /// poll at all while nobody watches" (the operation-mode read uses it); <see cref="PollSoon"/> still polls at once.
    /// </summary>
    public PollScheduler(Action poll, TimeSpan fastInterval, TimeSpan idleInterval, Action<Exception> onError)
    {
        _poll = poll;
        _fast = fastInterval;
        _idle = idleInterval;
        _onError = onError;
    }

    /// <summary>Interval while the readings are watched (the page is open or something subscribes).</summary>
    public TimeSpan FastInterval { get { lock (_lock) return _fast; } set { lock (_lock) _fast = value; } }

    /// <summary>Interval while nobody watches.</summary>
    public TimeSpan IdleInterval { get { lock (_lock) return _idle; } set { lock (_lock) _idle = value; } }

    /// <summary>
    /// True while something looks at the readings: the module's page is visible or one of its metrics has a subscriber (the
    /// module works that out from <c>IHostServices.PageVisible</c> and <c>IMetricsBus.SubscriberCount</c>). A change applies
    /// after the poll that is armed now; call <see cref="PollSoon"/> to look at once.
    /// </summary>
    public bool Watched { get { lock (_lock) return _watched; } set { lock (_lock) _watched = value; } }

    /// <summary>
    /// Host API 0.1.4: the interval somebody asked for in the background (Watch), or null. It applies only while nobody watches, in place of
    /// the idle interval; the module works it out with <c>MetricDemand</c> (never faster than the fast interval, never slower than a finite idle one).
    /// </summary>
    public TimeSpan? BackgroundInterval { get { lock (_lock) return _background; } set { lock (_lock) _background = value; } }

    /// <summary>The interval that applies now; <see cref="Timeout.InfiniteTimeSpan"/> when nothing is polled.</summary>
    public TimeSpan CurrentInterval { get { lock (_lock) return Current(); } }

    /// <summary>True when the scheduler has an interval to poll at (watched, a background demand, or a finite idle interval).</summary>
    public bool IsPolling { get { lock (_lock) return Current() != Timeout.InfiniteTimeSpan; } }

    private TimeSpan Current() => _watched ? _fast : _background ?? _idle;

    /// <summary>Starts polling; the first poll runs after <paramref name="firstDelay"/>.</summary>
    public void Start(TimeSpan firstDelay)
    {
        lock (_lock)
        {
            _stopped = false;
            _timer?.Dispose();
            _timer = new Timer(OnTimer, null, firstDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Polls right away (somebody started to look). Does nothing while a poll is running (it re-arms with the interval that
    /// applies when it ends) or when the scheduler is stopped.
    /// </summary>
    public void PollSoon()
    {
        lock (_lock)
        {
            if (_stopped || _running) return;
            try { _timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Stops polling.A poll that is running finishes; no new one starts.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _stopped = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private void OnTimer(object? state)
    {
        lock (_lock) { if (_stopped) return; _running = true; }
        try { _poll(); }
        catch (Exception ex)
        {
            try { _onError(ex); } catch (Exception) { /* nothing left to do */ }
        }
        lock (_lock)
        {
            _running = false;
            if (_stopped) return;
            _timer?.Change(Current(), Timeout.InfiniteTimeSpan);
        }
    }
}

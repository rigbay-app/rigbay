namespace Rigbay.PredatorSense.Sensors;

/// <summary>
/// Calls <see cref="SensorPoller.PollOnce"/> on a timer: every <see cref="FastInterval"/> while something looks at the
/// readings, every <see cref="IdleInterval"/> otherwise. The next poll is armed after the previous one finished, so polls
/// never overlap or pile up. Exceptions are caught here; a module-started thread must never throw.
/// </summary>
public sealed class PollScheduler : IDisposable
{
    private readonly SensorPoller _poller;
    private readonly Action<Exception> _onError;
    private readonly object _lock = new();
    private Timer? _timer;
    private bool _stopped = true;
    private bool _running;
    private bool _watched = true;
    private TimeSpan _fast;
    private TimeSpan _idle;

    /// <summary>Creates the scheduler (not started).</summary>
    public PollScheduler(SensorPoller poller, TimeSpan fastInterval, TimeSpan idleInterval, Action<Exception> onError)
    {
        _poller = poller;
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

    /// <summary>The interval that applies now.</summary>
    public TimeSpan CurrentInterval { get { lock (_lock) return _watched ? _fast : _idle; } }

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
        try { _poller.PollOnce(); }
        catch (Exception ex)
        {
            try { _onError(ex); } catch (Exception) { /* nothing left to do */ }
        }
        lock (_lock)
        {
            _running = false;
            if (_stopped) return;
            _timer?.Change(_watched ? _fast : _idle, Timeout.InfiniteTimeSpan);
        }
    }
}

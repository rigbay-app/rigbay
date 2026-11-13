namespace Rigbay.Gpu;

/// <summary>
/// Calls a poll action on a timer, but only while <see cref="Watched"/> is true: every <see cref="Interval"/> while somebody wants the readings, and
/// not at all otherwise (a laptop's NVIDIA GPU must be free to sleep). The next poll is armed after the previous one finished, so polls never overlap
/// or pile up. <see cref="PollSoon"/> polls at once when somebody starts to look. An exception is caught here and handed to the error callback: a
/// thread this module starts must never throw.
/// </summary>
internal sealed class PollScheduler : IDisposable
{
    private readonly Action _poll;
    private readonly Action<Exception> _onError;
    private readonly object _lock = new();
    private Timer? _timer;
    private bool _stopped = true;
    private bool _running;
    private bool _watched;
    private TimeSpan _interval;

    public PollScheduler(Action poll, TimeSpan interval, Action<Exception> onError)
    {
        _poll = poll;
        _interval = interval;
        _onError = onError;
    }

    /// <summary>Time between polls while watched.</summary>
    public TimeSpan Interval { get { lock (_lock) return _interval; } set { lock (_lock) _interval = value; } }

    /// <summary>True while somebody wants the readings. When it turns false the poll that is armed does nothing and no new one is armed.</summary>
    public bool Watched { get { lock (_lock) return _watched; } set { lock (_lock) _watched = value; } }

    /// <summary>True while a poll is armed or running (started and watched).</summary>
    public bool IsPolling { get { lock (_lock) return !_stopped && _watched; } }

    /// <summary>Starts the scheduler. The first poll runs after <paramref name="firstDelay"/> when watched; when not, nothing runs until <see cref="PollSoon"/>.</summary>
    public void Start(TimeSpan firstDelay)
    {
        lock (_lock)
        {
            _stopped = false;
            _timer?.Dispose();
            _timer = new Timer(OnTimer, null, _watched ? firstDelay : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Polls right away. Does nothing while a poll is running (it re-arms by itself) or when stopped or not watched.</summary>
    public void PollSoon()
    {
        lock (_lock)
        {
            if (_stopped || _running || !_watched) return;
            try { _timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Stops polling. A poll that is running finishes; no new one starts.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _stopped = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose() => Stop();

    private void OnTimer(object? state)
    {
        lock (_lock)
        {
            if (_stopped) return;
            if (!_watched) return;   // somebody stopped looking while the timer was armed: no read, no re-arm
            _running = true;
        }
        try { _poll(); }
        catch (Exception ex)
        {
            try { _onError(ex); } catch (Exception) { /* nothing left to do */ }
        }
        lock (_lock)
        {
            _running = false;
            if (_stopped) return;
            try { _timer?.Change(_watched ? _interval : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }
}

namespace Rigbay.SteamBypass;

/// <summary>
/// Keeps a failing network from filling the log: at most <c>burst</c> lines per <c>window</c>, then the lines are counted and one
/// "(N more lines left out)" is written when the window is over. Safe from any thread.
/// </summary>
internal sealed class LogThrottle
{
    private readonly int _burst;
    private readonly TimeSpan _window;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private DateTimeOffset _windowStart;
    private int _written;
    private int _suppressed;

    /// <param name="burst">Lines allowed per window.</param>
    /// <param name="window">Length of a window.</param>
    /// <param name="now">The clock.</param>
    public LogThrottle(int burst, TimeSpan window, Func<DateTimeOffset> now)
    {
        _burst = burst;
        _window = window;
        _now = now;
        _windowStart = now();
    }

    /// <summary>
    /// Asks whether a line may be written now. <paramref name="left"/> is the number of lines that were left out in the window that just
    /// ended (0 most of the time); the caller writes one summary line for them before its own.
    /// </summary>
    public bool Allow(out int left)
    {
        lock (_gate)
        {
            left = 0;
            var now = _now();
            if (now - _windowStart >= _window)
            {
                left = _suppressed;
                _suppressed = 0;
                _written = 0;
                _windowStart = now;
            }
            if (_written >= _burst) { _suppressed++; return false; }
            _written++;
            return true;
        }
    }
}

namespace Rigbay.Scenes;

/// <summary>A running program: the file name (<c>game.exe</c>), the full path when it was asked for and could be read, and the process id.</summary>
internal readonly record struct RunningProgram(string Name, string? Path, int ProcessId);

/// <summary>Which programs run. The only thing the module asks the machine; it never starts, stops or changes a process.</summary>
internal interface IProcessSource
{
    /// <summary>
    /// The running programs whose file name is one of <paramref name="exeNames"/> (lower case, <c>game.exe</c>). With <paramref name="withPath"/> the full path of each is read as well
    /// (null when Windows refuses). Never throws: a failing read is "not running".
    /// </summary>
    IReadOnlyList<RunningProgram> Find(IReadOnlyCollection<string> exeNames, bool withPath);

    /// <summary>The names of the programs that have a window, for the picker on the page. Sorted, without duplicates.</summary>
    IReadOnlyList<string> WindowedPrograms();
}

/// <summary>A repeating timer that tests fire by hand.</summary>
internal interface ITicker
{
    IDisposable Every(TimeSpan period, Action action);
}

/// <summary>Everything the module takes from the machine, in one place so tests can replace it.</summary>
internal sealed class Dependencies
{
    /// <summary>How often the process list is read while a scene is armed or a run is open.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the game must be gone before the scene is put back, so a launcher that restarts the game does not flap the machine.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the module waits at start for the modules an interrupted run needs.</summary>
    public TimeSpan TargetWait { get; init; } = TimeSpan.FromSeconds(30);

    public required IProcessSource Processes { get; init; }
    public required ITicker Ticker { get; init; }
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (t, ct) => Task.Delay(t, ct);

    public static Dependencies Real() => new() { Processes = new Windows.WindowsProcesses(), Ticker = new TimerTicker() };
}

internal sealed class TimerTicker : ITicker
{
    public IDisposable Every(TimeSpan period, Action action) => new Handle(period, action);

    private sealed class Handle : IDisposable
    {
        private readonly Timer _timer;
        private int _running;

        public Handle(TimeSpan period, Action action)
        {
            // a tick that is still running is not started again; the action catches its own exceptions
            _timer = new Timer(_ =>
            {
                if (Interlocked.Exchange(ref _running, 1) == 1) return;
                try { action(); } catch (Exception) { } finally { Volatile.Write(ref _running, 0); }
            }, null, period, period);
        }

        public void Dispose() => _timer.Dispose();
    }
}

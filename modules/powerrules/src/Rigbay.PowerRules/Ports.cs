namespace Rigbay.PowerRules;

/// <summary>Where the power comes from, and when that may have changed.</summary>
internal interface IPowerSourcePort : IDisposable
{
    /// <summary>True when the PC has a battery (a desktop has none, and the module has nothing to do there).</summary>
    bool HasBattery();

    /// <summary>The source now (one read; not polled).</summary>
    PowerSource Read();

    /// <summary>Starts listening for changes. Calling it twice does nothing.</summary>
    void Start();

    /// <summary>True when Windows accepted the notification request after <see cref="Start"/>. False: the module falls back to a slow read (<see cref="Dependencies.FallbackPoll"/>).</summary>
    bool Listening { get; }

    /// <summary>Raised on any thread when Windows says the power source changed.</summary>
    event Action? SourceChanged;

    /// <summary>Raised on any thread after the PC woke from sleep.</summary>
    event Action? Resumed;
}

/// <summary>The internal panel. The only write is the refresh rate, and only a rate the panel lists.</summary>
internal interface IPanelPort
{
    /// <summary>Finds the internal panel among the active displays and lists its rates at the current resolution. Reads only.</summary>
    PanelLookup Find();

    /// <summary>Asks Windows whether it would accept <paramref name="hz"/> (<c>CDS_TEST</c>). Changes nothing.</summary>
    DisplayChange Test(PanelInfo panel, int hz);

    /// <summary>Changes the refresh rate of this display, for this session. Only after <see cref="Test"/> said yes.</summary>
    DisplayChange Apply(PanelInfo panel, int hz);
}

/// <summary>The Windows power mode (the overlay scheme). The export is undocumented, so every member must cope with it missing.</summary>
internal interface IPowerModePort
{
    /// <summary>True when both exports were found in <c>powrprof.dll</c>.</summary>
    bool Available { get; }

    /// <summary>The effective mode, or <see cref="PowerMode.Unknown"/> when it cannot be read or is none of the three.</summary>
    PowerMode Read();

    /// <summary>Sets one of the three modes. A win32 code of 0 is success; -1 means the export is missing.</summary>
    int Set(PowerMode mode);
}

/// <summary>One-shot timers, so tests can fire them by hand.</summary>
internal interface IScheduler
{
    IDisposable Schedule(TimeSpan delay, Action action);
}

internal sealed class TimerScheduler : IScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        // Runs on a pool thread, outside the host's fault boundary: nothing may escape.
        var timer = new Timer(_ => { try { action(); } catch (Exception) { } }, null, delay, Timeout.InfiniteTimeSpan);
        return timer;
    }
}

/// <summary>Everything the module touches on the machine, in one place; tests pass fakes.</summary>
internal sealed class Dependencies
{
    public required IPowerSourcePort Source { get; init; }
    public required IPanelPort Panel { get; init; }
    public required IPowerModePort Mode { get; init; }
    public IScheduler Scheduler { get; init; } = new TimerScheduler();

    /// <summary>Wait after a change of source before the rules run: Windows swaps its own power mode first, and XTU's auto-switch has applied its profile.</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the source is read only when the notification could not be registered (a real read of <c>GetSystemPowerStatus</c>, nothing else).</summary>
    public TimeSpan FallbackPoll { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Wait after resume: displays and drivers come back first, XTU forces its check 4 s after resume.</summary>
    public TimeSpan ResumeDelay { get; init; } = TimeSpan.FromSeconds(8);

    public static Dependencies Real() => new()
    {
        Source = new Windows.WindowsPowerSource(),
        Panel = new Windows.WindowsPanel(),
        Mode = new Windows.WindowsPowerMode(),
    };
}

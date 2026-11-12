namespace Rigbay.PredatorSense;

/// <summary>The module's settings, one JSON file in the module's data folder.</summary>
public sealed class PredatorSenseSettings
{
    /// <summary>Lowest poll interval, seconds.</summary>
    public const int MinPollSeconds = 1;

    /// <summary>Highest poll interval, seconds.</summary>
    public const int MaxPollSeconds = 30;

    /// <summary>Seconds between sensor reads while nobody watches them (page hidden, no subscriber). Not a setting.</summary>
    public const int IdlePollSeconds = 10;

    /// <summary>Seconds between reads of the performance mode while the page is visible or its metric has a subscriber; it is not read at all otherwise. Not a setting.</summary>
    public const int OpModePollSeconds = 10;

    /// <summary>Seconds between sensor reads while something watches them. One read takes about 11 ms.</summary>
    public int PollSeconds { get; set; } = 2;

    /// <summary>When the module stops (turned off, host exit, fault) and Rigbay had set Maximum, put the fans back to Auto.</summary>
    public bool RestoreAutoOnExit { get; set; } = true;

    /// <summary>Brings a stored value into range (the file is plain text and may have been edited).</summary>
    public PredatorSenseSettings Clamped()
    {
        PollSeconds = Math.Clamp(PollSeconds, MinPollSeconds, MaxPollSeconds);
        return this;
    }
}

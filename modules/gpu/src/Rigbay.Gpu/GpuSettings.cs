namespace Rigbay.Gpu;

/// <summary>The module's saved settings.</summary>
public sealed class GpuSettings
{
    /// <summary>Smallest time between two reads while somebody looks.</summary>
    public const int MinPollSeconds = 1;

    /// <summary>Largest time between two reads while somebody looks.</summary>
    public const int MaxPollSeconds = 30;

    /// <summary>Default time between two reads while somebody looks.</summary>
    public const int DefaultPollSeconds = 2;

    /// <summary>Seconds between two reads while the page is open or something uses the readings (never read otherwise).</summary>
    public int PollSeconds { get; set; } = DefaultPollSeconds;

    /// <summary>A copy with every value inside its range (the file may have been edited).</summary>
    internal GpuSettings Clamped() => new() { PollSeconds = Math.Clamp(PollSeconds, MinPollSeconds, MaxPollSeconds) };
}

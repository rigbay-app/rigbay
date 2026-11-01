namespace Rigbay.Recorder;

/// <summary>The module's settings, one JSON file in the module's data folder. Everything here is the user's choice on the page.</summary>
public sealed class RecorderSettings
{
    /// <summary>
    /// The periods the page offers, in seconds, with the language key of each label. The verb accepts any whole number from
    /// <see cref="MinPeriodSeconds"/> to <see cref="MaxPeriodSeconds"/>. (The keys are spelled out so LangCheck sees them used.)
    /// </summary>
    public static readonly (int Seconds, string LabelKey)[] PeriodChoices =
    {
        (1, "period.1"), (2, "period.2"), (5, "period.5"), (10, "period.10"),
    };

    /// <summary>The auto-stop durations the page offers, in minutes, with the language key of each label; 0 is "no limit".</summary>
    public static readonly (int Minutes, string LabelKey)[] DurationChoices =
    {
        (0, "duration.0"), (1, "duration.1"), (5, "duration.5"), (10, "duration.10"), (15, "duration.15"),
        (30, "duration.30"), (60, "duration.60"), (120, "duration.120"), (240, "duration.240"),
    };

    /// <summary>The offered value closest to <paramref name="value"/> (a settings file may have been edited by hand).</summary>
    public static int Snap(IEnumerable<int> offered, int value) => offered.OrderBy(o => Math.Abs(o - value)).ThenBy(o => o).First();

    /// <summary>Shortest period, seconds. The sources refresh about once a second, so sampling faster only repeats values.</summary>
    public const int MinPeriodSeconds = 1;

    /// <summary>Longest period, seconds.</summary>
    public const int MaxPeriodSeconds = 3600;

    /// <summary>Longest timed recording, minutes (a week).</summary>
    public const int MaxDurationMinutes = 7 * 24 * 60;

    /// <summary>Seconds between rows.</summary>
    public int PeriodSeconds { get; set; } = 1;

    /// <summary>Minutes after which a recording stops by itself; 0 for no limit.</summary>
    public int DurationMinutes { get; set; }

    /// <summary>Folder for the recordings; empty for <c>Documents\Rigbay\logs</c>.</summary>
    public string Folder { get; set; } = "";

    /// <summary>
    /// Metric ids the user switched off. Everything else is recorded, so a metric that a module publishes for the first time
    /// is recorded without the user doing anything ("all by default").
    /// </summary>
    public List<string> ExcludedMetrics { get; set; } = new();

    /// <summary>Brings stored values into range (the file is plain text and may have been edited).</summary>
    public RecorderSettings Clamped()
    {
        PeriodSeconds = Snap(PeriodChoices.Select(c => c.Seconds), Math.Clamp(PeriodSeconds, MinPeriodSeconds, MaxPeriodSeconds));
        DurationMinutes = Snap(DurationChoices.Select(c => c.Minutes), Math.Clamp(DurationMinutes, 0, MaxDurationMinutes));
        Folder = (Folder ?? "").Trim();
        ExcludedMetrics = (ExcludedMetrics ?? new List<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        return this;
    }
}

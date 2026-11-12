namespace Rigbay.Core;

/// <summary>How a tick of a chart's time axis is written: "now", a count of seconds or a count of minutes before now.</summary>
public enum TimeTickUnit
{
    Now,
    Seconds,
    Minutes,
}

/// <summary>One label of a chart's time axis: <paramref name="SecondsAgo"/> before now, written as <paramref name="Value"/> in <paramref name="Unit"/>.</summary>
public readonly record struct TimeTick(int SecondsAgo, int Value, TimeTickUnit Unit);

/// <summary>What a chart's time axis spans and where its labels go.</summary>
/// <param name="ShownSeconds">The time the axis spans, right edge = now.</param>
/// <param name="StepSeconds">The time between two labels.</param>
/// <param name="Ticks">The labels, now first.</param>
/// <param name="CoversRange">True when the data reaches back over the whole chosen range.</param>
public sealed record TimeAxisPlan(double ShownSeconds, int StepSeconds, IReadOnlyList<TimeTick> Ticks, bool CoversRange);

/// <summary>
/// The time axis of a trend chart. Until the data covers the chosen range the axis spans the data's own age (at least <see cref="MinSpan"/>), so a
/// reading that just started is not a sliver in a corner. The labels are honest:
/// <list type="bullet">
/// <item>one unit on the whole axis, seconds while the labelled part is under two minutes, whole minutes from there on, so two labels never read the
/// same (a 90 s span used to say "-1 min" at 60 and at 90 s);</item>
/// <item>no label where there is no data yet (20 s of data on a one-minute axis is labelled up to "-20 s" at most, not to "-60 s"; none lies past the oldest point);</item>
/// <item>the step is the smallest round one (5, 10, 15, 30 s, 1, 2, 5 min) that leaves at most <see cref="MaxIntervals"/> intervals in the labelled
/// part and at least <see cref="MinTickGap"/> pixels between two labels.</item>
/// </list>
/// Pure, so the rules are tested without a window.
/// </summary>
public static class TimeAxis
{
    /// <summary>The shortest span the axis draws (a reading that started a few seconds ago still gets a minute).</summary>
    public const double MinSpan = 60;
    /// <summary>The fewest pixels between two labels ("-10 min" and "-12 phút" need about 50 px each).</summary>
    public const double MinTickGap = 80;
    /// <summary>The most intervals between labels.</summary>
    public const int MaxIntervals = 5;

    static readonly int[] Steps = { 5, 10, 15, 30, 60, 120, 300, 600 };

    /// <summary>
    /// Plans the axis of a chart whose oldest point is <paramref name="dataAgeSeconds"/> old, drawn over a range of <paramref name="rangeSeconds"/>,
    /// on a plot <paramref name="plotWidth"/> pixels wide. The data counts as covering the range when it is younger than the range by no more than
    /// <paramref name="toleranceSeconds"/> (one sample's spacing: the history keeps what is inside the range, so its oldest point is at most one
    /// sample younger than the range).
    /// </summary>
    public static TimeAxisPlan Plan(double dataAgeSeconds, int rangeSeconds, double plotWidth = double.PositiveInfinity, double toleranceSeconds = 2)
    {
        double range = Math.Max(1, rangeSeconds);
        double age = double.IsFinite(dataAgeSeconds) ? Math.Max(0, dataAgeSeconds) : 0;
        double tol = double.IsFinite(toleranceSeconds) ? Math.Max(0, toleranceSeconds) : 0;
        bool covers = age + tol >= range;
        double shown = covers ? range : Math.Min(range, Math.Max(MinSpan, age));

        // the labelled part: the whole axis once the data covers the range, else only up to the oldest point (the tolerance is for the full range
        // only: there the history itself cut the older points off)
        double labelled = covers ? shown : Math.Min(shown, age);
        bool minutes = labelled >= 120;
        double minStep = double.IsFinite(plotWidth) && plotWidth > 0 ? shown * MinTickGap / plotWidth : 0;
        int step = 0;
        foreach (var s in Steps)
        {
            if (minutes && s < 60) continue;
            if (s + 1e-9 < minStep) continue;
            if (labelled / s > MaxIntervals + 1e-9) continue;
            step = s;
            break;
        }
        if (step == 0) step = Math.Max(60, (int)Math.Ceiling(Math.Max(minStep, labelled / MaxIntervals) / 60) * 60);

        var ticks = new List<TimeTick>();
        for (int k = 0; k <= labelled + 1e-6 && k <= shown + 1e-6; k += step)
            ticks.Add(k == 0 ? new TimeTick(0, 0, TimeTickUnit.Now)
                : step >= 60 ? new TimeTick(k, k / 60, TimeTickUnit.Minutes)
                : new TimeTick(k, k, TimeTickUnit.Seconds));
        return new TimeAxisPlan(shown, step, ticks, covers);
    }
}

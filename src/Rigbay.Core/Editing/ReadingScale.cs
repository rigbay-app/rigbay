using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>
/// The honest scale of a small chart: it follows the data but never blows noise up into a full-height line. Every unit has a smallest
/// span the line is allowed to fill (10 °C, 10 W, 20 %, 500 rpm ...): a temperature that moves by one degree is drawn as the near-flat
/// line it is. Pure functions, so the rules are tested without a window.
/// </summary>
public static class ReadingScale
{
    /// <summary>The smallest range a chart of this unit may zoom in to; <paramref name="magnitude"/> (the largest absolute value) is used for units without a natural size.</summary>
    public static double MinSpan(MetricUnit unit, double magnitude) => unit switch
    {
        MetricUnit.Celsius => 10,
        MetricUnit.Watt => 10,
        MetricUnit.Percent => 20,
        MetricUnit.Rpm => 500,
        MetricUnit.Megahertz => 400,
        MetricUnit.Volt => 0.2,
        MetricUnit.Ampere => 1,
        MetricUnit.Milliseconds => 10,
        MetricUnit.Seconds => 10,
        _ => Math.Max(4, 0.1 * Math.Abs(magnitude)),
    };

    /// <summary>True for units that cannot go below zero, so a zoomed scale never shows negative numbers for them.</summary>
    public static bool NonNegative(MetricUnit unit) => unit is not (MetricUnit.Celsius or MetricUnit.Volt or MetricUnit.Ampere or MetricUnit.None);

    /// <summary>The natural range a reading of this unit is judged against (a tile meter): 0 to 100 for a percent and a temperature, none for the rest.</summary>
    public static (double Min, double Max)? NaturalRange(MetricUnit unit) => unit switch
    {
        MetricUnit.Percent => (0, 100),
        MetricUnit.Celsius => (0, 100),
        _ => null,
    };

    /// <summary>
    /// Fits a line to the data: the data's min and max, widened to the unit's smallest span around the middle, with 6 % room on both sides, and moved
    /// (not cut) back into 0 to 100 for a percent and above 0 for units that cannot go below it. Returns Lo below Hi, always.
    /// </summary>
    public static (double Lo, double Hi) Fit(double min, double max, MetricUnit unit)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max)) return (0, 1);
        if (min > max) (min, max) = (max, min);
        double span = Math.Max(MinSpan(unit, Math.Max(Math.Abs(min), Math.Abs(max))), max - min);
        double mid = (min + max) / 2;
        double lo = mid - span / 2, hi = mid + span / 2;
        double pad = span * 0.06;
        lo -= pad; hi += pad;
        return Keep(lo, hi, unit);
    }

    /// <summary>Slides the range back inside what the unit allows (never below zero, never above 100 for a percent), keeping its span.</summary>
    private static (double, double) Keep(double lo, double hi, MetricUnit unit)
    {
        if (NonNegative(unit) && lo < 0) { hi -= lo; lo = 0; }
        if (unit == MetricUnit.Percent && hi > 100) { lo -= hi - 100; hi = 100; if (lo < 0) lo = 0; }
        return (lo, hi);
    }

    /// <summary>
    /// The axis of a trend chart: <see cref="Fit"/> first, then widened to round numbers so the gridlines fall on them.
    /// <paramref name="fixedMin"/> and <paramref name="fixedMax"/> (a module's declared range) widen it further to contain them when set.
    /// </summary>
    /// <returns>The axis ends and the tick values from the bottom to the top (3 to 6 of them).</returns>
    public static (double Lo, double Hi, IReadOnlyList<double> Ticks) Axis(double min, double max, MetricUnit unit, double? fixedMin = null, double? fixedMax = null, int targetTicks = 5)
    {
        if (fixedMin is { } fm) min = Math.Min(min, fm);
        if (fixedMax is { } fx) max = Math.Max(max, fx);
        var (lo, hi) = fixedMin is not null && fixedMax is not null ? (min, max) : Fit(min, max, unit);   // min and max already hold the declared range
        double step = NiceStep((hi - lo) / Math.Max(2, targetTicks - 1));
        double a = Math.Floor(lo / step + 1e-9) * step, b = Math.Ceiling(hi / step - 1e-9) * step;
        if (NonNegative(unit) && a < 0) a = 0;
        if (unit == MetricUnit.Percent && b > 100) b = 100;
        if (b <= a) b = a + step;
        var ticks = new List<double>();
        for (double v = a; v <= b + step * 1e-6; v += step) ticks.Add(Math.Round(v / step) * step);
        return (a, b, ticks);
    }

    /// <summary>The next 1, 2 or 5 times a power of ten at or above <paramref name="raw"/>.</summary>
    public static double NiceStep(double raw)
    {
        if (!(raw > 0) || !double.IsFinite(raw)) return 1;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double f = raw / pow;
        double nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nice * pow;
    }

    /// <summary>A tick label: whole numbers when the step is whole, one more digit for each decimal the step has (invariant culture, like the readings).</summary>
    public static string FormatTick(double value, double step)
    {
        int decimals = 0;
        for (double s = Math.Abs(step); s > 0 && decimals < 4 && Math.Abs(s - Math.Round(s)) > 1e-9; s *= 10) decimals++;
        return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    /// <summary>The share (0 to 1) of <paramref name="value"/> between <paramref name="lo"/> and <paramref name="hi"/>, clamped; 0 for an empty range.</summary>
    public static double Fraction(double value, double lo, double hi) => hi > lo && double.IsFinite(value) ? Math.Clamp((value - lo) / (hi - lo), 0, 1) : 0;
}

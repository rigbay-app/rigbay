using System.Collections.Concurrent;
using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>A reading as text in two parts, the number and the unit, so a tile can set them at different sizes.</summary>
/// <param name="Number">The number in the current language's format (<c>3,448</c>, <c>21.2</c>; Vietnamese <c>3.448</c>, <c>21,2</c>).</param>
/// <param name="Unit">The unit symbol, scaled where the unit scales (<c>KB/s</c> for 17,978 B/s); empty for none.</param>
public readonly record struct ReadingText(string Number, string Unit)
{
    /// <summary>"3,448 rpm", "17.6 KB/s", "42": the number and the unit with a space between.</summary>
    public override string ToString() => Unit.Length > 0 ? Number + " " + Unit : Number;
}

/// <summary>
/// How a <see cref="ReadingRow"/> (and the dashboard, the tiles and the charts) turns a bus sample into text, including the "not available" case:
/// no sample, or a sample with <see cref="MetricSample.IsAvailable"/> false (the module withdrew the metric).
/// <para>Numbers are written in the current language's format with thousands grouped (<c>3,448 rpm</c>, <c>4,072 MHz</c>; Vietnamese
/// <c>3.448 rpm</c>, <c>21,2 W</c>). Bytes and bytes per second scale to KB, MB, GB (1024-based): one decimal under 100, none from 100 on
/// (<c>980 B/s</c>, <c>17.6 KB/s</c>, <c>176 KB/s</c>, <c>1.2 MB/s</c>), whatever the row's <see cref="ReadingRow.Decimals"/>.</para>
/// </summary>
public static class ReadingFormat
{
    static readonly ConcurrentDictionary<string, CultureInfo> Cultures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when there is a real number to show.</summary>
    /// <param name="sample">What <c>TryGetLatest</c> or a subscription gave; null when there is none.</param>
    public static bool IsAvailable(MetricSample? sample) => sample is { IsAvailable: true } && double.IsFinite(sample.Value);

    /// <summary>
    /// The number format of a UI language: English (and an unknown code) the invariant format (<c>1,234.5</c>), any other language its own
    /// culture's (Vietnamese <c>1.234,5</c>). Never the Windows user's regional format, so a page reads the same as its language.
    /// </summary>
    /// <param name="languageCode">The UI language code (<c>en</c>, <c>vi</c>).</param>
    public static CultureInfo CultureFor(string? languageCode)
    {
        if (string.IsNullOrEmpty(languageCode) || languageCode.Equals("en", StringComparison.OrdinalIgnoreCase)) return CultureInfo.InvariantCulture;
        return Cultures.GetOrAdd(languageCode, code =>
        {
            try
            {
                var c = CultureInfo.GetCultureInfo(code, predefinedOnly: true);   // a made-up code is not a culture with a format of its own
                // a culture the runtime does not know (or invariant globalization) comes back without a format of its own
                return string.IsNullOrEmpty(c.Name) ? CultureInfo.InvariantCulture : c;
            }
            catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
        });
    }

    /// <summary>The number format of the host's current language.</summary>
    public static CultureInfo Culture(LanguageCatalog languages) => CultureFor(languages.CurrentCode);

    /// <summary>The factor a byte unit is divided by and the symbol it then wears, chosen from <paramref name="magnitude"/>: (1024, "KB/s") for 17,978 B/s.</summary>
    /// <param name="unit">The reading's unit; only <see cref="MetricUnit.Bytes"/> and <see cref="MetricUnit.BytesPerSecond"/> scale.</param>
    /// <param name="magnitude">The largest absolute value that will be shown with this scale.</param>
    public static (double Divisor, string Symbol) Scale(MetricUnit unit, double magnitude)
    {
        if (unit is not (MetricUnit.Bytes or MetricUnit.BytesPerSecond)) return (1, unit.Symbol());
        string[] prefixes = { "B", "KB", "MB", "GB", "TB" };
        double m = double.IsFinite(magnitude) ? Math.Abs(magnitude) : 0;
        int k = 0;
        while (k < prefixes.Length - 1 && m >= 1024) { m /= 1024; k++; }
        return (Math.Pow(1024, k), prefixes[k] + (unit == MetricUnit.BytesPerSecond ? "/s" : ""));
    }

    /// <summary>A value as its number and unit (scaled for bytes) in <paramref name="culture"/>.</summary>
    /// <param name="value">The value in the metric's own unit.</param>
    /// <param name="unit">The metric's unit.</param>
    /// <param name="decimals">Digits after the decimal point (<see cref="ReadingRow.Decimals"/>); a scaled byte value chooses its own.</param>
    /// <param name="culture">The number format (<see cref="CultureFor"/>).</param>
    public static ReadingText Parts(double value, MetricUnit unit, int decimals, CultureInfo culture)
    {
        var (divisor, symbol) = Scale(unit, value);
        if (divisor > 1)
        {
            double scaled = value / divisor;
            return new ReadingText(scaled.ToString(Math.Abs(scaled) < 100 ? "N1" : "N0", culture), symbol);
        }
        if (unit is MetricUnit.Bytes or MetricUnit.BytesPerSecond) decimals = 0;   // a byte is not divided
        return new ReadingText(value.ToString("N" + Math.Clamp(decimals, 0, 6), culture), symbol);
    }

    /// <summary>
    /// The text of a reading: the number with <paramref name="decimals"/> digits and the unit symbol in the current language's format, or the
    /// host's "not available" text when <see cref="IsAvailable"/> is false.
    /// </summary>
    /// <param name="languages">The host catalog: the "not available" text and the number format of the current language.</param>
    /// <param name="sample">The sample, or null.</param>
    /// <param name="decimals">Digits after the decimal point (<see cref="ReadingRow.Decimals"/>).</param>
    public static string Format(LanguageCatalog languages, MetricSample? sample, int decimals = 0)
    {
        if (!IsAvailable(sample)) return languages.T("core.reading.unavailable");
        return Parts(sample!.Value, sample.Unit, decimals, Culture(languages)).ToString();
    }

    /// <summary><see cref="Format(LanguageCatalog, MetricSample?, int)"/> with the row's own decimals.</summary>
    public static string Format(LanguageCatalog languages, ReadingRow row, MetricSample? sample) => Format(languages, sample, row.Decimals);
}

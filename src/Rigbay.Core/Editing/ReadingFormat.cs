using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>
/// How a <see cref="ReadingRow"/> (and the dashboard) turns a bus sample into text, including the "not available" case:
/// no sample, or a sample with <see cref="MetricSample.IsAvailable"/> false (the module withdrew the metric).
/// </summary>
public static class ReadingFormat
{
    /// <summary>True when there is a real number to show.</summary>
    /// <param name="sample">What <c>TryGetLatest</c> or a subscription gave; null when there is none.</param>
    public static bool IsAvailable(MetricSample? sample) => sample is { IsAvailable: true } && double.IsFinite(sample.Value);

    /// <summary>
    /// The text of a reading: the number with <paramref name="decimals"/> digits (invariant culture, like the page shows it)
    /// and the unit symbol, or the host's "not available" text when <see cref="IsAvailable"/> is false.
    /// </summary>
    /// <param name="languages">The host catalog, for the "not available" text in the current language.</param>
    /// <param name="sample">The sample, or null.</param>
    /// <param name="decimals">Digits after the decimal point (<see cref="ReadingRow.Decimals"/>).</param>
    public static string Format(LanguageCatalog languages, MetricSample? sample, int decimals = 0)
    {
        if (!IsAvailable(sample)) return languages.T("core.reading.unavailable");
        var symbol = sample!.Unit.Symbol();
        return sample.Value.ToString("F" + Math.Max(0, decimals), CultureInfo.InvariantCulture) + (symbol.Length > 0 ? " " + symbol : "");
    }

    /// <summary><see cref="Format(LanguageCatalog, MetricSample?, int)"/> with the row's own decimals.</summary>
    public static string Format(LanguageCatalog languages, ReadingRow row, MetricSample? sample) => Format(languages, sample, row.Decimals);
}

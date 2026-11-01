using System.Globalization;
using System.Text;

namespace Rigbay.Recorder.Recording;

/// <summary>How values are written to the file. Same rules as the CSV of the old XTU app: invariant culture, no thousands separator, at most 3 decimals.</summary>
internal static class CsvFormat
{
    /// <summary>The line break between rows (RFC 4180).</summary>
    public const string NewLine = "\r\n";

    /// <summary>Prefix every recording has; the history lists files that match.</summary>
    public const string FilePrefix = "rigbay-log-";

    /// <summary>Extension of the data file.</summary>
    public const string CsvExtension = ".csv";

    /// <summary>Ending of the file next to it that describes the columns.</summary>
    public const string MetaSuffix = ".meta.json";

    /// <summary>UTF-8 without a byte order mark.</summary>
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A number with <c>.</c> as the decimal separator and at most 3 decimals; no <c>-0</c>, no exponent.</summary>
    public static string Number(double value)
    {
        var text = value.ToString("0.###", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    /// <summary>Local time, ISO 8601 with the offset, millisecond precision: <c>2026-11-01T20:35:06.848+07:00</c>.</summary>
    public static string Time(DateTimeOffset time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    /// <summary>Quotes a text field when it holds a comma, a quote or a line break.</summary>
    public static string Field(string text)
    {
        if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>The file name stem for a recording that starts at <paramref name="start"/>: <c>rigbay-log-2026-11-01_20-35-06</c>.</summary>
    public static string FileStem(DateTimeOffset start) => FilePrefix + start.ToString("yyyy-MM-dd'_'HH-mm-ss", CultureInfo.InvariantCulture);

    /// <summary>Elapsed time as <c>hh:mm:ss</c> (hours may exceed 24).</summary>
    public static string Clock(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return string.Create(CultureInfo.InvariantCulture, $"{(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");
    }

    /// <summary>A file size as text: <c>312 B</c>, <c>48.2 KB</c>, <c>3.4 MB</c>.</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return kb.ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        return (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
    }
}

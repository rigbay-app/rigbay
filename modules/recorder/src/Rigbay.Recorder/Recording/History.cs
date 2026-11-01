namespace Rigbay.Recorder.Recording;

/// <summary>One finished recording in the folder.</summary>
/// <param name="Path">Full path of the CSV.</param>
/// <param name="Name">File name.</param>
/// <param name="Bytes">Size of the CSV.</param>
/// <param name="LastWrite">When the file was last written (local time), which is when the recording ended.</param>
internal sealed record HistoryEntry(string Path, string Name, long Bytes, DateTime LastWrite);

/// <summary>The newest recordings in a folder. Only reads; the recorder never deletes a file.</summary>
internal static class History
{
    /// <summary>How many entries the page lists. Older files stay in the folder.</summary>
    public const int Limit = 10;

    /// <summary>The newest <paramref name="limit"/> recordings (<c>rigbay-log-*.csv</c>), newest first, leaving out <paramref name="except"/>. An unreadable folder gives an empty list.</summary>
    public static IReadOnlyList<HistoryEntry> List(string folder, int limit = Limit, string? except = null)
    {
        try
        {
            if (!Directory.Exists(folder)) return Array.Empty<HistoryEntry>();
            return new DirectoryInfo(folder)
                .EnumerateFiles(CsvFormat.FilePrefix + "*" + CsvFormat.CsvExtension, SearchOption.TopDirectoryOnly)
                .Where(f => except is null || !string.Equals(f.FullName, except, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Take(Math.Max(0, limit))
                .Select(f => new HistoryEntry(f.FullName, f.Name, f.Length, f.LastWriteTime))
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<HistoryEntry>();
        }
    }
}

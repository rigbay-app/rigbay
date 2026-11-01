using System.Text.Json;
using System.Text.Json.Serialization;
using Rigbay.Abstractions;

namespace Rigbay.Recorder.Recording;

/// <summary>One column of the CSV, as the .meta.json describes it.</summary>
/// <param name="Id">The metric id; it is also the header of the column.</param>
/// <param name="Label">Short name in the language the recording started in.</param>
/// <param name="Unit">Unit symbol (<c>°C</c>, <c>W</c>); empty for a plain number.</param>
/// <param name="Source">Id of the module that published the metric when the recording started.</param>
/// <param name="Description">One sentence about the metric.</param>
internal sealed record ColumnInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("description")] string Description);

/// <summary>A metric that was published for the first time after the file had begun. It is not in the CSV.</summary>
internal sealed record LateMetric(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("first_seen")] string FirstSeen);

/// <summary>The content of <c>rigbay-log-….meta.json</c>.</summary>
internal sealed class MetaDocument
{
    [JsonPropertyName("tool")] public string Tool { get; init; } = "Rigbay recorder";
    [JsonPropertyName("tool_version")] public string ToolVersion { get; init; } = "";
    [JsonPropertyName("host_version")] public string? HostVersion { get; init; }
    [JsonPropertyName("csv")] public string Csv { get; init; } = "";
    [JsonPropertyName("started")] public string Started { get; init; } = "";
    [JsonPropertyName("ended")] public string? Ended { get; init; }
    [JsonPropertyName("period_s")] public double PeriodSeconds { get; init; }
    [JsonPropertyName("limit_minutes")] public double? LimitMinutes { get; init; }
    [JsonPropertyName("rows")] public long Rows { get; init; }
    [JsonPropertyName("stop_reason")] public string? StopReason { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("machine")] public MachineSection Machine { get; init; } = new();
    [JsonPropertyName("modules")] public SortedDictionary<string, string> Modules { get; init; } = new(StringComparer.Ordinal);
    [JsonPropertyName("columns")] public IReadOnlyList<ColumnInfo> Columns { get; init; } = Array.Empty<ColumnInfo>();
    [JsonPropertyName("late_metrics")] public IReadOnlyList<LateMetric> LateMetrics { get; init; } = Array.Empty<LateMetric>();
    [JsonPropertyName("notes")] public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    internal sealed class MachineSection
    {
        [JsonPropertyName("manufacturer")] public string? Manufacturer { get; init; }
        [JsonPropertyName("model")] public string? Model { get; init; }
        [JsonPropertyName("cpu")] public string? Cpu { get; init; }
        [JsonPropertyName("os")] public string? Os { get; init; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // "°C" stays readable instead of becoming °
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The document as UTF-8 JSON without a byte order mark.</summary>
    public byte[] ToUtf8() => CsvFormat.Utf8NoBom.GetBytes(JsonSerializer.Serialize(this, Options));

    /// <summary>The three sentences every file carries (English; the file is data, not user interface).</summary>
    public static readonly IReadOnlyList<string> StandardNotes = new[]
    {
        "Each row holds the latest value every module had published at the time stamped on the row; a module that publishes about once a second is repeated when the period is shorter.",
        "An empty cell means the metric had no value at that moment: it was withdrawn, or its newest value was more than 30 seconds old.",
        "Columns are fixed when the recording starts. A metric that appeared later is listed in late_metrics and is not in the file.",
    };
}

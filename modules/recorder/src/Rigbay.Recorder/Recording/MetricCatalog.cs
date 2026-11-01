using Rigbay.Abstractions;

namespace Rigbay.Recorder.Recording;

/// <summary>What the recorder knows about a metric besides its numbers: the name and the sentence the owner gave it.</summary>
/// <param name="Label">Short name in the current language ("CPU temperature").</param>
/// <param name="Description">One sentence, or the label when the owner gave none.</param>
internal sealed record MetricText(string Label, string Description);

/// <summary>
/// Where the labels come from. The host's bus will answer this (task A12, <c>GetMetricInfo</c>); until it does the recorder uses
/// <see cref="BareCatalog"/>: the label is the id, and unit and source come from the samples themselves.
/// </summary>
internal interface IMetricCatalog
{
    /// <summary>The text of a metric, or null when nobody described it.</summary>
    MetricText? Describe(MetricId id);
}

/// <summary>A catalog that knows nothing: every column is labelled with its id.</summary>
internal sealed class BareCatalog : IMetricCatalog
{
    public MetricText? Describe(MetricId id) => null;
}

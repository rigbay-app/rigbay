using Rigbay.Abstractions;

namespace Rigbay.Recorder.Recording;

/// <summary>What the recorder knows about a metric besides its numbers: the name and the sentence the owner gave it.</summary>
/// <param name="Label">Short name in the current language ("CPU temperature").</param>
/// <param name="Description">One sentence, or the label when the owner gave none.</param>
internal sealed record MetricText(string Label, string Description);

/// <summary>
/// Where the labels come from. The host's bus answers this (<c>IMetricsBus.GetMetricInfo</c>, task A12), see <see cref="BusCatalog"/>. A host that does not, uses
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

/// <summary>
/// The host's answer: <see cref="IMetricsBus.GetMetricInfo"/> resolves the label and the description through the module that owns the
/// metric, in the current language. A host that does not have the member (a test double) answers null for everything and the
/// recorder falls back to the id.
/// </summary>
internal sealed class BusCatalog(IMetricsBus bus) : IMetricCatalog
{
    public MetricText? Describe(MetricId id)
    {
        var info = bus.GetMetricInfo(id);
        return info is null ? null : new MetricText(info.Label, info.Description);
    }
}

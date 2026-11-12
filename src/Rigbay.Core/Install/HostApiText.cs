using Rigbay.Abstractions;

namespace Rigbay.Core.Install;

/// <summary>The English wording of "this release needs another host API", shared by the install checks.</summary>
internal static class HostApiText
{
    /// <summary>
    /// "needs host API 0.1.3 or newer, this is 0.1.2" when only the revision is too low, else "needs host API &gt;=0.2 &lt;0.3, this is 0.1.2".
    /// </summary>
    /// <param name="range">The range the release declares.</param>
    /// <param name="host">This host's API version.</param>
    public static string Needs(ApiVersionRange range, Version host) =>
        range.NeededRevision(host) is { } needed
            ? $"needs host API {needed} or newer, this is {host}"
            : $"needs host API {range.Text}, this is {host}";
}

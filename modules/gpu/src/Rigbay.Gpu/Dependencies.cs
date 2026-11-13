using Rigbay.Gpu.Nvml;

namespace Rigbay.Gpu;

/// <summary>What the module needs from the machine; the tests replace it with a fake NVML.</summary>
internal sealed class Dependencies
{
    /// <summary>The result of opening the library: the API, or null with the language key of the reason.</summary>
    public sealed record Opened(INvml? Nvml, string? ReasonKey);

    /// <summary>Opens the NVML library. Nothing is initialised yet.</summary>
    public Func<Opened> OpenLibrary { get; init; } = RealOpen;

    /// <summary>The clock.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    /// <summary>False in tests: no timer thread, the test calls the poll itself.</summary>
    public bool AutoPoll { get; init; } = true;

    /// <summary>The real machine: <c>nvml.dll</c> from the system folder (the module puts every call behind one lock, see <see cref="SerialNvml"/>).</summary>
    public static Dependencies Real() => new();

    private static Opened RealOpen()
    {
        var api = NvmlApi.TryOpenSystemLibrary(out var reason);
        return api is null ? new Opened(null, reason ?? "probe.no-dll") : new Opened(api, null);
    }
}

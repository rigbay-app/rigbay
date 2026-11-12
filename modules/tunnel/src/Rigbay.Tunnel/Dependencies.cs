using Rigbay.Tunnel.Wg;

namespace Rigbay.Tunnel;

/// <summary>Everything the module takes from the machine, in one place. <see cref="Real"/> is the only code that names a real folder or program.</summary>
internal sealed class Dependencies
{
    public required IWireGuardTools Tools { get; init; }
    public required ITunnelStore Store { get; init; }
    public required IServiceControl Services { get; init; }
    public required INetworkProbe Network { get; init; }
    public required IClock Clock { get; init; }

    /// <summary>False in tests: they call <see cref="TunnelModule.PollOnceAsync"/> themselves instead of waiting for a timer.</summary>
    public bool AutoPoll { get; init; } = true;

    /// <summary>WireGuard for Windows in its default place. Nothing here is a bundled copy.</summary>
    public static Dependencies Real()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard");
        return new Dependencies
        {
            Tools = new WireGuardTools(folder, new ProcessRunner(), new AuthenticodeCheck()),
            Store = new TunnelStore(Path.Combine(folder, "Data", "Configurations")),
            Services = new ServiceControl(),
            Network = new NetworkProbe(),
            Clock = new SystemClock(),
        };
    }
}

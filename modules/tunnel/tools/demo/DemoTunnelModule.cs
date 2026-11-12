using Rigbay.Abstractions;
using Rigbay.Tunnel.Tests;
using Rigbay.Tunnel.Wg;

namespace Rigbay.Tunnel.Demo;

/// <summary>
/// The real <see cref="TunnelModule"/> on a made-up machine: two tunnels (example-tunnel is on, example-backup is off), a made-up server in the
/// documentation range, and traffic that moves. Turning one on or off changes only the made-up service table.
/// </summary>
public sealed class DemoTunnelModule : IModule
{
    private TunnelModule? _inner;
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;

    private TunnelModule Inner => _inner ??= new TunnelModule(Make());

    private Dependencies Make()
    {
        var services = new FakeServices();
        var tools = new FakeTools(services);
        var store = new FakeStore().Add("example-tunnel").Add("example-backup");
        var network = new FakeNetwork();
        services.Set("example-tunnel", ServiceState.Running);
        network.Adapters["example-tunnel"] = new[] { System.Net.IPAddress.Parse("10.9.0.2") };
        network.Listening.Add(3389);
        tools.ShowOutput = (_, field) => field switch
        {
            WgField.LatestHandshakes => World.Handshakes(DateTimeOffset.UtcNow.AddSeconds(-14)),
            WgField.Transfer => World.Transfer(2_400_000 + (long)(DateTimeOffset.UtcNow - _start).TotalSeconds * 18_000, 310_000 + (long)(DateTimeOffset.UtcNow - _start).TotalSeconds * 2_500),
            _ => World.Endpoint("198.51.100.1:51820"),
        };
        return new Dependencies { Tools = tools, Store = store, Services = services, Network = network, Clock = new SystemClock(), AutoPoll = true };
    }

    public ProbeResult Probe() => Inner.Probe();
    public void Initialize(IHostServices host) => Inner.Initialize(host);
    public PageModel? GetPageModel(IHostServices host) => Inner.GetPageModel(host);
    public void Shutdown() => Inner.Shutdown();
}

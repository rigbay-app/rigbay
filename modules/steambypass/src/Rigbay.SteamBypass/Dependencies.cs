using System.Net.Sockets;
using Rigbay.SteamBypass.Check;
using Rigbay.SteamBypass.Net;
using Rigbay.SteamBypass.Proxy;

namespace Rigbay.SteamBypass;

/// <summary>
/// Everything the module reaches outside its own process through: the Windows setting, sockets, DNS-over-HTTPS, the connection check and
/// the look at Steam. <see cref="Real"/> is the production wiring; the tests replace each part, so no test touches the real Windows proxy
/// setting or opens a real port.
/// </summary>
internal sealed class Dependencies
{
    /// <summary>The proxy ports and timings.</summary>
    public ProxyOptions Options { get; init; } = new();

    /// <summary>The Windows setting.</summary>
    public Func<ISystemProxyStore> ProxyStore { get; init; } = () => new RegistryProxyStore();

    /// <summary>Where the previous value is kept, given the file path in the data folder.</summary>
    public Func<string, IProxyStateStore> StateStore { get; init; } = path => new FileProxyStateStore(path);

    /// <summary>The listening sockets.</summary>
    public Func<ILoopbackListenerFactory> Listeners { get; init; } = () => new LoopbackListenerFactory();

    /// <summary>Name resolution (DoH). Disposed with the module when it is <see cref="IDisposable"/>.</summary>
    public Func<IHostResolver> Resolver { get; init; } = () => new DohResolver(DohResolver.CreateHttpClient());

    /// <summary>Replaces the dialer built from the resolver (tests).</summary>
    public Func<DomainSet, IHostResolver, IUpstreamDialer>? Dialer { get; init; }

    /// <summary>The connection check.</summary>
    public Func<IConnectionProbe> Probe { get; init; } = () => new HttpConnectionProbe();

    /// <summary>Looks at Steam.</summary>
    public Func<ISteamProbe> Steam { get; init; } = () => new SteamProbe();

    /// <summary>The clock.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    /// <summary>True when a program already answers on this loopback port (asked only at start, to leave a running Steam Bypass alone).</summary>
    public Func<int, bool> PortAnswers { get; init; } = DefaultPortAnswers;

    /// <summary>Production wiring.</summary>
    public static Dependencies Real() => new();

    private static bool DefaultPortAnswers(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(System.Net.IPAddress.Loopback, port);
            return connect.Wait(TimeSpan.FromMilliseconds(300)) && client.Connected;
        }
        catch (Exception) { return false; }
    }
}

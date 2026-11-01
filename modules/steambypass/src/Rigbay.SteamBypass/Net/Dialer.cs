using System.Net;
using System.Net.Sockets;

namespace Rigbay.SteamBypass.Net;

/// <summary>The proxy was asked to reach an address it must never connect to (itself, loopback, "any").</summary>
internal sealed class ForbiddenTargetException : Exception
{
    public ForbiddenTargetException(string host, IPAddress address) : base($"{host} resolves to {address}, which the bypass never connects to") { }
}

/// <summary>Opens the connection from the proxy to the real server.</summary>
internal interface IUpstreamDialer
{
    /// <summary>Resolves <paramref name="host"/> and connects to <paramref name="port"/>. <paramref name="forceDoh"/> sends the name through DoH even if it is not a listed domain.</summary>
    Task<TcpClient> ConnectAsync(string host, int port, bool forceDoh, CancellationToken ct);
}

/// <summary>
/// The real dialer: DoH for listed domains (or when forced), up to four addresses, 5 s each, never to a loopback or unspecified
/// address (a request for <c>Host: 127.0.0.1</c> on our own port 80 would otherwise connect the proxy to itself in a loop).
/// </summary>
internal sealed class UpstreamDialer : IUpstreamDialer
{
    private readonly IHostResolver _resolver;
    private readonly DomainSet _domains;
    private readonly TimeSpan _connectTimeout;

    public UpstreamDialer(IHostResolver resolver, DomainSet domains, TimeSpan? connectTimeout = null)
    {
        _resolver = resolver;
        _domains = domains;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>True for an address the bypass never connects to: loopback, unspecified, multicast, broadcast, IPv6 link/site scopes of loopback.</summary>
    public static bool IsForbidden(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0 || b[0] >= 224) return true;   // "this network", multicast, reserved
        }
        else if (address.IsIPv6Multicast) return true;
        return false;
    }

    /// <inheritdoc />
    public async Task<TcpClient> ConnectAsync(string host, int port, bool forceDoh, CancellationToken ct)
    {
        var ips = await _resolver.ResolveAsync(host, forceDoh || _domains.Contains(host), ct).ConfigureAwait(false);
        foreach (var ip in ips) if (IsForbidden(ip)) throw new ForbiddenTargetException(host, ip);

        Exception? last = null;
        int tried = 0;
        foreach (var ip in ips)
        {
            if (++tried > 4) break;
            var client = new TcpClient(ip.AddressFamily);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_connectTimeout);
                await client.ConnectAsync(ip, port, timeout.Token).ConfigureAwait(false);
                client.NoDelay = true;
                return client;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { client.Dispose(); throw; }
            catch (Exception ex)
            {
                last = ex;
                client.Dispose();
            }
        }
        throw new IOException($"cannot connect to {host}:{port}" + (last is null ? "" : " (" + last.Message + ")"));
    }
}

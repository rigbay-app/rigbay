using System.Net;
using System.Net.Sockets;

namespace Rigbay.SteamBypass.Net;

/// <summary>A listening socket on the loopback interface.</summary>
internal interface ILoopbackListener : IDisposable
{
    /// <summary>The address and port it is bound to.</summary>
    IPEndPoint LocalEndPoint { get; }

    /// <summary>Waits for the next connection. Throws when the listener is disposed or <paramref name="ct"/> is cancelled.</summary>
    Task<TcpClient> AcceptAsync(CancellationToken ct);
}

/// <summary>
/// The only way the module opens a listening socket. There is no parameter for an address: it is always 127.0.0.1, so nothing in
/// the module can listen on a network-facing interface. Tests replace it so no real port is opened.
/// </summary>
internal interface ILoopbackListenerFactory
{
    /// <summary>Starts listening on 127.0.0.1:<paramref name="port"/> (0 = any free port). Throws <see cref="SocketException"/> when taken.</summary>
    ILoopbackListener Listen(int port);
}

/// <summary>Real sockets: <see cref="TcpListener"/> bound to <see cref="IPAddress.Loopback"/>.</summary>
internal sealed class LoopbackListenerFactory : ILoopbackListenerFactory
{
    /// <inheritdoc />
    public ILoopbackListener Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        listener.Start(backlog: 128);
        return new Real(listener);
    }

    private sealed class Real(TcpListener listener) : ILoopbackListener
    {
        public IPEndPoint LocalEndPoint => (IPEndPoint)listener.LocalEndpoint;
        public Task<TcpClient> AcceptAsync(CancellationToken ct) => listener.AcceptTcpClientAsync(ct).AsTask();
        public void Dispose() { try { listener.Stop(); } catch (Exception) { } }
    }
}

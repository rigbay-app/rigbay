using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Rigbay.SteamBypass.Net;

namespace Rigbay.SteamBypass.Proxy;

/// <summary>Ports and timings of the proxy. The defaults are the real ones; tests use port 0 (any free loopback port).</summary>
internal sealed class ProxyOptions
{
    /// <summary>The proxy and PAC port. It is part of the PAC address written to Windows, so it is fixed in production.</summary>
    public const int DefaultProxyPort = 18081;

    /// <summary>Steam's downloader connects to port 80 of whatever DNS said (the ISP says 127.0.0.1), so this is fixed too.</summary>
    public const int DefaultDownloadPort = 80;

    public int ProxyPort { get; init; } = DefaultProxyPort;
    public int DownloadPort { get; init; } = DefaultDownloadPort;

    /// <summary>How long a client may take to send its first request head.</summary>
    public TimeSpan HeadTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Pause between the first two bytes of a TLS hello and the rest.</summary>
    public TimeSpan TlsSplitDelay { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>After the client is done on the download port, how long the last response may still arrive.</summary>
    public TimeSpan ResponseWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>More simultaneous connections than this are closed at once (a local flood must not use all memory).</summary>
    public int MaxConnections { get; init; } = 1024;
}

/// <summary>Counters of the proxy since the module started. Not reset when the bypass is turned off.</summary>
internal readonly record struct ProxyCounters(int Active, int Total, int Failed, int Rejected);

/// <summary>
/// The local proxy: <c>127.0.0.1:18081</c> for browsers and Steam's web engine (CONNECT tunnels with the TLS hello split, absolute-URI HTTP
/// with the Host header respelled, and the PAC file) and <c>127.0.0.1:80</c> for Steam's own downloader. Everything it accepts is on the
/// loopback interface (the listener factory has no way to say otherwise). Network access, DNS and sockets are injected.
/// </summary>
internal sealed class ProxyEngine : IDisposable
{
    private readonly DomainSet _domains;
    private readonly IUpstreamDialer _dialer;
    private readonly ILoopbackListenerFactory _listeners;
    private readonly ProxyOptions _options;
    private readonly Action<string> _log;
    private readonly Action<string> _verbose;
    private readonly ConcurrentDictionary<TcpClient, byte> _live = new();
    private readonly HashSet<string> _seen = new();
    private readonly object _gate = new();

    private int _active, _total, _failed, _rejected;
    private ILoopbackListener? _proxyListener;
    private ILoopbackListener? _downloadListener;
    private CancellationTokenSource? _cts;

    /// <param name="domains">The Steam domains (what the proxy accepts).</param>
    /// <param name="dialer">Opens connections to the real servers.</param>
    /// <param name="listeners">Opens the loopback listeners.</param>
    /// <param name="options">Ports and timings.</param>
    /// <param name="log">Events worth showing (warnings, errors).</param>
    /// <param name="verbose">Per-host detail, for the debug log only.</param>
    public ProxyEngine(DomainSet domains, IUpstreamDialer dialer, ILoopbackListenerFactory listeners, ProxyOptions? options = null,
        Action<string>? log = null, Action<string>? verbose = null)
    {
        _domains = domains;
        _dialer = dialer;
        _listeners = listeners;
        _options = options ?? new ProxyOptions();
        _log = log ?? (_ => { });
        _verbose = verbose ?? (_ => { });
    }

    /// <summary>True between a successful <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsRunning { get { lock (_gate) return _proxyListener is not null; } }

    /// <summary>Why game downloads cannot work (port 80 not available), or null when the download port is listening.</summary>
    public string? DownloadsError { get; private set; }

    /// <summary>The port the proxy listens on (0 while stopped).</summary>
    public int ProxyPort { get { lock (_gate) return _proxyListener?.LocalEndPoint.Port ?? 0; } }

    /// <summary>The port the download forwarder listens on (0 while it does not).</summary>
    public int DownloadPort { get { lock (_gate) return _downloadListener?.LocalEndPoint.Port ?? 0; } }

    /// <summary>The address of the PAC file Windows is pointed at.</summary>
    public string PacUrl => $"http://127.0.0.1:{ProxyPort}/proxy.pac";

    /// <summary>Every address a listener is bound to right now (tests assert all are 127.0.0.1).</summary>
    public IReadOnlyList<IPEndPoint> BoundEndPoints
    {
        get
        {
            lock (_gate)
                return new[] { _proxyListener?.LocalEndPoint, _downloadListener?.LocalEndPoint }.OfType<IPEndPoint>().ToArray();
        }
    }

    /// <summary>A copy of the counters.</summary>
    public ProxyCounters Counters => new(Volatile.Read(ref _active), Volatile.Read(ref _total), Volatile.Read(ref _failed), Volatile.Read(ref _rejected));

    /// <summary>
    /// Starts listening. The proxy port is required (throws <see cref="SocketException"/> when taken); the download port is optional:
    /// when it is not available <see cref="DownloadsError"/> says why and the proxy still works (browsing needs no port 80).
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_proxyListener is not null) return;
            var proxy = _listeners.Listen(_options.ProxyPort);
            _cts = new CancellationTokenSource();
            // The token is read here, under the lock. The loop used to read the field itself from a pool thread, where a quick Stop (which sets
            // it to null) or a Stop and Start (a newer token) could reach it first: a loop with no token, or one on the wrong run's token.
            var token = _cts.Token;
            _proxyListener = proxy;
            _ = Task.Run(() => AcceptLoopAsync(proxy, HandleProxyAsync, token));

            try
            {
                var downloads = _listeners.Listen(_options.DownloadPort);
                _downloadListener = downloads;
                DownloadsError = null;
                _ = Task.Run(() => AcceptLoopAsync(downloads, HandleDownloadAsync, token));
            }
            catch (Exception ex)
            {
                _downloadListener = null;
                DownloadsError = ex.Message;
                _log($"cannot listen on 127.0.0.1:{_options.DownloadPort} ({ex.Message}); game downloads will not work");
            }
        }
    }

    /// <summary>Stops listening and closes every open connection. Safe to call twice.</summary>
    public void Stop()
    {
        ILoopbackListener? a, b;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            a = _proxyListener; b = _downloadListener; cts = _cts;
            _proxyListener = null; _downloadListener = null; _cts = null;
        }
        try { cts?.Cancel(); } catch (Exception) { }
        try { a?.Dispose(); } catch (Exception) { }
        try { b?.Dispose(); } catch (Exception) { }
        foreach (var client in _live.Keys) CloseQuietly(client);
        cts?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    // ------------------------------------------------------------------------------------------------ accept

    private async Task AcceptLoopAsync(ILoopbackListener listener, Func<TcpClient, CancellationToken, Task> handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception)
            {
                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(50, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }
            _ = Task.Run(() => RunAsync(client, handler, ct));
        }
    }

    /// <summary>Wraps one connection: loopback-only check, connection limit, tracking, and nothing escapes.</summary>
    private async Task RunAsync(TcpClient client, Func<TcpClient, CancellationToken, Task> handler, CancellationToken ct)
    {
        try
        {
            if (!IsLoopbackPeer(client)) { Interlocked.Increment(ref _rejected); return; }
            if (Volatile.Read(ref _active) >= _options.MaxConnections) { Interlocked.Increment(ref _rejected); return; }
            _live[client] = 0;
            if (ct.IsCancellationRequested) return;
            await handler(client, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Handlers catch their own errors; this is the last net so a bug can never become an unobserved task exception.
            Interlocked.Increment(ref _failed);
            _log("error: " + ex.Message);
        }
        finally
        {
            _live.TryRemove(client, out _);
            CloseQuietly(client);
        }
    }

    // ------------------------------------------------------------------------------------------------ proxy port

    /// <summary>One connection on the proxy port. Public to the tests; the engine calls it for every accepted client.</summary>
    internal async Task HandleProxyAsync(TcpClient client, CancellationToken ct)
    {
        Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _total);
        TcpClient? upstream = null;
        try
        {
            client.NoDelay = true;
            var cs = client.GetStream();
            HttpHead? head;
            using (var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                headTimeout.CancelAfter(_options.HeadTimeout);
                head = await HttpHead.ReadAsync(cs, null, headTimeout.Token).ConfigureAwait(false);
            }
            if (head is null) return;   // closed without a request, too long, or too slow: nothing to answer

            var request = head.Request;
            if (request is null) { await RejectAsync(cs, "400 Bad Request", "bad request line", ct).ConfigureAwait(false); return; }

            if (request.Method == "CONNECT")
            {
                if (!HttpHead.TrySplitHostPort(request.Target, 443, out var host, out var port))
                { await RejectAsync(cs, "400 Bad Request", "bad CONNECT target", ct).ConfigureAwait(false); return; }
                if (!_domains.Contains(host))
                { await RejectAsync(cs, "403 Forbidden", "CONNECT to a host that is not on the Steam list: " + Clip(host), ct).ConfigureAwait(false); return; }

                upstream = await _dialer.ConnectAsync(host, port, false, ct).ConfigureAwait(false);
                _live[upstream] = 0;
                var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                await cs.WriteAsync(ok, ct).ConfigureAwait(false);
                var us = upstream.GetStream();

                // The first bytes the client sends are the TLS hello. Cut the first two bytes off: the middlebox that matches the
                // SNI does not recognise a record whose header is split, and the server just sees a normal TCP stream.
                var buf = new byte[16384];
                int n;
                if (head.Leftover.Length > 0) { n = Math.Min(head.Leftover.Length, buf.Length); Array.Copy(head.Leftover, buf, n); }
                else n = await cs.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n <= 0) return;
                if (buf[0] == 0x16 && n > 2)
                {
                    await us.WriteAsync(buf.AsMemory(0, 2), ct).ConfigureAwait(false);
                    await us.FlushAsync(ct).ConfigureAwait(false);
                    await Task.Delay(_options.TlsSplitDelay, ct).ConfigureAwait(false);
                    await us.WriteAsync(buf.AsMemory(2, n - 2), ct).ConfigureAwait(false);
                    Note(host, "TLS split");
                }
                else
                {
                    await us.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    Note(host, "tunnel");
                }
                await Task.WhenAll(PumpAsync(cs, us, client, upstream, ct), PumpAsync(us, cs, upstream, client, ct)).ConfigureAwait(false);
            }
            else if (request.Target.StartsWith('/'))
            {
                await ServeLocalAsync(cs, request.Target, ct).ConfigureAwait(false);
            }
            else
            {
                if (!Uri.TryCreate(request.Target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.Host.Length == 0 || uri.UserInfo.Length > 0)
                { await RejectAsync(cs, "400 Bad Request", "bad request target", ct).ConfigureAwait(false); return; }
                if (!_domains.Contains(uri.Host))
                { await RejectAsync(cs, "403 Forbidden", "request to a host that is not on the Steam list: " + Clip(uri.Host), ct).ConfigureAwait(false); return; }

                upstream = await _dialer.ConnectAsync(uri.Host, uri.Port, false, ct).ConfigureAwait(false);
                _live[upstream] = 0;
                var us = upstream.GetStream();
                var text = new StringBuilder();
                text.Append(request.Method).Append(' ').Append(uri.PathAndQuery).Append(' ').Append(request.Version).Append("\r\n");
                foreach (var line in head.Lines.Skip(1))
                {
                    if (StartsWithHeader(line, "proxy-connection") || StartsWithHeader(line, "connection") || StartsWithHeader(line, "keep-alive")) continue;
                    text.Append(StartsWithHeader(line, "host") ? "HoSt:" + line[5..] : line).Append("\r\n");   // the filter matches "Host:" exactly
                }
                text.Append("Connection: close\r\n\r\n");
                await us.WriteAsync(Encoding.Latin1.GetBytes(text.ToString()), ct).ConfigureAwait(false);
                if (head.Leftover.Length > 0) await us.WriteAsync(head.Leftover, ct).ConfigureAwait(false);
                Note(uri.Host, "HTTP host rewrite");
                await Task.WhenAll(PumpAsync(cs, us, client, upstream, ct), PumpAsync(us, cs, upstream, client, ct)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* stopping, or a slow client */ }
        catch (DnsMissException dns)
        {
            if (dns.Expected) Note(dns.Host, "no IPv4 address (expected for Steam's IPv6 check)");
            else Fail("error: " + dns.Message);
        }
        catch (ForbiddenTargetException forbidden)
        {
            Interlocked.Increment(ref _rejected);
            _verbose("refused: " + forbidden.Message);
        }
        catch (Exception ex)
        {
            Fail("error: " + ex.Message);
        }
        finally
        {
            if (upstream is not null) { _live.TryRemove(upstream, out _); CloseQuietly(upstream); }
            CloseQuietly(client);
            Interlocked.Decrement(ref _active);
        }
    }

    // ------------------------------------------------------------------------------------------------ download port

    /// <summary>
    /// One connection on 127.0.0.1:80. The ISP's resolver answers 127.0.0.1 for Steam's content hosts and Steam's downloader ignores the
    /// system proxy, so it connects here. The real address is resolved over DoH, the request goes out with the Host header respelled, and
    /// the connection stays open (Steam sends many requests to many cache hosts over one connection, so the upstream switches per request).
    /// </summary>
    internal async Task HandleDownloadAsync(TcpClient client, CancellationToken ct)
    {
        Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _total);
        TcpClient? up = null;
        Task? responsePump = null;
        var generation = new int[1];
        try
        {
            client.NoDelay = true;
            var cs = client.GetStream();
            NetworkStream? us = null;
            string? upHost = null;
            byte[]? pending = null;
            bool first = true;
            while (!ct.IsCancellationRequested)
            {
                HttpHead? head;
                using (var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    // The first request must arrive soon; between requests a kept-alive connection may be idle for as long as it likes.
                    if (first) headTimeout.CancelAfter(_options.HeadTimeout);
                    head = await HttpHead.ReadAsync(cs, pending, headTimeout.Token).ConfigureAwait(false);
                }
                first = false;
                if (head is null) break;
                pending = head.Leftover;

                var request = head.Request;
                string? hostHeader = head.Header("host");
                if (request is null || string.IsNullOrEmpty(hostHeader) || !HttpHead.TrySplitHostPort(hostHeader, 80, out var host, out _))
                { await RejectAsync(cs, "400 Bad Request", "request without a usable Host header", ct).ConfigureAwait(false); return; }

                long contentLength = 0;
                var lengthText = head.Header("content-length");
                if (lengthText is not null && (!long.TryParse(lengthText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out contentLength)))
                { await RejectAsync(cs, "400 Bad Request", "bad Content-Length", ct).ConfigureAwait(false); return; }
                bool chunked = head.Header("transfer-encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true;

                if (up is null || !string.Equals(host, upHost, StringComparison.OrdinalIgnoreCase))
                {
                    // Switch upstream. Steam sends the next request only after the previous response is complete, so the old one can go.
                    generation[0]++;
                    if (up is not null) { _live.TryRemove(up, out _); CloseQuietly(up); }
                    upHost = host;
                    up = await _dialer.ConnectAsync(host, 80, true, ct).ConfigureAwait(false);
                    _live[up] = 0;
                    us = up.GetStream();
                    responsePump = ResponsePumpAsync(up, client, generation, generation[0]);
                    Note(host, _domains.Contains(host) ? "HTTP via loopback (real IP over DoH)" : "HTTP via loopback, host is not on the Steam list (real IP over DoH)");
                }

                var text = new StringBuilder();
                var lines = head.Lines;
                for (int i = 0; i < lines.Length; i++)
                    text.Append(i > 0 && StartsWithHeader(lines[i], "host") ? "HoSt:" + lines[i][5..] : lines[i]).Append("\r\n");
                text.Append("\r\n");
                await us!.WriteAsync(Encoding.Latin1.GetBytes(text.ToString()), ct).ConfigureAwait(false);

                if (chunked)
                {
                    // A chunked request body cannot be framed here: hand the rest of the stream over untouched.
                    if (pending.Length > 0) await us.WriteAsync(pending, ct).ConfigureAwait(false);
                    await PumpAsync(cs, us, client, up, ct).ConfigureAwait(false);
                    break;
                }

                long remaining = contentLength;
                if (remaining > 0 && pending.Length > 0)
                {
                    int take = (int)Math.Min(remaining, pending.Length);
                    await us.WriteAsync(pending.AsMemory(0, take), ct).ConfigureAwait(false);
                    remaining -= take;
                    pending = pending[take..];
                }
                var body = new byte[16384];
                while (remaining > 0)
                {
                    int n = await cs.ReadAsync(body.AsMemory(0, (int)Math.Min(body.Length, remaining)), ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    await us.WriteAsync(body.AsMemory(0, n), ct).ConfigureAwait(false);
                    remaining -= n;
                }
            }
            if (up is not null) { try { up.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { } }
            if (responsePump is not null)
            {
                try { await responsePump.WaitAsync(_options.ResponseWait, ct).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (DnsMissException dns)
        {
            if (dns.Expected) Note(dns.Host, "no IPv4 address (expected for Steam's IPv6 check)");
            else Fail("error (loopback:80): " + dns.Message);
        }
        catch (ForbiddenTargetException forbidden)
        {
            Interlocked.Increment(ref _rejected);
            _verbose("refused (loopback:80): " + forbidden.Message);
        }
        catch (Exception ex)
        {
            Fail("error (loopback:80): " + ex.Message);
        }
        finally
        {
            if (up is not null) { _live.TryRemove(up, out _); CloseQuietly(up); }
            CloseQuietly(client);
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>Copies upstream to client for the current upstream only; a replaced upstream ends silently.</summary>
    private static async Task ResponsePumpAsync(TcpClient upstream, TcpClient client, int[] generation, int mine)
    {
        var buf = new byte[65536];
        try
        {
            var from = upstream.GetStream();
            var to = client.GetStream();
            int n;
            while ((n = await from.ReadAsync(buf).ConfigureAwait(false)) > 0)
            {
                if (generation[0] != mine) return;
                await to.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
            }
            if (generation[0] == mine) { try { client.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { } }
        }
        catch (Exception)
        {
            if (generation[0] == mine) CloseQuietly(client);
        }
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static async Task PumpAsync(NetworkStream from, NetworkStream to, TcpClient fromClient, TcpClient toClient, CancellationToken ct)
    {
        var buf = new byte[32768];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                await to.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            try { toClient.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { }
        }
        catch (Exception)
        {
            CloseQuietly(fromClient);
            CloseQuietly(toClient);
        }
    }

    private async Task ServeLocalAsync(NetworkStream stream, string path, CancellationToken ct)
    {
        string body, type = "text/plain", status = "200 OK";
        if (path == "/proxy.pac" || path.StartsWith("/proxy.pac?", StringComparison.Ordinal)) { body = _domains.BuildPac(ProxyPort); type = "application/x-ns-proxy-autoconfig"; }
        else if (path == "/") body = "Steam access proxy is running.";
        else { body = "not found"; status = "404 Not Found"; }
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    /// <summary>Answers a request that is not ours to serve. Counts as rejected, not as an error: a scanner must not look like a failure.</summary>
    private async Task RejectAsync(NetworkStream stream, string status, string why, CancellationToken ct)
    {
        Interlocked.Increment(ref _rejected);
        _verbose("rejected: " + why);
        try
        {
            var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (Exception) { /* the client is gone */ }
    }

    private void Fail(string message)
    {
        Interlocked.Increment(ref _failed);
        _log(message);
    }

    /// <summary>One debug line per distinct host and kind.</summary>
    private void Note(string host, string what)
    {
        lock (_seen) { if (!_seen.Add(host + "|" + what)) return; }
        _verbose(Clip(host) + "  " + what);
    }

    /// <summary>False only when the peer is known and is not on the loopback interface (defence in depth: the listener is loopback already).</summary>
    private static bool IsLoopbackPeer(TcpClient client)
    {
        try { return client.Client.RemoteEndPoint is not IPEndPoint remote || IPAddress.IsLoopback(remote.Address); }
        catch (Exception) { return true; }   // already disconnected: the handler will see the end of the stream
    }

    private static bool StartsWithHeader(string line, string name) =>
        line.Length > name.Length && line[name.Length] == ':' && line.StartsWith(name, StringComparison.OrdinalIgnoreCase);

    private static string Clip(string text) => text.Length <= 80 ? text : text[..80] + "...";

    private static void CloseQuietly(TcpClient client)
    {
        try { client.Close(); } catch (Exception) { }
    }
}

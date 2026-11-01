using System.Net;
using System.Net.Sockets;
using System.Text;
using Rigbay.SteamBypass.Net;
using Rigbay.SteamBypass.Proxy;

namespace Rigbay.SteamBypass.Tests;

/// <summary>
/// The real proxy engine on real loopback sockets, on ephemeral ports chosen by the system (never 80 or 18081), with a fake upstream
/// server of the test's own. Nothing here is reachable from another computer, and nothing touches the Windows settings.
/// </summary>
public class ProxyEngineTests
{
    /// <summary>A server on loopback that plays the real Steam server: records what it receives, answers with a fixed response.</summary>
    private sealed class Upstream : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public readonly List<byte[]> Reads = new();
        public readonly List<string> Heads = new();
        public string Response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
        public int Accepted;
        private readonly CancellationTokenSource _cts = new();

        public Upstream()
        {
            _listener.Start();
            _ = Task.Run(Loop);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch (Exception) { return; }
                Interlocked.Increment(ref Accepted);
                _ = Task.Run(() => Serve(c));
            }
        }

        private async Task Serve(TcpClient c)
        {
            try
            {
                c.NoDelay = true;
                var s = c.GetStream();
                var buf = new byte[8192];
                var all = new StringBuilder();
                while (true)
                {
                    int n = await s.ReadAsync(buf, _cts.Token);
                    if (n <= 0) return;
                    lock (Reads) Reads.Add(buf.AsSpan(0, n).ToArray());
                    all.Append(Encoding.Latin1.GetString(buf, 0, n));
                    var text = all.ToString();
                    int end;
                    while ((end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                    {
                        lock (Heads) Heads.Add(text[..(end + 4)]);
                        text = text[(end + 4)..];
                        all.Clear().Append(text);
                        await s.WriteAsync(Encoding.Latin1.GetBytes(Response), _cts.Token);
                    }
                }
            }
            catch (Exception) { }
            finally { c.Dispose(); }
        }

        public void Dispose() { _cts.Cancel(); _listener.Stop(); }
    }

    private sealed class Rig : IDisposable
    {
        public readonly Upstream Server = new();
        public readonly FakeDialer Dialer = new();
        public readonly ProxyEngine Engine;
        public readonly List<string> Logged = new();

        public Rig(ProxyOptions? options = null, ILoopbackListenerFactory? listeners = null)
        {
            Dialer.Connect = async (_, _) =>
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, Server.Port);
                c.NoDelay = true;
                return c;
            };
            Engine = new ProxyEngine(DomainSet.Default(), Dialer, listeners ?? new LoopbackListenerFactory(),
                options ?? new ProxyOptions { ProxyPort = 0, DownloadPort = 0, TlsSplitDelay = TimeSpan.FromMilliseconds(150), HeadTimeout = TimeSpan.FromMilliseconds(600) },
                log: m => { lock (Logged) Logged.Add(m); });
            Engine.Start();
        }

        public async Task<TcpClient> ConnectProxy() { var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, Engine.ProxyPort); c.NoDelay = true; return c; }
        public async Task<TcpClient> ConnectDownload() { var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, Engine.DownloadPort); c.NoDelay = true; return c; }

        public void Dispose() { Engine.Dispose(); Server.Dispose(); }
    }

    private static async Task<string> ReadAllText(TcpClient c, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var ms = new MemoryStream();
        var buf = new byte[4096];
        try
        {
            var s = c.GetStream();   // throws when the server has already reset the connection
            int n;
            while ((n = await s.ReadAsync(buf, cts.Token)) > 0) ms.Write(buf, 0, n);
        }
        catch (Exception) { }
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    private static async Task Send(TcpClient c, string text) => await c.GetStream().WriteAsync(Encoding.Latin1.GetBytes(text));

    private static async Task<string> ReadUntil(TcpClient c, string marker, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var sb = new StringBuilder();
        var buf = new byte[4096];
        var s = c.GetStream();
        while (!sb.ToString().Contains(marker, StringComparison.Ordinal))
        {
            int n = await s.ReadAsync(buf, cts.Token);
            if (n <= 0) break;
            sb.Append(Encoding.Latin1.GetString(buf, 0, n));
        }
        return sb.ToString();
    }

    // ---- where it listens -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_listener_is_bound_to_127_0_0_1_only()
    {
        using var rig = new Rig();
        var bound = rig.Engine.BoundEndPoints;
        Assert.Equal(2, bound.Count);
        Assert.All(bound, ep =>
        {
            Assert.Equal(IPAddress.Loopback, ep.Address);
            Assert.NotEqual(IPAddress.Any, ep.Address);
            Assert.NotEqual(IPAddress.IPv6Any, ep.Address);
        });
        Assert.NotEqual(0, rig.Engine.ProxyPort);
        Assert.NotEqual(rig.Engine.ProxyPort, rig.Engine.DownloadPort);
    }

    [Fact]
    public void The_real_listener_factory_has_no_way_to_name_another_address()
    {
        var factory = new LoopbackListenerFactory();
        using var l = factory.Listen(0);
        Assert.Equal(IPAddress.Loopback, l.LocalEndPoint.Address);
        Assert.Single(typeof(ILoopbackListenerFactory).GetMethods());
        Assert.Equal(new[] { typeof(int) }, typeof(ILoopbackListenerFactory).GetMethod("Listen")!.GetParameters().Select(p => p.ParameterType).ToArray());
        // Only Listeners.cs opens a listening socket, and it binds the loopback address.
        foreach (var file in Directory.EnumerateFiles(DohTests.SourceRoot(), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\")))
        {
            var text = File.ReadAllText(file);
            if (Path.GetFileName(file) == "Listeners.cs")
            {
                Assert.Contains("new TcpListener(IPAddress.Loopback, port)", text);
                Assert.DoesNotContain("Any", text);
            }
            else
            {
                Assert.DoesNotContain("TcpListener", text);
                Assert.DoesNotContain("HttpListener", text);
            }
        }
    }

    [Fact]
    public void The_listener_is_exclusive_a_second_bind_to_the_same_port_fails()
    {
        // Two engines cannot share a port: the second bind fails instead of silently sharing it.
        var factory = new LoopbackListenerFactory();
        using var first = factory.Listen(0);
        Assert.ThrowsAny<SocketException>(() => factory.Listen(first.LocalEndPoint.Port));
    }

    [Fact]
    public void Port_80_unavailable_is_not_fatal_and_says_why()
    {
        var fake = new FakeListenerFactory();
        fake.Taken.Add(80);
        using var rig = new Rig(new ProxyOptions(), fake);
        Assert.True(rig.Engine.IsRunning);
        Assert.NotNull(rig.Engine.DownloadsError);
        Assert.Equal(0, rig.Engine.DownloadPort);
        Assert.Equal(18081, rig.Engine.ProxyPort);
    }

    [Fact]
    public void The_proxy_port_unavailable_throws_and_leaves_the_engine_stopped()
    {
        var fake = new FakeListenerFactory();
        fake.Taken.Add(18081);
        var engine = new ProxyEngine(DomainSet.Default(), new FakeDialer(), fake, new ProxyOptions());
        Assert.Throws<SocketException>(engine.Start);
        Assert.False(engine.IsRunning);
        Assert.Equal(new[] { 18081 }, fake.Requested);   // port 80 is not even tried
    }

    // ---- the PAC --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_pac_is_served_with_the_port_it_listens_on()
    {
        using var rig = new Rig();
        using var c = await rig.ConnectProxy();
        await Send(c, "GET /proxy.pac HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        var text = await ReadAllText(c);
        Assert.StartsWith("HTTP/1.1 200 OK", text);
        Assert.Contains("application/x-ns-proxy-autoconfig", text);
        Assert.Contains($"PROXY 127.0.0.1:{rig.Engine.ProxyPort}; DIRECT", text);
        Assert.Contains("'steampowered.com'", text);
        Assert.Equal($"http://127.0.0.1:{rig.Engine.ProxyPort}/proxy.pac", rig.Engine.PacUrl);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1", "200 OK")]
    [InlineData("GET /nothing HTTP/1.1", "404 Not Found")]
    [InlineData("GET /proxy.pac?x=1 HTTP/1.1", "200 OK")]
    public async Task Local_paths_answer_without_touching_the_network(string requestLine, string status)
    {
        using var rig = new Rig();
        using var c = await rig.ConnectProxy();
        await Send(c, requestLine + "\r\nHost: a\r\n\r\n");
        Assert.Contains(status, await ReadAllText(c));
        Assert.Empty(rig.Dialer.Calls);
    }

    // ---- HTTPS tunnels --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tls_hello_is_cut_after_the_first_two_bytes()
    {
        using var rig = new Rig();
        using var c = await rig.ConnectProxy();
        await Send(c, "CONNECT store.steampowered.com:443 HTTP/1.1\r\nHost: store.steampowered.com:443\r\n\r\n");
        Assert.Contains("200 Connection Established", await ReadUntil(c, "\r\n\r\n"));

        var hello = new byte[] { 0x16, 0x03, 0x01, 0x00, 0x20, 1, 2, 3, 4, 5, 6, 7, 8 };
        await c.GetStream().WriteAsync(hello);
        Assert.True(Wait.Until(() => { lock (rig.Server.Reads) return rig.Server.Reads.Sum(r => r.Length) >= hello.Length; }));
        lock (rig.Server.Reads)
        {
            Assert.Equal(2, rig.Server.Reads[0].Length);                 // the first segment is the first two bytes ...
            Assert.Equal(new byte[] { 0x16, 0x03 }, rig.Server.Reads[0]);
            Assert.Equal(hello, rig.Server.Reads.SelectMany(r => r).ToArray());   // ... and the stream the server sees is the original, in order
        }
        Assert.Equal(("store.steampowered.com", 443, false), rig.Dialer.Calls.Single());
    }

    [Fact]
    public async Task Bytes_that_are_not_a_tls_hello_go_through_in_one_piece()
    {
        using var rig = new Rig();
        using var c = await rig.ConnectProxy();
        await Send(c, "CONNECT api.steampowered.com:8080 HTTP/1.1\r\n\r\n");
        await ReadUntil(c, "\r\n\r\n");
        await Send(c, "hello over a tunnel");
        Assert.True(Wait.Until(() => { lock (rig.Server.Reads) return rig.Server.Reads.Sum(r => r.Length) >= 19; }));
        lock (rig.Server.Reads) Assert.Equal(19, rig.Server.Reads[0].Length);
        Assert.Equal(8080, rig.Dialer.Calls.Single().Port);
    }

    [Fact]
    public async Task A_hello_that_arrives_with_the_connect_request_is_split_too()
    {
        using var rig = new Rig();
        using var c = await rig.ConnectProxy();
        var request = Encoding.Latin1.GetBytes("CONNECT x.steamcontent.com:443 HTTP/1.1\r\n\r\n");
        var hello = new byte[] { 0x16, 0x03, 0x01, 9, 9, 9, 9 };
        await c.GetStream().WriteAsync(request.Concat(hello).ToArray());   // one packet: head and hello together
        await ReadUntil(c, "\r\n\r\n");
        Assert.True(Wait.Until(() => { lock (rig.Server.Reads) return rig.Server.Reads.Sum(r => r.Length) >= hello.Length; }));
        lock (rig.Server.Reads) Assert.Equal(2, rig.Server.Reads[0].Length);
    }

    [Fact]
    public async Task Data_flows_back_from_the_upstream_to_the_client()
    {
        using var rig = new Rig();
        using var c = await rig.ConnectProxy();
        await Send(c, "CONNECT img.steamstatic.com:80 HTTP/1.1\r\n\r\n");
        await ReadUntil(c, "\r\n\r\n");
        await Send(c, "GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.Contains("200 OK", await ReadUntil(c, "ok"));
    }

    [Theory]
    [InlineData("CONNECT example.com:443 HTTP/1.1", "403 Forbidden")]
    [InlineData("CONNECT 127.0.0.1:443 HTTP/1.1", "403 Forbidden")]
    [InlineData("CONNECT 192.168.1.1:443 HTTP/1.1", "403 Forbidden")]
    [InlineData("CONNECT steampowered.com.evil.example:443 HTTP/1.1", "403 Forbidden")]
    [InlineData("CONNECT store.steampowered.com:0 HTTP/1.1", "400 Bad Request")]
    [InlineData("CONNECT store.steampowered.com:99999 HTTP/1.1", "400 Bad Request")]
    [InlineData("CONNECT store.steampowered.com:abc HTTP/1.1", "400 Bad Request")]
    [InlineData("CONNECT HTTP/1.1", "400 Bad Request")]
    [InlineData("CONNECT  HTTP/1.1", "400 Bad Request")]
    public async Task A_tunnel_only_opens_to_a_listed_host_on_a_valid_port(string requestLine, string status)
    {
        using var rig = new Rig();
        using var c = await rig.ConnectProxy();
        await Send(c, requestLine + "\r\n\r\n");
        Assert.Contains(status, await ReadAllText(c));
        Assert.Empty(rig.Dialer.Calls);                       // nothing was dialled
        Assert.Equal(0, rig.Engine.Counters.Failed);          // a refusal is not an error
        Assert.Equal(1, rig.Engine.Counters.Rejected);
    }

    // ---- plain HTTP through the proxy -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_proxied_http_request_gets_its_host_header_respelled_and_connection_close()
    {
        using var rig = new Rig();
        using var c = await rig.ConnectProxy();
        await Send(c, "GET http://store.steampowered.com/app/10?l=en HTTP/1.1\r\nHost: store.steampowered.com\r\nProxy-Connection: keep-alive\r\nConnection: keep-alive\r\nKeep-Alive: 300\r\nAccept: text/html\r\n\r\n");
        var reply = await ReadAllText(c);
        Assert.Contains("200 OK", reply);
        string head;
        Assert.True(Wait.Until(() => { lock (rig.Server.Heads) return rig.Server.Heads.Count > 0; }));
        lock (rig.Server.Heads) head = rig.Server.Heads[0];
        Assert.StartsWith("GET /app/10?l=en HTTP/1.1\r\n", head);          // absolute form turned into origin form
        Assert.Contains("\r\nHoSt: store.steampowered.com\r\n", head);     // the exact spelling the filter does not match
        Assert.DoesNotContain("\r\nHost:", head);
        Assert.DoesNotContain("Proxy-Connection", head);
        Assert.DoesNotContain("Keep-Alive", head);
        Assert.Contains("\r\nAccept: text/html\r\n", head);
        Assert.EndsWith("Connection: close\r\n\r\n", head);
        Assert.Equal(("store.steampowered.com", 80, false), rig.Dialer.Calls.Single());
    }

    [Theory]
    [InlineData("GET http://example.com/ HTTP/1.1", "403 Forbidden")]
    [InlineData("GET http://127.0.0.1/ HTTP/1.1", "403 Forbidden")]
    [InlineData("GET http://user:pw@store.steampowered.com/ HTTP/1.1", "400 Bad Request")]
    [InlineData("GET https://store.steampowered.com/ HTTP/1.1", "400 Bad Request")]
    [InlineData("GET ftp://store.steampowered.com/ HTTP/1.1", "400 Bad Request")]
    [InlineData("GET http:// HTTP/1.1", "400 Bad Request")]
    [InlineData("GET relative HTTP/1.1", "400 Bad Request")]
    [InlineData("NOT-A-REQUEST", "400 Bad Request")]
    [InlineData("get / http/1.1", "400 Bad Request")]
    public async Task Only_listed_http_hosts_in_absolute_form_are_forwarded(string requestLine, string status)
    {
        using var rig = new Rig();
        using var c = await rig.ConnectProxy();
        await Send(c, requestLine + "\r\nHost: whatever\r\n\r\n");
        Assert.Contains(status, await ReadAllText(c));
        Assert.Empty(rig.Dialer.Calls);
        Assert.Equal(0, rig.Engine.Counters.Failed);
    }

    // ---- the download port ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_download_port_forwards_with_a_respelled_host_header_and_forces_doh()
    {
        using var rig = new Rig();
        using var c = await rig.ConnectDownload();
        await Send(c, "GET /depot/1/chunk/abc HTTP/1.1\r\nHost: cache8-hkg1.steamcontent.com\r\nAccept: */*\r\n\r\n");
        Assert.Contains("200 OK", await ReadUntil(c, "ok"));
        string head;
        lock (rig.Server.Heads) head = rig.Server.Heads[0];
        Assert.Contains("\r\nHoSt: cache8-hkg1.steamcontent.com\r\n", head);
        Assert.DoesNotContain("\r\nHost:", head);
        Assert.StartsWith("GET /depot/1/chunk/abc HTTP/1.1\r\n", head);
        Assert.DoesNotContain("Connection: close", head);   // this path keeps the connection alive for speed
        Assert.Equal(("cache8-hkg1.steamcontent.com", 80, true), rig.Dialer.Calls.Single());
    }

    [Fact]
    public async Task One_download_connection_can_carry_requests_for_several_cache_hosts()
    {
        using var rig = new Rig();
        using var c = await rig.ConnectDownload();
        foreach (var host in new[] { "cache1-a.steamcontent.com", "cache2-b.steamcontent.com", "cache1-a.steamcontent.com" })
        {
            await Send(c, $"GET /x HTTP/1.1\r\nHost: {host}\r\n\r\n");
            Assert.Contains("200 OK", await ReadUntil(c, "ok"));
        }
        Assert.Equal(new[] { "cache1-a.steamcontent.com", "cache2-b.steamcontent.com", "cache1-a.steamcontent.com" }, rig.Dialer.Calls.Select(x => x.Host).ToArray());
        Assert.All(rig.Dialer.Calls, x => Assert.True(x.ForceDoh));
        Assert.Equal(0, rig.Engine.Counters.Failed);
    }

    [Fact]
    public async Task Several_requests_to_the_same_host_reuse_the_upstream_connection()
    {
        using var rig = new Rig();
        rig.Server.Response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok";   // keep-alive response
        using var c = await rig.ConnectDownload();
        for (int i = 0; i < 3; i++)
        {
            await Send(c, $"GET /{i} HTTP/1.1\r\nHost: cache1.steamcontent.com:80\r\n\r\n");   // a port in Host is stripped
            Assert.Contains("200 OK", await ReadUntil(c, "ok"));
        }
        Assert.Single(rig.Dialer.Calls);
        Assert.Equal(1, rig.Server.Accepted);
        Assert.Equal(3, rig.Server.Heads.Count);
        Assert.All(rig.Server.Heads, h => Assert.Contains("HoSt: cache1.steamcontent.com:80", h));
    }

    [Fact]
    public async Task A_request_body_with_content_length_is_forwarded_after_the_head()
    {
        using var rig = new Rig();
        using var c = await rig.ConnectDownload();
        await Send(c, "POST /upload HTTP/1.1\r\nHost: cache1.steamcontent.com\r\nContent-Length: 11\r\n\r\nhello world");
        await ReadUntil(c, "ok");
        Assert.True(Wait.Until(() => { lock (rig.Server.Reads) return string.Concat(rig.Server.Reads.Select(r => Encoding.Latin1.GetString(r))).EndsWith("hello world"); }));
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\n\r\n")]                                                   // no Host
    [InlineData("GET / HTTP/1.1\r\nHost: \r\n\r\n")]                                          // empty Host
    [InlineData("GET / HTTP/1.1\r\nHost: exa mple\r\n\r\n")]                                  // space in the name
    [InlineData("GET / HTTP/1.1\r\nHost: a.b:99999\r\n\r\n")]                                 // bad port
    [InlineData("GET / HTTP/1.1\r\nHost: a.steamcontent.com\r\nContent-Length: -5\r\n\r\n")]  // negative length
    [InlineData("GET / HTTP/1.1\r\nHost: a.steamcontent.com\r\nContent-Length: abc\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a.steamcontent.com\r\nContent-Length: 99999999999999999999\r\n\r\n")]
    [InlineData("garbage\r\n\r\n")]
    public async Task A_bad_download_request_is_answered_with_400_and_is_not_an_error(string request)
    {
        using var rig = new Rig();
        using var c = await rig.ConnectDownload();
        await Send(c, request);
        Assert.Contains("400 Bad Request", await ReadAllText(c));
        Assert.Empty(rig.Dialer.Calls);
        Assert.Equal(0, rig.Engine.Counters.Failed);
    }

    [Fact]
    public async Task A_download_request_for_loopback_is_never_connected_to_itself()
    {
        // Host: 127.0.0.1 on our own port 80 would connect the forwarder to the forwarder. The real dialer refuses it.
        var resolver = new FakeResolver();
        var dialer = new UpstreamDialer(resolver, DomainSet.Default());
        using var engine = new ProxyEngine(DomainSet.Default(), dialer, new LoopbackListenerFactory(), new ProxyOptions { ProxyPort = 0, DownloadPort = 0 });
        engine.Start();
        foreach (var host in new[] { "127.0.0.1", "localhost", "[::1]", "0.0.0.0" })
        {
            resolver.Map["localhost"] = new[] { IPAddress.Loopback };
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, engine.DownloadPort);
            await Send(c, $"GET / HTTP/1.1\r\nHost: {host}\r\n\r\n");
            await ReadAllText(c, 2000);
        }
        Assert.True(Wait.Until(() => engine.Counters.Active == 0));
        var counters = engine.Counters;
        Assert.Equal(0, counters.Failed);
        Assert.True(counters.Rejected >= 3);
    }

    // ---- errors are counted, refusals are not ---------------------------------------------------------------------------------

    [Fact]
    public async Task An_unreachable_upstream_is_one_error_and_the_connection_is_closed()
    {
        using var rig = new Rig();
        rig.Dialer.Throws = new IOException("cannot connect to x");
        using var c = await rig.ConnectProxy();
        await Send(c, "CONNECT store.steampowered.com:443 HTTP/1.1\r\n\r\n");
        await ReadAllText(c);
        Assert.True(Wait.Until(() => rig.Engine.Counters.Failed == 1 && rig.Engine.Counters.Active == 0));
        lock (rig.Logged) Assert.Contains(rig.Logged, l => l.StartsWith("error:"));
    }

    [Fact]
    public async Task An_expected_dns_miss_is_a_note_not_an_error_and_an_unexpected_one_is_an_error()
    {
        using var rig = new Rig();
        rig.Dialer.Throws = new DnsMissException("ipv6check-http.steamserver.net");
        using (var c = await rig.ConnectProxy()) { await Send(c, "CONNECT ipv6check-http.steamserver.net:80 HTTP/1.1\r\n\r\n"); await ReadAllText(c); }
        Assert.True(Wait.Until(() => rig.Engine.Counters.Active == 0));
        Assert.Equal(0, rig.Engine.Counters.Failed);

        rig.Dialer.Throws = new DnsMissException("api.steampowered.com");
        using (var c = await rig.ConnectProxy()) { await Send(c, "CONNECT api.steampowered.com:443 HTTP/1.1\r\n\r\n"); await ReadAllText(c); }
        Assert.True(Wait.Until(() => rig.Engine.Counters.Failed == 1));
    }

    [Fact]
    public async Task The_active_count_goes_up_while_connected_and_back_to_zero_after()
    {
        using var rig = new Rig();
        var c = await rig.ConnectProxy();
        Assert.True(Wait.Until(() => rig.Engine.Counters.Active == 1));
        c.Dispose();
        Assert.True(Wait.Until(() => rig.Engine.Counters.Active == 0));
        Assert.Equal(1, rig.Engine.Counters.Total);
    }

    // ---- stopping -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stopping_closes_the_listeners_and_every_open_connection()
    {
        var rig = new Rig();
        var c = await rig.ConnectProxy();
        await Send(c, "CONNECT store.steampowered.com:443 HTTP/1.1\r\n\r\n");
        await ReadUntil(c, "\r\n\r\n");
        int proxyPort = rig.Engine.ProxyPort;
        rig.Engine.Stop();
        Assert.False(rig.Engine.IsRunning);
        Assert.Equal(0, rig.Engine.ProxyPort);
        Assert.Empty(rig.Engine.BoundEndPoints);
        Assert.Equal("", await ReadAllText(c, 3000));                                  // the tunnel was closed
        Assert.True(Wait.Until(() => rig.Engine.Counters.Active == 0));
        await Assert.ThrowsAnyAsync<SocketException>(async () => { using var again = new TcpClient(); await again.ConnectAsync(IPAddress.Loopback, proxyPort); });
        rig.Engine.Stop();   // twice is fine
        rig.Server.Dispose();
    }

    [Fact]
    public async Task The_engine_can_be_started_again_after_a_stop_and_keeps_its_counters()
    {
        using var rig = new Rig();
        using (var c = await rig.ConnectProxy()) { await Send(c, "GET / HTTP/1.1\r\n\r\n"); await ReadAllText(c); }
        Assert.True(Wait.Until(() => rig.Engine.Counters.Total == 1));
        rig.Engine.Stop();
        rig.Engine.Start();
        using (var c = await rig.ConnectProxy()) { await Send(c, "GET / HTTP/1.1\r\n\r\n"); Assert.Contains("200 OK", await ReadAllText(c)); }
        Assert.True(Wait.Until(() => rig.Engine.Counters.Total == 2));
    }

    // ---- hostile and malformed input: the proxy must keep working -------------------------------------------------------------

    private static async Task AssertStillServing(Rig rig)
    {
        using var c = await rig.ConnectProxy();
        await Send(c, "GET /proxy.pac HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.Contains("PROXY 127.0.0.1:", await ReadAllText(c));
        using var d = await rig.ConnectDownload();
        await Send(d, "GET / HTTP/1.1\r\nHost: cache1.steamcontent.com\r\n\r\n");
        Assert.Contains("200 OK", await ReadUntil(d, "ok"));
    }

    [Fact]
    public async Task Fuzz_hand_made_hostile_requests_never_crash_the_proxy()
    {
        using var rig = new Rig();
        var cases = new List<byte[]>
        {
            Array.Empty<byte>(),                                                           // connect and close
            new byte[] { 0 },
            new byte[] { 0x16, 0x03, 0x01 },                                               // a TLS hello with no request in front of it
            Encoding.Latin1.GetBytes("\r\n\r\n"),
            Encoding.Latin1.GetBytes("\r\n"),
            Encoding.Latin1.GetBytes("GET"),
            Encoding.Latin1.GetBytes("GET / HTTP/1.1"),                                    // no terminator: waits, then the head timeout closes it
            Encoding.Latin1.GetBytes("GET / HTTP/1.1\r\n\r\n"),
            Encoding.Latin1.GetBytes("GET /\0/ HTTP/1.1\r\n\r\n"),
            Encoding.Latin1.GetBytes("CONNECT \0:443 HTTP/1.1\r\n\r\n"),
            Encoding.Latin1.GetBytes("CONNECT store.steampowered.com:443\r\n\r\n"),         // no version
            Encoding.Latin1.GetBytes("CONNECT store.steampowered.com:443 HTTP/1.1 junk\r\n\r\n"),
            Encoding.Latin1.GetBytes("GET http://store.steampowered.com:99999999999/ HTTP/1.1\r\n\r\n"),
            Encoding.Latin1.GetBytes("GET http://[::1/ HTTP/1.1\r\n\r\n"),
            Encoding.Latin1.GetBytes("GET http://store.steampowered.com/\u00e9\u00e8 HTTP/1.1\r\nHost: \u00e9\r\n\r\n"),
            Encoding.Latin1.GetBytes("GET / HTTP/1.1\r\n" + string.Concat(Enumerable.Repeat("X-A: b\r\n", 5000)) + "\r\n"),   // many headers
            Encoding.Latin1.GetBytes("GET /" + new string('a', 70000) + " HTTP/1.1\r\n\r\n"),                              // head over 64 KB
            Encoding.Latin1.GetBytes("GET / HTTP/1.1\r\nHost\r\n: x\r\nHost:\r\n:\r\n\r\n"),
            Encoding.Latin1.GetBytes("GET / HTTP/1.1\r\r\r\n\n\n\r\n\r\n"),
            Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(),
            Enumerable.Repeat((byte)'A', 100000).ToArray(),
        };
        foreach (var bytes in cases)
        {
            foreach (var port in new[] { rig.Engine.ProxyPort, rig.Engine.DownloadPort })
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, port);
                try { await c.GetStream().WriteAsync(bytes); } catch (IOException) { }
                try { c.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
                await ReadAllText(c, 2000);   // the proxy answers or closes; it must not hang
            }
        }
        Assert.True(Wait.Until(() => rig.Engine.Counters.Active == 0, 8000), "connections were left open");
        await AssertStillServing(rig);
        Assert.DoesNotContain(rig.Dialer.Calls, c => !c.Host.EndsWith("steamcontent.com") && !c.Host.EndsWith("steampowered.com"));
    }

    [Fact]
    public async Task Fuzz_random_bytes_at_both_ports_with_a_fixed_seed()
    {
        using var rig = new Rig();
        var random = new Random(20261101);
        for (int i = 0; i < 60; i++)
        {
            var bytes = new byte[random.Next(0, 3000)];
            random.NextBytes(bytes);
            if (i % 3 == 0 && bytes.Length > 8)
            {
                // Make some of them look like the start of a real request, so the parser goes further before it meets the junk.
                var prefix = Encoding.Latin1.GetBytes(i % 2 == 0 ? "CONNECT store.steampowered.com:443 HTTP/1.1\r\n" : "GET http://store.steampowered.com/ HTTP/1.1\r\n");
                bytes = prefix.Concat(bytes).ToArray();
            }
            foreach (var port in new[] { rig.Engine.ProxyPort, rig.Engine.DownloadPort })
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, port);
                try { await c.GetStream().WriteAsync(bytes); } catch (IOException) { }
                try { c.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
                await ReadAllText(c, 1500);
            }
        }
        Assert.True(Wait.Until(() => rig.Engine.Counters.Active == 0, 10000), "connections were left open");
        await AssertStillServing(rig);
    }

    [Fact]
    public async Task Many_connections_that_say_nothing_are_closed_by_the_head_timeout()
    {
        using var rig = new Rig();
        var clients = new List<TcpClient>();
        for (int i = 0; i < 30; i++) clients.Add(await rig.ConnectProxy());
        Assert.True(Wait.Until(() => rig.Engine.Counters.Active == 30));
        Assert.True(Wait.Until(() => rig.Engine.Counters.Active == 0, 5000));   // HeadTimeout is 600 ms in this rig
        clients.ForEach(c => c.Dispose());
        await AssertStillServing(rig);
    }

    [Fact]
    public async Task More_connections_than_the_limit_are_closed_at_once_and_counted_as_rejected()
    {
        using var rig = new Rig(new ProxyOptions { ProxyPort = 0, DownloadPort = 0, MaxConnections = 3, HeadTimeout = TimeSpan.FromSeconds(5) });
        var held = new List<TcpClient>();
        for (int i = 0; i < 3; i++) held.Add(await rig.ConnectProxy());
        Assert.True(Wait.Until(() => rig.Engine.Counters.Active == 3));
        using var extra = await rig.ConnectProxy();
        Assert.Equal("", await ReadAllText(extra, 3000));
        Assert.True(Wait.Until(() => rig.Engine.Counters.Rejected >= 1));
        held.ForEach(c => c.Dispose());
        Assert.True(Wait.Until(() => rig.Engine.Counters.Active == 0));
    }

    [Fact]
    public async Task A_client_that_vanishes_in_the_middle_of_a_download_leaves_nothing_open()
    {
        using var rig = new Rig();
        var c = await rig.ConnectDownload();
        await Send(c, "POST /up HTTP/1.1\r\nHost: cache1.steamcontent.com\r\nContent-Length: 1000000\r\n\r\npartial");
        await Task.Delay(100);
        c.Client.LingerState = new LingerOption(true, 0);   // reset instead of a clean close
        c.Dispose();
        Assert.True(Wait.Until(() => rig.Engine.Counters.Active == 0, 8000));
    }
}

using System.Net;
using System.Net.Http;
using Rigbay.SteamBypass.Net;

namespace Rigbay.SteamBypass.Tests;

public class DohTests
{
    private const string Good = """{"Status":0,"Answer":[{"name":"x","type":5,"TTL":300,"data":"alias.example."},{"name":"x","type":1,"TTL":120,"data":"23.45.67.89"},{"name":"x","type":1,"TTL":60,"data":"23.45.67.90"}]}""";

    [Fact]
    public void A_records_are_read_and_other_types_skipped_and_the_smallest_ttl_wins()
    {
        var parsed = DohResolver.ParseAnswer(Good)!.Value;
        Assert.Equal(new[] { "23.45.67.89", "23.45.67.90" }, parsed.Addresses.Select(a => a.ToString()).ToArray());
        Assert.Equal(60, parsed.Ttl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("{\"Answer\":")]
    public void Garbage_is_not_an_answer(string json)
    {
        Assert.Null(DohResolver.ParseAnswer(json));
    }

    [Theory]
    [InlineData("""{"Status":3}""")]
    [InlineData("""{"Answer":"nope"}""")]
    [InlineData("""{"Answer":[1,"a",null,{"type":"1","data":"1.2.3.4"},{"type":1},{"type":1,"data":5},{"type":1,"data":"::1"},{"type":1,"data":"999.1.1.1"}]}""")]
    public void Odd_shapes_give_no_address_and_no_exception(string json)
    {
        var parsed = DohResolver.ParseAnswer(json);
        Assert.True(parsed is null || parsed.Value.Addresses.Length == 0);
    }

    private static DohResolver Resolver(Func<HttpRequestMessage, HttpResponseMessage> handler, Func<string, CancellationToken, Task<IPAddress[]>>? system = null, Func<DateTime>? now = null) =>
        new(new HttpClient(new FakeHandler(handler)), system, now);

    [Fact]
    public async Task The_first_endpoint_that_answers_wins_and_asks_for_dns_json()
    {
        var urls = new List<string>();
        var accept = new List<string>();
        var r = Resolver(req =>
        {
            urls.Add(req.RequestUri!.ToString());
            accept.AddRange(req.Headers.GetValues("Accept"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Good) };
        });
        var ips = await r.ResolveAsync("store.steampowered.com", true, CancellationToken.None);
        Assert.Equal(2, ips.Count);
        Assert.Single(urls);
        Assert.Equal("https://1.1.1.1/dns-query?name=store.steampowered.com&type=A", urls[0]);   // IP-literal endpoint first: no DNS needed to bootstrap
        Assert.Equal(new[] { "application/dns-json" }, accept);
    }

    [Fact]
    public async Task A_failing_endpoint_falls_through_to_the_next_one_in_order()
    {
        var urls = new List<string>();
        var r = Resolver(req =>
        {
            urls.Add(req.RequestUri!.Host);
            if (urls.Count == 1) throw new HttpRequestException("down");
            if (urls.Count == 2) return new HttpResponseMessage(HttpStatusCode.BadGateway);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Good) };
        });
        var ips = await r.ResolveAsync("a.steampowered.com", true, CancellationToken.None);
        Assert.Equal(2, ips.Count);
        Assert.Equal(new[] { "1.1.1.1", "8.8.8.8", "cloudflare-dns.com" }, urls);
    }

    [Fact]
    public async Task All_endpoints_failing_falls_back_to_the_system_resolver_without_its_loopback_answers()
    {
        var r = Resolver(_ => throw new HttpRequestException("down"),
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback, IPAddress.Parse("34.1.2.3"), IPAddress.Any, IPAddress.IPv6Any }));
        var ips = await r.ResolveAsync("x.steampowered.com", true, CancellationToken.None);
        Assert.Equal(new[] { "34.1.2.3" }, ips.Select(a => a.ToString()).ToArray());
    }

    [Fact]
    public async Task Only_a_poisoned_system_answer_is_a_dns_miss()
    {
        var r = Resolver(_ => throw new HttpRequestException("down"), (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        var ex = await Assert.ThrowsAsync<DnsMissException>(() => r.ResolveAsync("x.steampowered.com", true, CancellationToken.None));
        Assert.False(ex.Expected);
        var v6 = await Assert.ThrowsAsync<DnsMissException>(() => r.ResolveAsync("ipv6check-http.steamserver.net", true, CancellationToken.None));
        Assert.True(v6.Expected);   // Steam asks for names that have no IPv4 address on purpose: a note, not an error
    }

    [Fact]
    public async Task A_name_that_does_not_use_doh_never_leaves_through_it()
    {
        int dohCalls = 0;
        var r = Resolver(_ => { dohCalls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Good) }; },
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var ips = await r.ResolveAsync("example.org", useDoh: false, CancellationToken.None);
        Assert.Equal("93.184.216.34", ips.Single().ToString());
        Assert.Equal(0, dohCalls);
    }

    [Fact]
    public async Task An_ip_literal_is_returned_as_it_is_without_any_lookup()
    {
        int calls = 0;
        var r = Resolver(_ => { calls++; throw new HttpRequestException(); }, (_, _) => { calls++; throw new InvalidOperationException(); });
        Assert.Equal("1.2.3.4", (await r.ResolveAsync("1.2.3.4", true, CancellationToken.None)).Single().ToString());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Answers_are_cached_for_the_ttl_clamped_to_30_to_300_seconds()
    {
        int calls = 0;
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var r = Resolver(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Good) }; }, now: () => now);
        await r.ResolveAsync("c.steampowered.com", true, CancellationToken.None);
        await r.ResolveAsync("c.steampowered.com", true, CancellationToken.None);
        Assert.Equal(1, calls);
        now = now.AddSeconds(59);   // the smallest TTL in the answer is 60
        await r.ResolveAsync("c.steampowered.com", true, CancellationToken.None);
        Assert.Equal(1, calls);
        now = now.AddSeconds(2);
        await r.ResolveAsync("c.steampowered.com", true, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_cancelled_lookup_throws_cancellation_and_does_not_try_the_system_resolver()
    {
        bool systemAsked = false;
        var r = Resolver(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Good) }, (_, _) => { systemAsked = true; return Task.FromResult(Array.Empty<IPAddress>()); });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.ResolveAsync("d.steampowered.com", true, cts.Token));
        Assert.False(systemAsked);
    }

    [Fact]
    public async Task The_host_name_is_escaped_into_the_query()
    {
        string? url = null;
        var r = Resolver(req => { url = req.RequestUri!.ToString(); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Good) }; });
        await r.ResolveAsync("a&type=AAAA#x.steampowered.com", true, CancellationToken.None);
        Assert.Contains("name=a%26type%3DAAAA%23x.steampowered.com&type=A", url);
    }

    [Fact]
    public void The_endpoints_are_the_four_of_the_old_app_with_ip_literals_first()
    {
        Assert.Equal(new[] { "1.1.1.1", "8.8.8.8", "cloudflare-dns.com", "dns.google" },
            DohResolver.Endpoints.Select(e => new Uri(string.Format(e, "x")).Host).ToArray());
        Assert.All(DohResolver.Endpoints, e => Assert.StartsWith("https://", e));
    }

    [Fact]
    public void The_http_client_is_modern_tls_without_the_system_proxy_and_the_source_has_no_legacy_stack()
    {
        // The handler's settings are not readable from outside, so the source is checked: SocketsHttpHandler with UseProxy = false and
        // TLS 1.2/1.3, and none of the .NET Framework era settings the standalone app needed.
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "Net", "Resolver.cs"));
        Assert.Contains("UseProxy = false", source);
        Assert.Contains("SslProtocols.Tls12 | SslProtocols.Tls13", source);
        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("ServicePointManager", text);
            Assert.DoesNotContain("HttpWebRequest", text);
            Assert.DoesNotContain("ServerCertificateCustomValidationCallback", text);   // certificates are never switched off
            Assert.DoesNotContain("DangerousAcceptAnyServerCertificateValidator", text);
        }
        using var client = DohResolver.CreateHttpClient();
        Assert.Equal(TimeSpan.FromSeconds(5), client.Timeout);
    }

    internal static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Rigbay.SteamBypass"))) dir = dir.Parent;
        return dir is null ? throw new DirectoryNotFoundException("module source folder") : Path.Combine(dir.FullName, "src", "Rigbay.SteamBypass");
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}

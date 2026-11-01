using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace Rigbay.SteamBypass.Net;

/// <summary>No usable IPv4 address for a name. <see cref="Expected"/> is true for Steam's IPv6-only probe names.</summary>
internal sealed class DnsMissException : Exception
{
    public DnsMissException(string host) : base("DNS failed for " + host) => Host = host;

    /// <summary>The name that had no address.</summary>
    public string Host { get; }

    /// <summary>Steam asks for <c>ipv6check-*</c> names that have no A record on purpose; that is a note, not an error.</summary>
    public bool Expected => Host.StartsWith("ipv6check", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Turns a host name into addresses.</summary>
internal interface IHostResolver
{
    /// <summary>
    /// Resolves <paramref name="host"/>. With <paramref name="useDoh"/> the name goes to DNS-over-HTTPS first; without it only the
    /// system resolver is asked. An IP literal returns itself. Throws <see cref="DnsMissException"/> when nothing usable comes back.
    /// </summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, bool useDoh, CancellationToken ct);
}

/// <summary>
/// DNS-over-HTTPS client with a small cache. Only names the caller passes with <c>useDoh</c> leave through DoH. The request uses
/// <see cref="HttpClient"/> with TLS 1.2 or newer and never the Windows proxy (the PAC would send it back into our own proxy).
/// </summary>
internal sealed class DohResolver : IHostResolver
{
    /// <summary>Endpoints in order. IP-literal URLs first: they need no DNS to bootstrap.</summary>
    public static readonly IReadOnlyList<string> Endpoints = new[]
    {
        "https://1.1.1.1/dns-query?name={0}&type=A",
        "https://8.8.8.8/resolve?name={0}&type=A",
        "https://cloudflare-dns.com/dns-query?name={0}&type=A",
        "https://dns.google/resolve?name={0}&type=A",
    };

    private sealed record Entry(IPAddress[] Ips, DateTime Expires);

    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _systemDns;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the resolver. All three collaborators can be replaced in tests.</summary>
    /// <param name="http">The client for DoH requests; use <see cref="CreateHttpClient"/>.</param>
    /// <param name="systemDns">The system resolver (fallback, and the only one for names that do not use DoH).</param>
    /// <param name="utcNow">The clock.</param>
    public DohResolver(HttpClient http, Func<string, CancellationToken, Task<IPAddress[]>>? systemDns = null, Func<DateTime>? utcNow = null)
    {
        _http = http;
        _systemDns = systemDns ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// An <see cref="HttpClient"/> for DoH: TLS 1.2 and 1.3 only, no system proxy, 5 s to connect, 5 s per request, no redirects.
    /// Certificates are validated by the platform (nothing is disabled).
    /// </summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, bool useDoh, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal)) return new[] { literal };
        lock (_cache)
        {
            if (_cache.TryGetValue(host, out var hit) && hit.Expires > _utcNow()) return hit.Ips;
        }

        Entry? entry = null;
        if (useDoh) entry = await QueryDohAsync(host, ct).ConfigureAwait(false);
        if (entry is null)
        {
            // DoH was not asked for, or all four endpoints failed: the system resolver. A loopback or "any" answer is the
            // ISP's poisoned reply, not an address.
            var system = await _systemDns(host, ct).ConfigureAwait(false);
            var good = system.Where(a => !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).ToArray();
            if (good.Length == 0) throw new DnsMissException(host);
            entry = new Entry(good, _utcNow().AddSeconds(60));
        }
        lock (_cache) _cache[host] = entry;
        return entry.Ips;
    }

    private async Task<Entry?> QueryDohAsync(string host, CancellationToken ct)
    {
        foreach (var format in Endpoints)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, string.Format(format, Uri.EscapeDataString(host)));
                request.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (json.Length > 64 * 1024) continue;
                var answer = ParseAnswer(json);
                if (answer is { } a && a.Addresses.Length > 0)
                    return new Entry(a.Addresses, _utcNow().AddSeconds(Math.Clamp(a.Ttl, 30, 300)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* this endpoint failed: try the next */ }
        }
        return null;
    }

    /// <summary>
    /// Reads the A records of a DoH JSON answer (<c>{"Answer":[{"type":1,"TTL":60,"data":"1.2.3.4"}]}</c>). Records of other types
    /// (CNAME) and addresses that are not plain IPv4 are skipped. The TTL is the smallest of the A records (300 when none says).
    /// Returns null when the text is not the expected JSON.
    /// </summary>
    public static (IPAddress[] Addresses, int Ttl)? ParseAnswer(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array) return (Array.Empty<IPAddress>(), 300);
            var ips = new List<IPAddress>();
            int ttl = 300;
            foreach (var record in answers.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object) continue;
                if (!record.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Number || !type.TryGetInt32(out var t) || t != 1) continue;
                if (!record.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) continue;
                if (!IPAddress.TryParse(data.GetString(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) continue;
                ips.Add(ip);
                if (record.TryGetProperty("TTL", out var ttlValue) && ttlValue.ValueKind == JsonValueKind.Number && ttlValue.TryGetInt32(out var seconds))
                    ttl = Math.Min(ttl, seconds);
            }
            return (ips.ToArray(), ttl);
        }
        catch (JsonException) { return null; }
    }
}

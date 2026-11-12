using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Rigbay.SteamBypass.Check;

/// <summary>One of the four checks of the connection test.</summary>
/// <param name="Id">Short id.</param>
/// <param name="TitleKey">Language key of the check's name (a line of the test row, a line of the test verb).</param>
/// <param name="Url">What is requested.</param>
/// <param name="HostHeader">A Host header to send instead of the URL's host, or null.</param>
/// <param name="UseProxy">True: through the bypass proxy when the bypass is on. False: always direct (the loopback download port).</param>
internal sealed record CheckSpec(string Id, string TitleKey, Uri Url, string? HostHeader, bool UseProxy)
{
    /// <summary>The four checks of the standalone app: store, community, static content (all through the proxy) and game downloads (the loopback port 80).</summary>
    public static readonly IReadOnlyList<CheckSpec> All = new[]
    {
        new CheckSpec("store", "check.store.title", new Uri("https://store.steampowered.com/"), null, true),
        new CheckSpec("community", "check.community.title", new Uri("https://steamcommunity.com/"), null, true),
        new CheckSpec("static", "check.static.title", new Uri("https://store.cloudflare.steamstatic.com/"), null, true),
        new CheckSpec("downloads", "check.downloads.title", new Uri("http://127.0.0.1/"), "cache8-hkg1.steamcontent.com", false),
    };
}

/// <summary>Why a check failed.</summary>
internal enum CheckFailure
{
    /// <summary>It did not fail.</summary>
    None,
    /// <summary>The connection was refused (nothing listens there).</summary>
    Refused,
    /// <summary>No answer in time.</summary>
    TimedOut,
    /// <summary>The name could not be resolved.</summary>
    DnsFailed,
    /// <summary>The connection was reset or closed: what the network's block looks like.</summary>
    Reset,
    /// <summary>Something else; <see cref="CheckResult.Detail"/> has the text.</summary>
    Other,
}

/// <summary>Result of one check. Any HTTP answer counts as reachable.</summary>
internal sealed record CheckResult(string Id, bool Ok, CheckFailure Failure, int? HttpStatus, long Milliseconds, string? Detail);

/// <summary>Makes the request of one check.</summary>
internal interface IConnectionProbe
{
    /// <summary>Runs one check. <paramref name="proxyPort"/> is the bypass proxy to go through, or null for a direct request. Never throws for a network problem.</summary>
    Task<CheckResult> RunAsync(CheckSpec spec, int? proxyPort, CancellationToken ct);
}

/// <summary>The real probe: <see cref="HttpClient"/>, 12 s, no redirects, an explicit proxy (never the Windows setting).</summary>
internal sealed class HttpConnectionProbe : IConnectionProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(CheckSpec spec, int? proxyPort, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = spec.UseProxy && proxyPort is not null,
            Proxy = spec.UseProxy && proxyPort is { } p ? new WebProxy($"http://127.0.0.1:{p}") : null,
            ConnectTimeout = Timeout,
            PooledConnectionLifetime = TimeSpan.FromSeconds(1),
        };
        using var http = new HttpClient(handler) { Timeout = Timeout };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, spec.Url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            if (spec.HostHeader is not null) request.Headers.Host = spec.HostHeader;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return new CheckResult(spec.Id, true, CheckFailure.None, (int)response.StatusCode, watch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new CheckResult(spec.Id, false, Classify(ex), null, watch.ElapsedMilliseconds, ex.GetBaseException().Message);
        }
    }

    /// <summary>Maps the exception of a failed request to the reason the page shows.</summary>
    internal static CheckFailure Classify(Exception ex)
    {
        if (ex is TaskCanceledException or TimeoutException) return CheckFailure.TimedOut;
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException s)
            {
                return s.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => CheckFailure.Refused,
                    SocketError.TimedOut => CheckFailure.TimedOut,
                    SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => CheckFailure.DnsFailed,
                    SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown => CheckFailure.Reset,
                    _ => CheckFailure.Other,
                };
            }
            if (e is System.Security.Authentication.AuthenticationException) return CheckFailure.Reset;
            if (e is IOException && e.InnerException is null) return CheckFailure.Reset;   // "the connection was closed" without a socket error
        }
        if (ex is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }) return CheckFailure.DnsFailed;
        return CheckFailure.Other;
    }
}

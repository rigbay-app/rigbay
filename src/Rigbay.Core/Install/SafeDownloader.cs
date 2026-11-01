using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Rigbay.Core.Install;

public readonly record struct DownloadProgress(long Done, long? Total);

public sealed record DownloadResult(long Bytes, string Sha256);

public sealed record TextResult(string? Body, string? ETag, bool NotModified);

/// <summary>
/// Downloads over HTTPS from a short list of hosts. Redirects are followed by hand (at most <see cref="InstallPolicy.MaxRedirects"/>,
/// each target checked against the same host list and HTTPS), nothing but a conditional-request header is sent, no cookies, no
/// credentials. The file is written while it is hashed and counted, and stops at the size cap, whatever the server says.
/// </summary>
public sealed class SafeDownloader
{
    private readonly HttpClient _http;
    private readonly InstallPolicy _policy;

    public SafeDownloader(HttpClient http, InstallPolicy policy)
    {
        _http = http;
        _policy = policy;
    }

    /// <summary>An HttpClient with automatic redirects, cookies and decompression off. The caller owns it.</summary>
    public static HttpClient CreateHttpClient(InstallPolicy policy, HttpMessageHandler? handlerOverride = null)
    {
        HttpMessageHandler handler = handlerOverride ?? (policy.AllowLocalSources
            ? new LocalFileHttpHandler(policy.LocalReadDelay)
            : new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                UseProxy = true,
                DefaultProxyCredentials = null,
            });
        var client = new HttpClient(handler, disposeHandler: handlerOverride is null) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Rigbay-Installer", null));   // no version, no machine identifier
        return client;
    }

    /// <summary>Sends a GET and follows allowed redirects; returns the final response with its headers read.</summary>
    internal async Task<HttpResponseMessage> SendAsync(Uri start, IReadOnlyList<string> hosts, string? ifNoneMatch, CancellationToken ct)
    {
        var uri = start;
        for (int hop = 0; ; hop++)
        {
            if (!_policy.IsAllowedRequest(uri, hosts))
                throw new InstallException(InstallErrorCode.UrlNotAllowed, $"'{Describe(uri)}' is not an address Rigbay downloads from (https, {string.Join(" / ", hosts)})");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (hop == 0 && ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
            catch (HttpRequestException ex) { throw Classify(ex); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InstallException(InstallErrorCode.DownloadFailed, "the server did not answer in time"); }

            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InstallException(InstallErrorCode.DownloadFailed, "a redirect without a target");
                if (hop >= _policy.MaxRedirects) throw new InstallException(InstallErrorCode.UrlNotAllowed, $"more than {_policy.MaxRedirects} redirects");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            return response;
        }
    }

    /// <summary>Downloads to <paramref name="destination"/> (replaced). Throws <see cref="InstallException"/>; the partial file is deleted on any failure.</summary>
    public async Task<DownloadResult> DownloadFileAsync(Uri url, string destination, long expectedSize, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        try
        {
            using var response = await SendAsync(url, InstallPolicy.ReleaseHosts, null, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InstallException(InstallErrorCode.DownloadFailed, $"the server answered {(int)response.StatusCode} {response.ReasonPhrase}");

            long? declared = response.Content.Headers.ContentLength;
            if (declared > _policy.MaxZipBytes) throw new InstallException(InstallErrorCode.TooLarge, $"the download is {declared} bytes (limit {_policy.MaxZipBytes})");
            if (declared is { } d && d != expectedSize) throw new InstallException(InstallErrorCode.SizeMismatch, $"the server announces {d} bytes, the registry says {expectedSize}");

            long total = 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                progress?.Report(new DownloadProgress(0, expectedSize));
                while (true)
                {
                    stall.CancelAfter(_policy.StallTimeout);
                    int n;
                    try { n = await input.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or HttpRequestException)
                    {
                        throw new InstallException(InstallErrorCode.Incomplete, $"the connection ended after {total} of {expectedSize} bytes", ex);
                    }
                    if (n == 0) break;
                    total += n;
                    if (total > _policy.MaxZipBytes) throw new InstallException(InstallErrorCode.TooLarge, $"the download is larger than {_policy.MaxZipBytes / (1024 * 1024)} MB");
                    if (total > expectedSize) throw new InstallException(InstallErrorCode.SizeMismatch, $"more than the {expectedSize} bytes the registry says");
                    sha.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    progress?.Report(new DownloadProgress(total, expectedSize));
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InstallException(InstallErrorCode.Incomplete, $"the download stalled after {total} of {expectedSize} bytes"); }

            if (total < expectedSize) throw new InstallException(InstallErrorCode.Incomplete, $"only {total} of {expectedSize} bytes arrived");
            return new DownloadResult(total, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
        }
        catch
        {
            try { if (File.Exists(destination)) File.Delete(destination); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>Fetches a small text file (the registry). With <paramref name="etag"/> the request is conditional and a 304 returns <c>NotModified</c>.</summary>
    public async Task<TextResult> GetTextAsync(Uri url, string? etag, CancellationToken ct)
    {
        using var response = await SendAsync(url, InstallPolicy.RegistryHosts, etag, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified) return new TextResult(null, etag, true);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InstallException(InstallErrorCode.RegistryUnreachable, $"the server answered {(int)response.StatusCode} {response.ReasonPhrase}");
        if (response.Content.Headers.ContentLength > _policy.MaxRegistryBytes) throw new InstallException(InstallErrorCode.RegistryInvalid, "the registry file is too large");
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[16384];
            int n;
            while ((n = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                memory.Write(buffer, 0, n);
                if (memory.Length > _policy.MaxRegistryBytes) throw new InstallException(InstallErrorCode.RegistryInvalid, "the registry file is too large");
            }
            var text = new UTF8Encoding(false, true).GetString(memory.GetBuffer(), 0, (int)memory.Length);
            return new TextResult(text.TrimStart('﻿'), response.Headers.ETag?.ToString(), false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new InstallException(InstallErrorCode.RegistryUnreachable, "the connection ended while reading the registry: " + ex.Message, ex);
        }
        catch (DecoderFallbackException ex) { throw new InstallException(InstallErrorCode.RegistryInvalid, "the registry file is not UTF-8 text", ex); }
    }

    /// <summary>Maps a transport failure to offline (no network or name) or unreachable (everything else).</summary>
    internal static InstallException Classify(HttpRequestException ex)
    {
        bool offline = ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
            || ex.InnerException is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostUnreachable or System.Net.Sockets.SocketError.TryAgain or System.Net.Sockets.SocketError.NetworkDown };
        return offline
            ? new InstallException(InstallErrorCode.Offline, "no network connection: " + ex.Message, ex)
            : new InstallException(InstallErrorCode.DownloadFailed, ex.Message, ex);
    }

    private static string Describe(Uri uri) => uri.IsAbsoluteUri ? uri.GetLeftPart(UriPartial.Path) : uri.ToString();
}

/// <summary>Answers <c>file:</c> requests from disk and passes nothing else on. Only used with <see cref="InstallPolicy.AllowLocalSources"/>.</summary>
internal sealed class LocalFileHttpHandler(TimeSpan readDelay) : HttpMessageHandler
{
    // everything that is not a file: address goes out through the real HTTP stack (loopback servers in tests), still without redirects, cookies or decompression
    private readonly HttpMessageInvoker _network = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5) });

    private static Stream Slow(Stream inner, TimeSpan delay) => delay > TimeSpan.Zero ? new SlowStream(inner, delay) : inner;

    private sealed class SlowStream(Stream inner, TimeSpan delay) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 16384));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 16384)], cancellationToken).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Scheme != Uri.UriSchemeFile) return _network.SendAsync(request, cancellationToken);
        var path = uri.LocalPath;
        if (!File.Exists(path)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(Slow(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), readDelay)) };
        response.Content.Headers.ContentLength = new FileInfo(path).Length;
        return Task.FromResult(response);
    }
}

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rigbay.Abstractions;
using Rigbay.Core.Install;
using Xunit;

namespace Rigbay.Core.Tests;

/// <summary>A module as a test builds it: a manifest and a dummy entry assembly. Never loaded, only installed.</summary>
public sealed record TestModule(
    string Id = "demo-tool",
    string Version = "1.0.0",
    string HostApi = ">=0.1 <0.2",
    bool Admin = false,
    string[]? Network = null,
    string[]? ProbePaths = null,
    string? ManifestIdOverride = null,
    string? ManifestVersionOverride = null)
{
    public string ManifestJson()
    {
        var requires = new Dictionary<string, object?> { ["admin"] = Admin, ["network"] = Network ?? Array.Empty<string>() };
        var manifest = new Dictionary<string, object?>
        {
            ["manifestVersion"] = 1,
            ["id"] = ManifestIdOverride ?? Id,
            ["name"] = "Demo " + Id,
            ["version"] = ManifestVersionOverride ?? Version,
            ["author"] = "tests",
            ["description"] = "A module built by a test.",
            ["entry"] = new { assembly = "Demo.dll", type = "Demo.Module" },
            ["hostApi"] = HostApi,
            ["group"] = "other",
            ["requires"] = requires,
        };
        if (ProbePaths is { Length: > 0 }) manifest["probePaths"] = ProbePaths;
        return JsonSerializer.Serialize(manifest);
    }

    public RegistryDeclaresJson Declares => new(Admin, Network ?? Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), ProbePaths ?? Array.Empty<string>());

    public byte[] Zip(Action<ZipArchive>? more = null) => ZipKit.Build(z =>
    {
        ZipKit.Add(z, "module.json", ManifestJson());
        ZipKit.Add(z, "Demo.dll", new byte[] { 0x4D, 0x5A, 1, 2, 3, 4 });
        ZipKit.Add(z, "lang/en.lang", "@code = en\n");
        more?.Invoke(z);
    });
}

public sealed record RegistryDeclaresJson(bool Admin, string[] Network, string[] Registry, string[] Services, string[] OutsideFiles);

public static class ZipKit
{
    public static byte[] Build(Action<ZipArchive> fill)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) fill(z);
        return ms.ToArray();
    }

    public static ZipArchiveEntry Add(ZipArchive z, string name, string text) => Add(z, name, Encoding.UTF8.GetBytes(text));

    public static ZipArchiveEntry Add(ZipArchive z, string name, byte[] data)
    {
        var e = z.CreateEntry(name, CompressionLevel.Fastest);
        using var s = e.Open();
        s.Write(data);
        return e;
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

/// <summary>A scripted HttpMessageHandler: the test says what each URL answers. Every request is recorded.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public List<(Uri Uri, string? IfNoneMatch)> Requests { get; } = new();
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add((request.RequestUri!, request.Headers.TryGetValues("If-None-Match", out var v) ? v.First() : null));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Respond(request));
    }

    public static HttpResponseMessage Bytes(byte[] data, string? etag = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        if (etag is not null) r.Headers.TryAddWithoutValidation("ETag", etag);
        return r;
    }

    public static HttpResponseMessage Text(string text, string? etag = null) => Bytes(Encoding.UTF8.GetBytes(text), etag);

    public static HttpResponseMessage Redirect(string to, HttpStatusCode code = HttpStatusCode.Found)
    {
        var r = new HttpResponseMessage(code);
        r.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return r;
    }
}

/// <summary>A response body that sends some bytes and then fails or waits, to test partial downloads and stalls.</summary>
public sealed class BrokenContent : HttpContent
{
    private readonly byte[] _first;
    private readonly long _announced;
    private readonly bool _stall;

    public BrokenContent(byte[] first, long announced, bool stall)
    {
        _first = first;
        _announced = announced;
        _stall = stall;
        Headers.ContentLength = announced;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new BrokenStream(_first, _stall));

    protected override bool TryComputeLength(out long length)
    {
        length = _announced;
        return true;
    }

    private sealed class BrokenStream(byte[] first, bool stall) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos < first.Length)
            {
                int n = Math.Min(buffer.Length, first.Length - _pos);
                first.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }
            if (stall) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
            throw new IOException("the connection was closed before the response ended");
        }
    }
}

public static class RegistryKit
{
    public const string Repo = "https://github.com/rigbay-app/module-demo";

    public static string ZipUrl(string version = "1.0.0", string repo = Repo, string file = "demo-tool.zip") => $"{repo}/releases/download/v{version}/{file}";

    public static string ZipUrlOf(TestModule m) => ZipUrl(m.Version, file: m.Id + ".zip");

    public static object Version(TestModule m, byte[] zip, string? zipUrl = null, string? sha = null, long? size = null, string? hostApi = null, RegistryDeclaresJson? declares = null) => new
    {
        version = m.Version,
        hostApi = hostApi ?? m.HostApi,
        zip = zipUrl ?? ZipUrlOf(m),
        sha256 = sha ?? ZipKit.Sha256(zip),
        size = size ?? zip.LongLength,
        released = "2026-11-01",
        declares = declares ?? m.Declares,
    };

    public static object Module(string id, object[] versions, string repo = Repo) => new { id, name = "Demo " + id, description = "A module for tests.", repo, versions };

    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static string Json(params object[] modules) => JsonSerializer.Serialize(new { schemaVersion = 1, modules }, Web);

    /// <summary>A registry with one module and one release.</summary>
    public static string One(TestModule m, byte[] zip, Func<object, object>? edit = null)
    {
        var v = Version(m, zip);
        return Json(Module(m.Id, new[] { edit?.Invoke(v) ?? v }));
    }
}

/// <summary>A scratch modules folder, install folder and data folder for one test.</summary>
public sealed class InstallEnv : IDisposable
{
    public InstallEnv()
    {
        Root = Path.Combine(Path.GetTempPath(), "rigbay-inst-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        ModulesRoot = Path.Combine(Root, "modules");
        DataRoot = Path.Combine(Root, "data");
        Directory.CreateDirectory(ModulesRoot);
        Directory.CreateDirectory(DataRoot);
        Paths = new InstallPaths(ModulesRoot);
        Http = new FakeHttp();
        Client = SafeDownloader.CreateHttpClient(InstallPolicy.Production, Http);
        Log = new ListLog();
    }

    public string Root { get; }
    public string ModulesRoot { get; }
    public string DataRoot { get; }
    public InstallPaths Paths { get; }
    public FakeHttp Http { get; }
    public HttpClient Client { get; }
    public ListLog Log { get; }
    public InstallPolicy Policy { get; set; } = InstallPolicy.Production;
    public IFolderSecurity Security { get; set; } = new NoFolderSecurity();
    public Version HostApi { get; set; } = new(0, 1);

    public SafeDownloader Downloader => new(Client, Policy);
    public ModuleInstaller NewInstaller() => new(Paths, Policy, Downloader, Security, HostApi, Log);
    public RegistryClient NewRegistryClient(Func<DateTimeOffset>? clock = null) => new(Downloader, Policy, Paths, Security, InstallPolicy.DefaultRegistryUrl, Log, clock);

    /// <summary>Puts an installed module into the modules folder as if it had been installed earlier.</summary>
    public string Install(TestModule m)
    {
        var folder = Path.Combine(ModulesRoot, m.Id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "module.json"), m.ManifestJson());
        File.WriteAllBytes(Path.Combine(folder, "Demo.dll"), new byte[] { 0x4D, 0x5A, 1, 2, 3 });
        return folder;
    }

    public string Version(string id) => ManifestReader.ReadFolder(Path.Combine(ModulesRoot, id)).Manifest?.Version ?? "?";

    /// <summary>Makes the fake server answer a release download of <paramref name="zip"/> at the registry address.</summary>
    public void ServeZip(TestModule m, byte[] zip) =>
        Http.Respond = r => r.RequestUri!.ToString() == RegistryKit.ZipUrlOf(m) ? FakeHttp.Bytes(zip) : new HttpResponseMessage(HttpStatusCode.NotFound);

    public RegistryModule Entry(TestModule m, byte[] zip, Func<object, object>? edit = null)
    {
        var parsed = RegistryReader.Parse(RegistryKit.One(m, zip, edit), Policy);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
        return parsed.Document!.Modules[0];
    }

    public void Dispose()
    {
        PendingRunner.StepHook = null;
        try { Directory.Delete(Root, recursive: true); } catch (Exception) { }
    }
}

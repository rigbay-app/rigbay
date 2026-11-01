// Verifies module release zips with the host's own installer logic (Rigbay.Core.Install) and the real loader.
//
//   verify-module-zip zip <file.zip> --repo https://github.com/rigbay-app/module-x
//       A zip that is still on this machine. A registry entry is made from the zip and its module.json (what scripts/entry.mjs of
//       the registry repository does), read with RegistryReader, and the zip is installed by ModuleInstaller through a fake HTTP
//       server that answers the release address with the file: SHA-256, size, SafeZip unpack, manifest id/version, host API range,
//       declares equal or narrower. Then the pending change is applied (PendingRunner) and the real RigbayHost discovers the
//       module in the scratch modules folder (load context, entry type, Probe). Two negative controls prove the checks can fail.
//
//   verify-module-zip registry [--url <registry.json address>] [--expect id,id,...]
//       The real thing over the network: the registry file is fetched with the production policy (raw.githubusercontent.com only),
//       parsed with RegistryReader, and for every module the newest release is downloaded from GitHub by ModuleInstaller (the
//       hash is compared before unpacking), applied and discovered. Prints the hash and size of each asset.
//
// Everything happens under %TEMP%\rigbay-verify-<random>; nothing is written to %ProgramData%\Rigbay or %LOCALAPPDATA%\Rigbay.
// Nothing is enabled: modules are loaded and probed (read only by contract), never initialised. Exit code 0 = all passed.

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rigbay.Abstractions;
using Rigbay.Core;
using Rigbay.Core.Install;

var failures = new List<string>();
void Pass(string what) => Console.WriteLine("  PASS  " + what);
void Info(string what) => Console.WriteLine("        " + what);
void Fail(string what) { Console.WriteLine("  FAIL  " + what); failures.Add(what); }
void Check(bool ok, string what) { if (ok) Pass(what); else Fail(what); }

if (args.Length == 0 || args[0] is not ("zip" or "registry"))
{
    Console.Error.WriteLine("usage: verify-module-zip zip <file.zip> --repo <https://github.com/owner/repo>");
    Console.Error.WriteLine("       verify-module-zip registry [--url <address>] [--expect id,id]");
    return 2;
}

string? Opt(string name) { for (int i = 1; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1]; return null; }

string scratch = Path.Combine(Path.GetTempPath(), "rigbay-verify-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(scratch);
try
{
    int rc = args[0] == "zip"
        ? await VerifyZip(Path.GetFullPath(args.Length > 1 ? args[1] : throw new ArgumentException("the zip file is missing")), Opt("--repo") ?? throw new ArgumentException("--repo is required"))
        : await VerifyRegistry(Opt("--url") ?? InstallPolicy.DefaultRegistryUrl.ToString(), Opt("--expect"));
    Console.WriteLine();
    Console.WriteLine(failures.Count == 0 ? "ALL CHECKS PASSED" : $"{failures.Count} CHECK(S) FAILED");
    return rc != 0 ? rc : failures.Count == 0 ? 0 : 1;
}
finally
{
    try { Directory.Delete(scratch, recursive: true); } catch (Exception) { /* a scanner may hold a file; it is a temp folder */ }
}

// ---------------------------------------------------------------------------------------------------------------------------

async Task<int> VerifyZip(string zipPath, string repo)
{
    var policy = InstallPolicy.Production;
    Console.WriteLine($"zip {zipPath}");
    byte[] bytes = File.ReadAllBytes(zipPath);
    string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    Info($"{bytes.Length} bytes, sha256 {sha}");
    Check(bytes.Length is > 0 and <= 64 * 1024 * 1024, "size is within the 64 MB limit of the installer");

    // --- the manifest inside, read without unpacking
    string manifestJson;
    using (var z = ZipFile.OpenRead(zipPath))
    {
        var names = z.Entries.Select(e => e.FullName).ToList();
        Check(names.Contains("module.json", StringComparer.Ordinal), "module.json is at the top level of the zip");
        Check(!names.Any(n => n.Contains("Rigbay.Abstractions", StringComparison.OrdinalIgnoreCase)), "no Rigbay.Abstractions in the zip");
        Check(!names.Any(n => n.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)), "no .pdb or .exe in the zip");
        Check(names.Count == names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "no two entries differ only by case");
        using var r = new StreamReader(z.GetEntry("module.json")?.Open() ?? throw new InvalidDataException("no module.json"), Encoding.UTF8);
        manifestJson = r.ReadToEnd();
    }
    var peek = ManifestReader.Parse(manifestJson, null);
    if (peek.Manifest is not { } pm) { Fail("module.json does not parse: " + peek.ErrorSummary); return 1; }
    var declares = RegistryDeclares.Of(pm);
    string fileName = Path.GetFileName(zipPath);
    string zipUrl = $"{repo.TrimEnd('/').Replace(".git", "")}/releases/download/v{pm.Version}/{fileName}";

    // --- the registry entry, as the registry repository would hold it, read by the host's strict reader
    string RegistryJson(string hash, RegistryDeclares d, long size) => new JsonObject
    {
        ["schemaVersion"] = 1,
        ["modules"] = new JsonArray(new JsonObject
        {
            ["id"] = pm.Id,
            ["name"] = pm.Name,
            ["description"] = pm.Description,
            ["repo"] = repo.TrimEnd('/').Replace(".git", ""),
            ["versions"] = new JsonArray(new JsonObject
            {
                ["version"] = pm.Version,
                ["hostApi"] = pm.HostApi.Text,
                ["zip"] = zipUrl,
                ["sha256"] = hash,
                ["size"] = size,
                ["released"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                ["declares"] = new JsonObject
                {
                    ["admin"] = d.Admin,
                    ["network"] = new JsonArray(d.Network.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                    ["registry"] = new JsonArray(d.Registry.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                    ["services"] = new JsonArray(d.Services.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                    ["outsideFiles"] = new JsonArray(d.OutsideFiles.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                },
            }),
        }),
    }.ToJsonString();

    var parsed = RegistryReader.Parse(RegistryJson(sha, declares, bytes.Length), policy);
    Check(parsed.IsValid, "the registry entry made from this zip is accepted by RegistryReader" + (parsed.IsValid ? "" : ": " + string.Join("; ", parsed.Errors)));
    if (!parsed.IsValid) return 1;
    var module = parsed.Document!.Modules[0];
    var version = module.Versions[0];
    Check(version.SupportsHost(HostApi.Current), $"hostApi {version.HostApi} contains this host's API {HostApi.Current.Major}.{HostApi.Current.Minor}");

    // --- install through the real ModuleInstaller (fake HTTP: the release address answers with this file)
    async Task<(bool ok, InstallErrorCode? code, string text)> TryInstall(string name, RegistryModule m, RegistryVersion v)
    {
        var env = new Env(Path.Combine(scratch, name), policy, new FakeServer(zipUrl, bytes));
        try { await env.Installer.InstallAsync(m, v, null, CancellationToken.None); return (true, null, "queued"); }
        catch (InstallException ex) { return (false, ex.Code, ex.Message); }
    }

    var main = new Env(Path.Combine(scratch, "main"), policy, new FakeServer(zipUrl, bytes));
    try
    {
        await main.Installer.InstallAsync(module, version, null, CancellationToken.None);
        Pass("ModuleInstaller: download, SHA-256, unpack (SafeZip), module.json id/version/hostApi, declares equal or narrower: queued");
    }
    catch (InstallException ex) { Fail($"ModuleInstaller refused the zip: {ex.Code}: {ex.Message}"); return 1; }
    Check(main.Installer.Pending.Read().Any(o => o.Id == pm.Id && o.Op == PendingKinds.Install), "pending.json holds the install");

    // --- negative controls: the same checks must refuse a wrong hash and a narrower declaration
    var wrong = RegistryReader.Parse(RegistryJson(new string('0', 64), declares, bytes.Length), policy).Document!.Modules[0];
    var r1 = await TryInstall("neg-hash", wrong, wrong.Versions[0]);
    Check(!r1.ok && r1.code == InstallErrorCode.HashMismatch, $"control: a wrong SHA-256 is refused ({r1.code})");
    var shorter = new RegistryDeclares(false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
    if (!declares.IsEmpty)
    {
        var narrow = RegistryReader.Parse(RegistryJson(sha, shorter, bytes.Length), policy).Document!.Modules[0];
        var r2 = await TryInstall("neg-declares", narrow, narrow.Versions[0]);
        Check(!r2.ok && r2.code == InstallErrorCode.DeclaresWider, $"control: a registry entry that declares less than module.json is refused ({r2.code})");
    }
    else Info("control skipped: the module declares nothing, so there is nothing to make narrower");
    var r3 = await TryInstall("neg-size", parsed.Document!.Modules[0], parsed.Document.Modules[0].Versions[0] with { Size = bytes.Length + 1 });
    Check(!r3.ok, $"control: a wrong size is refused ({r3.code})");

    // --- the start-up swap, then the real loader
    return ApplyAndLoad(main, pm.Id, pm.Version);
}

async Task<int> VerifyRegistry(string url, string? expect)
{
    var policy = InstallPolicy.Production;
    Console.WriteLine($"registry {url}");
    var http = SafeDownloader.CreateHttpClient(policy);
    var downloader = new SafeDownloader(http, policy);
    TextResult text;
    try { text = await downloader.GetTextAsync(new Uri(url), null, CancellationToken.None); }
    catch (Exception ex) { Fail("fetching the registry failed: " + ex.Message); return 1; }
    Pass($"fetched {text.Body!.Length} characters from {url}");
    var parsed = RegistryReader.Parse(text.Body, policy);
    Check(parsed.IsValid, "RegistryReader accepts the file" + (parsed.IsValid ? "" : ": " + string.Join("; ", parsed.Errors)));
    if (!parsed.IsValid) return 1;
    var doc = parsed.Document!;
    Info("modules: " + string.Join(", ", doc.Modules.Select(m => $"{m.Id} {string.Join("/", m.Versions.Select(v => v.Version))}")));
    if (expect is not null)
        foreach (var id in expect.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Check(doc.Find(id) is not null, $"the registry lists '{id}'");

    foreach (var m in doc.Modules)
    {
        var v = m.Latest(HostApi.Current) ?? m.Newest;
        if (v is null) { Fail($"{m.Id}: no release"); continue; }
        Console.WriteLine();
        Console.WriteLine($"{m.Id} {v.Version}: {v.Zip}");
        Info($"registry says sha256 {v.Sha256}, size {v.Size}, hostApi {v.HostApi}, offered by this host: {v.SupportsHost(HostApi.Current)}");
        Check(v.SupportsHost(HostApi.Current), $"{m.Id}: the release fits host API {HostApi.Current.Major}.{HostApi.Current.Minor}");
        var env = new Env(Path.Combine(scratch, m.Id), policy, null);   // the real network
        try
        {
            await env.Installer.InstallAsync(m, v, null, CancellationToken.None);
            Pass($"{m.Id}: downloaded from GitHub; SHA-256 and size equal the registry's; unpacked; manifest and declares checked");
        }
        catch (InstallException ex) { Fail($"{m.Id}: ModuleInstaller refused the release: {ex.Code}: {ex.Message}"); continue; }
        ApplyAndLoad(env, m.Id, v.Version);
    }
    return 0;
}

int ApplyAndLoad(Env env, string id, string version)
{
    var report = PendingRunner.Run(env.Paths, new NoFolderSecurity(), env.DataRoot, s => Info("pending: " + s));
    var outcome = report.Outcomes.FirstOrDefault(o => o.Id == id);
    Check(outcome is { Applied: true }, $"{id}: the swap at start-up applied the install" + (outcome is null ? " (no outcome)" : outcome.Applied ? "" : ": " + outcome.Message));
    var folder = env.Paths.ModuleFolder(id);
    Check(Directory.Exists(folder), $"{id}: the module folder exists in the scratch modules folder");
    if (!Directory.Exists(folder)) return 1;
    Info("files: " + string.Join(", ", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal)));

    var read = ManifestReader.ReadFolder(folder);
    Check(read.IsValid && read.Manifest!.Version == version, $"{id}: the loader's manifest reader accepts module.json from the folder ({read.ErrorSummary})");
    foreach (var issue in read.Issues) Info($"manifest {(issue.IsError ? "error" : "warning")}: {issue}");
    Check(!read.Issues.Any(i => !i.IsError), $"{id}: no manifest warnings (icon, languages, unknown fields)");

    using var host = new RigbayHost(new RigbayHostOptions { ModulesRoot = env.Paths.ModulesRoot, DataRoot = env.DataRoot });
    host.Discover();
    var entry = host.Find(id);
    Check(entry is not null, $"{id}: RigbayHost.Discover found the module");
    if (entry is null) return 1;
    var reason = entry.Reason is { } r ? $"{r.Key} {string.Join(",", r.Args)}" : "";
    Info($"lifecycle {entry.Lifecycle} {reason}");
    Check(entry.HasLoadedCode, $"{id}: the entry assembly was loaded into its own load context, the entry type created");
    Check(entry.Lifecycle is ModuleLifecycle.Off or ModuleLifecycle.Unsupported, $"{id}: the real loader's state is Off or Unsupported (not Invalid, Incompatible or Faulted): {entry.Lifecycle}");
    if (entry.Lifecycle == ModuleLifecycle.Unsupported) Info("Unsupported = the module's own Probe said this machine is not supported; the zip is fine, the machine is not the target");
    var abstractions = entry.LoadContext?.Assemblies.Where(a => a.GetName().Name == "Rigbay.Abstractions").ToList();
    Check(abstractions is { Count: 0 }, $"{id}: Rigbay.Abstractions is not loaded from the module (the host's copy is used)");
    if (entry.LoadContext is { } ctx)
        Info("assemblies in the module's context: " + string.Join(", ", ctx.Assemblies.Select(a => a.GetName().Name).Order(StringComparer.Ordinal)));
    if (entry.Fault is { } fault) Info("fault: " + fault);
    host.Shutdown();
    return 0;
}

// ---------------------------------------------------------------------------------------------------------------------------

sealed class Env
{
    public Env(string root, InstallPolicy policy, HttpMessageHandler? handler)
    {
        Directory.CreateDirectory(root);
        DataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(Path.Combine(root, "modules"));
        Paths = new InstallPaths(Path.Combine(root, "modules"));
        var client = SafeDownloader.CreateHttpClient(policy, handler);
        Installer = new ModuleInstaller(Paths, policy, new SafeDownloader(client, policy), new NoFolderSecurity(), HostApi.Current, new ConsoleLog());
    }

    public InstallPaths Paths { get; }
    public string DataRoot { get; }
    public ModuleInstaller Installer { get; }
}

sealed class FakeServer(string url, byte[] body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.ToString() != url) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentLength = body.Length;
        return Task.FromResult(response);
    }
}

sealed class ConsoleLog : ILog
{
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        if (level >= LogLevel.Warn) Console.WriteLine($"        [{level}] {message}{(exception is null ? "" : " " + exception.Message)}");
    }
}

using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.Core.Tests;

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class ToastCounter : IToastSink
{
    public List<(string Module, string Title, string Body, ToastKind Kind)> Shown { get; } = new();
    public void Show(string moduleId, string title, string body, ToastKind kind) { lock (Shown) Shown.Add((moduleId, title, body, kind)); }
}

/// <summary>
/// A host with a "target" module (verbs that change an in-memory number) and an "owner" module (drives plans), a scratch plans folder, a hand-moved clock
/// and a counting prompt. The modules are the real test module loaded the way the host loads any module; nothing touches hardware.
/// </summary>
public sealed class PlanKit : IDisposable
{
    public TestEnv Env { get; } = new();
    public ManualTime Time { get; } = new();
    public FakeApproval Prompt { get; } = new();
    public ToastCounter Toasts { get; } = new();
    public string PlansRoot { get; }
    public RigbayHost Host { get; private set; } = null!;
    public string OwnerFolder { get; private set; } = "";
    public string TargetFolder { get; private set; } = "";
    public int StartsTotal { get; }

    public PlanKit(string targetFlags = "", string targetVersion = "1.0.0", TimeSpan? verbTimeout = null, string? plansRoot = null, bool start = true, int startsPerHour = 6, int startsPerHourTotal = 20)
    {
        PlansRoot = plansRoot ?? Path.Combine(Env.Root, "plans");
        TargetFolder = Env.AddModule("verbs", "target" + (targetFlags.Length > 0 ? "\n" + targetFlags : ""), json => Edit(json, "target", targetVersion), folderName: "target");
        OwnerFolder = Env.AddModule("verbs", "owner", json => Edit(json, "owner", "1.0.0"), folderName: "owner");
        StartsTotal = startsPerHourTotal;
        if (start) Start(verbTimeout, startsPerHour);
    }

    private static string Edit(string json, string id, string version) =>
        json.Replace("\"id\": \"verbs\"", $"\"id\": \"{id}\"").Replace("\"version\": \"1.0.0\"", $"\"version\": \"{version}\"").Replace("\"name\": \"Verb test module\"", $"\"name\": \"{char.ToUpper(id[0])}{id[1..]} module\"");

    public RigbayHost Start(TimeSpan? verbTimeout = null, int startsPerHour = 6, Rigbay.Core.Install.IFolderSecurity? security = null)
    {
        Host = new RigbayHost(new RigbayHostOptions
        {
            ModulesRoot = Env.ModulesRoot,
            DataRoot = Env.DataRoot,
            FolderSecurity = security ?? new Rigbay.Core.Install.NoFolderSecurity(),
            PlansRoot = PlansRoot,
            TimeProvider = Time,
            VerbTimeout = verbTimeout ?? TimeSpan.FromSeconds(10),
            PlanRunTimeout = TimeSpan.FromSeconds(30),
            PreviewTimeout = TimeSpan.FromSeconds(5),
            PlanStartsPerHour = startsPerHour,
            PlanStartsPerHourTotal = StartsTotal,
            LogLevel = LogLevel.Debug,
        })
        {
            ApprovalPrompt = Prompt,
            ToastSink = Toasts,
        };
        Host.Discover();
        Host.StartEnabled();                                           // what the user enabled before (a restart)
        foreach (var m in Host.Modules.Where(m => m.Lifecycle == ModuleLifecycle.Off)) m.Enable();   // first start: enable and remember
        return Host;
    }

    /// <summary>A second process: shuts the host down and starts a new one on the same folders.</summary>
    public RigbayHost Restart(Rigbay.Core.Install.IFolderSecurity? security = null)
    {
        Host.Shutdown();
        return Start(security: security);
    }

    public void WritePlan(string json) => File.WriteAllText(Path.Combine(OwnerFolder, "plan.json"), json);

    /// <summary>The plan the tests use: set 5 (recorded undo), steam-like on/off pair is not needed; one step is enough to check exact matching.</summary>
    public static string SetPlan(string value = "5", bool restore = true, string id = "scene") =>
        $$"""{ "id": "{{id}}", "steps": [ { "module": "target", "verb": "set", "args": ["{{value}}"], "restore": {{(restore ? "true" : "false")}} } ] }""";

    public async Task<string> Owner(params string[] args)
    {
        var all = new List<string> { "owner" };
        all.AddRange(args);
        var outcome = await Host.ExecuteCommandAsync(all);
        return outcome.Output;
    }

    public async Task<string> Target(params string[] args)
    {
        var all = new List<string> { "target" };
        all.AddRange(args);
        var outcome = await Host.ExecuteCommandAsync(all);
        return outcome.Output;
    }

    public static string Field(string output, string name)
    {
        foreach (var part in output.Split(';'))
            if (part.StartsWith(name + "=", StringComparison.Ordinal)) return part[(name.Length + 1)..];
        return "";
    }

    public void Dispose()
    {
        try { Host.Shutdown(); } catch (Exception) { }
        Env.Dispose();
    }
}

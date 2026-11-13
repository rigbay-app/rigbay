using System.Text.Json;
using Rigbay.Abstractions;

namespace Rigbay.Scenes.Tests;

/// <summary>A started module on fakes: the host's plan engine, the process list, the clock and the poll timer.</summary>
internal sealed class Rig
{
    public readonly FakeVerbs Verbs = new();
    public readonly FakeHost Host;
    public readonly FakeProcesses Procs = new();
    public readonly FakeTicker Ticker = new();
    public readonly List<TimeSpan> Delays = new();
    public DateTimeOffset Now = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);
    public ScenesModule Module = null!;

    public Rig(string language = "en")
    {
        Host = new FakeHost(Verbs, language);
        Verbs.Infos = DefaultInfos();
    }

    public ScenesRuntime Runtime => Module.Runtime!;

    public static List<VerbInfo> DefaultInfos() => new()
    {
        new("demo-target", "Demo target", "get", "Reads the number", "get", VerbAccess.ReadOnly, null, EffectKind.Hardware, null, false),
        new("demo-target", "Demo target", "mark", "Marks it", "mark on|off", VerbAccess.Writes, "Marks a setting", EffectKind.SystemSetting, null, true),
        new("demo-target", "Demo target", "set", "Sets the number", "set <0..100>", VerbAccess.Writes, "Changes a number in memory", EffectKind.Hardware, "set", true),
        new("steam", "Steam access", "off", "Turns it off", null, VerbAccess.Unknown, null, EffectKind.Hardware, null, false),
        new("steam", "Steam access", "on", "Turns it on", null, VerbAccess.Unknown, null, EffectKind.Hardware, null, false),
        new("recorder", "Recorder", "start", "Starts a recording", "start", VerbAccess.Writes, "Writes a CSV file", EffectKind.SystemSetting, null, false),
    };

    public Dependencies Deps => new()
    {
        Processes = Procs,
        Ticker = Ticker,
        Now = () => Now,
        Delay = (t, ct) => { lock (Delays) Delays.Add(t); Now += t; return Task.CompletedTask; },
        Poll = TimeSpan.FromSeconds(5),
        Grace = TimeSpan.FromSeconds(10),
        TargetWait = TimeSpan.FromSeconds(30),
    };

    public static SceneSettings Scene(string id = "scene-a", string name = "Gaming", string game = "game.exe", bool enabled = true) => new()
    {
        Id = id,
        Name = name,
        Enabled = enabled,
        Games = new List<string> { game },
        Steps = new List<StepSettings>
        {
            new() { Module = "demo-target", Verb = "set", Args = new List<string> { "5" }, Undo = UndoMode.Recorded },
            new() { Module = "steam", Verb = "on", Undo = UndoMode.Fixed, UndoModule = "steam", UndoVerb = "off" },
        },
    };

    /// <summary>The owner said yes to the scene exactly as it is: the host stores the plan, the module stores the fingerprint.</summary>
    public SceneSettings Approved(SceneSettings scene)
    {
        scene.ApprovedFingerprint = ScenePlans.Fingerprint(scene);
        Verbs.Approve(ScenePlans.Build(scene));
        return scene;
    }

    public Rig Seed(params SceneSettings[] scenes)
    {
        Host.SettingsStore.Json = JsonSerializer.Serialize(new ScenesSettings { Scenes = scenes.ToList() });
        return this;
    }

    public async Task<Rig> StartAsync()
    {
        Module = new ScenesModule(Deps);
        Module.Initialize(Host);
        await Module.WhenIdleAsync();
        return this;
    }

    /// <summary>Moves the clock and looks at the processes once, the way the timer would.</summary>
    public async Task Tick(int seconds = 0)
    {
        Now += TimeSpan.FromSeconds(seconds);
        Verbs.Now = Now;
        await Runtime.TickAsync();
        await Module.WhenIdleAsync();
    }

    public SceneSettings Stored(string id = "scene-a") => Host.SettingsStore.Current.Scenes.Single(s => s.Id == id);

    public SceneState State(string id = "scene-a") => Runtime.State(id);

    public PageModel Page() => Module.GetPageModel(Host)!;

    public string Words(LocalisedText? text) => text is { } t ? Host.Loc.Resolve(t) : "";
}

internal static class PageExtensions
{
    public static IEnumerable<Row> EnumerateRows(this PageModel page)
    {
        IEnumerable<Row> Walk(IEnumerable<Row> rows)
        {
            foreach (var row in rows)
            {
                yield return row;
                if (row is ExpanderRow e)
                {
                    if (e.HeaderRow is { } h) yield return h;
                    foreach (var child in Walk(e.Children)) yield return child;
                }
            }
        }
        if (page.Primary is { } primary) yield return primary;
        foreach (var group in page.Groups)
            foreach (var row in Walk(group.Rows)) yield return row;
    }

    public static ExpanderRow Scene(this PageModel page, string name) =>
        page.Groups.SelectMany(g => g.Rows).OfType<ExpanderRow>().Single(e => e.HeaderKey == Text.Literal(name));

    public static T Row<T>(this ExpanderRow scene, string header) where T : Row =>
        scene.Children.OfType<T>().Single(r => r.HeaderKey == header);
}

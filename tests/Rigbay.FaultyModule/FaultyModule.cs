using Rigbay.Abstractions;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Rigbay.SystemDep;
using Rigbay.TestDependency;

namespace Rigbay.FaultyModule;

/// <summary>
/// Misbehaves in the way named in <c>behavior.txt</c> next to this DLL (the test writes it before the host loads the
/// module): ctor-throws, probe-throws, probe-null, probe-unsupported, probe-hangs, init-throws, init-hangs,
/// page-throws, bad-page, toggle-throws, button-throws, hotkey-throws, command-throws, metric-handler-throws,
/// refresh (button asks for a new page), refresh-throws (second GetPageModel throws), refresh-slow (second and later builds take 300 ms), uses-dependency, uses-system-dependency (a System.* assembly the host lacks), json-context (which context holds System.Text.Json), none.
/// </summary>
public sealed class FaultyModule : IModule
{
    private static readonly string Folder = Path.GetDirectoryName(typeof(FaultyModule).Assembly.Location)!;
    private readonly string _behavior;
    private readonly ObservableValue<bool> _toggle = new(false);
    private IHostServices? _host;
    private int _pageCalls;

    public FaultyModule()
    {
        var file = Path.Combine(Folder, "behavior.txt");
        _behavior = File.Exists(file) ? File.ReadAllText(file).Trim() : "none";
        if (_behavior == "ctor-throws") throw new InvalidOperationException("constructor blew up");
    }

    public ProbeResult Probe()
    {
        switch (_behavior)
        {
            case "probe-throws": throw new InvalidOperationException("probe blew up");
            case "probe-null": return null!;
            case "probe-unsupported": return ProbeResult.No("probe.nope");
            case "probe-hangs": Thread.Sleep(TimeSpan.FromMinutes(1)); break;
        }
        return ProbeResult.Yes();
    }

    public void Initialize(IHostServices host)
    {
        _host = host;
        switch (_behavior)
        {
            case "init-throws": throw new InvalidOperationException("initialize blew up");
            case "init-hangs": Thread.Sleep(TimeSpan.FromMinutes(1)); break;
            case "hotkey-throws":
                host.Hotkeys.Register(new HotkeyRegistration("boom", "hotkey.boom", HotkeyGesture.Parse("Ctrl+Alt+F9"),
                    () => throw new InvalidOperationException("hotkey blew up")));
                break;
            case "command-throws":
                host.Commands.Register("boom", "cmd.boom", _ => throw new InvalidOperationException("command blew up"));
                break;
            case "metric-handler-throws":
                host.Metrics.Subscribe(_ => throw new InvalidOperationException("metric handler blew up"));
                break;
            case "signals":
            case "signal-throws":
                InitSignals(host);
                break;
        }
        host.Tray.Add(new TrayItem("faulty", "tray.faulty", () => { }));
        host.Commands.Register("ok", "cmd.ok", _ => CommandResult.Ok("fine"));
        host.Commands.Register("fail", "cmd.fail", _ => CommandResult.Failed("expected failure"));
        host.Commands.Register("usage", "cmd.usage", _ => CommandResult.Usage("usage: rigbay faulty usage"));
        _toggle.Changed += () =>
        {
            if (_behavior == "toggle-throws") throw new InvalidOperationException("toggle handler blew up");
        };
    }

    // behavior "signals": uses PageVisible, SubscribersChanged, SubscriberCount, Describe, Withdraw and Latest and reports
    // through commands. "signal-throws" does the same but its PageVisible and SubscribersChanged handlers throw.
    private readonly List<bool> _visibleChanges = new();
    private int _subscriberChanges;
    private bool _visibleAtInit;

    private void InitSignals(IHostServices host)
    {
        bool throws = _behavior == "signal-throws";
        var temp = new MetricId("faulty.temp");
        _visibleAtInit = host.PageVisible.Value;
        host.PageVisible.Changed += () =>
        {
            if (throws) throw new InvalidOperationException("page visible handler blew up");
            lock (_visibleChanges) _visibleChanges.Add(host.PageVisible.Value);
        };
        host.Metrics.SubscribersChanged += () =>
        {
            if (throws) throw new InvalidOperationException("subscribers handler blew up");
            Interlocked.Increment(ref _subscriberChanges);
        };

        // one label by module-relative key, one by literal text, one with a key the language file lacks
        host.Metrics.Describe(new MetricDescriptor(temp, "metric.temp", MetricUnit.Celsius, "0.0"));
        host.Metrics.Describe(new MetricDescriptor(new MetricId("faulty.fan"), Text.Literal("Fan 1"), MetricUnit.Rpm));
        host.Metrics.Describe(new MetricDescriptor(new MetricId("faulty.nokey"), "metric.not-in-the-file"));

        host.Commands.Register("signals", "cmd.signals", _ =>
        {
            string changes;
            lock (_visibleChanges) changes = string.Join(",", _visibleChanges);
            return CommandResult.Ok($"init={_visibleAtInit};visible={host.PageVisible.Value};changes={changes};subchanges={Volatile.Read(ref _subscriberChanges)};subs={host.Metrics.SubscriberCount(temp)}");
        });
        host.Commands.Register("publish", "cmd.publish", ctx =>
        {
            host.Metrics.Publish(new MetricId(ctx.Args[0]), double.Parse(ctx.Args[1], System.Globalization.CultureInfo.InvariantCulture), MetricUnit.Celsius);
            return CommandResult.Ok("published");
        });
        host.Commands.Register("withdraw", "cmd.withdraw", ctx => { host.Metrics.Withdraw(new MetricId(ctx.Args[0])); return CommandResult.Ok("withdrawn"); });
        host.Commands.Register("latest", "cmd.latest", ctx =>
            CommandResult.Ok(host.Metrics.Latest(new MetricId(ctx.Args[0])) is { } s ? s.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none"));
        host.Commands.Register("set-visible", "cmd.set-visible", _ => { host.PageVisible.Value = !host.PageVisible.Value; return CommandResult.Ok("visible=" + host.PageVisible.Value); });
        host.Commands.Register("listen", "cmd.listen", _ =>
        {
            host.Metrics.Subscribe(temp, _ => { }); // the module's own subscription: not counted for itself
            return CommandResult.Ok("listening");
        });
    }

    public PageModel? GetPageModel(IHostServices host)
    {
        int call = Interlocked.Increment(ref _pageCalls);
        if (_behavior == "refresh-throws" && call > 1) throw new InvalidOperationException("refresh blew up");
        if (_behavior == "refresh-slow" && call > 1) Thread.Sleep(300);
        switch (_behavior)
        {
            case "page-throws": throw new InvalidOperationException("page blew up");
            case "bad-page":
                return new PageModel
                {
                    Groups = new[] { new RowGroup("group.bad", new Row[] { new NumberRow("row.bad", new ObservableValue<double>(1), Minimum: 10, Maximum: 1) }) },
                };
        }

        var rows = new List<Row>
        {
            new ToggleRow("row.toggle", _toggle),
            new ButtonRow("row.button", "row.button.label", _ =>
            {
                if (_behavior == "button-throws") throw new InvalidOperationException("button blew up");
                if (_behavior.StartsWith("refresh")) _host!.RequestPageRefresh();
                return Task.FromResult(ActionResult.Ok());
            }),
        };
        rows.Add(new InfoRow(Severity.Info, Text.Literal($"build {call}")));
        if (_behavior == "uses-dependency")
            rows.Add(new ValueRow("row.dep", new ObservableValue<string>(Greeter.Hello())));
        if (_behavior == "uses-system-dependency") rows.Add(new ValueRow("row.sysdep", new ObservableValue<string>(SystemDepText())));
        if (_behavior == "json-context") rows.Add(new ValueRow("row.json", new ObservableValue<string>(JsonContextName())));
        return new PageModel { Groups = new[] { new RowGroup("group.main", rows) } };
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string SystemDepText() => OnlyHere.Hello();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string JsonContextName() => AssemblyLoadContext.GetLoadContext(typeof(JsonSerializer).Assembly)!.Name ?? "";


    public void Shutdown()
    {
        File.WriteAllText(Path.Combine(Folder, "shutdown.marker"), "shutdown was called");
    }
}

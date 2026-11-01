using Rigbay.Abstractions;

namespace Rigbay.DemoModules;

/// <summary>Hardware this machine does not have: Probe says no, with a reason key the host shows.</summary>
public sealed class LightingModule : IModule
{
    public ProbeResult Probe() => ProbeResult.No("probe.no-device");
    public void Initialize(IHostServices host) { }
    public void Shutdown() { }
}

/// <summary>Supported, but throws as soon as it is turned on: the host marks it failed, logs it, and carries on.</summary>
public sealed class BrokenModule : IModule
{
    public ProbeResult Probe() => ProbeResult.Yes();
    public void Initialize(IHostServices host) => throw new InvalidOperationException("Demo: the battery controller did not answer.");
    public void Shutdown() { }
}

// ---- used only by the self-test (tools/out/modules-test): each one misbehaves in a different way ----

/// <summary>A value handler that throws when the user edits the row.</summary>
public sealed class ThrowOnEditModule : IModule
{
    private readonly ObservableValue<bool> _flag = new(false);
    public ProbeResult Probe() => ProbeResult.Yes();
    public void Initialize(IHostServices host) => _flag.Changed += () => throw new InvalidOperationException("Demo: the handler threw on edit.");
    public PageModel? GetPageModel(IHostServices host) => new PageModel
    {
        Groups = new[] { new RowGroup("g", new Row[] { new ToggleRow("t", _flag) }) },
    };
    public void Shutdown() { }
}

/// <summary>A button whose action throws.</summary>
public sealed class ThrowOnButtonModule : IModule
{
    public ProbeResult Probe() => ProbeResult.Yes();
    public void Initialize(IHostServices host) { }
    public PageModel? GetPageModel(IHostServices host) => new PageModel
    {
        Groups = new[] { new RowGroup("g", new Row[] { new ButtonRow("b", "go", _ => throw new InvalidOperationException("Demo: the button action threw.")) }) },
    };
    public void Shutdown() { }
}

/// <summary>Asks for the UI thread and throws there: the case that ends a process when a raw dispatcher is used.</summary>
public sealed class ThrowOnUiModule : IModule
{
    public ProbeResult Probe() => ProbeResult.Yes();
    public void Initialize(IHostServices host) =>
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            host.RunOnUi(() => throw new InvalidOperationException("Demo: the RunOnUi action threw."));
        });
    public void Shutdown() { }
}

/// <summary>
/// A supported module whose page shows a list that changes. It demonstrates page refresh (A8) and literal text: device names
/// are data, so they go through <see cref="Text.Literal"/> in a list, in the choice labels and in the tray item. Its manifest
/// also declares two probe paths, so the Modules page and the turn-on dialog show "Reads files outside its folder".
/// </summary>
public sealed class DevicesModule : IModule
{
    private readonly List<string> _devices = new() { "Pump A", "Radiator fan" };
    private readonly ObservableValue<string> _selected = new("Pump A");
    private IHostServices? _host;

    public ProbeResult Probe() => ProbeResult.Yes();

    public void Initialize(IHostServices host)
    {
        _host = host;
        host.Tray.Add(new TrayItem("first", Text.Literal("Open Pump A"), () => { }));
        host.Hotkeys.Register(new HotkeyRegistration("flush-a", Text.Literal("Flush Pump A"), HotkeyGesture.Parse("Ctrl+Alt+F8"), () => { }));
    }

    public PageModel? GetPageModel(IHostServices host)
    {
        string[] names; lock (_devices) names = _devices.ToArray();
        return new PageModel
        {
            Icon = "",
            Groups = new[]
            {
                new RowGroup("group.devices", new Row[]
                {
                    new ExpanderRow("row.list.header", names.Select(n => (Row)new InfoRow(Severity.Info, Text.Literal(n))).ToArray()) { DescriptionKey = "row.list.description" },
                    new ChoiceRow("row.pick.header", names.Select(n => new ChoiceOption(n, Text.Literal(n))).ToArray(), _selected) { DescriptionKey = "row.pick.description" },
                    new ButtonRow("row.add.header", "row.add.button", Add) { DescriptionKey = "row.add.description" },
                    new ButtonRow("row.remove.header", "row.remove.button", Remove) { DescriptionKey = "row.remove.description" },
                }),
            },
        };
    }

    private Task<ActionResult> Add(CancellationToken ct)
    {
        lock (_devices) _devices.Add($"Sensor {_devices.Count + 1}");
        _host?.RequestPageRefresh();
        return Task.FromResult(ActionResult.Ok());
    }

    private Task<ActionResult> Remove(CancellationToken ct)
    {
        lock (_devices) if (_devices.Count > 0) _devices.RemoveAt(_devices.Count - 1);
        _host?.RequestPageRefresh();
        return Task.FromResult(ActionResult.Ok());
    }

    public void Shutdown() { }
}

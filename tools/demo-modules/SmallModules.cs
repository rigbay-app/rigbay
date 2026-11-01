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

using Rigbay.Abstractions;

namespace Rigbay.PowerRules.Tests;

// Fake machine: a laptop with a 2560 x 1600 panel that lists 60, 120 and 165 Hz, a power source you can flip and a power mode.
// Nothing here touches Windows.

internal sealed class FakeSource : IPowerSourcePort
{
    public bool Battery = true;
    public PowerSource Now = PowerSource.Plugged;
    public bool Started, Disposed;
    public bool Listening { get; set; } = true;
    public int Reads;

    public bool HasBattery() => Battery;
    public PowerSource Read() { Reads++; return Now; }
    public void Start() => Started = true;
    public void Dispose() => Disposed = true;
    public event Action? SourceChanged;
    public event Action? Resumed;

    /// <summary>The user unplugs or plugs in: Windows says so.</summary>
    public void Switch(PowerSource to) { Now = to; SourceChanged?.Invoke(); }
    public void Resume() => Resumed?.Invoke();
    public bool HasSubscribers => SourceChanged is not null || Resumed is not null;
}

internal sealed class FakePanel : IPanelPort
{
    public PanelState State = PanelState.Active;
    public int Current = 165;
    public int[] Rates = { 60, 120, 165 };
    public int Width = 2560, Height = 1600;
    public readonly List<int> Tests = new();
    public readonly List<int> Applies = new();
    public readonly List<string> Order = new();
    public int TestCode;
    public int ApplyCode;
    /// <summary>False: Windows says "ok" but the display keeps its rate.</summary>
    public bool ApplyTakesEffect = true;
    public Exception? FindThrows;
    public int Finds;

    public PanelLookup Find()
    {
        Finds++;
        if (FindThrows is { } ex) throw ex;
        return State == PanelState.Active
            ? new PanelLookup(PanelState.Active, new PanelInfo(@"\\.\DISPLAY1", "Internal panel", Width, Height, Current, Rates))
            : new PanelLookup(State);
    }

    public DisplayChange Test(PanelInfo panel, int hz) { Tests.Add(hz); Order.Add("test " + hz); return new DisplayChange(TestCode); }

    public DisplayChange Apply(PanelInfo panel, int hz)
    {
        Applies.Add(hz);
        Order.Add("apply " + hz);
        if (ApplyCode == 0 && ApplyTakesEffect) Current = hz;
        return new DisplayChange(ApplyCode);
    }
}

internal sealed class FakeMode : IPowerModePort
{
    public bool Available { get; set; } = true;
    public PowerMode Current = PowerMode.Balanced;
    public readonly List<PowerMode> Sets = new();
    public int SetCode { get; set; }
    public bool SetTakesEffect = true;

    public PowerMode Read() => Available ? Current : PowerMode.Unknown;

    public int Set(PowerMode mode)
    {
        Sets.Add(mode);
        if (!Available) return -1;
        if (SetCode == 0 && SetTakesEffect) Current = mode;
        return SetCode;
    }
}

/// <summary>One-shot timers that run when the test says so. A new timer replaces the one before it, like the module's.</summary>
internal sealed class FakeScheduler : IScheduler
{
    private sealed class Handle(FakeScheduler owner, Action action, TimeSpan delay) : IDisposable
    {
        public readonly Action Action = action;
        public readonly TimeSpan Delay = delay;
        public bool Cancelled;
        public void Dispose() { Cancelled = true; owner.Cancels++; }
    }

    private readonly List<Handle> _all = new();
    public int Scheduled => _all.Count;
    public int Cancels;
    public TimeSpan LastDelay => _all.Count == 0 ? TimeSpan.Zero : _all[^1].Delay;
    public bool Pending => _all.Any(h => !h.Cancelled);

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var h = new Handle(this, action, delay);
        _all.Add(h);
        return h;
    }

    /// <summary>Runs the timer that is still armed (the latest one), once.</summary>
    public bool Fire()
    {
        var h = _all.LastOrDefault(x => !x.Cancelled);
        if (h is null) return false;
        h.Cancelled = true;
        h.Action();
        return true;
    }
}

/// <summary>One module on a fake machine and a fake host.</summary>
internal sealed class Rig
{
    public readonly FakeSource Source = new();
    public readonly FakePanel Panel = new();
    public readonly FakeMode Mode = new();
    public readonly FakeScheduler Scheduler = new();
    public readonly FakeHost Host;
    public readonly PowerRulesModule Module;

    public Rig(string language = "en")
    {
        Host = new FakeHost(language);
        Module = new PowerRulesModule(new Dependencies { Source = Source, Panel = Panel, Mode = Mode, Scheduler = Scheduler });
    }

    public Rig Start()
    {
        Assert.True(Module.Probe().Supported);
        Module.Initialize(Host);
        return this;
    }

    public PageModel Page() => Module.GetPageModel(Host)!;
    public T Row<T>(string header) where T : Row => (T)Page().EnumerateRows().Single(r => r.HeaderKey == header);
    public ToggleRow Auto => (ToggleRow)Page().Primary!;
    public ChoiceRow Choice(string header) => Row<ChoiceRow>(header);
    public PowerRulesSettings Saved => Host.SettingsStore.Current;
    public Task Idle() => Module.WhenIdleAsync();

    public string Words(HardwareEffect e) => Host.Loc.T(e.DescriptionKey);
    public string Words(LocalisedText? t) => t is { } x ? Host.Loc.Resolve(x) : "";
    public string[] Toasts() => Host.ToastService.All().Select(t => Host.Loc.Resolve(t.Body)).ToArray();
    /// <summary>The module's state line (dashboard and page header).</summary>
    public string State => Host.Loc.Resolve(((IObservableValue<LocalisedText>)Page().Summary!.StateLine!).Value);
    public string Line(Row row) => row.Line is { } l ? Host.Loc.Resolve(l.Value) : "";

    /// <summary>Sets a rule the way the page does: by choosing in its rows.</summary>
    public async Task Rules(int batteryHz, PowerMode? batteryMode, int pluggedHz, PowerMode? pluggedMode)
    {
        Choice("row.bat-rate.header").Value.Value = batteryHz == 0 ? "leave" : batteryHz.ToString();
        Choice("row.bat-mode.header").Value.Value = batteryMode is { } b ? PowerModes.Id(b) : "leave";
        Choice("row.plug-rate.header").Value.Value = pluggedHz == 0 ? "leave" : pluggedHz.ToString();
        Choice("row.plug-mode.header").Value.Value = pluggedMode is { } p ? PowerModes.Id(p) : "leave";
        await Idle();
    }

    /// <summary>The owner turns automatic switching on and answers the question.</summary>
    public async Task Approve(bool answer = true)
    {
        Host.ApprovalService.Answer = answer;
        Auto.Value.Value = true;
        await Idle();
    }

    /// <summary>The settle timer runs, and the switch with it.</summary>
    public async Task Fire()
    {
        Assert.True(Scheduler.Fire(), "no timer was armed");
        await Idle();
    }
}

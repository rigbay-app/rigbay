using Rigbay.Abstractions;
using Rigbay.SteamBypass.Proxy;

namespace Rigbay.SteamBypass.Tests;

/// <summary>
/// The real module class against fakes for everything outside the process: Windows setting in memory, listeners that open no socket,
/// a dialer, a probe, a look at Steam. No test that uses it touches the real proxy setting or a real port.
/// </summary>
internal sealed class ModuleRig : IDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 1, 18, 42, 0, TimeSpan.FromHours(7));

    public readonly MemoryProxyStore Proxy = new();
    public readonly MemoryStateStore State = new();
    public readonly FakeListenerFactory Listeners = new();
    public readonly FakeResolver Resolver = new();
    public readonly FakeDialer Dialer = new();
    public readonly FakeProbe Probe = new();
    public readonly FakeSteam Steam = new();
    public readonly FakeHost Host;
    public readonly SteamBypassModule Module;
    public DateTimeOffset Now = T0;
    public bool PortAnswers;
    private bool _stopped;

    public ModuleRig(string language = "en", bool approve = false, bool initialize = true, Action<ModuleRig>? arrange = null)
    {
        Host = new FakeHost(language);
        Host.ApprovalService.Answer = approve;
        arrange?.Invoke(this);
        Module = new SteamBypassModule(new Dependencies
        {
            ProxyStore = () => Proxy,
            StateStore = _ => State,
            Listeners = () => Listeners,
            Resolver = () => Resolver,
            Dialer = (_, _) => Dialer,
            Probe = () => Probe,
            Steam = () => Steam,
            Now = () => Now,
            PortAnswers = _ => PortAnswers,
            Options = new ProxyOptions(),
        });
        if (initialize)
        {
            Assert.True(Module.Probe().Supported);
            Module.Initialize(Host);
        }
    }

    public const string Pac = "http://127.0.0.1:18081/proxy.pac";

    public PageModel Page => Module.GetPageModel(Host)!;
    public IEnumerable<Row> Rows => Page.EnumerateRows();
    public ToggleRow Switch => Rows.OfType<ToggleRow>().Single(r => r.HeaderKey == "row.bypass.header");
    public ToggleRow AutoOn => Rows.OfType<ToggleRow>().Single(r => r.HeaderKey == "row.auto-on.header");
    public ButtonRow TestButton => Rows.OfType<ButtonRow>().Single();
    public string ValueOf(string headerKey) => Rows.OfType<ValueRow>().Single(r => r.HeaderKey == headerKey).Value.Value;
    public string StateLine => Host.Loc.Resolve(Page.Summary!.StateLine!.Value);
    public bool IsOn => Proxy.Value == Pac;

    /// <summary>Runs the verb on a pool thread like the host does.</summary>
    public Task<CommandResult> Run(string verb, params string[] args) => Task.Run(() => Host.CommandRegistry.Run(verb, args));

    /// <summary>Flips the switch the way the host does after the user confirmed, and waits until the work has finished.</summary>
    public void FlipSwitch(bool on)
    {
        Switch.Value.Value = on;
        Assert.True(Wait.Until(() => on ? IsOn || Module.LastFailureForTests is not null : !IsOn), "the switch did not take effect");
        Assert.True(Wait.Until(() => Switch.Value.Value == (on ? IsOn : false)));
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        Module.Shutdown();
    }

    public void Dispose() => Stop();
}

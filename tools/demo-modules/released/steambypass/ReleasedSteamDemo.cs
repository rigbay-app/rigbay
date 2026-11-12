using Rigbay.Abstractions;
using Rigbay.SteamBypass.Proxy;

namespace Rigbay.SteamBypass.Tests;

/// <summary>
/// The real <see cref="SteamBypassModule"/> on the fakes of its tests: the Windows proxy setting and the module's state live in memory, the
/// listeners open no socket, nothing resolves or dials. The bypass starts off, as it does for the owner most of the time.
/// </summary>
public sealed class ReleasedSteamDemo : IModule
{
    private readonly MemoryProxyStore _proxy = new();
    private readonly MemoryStateStore _state = new();
    private readonly FakeListenerFactory _listeners = new();
    private readonly FakeResolver _resolver = new();
    private readonly FakeDialer _dialer = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeSteam _steam = new();
    private readonly SteamBypassModule _inner;

    public ReleasedSteamDemo()
    {
        _inner = new SteamBypassModule(new Dependencies
        {
            ProxyStore = () => _proxy,
            StateStore = _ => _state,
            Listeners = () => _listeners,
            Resolver = () => _resolver,
            Dialer = (_, _) => _dialer,
            Probe = () => _probe,
            Steam = () => _steam,
            Now = () => DateTimeOffset.Now,
            PortAnswers = _ => false,
            Options = new ProxyOptions(),
        });
    }

    public ProbeResult Probe() => _inner.Probe();
    public void Initialize(IHostServices host) => _inner.Initialize(host);
    public PageModel? GetPageModel(IHostServices host) => _inner.GetPageModel(host);
    public void Shutdown() => _inner.Shutdown();
}

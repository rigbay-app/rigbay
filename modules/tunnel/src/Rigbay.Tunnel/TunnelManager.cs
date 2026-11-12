using System.ComponentModel;
using Rigbay.Tunnel.Wg;

namespace Rigbay.Tunnel;

/// <summary>One tunnel as the store and the service control manager show it right now.</summary>
internal sealed record TunnelInfo(string Name, string Slug, string ConfigPath, ServiceSnapshot Service)
{
    /// <summary>ON means the service <c>WireGuardTunnel$name</c> exists and is running (what the official app calls active).</summary>
    public bool IsOn => Service.IsRunning;
}

/// <summary>The tunnels found in WireGuard's store.</summary>
internal sealed record TunnelList(bool StoreReadable, IReadOnlyList<TunnelInfo> Tunnels);

/// <summary>A sentence for the user: a module-relative key and its arguments.</summary>
internal sealed record Problem(string Key, params object[] Args);

internal enum ChangeStatus
{
    /// <summary>The change was made and read back.</summary>
    Done,
    /// <summary>The tunnel already was in the state asked for; nothing was changed.</summary>
    AlreadyThere,
    /// <summary>Another change is running; nothing was changed.</summary>
    Busy,
    /// <summary>No such tunnel in WireGuard's store.</summary>
    UnknownTunnel,
    /// <summary>WireGuard is missing or not genuine; nothing was run.</summary>
    ToolsUnusable,
    /// <summary>It was tried and failed; <see cref="ChangeResult.Problem"/> says why.</summary>
    Failed,
}

internal sealed record ChangeResult(ChangeStatus Status, Problem? Problem = null)
{
    public bool Changed => Status == ChangeStatus.Done;
}

internal enum HandshakeOutcome
{
    /// <summary>A handshake younger than the connected limit.</summary>
    Connected,
    /// <summary>The wait ran out without a handshake.</summary>
    NoHandshake,
    /// <summary>The tunnel was turned off while waiting.</summary>
    TurnedOff,
    /// <summary>Nothing could be read from <c>wg.exe</c>.</summary>
    Unreadable,
}

internal enum LocalCheck
{
    TunnelOff,
    NoAddress,
    Listening,
    NotListening,
}

/// <summary>
/// The logic of the module without any host type: lists the tunnels, turns one on or off the way the WireGuard app does, reads its status. Approval,
/// pages and notifications are the module's job; this class never asks anyone and never reads a configuration. One change runs at a time.
/// </summary>
internal sealed class TunnelManager
{
    /// <summary>"Connected" means the newest handshake is younger than this (a tunnel with a keepalive renews every two minutes at most).</summary>
    public static readonly TimeSpan ConnectedLimit = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan HandshakeWait = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ServicePoll = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan ServiceWait = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan HandshakePoll = TimeSpan.FromSeconds(1);

    private readonly IWireGuardTools _tools;
    private readonly ITunnelStore _store;
    private readonly IServiceControl _services;
    private readonly INetworkProbe _network;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private bool _changing;

    public TunnelManager(IWireGuardTools tools, ITunnelStore store, IServiceControl services, INetworkProbe network, IClock clock)
    {
        _tools = tools;
        _store = store;
        _services = services;
        _network = network;
        _clock = clock;
    }

    public ToolProbe Probe() => _tools.Probe();

    /// <summary>True while a change runs.</summary>
    public bool Busy { get { lock (_gate) return _changing; } }

    /// <summary>The tunnels in the store with the state of each service.</summary>
    public TunnelList List()
    {
        var listing = _store.List();
        var result = new List<TunnelInfo>(listing.Tunnels.Count);
        foreach (var t in listing.Tunnels)
            result.Add(new TunnelInfo(t.Name, TunnelNames.Slug(t.Name), t.Path, _services.Query(TunnelNames.ServiceName(t.Name))));
        return new TunnelList(listing.Readable, result);
    }

    public TunnelInfo? Find(string name) => List().Tunnels.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Takes the one-change slot, or says it is taken. Release with <see cref="End"/>.</summary>
    public bool TryBegin()
    {
        lock (_gate)
        {
            if (_changing) return false;
            _changing = true;
            return true;
        }
    }

    public void End() { lock (_gate) _changing = false; }

    /// <summary>
    /// Turns a tunnel on or off. It holds the one-change slot only for the work itself. The caller has already asked the user; nothing here asks.
    /// On: if a stale service of that name exists and is not running it is removed first, then <c>/installtunnelservice</c> with the existing store file,
    /// then the service is waited for until it runs. Off: <c>/uninstalltunnelservice</c>, then the service is waited for until it is gone.
    /// </summary>
    public async Task<ChangeResult> ChangeAsync(string name, bool wanted, CancellationToken ct)
    {
        if (!TunnelNames.IsValid(name)) return new ChangeResult(ChangeStatus.UnknownTunnel);
        if (!TryBegin()) return new ChangeResult(ChangeStatus.Busy);
        try
        {
            var probe = _tools.Probe();
            if (!probe.Usable) return new ChangeResult(ChangeStatus.ToolsUnusable, new Problem(probe.ReasonKey ?? "probe.no-wireguard"));
            var service = TunnelNames.ServiceName(name);

            if (!wanted)
            {
                if (!_services.Query(service).Exists) return new ChangeResult(ChangeStatus.AlreadyThere);
                var run = await _tools.UninstallTunnelServiceAsync(name, ct).ConfigureAwait(false);
                if (!await WaitUntil(() => !_services.Query(service).Exists, ct).ConfigureAwait(false))
                    return new ChangeResult(ChangeStatus.Failed, run.TimedOut ? new Problem("err.timeout", name) : new Problem("err.uninstall-failed", name, run.ExitCode));
                return new ChangeResult(ChangeStatus.Done);
            }

            var stored = _store.List().Tunnels.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (stored is null) return new ChangeResult(ChangeStatus.UnknownTunnel, new Problem("err.config-missing", name));

            var before = _services.Query(service);
            if (before.IsRunning) return new ChangeResult(ChangeStatus.AlreadyThere);
            if (before.Exists)
            {
                // A service that exists but does not run would make /installtunnelservice fail: remove it, then install (the approved action is still "turn on").
                await _tools.UninstallTunnelServiceAsync(stored.Name, ct).ConfigureAwait(false);
                await WaitUntil(() => !_services.Query(service).Exists, ct).ConfigureAwait(false);
            }

            var install = await _tools.InstallTunnelServiceAsync(stored.Path, ct).ConfigureAwait(false);
            if (install.TimedOut) return new ChangeResult(ChangeStatus.Failed, new Problem("err.timeout", stored.Name));
            bool running = await WaitUntil(() => _services.Query(service).IsRunning, ct).ConfigureAwait(false);
            if (running) return new ChangeResult(ChangeStatus.Done);

            // It did not come up: say why from the service's own last error, and do not leave a half-installed service behind.
            var after = _services.Query(service);
            var problem = FailureOf(stored.Name, install, after);
            if (after.Exists) await _tools.UninstallTunnelServiceAsync(stored.Name, ct).ConfigureAwait(false);
            return new ChangeResult(ChangeStatus.Failed, problem);
        }
        finally { End(); }
    }

    private static Problem FailureOf(string name, ToolRun install, ServiceSnapshot after)
    {
        if (install.StartFailed) return new Problem("err.install-not-run", name);
        if (after.Win32ExitCode != 0) return new Problem("err.service-failed", name, DescribeWin32(after.Win32ExitCode));
        if (after.ServiceExitCode != 0) return new Problem("err.service-failed", name, "service error " + after.ServiceExitCode);
        return new Problem("err.install-failed", name, install.ExitCode);
    }

    private static string DescribeWin32(int code)
    {
        try { return new Win32Exception(code).Message + " (" + code + ")"; }
        catch (Exception) { return "error " + code; }
    }

    private async Task<bool> WaitUntil(Func<bool> condition, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            if (condition()) return true;
            if (waited >= ServiceWait) return false;
            await _clock.Delay(ServicePoll, ct).ConfigureAwait(false);
            waited += ServicePoll;
        }
    }

    /// <summary>
    /// After a turn-on: waits up to <paramref name="timeout"/> for a handshake, once a second. The service is new, so any handshake it reports is a fresh one.
    /// </summary>
    public async Task<HandshakeOutcome> WaitForHandshakeAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        var service = TunnelNames.ServiceName(name);
        var waited = TimeSpan.Zero;
        bool readAny = false;
        while (true)
        {
            if (!_services.Query(service).IsRunning) return HandshakeOutcome.TurnedOff;
            var text = await _tools.ShowAsync(name, WgField.LatestHandshakes, ct).ConfigureAwait(false);
            if (text is not null)
            {
                readAny = true;
                var at = WgParsers.LatestHandshake(text);
                if (at is { } t && _clock.Now - t <= ConnectedLimit) return HandshakeOutcome.Connected;
            }
            if (waited >= timeout) return readAny ? HandshakeOutcome.NoHandshake : HandshakeOutcome.Unreadable;
            await _clock.Delay(HandshakePoll, ct).ConfigureAwait(false);
            waited += HandshakePoll;
        }
    }

    /// <summary>Latest handshake, transfer counters and endpoint through the three allowed <c>wg show</c> fields; null when nothing could be read.</summary>
    public async Task<WgReading?> ReadAsync(string name, CancellationToken ct)
    {
        var handshakes = await _tools.ShowAsync(name, WgField.LatestHandshakes, ct).ConfigureAwait(false);
        var transfer = await _tools.ShowAsync(name, WgField.Transfer, ct).ConfigureAwait(false);
        var endpoints = await _tools.ShowAsync(name, WgField.Endpoints, ct).ConfigureAwait(false);
        if (handshakes is null && transfer is null && endpoints is null) return null;
        var t = WgParsers.Transfer(transfer);
        return new WgReading(WgParsers.LatestHandshake(handshakes), t?.Rx, t?.Tx, WgParsers.Endpoint(endpoints));
    }

    /// <summary>Whether something listens on <paramref name="port"/> at the tunnel's own address.</summary>
    public LocalCheck CheckPort(string name, int port, bool tunnelOn)
    {
        if (!tunnelOn) return LocalCheck.TunnelOff;
        var addresses = _network.AddressesOf(name);
        if (addresses.Count == 0) return LocalCheck.NoAddress;
        return _network.IsListening(addresses, port) ? LocalCheck.Listening : LocalCheck.NotListening;
    }

    /// <summary>Sets the start type of the tunnel's service. Returns 0 or the Win32 error. The service must exist.</summary>
    public int SetStart(string name, bool automatic)
    {
        if (!TunnelNames.IsValid(name)) return 87;   // ERROR_INVALID_PARAMETER
        return _services.SetStartKind(TunnelNames.ServiceName(name), automatic ? StartKind.Automatic : StartKind.Manual);
    }
}

using System.Net;

namespace Rigbay.Tunnel.Wg;

// Everything the module needs from the machine sits behind these small interfaces, so the tests drive the whole module with fakes
// (no process, no service, no file). The real implementations are in the other files of this folder and are wired in Dependencies.cs.

/// <summary>What <c>wg.exe show &lt;tunnel&gt; &lt;field&gt;</c> may be asked for. There is deliberately no value for a bare <c>show</c>, <c>dump</c> or
/// <c>showconf</c>: those print the private key (or the whole configuration), and the type makes asking for them impossible.</summary>
internal enum WgField
{
    LatestHandshakes,
    Transfer,
    Endpoints,
}

/// <summary>State of a Windows service as far as this module cares.</summary>
internal enum ServiceState
{
    Missing,
    Stopped,
    StartPending,
    Running,
    StopPending,
    Other,
}

/// <summary>How a service starts.</summary>
internal enum StartKind
{
    Automatic,
    Manual,
    Disabled,
    Other,
}

/// <summary>One look at a service. <see cref="Win32ExitCode"/> is the service's last exit code (0 when it never failed).</summary>
internal sealed record ServiceSnapshot(ServiceState State, StartKind Start, int Win32ExitCode, int ServiceExitCode)
{
    public bool Exists => State != ServiceState.Missing;
    public bool IsRunning => State == ServiceState.Running;
    public static ServiceSnapshot Missing { get; } = new(ServiceState.Missing, StartKind.Other, 0, 0);
}

/// <summary>Reads a service and changes its start type (nothing else: the module never starts or stops a service itself).</summary>
internal interface IServiceControl
{
    ServiceSnapshot Query(string serviceName);

    /// <summary>Sets the start type. Returns 0 on success, otherwise the Win32 error code.</summary>
    int SetStartKind(string serviceName, StartKind start);
}

/// <summary>Result of running a WireGuard command. The command's own output is never kept (it can quote a configuration).</summary>
internal sealed record ToolRun(int ExitCode, bool TimedOut = false, bool StartFailed = false)
{
    public bool Ok => ExitCode == 0 && !TimedOut && !StartFailed;
}

/// <summary>Whether the official WireGuard for Windows is installed and genuine.</summary>
/// <param name="Usable">True when both programs are present and signed by WireGuard LLC.</param>
/// <param name="ReasonKey">Module-relative language key of the reason when not usable.</param>
internal sealed record ToolProbe(bool Usable, string? ReasonKey)
{
    public static ToolProbe Yes { get; } = new(true, null);
}

/// <summary>The two programs of WireGuard for Windows, used in exactly the ways the official app uses them.</summary>
internal interface IWireGuardTools
{
    /// <summary>Read-only: are <c>wireguard.exe</c> and <c>wg.exe</c> there and signed by WireGuard LLC?</summary>
    ToolProbe Probe();

    /// <summary><c>wireguard.exe /installtunnelservice "&lt;config file&gt;"</c>: installs and starts the tunnel service (what Activate does).</summary>
    Task<ToolRun> InstallTunnelServiceAsync(string configPath, CancellationToken ct);

    /// <summary><c>wireguard.exe /uninstalltunnelservice &lt;name&gt;</c>: stops and removes the tunnel service (what Deactivate does).</summary>
    Task<ToolRun> UninstallTunnelServiceAsync(string name, CancellationToken ct);

    /// <summary><c>wg.exe show &lt;name&gt; &lt;field&gt;</c>; the text it printed, or null when it failed. Only the three fields of <see cref="WgField"/>.</summary>
    Task<string?> ShowAsync(string name, WgField field, CancellationToken ct);
}

/// <summary>A configuration in WireGuard's own store. Only its name and path are ever known; the content is never read.</summary>
internal sealed record StoredTunnel(string Name, string Path);

/// <summary>The list of tunnels in WireGuard's store.</summary>
/// <param name="Tunnels">The tunnels, ordered by name.</param>
/// <param name="Readable">False when the folder could not be listed (not elevated, folder missing).</param>
internal sealed record StoreListing(IReadOnlyList<StoredTunnel> Tunnels, bool Readable);

/// <summary>Lists the names of the configurations in WireGuard's store (<c>Data\Configurations</c>).</summary>
internal interface ITunnelStore
{
    StoreListing List();
}

/// <summary>Network facts used by the "check a local service" option.</summary>
internal interface INetworkProbe
{
    /// <summary>The IPv4 and IPv6 unicast addresses of the adapter that has this name (WireGuard names its adapter after the tunnel).</summary>
    IReadOnlyList<IPAddress> AddressesOf(string adapterName);

    /// <summary>Whether something listens on TCP <paramref name="port"/> on one of the addresses (or on every address).</summary>
    bool IsListening(IReadOnlyList<IPAddress> addresses, int port);
}

/// <summary>Time, so tests do not wait.</summary>
internal interface IClock
{
    DateTimeOffset Now { get; }
    Task Delay(TimeSpan span, CancellationToken ct);
}

/// <summary>The real clock.</summary>
internal sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public Task Delay(TimeSpan span, CancellationToken ct) => Task.Delay(span, ct);
}

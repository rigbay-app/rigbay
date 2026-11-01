using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Rigbay.SteamBypass;

/// <summary>How the Steam client relates to the bypass, for the status row.</summary>
internal enum SteamState
{
    /// <summary>steam.exe is not running.</summary>
    NotRunning,

    /// <summary>Steam runs but the bypass is off.</summary>
    RunningBypassOff,

    /// <summary>Steam or the bypass was started a moment ago; Steam has not connected to the proxy yet.</summary>
    Starting,

    /// <summary>Steam has a connection to the proxy.</summary>
    Using,

    /// <summary>Steam started before the bypass and reads the proxy setting only at start: it has to be restarted.</summary>
    NeedsRestart,
}

/// <summary>A running steam.exe.</summary>
internal sealed record SteamProcess(int Pid, DateTimeOffset Started);

/// <summary>Looks at the machine for Steam. Read-only.</summary>
internal interface ISteamProbe
{
    /// <summary>The running steam.exe processes (the client itself, not its web helpers).</summary>
    IReadOnlyList<SteamProcess> Processes();

    /// <summary>True when one of the processes has an established connection to 127.0.0.1:<paramref name="port"/>.</summary>
    bool AnyConnectedTo(IReadOnlyCollection<int> pids, int port);
}

/// <summary>Decides the Steam row's state. A pure function, so the rules can be tested without Steam.</summary>
internal static class SteamLink
{
    /// <summary>How long Steam and the bypass must both have been around before a missing connection means "restart Steam".</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(45);

    /// <summary>Computes the state.</summary>
    /// <param name="bypassOn">The bypass is on.</param>
    /// <param name="onSince">When it was turned on.</param>
    /// <param name="processes">The running steam.exe processes.</param>
    /// <param name="usesProxy">Whether one of them is connected to the proxy.</param>
    /// <param name="now">The time.</param>
    public static SteamState Evaluate(bool bypassOn, DateTimeOffset? onSince, IReadOnlyList<SteamProcess> processes, bool usesProxy, DateTimeOffset now)
    {
        if (processes.Count == 0) return SteamState.NotRunning;
        if (!bypassOn) return SteamState.RunningBypassOff;
        if (usesProxy) return SteamState.Using;
        var oldestStart = processes.Min(p => p.Started);
        if (now - oldestStart < Grace || (onSince is { } since && now - since < Grace)) return SteamState.Starting;
        return SteamState.NeedsRestart;
    }
}

/// <summary>The real probe: process list and the IPv4 TCP table of the machine.</summary>
internal sealed class SteamProbe : ISteamProbe
{
    /// <inheritdoc />
    public IReadOnlyList<SteamProcess> Processes()
    {
        var list = new List<SteamProcess>();
        foreach (var p in Process.GetProcessesByName("steam"))
        {
            try { list.Add(new SteamProcess(p.Id, new DateTimeOffset(p.StartTime))); }
            catch (Exception) { /* exited meanwhile, or no access */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    /// <inheritdoc />
    public bool AnyConnectedTo(IReadOnlyCollection<int> pids, int port)
    {
        if (pids.Count == 0) return false;
        var buffer = IntPtr.Zero;
        try
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, TcpTableOwnerPidAll, 0);   // asks how many bytes are needed
            if (size <= 0) return false;
            size += 8192;   // the table may have grown since the size was asked
            buffer = Marshal.AllocHGlobal(size);
            if (GetExtendedTcpTable(buffer, ref size, false, 2, TcpTableOwnerPidAll, 0) != 0) return false;

            int rows = Marshal.ReadInt32(buffer);
            var loopback = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
            for (int i = 0; i < rows; i++)
            {
                // MIB_TCPROW_OWNER_PID, 24 bytes: state, local address, local port, remote address, remote port, pid.
                var row = IntPtr.Add(buffer, 4 + i * 24);
                int state = Marshal.ReadInt32(row, 0);
                uint remoteAddress = (uint)Marshal.ReadInt32(row, 12);
                int remotePort = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 16));
                int pid = Marshal.ReadInt32(row, 20);
                if (state == Established && remoteAddress == loopback && remotePort == port && pids.Contains(pid)) return true;
            }
            return false;
        }
        catch (Exception) { return false; }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
    }

    private const int TcpTableOwnerPidAll = 5;
    private const int Established = 5;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
}

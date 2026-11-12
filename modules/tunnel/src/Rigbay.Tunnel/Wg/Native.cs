using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Rigbay.Tunnel.Wg;

/// <summary>Reads the Windows service of a tunnel and sets its start type, with the service control manager directly (no extra package).</summary>
internal sealed class ServiceControl : IServiceControl
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001, ServiceChangeConfig = 0x0002, ServiceQueryStatus = 0x0004;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const int ErrorServiceDoesNotExist = 1060;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, IntPtr buffer, uint size, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryServiceConfigW(IntPtr service, IntPtr buffer, uint size, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType, uint errorControl, string? binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? startName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    public ServiceSnapshot Query(string serviceName)
    {
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var svc = OpenServiceW(scm, serviceName, ServiceQueryConfig | ServiceQueryStatus);
            if (svc == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist) return ServiceSnapshot.Missing;
                throw new Win32Exception(error);
            }
            try
            {
                var state = ServiceState.Other;
                int win32 = 0, specific = 0;
                IntPtr buffer = Marshal.AllocHGlobal(256);
                try
                {
                    if (QueryServiceStatusEx(svc, 0, buffer, 256, out _))
                    {
                        // SERVICE_STATUS_PROCESS: type, current state, controls accepted, win32 exit code, service exit code, ...
                        state = Marshal.ReadInt32(buffer, 4) switch
                        {
                            1 => ServiceState.Stopped,
                            2 => ServiceState.StartPending,
                            3 => ServiceState.StopPending,
                            4 => ServiceState.Running,
                            _ => ServiceState.Other,
                        };
                        win32 = Marshal.ReadInt32(buffer, 12);
                        specific = Marshal.ReadInt32(buffer, 16);
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }

                var start = StartKind.Other;
                QueryServiceConfigW(svc, IntPtr.Zero, 0, out uint needed);   // ask for the size
                if (needed is > 0 and < 65536)
                {
                    IntPtr cfg = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        if (QueryServiceConfigW(svc, cfg, needed, out _))
                            start = Marshal.ReadInt32(cfg, 4) switch   // QUERY_SERVICE_CONFIG: service type, start type, ...
                            {
                                2 => StartKind.Automatic,
                                3 => StartKind.Manual,
                                4 => StartKind.Disabled,
                                _ => StartKind.Other,
                            };
                    }
                    finally { Marshal.FreeHGlobal(cfg); }
                }
                return new ServiceSnapshot(state, start, win32, specific);
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    public int SetStartKind(string serviceName, StartKind start)
    {
        uint kind = start switch { StartKind.Automatic => 2u, StartKind.Manual => 3u, _ => throw new ArgumentOutOfRangeException(nameof(start), "only Automatic and Manual can be set") };
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return Marshal.GetLastWin32Error();
        try
        {
            var svc = OpenServiceW(scm, serviceName, ServiceChangeConfig | ServiceQueryConfig);
            if (svc == IntPtr.Zero) return Marshal.GetLastWin32Error();
            try
            {
                // Only the start type changes: every other argument is "no change".
                return ChangeServiceConfigW(svc, ServiceNoChange, kind, ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null) ? 0 : Marshal.GetLastWin32Error();
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }
}

/// <summary>Authenticode check with the operating system's own verifier (no network: the revocation check is off and only cached data is used).</summary>
internal sealed class AuthenticodeCheck : ISignatureCheck
{
    private const string PublisherName = "WireGuard LLC";

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WintrustData data);

    public bool IsSignedByWireGuard(string path)
    {
        if (!File.Exists(path)) return false;
        IntPtr pathPtr = Marshal.StringToHGlobalUni(path);
        IntPtr filePtr = IntPtr.Zero;
        try
        {
            var file = new WintrustFileInfo { StructSize = (uint)Marshal.SizeOf<WintrustFileInfo>(), FilePath = pathPtr };
            filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WintrustData
            {
                StructSize = (uint)Marshal.SizeOf<WintrustData>(),
                UiChoice = 2,                       // WTD_UI_NONE
                RevocationChecks = 0,               // WTD_REVOKE_NONE
                UnionChoice = 1,                    // WTD_CHOICE_FILE
                FileInfo = filePtr,
                StateAction = 0,                    // WTD_STATEACTION_IGNORE
                ProviderFlags = 0x10 | 0x1000,      // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) return false;
        }
        finally
        {
            if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }

        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return string.Equals(cert.GetNameInfo(X509NameType.SimpleName, false), PublisherName, StringComparison.Ordinal);
        }
        catch (Exception) { return false; }
    }
}

/// <summary>Lists the names in WireGuard's store: <c>*.conf.dpapi</c> (what the manager keeps) and <c>*.conf</c> (a file the manager has not converted yet). Content is never opened.</summary>
internal sealed class TunnelStore : ITunnelStore
{
    private readonly string _folder;

    public TunnelStore(string folder) => _folder = folder;

    public StoreListing List()
    {
        try
        {
            var byName = new Dictionary<string, StoredTunnel>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(_folder))
            {
                var file = Path.GetFileName(path);
                bool dpapi = file.EndsWith(".conf.dpapi", StringComparison.OrdinalIgnoreCase);
                bool plain = !dpapi && file.EndsWith(".conf", StringComparison.OrdinalIgnoreCase);
                if (!dpapi && !plain) continue;
                var name = file[..^(dpapi ? ".conf.dpapi".Length : ".conf".Length)];
                if (!TunnelNames.IsValid(name)) continue;
                // Both forms present (the manager is converting): the encrypted one wins, it is the one WireGuard keeps.
                if (byName.TryGetValue(name, out var known) && (plain || known.Path.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase))) continue;
                byName[name] = new StoredTunnel(name, path);
            }
            return new StoreListing(byName.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList(), true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new StoreListing(Array.Empty<StoredTunnel>(), false);
        }
    }
}

/// <summary>Adapter addresses and TCP listeners from the .NET network classes; nothing is sent on the network.</summary>
internal sealed class NetworkProbe : INetworkProbe
{
    public IReadOnlyList<IPAddress> AddressesOf(string adapterName)
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!string.Equals(nic.Name, adapterName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
                if (u.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal)
                    result.Add(u.Address);
        }
        return result;
    }

    public bool IsListening(IReadOnlyList<IPAddress> addresses, int port)
    {
        foreach (var l in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
        {
            if (l.Port != port) continue;
            if (IPAddress.Any.Equals(l.Address) || IPAddress.IPv6Any.Equals(l.Address)) return true;
            if (addresses.Any(a => a.Equals(l.Address))) return true;
        }
        return false;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;
using Rigbay.Abstractions;

namespace Rigbay.Module.Xtu;

/// <summary>Where XTU keeps its data files on this machine (read only). The module needs no file of Intel's program folder.</summary>
internal sealed record XtuPaths(string DataDir)
{
    public const string ServiceName = "XTU3SERVICE";

    public string ProfilesXml => Path.Combine(DataDir, "Profiles.xml");
    public string UiSettings => Path.Combine(DataDir, "uisettings.xml");
    public string HotkeySettings => Path.Combine(DataDir, "xtu.hotkey.settings");

    /// <summary>The default data folder: <c>%ProgramData%\Intel\Intel Extreme Tuning Utility</c>.</summary>
    public static XtuPaths Default() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Intel", "Intel Extreme Tuning Utility"));
}

internal enum ServiceState { NotFound, Stopped, Running, Other }

/// <summary>Whether a named pipe endpoint of the XTU service accepts a connection.</summary>
internal enum PipeState
{
    /// <summary>Not answered (no endpoint is listening there).</summary>
    Missing,
    /// <summary>A connection was accepted.</summary>
    Listening,
    /// <summary>Could not tell (for example the caller is not elevated): never a reason to refuse the machine.</summary>
    Unknown,
}

/// <summary>Read-only facts about the machine. Faked in tests.</summary>
internal interface ISystemProbe
{
    /// <summary>The CPU vendor string (<c>GenuineIntel</c>, <c>AuthenticAMD</c>), or empty when unknown.</summary>
    string CpuVendor();
    ServiceState Service(string name);
    /// <summary>Connects to the endpoint and disconnects at once (no operation is called). Quick: a local pipe.</summary>
    PipeState Pipe(string uri);
    /// <summary>The file version of the executable a Windows service runs, or empty.</summary>
    string ServiceVersion(string serviceName);
    /// <summary><c>On</c>, <c>Off</c> or <c>?</c> (Windows Fast Startup; it makes "Shut down" skip the XTU service restart, so limits return to BIOS defaults).</summary>
    string FastStartup();
    PowerLine GetPowerLine();
}

internal static class ProbeLogic
{
    /// <summary>
    /// Supported only on an Intel CPU, with the XTU service installed and running and its two named pipes accepting a
    /// connection. No file of Intel's is needed (the contracts are the module's own). Clear reasons otherwise
    /// (module-relative keys). Read-only and quick.
    /// </summary>
    public static ProbeResult Run(ISystemProbe sys)
    {
        var vendor = sys.CpuVendor();
        if (vendor.Length > 0 && !vendor.Equals("GenuineIntel", StringComparison.OrdinalIgnoreCase))
            return ProbeResult.No("probe.not-intel", vendor);

        var service = sys.Service(XtuPaths.ServiceName);
        if (service == ServiceState.NotFound) return ProbeResult.No("probe.no-service", XtuPaths.ServiceName);
        if (service != ServiceState.Running) return ProbeResult.No("probe.service-stopped", XtuPaths.ServiceName);

        foreach (var pipe in new[] { Intel.Pipes.Tuning, Intel.Pipes.Monitoring })
            if (sys.Pipe(pipe) == PipeState.Missing) return ProbeResult.No("probe.no-pipe", pipe);
        return ProbeResult.Yes();
    }
}

internal sealed class WindowsSystemProbe : ISystemProbe
{
    public string CpuVendor()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("VendorIdentifier") as string ?? "";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return ""; }
    }

    public ServiceState Service(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                _ => ServiceState.Other,
            };
        }
        catch (InvalidOperationException) { return ServiceState.NotFound; }   // "Service not found on computer '.'"
        catch (System.ComponentModel.Win32Exception) { return ServiceState.NotFound; }
    }

    public PipeState Pipe(string uri) => Intel.PipeCheck.State(uri);

    public string ServiceVersion(string serviceName)
    {
        // The service's own image path from the Service Control Manager's registry entry (read only); it may be quoted and carry arguments.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
            var path = ExecutableOf(key?.GetValue("ImagePath") as string ?? "");
            return path.Length == 0 ? "" : FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or System.Security.SecurityException or UnauthorizedAccessException or ArgumentException) { return ""; }
    }

    /// <summary>The executable in a service's <c>ImagePath</c>: quoted or not, with or without arguments, environment variables expanded.</summary>
    internal static string ExecutableOf(string imagePath)
    {
        var image = imagePath.Trim();
        if (image.Length == 0) return "";
        string path;
        if (image[0] == '"') path = image.Split('"', StringSplitOptions.RemoveEmptyEntries)[0];
        else
        {
            var exe = image.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe >= 0 ? image[..(exe + 4)] : image;
        }
        return Environment.ExpandEnvironmentVariables(path);
    }

    public string FastStartup()
    {
        try
        {
            var v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", null);
            return v is null ? "?" : Convert.ToInt32(v) == 0 ? "Off" : "On";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or FormatException) { return "?"; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public PowerLine GetPowerLine()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s)) return PowerLine.Unknown;
            return s.ACLineStatus switch { 1 => PowerLine.Online, 0 => PowerLine.Offline, _ => PowerLine.Unknown };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return PowerLine.Unknown; }
    }
}

/// <summary>The real power environment: <see cref="SystemEvents.PowerModeChanged"/> and the AC line from Windows.</summary>
internal sealed class WindowsPowerEnvironment : IPowerEnvironment
{
    private readonly ISystemProbe _sys;
    private readonly Action<Exception> _onError;

    public WindowsPowerEnvironment(ISystemProbe sys, Action<Exception> onError)
    {
        _sys = sys;
        _onError = onError;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public PowerLine Line => _sys.GetPowerLine();
    public event Action? Changed;
    public event Action? Resumed;

    // Raised on a system thread, outside the host's fault boundary: nothing may escape.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        try
        {
            Changed?.Invoke();
            if (e.Mode == PowerModes.Resume) Resumed?.Invoke();
        }
        catch (Exception ex) { _onError(ex); }
    }

    public void Dispose() => SystemEvents.PowerModeChanged -= OnPowerModeChanged;
}

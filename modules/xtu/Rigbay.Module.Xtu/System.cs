using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;
using Rigbay.Abstractions;

namespace Rigbay.Module.Xtu;

/// <summary>Where XTU keeps its files on this machine.</summary>
internal sealed record XtuPaths(string DriversDir, string DataDir)
{
    public const string ServiceName = "XTU3SERVICE";

    public string InterfaceDll => Path.Combine(DriversDir, "XtuInterface.dll");
    public string CommonDll => Path.Combine(DriversDir, "XtuCommon.dll");
    public string ServiceExe => Path.Combine(DriversDir, "XtuService.exe");
    public string ProfilesXml => Path.Combine(DataDir, "Profiles.xml");
    public string UiSettings => Path.Combine(DataDir, "uisettings.xml");
    public string HotkeySettings => Path.Combine(DataDir, "xtu.hotkey.settings");

    /// <summary>The default install: <c>%ProgramFiles%\Intel\Intel(R) Extreme Tuning Utility\Drivers</c> and <c>%ProgramData%\Intel\Intel Extreme Tuning Utility</c>.</summary>
    public static XtuPaths Default() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "Intel(R) Extreme Tuning Utility", "Drivers"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Intel", "Intel Extreme Tuning Utility"));
}

internal enum ServiceState { NotFound, Stopped, Running, Other }

/// <summary>Read-only facts about the machine. Faked in tests.</summary>
internal interface ISystemProbe
{
    /// <summary>The CPU vendor string (<c>GenuineIntel</c>, <c>AuthenticAMD</c>), or empty when unknown.</summary>
    string CpuVendor();
    ServiceState Service(string name);
    bool FileExists(string path);
    /// <summary>The file version of an executable, or empty.</summary>
    string FileVersion(string path);
    /// <summary><c>On</c>, <c>Off</c> or <c>?</c> (Windows Fast Startup; it makes "Shut down" skip the XTU service restart, so limits return to BIOS defaults).</summary>
    string FastStartup();
    PowerLine GetPowerLine();
}

internal static class ProbeLogic
{
    /// <summary>
    /// Supported only on an Intel CPU, with the XTU service installed and running, and Intel's contract DLLs in the Intel
    /// folder. Clear reasons otherwise (module-relative keys). Read-only and quick.
    /// </summary>
    public static ProbeResult Run(ISystemProbe sys, XtuPaths paths)
    {
        var vendor = sys.CpuVendor();
        if (vendor.Length > 0 && !vendor.Equals("GenuineIntel", StringComparison.OrdinalIgnoreCase))
            return ProbeResult.No("probe.not-intel", vendor);

        var service = sys.Service(XtuPaths.ServiceName);
        if (service == ServiceState.NotFound) return ProbeResult.No("probe.no-service", XtuPaths.ServiceName);

        if (!sys.FileExists(paths.InterfaceDll) || !sys.FileExists(paths.CommonDll))
            return ProbeResult.No("probe.no-dlls", paths.DriversDir);

        if (service != ServiceState.Running) return ProbeResult.No("probe.service-stopped", XtuPaths.ServiceName);
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

    public bool FileExists(string path) => File.Exists(path);

    public string FileVersion(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileVersion ?? ""; }
        catch (Exception ex) when (ex is IOException or FileNotFoundException) { return ""; }
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

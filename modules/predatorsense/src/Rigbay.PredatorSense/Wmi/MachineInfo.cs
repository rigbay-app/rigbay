using System.Management;
using Microsoft.Win32;

namespace Rigbay.PredatorSense.Wmi;

/// <summary>
/// The real <see cref="IMachineInfo"/>. Reads the service control database key, the WMI class schema (no instance, no
/// method) and the firmware's model strings from the registry. All of it is read-only and works without administrator.
/// </summary>
public sealed class MachineInfo : IMachineInfo
{
    private readonly TimeSpan _schemaTimeout;

    /// <summary>Creates the object.</summary>
    public MachineInfo(TimeSpan? schemaTimeout = null) => _schemaTimeout = schemaTimeout ?? TimeSpan.FromSeconds(6);

    /// <inheritdoc />
    public bool ServiceExists(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
        return key is not null;
    }

    /// <inheritdoc />
    public bool WmiClassExists(string wmiNamespace, string className)
    {
        // ManagementClass.Get() can stall on a broken WMI repository: give it a task and a timeout.
        var task = Task.Run(() =>
        {
            try
            {
                using var cls = new ManagementClass(wmiNamespace, className, null);
                cls.Get();   // throws ManagementException "Not found" when the class is absent
                return true;
            }
            catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.NotFound or ManagementStatus.InvalidClass or ManagementStatus.InvalidNamespace) { return false; }
        });
        if (!task.Wait(_schemaTimeout)) throw new TimeoutException("WMI did not answer the schema question in time.");
        return task.Result;
    }

    /// <inheritdoc />
    public string? ModelName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (key is null) return null;
            var maker = (key.GetValue("SystemManufacturer") as string)?.Trim();
            var model = (key.GetValue("SystemProductName") as string)?.Trim();
            if (string.IsNullOrEmpty(model)) return string.IsNullOrEmpty(maker) ? null : maker;
            return string.IsNullOrEmpty(maker) || model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? model : maker + " " + model;
        }
        catch (Exception) { return null; }
    }
}

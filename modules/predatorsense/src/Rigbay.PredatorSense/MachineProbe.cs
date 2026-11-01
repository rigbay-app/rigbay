using Rigbay.Abstractions;
using Rigbay.PredatorSense.Wmi;

namespace Rigbay.PredatorSense;

/// <summary>
/// "Is this machine supported": the Windows service <c>PSSvc</c> exists AND the WMI class <c>root\WMI:AcerGamingFunction</c>
/// exists. Read-only (the registry key of the service, the class schema); no method is called and nothing is started. No model
/// name is hard-wired: a different Acer with the same service and class is supported too. Unsupported is an answer with a
/// reason, never an exception.
/// </summary>
public static class MachineProbe
{
    /// <summary>Name of the Acer PredatorSense service.</summary>
    public const string ServiceName = "PSSvc";

    /// <summary>Runs the probe.</summary>
    public static ProbeResult Run(IMachineInfo machine)
    {
        try
        {
            if (!machine.ServiceExists(ServiceName)) return ProbeResult.No("probe.no-service");
            if (!machine.WmiClassExists(WmiAllowList.Namespace, WmiAllowList.GamingClass)) return ProbeResult.No("probe.no-wmi");
            return ProbeResult.Yes();
        }
        catch (Exception ex)
        {
            return ProbeResult.No("probe.check-failed", ex.Message);
        }
    }
}

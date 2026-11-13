using Rigbay.Abstractions;
using Rigbay.Gpu.Nvml;

namespace Rigbay.Gpu;

/// <summary>
/// "Is this machine supported": the NVML library loads (done before), NVML initialises, and it reports at least one GPU with a name. Read only:
/// the session is shut down again before the answer. Unsupported is an answer with a reason, never an exception.
/// </summary>
internal static class GpuProbe
{
    public static ProbeResult Run(INvml nvml)
    {
        var code = nvml.Init();
        if (code != NvmlReturn.Success) return ProbeResult.No("probe.init-failed", nvml.ErrorString(code));
        try
        {
            code = nvml.GetCount(out var count);
            if (code != NvmlReturn.Success) return ProbeResult.No("probe.init-failed", nvml.ErrorString(code));
            if (count == 0) return ProbeResult.No("probe.no-device");
            code = nvml.GetHandle(0, out var device);
            if (code == NvmlReturn.Success) code = nvml.GetName(device, out _);
            return code == NvmlReturn.Success ? ProbeResult.Yes() : ProbeResult.No("probe.init-failed", nvml.ErrorString(code));
        }
        finally
        {
            nvml.Shutdown();
        }
    }
}

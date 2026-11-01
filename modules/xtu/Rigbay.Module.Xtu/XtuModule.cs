using Rigbay.Abstractions;

namespace Rigbay.Module.Xtu;

/// <summary>
/// The XTU profiles module. The class itself only wires the real parts (service probe, Intel-backed client, Windows power
/// events) into an <see cref="XtuRuntime"/>; the behaviour lives there and is tested with fakes.
/// </summary>
public sealed class XtuModule : IModule
{
    private XtuRuntime? _runtime;

    // The constructor does nothing but set fields.
    public XtuModule() { }

    public ProbeResult Probe() => ProbeLogic.Run(new WindowsSystemProbe(), XtuPaths.Default());

    public void Initialize(IHostServices host)
    {
        // Intel's assemblies and the WCF client are first touched inside CreateRuntime, after the loader knows the module's
        // probePaths (module.json), so nothing needs a resolver of its own.
        _runtime = CreateRuntime(host);
        _runtime.Start();
        _runtime.StartTimers();
    }

    public PageModel? GetPageModel(IHostServices host) => _runtime?.BuildPage();

    public void Shutdown()
    {
        _runtime?.Dispose();
        _runtime = null;
    }

    private static XtuRuntime CreateRuntime(IHostServices host)
    {
        var sys = new WindowsSystemProbe();
        var latch = new WriteLatch(host.DataDirectory);
        var client = new XtuClient(new Intel.IntelTuningPort(latch), new Intel.IntelMonitorPort(), latch);
        var power = new WindowsPowerEnvironment(sys, ex => host.Log.Error("power event failed", ex));
        return new XtuRuntime(host, client, sys, XtuPaths.Default(), power, latch);
    }
}

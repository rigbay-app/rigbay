using Rigbay.Abstractions;

namespace Rigbay.Module.Broken;

/// <summary>Probe passes, CreatePage throws: the host must show a failed bay, not crash.</summary>
public sealed class BrokenModule : IRigbayModule
{
    public string Id => "spike.broken";
    public string Name => "Broken module";
    public string Group => "Power";
    public Version Version => new(0, 0, 1);
    public ProbeResult Probe() => ProbeResult.Yes();
    public object CreatePage(IHostServices host) =>
        throw new InvalidOperationException("Sensor service 'RigbayFakeSvc' did not answer within 5 s.");
}

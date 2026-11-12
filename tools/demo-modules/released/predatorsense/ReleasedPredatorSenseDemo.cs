using Rigbay.Abstractions;
using Rigbay.PredatorSense.Wmi;

namespace Rigbay.PredatorSense.Tests;

/// <summary>
/// The real <see cref="PredatorSenseModule"/> on a made-up laptop: its firmware is the fake of the module's tests, whose sensors drift slowly
/// (CPU, GPU and system temperature, three fans) and whose fan mode write only changes the made-up fan speeds. Nothing reaches the machine.
/// </summary>
public sealed class ReleasedPredatorSenseDemo : IModule
{
    private readonly FakeAcerWmi _wmi = new();
    private readonly PredatorSenseModule _inner;
    private readonly DateTime _start = DateTime.UtcNow;
    private volatile bool _max;

    public ReleasedPredatorSenseDemo()
    {
        _wmi.OnRead = _ => Drift();
        _wmi.OnWrite = v => _max = v == WmiAllowList.FanMax;
        _inner = new PredatorSenseModule(new FakeMachine { Model = "Predator (demo laptop)" }, () => _wmi, null, null);
    }

    private void Drift()
    {
        double t = (DateTime.UtcNow - _start).TotalSeconds;
        int cpu = (int)Math.Round(63 + 9 * Math.Sin(t / 11) + 3 * Math.Sin(t / 3.1) - (_max ? 6 : 0));
        int gpu = (int)Math.Round(54 + 5 * Math.Sin(t / 13 + 1));
        int sys = (int)Math.Round(45 + 2 * Math.Sin(t / 17));
        int fan = _max ? 5550 + (int)(40 * Math.Sin(t)) : (int)(2500 + (cpu - 55) * 70 + 60 * Math.Sin(t / 2));
        int g1 = _max ? 5880 : (int)(2450 + (gpu - 50) * 60);
        int g2 = _max ? 5900 : g1 + 90;
        lock (_wmi.Gate)
        {
            _wmi.Sensors[1] = FakeAcerWmi.Raw(cpu);
            _wmi.Sensors[2] = FakeAcerWmi.Raw(fan);
            _wmi.Sensors[3] = FakeAcerWmi.Raw(sys);   // system temperature
            _wmi.Sensors[6] = FakeAcerWmi.Raw(g1);
            _wmi.Sensors[9] = FakeAcerWmi.Raw(g2);
            _wmi.Sensors[10] = FakeAcerWmi.Raw(gpu);  // GPU temperature
        }
    }

    public ProbeResult Probe() => _inner.Probe();
    public void Initialize(IHostServices host) => _inner.Initialize(host);
    public PageModel? GetPageModel(IHostServices host) => _inner.GetPageModel(host);
    public void Shutdown() => _inner.Shutdown();
}

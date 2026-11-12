using Rigbay.Abstractions;

namespace Rigbay.Module.Xtu.Tests;

/// <summary>
/// The real XTU module runtime (the same <see cref="XtuRuntime"/> the released module wires to Intel's service) on a made-up XTU: three saved
/// profiles in a scratch Profiles.xml, a client whose monitor readings drift slowly and whose "apply" changes only its own fields, the system
/// probe and power events of the module's tests. Nothing is sent to XTU and nothing is written to the CPU.
/// </summary>
public sealed class ReleasedXtuDemo : IModule
{
    private XtuRuntime? _runtime;

    public ProbeResult Probe() => ProbeLogic.Run(new FakeSystem());

    public void Initialize(IHostServices host)
    {
        var dir = Path.Combine(host.DataDirectory, "made-up-xtu");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Profiles.xml"), Profiles);
        _runtime = new XtuRuntime(host, new DriftingClient(), new FakeSystem(), new XtuPaths(dir), new FakePower());
        _runtime.Start();
        _runtime.StartTimers();
    }

    public PageModel? GetPageModel(IHostServices host) => _runtime?.BuildPage();

    public void Shutdown()
    {
        _runtime?.Dispose();
        _runtime = null;
    }

    private const string Profiles = """
        <ProfileDatabase><Profiles>
          <DatabaseProfile><Name>Quiet 25W</Name><ControlSettings>
            <ControlSetting><Id>48</Id><Value>25</Value></ControlSetting><ControlSetting><Id>47</Id><Value>35</Value></ControlSetting>
            <ControlSetting><Id>49</Id><Value>1</Value></ControlSetting><ControlSetting><Id>66</Id><Value>28</Value></ControlSetting>
          </ControlSettings></DatabaseProfile>
          <DatabaseProfile><Name>Balanced 45W</Name><ControlSettings>
            <ControlSetting><Id>48</Id><Value>45</Value></ControlSetting><ControlSetting><Id>47</Id><Value>60</Value></ControlSetting>
            <ControlSetting><Id>49</Id><Value>1</Value></ControlSetting><ControlSetting><Id>66</Id><Value>56</Value></ControlSetting>
          </ControlSettings></DatabaseProfile>
          <DatabaseProfile><Name>Performance 65W</Name><ControlSettings>
            <ControlSetting><Id>48</Id><Value>65</Value></ControlSetting><ControlSetting><Id>47</Id><Value>90</Value></ControlSetting>
            <ControlSetting><Id>49</Id><Value>1</Value></ControlSetting><ControlSetting><Id>66</Id><Value>56</Value></ControlSetting>
          </ControlSettings></DatabaseProfile>
        </Profiles></ProfileDatabase>
        """;

    /// <summary>A made-up XTU client: the limits of the profile last "applied" and monitor values that drift with time.</summary>
    private sealed class DriftingClient : IXtuClient
    {
        private readonly DateTime _start = DateTime.UtcNow;
        private bool _opened;
        private XtuState _state = new(45, 60, true, 56, 90, 115, true, 56, true);

        public XtuState ReadState() => _state;

        public IReadOnlyDictionary<uint, decimal> ReadMonitor(out bool justOpened)
        {
            justOpened = !_opened;
            _opened = true;
            double t = (DateTime.UtcNow - _start).TotalSeconds;
            double load = 38 + 22 * Math.Sin(t / 9) + 8 * Math.Sin(t / 2.3);
            double power = Math.Min((double)_state.Pl1 + 4, 14 + load * 0.75 + 3 * Math.Sin(t / 1.7));
            var v = new Dictionary<uint, decimal>
            {
                [Mon.PkgTemp] = (decimal)Math.Round(58 + power * 0.45 + 2 * Math.Sin(t / 4)),
                [Mon.PkgPower] = (decimal)Math.Round(power, 1),
                [Mon.CorePower] = (decimal)Math.Round(power * 0.8, 1),
                [Mon.MaxPFreq] = (decimal)Math.Round(3.6 + 0.6 * Math.Sin(t / 6), 2),
                [Mon.CpuThrottle] = 0, [Mon.PowerThrottle] = 0, [Mon.CurrentThrottle] = 0, [Mon.VrThrottle] = 0,
            };
            for (int i = 0; i < Mon.Cores; i++)
            {
                v[Mon.CoreUtil0 + (uint)i] = (decimal)Math.Clamp(Math.Round(load + 6 * Math.Sin(t / 3 + i)), 1, 99);
                v[Mon.CoreFreq0 + (uint)i] = (decimal)Math.Round(3.4 + 0.5 * Math.Sin(t / 5 + i * 0.7), 2);
                v[Mon.CoreTemp0 + (uint)i] = (decimal)Math.Round(60 + power * 0.4 + 3 * Math.Sin(t / 4 + i));
            }
            return v;
        }

        public void ResetMonitor() => _opened = false;

        public ApplyOutcome Apply(XtuProfile profile)
        {
            Rules.Validate(profile);
            if (profile.Matches(_state)) return new ApplyOutcome(true, _state);
            _state = _state with { Pl1 = profile.Pl1, Pl2 = profile.Pl2, Pl2Enable = profile.Pl2Enable, Window = profile.Window };
            return new ApplyOutcome(false, _state);
        }

        public void Dispose() { }
    }
}

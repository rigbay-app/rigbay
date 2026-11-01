// Design A: read-only XTU status + monitoring read, in-process on .NET 10.
// Same calls as `xtu-switcher --status`: RefreshActiveValuesFromHardwareAccess, GetControl, GetDoRestoreUserValuesOnBoot,
// monitoring Connect/Start/GetValues/Stop. No write call exists in this file.
using System.Diagnostics;
using System.ServiceModel;
using System.ServiceModel.Description;
using System.Text;

namespace XtuModule;

public static class XtuReader
{
    private const string Base = "net.pipe://localhost/ExtremeTuningUtility/";
    private static readonly uint[] ControlIds = { 47, 48, 49, 66 };

    private static NetNamedPipeBinding Binding()
    {
        var b = new NetNamedPipeBinding();
        b.OpenTimeout = TimeSpan.FromSeconds(5);
        b.SendTimeout = TimeSpan.FromSeconds(15);
        b.ReceiveTimeout = TimeSpan.FromSeconds(15);
        b.MaxReceivedMessageSize = 67108864;
        b.MaxBufferSize = 67108864;
        b.ReaderQuotas.MaxArrayLength = 67108864;
        b.ReaderQuotas.MaxStringContentLength = 67108864;
        b.ReaderQuotas.MaxDepth = 64;
        return b;
    }

    private static void Quotas(ServiceEndpoint ep)
    {
        foreach (var op in ep.Contract.Operations)
        {
            var dc = op.Behaviors.Find<DataContractSerializerOperationBehavior>();
            if (dc != null) dc.MaxItemsInObjectGraph = int.MaxValue;
        }
    }

    // What a real module has to do on its own: the host's ModuleLoadContext sends every "System.*" assembly to the default
    // context and ignores the module's copy, and it knows nothing about the Intel folder. The AssemblyLoadContext.Resolving
    // event fires after the context's Load override and the default context both failed, so the module can answer it
    // from its own folder (WCF client packages) and from the Intel folder (XtuInterface, XtuCommon), in place.
    private const string IntelDir = @"C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Drivers";
    private static bool hookInstalled;

    private static void InstallResolver()
    {
        if (hookInstalled) return;
        hookInstalled = true;
        var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(XtuReader).Assembly)!;
        var moduleDir = Path.GetDirectoryName(typeof(XtuReader).Assembly.Location)!;
        alc.Resolving += (ctx, name) =>
        {
            var n = name.Name;
            if (n is null) return null;
            var local = Path.Combine(moduleDir, n + ".dll");
            if (File.Exists(local)) return ctx.LoadFromAssemblyPath(local);
            if (n is "XtuInterface" or "XtuCommon" or "Microsoft.Practices.Unity" or "Microsoft.Win32.TaskScheduler")
            {
                var intel = Path.Combine(IntelDir, n + ".dll");
                if (File.Exists(intel)) return ctx.LoadFromAssemblyPath(intel);
            }
            return null;
        };
    }

    // Entry point called by the runner. Set XTU_SPIKE_NO_HOOK=1 to leave the resolver out (negative test).
    public static string Run(int repeat)
    {
        if (Environment.GetEnvironmentVariable("XTU_SPIKE_NO_HOOK") != "1") InstallResolver();
        return RunCore(repeat);
    }

    // Returns a text report; stops at the first exception and reports it in full. NoInlining keeps the first touch of
    // the WCF and Intel types behind the resolver registration.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string RunCore(int repeat)
    {
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        var step = "start";
        try
        {
            step = "create tuning ChannelFactory<IXtuTuning>";
            var tf = new ChannelFactory<IronCity.Core.IXtuTuning>(Binding(), new EndpointAddress(Base + "Tuning"));
            Quotas(tf.Endpoint);
            sb.AppendLine($"[{sw.ElapsedMilliseconds,5} ms] {step}: ok");

            step = "CreateChannel";
            var tuning = tf.CreateChannel();
            sb.AppendLine($"[{sw.ElapsedMilliseconds,5} ms] {step}: ok");

            step = "IXtuTuning.Connect()";
            if (!tuning.Connect()) throw new InvalidOperationException("XTU refused the connection");
            sb.AppendLine($"[{sw.ElapsedMilliseconds,5} ms] {step}: ok");

            var totals = new List<double>();
            for (int i = 0; i < repeat; i++)
            {
                var t = Stopwatch.StartNew();
                step = "RefreshActiveValuesFromHardwareAccess()";
                tuning.RefreshActiveValuesFromHardwareAccess();
                double refreshMs = t.Elapsed.TotalMilliseconds;

                step = "GetControl(id)";
                var c = new Dictionary<uint, IronCity.Core.XtuTuningControl>();
                foreach (var id in ControlIds) c[id] = tuning.GetControl(id);
                double controlsMs = t.Elapsed.TotalMilliseconds - refreshMs;

                step = "GetDoRestoreUserValuesOnBoot()";
                bool restore = tuning.GetDoRestoreUserValuesOnBoot();
                double total = t.Elapsed.TotalMilliseconds;
                totals.Add(total);
                if (i == 0 || i == repeat - 1)
                {
                    sb.AppendLine($"status #{i + 1}: active: PL1={c[48].ActiveValue:0.###} PL2={c[47].ActiveValue:0.###} pl2enable={c[49].ActiveValue != 0} window={c[66].ActiveValue:0.###}; restore after reboot: {restore}");
                    sb.AppendLine($"          total {total:F1} ms (refresh {refreshMs:F1}, 4x GetControl {controlsMs:F1}, restore flag {total - refreshMs - controlsMs:F1})");
                }
            }
            totals.Sort();
            sb.AppendLine($"status warm x{repeat}: p50 {totals[totals.Count / 2]:F1} ms, p95 {totals[(int)Math.Ceiling(0.95 * totals.Count) - 1]:F1} ms, min {totals[0]:F1} ms, max {totals[^1]:F1} ms");
            try { ((ICommunicationObject)tuning).Abort(); tf.Abort(); } catch { }

            step = "create monitoring ChannelFactory<IXtuMonitoring>";
            var mf = new ChannelFactory<IronCity.Core.IXtuMonitoring>(Binding(), new EndpointAddress(Base + "Monitoring"));
            Quotas(mf.Endpoint);
            var mon = mf.CreateChannel();
            int pid = Environment.ProcessId;
            step = "IXtuMonitoring.Connect(pid)";
            if (!mon.Connect(pid)) throw new InvalidOperationException("XTU refused the monitoring connection");
            step = "IXtuMonitoring.Start(pid)";
            mon.Start(pid);
            try
            {
                step = "IXtuMonitoring.GetValues()";
                var t = Stopwatch.StartNew();
                var v = mon.GetValues();
                sb.AppendLine($"monitor #1: {v.Count} values in {t.Elapsed.TotalMilliseconds:F1} ms; package power(1000)={v.GetValueOrDefault(1000u)} W, package temp(2701131777)={v.GetValueOrDefault(2701131777u)} C");
                var lat = new List<double>();
                for (int i = 0; i < repeat; i++) { t.Restart(); mon.GetValues(); lat.Add(t.Elapsed.TotalMilliseconds); }
                lat.Sort();
                sb.AppendLine($"monitor warm x{repeat}: p50 {lat[lat.Count / 2]:F2} ms, max {lat[^1]:F2} ms");
            }
            finally
            {
                try { mon.Stop(pid); } catch { }
                try { ((ICommunicationObject)mon).Abort(); mf.Abort(); } catch { }
            }
            sb.AppendLine("RESULT A: all read calls worked");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[{sw.ElapsedMilliseconds,5} ms] FAILED at step '{step}'");
            sb.AppendLine(ex.ToString());
        }
        return sb.ToString();
    }
}

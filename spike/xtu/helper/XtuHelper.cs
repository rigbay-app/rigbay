// C1 spike, design B: out-of-process XTU helper.
// C# 5, .NET Framework 4.8, built with the OS csc.exe (see ..\build-helper.ps1). No NuGet, no project file.
// Intel's XtuInterface.dll / XtuCommon.dll are referenced in place and loaded at run time by ResolveIntel; they are never copied.
//
// Protocol: one JSON object per line on stdin (request) and one per line on stdout (reply). stderr is free text for logs.
//   request : {"id":1,"op":"hello|status|monitor|ping|quit"}
//   reply   : {"id":1,"ok":true,"ms":12.3,"data":{...}}   or   {"id":1,"ok":false,"error":"Type: message"}
// "ms" is the time spent inside the XTU calls only, so a client can subtract it from its own round trip.
//
// READ-ONLY. The only XTU calls in this file are: IXtuTuning.Connect, RefreshActiveValuesFromHardwareAccess, GetControl,
// GetDoRestoreUserValuesOnBoot; IXtuMonitoring.Connect/Start/GetValues/Stop (client registration plus read).
// No ProposeChange, ApplyChanges, DiscardChanges or any other write exists in this program.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceProcess;
using System.Text;
using System.Web.Script.Serialization;

namespace XtuHelper
{
    internal static class Program
    {
        internal const string XtuDir = @"C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Drivers";

        private static int Main(string[] args)
        {
            // Must be registered before any Intel type is touched; Run is NoInlining for that reason.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveIntel;
            return Run(args);
        }

        private static Assembly ResolveIntel(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            string path = Path.Combine(XtuDir, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            JavaScriptSerializer json = new JavaScriptSerializer();
            Xtu xtu = new Xtu();
            Console.Error.WriteLine("xtu-helper pid " + Process.GetCurrentProcess().Id + " clr " + Environment.Version);
            try
            {
                string line;
                while ((line = Console.In.ReadLine()) != null)
                {
                    line = line.TrimStart((char)0xFEFF); if (line.Trim().Length == 0) continue;
                    Dictionary<string, object> reply = new Dictionary<string, object>();
                    bool quit = false;
                    try
                    {
                        Dictionary<string, object> req = json.Deserialize<Dictionary<string, object>>(line);
                        object id;
                        if (req.TryGetValue("id", out id)) reply["id"] = id;
                        string op = req.ContainsKey("op") ? Convert.ToString(req["op"]) : "";
                        Stopwatch sw = Stopwatch.StartNew();
                        object data;
                        switch (op)
                        {
                            case "hello": data = Hello(); break;
                            case "ping": data = "pong"; break;
                            case "status": data = xtu.Status(); break;
                            case "monitor": data = xtu.Monitor(); break;
                            case "quit": data = "bye"; quit = true; break;
                            default: throw new InvalidOperationException("unknown op: " + op);
                        }
                        reply["ok"] = true;
                        reply["ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 3);
                        reply["data"] = data;
                    }
                    catch (Exception ex)
                    {
                        reply["ok"] = false;
                        reply["error"] = Describe(ex);
                    }
                    Console.Out.WriteLine(json.Serialize(reply));
                    Console.Out.Flush();
                    if (quit) break;
                }
            }
            finally { xtu.Dispose(); }
            return 0;
        }

        private static string Describe(Exception ex)
        {
            StringBuilder sb = new StringBuilder();
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(e.GetType().FullName).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        private static Dictionary<string, object> Hello()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["helper"] = "xtu-helper spike";
            d["pid"] = Process.GetCurrentProcess().Id;
            d["clr"] = Environment.Version.ToString();
            d["workingSetMB"] = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1048576.0, 1);
            d["elevated"] = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            return d;
        }
    }

    // All Intel types live here, so they are only resolved after ResolveIntel is registered.
    internal sealed class Xtu : IDisposable
    {
        private const string Base = "net.pipe://localhost/ExtremeTuningUtility/";
        private static readonly uint[] ControlIds = new uint[] { 47, 48, 49, 66 };   // PL2, PL1, PL2 enable, time window

        private ChannelFactory<IronCity.Core.IXtuTuning> tuningFactory;
        private IronCity.Core.IXtuTuning tuning;
        private ChannelFactory<IronCity.Core.IXtuMonitoring> monFactory;
        private IronCity.Core.IXtuMonitoring mon;
        private readonly int pid = Process.GetCurrentProcess().Id;

        private static NetNamedPipeBinding Binding()
        {
            NetNamedPipeBinding b = new NetNamedPipeBinding();
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
            foreach (OperationDescription op in ep.Contract.Operations)
            {
                DataContractSerializerOperationBehavior dc = op.Behaviors.Find<DataContractSerializerOperationBehavior>();
                if (dc != null) dc.MaxItemsInObjectGraph = int.MaxValue;
            }
        }

        private void EnsureTuning()
        {
            if (tuning != null) return;
            tuningFactory = new ChannelFactory<IronCity.Core.IXtuTuning>(Binding(), new EndpointAddress(Base + "Tuning"));
            Quotas(tuningFactory.Endpoint);
            tuning = tuningFactory.CreateChannel();
            try { if (!tuning.Connect()) throw new InvalidOperationException("XTU refused the Tuning connection"); }
            catch { CloseTuning(); throw; }
        }

        private void CloseTuning()
        {
            try { if (tuning != null) ((ICommunicationObject)tuning).Abort(); } catch { }
            try { if (tuningFactory != null) tuningFactory.Abort(); } catch { }
            tuning = null; tuningFactory = null;
        }

        private void EnsureMonitor()
        {
            if (mon != null) return;
            monFactory = new ChannelFactory<IronCity.Core.IXtuMonitoring>(Binding(), new EndpointAddress(Base + "Monitoring"));
            Quotas(monFactory.Endpoint);
            mon = monFactory.CreateChannel();
            try
            {
                if (!mon.Connect(pid)) throw new InvalidOperationException("XTU refused the Monitoring connection");
                mon.Start(pid);
            }
            catch { CloseMonitor(false); throw; }
        }

        private void CloseMonitor(bool stop)
        {
            try { if (stop && mon != null) mon.Stop(pid); } catch { }
            try { if (mon != null) ((ICommunicationObject)mon).Abort(); } catch { }
            try { if (monFactory != null) monFactory.Abort(); } catch { }
            mon = null; monFactory = null;
        }

        // Same read as `xtu-switcher --status`: refresh from hardware first, then GetControl for each of the four controls.
        public Dictionary<string, object> Status()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            try { d["service"] = new ServiceController("XTU3SERVICE").Status.ToString(); } catch (Exception ex) { d["service"] = "unknown: " + ex.Message; }
            try
            {
                EnsureTuning();
                tuning.RefreshActiveValuesFromHardwareAccess();
                Dictionary<string, object> controls = new Dictionary<string, object>();
                foreach (uint id in ControlIds)
                {
                    IronCity.Core.XtuTuningControl c = tuning.GetControl(id);
                    Dictionary<string, object> o = new Dictionary<string, object>();
                    o["active"] = c.ActiveValue;
                    o["default"] = c.DefaultValue;
                    o["readOnly"] = c.ReadOnly;
                    o["enabled"] = c.Enabled;
                    controls[id.ToString()] = o;
                }
                d["controls"] = controls;
                d["restoreOnBoot"] = tuning.GetDoRestoreUserValuesOnBoot();
            }
            catch { CloseTuning(); throw; }
            return d;
        }

        // Monitoring read: GetValues() returns every monitor id with its current value.
        public Dictionary<string, object> Monitor()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            try
            {
                EnsureMonitor();
                Dictionary<uint, decimal> values = mon.GetValues();
                Dictionary<string, object> v = new Dictionary<string, object>();
                foreach (KeyValuePair<uint, decimal> kv in values) v[kv.Key.ToString()] = kv.Value;
                d["count"] = values.Count;
                d["values"] = v;
            }
            catch { CloseMonitor(false); throw; }
            return d;
        }

        public void Dispose() { CloseMonitor(true); CloseTuning(); }
    }
}

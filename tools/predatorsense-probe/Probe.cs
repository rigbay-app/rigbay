// predatorsense-probe: READ-ONLY inspection of how Acer PredatorSense is set up on this machine.
//
// What it does (default run):
//   - identity: manufacturer, model, board, BIOS version
//   - is the PredatorSense service present / running, and are its named pipes present
//   - does the Acer gaming WMI class exist in root\wmi (schema only, no instances, no method calls)
//   - the settings PredatorSense itself caches in HKLM\SOFTWARE\OEM\PredatorSense and in
//     C:\ProgramData\oem\PredatorSense\*.ini (these are the app's own records, not live firmware state)
//   - generic Windows battery and thermal-zone readings
//
// What it never does: call any WMI method, open the service pipe, write the registry, touch services
// or drivers. The one optional firmware read (--acer-sysinfo) is off by default and needs the owner's
// approval; see README.md.
//
// Build: .\build.ps1   (uses the .NET Framework csc.exe that ships with Windows; no SDK needed)
// C# 5 on purpose, so it compiles with that csc.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

namespace PredatorSenseProbe
{
    // Tiny ordered JSON-ish tree so we do not need any extra library.
    class Node
    {
        public List<KeyValuePair<string, object>> Items = new List<KeyValuePair<string, object>>();
        public void Set(string key, object value) { Items.Add(new KeyValuePair<string, object>(key, value)); }
    }

    static class Json
    {
        public static string Write(object o) { StringBuilder sb = new StringBuilder(); Emit(sb, o, 0); return sb.ToString(); }

        static void Emit(StringBuilder sb, object o, int depth)
        {
            if (o == null) { sb.Append("null"); return; }
            Node n = o as Node;
            if (n != null)
            {
                if (n.Items.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                for (int i = 0; i < n.Items.Count; i++)
                {
                    sb.Append(' ', (depth + 1) * 2);
                    sb.Append(Quote(n.Items[i].Key)).Append(": ");
                    Emit(sb, n.Items[i].Value, depth + 1);
                    if (i < n.Items.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(' ', depth * 2).Append('}');
                return;
            }
            System.Collections.IList list = o as System.Collections.IList;
            if (list != null)
            {
                if (list.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    Emit(sb, list[i], depth + 1);
                }
                sb.Append(']');
                return;
            }
            if (o is bool) { sb.Append(((bool)o) ? "true" : "false"); return; }
            if (o is int || o is long || o is uint || o is ulong || o is short || o is byte)
            { sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); return; }
            if (o is double) { sb.Append(((double)o).ToString("R", CultureInfo.InvariantCulture)); return; }
            sb.Append(Quote(o.ToString()));
        }

        static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }

    static class Program
    {
        const string RegPath = @"SOFTWARE\OEM\PredatorSense";
        const string DataDir = @"C:\ProgramData\oem\PredatorSense";
        const string ServiceName = "PSSvc";
        const string ServicePipe = "predatorsense_service_namedpipe";
        const string AdminAgentPipePrefix = "predatorsense_admin_agent_";
        const string WmiNamespace = @"root\wmi";
        const string WmiClassGaming = "AcerGamingFunction";
        const string WmiClassGeneric = "AcerGenericMethod";

        // Names of the sensor indexes of GetGamingSysInfo, as listed by the PredatorSense UI.
        static readonly string[] SysInfoNames = new string[]
        {
            "", "cpu_temperature", "cpu_fan_speed", "system_temperature", "system_fan_speed",
            "system_fan_speed_external", "gpu1_fan_speed", "system2_temperature", "system2_fan_speed",
            "gpu2_fan_speed", "gpu1_temperature", "gpu2_temperature", "cpu_fan_rate", "system_fan_rate",
            "system2_fan_rate", "system_fan_rate_external", "gpu1_fan_rate", "gpu2_fan_rate"
        };

        static int Main(string[] args)
        {
            bool json = false, allowCall = false, help = false, bad = false;
            int sysinfoIndex = -1;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--json") json = true;
                else if (a == "--allow-call") allowCall = true;
                else if (a == "--sysinfo-index" && i + 1 < args.Length)
                {
                    int v;
                    if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= 1 && v <= 17) sysinfoIndex = v;
                    else { Console.Error.WriteLine("--sysinfo-index must be 1..17"); bad = true; }
                }
                else if (a == "-h" || a == "--help" || a == "/?") help = true;
                else { Console.Error.WriteLine("Unknown argument: " + a); bad = true; }
            }
            if (help || bad)
            {
                Console.WriteLine("predatorsense-probe [--json] [--allow-call --sysinfo-index N]");
                Console.WriteLine("  Read-only. Prints whether this machine looks supported and what PredatorSense has recorded.");
                Console.WriteLine("  --json                 machine-readable output");
                Console.WriteLine("  --allow-call           permit the ONE firmware call this tool knows (GetGamingSysInfo). Without it");
                Console.WriteLine("                         the tool calls no WMI method at all. Needs the owner's approval first and admin.");
                Console.WriteLine("  --sysinfo-index N      which sensor (1..17) to read with that call; one index per run.");
                Console.WriteLine("                         1 = CPU temperature (the first approved input, 0x0101).");
                return bad ? 2 : 0;
            }
            if (allowCall != (sysinfoIndex > 0))
            {
                Console.Error.WriteLine("--allow-call and --sysinfo-index N must be given together.");
                return 2;
            }

            Node root = new Node();
            root.Set("probe", "predatorsense-probe 0.1 (read-only)");
            root.Set("elevated", IsElevated());
            root.Set("identity", Identity());
            Node support = new Node();
            root.Set("support", support);
            root.Set("service", Service(support));
            root.Set("pipes", Pipes(support));
            root.Set("wmi_schema", WmiSchema(support));
            root.Set("predatorsense_cache", Cache());
            root.Set("hardware_support_ini", Ini(Path.Combine(DataDir, "HW_Support.ini"), new string[] { "FanSupport", "LightingType", "FanDtail" }));
            root.Set("feature_ini", Ini(Path.Combine(DataDir, "Feature.ini"), new string[] { "MainFunction", "OperationModeSupport", "OverclockSupport", "OverclockDetial", "MachineType", "PowerLimitSupport" }));
            root.Set("windows_battery", Battery());
            root.Set("windows_thermal_zones", ThermalZones());
            if (allowCall) root.Set("acer_sysinfo", AcerSysInfo(sysinfoIndex));
            else root.Set("acer_sysinfo", "not requested (no WMI method was called; needs owner approval, then --allow-call --sysinfo-index N)");

            // verdict
            bool ok = (bool)support.Items.Find(delegate(KeyValuePair<string, object> kv) { return kv.Key == "service_present"; }).Value
                   && (bool)support.Items.Find(delegate(KeyValuePair<string, object> kv) { return kv.Key == "gaming_wmi_class_present"; }).Value;
            support.Set("supported", ok);

            if (json) Console.WriteLine(Json.Write(root));
            else PrintText(root, 0);
            return ok ? 0 : 1;
        }

        // ---- pieces -------------------------------------------------------------------------

        static bool IsElevated()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static Node Identity()
        {
            Node n = new Node();
            Query(n, "root\\cimv2", "SELECT Manufacturer, Model, SystemSKUNumber FROM Win32_ComputerSystem", new string[] { "Manufacturer", "Model", "SystemSKUNumber" }, "system_");
            Query(n, "root\\cimv2", "SELECT Manufacturer, Product FROM Win32_BaseBoard", new string[] { "Manufacturer", "Product" }, "board_");
            Query(n, "root\\cimv2", "SELECT Manufacturer, SMBIOSBIOSVersion FROM Win32_BIOS", new string[] { "Manufacturer", "SMBIOSBIOSVersion" }, "bios_");
            return n;
        }

        static void Query(Node into, string ns, string wql, string[] props, string prefix)
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(ns, wql))
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        foreach (string p in props) into.Set(prefix + p.ToLowerInvariant(), o[p] == null ? "" : o[p].ToString().Trim());
                        break;
                    }
            }
            catch (Exception e) { into.Set(prefix + "error", e.Message); }
        }

        static Node Service(Node support)
        {
            Node n = new Node();
            bool present = false;
            try
            {
                foreach (ServiceController sc in ServiceController.GetServices())
                {
                    if (string.Equals(sc.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase))
                    {
                        present = true;
                        n.Set("name", sc.ServiceName);
                        n.Set("display_name", sc.DisplayName);
                        n.Set("state", sc.Status.ToString());
                        break;
                    }
                }
            }
            catch (Exception e) { n.Set("error", e.Message); }
            if (present)
            {
                // Path and account come from the service config; reading Win32_Service is read-only.
                Query(n, "root\\cimv2", "SELECT PathName, StartName, StartMode FROM Win32_Service WHERE Name='" + ServiceName + "'", new string[] { "PathName", "StartName", "StartMode" }, "");
            }
            support.Set("service_present", present);
            return n;
        }

        static Node Pipes(Node support)
        {
            Node n = new Node();
            bool svc = false;
            List<string> agents = new List<string>();
            try
            {
                foreach (string p in Directory.GetFiles(@"\\.\pipe\"))
                {
                    string name = p.Substring(p.LastIndexOf('\\') + 1);
                    if (string.Equals(name, ServicePipe, StringComparison.OrdinalIgnoreCase)) svc = true;
                    if (name.StartsWith(AdminAgentPipePrefix, StringComparison.OrdinalIgnoreCase)) agents.Add(name);
                }
            }
            catch (Exception e) { n.Set("error", e.Message); }
            n.Set("service_pipe_" + ServicePipe, svc);
            n.Set("admin_agent_pipes", agents);
            n.Set("note", "Pipes are only listed, never opened. The service accepts only the genuine PredatorSense app as a client.");
            support.Set("service_pipe_present", svc);
            return n;
        }

        static Node WmiSchema(Node support)
        {
            Node n = new Node();
            bool gaming = false;
            foreach (string cls in new string[] { WmiClassGaming, WmiClassGeneric })
            {
                Node c = new Node();
                try
                {
                    using (ManagementClass mc = new ManagementClass(WmiNamespace, cls, null))
                    {
                        List<string> methods = new List<string>();
                        foreach (MethodData m in mc.Methods)
                        {
                            object id = m.Qualifiers["WmiMethodId"].Value;
                            methods.Add(m.Name + "=" + id);
                        }
                        c.Set("present", true);
                        object guid = null;
                        try { guid = mc.Qualifiers["guid"].Value; } catch { }
                        c.Set("guid", guid == null ? "" : guid.ToString());
                        c.Set("methods", methods);
                        if (cls == WmiClassGaming) gaming = true;
                    }
                }
                catch (ManagementException e) { c.Set("present", false); c.Set("error", e.ErrorCode.ToString()); }
                catch (Exception e) { c.Set("present", false); c.Set("error", e.Message); }
                n.Set(WmiNamespace + ":" + cls, c);
            }
            support.Set("gaming_wmi_class_present", gaming);
            n.Set("note", "Schema only. No instance is read and no method is called.");
            return n;
        }

        static Node Cache()
        {
            Node n = new Node();
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(RegPath))
                {
                    if (k == null) { n.Set("present", false); return n; }
                    n.Set("present", true);
                    CopyValues(n, k, new string[] { "Model_Name_1st", "Model_Name_2nd", "Operation_Mode_Support", "RGB_Keyboard_Support", "Intel_CPU_Support", "Nvdia_GPU_Support", "HeatPipe_opening", "EnabledSilentMode", "NV_WhisperMode20_Support", "NV_WhisperMode20_Status", "PD_Support", "Two_AC_Support", "Keyboard_Type", "Customize_POST_Support" }, "");
                    using (RegistryKey f = k.OpenSubKey("FanControl"))
                        if (f != null) CopyValues(n, f, new string[] { "CurrentFanMode", "CPU1FanPercentage", "CPU1FanCustomAuto", "GPU1FanPercentage", "GPU1FanCustomAuto", "GPU2FanPercentage", "GPU2FanCustomAuto" }, "fan.");
                    using (RegistryKey o = k.OpenSubKey("Overclock"))
                        if (o != null) CopyValues(n, o, new string[] { "CurrentCPUMode", "CurrentGPUMode", "CurrentOperationMode", "MaxQ_Mode", "CPU_OC_Support", "GPU_OC_Support", "SP_CPU_OC_Support", "CPU_PL_Support" }, "oc.");
                    using (RegistryKey a = k.OpenSubKey("AdvanceSettings"))
                        if (a != null) CopyValues(n, a, new string[] { "LCDOverDrive", "AutoOverclocking", "discrete_gpu_support", "post_animation_support", "LCD_Overdrive_support" }, "advanced.");
                    using (RegistryKey l = k.OpenSubKey("LightSetting"))
                        if (l != null) CopyValues(n, l, new string[] { "KeyBoardArea", "KeyBoardColor", "LightingProfile" }, "light.");
                }
            }
            catch (Exception e) { n.Set("error", e.Message); }
            n.Set("note", "These are the values the PredatorSense app and service last recorded. They can be stale; they are not a firmware read.");
            return n;
        }

        static void CopyValues(Node into, RegistryKey k, string[] names, string prefix)
        {
            foreach (string name in names)
            {
                object v = k.GetValue(name);
                if (v == null) continue;
                if (v is byte[]) v = "bytes[" + ((byte[])v).Length + "]";
                into.Set(prefix + name, v.ToString());
            }
        }

        static Node Ini(string path, string[] sections)
        {
            Node n = new Node();
            if (!File.Exists(path)) { n.Set("present", false); return n; }
            n.Set("present", true);
            string cur = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { cur = line.Substring(1, line.Length - 2); continue; }
                if (cur == null || Array.IndexOf(sections, cur) < 0) continue;
                int i = line.IndexOfAny(new char[] { '=', ':' });
                if (i <= 0) continue;
                n.Set(cur + "." + line.Substring(0, i).Trim(), line.Substring(i + 1).Trim());
            }
            return n;
        }

        static Node Battery()
        {
            Node n = new Node();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("root\\cimv2", "SELECT BatteryStatus, EstimatedChargeRemaining, Name FROM Win32_Battery"))
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        n.Set("name", o["Name"] == null ? "" : o["Name"].ToString());
                        n.Set("status_code", o["BatteryStatus"] == null ? "" : o["BatteryStatus"].ToString());
                        n.Set("percent", o["EstimatedChargeRemaining"] == null ? "" : o["EstimatedChargeRemaining"].ToString());
                        break;
                    }
            }
            catch (Exception e) { n.Set("error", e.Message); }
            n.Set("note", "Plain Windows view of the battery. PredatorSense v3 has no charge limit feature on this machine, so there is nothing PredatorSense-specific to read here.");
            return n;
        }

        static Node ThermalZones()
        {
            Node n = new Node();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(WmiNamespace, "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                {
                    int i = 0;
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        double k10 = Convert.ToDouble(o["CurrentTemperature"], CultureInfo.InvariantCulture);
                        n.Set("zone" + i + "_" + o["InstanceName"], Math.Round(k10 / 10.0 - 273.15, 1));
                        i++;
                    }
                    if (i == 0) n.Set("zones", "none reported");
                }
            }
            catch (Exception e) { n.Set("error", e.Message + " (the standard ACPI thermal zone class usually needs admin)"); }
            return n;
        }

        // ---- optional firmware read -------------------------------------------------------------
        // Calls ONLY AcerGamingFunction.GetGamingSysInfo, once, with input (index << 8) | 1 for the one
        // index given on the command line (1..17). The PredatorSense dashboard makes the same call.
        // It is still a firmware call that this project cannot prove side-effect free, so it is only
        // reachable through --allow-call, after the owner approved the specific input
        // (see docs/predatorsense/GATE-getgamingsysinfo.md).
        static Node AcerSysInfo(int onlyIndex)
        {
            Node n = new Node();
            if (onlyIndex < 1 || onlyIndex > 17) { n.Set("error", "index out of range"); return n; }
            if (!IsElevated()) { n.Set("error", "needs an elevated (administrator) process"); return n; }
            try
            {
                using (ManagementClass mc = new ManagementClass(WmiNamespace, WmiClassGaming, null))
                {
                    ManagementObject inst = null;
                    foreach (ManagementObject o in mc.GetInstances()) { inst = o; break; }
                    if (inst == null) { n.Set("error", "no instance of " + WmiClassGaming); return n; }
                    for (int index = onlyIndex; index <= onlyIndex; index++)
                    {
                        uint input = (uint)((index << 8) | 1);
                        Node r = new Node();
                        try
                        {
                            ManagementBaseObject inParams = inst.GetMethodParameters("GetGamingSysInfo");
                            inParams["gmInput"] = input;
                            ManagementBaseObject outParams = inst.InvokeMethod("GetGamingSysInfo", inParams, null);
                            ulong raw = Convert.ToUInt64(outParams["gmOutput"], CultureInfo.InvariantCulture);
                            r.Set("input", "0x" + input.ToString("X"));
                            r.Set("raw", "0x" + raw.ToString("X"));
                            uint status = (uint)(raw & 0xFF);
                            r.Set("status", status);
                            if (status == 0) r.Set("value", (int)((raw >> 8) & 0xFFFF));
                        }
                        catch (Exception e) { r.Set("error", e.Message); }
                        n.Set(SysInfoNames[index], r);
                    }
                }
            }
            catch (Exception e) { n.Set("error", e.Message); }
            return n;
        }

        // ---- text output -----------------------------------------------------------------------

        static void PrintText(object o, int depth)
        {
            Node n = o as Node;
            string pad = new string(' ', depth * 2);
            if (n == null) { Console.WriteLine(pad + o); return; }
            foreach (KeyValuePair<string, object> kv in n.Items)
            {
                Node child = kv.Value as Node;
                if (child != null) { Console.WriteLine(pad + kv.Key + ":"); PrintText(child, depth + 1); }
                else if (kv.Value is System.Collections.IList && !(kv.Value is string))
                {
                    List<string> parts = new List<string>();
                    foreach (object x in (System.Collections.IList)kv.Value) parts.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
                    Console.WriteLine(pad + kv.Key + ": " + (parts.Count == 0 ? "(none)" : string.Join(", ", parts.ToArray())));
                }
                else Console.WriteLine(pad + kv.Key + ": " + Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
            }
        }
    }
}

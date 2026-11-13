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
// What it never does by default: call any WMI method, open the service pipe, write the registry, touch
// services or drivers. A short list of firmware READS exists behind explicit flags (see Refusal() below);
// each needs --allow-call, an elevated process and the owner's approval of that exact call. Any call that is
// not on that list, and every Set* method, is refused before WMI is touched. See README.md.
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
        const string ClassGaming = WmiClassGaming;
        const string ClassGeneric = WmiClassGeneric;
        // B11 (static analysis of PSSvc.exe and PSAdminAgent.exe): Acer's own code calls GetFunction/SetFunction on the class
        // APGeAction, never on AcerGenericMethod. Whether the two names are one class on this machine is not known.
        const string ClassApge = "APGeAction";

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
            bool json = false, allowCall = false, help = false, bad = false, selfTest = false;
            List<ReadCall> calls = new List<ReadCall>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string err = null;
                if (a == "--json") json = true;
                else if (a == "--allow-call") allowCall = true;
                else if (a == "--sysinfo-index" && i + 1 < args.Length)
                {
                    int v;
                    if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= 1 && v <= 17) calls.Add(SysInfoIndexCall(v));
                    else err = "--sysinfo-index must be 1..17";
                }
                else if (a == "--r1") calls.Add(SysInfoIndexCall(2));                  // CPU fan speed, GetGamingSysInfo 0x0201
                else if (a == "--r2") calls.Add(new ReadCall("r2_cool_booster_state", ClassGeneric, "GetFunction", "uiInput", "uiOutput", 0x207, "coolbooster"));
                else if (a == "--r3") err = "--r3 is void since B11: it sent the wrong input 0x80184 to AcerGenericMethod. Use --kb-backlight (APGeAction.GetFunction 0x88401; FINDINGS.md 16.1)";
                else if (a == "--kb-backlight") calls.Add(new ReadCall("kb_backlight", ClassApge, "GetFunction", "uiInput", "uiOutput", 0x88401, "backlight"));   // 1 | (132 << 8) | 0x80000; hotkey 132 is machine specific
                else if (a == "--battery-boost") calls.Add(new ReadCall("battery_boost", ClassGaming, "GetGamingSysInfo", "gmInput", "gmOutput", 0x2, "batteryboost"));
                else if (a == "--misc-get" && i + 1 < args.Length)
                {
                    string v = args[++i];
                    if (v == "2") calls.Add(new ReadCall("misc_get_2_gpu_mode", ClassGaming, "GetGamingMiscSetting", "gmInput", "gmOutput", 2, "gpumode"));
                    else if (v == "9") calls.Add(new ReadCall("misc_get_9_gpu_mode_capability", ClassGaming, "GetGamingMiscSetting", "gmInput", "gmOutput", 9, "gpucap"));
                    else if (v == "10" || v == "0x0A" || v == "0x0a") calls.Add(new ReadCall("misc_get_0A_opmode_capability", ClassGaming, "GetGamingMiscSetting", "gmInput", "gmOutput", 0x0A, "opmodecap"));
                    else if (v == "11" || v == "0x0B" || v == "0x0b") calls.Add(new ReadCall("misc_get_0B_opmode_current", ClassGaming, "GetGamingMiscSetting", "gmInput", "gmOutput", 0x0B, "opmode"));
                    else err = "--misc-get must be 2, 9, 10 (0x0A) or 11 (0x0B)";
                }
                else if (a == "--self-test-refusals") selfTest = true;
                else if (a == "--led-behavior-get") err = "--led-behavior-get is void since B11: it called GetGamingLEDBehavior (id 11), which no Acer code calls. Use --led-get (GetGamingLED, id 4, input 1; FINDINGS.md 16.1)";
                else if (a == "--led-get") calls.Add(new ReadCall("led_get_1", ClassGaming, "GetGamingLED", "gmInput", "gmOutput", 1, "ledbehavior"));
                else if (a == "-h" || a == "--help" || a == "/?") help = true;
                else err = "Unknown argument: " + a;
                if (err != null) { Console.Error.WriteLine(err); bad = true; }
            }
            if (selfTest) return SelfTestRefusals();
            if (help || bad)
            {
                Console.WriteLine("predatorsense-probe [--json] [--allow-call <read flags>]");
                Console.WriteLine("  Read-only. Prints whether this machine looks supported and what PredatorSense has recorded.");
                Console.WriteLine("  --json                 machine-readable output");
                Console.WriteLine("  --allow-call           required before any firmware read flag below is honoured. Without it the tool");
                Console.WriteLine("                         calls no WMI method at all. Needs the owner's approval and an elevated process.");
                Console.WriteLine("  Firmware read flags (each is exactly one call; they run in the order given, once each; the run");
                Console.WriteLine("  stops at the first call that errors or returns a non-zero status):");
                Console.WriteLine("    --sysinfo-index N    GetGamingSysInfo, input 1|(N<<8), N 1..17 (1 = CPU temperature)");
                Console.WriteLine("    --r1                 GetGamingSysInfo 0x0201 (CPU fan speed) = --sysinfo-index 2");
                Console.WriteLine("    --r2                 AcerGenericMethod.GetFunction 0x207 (Cool Booster state)");
                Console.WriteLine("    --kb-backlight       APGeAction.GetFunction 0x88401 (keyboard backlight; replaces the void --r3)");
                Console.WriteLine("    --battery-boost      GetGamingSysInfo 0x2 (battery boost flag)");
                Console.WriteLine("    --misc-get 2|9|10|11 GetGamingMiscSetting 2 (GPU mode), 9 (GPU mode capability), 10 = 0x0A (operation-mode capability),");
                Console.WriteLine("                         11 = 0x0B (current operation mode)");
                Console.WriteLine("    --led-get            GetGamingLED 1 (Acer's boost indicator read; replaces the void --led-behavior-get)");
                Console.WriteLine("  --self-test-refusals   no WMI, no flags needed: checks that every other GetGamingMiscSetting input and every Set* is refused");
                Console.WriteLine("  Anything else, and every Set* method, is refused in code.");
                return bad ? 2 : 0;
            }
            if (allowCall != (calls.Count > 0))
            {
                Console.Error.WriteLine("--allow-call and at least one firmware read flag must be given together.");
                return 2;
            }
            // Refuse the whole run, before touching WMI, if any call is off the allowlist or repeated.
            List<string> seen = new List<string>();
            foreach (ReadCall c in calls)
            {
                string why = Refusal(c);
                if (why == null && seen.Contains(c.Key)) why = "call requested twice in one run (each is allowed once)";
                if (why != null) { Console.Error.WriteLine("REFUSED " + c.Describe() + ": " + why); return 2; }
                seen.Add(c.Key);
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
            if (allowCall) root.Set("firmware_reads", RunReads(calls));
            else root.Set("firmware_reads", "not requested (no WMI method was called; needs owner approval, then --allow-call plus read flags, see --help)");

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

        // ---- optional firmware reads -------------------------------------------------------------
        // Every call below is a READ traced to the PredatorSense UI's own code (docs/predatorsense/FINDINGS.md,
        // GATE-getgamingsysinfo.md, GATE-b2-reads.md). They are still firmware calls this project cannot prove
        // side-effect free, so each is reachable only through its own flag plus --allow-call, in an elevated
        // process, after the owner approved that exact call. Refusal() is the allowlist: exact class, method and
        // input. Nothing else can run, and no method whose name starts with "Set" can run at all.

        class ReadCall
        {
            public string Key, Class, Method, InName, OutName, Decoder;
            public ulong Input;
            public ReadCall(string key, string cls, string method, string inName, string outName, ulong input, string decoder)
            { Key = key; Class = cls; Method = method; InName = inName; OutName = outName; Input = input; Decoder = decoder; }
            public string Describe() { return Class + "." + Method + "(0x" + Input.ToString("X") + ")"; }
        }

        static ReadCall SysInfoIndexCall(int index)
        {
            return new ReadCall(SysInfoNames[index], ClassGaming, "GetGamingSysInfo", "gmInput", "gmOutput", (ulong)((index << 8) | 1), "sensor");
        }

        // Returns null when the call is on the allowlist, else the reason it is refused.
        static string Refusal(ReadCall c)
        {
            if (c.Method.StartsWith("Set", StringComparison.OrdinalIgnoreCase)) return "Set* methods are never allowed";
            if (c.Class == ClassGaming && c.Method == "GetGamingSysInfo")
            {
                if (c.Input == 2) return null;                                  // battery boost flag
                if ((c.Input & 0xFF) == 1 && (c.Input >> 8) >= 1 && (c.Input >> 8) <= 17) return null;   // sensor index 1..17
                return "GetGamingSysInfo input is not 2 and not 1|(1..17 << 8)";
            }
            if (c.Class == ClassGeneric && c.Method == "GetFunction")
            {
                if (c.Input == 0x207) return null;                              // Cool Booster state (B3 ran it once: status 0xE1)
                return "GetFunction on AcerGenericMethod: input is not 0x207 (0x80184 was wrong, void since B11)";
            }
            if (c.Class == ClassApge && c.Method == "GetFunction")
            {
                if (c.Input == 0x88401) return null;                            // keyboard backlight: 1 | (132 << 8) | 0x80000 (B11, traced in the UI and PSAdminAgent.exe)
                return "GetFunction on APGeAction: input is not 0x88401";
            }
            if (c.Class == ClassGaming && c.Method == "GetGamingLED")
            {
                if (c.Input == 1) return null;                                  // Acer's pipe 14 (the UI calls it LEDBehavior): the boost indicator read
                return "GetGamingLED input is not 1";
            }
            if (c.Class == ClassGaming && c.Method == "GetGamingMiscSetting")
            {
                if (c.Input == 2 || c.Input == 9) return null;                  // GPU mode, GPU mode capability
                if (c.Input == 0x0A || c.Input == 0x0B) return null;            // operation-mode capability, current operation mode (B10, owner approved 2026-11-12)
                return "GetGamingMiscSetting input is not 2, 9, 0x0A or 0x0B";
            }
            return "call is not on the approved read list";
        }

        // Offline check of the allowlist: builds ReadCall objects and asks Refusal(); touches no WMI.
        static int SelfTestRefusals()
        {
            int fails = 0;
            for (ulong i = 0; i <= 0x1FF; i++)
            {
                bool allowed = Refusal(new ReadCall("t", ClassGaming, "GetGamingMiscSetting", "gmInput", "gmOutput", i, "")) == null;
                bool expected = (i == 2 || i == 9 || i == 0x0A || i == 0x0B);
                if (allowed != expected) { Console.WriteLine("FAIL GetGamingMiscSetting input 0x" + i.ToString("X") + " allowed=" + allowed); fails++; }
            }
            ulong[] big = new ulong[] { 0x10B, 0x20B, 0x40B, 0x50B, 0x105, 0x10000000BUL, 0xFFFFFFFFUL, 0xFFFFFFFFFFFFFFFFUL };
            foreach (ulong v in big)
                if (Refusal(new ReadCall("t", ClassGaming, "GetGamingMiscSetting", "gmInput", "gmOutput", v, "")) == null) { Console.WriteLine("FAIL large input 0x" + v.ToString("X") + " allowed"); fails++; }
            foreach (string m in new string[] { "SetGamingMiscSetting", "SetGamingFanBehavior", "setGamingMiscSetting", "SetGamingLEDBehavior" })
                if (Refusal(new ReadCall("t", ClassGaming, m, "gmInput", "gmOutput", 0x0B, "")) == null) { Console.WriteLine("FAIL " + m + " allowed"); fails++; }
            if (Refusal(new ReadCall("t", ClassGeneric, "GetGamingMiscSetting", "gmInput", "gmOutput", 0x0B, "")) == null) { Console.WriteLine("FAIL wrong class allowed"); fails++; }
            // B11: lighting reads. Only the traced inputs on the traced classes are allowed.
            for (ulong i = 0; i <= 0x1FF; i++)
                if ((Refusal(new ReadCall("t", ClassGeneric, "GetFunction", "uiInput", "uiOutput", i, "")) == null) != (i == 0x207)) { Console.WriteLine("FAIL AcerGenericMethod.GetFunction input 0x" + i.ToString("X")); fails++; }
            ulong[] apge = new ulong[] { 0x80184, 0x207, 0x88400, 0x88402, 0x80001, 0x0, 0x1, 0xFFFFFFFFUL };
            foreach (ulong v in apge)
                if (Refusal(new ReadCall("t", ClassApge, "GetFunction", "uiInput", "uiOutput", v, "")) == null) { Console.WriteLine("FAIL APGeAction.GetFunction input 0x" + v.ToString("X") + " allowed"); fails++; }
            if (Refusal(new ReadCall("t", ClassApge, "GetFunction", "uiInput", "uiOutput", 0x88401, "")) != null) { Console.WriteLine("FAIL APGeAction.GetFunction 0x88401 refused"); fails++; }
            if (Refusal(new ReadCall("t", ClassApge, "SetFunction", "uiInput", "uiOutput", 0x88401, "")) == null) { Console.WriteLine("FAIL APGeAction.SetFunction allowed"); fails++; }
            for (ulong i = 0; i <= 0x1FF; i++)
                if ((Refusal(new ReadCall("t", ClassGaming, "GetGamingLED", "gmInput", "gmOutput", i, "")) == null) != (i == 1)) { Console.WriteLine("FAIL GetGamingLED input 0x" + i.ToString("X")); fails++; }
            foreach (string m in new string[] { "GetGamingLEDBehavior", "GetGamingRgbKb", "GetGamingKBBacklight", "GetGamingLEDColor", "SetGamingLED", "SetGamingRgbKb", "SetGamingKBBacklight" })
                if (Refusal(new ReadCall("t", ClassGaming, m, "gmInput", "gmOutput", 1, "")) == null) { Console.WriteLine("FAIL " + m + " allowed"); fails++; }
            Console.WriteLine(fails == 0 ? "self-test-refusals: OK (GetGamingMiscSetting allows only 2, 9, 0x0A, 0x0B; Set* and every other input refused; no WMI touched)" : "self-test-refusals: " + fails + " FAILURES");
            return fails == 0 ? 0 : 1;
        }

        static string Hex(ulong v) { return "0x" + v.ToString("X"); }

        static Node RunReads(List<ReadCall> calls)
        {
            Node n = new Node();
            if (!IsElevated()) { n.Set("error", "needs an elevated (administrator) process"); return n; }
            Dictionary<string, ManagementObject> instances = new Dictionary<string, ManagementObject>();
            try
            {
                foreach (ReadCall c in calls)
                {
                    // Defence in depth: the allowlist is checked again right before the call.
                    string why = Refusal(c);
                    if (why != null) { n.Set(c.Key, "REFUSED: " + why); n.Set("stopped_at", c.Key); return n; }
                    Node r = new Node();
                    r.Set("call", c.Describe());
                    bool clean = false;
                    try
                    {
                        ManagementObject inst;
                        if (!instances.TryGetValue(c.Class, out inst))
                        {
                            using (ManagementClass mc = new ManagementClass(WmiNamespace, c.Class, null))
                                foreach (ManagementObject o in mc.GetInstances()) { inst = o; break; }
                            if (inst == null) throw new InvalidOperationException("no instance of " + c.Class);
                            instances[c.Class] = inst;
                        }
                        ManagementBaseObject inParams = inst.GetMethodParameters(c.Method);
                        // gmInput / uiInput of the Get* methods are UInt32 in the schema.
                        inParams[c.InName] = (uint)c.Input;
                        ManagementBaseObject outParams = inst.InvokeMethod(c.Method, inParams, null);
                        ulong raw = Convert.ToUInt64(outParams[c.OutName], CultureInfo.InvariantCulture);
                        r.Set("input", Hex(c.Input));
                        r.Set("raw", Hex(raw));
                        uint status = (uint)(raw & 0xFF);
                        r.Set("status", status);
                        Decode(r, c.Decoder, raw, status);
                        clean = status == 0;
                    }
                    catch (Exception e) { r.Set("error", e.Message); }
                    n.Set(c.Key, r);
                    if (!clean) { n.Set("stopped_at", c.Key + " (error or non-zero status; remaining calls NOT made)"); return n; }
                }
            }
            catch (Exception e) { n.Set("error", e.Message); }
            return n;
        }

        // The decodings below are the UI's own parse of each answer (see FINDINGS.md); the raw value is always printed too.
        static void Decode(Node r, string decoder, ulong raw, uint status)
        {
            if (status != 0) return;
            switch (decoder)
            {
                case "sensor": r.Set("value", (int)((raw >> 8) & 0xFFFF)); break;
                case "batteryboost": r.Set("battery_boost_flag_bits40_47", (int)((raw >> 40) & 0xFF)); break;
                case "coolbooster": r.Set("byte1", (int)((raw >> 8) & 0xFF)); r.Set("cool_booster_on", ((raw >> 8) & 0xFF) == 1); break;
                case "backlight":
                    r.Set("brightness_percent_bits32_39", (int)((raw >> 32) & 0xFF));
                    r.Set("bits40_47", (int)((raw >> 40) & 0xFF));
                    r.Set("backlight_off_if_bits40_47_eq_30", ((raw >> 40) & 0xFF) == 30);
                    break;
                case "gpumode": r.Set("mode_bits8_15", (int)((raw >> 8) & 0xFF)); break;
                case "opmode": r.Set("mode_bits8_15_if_layout_is_like_other_misc_reads", (int)((raw >> 8) & 0xFF)); r.Set("bits16_31", (int)((raw >> 16) & 0xFFFF)); r.Set("bits32_63", (long)(raw >> 32)); break;
                case "opmodecap": r.Set("bits8_15", (int)((raw >> 8) & 0xFF)); r.Set("bits16_31", (int)((raw >> 16) & 0xFFFF)); r.Set("bits32_63", (long)(raw >> 32)); break;
                case "gpucap": r.Set("cap_bit8", ((raw >> 8) & 1) == 1); r.Set("cap_bit9", ((raw >> 9) & 1) == 1); r.Set("cap_bit10", ((raw >> 10) & 1) == 1); break;
                case "ledbehavior":
                    r.Set("bits8_9", (int)((raw >> 8) & 3));
                    r.Set("bits14_15", (int)((raw >> 14) & 3));
                    break;
            }
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

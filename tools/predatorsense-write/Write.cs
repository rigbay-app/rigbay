// predatorsense-write: the ONLY tool in this repo that may write to the Acer firmware, and only the writes in
// Writes[] below, which are exactly the writes the owner approved (see docs/predatorsense/WRITE-PROPOSALS.md).
// Today that is TWO entries: W4a, AcerGamingFunction.SetGamingFanBehavior(0x01410019) = all fans Auto (B4), and
// W4b, SetGamingFanBehavior(0x02820019) = all fans Max (B5), which always runs together with W4a as its undo.
// The value cannot be given on the command line: the call is chosen by key, the input is a constant.
//
// C# 5 / csc.exe from .NET Framework, no SDK, same as tools/predatorsense-probe.
// Needs an elevated process (instance access under root\WMI is denied to normal users).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Rigbay.PredatorSenseWrite
{
    static class Program
    {
        const string Namespace = @"root\WMI";
        const string ClassGaming = "AcerGamingFunction";

        // ---- the allow-list of writes -------------------------------------------------------------
        // Adding an entry here is a code change that needs the owner's approval for that exact call and value.

        class AllowedWrite
        {
            public string Key, Class, Method, InName, OutName, Description;
            public ulong Input;
            public string UndoKey;   // when set, the run is "write, watch at most 15 s, then this undo write" (own log for the undo)
            public AllowedWrite(string key, string cls, string method, string inName, string outName, ulong input, string description, string undoKey)
            { Key = key; Class = cls; Method = method; InName = inName; OutName = outName; Input = input; Description = description; UndoKey = undoKey; }
            public string Describe() { return Class + "." + Method + "(0x" + Input.ToString("X8") + ")"; }
        }

        static readonly AllowedWrite[] Writes = new AllowedWrite[]
        {
            // Gate g-write-w4a, answered "approve" by the owner: once, elevated, all fans Auto.
            // lo 0x0019 = CPU fan (0x01) + GPU 1 (0x08) + GPU 2 (0x10); hi 0x0141 = mode 1 (auto) in slots 0, 3, 4.
            new AllowedWrite("W4a", ClassGaming, "SetGamingFanBehavior", "gmInput", "gmOutput", 0x01410019UL,
                "fan mode, all fans Auto (restates the recorded state)", null),
            // Gate g-write-w4b, answered "approve" by the owner: exactly two calls, once each, elevated:
            // this one for at most 15 s, then W4a (all fans Auto) as the undo. Same mask 0x0019, mode 2 (max) in slots 0, 3, 4.
            new AllowedWrite("W4b", ClassGaming, "SetGamingFanBehavior", "gmInput", "gmOutput", 0x02820019UL,
                "fan mode, all fans Max for at most 15 s, then W4a as the undo", "W4a")
        };

        static AllowedWrite FindWrite(string key)
        {
            for (int i = 0; i < Writes.Length; i++) if (string.Equals(Writes[i].Key, key, StringComparison.Ordinal)) return Writes[i];
            return null;
        }

        // Defence in depth: checked when the call is chosen and again right before it is sent.
        static string WriteRefusal(string cls, string method, ulong input)
        {
            for (int i = 0; i < Writes.Length; i++)
            {
                AllowedWrite w = Writes[i];
                if (string.Equals(w.Class, cls, StringComparison.Ordinal) && string.Equals(w.Method, method, StringComparison.Ordinal) && w.Input == input)
                    return null;
            }
            return "not on the owner-approved write list (" + cls + "." + method + "(0x" + input.ToString("X") + "))";
        }

        // The only reads this tool makes: GetGamingSysInfo sensor indexes already approved and run in B2/B3.
        static readonly int[] ReadIndexes = new int[] { 1, 2, 3, 6, 9, 10 };
        static string ReadRefusal(string cls, string method, ulong input)
        {
            if (cls == ClassGaming && method == "GetGamingSysInfo" && (input & 0xFF) == 1)
            {
                ulong idx = input >> 8;
                for (int i = 0; i < ReadIndexes.Length; i++) if ((ulong)ReadIndexes[i] == idx) return null;
            }
            return "read is not on the approved list";
        }

        // ---- log ----------------------------------------------------------------------------------

        static StreamWriter _log;
        static string _logPath;
        static readonly object LogLock = new object();

        static StreamWriter OpenWriter(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            StreamWriter w = new StreamWriter(fs, new UTF8Encoding(false));
            w.AutoFlush = true;
            return w;
        }

        static void OpenLog(string path)
        {
            _logPath = path;
            _log = OpenWriter(path);
        }

        // Switches all further log lines to another, already open file (the undo write has its own log).
        static void SwitchLog(string path, StreamWriter next)
        {
            lock (LogLock)
            {
                StreamWriter old = _log;
                _log = next; _logPath = path;
                if (old != null) { try { old.Flush(); old.Dispose(); } catch (Exception) { } }
            }
        }

        static void Log(string evt, string detail)
        {
            DateTime now = DateTime.Now;
            string line = now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z"
                + " local=" + now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " event=" + evt + (string.IsNullOrEmpty(detail) ? "" : " " + detail);
            lock (LogLock)
            {
                Console.WriteLine(line);
                if (_log != null) { _log.WriteLine(line); _log.Flush(); }
            }
        }

        static bool LogAlreadyHasWriteAttempt(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs))
                {
                    string s;
                    while ((s = sr.ReadLine()) != null) if (s.IndexOf("event=WRITE_ATTEMPT", StringComparison.Ordinal) >= 0) return true;
                }
            }
            catch (Exception) { return true; }   // cannot tell: refuse
            return false;
        }

        // ---- WMI with a timeout -------------------------------------------------------------------
        // A WMI-ACPI call can stall (FINDINGS 12.5). Every call runs on its own thread; a timeout is reported and the
        // caller decides (the write path exits the process: no retry, no further calls).

        class CallResult { public bool Done; public ulong Raw; public Exception Error; public string Extra; }

        static ManagementObject _instance;

        static ManagementObject Instance()
        {
            if (_instance != null) return _instance;
            using (ManagementClass mc = new ManagementClass(Namespace, ClassGaming, null))
                foreach (ManagementObject o in mc.GetInstances()) { _instance = o; break; }
            if (_instance == null) throw new InvalidOperationException("no instance of " + ClassGaming);
            return _instance;
        }

        static CallResult RunWithTimeout(Func<CallResult> work, int timeoutMs)
        {
            CallResult r = new CallResult();
            Thread t = new Thread(delegate ()
            {
                try { CallResult x = work(); r.Raw = x.Raw; r.Extra = x.Extra; }
                catch (Exception e) { r.Error = e; }
                r.Done = true;
            });
            t.IsBackground = true;
            t.Start();
            if (!t.Join(timeoutMs)) { r.Done = false; r.Error = null; return r; }
            return r;
        }

        // A sensor read: GetGamingSysInfo, UInt32 in (as the probe does), UInt32 out.
        static CallResult ReadSysInfo(int index, int timeoutMs)
        {
            ulong input = (ulong)((index << 8) | 1);
            string why = ReadRefusal(ClassGaming, "GetGamingSysInfo", input);
            if (why != null) { CallResult c = new CallResult(); c.Done = true; c.Error = new InvalidOperationException("REFUSED: " + why); return c; }
            return RunWithTimeout(delegate ()
            {
                ManagementObject inst = Instance();
                ManagementBaseObject p = inst.GetMethodParameters("GetGamingSysInfo");
                p["gmInput"] = (uint)input;
                ManagementBaseObject o = inst.InvokeMethod("GetGamingSysInfo", p, null);
                CallResult x = new CallResult();
                x.Raw = Convert.ToUInt64(o["gmOutput"], CultureInfo.InvariantCulture);
                return x;
            }, timeoutMs);
        }

        // ---- power, battery, load -----------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct SystemPowerStatus
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SystemPowerStatus s);

        static bool IsElevated()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception) { return false; }
        }

        // ---- nvidia-smi (best effort, read-only) --------------------------------------------------

        static void NvidiaSmi(string tag)
        {
            string[] candidates = new string[] { "nvidia-smi", @"C:\Windows\System32\nvidia-smi.exe", @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" };
            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(candidates[i],
                        "--query-gpu=temperature.gpu,fan.speed,power.draw,utilization.gpu,clocks.sm,pstate --format=csv,noheader");
                    psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.CreateNoWindow = true;
                    using (Process p = Process.Start(psi))
                    {
                        if (!p.WaitForExit(8000)) { try { p.Kill(); } catch (Exception) { } Log("NVIDIA_SMI", "tag=" + tag + " result=timeout"); return; }
                        Log("NVIDIA_SMI", "tag=" + tag + " exit=" + p.ExitCode + " out=\"" + p.StandardOutput.ReadToEnd().Trim().Replace("\r", " ").Replace("\n", " | ") + "\"");
                        return;
                    }
                }
                catch (Exception) { }
            }
            Log("NVIDIA_SMI", "tag=" + tag + " result=not-available");
        }

        // ---- one pass of sensor reads --------------------------------------------------------------

        class Sample
        {
            public bool Ok = true;                  // every read returned status 0 within the timeout
            public Dictionary<int, int> Value = new Dictionary<int, int>();
        }

        static Sample ReadPass(string phase, int[] indexes) { return ReadPass(phase, indexes, 10000, true); }

        // exitOnTimeout = false is used only while a Max override is armed: the caller must be able to run the undo.
        static Sample ReadPass(string phase, int[] indexes, int timeoutMs, bool exitOnTimeout)
        {
            Sample s = new Sample();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < indexes.Length; i++)
            {
                int idx = indexes[i];
                CallResult r = ReadSysInfo(idx, timeoutMs);
                string call = "GetGamingSysInfo(0x" + ((idx << 8) | 1).ToString("X") + ")";
                if (!r.Done)
                {
                    Log("READ_TIMEOUT", "phase=" + phase + " call=" + call + (exitOnTimeout ? " (no further calls)" : " (caller decides)"));
                    if (exitOnTimeout) Environment.Exit(3);
                    s.Ok = false;
                    return s;
                }
                if (r.Error != null) { Log("READ_ERROR", "phase=" + phase + " call=" + call + " error=\"" + r.Error.Message + "\""); s.Ok = false; return s; }
                uint status = (uint)(r.Raw & 0xFF);
                int value = (int)((r.Raw >> 8) & 0xFFFF);
                Log("READ", "phase=" + phase + " call=" + call + " index=" + idx + " raw=0x" + r.Raw.ToString("X") + " status=" + status + " value=" + value);
                if (status != 0) { s.Ok = false; return s; }
                s.Value[idx] = value;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(idx + "=" + value);
            }
            Log("PASS", "phase=" + phase + " values: " + sb.ToString());
            return s;
        }

        // ---- one allow-listed write ---------------------------------------------------------------
        // Logs WRITE_ATTEMPT and the outcome. Returns the status byte (0 = ok), -1 on a WMI error, -2 on a timeout, -3 refused.
        // Never retries.

        static int SendWrite(AllowedWrite w)
        {
            string again = WriteRefusal(w.Class, w.Method, w.Input);
            if (again != null) { Log("REFUSED", "reason=" + again); return -3; }
            Stopwatch clock = Stopwatch.StartNew();
            Log("WRITE_ATTEMPT", "key=" + w.Key + " call=" + w.Describe() + " input=0x" + w.Input.ToString("X8") + " timeout_ms=15000");
            ulong input = w.Input;
            CallResult wr = RunWithTimeout(delegate ()
            {
                ManagementObject inst = Instance();
                ManagementBaseObject p = inst.GetMethodParameters(w.Method);
                p[w.InName] = input;                       // ulong: UInt64 in the schema, checked before
                ManagementBaseObject o = inst.InvokeMethod(w.Method, p, null);
                CallResult x = new CallResult();
                x.Raw = Convert.ToUInt64(o[w.OutName], CultureInfo.InvariantCulture);
                return x;
            }, 15000);
            long elapsed = clock.ElapsedMilliseconds;
            if (!wr.Done) { Log("WRITE_TIMEOUT", "key=" + w.Key + " elapsed_ms=" + elapsed + " state unknown; NOT retried"); return -2; }
            if (wr.Error != null) { Log("WRITE_ERROR", "key=" + w.Key + " elapsed_ms=" + elapsed + " error=\"" + wr.Error.Message + "\" (not retried)"); return -1; }
            uint status = (uint)(wr.Raw & 0xFF);
            Log("WRITE_RESULT", "key=" + w.Key + " call=" + w.Describe() + " input=0x" + w.Input.ToString("X8") + " raw=0x" + wr.Raw.ToString("X") + " status=" + status + " elapsed_ms=" + elapsed);
            if (status != 0) Log("ALERT", "key=" + w.Key + " write status is non-zero; not retried");
            return (int)status;
        }

        // ---- W4b: Max for at most 15 s, then the undo ----------------------------------------------
        // Sequence (everything checked that can be checked BEFORE the Max write, so the undo cannot be refused afterwards):
        //   Max write -> reads of 2, 6, 9, 1, 10 every 2 s (t = 0..14 s, 3 s read timeout) -> undo write (W4a, its own log)
        //   -> reads every 2 s for up to 60 s until all three fans are inside the baseline range twice in a row.
        // Abort rules (each ends the Max window at once and runs the undo): non-zero status or WMI error or timeout of the Max write,
        // a failed or non-zero read, a temperature of 90 C or more, no fan rise of at least RiseRpm over the baseline maximum within 10 s.
        const int RiseRpm = 300;

        static int RunMaxThenUndo(AllowedWrite w, string undoLogPath, Dictionary<int, int> hi, Dictionary<int, int> rlo, Dictionary<int, int> rhi)
        {
            AllowedWrite undo = FindWrite(w.UndoKey);
            if (undo == null || WriteRefusal(undo.Class, undo.Method, undo.Input) != null) { Log("REFUSED", "reason=the undo write is not on the allow-list"); return 2; }
            if (string.Equals(Path.GetFullPath(undoLogPath), Path.GetFullPath(_logPath), StringComparison.OrdinalIgnoreCase)) { Log("REFUSED", "reason=the undo needs its own log file"); return 2; }
            if (LogAlreadyHasWriteAttempt(undoLogPath)) { Log("REFUSED", "reason=the undo log already holds a write attempt: " + undoLogPath); return 2; }
            StreamWriter undoWriter;
            try { undoWriter = OpenWriter(undoLogPath); }
            catch (Exception e) { Log("REFUSED", "reason=cannot open the undo log: " + e.Message); return 2; }

            int[] fans = new int[] { 2, 6, 9 };
            int[] temps = new int[] { 1, 10 };
            int[] post = new int[] { 2, 6, 9, 1, 10 };
            Dictionary<int, int> peak = new Dictionary<int, int>(), tpeak = new Dictionary<int, int>(), last = new Dictionary<int, int>();
            for (int i = 0; i < fans.Length; i++) { peak[fans[i]] = 0; last[fans[i]] = 0; }
            for (int i = 0; i < temps.Length; i++) tpeak[temps[i]] = 0;
            bool risen = false;
            string abort = null;
            Log("MAX_PLAN", "rise_threshold_rpm=" + RiseRpm + " over the baseline max: fan2>=" + (hi[2] + RiseRpm) + " fan6>=" + (hi[6] + RiseRpm) + " fan9>=" + (hi[9] + RiseRpm)
                + " window=14s of reads, hard cap 15s; undo=" + undo.Key + " undo_log=" + undoLogPath);

            // ---- the Max write and the window ----
            Stopwatch held = Stopwatch.StartNew();
            int rc = SendWrite(w);
            if (rc != 0) abort = "Max write did not return status 0 (code " + rc + ")";
            else
            {
                try
                {
                    Stopwatch since = Stopwatch.StartNew();
                    for (int n = 0; n <= 7 && abort == null; n++)
                    {
                        long wait = n * 2000L - since.ElapsedMilliseconds;
                        if (wait > 0) Thread.Sleep((int)wait);
                        Sample s = ReadPass("max+" + (since.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s", post, 3000, false);
                        if (!s.Ok) { abort = "a read failed, timed out or returned a non-zero status"; break; }
                        for (int i = 0; i < fans.Length; i++)
                        {
                            int v = s.Value[fans[i]];
                            last[fans[i]] = v;
                            if (v > peak[fans[i]]) peak[fans[i]] = v;
                            if (v >= hi[fans[i]] + RiseRpm && !risen) { risen = true; Log("RISE", "fan index " + fans[i] + " = " + v + " (baseline max " + hi[fans[i]] + ") at " + since.ElapsedMilliseconds + " ms"); }
                        }
                        for (int i = 0; i < temps.Length; i++)
                        {
                            int v = s.Value[temps[i]];
                            if (v > tpeak[temps[i]]) tpeak[temps[i]] = v;
                            if (v >= 90) { abort = "temperature index " + temps[i] + " = " + v + " C (90 or more)"; }
                        }
                        if (abort == null && !risen && since.ElapsedMilliseconds >= 10000) abort = "no fan rise within 10 s";
                        if (since.ElapsedMilliseconds >= 15000) break;
                    }
                }
                catch (Exception e) { abort = "exception in the window: " + e.Message; }
            }

            // ---- the undo (always, once) ----
            long heldMs = held.ElapsedMilliseconds;
            Log("UNDO_HANDOFF", "reason=" + (abort ?? "window over (14 s of reads)") + " max_window_ms=" + heldMs + " risen=" + risen
                + " peak2=" + peak[2] + " peak6=" + peak[6] + " peak9=" + peak[9] + " temp_peak1=" + tpeak[1] + " temp_peak10=" + tpeak[10]
                + " next_log=" + undoLogPath);
            SwitchLog(undoLogPath, undoWriter);
            Log("START", "key=" + undo.Key + " call=" + undo.Describe() + " mode=UNDO after " + w.Key + " (max window " + heldMs + " ms; reason: " + (abort ?? "window over") + ")");
            int urc = SendWrite(undo);

            // ---- back to baseline? ----
            int exit = 0;
            bool back = false;
            int inRange = 0;
            long backAtMs = -1;
            Dictionary<int, int> fin = new Dictionary<int, int>();
            Stopwatch u = Stopwatch.StartNew();
            for (int n = 0; n <= 30 && !back; n++)
            {
                long wait = n * 2000L - u.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
                Sample s = ReadPass("undo+" + (u.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s", post, 3000, false);
                if (!s.Ok) { Log("ALERT", "read failed after the undo (pass " + n + ")"); inRange = 0; continue; }
                bool all = true;
                for (int i = 0; i < fans.Length; i++)
                {
                    int v = s.Value[fans[i]];
                    fin[fans[i]] = v;
                    if (v < rlo[fans[i]] || v > rhi[fans[i]]) all = false;
                }
                for (int i = 0; i < temps.Length; i++) if (s.Value[temps[i]] >= 90) Log("ALERT", "temperature index " + temps[i] + " = " + s.Value[temps[i]] + " C after the undo");
                inRange = all ? inRange + 1 : 0;
                if (inRange >= 2) { back = true; backAtMs = u.ElapsedMilliseconds; }
            }
            NvidiaSmi("after");
            if (urc != 0 || !back)
            {
                exit = 5;
                Log("ALERT", "UNDO NOT CONFIRMED: undo_status=" + urc + " back_in_baseline=" + back + ". Press Auto in the Acer PredatorSense app NOW.");
            }
            else if (abort != null) exit = 1;
            Log("DONE", "mode=EXECUTE exit=" + exit + " risen=" + risen + " abort=" + (abort ?? "none") + " max_window_ms=" + heldMs
                + " undo_status=" + urc + " back_in_baseline=" + back + (backAtMs >= 0 ? " back_after_ms=" + backAtMs : "")
                + " peak2=" + peak[2] + " peak6=" + peak[6] + " peak9=" + peak[9] + " final2=" + (fin.ContainsKey(2) ? fin[2].ToString() : "n/a")
                + " final6=" + (fin.ContainsKey(6) ? fin[6].ToString() : "n/a") + " final9=" + (fin.ContainsKey(9) ? fin[9].ToString() : "n/a"));
            return exit;
        }

        // ---- main -----------------------------------------------------------------------------------

        static int Usage()
        {
            Console.WriteLine("predatorsense-write: runs ONLY the owner-approved Acer firmware writes listed below. Nothing else is possible.");
            Console.WriteLine();
            for (int i = 0; i < Writes.Length; i++)
                Console.WriteLine("  " + Writes[i].Key + "  " + Writes[i].Describe() + "  " + Writes[i].Description);
            Console.WriteLine();
            Console.WriteLine("  --call <key>               dry run: checks, baseline reads, NO write");
            Console.WriteLine("  --call W4a --execute       the same, then the ONE write, then 20 s of reads. Once only (a log line blocks a repeat)");
            Console.WriteLine("  --call W4b --execute       the same, then W4b for at most 15 s with abort rules, then the undo W4a (own log), then reads until RPM is back (up to 60 s)");
            Console.WriteLine("  --check <Class> <Method> <hex input>   prints whether the allow-list would accept it; touches no WMI");
            Console.WriteLine("  --log <path>               log file (default: predatorsense-write.log next to the exe)");
            Console.WriteLine("  --undo-log <path>          W4b only: the undo's own log (default: the --log name with -undo before the extension)");
            Console.WriteLine("Exit codes: 0 ok, 1 write answered non-zero / alert during the reads (W4b: aborted, undo confirmed), 2 refused or bad arguments, 3 timeout, 4 precondition failed,");
            Console.WriteLine("            5 (W4b) undo not confirmed: use the Acer app's Auto button.");
            return 2;
        }

        static int Main(string[] args)
        {
            string key = null, logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "predatorsense-write.log");
            bool execute = false;
            string undoLogPath = null;
            string[] check = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--call" && i + 1 < args.Length) key = args[++i];
                else if (args[i] == "--execute") execute = true;
                else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
                else if (args[i] == "--undo-log" && i + 1 < args.Length) undoLogPath = args[++i];
                else if (args[i] == "--check" && i + 3 < args.Length) { check = new string[] { args[i + 1], args[i + 2], args[i + 3] }; i += 3; }
                else return Usage();
            }

            if (check != null)
            {
                ulong v;
                string h = check[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? check[2].Substring(2) : check[2];
                if (!ulong.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) { Console.WriteLine("REFUSED: input is not hex"); return 2; }
                string why = WriteRefusal(check[0], check[1], v);
                Console.WriteLine(why == null ? "ALLOWED (write list)" : "REFUSED: " + why);
                return why == null ? 0 : 2;
            }

            if (key == null) return Usage();
            AllowedWrite w = null;
            for (int i = 0; i < Writes.Length; i++) if (string.Equals(Writes[i].Key, key, StringComparison.Ordinal)) w = Writes[i];
            if (w == null) { Console.WriteLine("REFUSED: '" + key + "' is not a key on the write list"); return 2; }
            string refused = WriteRefusal(w.Class, w.Method, w.Input);
            if (refused != null) { Console.WriteLine("REFUSED: " + refused); return 2; }

            OpenLog(logPath);
            Log("START", "key=" + w.Key + " call=" + w.Describe() + " mode=" + (execute ? "EXECUTE" : "DRYRUN") + " log=" + _logPath);

            if (!IsElevated()) { Log("REFUSED", "reason=needs an elevated (administrator) process"); return 2; }
            if (execute && LogAlreadyHasWriteAttempt(_logPath)) { Log("REFUSED", "reason=a write was already attempted (see log); every write needs its own owner approval"); return 2; }

            // 1. Preconditions: AC power, battery above 50 %, no heavy load.
            SystemPowerStatus ps;
            if (!GetSystemPowerStatus(out ps)) { Log("PRECONDITION_FAILED", "reason=GetSystemPowerStatus failed"); return 4; }
            Log("POWER", "ac_line=" + ps.ACLineStatus + " battery_percent=" + ps.BatteryLifePercent + " battery_flag=" + ps.BatteryFlag);
            if (ps.ACLineStatus != 1) { Log("PRECONDITION_FAILED", "reason=not on AC power"); return 4; }
            if (ps.BatteryLifePercent == 255 || ps.BatteryLifePercent <= 50) { Log("PRECONDITION_FAILED", "reason=battery not above 50 percent (or unknown)"); return 4; }
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor"))
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        int load = Convert.ToInt32(o["LoadPercentage"], CultureInfo.InvariantCulture);
                        Log("CPU_LOAD", "percent=" + load);
                        if (load > 50) { Log("PRECONDITION_FAILED", "reason=CPU load above 50 percent"); return 4; }
                    }
            }
            catch (Exception e) { Log("PRECONDITION_FAILED", "reason=cannot read CPU load: " + e.Message); return 4; }

            // 2. The input type in the schema must be UInt64 (the document says so); no coercion.
            CallResult sc = RunWithTimeout(delegate ()
            {
                ManagementObject inst = Instance();
                ManagementBaseObject p = inst.GetMethodParameters(w.Method);
                PropertyData pd = p.Properties[w.InName];
                CallResult x = new CallResult();
                x.Extra = pd.Type.ToString();
                return x;
            }, 10000);
            if (!sc.Done) { Log("SCHEMA_TIMEOUT", "no further calls"); return 3; }
            if (sc.Error != null) { Log("PRECONDITION_FAILED", "reason=schema read failed: " + sc.Error.Message); return 4; }
            Log("SCHEMA", "method=" + w.Method + " in=" + w.InName + " type=" + sc.Extra);
            if (sc.Extra != "UInt64") { Log("PRECONDITION_FAILED", "reason=input type is not UInt64; not coercing"); return 4; }

            // 3. Baseline in the same minute: nvidia-smi, then three passes of sysinfo 1, 2, 3, 6, 9, 10, 2 s apart.
            NvidiaSmi("baseline");
            Dictionary<int, int> lo = new Dictionary<int, int>(), hi = new Dictionary<int, int>();
            for (int pass = 0; pass < 3; pass++)
            {
                Sample b = ReadPass("baseline" + (pass + 1), ReadIndexes);
                if (!b.Ok) { Log("PRECONDITION_FAILED", "reason=baseline read failed or non-zero status"); return 4; }
                foreach (KeyValuePair<int, int> kv in b.Value)
                {
                    if (!lo.ContainsKey(kv.Key) || kv.Value < lo[kv.Key]) lo[kv.Key] = kv.Value;
                    if (!hi.ContainsKey(kv.Key) || kv.Value > hi[kv.Key]) hi[kv.Key] = kv.Value;
                }
                if (pass < 2) Thread.Sleep(2000);
            }
            int[] fans = new int[] { 2, 6, 9 };
            int[] temps = new int[] { 1, 10 };
            for (int i = 0; i < fans.Length; i++)
                if (lo[fans[i]] <= 0) { Log("PRECONDITION_FAILED", "reason=fan index " + fans[i] + " reads 0 in the baseline"); return 4; }
            for (int i = 0; i < temps.Length; i++)
                if (hi[temps[i]] >= 90) { Log("PRECONDITION_FAILED", "reason=temperature index " + temps[i] + " is already 90 C or more"); return 4; }
            // "Baseline range" for the alert: 30 % either side of what the three baseline passes showed.
            Dictionary<int, int> rlo = new Dictionary<int, int>(), rhi = new Dictionary<int, int>();
            for (int i = 0; i < fans.Length; i++) { rlo[fans[i]] = (int)(lo[fans[i]] * 0.7); rhi[fans[i]] = (int)(hi[fans[i]] * 1.3); }
            Log("BASELINE_RANGE", "fan2=" + rlo[2] + ".." + rhi[2] + " fan6=" + rlo[6] + ".." + rhi[6] + " fan9=" + rlo[9] + ".." + rhi[9] + " (baseline min/max -30%/+30%)");

            if (!execute) { Log("DONE", "mode=DRYRUN no write was made"); return 0; }

            // 4a. A write with an undo (W4b): Max for at most 15 s, then the undo, in its own sequence.
            if (w.UndoKey != null)
            {
                if (undoLogPath == null)
                {
                    string ext = Path.GetExtension(logPath);
                    undoLogPath = logPath.Substring(0, logPath.Length - ext.Length) + "-undo" + ext;
                }
                return RunMaxThenUndo(w, undoLogPath, hi, rlo, rhi);
            }
            if (undoLogPath != null) { Log("REFUSED", "reason=--undo-log is only for a write with an undo"); return 2; }

            // 4. The ONE write.
            string again = WriteRefusal(w.Class, w.Method, w.Input);
            if (again != null) { Log("REFUSED", "reason=" + again); return 2; }
            Stopwatch clock = Stopwatch.StartNew();
            Log("WRITE_ATTEMPT", "key=" + w.Key + " call=" + w.Describe() + " input=0x" + w.Input.ToString("X8") + " timeout_ms=15000");
            ulong input = w.Input;
            CallResult wr = RunWithTimeout(delegate ()
            {
                ManagementObject inst = Instance();
                ManagementBaseObject p = inst.GetMethodParameters(w.Method);
                p[w.InName] = input;                       // ulong: UInt64 in the schema, checked above
                ManagementBaseObject o = inst.InvokeMethod(w.Method, p, null);
                CallResult x = new CallResult();
                x.Raw = Convert.ToUInt64(o[w.OutName], CultureInfo.InvariantCulture);
                return x;
            }, 15000);
            long elapsed = clock.ElapsedMilliseconds;
            int exit = 0;
            bool sampleAfter = true;
            if (!wr.Done)
            {
                Log("WRITE_TIMEOUT", "elapsed_ms=" + elapsed + " state unknown; NOT retried; no further calls made");
                return 3;
            }
            else if (wr.Error != null)
            {
                Log("WRITE_ERROR", "elapsed_ms=" + elapsed + " error=\"" + wr.Error.Message + "\" (not retried)");
                exit = 1;
            }
            else
            {
                uint status = (uint)(wr.Raw & 0xFF);
                Log("WRITE_RESULT", "call=" + w.Describe() + " input=0x" + w.Input.ToString("X8") + " raw=0x" + wr.Raw.ToString("X") + " status=" + status + " elapsed_ms=" + elapsed);
                if (status != 0) { Log("ALERT", "write status is non-zero; not retried; no other write will be made"); exit = 1; }
            }

            // 5. Reads of 2, 6, 9, 1, 10 every 2 s for 20 s (11 passes: t=0, 2, ... 20).
            int[] post = new int[] { 2, 6, 9, 1, 10 };
            Stopwatch since = Stopwatch.StartNew();
            for (int n = 0; n <= 10 && sampleAfter; n++)
            {
                long due = n * 2000L;
                long wait = due - since.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
                Sample s = ReadPass("after+" + (since.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s", post);
                if (!s.Ok) { Log("ALERT", "read failed or non-zero status after the write; sampling stopped"); exit = 1; break; }
                for (int i = 0; i < fans.Length; i++)
                {
                    int v = s.Value[fans[i]];
                    if (v < rlo[fans[i]] || v > rhi[fans[i]])
                    { Log("ALERT", "fan index " + fans[i] + " = " + v + " is outside the baseline range " + rlo[fans[i]] + ".." + rhi[fans[i]]); exit = 1; }
                }
                for (int i = 0; i < temps.Length; i++)
                    if (s.Value[temps[i]] >= 90) { Log("ALERT", "temperature index " + temps[i] + " = " + s.Value[temps[i]] + " C (90 or more)"); exit = 1; }
            }
            NvidiaSmi("after");
            Log("DONE", "mode=EXECUTE exit=" + exit + (exit == 0 ? " (status 0, RPM inside the baseline range for the whole window)" : " (see ALERT/WRITE_* lines above)"));
            return exit;
        }
    }
}

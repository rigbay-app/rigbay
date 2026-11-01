// Design B harness (.NET 10): behaves like a Rigbay module that owns a helper process.
// Read-only: it only sends the ops "hello", "status", "monitor", "ping", "quit" (see helper/XtuHelper.cs).
using System.Diagnostics;
using System.Text;
using System.Text.Json;

string helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "XtuHelper.exe"));
int n = 50, cold = 5;
string? outFile = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--helper") helper = args[++i];
    else if (args[i] == "--n") n = int.Parse(args[++i]);
    else if (args[i] == "--cold") cold = int.Parse(args[++i]);
    else if (args[i] == "--out") outFile = args[++i];
}

if (args.Contains("--orphan"))
{
    // Lifecycle test: start the helper, publish its pid, then die without any cleanup. The helper must notice (stdin closes) and exit.
    var o = Helper.Start(helper);
    var r = o.Call("hello");
    File.WriteAllText(args[Array.IndexOf(args, "--orphan") + 1], r.Data.GetProperty("pid").GetInt32().ToString());
    Process.GetCurrentProcess().Kill();
}

var report = new StringBuilder();
void Say(string s) { Console.WriteLine(s); report.AppendLine(s); }

Say($"harness runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, pid {Environment.ProcessId}");
Say($"helper: {helper}  (files next to it: {string.Join(", ", Directory.GetFiles(Path.GetDirectoryName(helper)!).Select(Path.GetFileName))})");

// ---- one long-lived helper: warm latency ---------------------------------------------------------------------
var sw = Stopwatch.StartNew();
using var h = Helper.Start(helper);
var hello = h.Call("hello");
double startMs = sw.Elapsed.TotalMilliseconds;
Say($"\nspawn + hello: {startMs:F1} ms   reply: {hello.Raw}");

var st1 = h.Call("status");
Say($"status #1 (cold: pipe connect + 6 calls): round trip {st1.RoundTripMs:F1} ms, inside XTU calls {st1.InnerMs:F1} ms, ok={st1.Ok}");
if (!st1.Ok) Say("  error: " + st1.Error);
Say("  raw: " + st1.Raw);
if (st1.Ok) PrintStatus(st1);

var mon1 = h.Call("monitor");
Say($"\nmonitor #1 (cold: Connect + Start + GetValues): round trip {mon1.RoundTripMs:F1} ms, inside XTU {mon1.InnerMs:F1} ms, ok={mon1.Ok}");
if (!mon1.Ok) Say("  error: " + mon1.Error);
else
{
    var vals = mon1.Data.GetProperty("values");
    Say($"  {mon1.Data.GetProperty("count").GetInt32()} monitor values; package power(1000)={Val(vals, "1000")} W, package temp(2701131777)={Val(vals, "2701131777")} C, core0 temp(7)={Val(vals, "7")} C");
}

var ping = Series(h, "ping", n);
var status = Series(h, "status", n);
var monitor = Series(h, "monitor", n);
Say($"\nwarm latency, {n} calls each, same helper (ms):");
Say("                    p50      p95      min      max     helper-inner p50");
Say(Row("ping (IPC only)", ping));
Say(Row("status (6 XTU calls)", status));
Say(Row("monitor (GetValues)", monitor));

// ---- paced like a 1 Hz sampler -------------------------------------------------------------------------------
var paced = new List<Reply>();
for (int i = 0; i < 5; i++) { Thread.Sleep(1000); paced.Add(h.Call("monitor")); }
Say($"monitor at 1 Hz (5 samples): " + string.Join(", ", paced.Select(r => r.RoundTripMs.ToString("F1"))) + " ms");

var hello2 = h.Call("hello");
Say($"helper working set after the series: {hello2.Data.GetProperty("workingSetMB").GetDouble()} MB (just after start: {hello.Data.GetProperty("workingSetMB").GetDouble()} MB)");
h.Call("quit");
h.WaitExit();

// ---- throw-away helper per call: what 'spawn on demand' would cost --------------------------------------------
var coldTimes = new List<double>();
for (int i = 0; i < cold; i++)
{
    var t = Stopwatch.StartNew();
    using var hh = Helper.Start(helper);
    var r = hh.Call("status");
    coldTimes.Add(t.Elapsed.TotalMilliseconds);
    hh.Call("quit"); hh.WaitExit();
    if (!r.Ok) Say("  cold run error: " + r.Error);
}
Say($"\nspawn + connect + status + exit, fresh helper each time ({cold} runs, ms): " + string.Join(", ", coldTimes.Select(x => x.ToString("F0"))));

// ---- failure behaviour: kill the helper in the middle --------------------------------------------------------
using (var k = Helper.Start(helper))
{
    k.Call("hello");
    k.Kill();
    var r = k.Call("status");
    Say($"\ncall after the helper was killed: ok={r.Ok}, error={r.Error}");
}

if (outFile != null) File.WriteAllText(outFile, report.ToString());
return 0;

// ---------------------------------------------------------------------------------------------------------------
static string Val(JsonElement vals, string id) => vals.TryGetProperty(id, out var v) ? v.ToString() : "n/a";

static void PrintStatus(Reply r)
{
    var c = r.Data.GetProperty("controls");
    decimal A(string id) => c.GetProperty(id).GetProperty("active").GetDecimal();
    Console.WriteLine($"  service: {r.Data.GetProperty("service").GetString()}");
    Console.WriteLine($"  active: PL1={A("48"):0.###} PL2={A("47"):0.###} pl2enable={(A("49") != 0)} window={A("66"):0.###}");
    Console.WriteLine($"  restore after reboot: {r.Data.GetProperty("restoreOnBoot").GetBoolean()}");
}

static List<Reply> Series(Helper h, string op, int n)
{
    var l = new List<Reply>();
    for (int i = 0; i < n; i++) l.Add(h.Call(op));
    return l;
}

static string Row(string name, List<Reply> l)
{
    if (l.Any(x => !x.Ok)) return $"{name,-20} {l.Count(x => !x.Ok)} of {l.Count} failed: {l.First(x => !x.Ok).Error}";
    var rt = l.Select(x => x.RoundTripMs).OrderBy(x => x).ToList();
    var inner = l.Select(x => x.InnerMs).OrderBy(x => x).ToList();
    double P(List<double> s, double p) => s[Math.Min(s.Count - 1, (int)Math.Ceiling(p * s.Count) - 1)];
    return $"{name,-20} {P(rt, .5),7:F2}  {P(rt, .95),7:F2}  {rt[0],7:F2}  {rt[^1],7:F2}  {P(inner, .5),10:F2}";
}

record Reply(bool Ok, double RoundTripMs, double InnerMs, string Raw, string Error, JsonElement Data);

sealed class Helper : IDisposable
{
    private readonly Process p;
    private int nextId;

    private Helper(Process p) { this.p = p; }

    public static Helper Start(string exe)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, e) => { };   // drain stderr so the helper can never block on it
        p.BeginErrorReadLine();
        return new Helper(p);
    }

    public Reply Call(string op)
    {
        int id = ++nextId;
        var t = Stopwatch.StartNew();
        try
        {
            p.StandardInput.WriteLine($"{{\"id\":{id},\"op\":\"{op}\"}}");
            p.StandardInput.Flush();
            string? line = p.StandardOutput.ReadLine();
            double rt = t.Elapsed.TotalMilliseconds;
            if (line == null) return new Reply(false, rt, 0, "", "helper closed its output (exit code " + (p.HasExited ? p.ExitCode.ToString() : "?") + ")", default);
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement.Clone();
            bool ok = root.GetProperty("ok").GetBoolean();
            return new Reply(ok, rt,
                root.TryGetProperty("ms", out var ms) ? ms.GetDouble() : 0, line,
                root.TryGetProperty("error", out var er) ? er.GetString() ?? "" : "",
                root.TryGetProperty("data", out var d) ? d : default);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return new Reply(false, t.Elapsed.TotalMilliseconds, 0, "", ex.GetType().Name + ": " + ex.Message, default);
        }
    }

    public void WaitExit() { p.WaitForExit(5000); }
    public void Kill() { p.Kill(); p.WaitForExit(); }
    public void Dispose() { try { if (!p.HasExited) p.Kill(); } catch { } p.Dispose(); }
}

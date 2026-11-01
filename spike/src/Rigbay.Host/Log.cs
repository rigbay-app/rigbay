using System.Diagnostics;

namespace Rigbay.Host;

/// <summary>Tiny file log; every line carries milliseconds since process start.</summary>
static class Log
{
    static string? _path;
    static readonly object Gate = new();
    public static void Init(string? path) { _path = path; if (path != null) File.WriteAllText(path, ""); }
    public static double SinceStartMs => (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
    public static void W(string line)
    {
        var s = $"[{SinceStartMs,7:F0} ms] {line}";
        Debug.WriteLine(s);
        if (_path == null) return;
        lock (Gate) File.AppendAllText(_path, s + Environment.NewLine);
    }
}

sealed class Options
{
    public string? Log, Shot, ShotMode = "printwindow", Theme, Lang, ModulesDir;
    public int ExitAfterMs;
    public bool SelfTest, AllowMulti; public string? CrashProbe, StartPage;

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 1; i < a.Length; i++)
        {
            string Next() => i + 1 < a.Length ? a[++i] : "";
            switch (a[i])
            {
                case "--log": o.Log = Next(); break;
                case "--shot": o.Shot = Next(); break;
                case "--shot-mode": o.ShotMode = Next(); break;
                case "--theme": o.Theme = Next(); break;
                case "--lang": o.Lang = Next(); break;
                case "--modules": o.ModulesDir = Next(); break;
                case "--exit-after": int.TryParse(Next(), out o.ExitAfterMs); break;
                case "--selftest": o.SelfTest = true; break;
                case "--crash-probe": o.CrashProbe = Next(); break;
                case "--page": o.StartPage = Next(); break;
                case "--allow-multi": o.AllowMulti = true; break;
            }
        }
        return o;
    }
}

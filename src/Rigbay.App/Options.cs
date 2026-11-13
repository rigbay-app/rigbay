namespace Rigbay.App;

/// <summary>
/// Command line. Verbs: <c>Rigbay.App.exe &lt;module&gt; &lt;verb&gt; [args]</c> (headless; forwarded to a running instance).
/// Switches are for the shell, tests and screenshots; see docs/APP.md.
/// </summary>
sealed class Options
{
    static readonly HashSet<string> WithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "--data", "--modules", "--theme", "--lang", "--shot", "--shot-mode", "--page", "--script", "--out", "--size", "--exit-after", "--selftest-out", "--accent", "--shot-scale", "--time-scale", "--registry", "--wait-pid", "--slow-download", "--task-name", "--trim-delay", "--trim-steps",
    };

    public string? Data, Modules, Theme, Lang, Shot, ShotMode = "printwindow", Page, Script, Out, Size, SelfTestOut, Accent;
    public int ExitAfterMs;
    /// <summary>Test switch: a local registry file or URL. Honoured only with scratch --data and --modules (docs/INSTALL.md).</summary>
    public string? Registry;
    /// <summary>Set by "Restart now": the new process waits for this process id to end before it starts.</summary>
    public int WaitPid;
    /// <summary>Test switch (with --registry): milliseconds to wait before each 16 KB read of a local file, so progress and cancel can be seen.</summary>
    public int SlowDownloadMs;
    /// <summary>Test switch: render the screenshot at this display scale (1.5, 2) with a RenderTargetBitmap of the window's own content; icons are then picked for that scale.</summary>
    public double ShotScale = 1;
    /// <summary>Test switch: the metrics clock (sample times, history, charts) runs this many times faster than real time, so a screenshot can show five minutes of history after one.</summary>
    public double TimeScale = 1;
    /// <summary>Test switch <c>--trim-delay &lt;seconds&gt;</c>: wait this long in the tray before the memory trim, instead of the setting's delay (0 = the setting).</summary>
    public int TrimDelaySeconds;
    /// <summary>Test switch <c>--trim-steps all|none|drop,caches,gc,ws</c>: which steps of the memory trim run (tools\perf\measure.ps1 measures them one by one).</summary>
    public Services.TrimSteps TrimSteps = Services.TrimSteps.Default;
    public bool Tray, SelfTest, AllowMulti, LangCheck, Help;
    /// <summary><c>--version</c>: print the version and exit (install scripts, bug reports).</summary>
    public bool Version;
    /// <summary>Test switch: another name for the logon task (letters, digits, dot, dash), so install tests never touch the real <c>Rigbay</c> task.</summary>
    public string? TaskName;
    /// <summary><c>--uninstall-cleanup</c>: ask a running instance to quit, then remove the logon task. With <c>--keep-task</c> only the quit (used by an upgrade).</summary>
    public bool UninstallCleanup, KeepTask;
    /// <summary>Test switch: behave as on the first start without modules (opens the Modules page).</summary>
    public bool FirstRun;
    public List<string> Positional { get; } = new();
    public string[] Raw { get; private set; } = Array.Empty<string>();

    /// <summary>True when the command line is a verb for a module (or one of the host words).</summary>
    public bool IsVerb => Positional.Count > 0;

    public static Options Parse(string[] args)
    {
        var o = new Options { Raw = args };
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--"))
            {
                // once the verb words start, everything after them belongs to the verb
                o.Positional.AddRange(args.Skip(i));
                break;
            }
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (a.ToLowerInvariant())
            {
                case "--data": o.Data = Next(); break;
                case "--modules": o.Modules = Next(); break;
                case "--theme": o.Theme = Next(); break;
                case "--lang": o.Lang = Next(); break;
                case "--shot": o.Shot = Next(); break;
                case "--shot-mode": o.ShotMode = Next(); break;
                case "--page": o.Page = Next(); break;
                case "--script": o.Script = Next(); break;
                case "--out": o.Out = Next(); break;
                case "--size": o.Size = Next(); break;
                case "--shot-scale": double.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out o.ShotScale); break;
                case "--time-scale": double.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out o.TimeScale); break;
                case "--accent": o.Accent = Next(); break;   // test switch: pretend the Windows accent is this #RRGGBB (title bar mark)
                case "--registry": o.Registry = Next(); break;
                case "--wait-pid": int.TryParse(Next(), out o.WaitPid); break;
                case "--slow-download": int.TryParse(Next(), out o.SlowDownloadMs); break;
                case "--selftest-out": o.SelfTestOut = Next(); break;
                case "--exit-after": int.TryParse(Next(), out o.ExitAfterMs); break;
                case "--trim-delay": int.TryParse(Next(), out o.TrimDelaySeconds); break;
                case "--trim-steps": o.TrimSteps = Services.TrimStepsText.Parse(Next()) ?? Services.TrimSteps.Default; break;
                case "--tray": o.Tray = true; break;
                case "--selftest": o.SelfTest = true; break;
                case "--allow-multi": o.AllowMulti = true; break;
                case "--first-run": o.FirstRun = true; break;
                case "--langcheck": o.LangCheck = true; break;
                case "--help": case "-h": o.Help = true; break;
                case "--version": o.Version = true; break;
                case "--uninstall-cleanup": o.UninstallCleanup = true; break;
                case "--keep-task": o.KeepTask = true; break;
                case "--task-name": o.TaskName = Next(); break;
                default:
                    if (WithValue.Contains(a)) Next();
                    break;
            }
        }
        return o;
    }
}

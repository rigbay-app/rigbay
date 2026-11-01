namespace Rigbay.App;

/// <summary>
/// Command line. Verbs: <c>Rigbay.App.exe &lt;module&gt; &lt;verb&gt; [args]</c> (headless; forwarded to a running instance).
/// Switches are for the shell, tests and screenshots; see docs/APP.md.
/// </summary>
sealed class Options
{
    static readonly HashSet<string> WithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "--data", "--modules", "--theme", "--lang", "--shot", "--shot-mode", "--page", "--script", "--out", "--size", "--exit-after", "--selftest-out",
    };

    public string? Data, Modules, Theme, Lang, Shot, ShotMode = "printwindow", Page, Script, Out, Size, SelfTestOut;
    public int ExitAfterMs;
    public bool Tray, SelfTest, AllowMulti, LangCheck, Help;
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
                case "--selftest-out": o.SelfTestOut = Next(); break;
                case "--exit-after": int.TryParse(Next(), out o.ExitAfterMs); break;
                case "--tray": o.Tray = true; break;
                case "--selftest": o.SelfTest = true; break;
                case "--allow-multi": o.AllowMulti = true; break;
                case "--langcheck": o.LangCheck = true; break;
                case "--help": case "-h": o.Help = true; break;
                default:
                    if (WithValue.Contains(a)) Next();
                    break;
            }
        }
        return o;
    }
}

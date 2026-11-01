using System.Text;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App.Services;

/// <summary>
/// Headless modes of the executable: <c>--langcheck</c> and <c>&lt;module&gt; &lt;verb&gt;</c>. The exe is a Windows-subsystem program, so
/// output goes to the parent's console when there is one, and always to <c>--out &lt;file&gt;</c> when given (use it when the
/// shell does not wait for a GUI-subsystem process, or when the elevated process cannot reach the caller's console).
/// </summary>
static class Cli
{
    /// <summary>Checks the host's language files and those of every module under the modules root (the core's LangCheck).</summary>
    public static int LangCheck(Options o)
    {
        var sb = new StringBuilder();
        bool errors = false;
        void Check(string title, string dir, string? prefix)
        {
            sb.AppendLine($"== {title}: {dir}");
            if (!Directory.Exists(dir)) { sb.AppendLine("no such folder"); errors = true; return; }
            var report = Core.LangCheck.Check(Core.LangCheck.LoadDirectory(dir), new LangCheckOptions(prefix));
            sb.AppendLine(report.ToText().TrimEnd());
            errors |= report.HasErrors;
        }
        Check("host", AppPaths.HostLangDir, null);
        if (Directory.Exists(AppPaths.ModulesRoot))
            foreach (var dir in Directory.GetDirectories(AppPaths.ModulesRoot))
            {
                var result = ManifestReader.ReadFolder(dir);
                var id = result.Manifest?.Id ?? Path.GetFileName(dir);
                var lang = Path.Combine(dir, result.Manifest?.LanguagesFolder ?? "lang");
                if (Directory.Exists(lang) && Directory.GetFiles(lang, "*.lang").Length > 0) Check("module " + id, lang, LanguageCatalog.ModulePrefix(id));
            }
        sb.AppendLine(errors ? "langcheck: ERRORS" : "langcheck: OK");
        Emit(o, errors ? 1 : 0, sb.ToString().TrimEnd());
        return errors ? 1 : 0;
    }

    /// <summary>
    /// Used by uninstall.ps1 (and, with <c>--keep-task</c>, by an upgrade): asks a running instance to quit the normal way, waits up to
    /// 30 s for it to end, then removes the "Start with Windows" task through the same code that created it. Touches nothing else:
    /// not the modules, not the settings.
    /// </summary>
    public static int UninstallCleanup(Options o)
    {
        var lines = new List<string>();
        int exit = 0;
        var reply = InstanceServer.SendAsync(new[] { "quit" }).GetAwaiter().GetResult();
        if (reply is null) lines.Add("no running instance");
        else
        {
            lines.Add("asked the running instance to quit");
            var end = DateTime.UtcNow.AddSeconds(30);
            bool gone = false;
            while (DateTime.UtcNow < end)
            {
                if (!Mutex.TryOpenExisting(@"Global\Rigbay.App.SingleInstance", out var m)) { gone = true; break; }
                m.Dispose();
                Thread.Sleep(250);
            }
            lines.Add(gone ? "the instance has ended" : "the instance did not end within 30 s");
            if (!gone) exit = 1;
        }
        if (!o.KeepTask)
        {
            var (ok, msg) = StartupTask.Delete();
            lines.Add("start with Windows task: " + (ok ? msg : "could not remove: " + msg));
            if (!ok) exit = 1;
        }
        Emit(o, exit, string.Join(Environment.NewLine, lines));
        return exit;
    }

    public static int RunVerb(Options o)
    {
        var words = o.Positional;
        // 1. a running instance owns the modules: forward the words to it
        var forwarded = InstanceServer.SendAsync(new[] { "cmd" }.Concat(words).ToArray()).GetAwaiter().GetResult();
        if (forwarded is { } f)
        {
            Emit(o, f.Exit, f.Output);
            return f.Exit;
        }
        // 2. headless: load the modules, start what the verb needs, run it, stop
        var (exit, text) = RunHeadless(words).GetAwaiter().GetResult();
        Emit(o, exit, text);
        return exit;
    }

    static async Task<(int, string)> RunHeadless(IReadOnlyList<string> words)
    {
        using var host = new RigbayHost(AppPaths.HostOptions());
        host.Languages.LoadEmbedded(typeof(Cli).Assembly, "Rigbay.App.lang.");
        host.ApprovalPrompt = null;                      // no window: every hardware change is refused
        await Task.Run(host.Discover);
        try
        {
            // version, list and help are answered by the core (ExecuteCommandAsync), headless or forwarded alike
            if (words[0] is not ("list" or "version" or "help" or "--help" or "-h"))
            {
                var target = host.Find(words[0]);
                if (target != null && host.IsEnabledByUser(target.Id) && target.Lifecycle == ModuleLifecycle.Off) await Task.Run(() => target.Enable());
            }
            var result = await AppCommands.ExecuteAsync(host, words);
            return (result.ExitCode, result.Output);
        }
        finally { await Task.Run(host.Shutdown); }
    }

    /// <summary>Prints to the caller's console (stdout for 0, stderr otherwise) and to <c>--out</c>.</summary>
    public static void Emit(Options o, int exit, string text)
    {
        if (o.Out != null)
        {
            try { File.WriteAllText(o.Out, $"exit={exit}\n{text}\n", new UTF8Encoding(false)); } catch { }
        }
        try
        {
            Native.AttachConsole(-1);
            var stream = exit == 0 ? Console.OpenStandardOutput() : Console.OpenStandardError();
            var bytes = new UTF8Encoding(false).GetBytes(text + Environment.NewLine);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch { }
    }
}

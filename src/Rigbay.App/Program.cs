using Microsoft.UI.Dispatching;
using Rigbay.App.Services;
using Rigbay.Core;

namespace Rigbay.App;

/// <summary>
/// Entry point. The process decides here, before any XAML starts, whether this is a command-line verb
/// (<c>Rigbay.App.exe &lt;module&gt; &lt;verb&gt;</c>, headless), a language check, a second start of an already running
/// instance (shows the first one), or the UI.
/// </summary>
public static class Program
{
    const string MutexName = @"Global\Rigbay.App.SingleInstance";
    static Mutex? _single;

    [STAThread]
    public static int Main(string[] args)
    {
        var opt = Options.Parse(args);
        AppPaths.Init(opt.Data, opt.Modules);

        if (opt.LangCheck) return Cli.LangCheck(opt);
        if (opt.Help)
        {
            Cli.Emit(opt, 0, "Rigbay.App.exe [--tray] [--data <dir>] [--modules <dir>] | <module> <verb> [args] | list | version | --langcheck");
            return 0;
        }
        if (opt.IsVerb) return Cli.RunVerb(opt);

        if (!opt.AllowMulti)
        {
            _single = new Mutex(true, MutexName, out bool first);
            if (!first)
            {
                // A first instance is running: bring its window forward and leave.
                var r = InstanceServer.SendAsync(new[] { "show" }).GetAwaiter().GetResult();
                AppLog.Info("second instance: " + (r.HasValue ? "asked the first to show itself" : "no first instance answered") + "; exiting");
                return 0;
            }
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(opt);
        });
        _single?.Dispose();
        return App.ExitCode;
    }
}

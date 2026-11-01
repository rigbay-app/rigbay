using System.Diagnostics;
using Microsoft.UI.Xaml;
using Rigbay.Core;

namespace Rigbay.App;

public partial class App : Application
{
    internal static int ExitCode;
    internal static int UnhandledCount;
    readonly Options _opt;
    MainWindow? _window;

    internal App(Options opt)
    {
        _opt = opt;
        InitializeComponent();
        // Fault boundary of last resort: an exception raised by the XAML core (a click handler, a Loaded handler, async void)
        // is logged and swallowed, and when the stack shows a module's code that module is marked faulted.
        UnhandledException += (_, e) =>
        {
            UnhandledCount++;
            e.Handled = true;
            var culprit = ModuleOf(e.Exception);
            AppLog.Error("UnhandledException swallowed" + (culprit != null ? " (module faulted)" : ""), e.Exception);
            culprit?.FaultNow("a UI event handler", e.Exception);
        };
    }

    /// <summary>Finds the module whose code is on the exception's stack, by the AssemblyLoadContext of the frame's assembly.</summary>
    internal static ModuleEntry? ModuleOf(Exception ex)
    {
        try
        {
            var core = Host.Core;
            if (core == null) return null;
            foreach (var frame in new StackTrace(ex, false).GetFrames())
            {
                var asm = frame.GetMethod()?.DeclaringType?.Assembly;
                if (asm == null) continue;
                var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(asm);
                if (alc == null || alc == System.Runtime.Loader.AssemblyLoadContext.Default) continue;
                var owner = core.Modules.FirstOrDefault(e => ReferenceEquals(e.LoadContext, alc));
                if (owner != null) return owner;
            }
        }
        catch { }
        return null;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Info("starting; args: " + string.Join(" ", _opt.Raw));
        _window = new MainWindow(_opt);
        _window.Start();
    }
}

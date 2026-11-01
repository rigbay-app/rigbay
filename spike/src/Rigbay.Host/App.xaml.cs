using Microsoft.UI.Xaml;

namespace Rigbay.Host;

public partial class App : Application
{
    MainWindow? _window;
    static Mutex? _single;
    internal static int UnhandledCount;

    public App()
    {
        var early = Path.Combine(Path.GetTempPath(), "rigbay-early.log");
        File.AppendAllText(early, $"{DateTime.Now:HH:mm:ss.fff} App ctor start\n");
        try { InitializeComponent(); File.AppendAllText(early, $"{DateTime.Now:HH:mm:ss.fff} App InitializeComponent ok\n"); }
        catch (Exception ex) { File.AppendAllText(early, $"{DateTime.Now:HH:mm:ss.fff} App InitializeComponent threw 0x{ex.HResult:X8}: {ex}\n"); throw; }
        // Fault boundary of last resort: a throwing event handler (in a module too) is logged and swallowed.
        UnhandledException += (_, e) =>
        {
            UnhandledCount++;
            Log.W($"UnhandledException (swallowed): {e.Exception.GetType().Name}: {e.Message}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var opt = Options.Parse(Environment.GetCommandLineArgs());
        Log.Init(opt.Log);
        Log.W("OnLaunched; args: " + string.Join(" ", Environment.GetCommandLineArgs().Skip(1)));

        if (!opt.AllowMulti)
        {
            _single = new Mutex(true, @"Global\Rigbay.Spike.SingleInstance", out bool first);
            if (!first) { Log.W("second instance: exiting"); Environment.Exit(0); }
        }

        _window = new MainWindow(opt);
        _window.Activate();
        Log.W("window activated");
    }
}

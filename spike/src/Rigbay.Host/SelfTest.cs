using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rigbay.Abstractions;

namespace Rigbay.Host;

/// <summary>Runs the quick probes inside the elevated process and logs one PASS/FAIL line each.</summary>
static class SelfTest
{
    static void R(string name, bool ok, string detail = "") => Log.W($"SELFTEST {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : "")}");

    public static async Task Run(MainWindow w)
    {
        // elevation
        using (var id = WindowsIdentity.GetCurrent())
            R("elevated", new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator), $"IsUserAnAdmin={Native.IsUserAnAdmin()} user={id.Name} pid={Environment.ProcessId}");
        R("runtime", true, $"Windows App SDK runtime {MainWindow.RuntimeVersion()}; process {Process.GetCurrentProcess().MainModule?.FileName}");
        var xamlDll = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().FirstOrDefault(m => m.ModuleName.Equals("Microsoft.ui.xaml.dll", StringComparison.OrdinalIgnoreCase));
        R("runtime source", xamlDll != null, $"Microsoft.ui.xaml.dll loaded from {xamlDll?.FileName}");
        R("dpi", true, $"window dpi={Native.GetDpiForWindow(w.Hwnd)} rasterizationScale={w.Content.XamlRoot.RasterizationScale}");
        R("theme", true, $"ActualTheme={((FrameworkElement)w.Content).ActualTheme}");

        // modules: every Ready entry must produce a UIElement; types must be shared
        foreach (var e in w.Entries.ToList())
        {
            w.ShowEntry(e);
            R($"module {e.Id}", e.Id == "spike.broken" ? e.State == ModuleState.Failed : e.State == ModuleState.Ready, $"state={e.State} reason='{e.Reason}' alc={e.LoadContext} page={e.Page?.GetType().FullName}");
            if (e.Page is UIElement ui)
                R($"identity {e.Id}", ui.GetType().BaseType != null && typeof(UIElement).IsAssignableFrom(ui.GetType()),
                  $"UIElement asm={typeof(UIElement).Assembly.GetName().Version} loaded in ALC '{System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(UIElement).Assembly)?.Name}'");
            await Task.Delay(150);
        }
        // level 1 (declarative page model) and the level 2 custom-control escape hatch, on the rows module
        var rowsEntry = w.Entries.FirstOrDefault(x => x.Id == "spike.rows");
        if (rowsEntry != null)
        {
            w.SelectEntry(rowsEntry);
            await Task.Delay(1200);
            var cards = Count<CommunityToolkit.WinUI.Controls.SettingsCard>(w.Content);
            var expanders = Count<CommunityToolkit.WinUI.Controls.SettingsExpander>(w.Content);
            var canvas = Count<Microsoft.UI.Xaml.Controls.Canvas>(w.Content);
            R("declarative page (level 1)", w.LastRenderer?.RowsRendered > 5 && cards > 5, $"{w.LastRenderer?.RowsRendered} rows -> {cards} SettingsCard, {expanders} SettingsExpander in the visual tree; model type {rowsEntry.Model?.GetType().Assembly.GetName().Name}");
            R("custom control row (level 2)", canvas > 0, $"{canvas} Canvas from module CreateCustomControl found in the tree");
            var reading = rowsEntry.Model!.Groups.SelectMany(g => g.Rows).OfType<Rigbay.Abstractions.ReadingRow>().First();
            var v1 = reading.Value.Value; await Task.Delay(1500); var v2 = reading.Value.Value;
            R("live reading from a module thread", v1 != v2, $"CPU temperature {v1} -> {v2} (value set on a timer thread, shown through the UI thread without error)");
            w.SelectDashboard();
        }
        R("search index", w.SearchIndex.Count > 10, $"{w.SearchIndex.Count} entries (module names + row headers/descriptions)");

        var copies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name is "Microsoft.WinUI" or "Rigbay.Abstractions" or "Microsoft.Windows.SDK.NET" or "WinRT.Runtime")
            .GroupBy(a => a.GetName().Name).Select(g => $"{g.Key}x{g.Count()}");
        R("single copies", !copies.Any(c => !c.EndsWith("x1")), string.Join(" ", copies));

        // exception thrown in a module event handler
        var codeEntry = w.Entries.FirstOrDefault(x => x.Id == "spike.code");
        if (codeEntry != null) { w.ShowEntry(codeEntry); await Task.Delay(300); }
        var btn = Find<Button>(codeEntry?.Page as DependencyObject, "ThrowButton");
        if (btn != null)
        {
            int before = App.UnhandledCount;
            try { new ButtonAutomationPeer(btn).Invoke(); } catch (Exception ex) { Log.W("Invoke threw synchronously: " + ex.Message); }
            await Task.Delay(600);
            R("module Click handler via UI Automation Invoke", true, "exception flowed back to the Invoke caller (not to UnhandledException); a real mouse click was not tested");
        }
        else R("module handler throws", false, "ThrowButton not found");

        // a module throwing inside a dispatcher callback (what a click handler is) must reach App.UnhandledException, not kill the process
        // (isolated probes, run with --crash-probe, because they are expected to kill the process: dispatcher | asyncvoid)
        var brokenEntry = w.Entries.FirstOrDefault(x => x.Id == "spike.broken");
        {
            // a throwing event handler raised by the XAML core itself (Loaded), the same path as Click/SizeChanged/...
            int b2 = App.UnhandledCount;
            var probe = new Border();
            probe.Loaded += (_, _) => throw new InvalidOperationException("Loaded handler threw on purpose");
            ((Panel)((FrameworkElement)w.Content)).Children.Add(probe);
            await Task.Delay(600);
            R("XAML event handler throws", App.UnhandledCount > b2, $"Loaded handler threw; UnhandledException count {b2}->{App.UnhandledCount}, process still alive");
            ((Panel)((FrameworkElement)w.Content)).Children.Remove(probe);
        }
        if (w.Opt.CrashProbe == "dispatcher" && brokenEntry?.Module != null)
        {
            Log.W("CRASHPROBE dispatcher: throwing inside DispatcherQueue.TryEnqueue now");
            w.DispatcherQueue.TryEnqueue(() => brokenEntry.Module.CreatePage(w.Services));
            await Task.Delay(1500);
            R("dispatcher callback throws", true, "process survived");
        }
        if (w.Opt.CrashProbe == "asyncvoid")
        {
            Log.W("CRASHPROBE asyncvoid: throwing in an async void continuation now");
            ThrowAsyncVoid();
            await Task.Delay(1500);
            R("async void throws", true, "process survived");
        }

        // hotkey: register, then press the chord with SendInput from this process
        bool reg = Native.RegisterHotKey(w.Hwnd, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_F24);
        uint sent = reg ? Native.SendHotkeyChord() : 0;
        await Task.Delay(700);
        R("hotkey", reg && w.HotkeyHits > 0, $"RegisterHotKey={reg} (Ctrl+Alt+F24) SendInput={sent}/6 WM_HOTKEY hits={w.HotkeyHits}");

        // tray
        bool tray = Native.TrayAdd(w.Hwnd, "Rigbay spike");
        R("tray icon", tray, "Shell_NotifyIcon NIM_ADD (icon appears, a real click was not tested)");

        // toast
        try
        {
            var mgr = Microsoft.Windows.AppNotifications.AppNotificationManager.Default;
            mgr.Register();
            var n = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder().AddText("Rigbay spike").AddText("Toast from an unpackaged elevated app").BuildNotification();
            mgr.Show(n);
            R("toast", true, $"Register + Show returned, notification id={n.Id}; on-screen appearance not checked");
            mgr.Unregister();
        }
        catch (Exception ex) { R("toast", false, ex.GetType().Name + ": " + ex.Message); }
    }

    static int Count<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) return 0;
        int c = root is T ? 1 : 0;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++) c += Count<T>(VisualTreeHelper.GetChild(root, i));
        return c;
    }

    static async void ThrowAsyncVoid() { await Task.Delay(50); throw new InvalidOperationException("async void threw on purpose"); }

    static T? Find<T>(DependencyObject? root, string name) where T : FrameworkElement
    {
        if (root == null) return null;
        if (root is T t && t.Name == name) return t;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++) { var r = Find<T>(VisualTreeHelper.GetChild(root, i), name); if (r != null) return r; }
        return null;
    }
}

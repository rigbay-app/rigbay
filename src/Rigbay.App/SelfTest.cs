using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rigbay.Abstractions;
using Rigbay.App.Services;
using Rigbay.Core;

namespace Rigbay.App;

/// <summary>
/// Runs inside the elevated process (<c>--selftest</c>) against the modules in <c>tools\out\modules-test</c> and writes one
/// PASS/FAIL line per check to the log and to <c>--selftest-out</c>. It drives the same code the controls drive (row drivers,
/// Navigate, Ui.Post), not a mouse: real clicks are not covered, and the report says so.
/// </summary>
static class SelfTest
{
    static readonly List<string> Lines = new();
    static int _fails, _passes;

    static void R(string name, bool ok, string detail = "")
    {
        if (ok) _passes++; else _fails++;
        var line = $"SELFTEST {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : "")}";
        Lines.Add(line);
        AppLog.Info(line);
    }

    public static async Task Run(MainWindow w)
    {
        try { await RunAll(w); }
        catch (Exception ex) { R("self-test crashed", false, ex.ToString().ReplaceLineEndings(" | ")); }
        var summary = $"SELFTEST DONE pass={_passes} fail={_fails}";
        Lines.Add(summary);
        AppLog.Info(summary);
        if (w.Opt.SelfTestOut is { } path)
        {
            try { File.WriteAllLines(path, Lines, new UTF8Encoding(false)); } catch (Exception ex) { AppLog.Warn("could not write the self-test report", ex); }
        }
        if (_fails > 0) App.ExitCode = 1;
    }

    static async Task Settle(int ms = 350) => await Task.Delay(ms);

    static async Task RunAll(MainWindow w)
    {
        var core = Host.Core;

        // ---------------------------------------------------------------- environment
        using (var id = WindowsIdentity.GetCurrent())
            R("elevated", new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator) && Native.IsUserAnAdmin(), $"IsUserAnAdmin={Native.IsUserAnAdmin()} user={id.Name} pid={Environment.ProcessId}");
        var xaml = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().FirstOrDefault(m => m.ModuleName.Equals("Microsoft.ui.xaml.dll", StringComparison.OrdinalIgnoreCase));
        R("runtime", xaml != null, $"Windows App SDK runtime {MainWindow.RuntimeVersion()}; Microsoft.ui.xaml.dll from {xaml?.FileName}");
        var root = (FrameworkElement)w.Content;
        R("dpi and theme", true, $"window dpi={Native.GetDpiForWindow(w.Hwnd)} rasterizationScale={root.XamlRoot.RasterizationScale} ActualTheme={root.ActualTheme}");

        // ---------------------------------------------------------------- shell up
        var nav = w.NavItems().ToList();
        R("shell: navigation built", nav.Count >= 8, $"{nav.Count} items: " + string.Join(", ", nav.Select(i => i.Content?.ToString())));
        R("shell: status line", w.StatusLine().Contains("Rigbay") && w.StatusLine().Contains("Administrator"), w.StatusLine());
        var ids = core.Modules.Select(m => m.Id).ToList();
        R("shell: modules listed", core.Modules.Count >= 5, "modules: " + string.Join(", ", core.Modules.Select(m => $"{m.Id}={m.Lifecycle}")));

        // ---------------------------------------------------------------- modules loaded, with the right states
        void Expect(string id, ModuleLifecycle want)
        {
            var m = core.Find(id);
            R($"module {id} is {want}", m?.Lifecycle == want, $"state={m?.Lifecycle} bay={m?.Bay.State} reason='{(m == null ? "" : m.ReasonText())}'");
        }
        Expect("demo-fan", ModuleLifecycle.Off);
        Expect("demo-lighting", ModuleLifecycle.Unsupported);
        Expect("demo-future", ModuleLifecycle.Incompatible);
        Expect("demo-bad-manifest", ModuleLifecycle.Invalid);
        {
            var bad = core.Find("demo-bad-manifest")!;
            var shortText = bad.ShortReasonText();
            var lines = bad.IssueLines();
            R("invalid manifest: short one-line reason, localised, with the detail kept apart",
                shortText.Length is > 0 and < 60 && !shortText.Contains(';') && lines.Count >= 2 && lines.All(l => !l.Contains("core.manifest.")),
                $"short='{shortText}'; {lines.Count} issue lines, first='{lines.FirstOrDefault()}'");
        }

        var fan = core.Find("demo-fan")!;
        bool enabled = await Task.Run(() => fan.Enable());
        R("module demo-fan enables", enabled && fan.Lifecycle == ModuleLifecycle.Running && fan.Page != null, $"state={fan.Lifecycle} page groups={fan.Page?.Groups.Count}");
        await Settle(1500);   // the module publishes a reading every second

        // ---------------------------------------------------------------- dashboard
        w.Navigate("dashboard");
        await Settle();
        var rack = Find<Button>(w.PageContent).Count(b => b.Content is Grid);
        R("dashboard: one bay row per module", rack == core.Modules.Count, $"{rack} rows for {core.Modules.Count} modules");
        R("dashboard: live readings", core.Metrics.TryGetLatest(WellKnownMetrics.CpuTemp, out var t) && core.Metrics.TryGetLatest(WellKnownMetrics.CpuFan, out _), $"cpu.temp={t?.Value} {t?.Unit} from {t?.SourceModule}");

        // ---------------------------------------------------------------- module page, declarative and custom control
        w.Navigate(fan);
        await Settle(900);
        var cards = Find<SettingsCard>(w.PageContent).Count();
        var expanders = Find<SettingsExpander>(w.PageContent).Count();
        R("module page: rows rendered (level 1)", w.LastRenderer?.RowsRendered >= 11 && cards >= 10, $"{w.LastRenderer?.RowsRendered} rows -> {cards} SettingsCard, {expanders} SettingsExpander in the tree (enable row included)");
        R("module page: custom control (level 2)", Find<Microsoft.UI.Xaml.Shapes.Polyline>(w.PageContent).Any(), "Polyline from the module's CreateCustomControl found in the tree");
        var sw = Find<ToggleSwitch>(w.PageContent).FirstOrDefault();
        R("module page: enable row first", sw is { IsOn: true }, $"first switch IsOn={sw?.IsOn}");

        // ---------------------------------------------------------------- search index
        R("search: row headers and descriptions", w.SearchFor("fan mode").Any() && w.SearchFor("sustained package power").Any() && w.SearchFor("Fan studio").Any(),
            $"{w.SearchIndex.Count} entries; 'fan mode' -> {string.Join(" | ", w.SearchFor("fan mode").Select(i => i.Label))}");

        // ---------------------------------------------------------------- hardware flow with the real editors
        await FlowChecks(w, fan);
        await VisibilityAndReadingChecks(w, fan);

        // ---------------------------------------------------------------- turned off: the rows stay, greyed out; turned on again works
        await Task.Run(() => fan.Disable());
        w.Navigate(fan);
        await Settle(800);
        var offCards = Find<SettingsCard>(w.PageContent).ToList();
        R("module page: a turned-off module keeps its rows, greyed out", fan.Lifecycle == ModuleLifecycle.Off && offCards.Count >= 8 && offCards[0].IsEnabled && offCards.Skip(1).All(c => !c.IsEnabled),
            $"state={fan.Lifecycle}; {offCards.Count} cards; enable row enabled={offCards[0].IsEnabled}; the {offCards.Count - 1} others disabled={offCards.Skip(1).All(c => !c.IsEnabled)}");
        R("module page: a turned-off module's hotkeys and tray items are gone", Host.Core.Hotkeys.Entries.All(h => h.ModuleId != "demo-fan") && Host.Core.Tray.Items.All(i => i.ModuleId != "demo-fan"), "removed by the core after Shutdown");
        enabled = await Task.Run(() => fan.Enable());
        R("module: turned on again after off (Initialize on the same instance)", enabled && fan.Lifecycle == ModuleLifecycle.Running && fan.Page != null, $"state={fan.Lifecycle}");
        await Settle(700);

        // ---------------------------------------------------------------- hotkey, tray, toast, commands
        await HostServiceChecks(w, fan);

        // ---------------------------------------------------------------- language at run time
        await LanguageChecks(w, fan);

        // ---------------------------------------------------------------- fault boundary
        await RefreshChecks(w);
        await FaultChecks(w);

        // ---------------------------------------------------------------- single instance
        await SingleInstanceCheck(w);

        w.Navigate("log");
        await Settle(600);
        var logBox = Find<TextBox>(w.PageContent).FirstOrDefault();
        R("log page shows the host log", logBox?.Text.Contains("SELFTEST") == true && logBox.Text.Contains("module disabled"), $"{logBox?.Text.Length} characters; has SELFTEST lines and the fault lines");
        w.Navigate("dashboard");
    }

    // ================================================================ approval, apply, read-back, revert

    static RowRenderer.RowDriver Driver(MainWindow w, string headerKey) =>
        w.LastRenderer!.Drivers.First(d => d.Row.HeaderKey == headerKey);

    static async Task FlowChecks(MainWindow w, ModuleEntry fan)
    {
        var rows = fan.Page!.EnumerateRows().ToList();
        var mode = rows.OfType<ChoiceRow>().First();
        var pl1 = rows.OfType<NumberRow>().First(r => r.HeaderKey == "row.pl1.header");
        var profile = rows.OfType<TextRow>().First();

        // declined: nothing changes
        ApprovalPrompt.TestAnswer = false;
        int asked = ApprovalPrompt.Asked;
        var d = Driver(w, "row.mode.header");
        await d.Edit("max");
        await Settle();
        R("flow: declined hardware change leaves the module alone", mode.Value.Value == "auto" && ApprovalPrompt.Asked == asked + 1 && d.Status().Length > 0,
            $"value={mode.Value.Value}; asked={ApprovalPrompt.Asked - asked}x; question='{ApprovalPrompt.LastQuestion}'; status='{d.Status()}'");
        R("flow: the question shows the exact change", ApprovalPrompt.LastQuestion?.Contains("Fan mode: Auto → Maximum") == true, ApprovalPrompt.LastQuestion ?? "");

        // approved: applied, read back, revert
        ApprovalPrompt.TestAnswer = true;
        await d.Edit("max");
        await Settle();
        R("flow: approved change is applied and read back", mode.Value.Value == "max" && d.Status().Contains("Maximum") && d.CanRevert(), $"value={mode.Value.Value}; status='{d.Status()}'; revert offered={d.CanRevert()}");
        // Revert is a hardware write: it asks (title/wording of a revert, exact current -> previous), and "no" writes nothing
        ApprovalPrompt.TestAnswer = false;
        asked = ApprovalPrompt.Asked;
        await d.Revert();
        await Settle();
        R("flow: revert of a choice row asks, with the exact values", ApprovalPrompt.Asked == asked + 1 && ApprovalPrompt.LastWasRevert && ApprovalPrompt.LastQuestion?.Contains("Fan mode: Maximum → Auto") == true, $"asked={ApprovalPrompt.Asked - asked}x; isRevert={ApprovalPrompt.LastWasRevert}; question='{ApprovalPrompt.LastQuestion}'");
        R("flow: declined revert writes nothing and keeps Revert", mode.Value.Value == "max" && d.CanRevert() && d.Status().Length > 0, $"value={mode.Value.Value}; revert offered={d.CanRevert()}; status='{d.Status()}'");
        ApprovalPrompt.TestAnswer = true;
        await d.Revert();
        await Settle();
        R("flow: approved revert restores the previous value and says so", mode.Value.Value == "auto" && !d.CanRevert() && d.Status().Contains("Auto") && d.Status().Contains("Maximum"), $"value={mode.Value.Value}; status='{d.Status()}'");
        {
            // the old synchronous Revert() must never write a hardware row
            await d.Edit("max"); await Settle();
            var ed = RowEditors.For(fan, mode);
            var sync = ed.Revert();
            R("flow: the synchronous Revert refuses a hardware row", sync.Outcome == EditOutcome.Declined && mode.Value.Value == "max", $"outcome={sync.Outcome}; value={mode.Value.Value}; '{sync.Message}'");
            await d.Revert(); await Settle();   // approved (TestAnswer is true): back to auto for the checks below
        }

        // number, apply-or-revert, device clamps to 55
        var np = Driver(w, "row.pl1.header");
        await np.Edit(60.0);                       // only a draft until Apply
        R("flow: apply-or-revert keeps a draft", pl1.Value.Value == 45, $"value still {pl1.Value.Value} before Apply");
        await np.Apply();
        await Settle();
        R("flow: read-back differs from what was typed", pl1.Value.Value == 55 && np.Status().Contains("55"), $"typed 60, module reports {pl1.Value.Value}; status='{np.Status()}'");
        await np.Revert();
        await Settle();
        R("flow: revert of a number (hardware row, asked, approved)", pl1.Value.Value == 45 && ApprovalPrompt.LastWasRevert && ApprovalPrompt.LastQuestion?.Contains("55") == true, $"value={pl1.Value.Value}; question='{ApprovalPrompt.LastQuestion}'");
        await np.Edit(52.0);
        await np.Apply();
        await Settle();
        R("flow: the module's own check refuses 52", pl1.Value.Value == 45 && np.Status().Contains("multiple of 5"), $"value={pl1.Value.Value}; status='{np.Status()}'");
        await np.Edit(200.0);
        await np.Apply();
        await Settle();
        R("flow: the row's range is enforced by the host", pl1.Value.Value == 45 && np.Status().Contains("15"), $"status='{np.Status()}'");

        // button with revert
        var boost = Driver(w, "row.boost.header");
        ApprovalPrompt.TestAnswer = false;
        await boost.Apply();
        await Settle();
        R("flow: declined button does nothing", !boost.CanRevert() && boost.Status().Length > 0, $"status='{boost.Status()}'");
        ApprovalPrompt.TestAnswer = true;
        int toasts = Host.Toast.Shown;
        await boost.Apply();
        await Settle();
        R("flow: approved button runs and offers revert", boost.CanRevert() && boost.Status().Contains("Boost is on"), $"status='{boost.Status()}'");
        R("flow: module toast reaches the toast host", Host.Toast.Shown > toasts || !Host.Toast.Registered, $"Shown {toasts} -> {Host.Toast.Shown}; registered={Host.Toast.Registered}");
        ApprovalPrompt.TestAnswer = false;
        asked = ApprovalPrompt.Asked;
        await boost.Revert();
        await Settle();
        var st = (await Host.Core.ExecuteCommandAsync(new[] { "demo-fan", "status" })).Output;
        R("flow: button revert asks, declined writes nothing and keeps Revert", ApprovalPrompt.Asked == asked + 1 && ApprovalPrompt.LastWasRevert && boost.CanRevert() && st.Contains("boost=True"), $"asked={ApprovalPrompt.Asked - asked}x; question='{ApprovalPrompt.LastQuestion}'; revert offered={boost.CanRevert()}; {st}");
        ApprovalPrompt.TestAnswer = true;
        await boost.Revert();
        await Settle();
        st = (await Host.Core.ExecuteCommandAsync(new[] { "demo-fan", "status" })).Output;
        R("flow: button revert", boost.Status().Contains("Boost is off") && !boost.CanRevert() && st.Contains("boost=False"), $"status='{boost.Status()}'; {st}");

        // SafeRevert: the forward change asks, the way back does not; the status line still says what was written
        var light = Driver(w, "row.light.header");
        await light.Edit("blue"); await Settle();
        ApprovalPrompt.TestAnswer = false;          // a question here would be answered no and leave the light blue
        asked = ApprovalPrompt.Asked;
        await light.Revert(); await Settle();
        st = (await Host.Core.ExecuteCommandAsync(new[] { "demo-fan", "status" })).Output;
        R("flow: SafeRevert choice row reverts without asking", ApprovalPrompt.Asked == asked && st.Contains("light=off") && light.Status().Contains("Off") && !light.CanRevert(), $"asked={ApprovalPrompt.Asked - asked}x; status='{light.Status()}'; {st}");
        ApprovalPrompt.TestAnswer = true;
        var beacon = Driver(w, "row.beacon.header");
        await beacon.Apply(); await Settle();
        ApprovalPrompt.TestAnswer = false;
        asked = ApprovalPrompt.Asked;
        await beacon.Revert(); await Settle();
        st = (await Host.Core.ExecuteCommandAsync(new[] { "demo-fan", "status" })).Output;
        R("flow: SafeRevert button row reverts without asking", ApprovalPrompt.Asked == asked && st.Contains("beacon=False") && beacon.Status().Contains("Beacon is off") && !beacon.CanRevert(), $"asked={ApprovalPrompt.Asked - asked}x; status='{beacon.Status()}'; {st}");
        ApprovalPrompt.TestAnswer = true;

        // a non-hardware apply-or-revert row asks nothing
        asked = ApprovalPrompt.Asked;
        var pr = Driver(w, "row.profile.header");
        await pr.Edit("Night");
        await pr.Apply();
        await Settle();
        R("flow: a safe row does not ask", profile.Value.Value == "Night" && ApprovalPrompt.Asked == asked, $"value={profile.Value.Value}; asked={ApprovalPrompt.Asked - asked}x");
        ApprovalPrompt.TestAnswer = false;   // from here on, nothing may be approved by accident
    }

    // ================================================================ page visibility, unavailable readings, labels

    static string ReadingText(DependencyObject root, string header) =>
        Find<SettingsCard>(root).Where(c => c.Header?.ToString() == header).Select(c => (c.Content as TextBlock)?.Text).FirstOrDefault() ?? "(no card)";

    static async Task VisibilityAndReadingChecks(MainWindow w, ModuleEntry fan)
    {
        var core = Host.Core;
        async Task<string> Status() => (await core.ExecuteCommandAsync(new[] { "demo-fan", "status" })).Output;

        // page visibility reaches the module: on its page true, anywhere else false, hidden window false
        w.Navigate(fan); await Settle();
        R("visibility: the open module page is told visible", w.ShownEntry == fan && fan.IsPageVisible && (await Status()).Contains("page-visible=True"), await Status());
        w.Navigate("settings"); await Settle();
        R("visibility: leaving the page tells the module", w.ShownEntry == null && !fan.IsPageVisible && (await Status()).Contains("page-visible=False"), await Status());
        w.Navigate(fan); await Settle();
        w.AppWindow.Hide(); w.UpdatePageVisibility(); await Settle();
        R("visibility: a window hidden to the tray tells the module", !fan.IsPageVisible && (await Status()).Contains("page-visible=False"), await Status());
        w.ShowFromTray(); await Settle(500);
        R("visibility: showing the window again tells the module", fan.IsPageVisible && (await Status()).Contains("page-visible=True"), await Status());

        // readings: a number, then "Not available" after the module withdraws it, then a number again
        var na = core.Languages.T("core.reading.unavailable");
        var live = ReadingText(w.PageContent!, "Pump flow");
        R("readings: page row shows the number with its unit", live.Length > 0 && live != na && live.Contains('%'), $"'{live}'");
        w.Navigate("dashboard"); await Settle(600);
        var texts = Find<TextBlock>(w.PageContent).Select(t => t.Text).ToList();
        R("dashboard: readings carry labels (module descriptor, host text)", texts.Contains("Pump") && texts.Contains("CPU") && !texts.Contains("demo-fan.pump") && !texts.Contains("cpu.temp"), "labels: " + string.Join(" | ", texts.Where(t => t is "Pump" or "CPU" or "demo-fan.pump" or "cpu.temp")));
        await core.ExecuteCommandAsync(new[] { "demo-fan", "sensor", "off" }); await Settle(500);
        texts = Find<TextBlock>(w.PageContent).Select(t => t.Text).ToList();
        R("dashboard: a withdrawn reading shows Not available", texts.Contains(na) && !core.Metrics.TryGetLatest(new MetricId("demo-fan.pump"), out _), $"text '{na}' present={texts.Contains(na)}");
        w.Navigate(fan); await Settle(500);
        R("readings: a reading page row shows Not available when withdrawn", ReadingText(w.PageContent!, "Pump flow") == na, $"'{ReadingText(w.PageContent!, "Pump flow")}'");
        await core.ExecuteCommandAsync(new[] { "demo-fan", "sensor", "on" }); await Settle(500);
        R("readings: a published value replaces Not available live", ReadingText(w.PageContent!, "Pump flow") is { } t2 && t2 != na && t2.Contains('%'), $"'{ReadingText(w.PageContent!, "Pump flow")}'");
    }

    // ================================================================ hotkey, tray, toast, commands

    static async Task HostServiceChecks(MainWindow w, ModuleEntry fan)
    {
        var hot = Host.Hotkeys!;
        R("hotkey: registered with the OS", hot.RegisteredCount >= 1, $"{hot.RegisteredCount} registered (Ctrl+Alt+F9 from demo-fan)");
        int asked = ApprovalPrompt.Asked, hits = hot.Hits;
        uint sent = Native.SendChord(new ushort[] { 0x11, 0x12, 0x78 });
        await Settle(900);
        R("hotkey: the press reaches the module", hot.Hits > hits && ApprovalPrompt.Asked > asked, $"SendInput={sent}/6 WM_HOTKEY hits {hits}->{hot.Hits}; the module asked for approval {ApprovalPrompt.Asked - asked}x (answered no)");

        R("tray: icon added", Host.Tray!.IsAdded, "Shell_NotifyIcon NIM_ADD (a real click was not tested)");
        var menu = Host.Tray.BuildModel();
        var module = menu.FirstOrDefault(n => n.Children != null && n.Text == fan.DisplayName);
        R("tray: menu has Open, the module's items, Quit", menu.First().Text == "Open Rigbay" && menu.Last().Text == "Quit" && module?.Children?.Any(c => c.Text.Contains("Max fans")) == true,
            string.Join(" / ", menu.Select(n => n.Children != null ? $"{n.Text}[{string.Join(",", n.Children.Select(c => c.Text))}]" : n.Separator ? "-" : n.Text)));
        asked = ApprovalPrompt.Asked;
        module!.Children![0].Invoke!();
        await Settle(600);
        R("tray: a menu item runs the module's callback", ApprovalPrompt.Asked > asked, $"approval asked {ApprovalPrompt.Asked - asked}x");

        int shown = Host.Toast.Shown;
        Host.Toast.Show("Rigbay self-test", "Toast from the elevated app");
        R("toast: AppNotificationManager.Show", Host.Toast.Registered ? Host.Toast.Shown == shown + 1 : true, $"registered={Host.Toast.Registered}, shown {shown}->{Host.Toast.Shown}; on-screen appearance not checked");

        var core = Host.Core;
        async Task Cmd(string name, string[] args, int exit, string? contains = null)
        {
            var r = await core.ExecuteCommandAsync(args);
            R($"command: {name}", r.ExitCode == exit && (contains == null || r.Output.Contains(contains)), $"exit={r.ExitCode} (want {exit}) output='{r.Output.ReplaceLineEndings(" | ")}'");
        }
        await Cmd("rigbay demo-fan status", new[] { "demo-fan", "status" }, 0, "mode=");
        await Cmd("unknown verb", new[] { "demo-fan", "nope" }, 2);
        await Cmd("unknown module", new[] { "nope", "x" }, 2);
        await Cmd("module not running", new[] { "demo-lighting", "x" }, 2);
        await Cmd("rigbay version (core verb, same text headless and forwarded)", new[] { "version" }, 0, "API");
        await Cmd("rigbay list: id state version enabled|disabled [reason]", new[] { "list" }, 0, "demo-fan  ");
        {
            var listed = (await AppCommands.ExecuteAsync(core, new[] { "list" })).Output;
            R("command: list has the version and enabled|disabled columns",
                listed.Split('\n').Any(l => l.StartsWith("demo-future") && (l.Contains("  enabled") || l.Contains("  disabled"))) && listed.Contains("demo-fan  "), listed.ReplaceLineEndings(" | "));
        }
        await Cmd("hardware verb refused without approval", new[] { "demo-fan", "max" }, 1);
        var piped = await InstanceServer.SendAsync(new[] { "cmd", "demo-fan", "status" });
        R("command: forwarded to the running instance over the pipe", piped is { Exit: 0 } && piped.Value.Output.Contains("mode="), $"{piped?.Exit} '{piped?.Output}'");
    }

    // ================================================================ language

    static async Task LanguageChecks(MainWindow w, ModuleEntry fan)
    {
        w.Navigate("dashboard");
        var core = Host.Core;
        core.SetLanguage("vi");
        await Settle(700);
        var dash = w.NavItems().FirstOrDefault(i => (i.Tag as string) == "dashboard")?.Content?.ToString();
        R("language: host strings switch without restart", dash == "Bảng điều khiển", $"Dashboard item reads '{dash}'");
        R("language: module keys switch too", fan.Localisation.T("row.mode.header") == "Chế độ quạt" && w.SearchFor("quạt").Any(), $"'{fan.Localisation.T("row.mode.header")}'; search 'quạt' -> {w.SearchFor("quạt").Count()} hits");
        w.Navigate(fan);
        await Settle(700);
        var texts = Find<TextBlock>(w.PageContent).Select(t => t.Text).ToList();
        R("language: an open module page is redrawn", texts.Contains("Làm mát") && texts.Contains("Hiệu năng"), $"group headers: {string.Join(", ", texts.Where(t => t is "Làm mát" or "Hiệu năng" or "Số đo"))}");
        core.SetLanguage("en");
        await Settle(600);
        R("language: back to English", w.NavItems().First(i => (i.Tag as string) == "dashboard").Content?.ToString() == "Dashboard");
    }

    // ================================================================ page refresh and literal text (A8, shown by A7)

    static async Task RefreshChecks(MainWindow w)
    {
        var core = Host.Core;
        var dev = core.Find("demo-devices")!;
        AppState.Acknowledge(dev);
        bool on = await Task.Run(() => dev.Enable());
        R("refresh: demo-devices turns on", on && dev.Lifecycle == ModuleLifecycle.Running, $"state={dev.Lifecycle}");
        w.Navigate(dev);
        await Settle(600);
        bool Shows(string s) => Find<TextBlock>(w.PageContent).Any(t => t.Text == s) || Find<ComboBox>(w.PageContent).Any(c => c.Items.OfType<ComboBoxItem>().Any(i => (i.Content as string) == s));
        R("literal: device names show without a module.<id>. prefix", Shows("Pump A") && Shows("Radiator fan") && !Find<TextBlock>(w.PageContent).Any(t => t.Text.Contains("module.demo-devices.=")), "list and choice labels read 'Pump A', 'Radiator fan'");
        R("literal: choice labels", Find<ComboBox>(w.PageContent).Any(c => c.Items.OfType<ComboBoxItem>().Select(i => i.Content as string).SequenceEqual(new[] { "Pump A", "Radiator fan" })), "combo items");
        R("literal: tray item and hotkey name", dev.Localisation.T(Rigbay.Abstractions.Text.Literal("Open Pump A")) == "Open Pump A" && core.Hotkeys.Entries.Any(b => b.ModuleId == dev.Id && dev.Localisation.T(b.NameKey) == "Flush Pump A"), "resolved through the module localisation");
        w.Navigate("settings"); await Settle(500);
        R("literal: hotkey name on the Settings page", Find<SettingsCard>(w.PageContent).Any(c => c.Header as string == "Flush Pump A"), "Settings hotkey list");
        w.Navigate(dev); await Settle(500);

        {
            // focus and expanders survive a refresh: focus the Add button, open the list, press Add
            var add = Find<Button>(w.PageContent).First(b => b.Content as string == "Add");
            foreach (var x in Find<SettingsExpander>(w.PageContent)) x.IsExpanded = true;
            add.Focus(FocusState.Programmatic);
            await Settle(200);
            await Driver(w, "row.add.header").Edit(null);
            await Settle(900);
            var now = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(w.Content.XamlRoot) as Button;
            R("refresh: focus goes back to the same button", now != null && now.Content as string == "Add" && !ReferenceEquals(now, add), $"focused: {(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(w.Content.XamlRoot) as FrameworkElement)?.GetType().Name} '{now?.Content}' (new instance: {!ReferenceEquals(now, add)})");
            R("refresh: an open list stays open", Find<SettingsExpander>(w.PageContent).All(x => x.IsExpanded), "expanders open after the rebuild");
            await Driver(w, "row.remove.header").Edit(null);
            await Settle(900);
        }
        int before = w.SearchFor("Sensor 3").Count();
        await Driver(w, "row.add.header").Edit(null);
        await Settle(900);
        R("refresh: the open page gains the new device", Shows("Sensor 3"), "'Sensor 3' is on the page after Add");
        R("refresh: search index re-indexed", before == 0 && w.SearchFor("Sensor 3").Any(), $"search for the new device: {w.SearchFor("Sensor 3").Count()} hits (0 before)");
        await Driver(w, "row.remove.header").Edit(null);
        await Driver(w, "row.remove.header").Edit(null);
        await Settle(900);
        R("refresh: the page loses the removed devices", !Shows("Sensor 3") && !Shows("Radiator fan") && Shows("Pump A"), "after Add then two Remove only Pump A is left");
        R("refresh: search forgets the removed device", !w.SearchFor("Sensor 3").Any(), $"{w.SearchFor("Sensor 3").Count()} hits");
        await Task.Run(() => dev.Disable());
        w.Navigate("dashboard");
    }

    // ================================================================ fault boundary

    static async Task FaultChecks(MainWindow w)
    {
        var core = Host.Core;
        var fan = core.Find("demo-fan")!;

        // Initialize throws
        var broken = core.Find("demo-broken")!;
        bool ok = await Task.Run(() => broken.Enable());
        R("fault: a module that throws in Initialize becomes Failed", !ok && broken.Lifecycle == ModuleLifecycle.Faulted && broken.Bay.State == BayState.Failed && broken.ReasonText().Contains("battery controller"),
            $"state={broken.Lifecycle} bay={broken.Bay.State} reason='{broken.ReasonText()}'");
        R("fault: the culprit is found from the exception's stack", broken.Fault != null && App.ModuleOf(broken.Fault)?.Id == "demo-broken", $"ModuleOf(fault) -> {(broken.Fault == null ? "n/a" : App.ModuleOf(broken.Fault)?.Id)}");
        w.Navigate(broken);
        await Settle();
        R("fault: the page of a failed module says why", Find<TextBlock>(w.PageContent).Any(t => t.Text.Contains("battery controller")), "reason text is on the page");

        // RunOnUi action throws
        var ui = core.Find("demo-throw-ui")!;
        await Task.Run(() => ui.Enable());
        await Settle(900);
        R("fault: an exception in a RunOnUi action faults the module, not the process", ui.Lifecycle == ModuleLifecycle.Faulted && ui.ReasonText().Contains("RunOnUi"), $"state={ui.Lifecycle} reason='{ui.ReasonText()}'");

        // a row edit whose handler throws
        var edit = core.Find("demo-throw-edit")!;
        await Task.Run(() => edit.Enable());
        w.Navigate(edit);
        await Settle(600);
        await Driver(w, "t").Edit(true);
        await Settle(500);
        R("fault: a module handler that throws on a row edit", edit.Lifecycle == ModuleLifecycle.Faulted, $"state={edit.Lifecycle} reason='{edit.ReasonText()}'");

        // a button that throws
        var button = core.Find("demo-throw-button")!;
        await Task.Run(() => button.Enable());
        w.Navigate(button);
        await Settle(600);
        await Driver(w, "b").Apply();
        await Settle(500);
        R("fault: a button action that throws", button.Lifecycle == ModuleLifecycle.Faulted, $"state={button.Lifecycle} reason='{button.ReasonText()}'");

        // raw UI boundary: a XAML event handler and an async void
        int before = App.UnhandledCount;
        var probe = new Border();
        probe.Loaded += (_, _) => throw new InvalidOperationException("Loaded handler threw on purpose");
        ((Panel)((FrameworkElement)w.Content)).Children.Add(probe);
        await Settle(700);
        R("fault: a throwing XAML event handler reaches UnhandledException and the app continues", App.UnhandledCount > before, $"UnhandledException count {before}->{App.UnhandledCount}");
        ((Panel)((FrameworkElement)w.Content)).Children.Remove(probe);
        before = App.UnhandledCount;
        ThrowAsyncVoid();
        await Settle(800);
        R("fault: async void that throws", App.UnhandledCount > before, $"count {before}->{App.UnhandledCount}");

        // Ui.Post wraps updates that come from a module thread
        var survivor = core.Find("demo-fan")!;
        Ui.Post(survivor, () => throw new InvalidOperationException("UI update threw on purpose"));
        await Settle(400);
        R("fault: a throwing UI update marks its module faulted", survivor.Lifecycle == ModuleLifecycle.Faulted && survivor.ReasonText().Contains("UI update"), $"state={survivor.Lifecycle} reason='{survivor.ReasonText()}'");
        var tcs = new TaskCompletionSource<bool>();
        w.DispatcherQueue.TryEnqueue(() => tcs.SetResult(true));
        R("fault: the process and the UI thread are still alive", await Task.WhenAny(tcs.Task, Task.Delay(2000)) == tcs.Task, "dispatcher round trip after all of the above");
        w.Navigate("dashboard");
        await Settle(500);
        var rows = Find<Button>(w.PageContent).Count(b => b.Content is Grid);
        R("fault: the dashboard shows failed bays", rows == core.Modules.Count && core.Modules.Count(m => m.Bay.State == BayState.Failed) >= 5, $"{core.Modules.Count(m => m.Bay.State == BayState.Failed)} failed bays among {rows} rows");
        _ = fan;
    }

    static async void ThrowAsyncVoid() { await Task.Delay(50); throw new InvalidOperationException("async void threw on purpose"); }

    // ================================================================ single instance

    static async Task SingleInstanceCheck(MainWindow w)
    {
        int requests = w.ShowRequests;
        var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--data \"{Host.Core.Paths.DataRoot}\" --modules \"{AppPaths.ModulesRoot}\"") { UseShellExecute = false };
        using var second = Process.Start(psi)!;
        bool exited = await Task.Run(() => second.WaitForExit(15000));
        await Settle(400);
        R("single instance: the second start exits and shows the first", exited && second.ExitCode == 0 && w.ShowRequests > requests, $"second exited={exited} code={(exited ? second.ExitCode : -1)}; show requests {requests}->{w.ShowRequests}");
        if (!exited) { try { second.Kill(); } catch { } }
    }

    // ================================================================ visual tree helpers

    internal static IEnumerable<T> Find<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) yield break;
        if (root is T t) yield return t;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            foreach (var x in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return x;
    }
}

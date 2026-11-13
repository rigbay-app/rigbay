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
using Rigbay.Core.Install;

namespace Rigbay.App;

/// <summary>
/// Runs inside the elevated process (<c>--selftest</c>) against the modules in <c>tools\out\modules-test</c> and writes one
/// PASS/FAIL line per check to the log and to <c>--selftest-out</c>. It drives the same code the controls drive (row drivers,
/// Navigate, Ui.Post), not a mouse: real clicks are not covered, and the report says so.
/// </summary>
static partial class SelfTest
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

    /// <summary>
    /// A check that is not run, with the reason: neither a pass nor a failure. Used under --allow-multi for the checks that would reach another
    /// Rigbay running on the machine (the owner's real one): its pipe and its single-instance mutex.
    /// </summary>
    static void Skip(string name, string why)
    {
        var line = $"SELFTEST SKIP {name}: {why}";
        Lines.Add(line);
        AppLog.Info(line);
    }

    const string SiblingReason = "--allow-multi: this instance has no pipe server and no single-instance mutex, so the check would reach another Rigbay running on this machine";

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
        {
            // E2E-06: a text box takes the width the page gives it (half of the row, 280 to 560), keeps the whole text as a tooltip; a read-only value wraps instead of being cut
            foreach (var x in Find<SettingsExpander>(w.PageContent).ToList()) x.IsExpanded = true;
            await Settle(500);
            var folderBox = Find<TextBox>(w.PageContent).FirstOrDefault(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(t) == "Folder");
            var folderCard = folderBox == null ? null : Ancestors(folderBox).OfType<SettingsCard>().FirstOrDefault();
            double expectedWidth = folderCard == null ? 0 : RowRenderer.TextBoxWidthFor(folderCard.ActualWidth);
            R("module page: a text box is 280 to 560 wide and follows the width of its row, with the whole text as a tooltip",
                folderBox != null && folderCard != null && Math.Abs(folderBox.Width - expectedWidth) <= 1 && folderBox.Width >= RowRenderer.TextBoxMinWidth && folderBox.Width <= RowRenderer.TextBoxMaxWidth && ToolTipService.GetToolTip(folderBox) as string == folderBox.Text,
                $"box width={folderBox?.Width} row width={folderCard?.ActualWidth} expected={expectedWidth}; tooltip='{(folderBox == null ? null : ToolTipService.GetToolTip(folderBox))}' text='{folderBox?.Text}'");
            R("module page: the width rule keeps 280 for a narrow row, half of a wide one, and 560 at most",
                RowRenderer.TextBoxWidthFor(300) == 280 && RowRenderer.TextBoxWidthFor(800) == 400 && RowRenderer.TextBoxWidthFor(1500) == 560 && RowRenderer.TextBoxWidthFor(0) == 280 && RowRenderer.TextBoxWidthFor(double.NaN) == 280,
                $"300 -> {RowRenderer.TextBoxWidthFor(300)}, 800 -> {RowRenderer.TextBoxWidthFor(800)}, 1500 -> {RowRenderer.TextBoxWidthFor(1500)}");
            var valueText = Find<TextBlock>(w.PageContent).FirstOrDefault(t => t.Text == "none yet");
            R("module page: a read-only value wraps and is never cut", valueText is { TextWrapping: TextWrapping.Wrap, TextTrimming: TextTrimming.None } && !valueText.IsTextTrimmed, $"found={valueText != null} wrap={valueText?.TextWrapping} trimming={valueText?.TextTrimming}");
            foreach (var x in Find<SettingsExpander>(w.PageContent).ToList()) x.IsExpanded = false;
            await Settle(300);
        }

        // ---------------------------------------------------------------- search index
        R("search: row headers and descriptions", w.SearchFor("fan mode").Any() && w.SearchFor("sustained package power").Any() && w.SearchFor("Fan studio").Any(),
            $"{w.SearchIndex.Count} entries; 'fan mode' -> {string.Join(" | ", w.SearchFor("fan mode").Select(i => i.Label))}");

        // ---------------------------------------------------------------- hardware flow with the real editors
        await FlowChecks(w, fan);
        await VisibilityAndReadingChecks(w, fan);
        await VisualReadingChecks(w, fan);
        await TrayTrimChecks(w, fan);
        await TrayTipChecks(w, fan);
        await PageModelAdditionChecks(w, fan);

        // ---------------------------------------------------------------- turned off: the rows stay, greyed out; turned on again works
        await Task.Run(() => fan.Disable());
        w.Navigate(fan);
        await Settle(800);
        var offCards = Find<SettingsCard>(w.PageContent).ToList();
        var offSwitch = Find<ToggleSwitch>(ModulePage.LastHeader!).FirstOrDefault();
        R("module page: a turned-off module keeps its rows, greyed out; the header card's switch stays usable", fan.Lifecycle == ModuleLifecycle.Off && offCards.Count >= 7 && offCards.All(c => !c.IsEnabled)
            && offSwitch is { IsEnabled: true, IsOn: false },
            $"state={fan.Lifecycle}; {offCards.Count} cards, all disabled={offCards.All(c => !c.IsEnabled)}; header switch enabled={offSwitch?.IsEnabled} on={offSwitch?.IsOn}");
        R("module page: a turned-off module's hotkeys and tray items are gone", Host.Core.Hotkeys.Entries.All(h => h.ModuleId != "demo-fan") && Host.Core.Tray.Items.All(i => i.ModuleId != "demo-fan"), "removed by the core after Shutdown");
        enabled = await Task.Run(() => fan.Enable());
        R("module: turned on again after off (Initialize on the same instance)", enabled && fan.Lifecycle == ModuleLifecycle.Running && fan.Page != null, $"state={fan.Lifecycle}");
        await Settle(700);

        // ---------------------------------------------------------------- hotkey, tray, toast, commands
        await HostServiceChecks(w, fan);

        // ---------------------------------------------------------------- brand: window mark, tray icon, module icons
        await BrandChecks(w);

        // ---------------------------------------------------------------- language at run time
        await LanguageChecks(w, fan);

        // ---------------------------------------------------------------- fault boundary
        await RefreshChecks(w);
        await FaultChecks(w);
        await PlanChecks(w);

        // ---------------------------------------------------------------- install, update and remove
        await InstallChecks(w);

        // ---------------------------------------------------------------- single instance
        // with --allow-multi another Rigbay may be running (the owner's own): a second start would show its window, so the check is not run
        if (Host.Options.AllowMulti) Skip("single instance: the second start exits and shows the first", "--allow-multi: another Rigbay may own the single-instance lock");
        else await SingleInstanceCheck(w);

        w.Navigate("log");
        await Settle(600);
        // LG-1: the log is a list of lines (the level and source filters start at All)
        LogPage.Level = LogPage.Filter.All; LogPage.Source = null;
        w.Navigate("log"); await Settle(600);
        var logLines = LogPage.Shown.Select(AppLog.FormatLine).ToList();
        R("log page shows the host log", LogPage.LastList?.Items.Count == logLines.Count && logLines.Any(l => l.Contains("SELFTEST")) && logLines.Any(l => l.Contains("module disabled")), $"{logLines.Count} lines in the list; has SELFTEST lines and the fault lines");
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
        R("flow: module toast reaches the toast host", Host.Toast.Shown > toasts, $"Shown {toasts} -> {Host.Toast.Shown}; balloon mode={Host.Toast.UsesBalloon}");
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

    // a reading of a group is a line of "All readings" (UI review VR-1): its value follows the bus even while the expander is folded
    static string ReadingText(MainWindow w, string header) =>
        w.LastRenderer?.ReadingValues.TryGetValue(header, out var t) == true ? t.Text : "(no line)";

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
        var live = ReadingText(w, "Pump flow");
        R("readings: page row shows the number with its unit", live.Length > 0 && live != na && live.Contains('%'), $"'{live}'");
        w.Navigate("dashboard"); await Settle(600);
        var texts = Find<TextBlock>(w.PageContent).Select(t => t.Text).ToList();
        R("dashboard: readings carry labels (module descriptor, host text)", texts.Contains("Pump") && texts.Contains("CPU") && !texts.Contains("demo-fan.pump") && !texts.Contains("cpu.temp"), "labels: " + string.Join(" | ", texts.Where(t => t is "Pump" or "CPU" or "demo-fan.pump" or "cpu.temp")));
        await core.ExecuteCommandAsync(new[] { "demo-fan", "sensor", "off" }); await Settle(500);
        // DB-2: a reading that is not available is left out (no "Not available" on the Dashboard); the page redraws after the bus says so
        for (int i = 0; i < 12 && Find<TextBlock>(w.PageContent).Any(t => t.Text is "Pump"); i++) await Settle(250);
        // only text that is drawn: a tile keeps a collapsed "Not available" for the moment its reading goes
        texts = Find<TextBlock>(w.PageContent).Where(t => t.Visibility == Visibility.Visible && Ancestors(t).OfType<UIElement>().All(a => a.Visibility == Visibility.Visible)).Select(t => t.Text).ToList();
        R("dashboard: a withdrawn reading is left out", !texts.Contains(na) && !texts.Contains("Pump") && texts.Contains("CPU") && !core.Metrics.TryGetLatest(new MetricId("demo-fan.pump"), out _), $"text '{na}' present={texts.Contains(na)}, label Pump present={texts.Contains("Pump")}, CPU present={texts.Contains("CPU")}");
        w.Navigate(fan); await Settle(500);
        R("readings: a reading page row shows Not available when withdrawn", ReadingText(w, "Pump flow") == na, $"'{ReadingText(w, "Pump flow")}'");
        await core.ExecuteCommandAsync(new[] { "demo-fan", "sensor", "on" }); await Settle(500);
        R("readings: a published value replaces Not available live", ReadingText(w, "Pump flow") is { } t2 && t2 != na && t2.Contains('%'), $"'{ReadingText(w, "Pump flow")}'");
    }


    // ================================================================ V1: sparklines, tiles, trend chart, nothing live while hidden

    // ================================================================ memory in the tray: the page is dropped after the delay and comes back

    /// <summary>A weak reference to the page that is on screen, taken in a method of its own so no local of the caller keeps the page alive.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static (WeakReference Root, WeakReference? Chart, int Elements) PageRefs(MainWindow w)
    {
        var chart = Find<TrendChart>(w.PageContent).FirstOrDefault();
        return (new WeakReference(w.PageContent), chart == null ? null : new WeakReference(chart), Find<DependencyObject>(w.PageContent).Count(d => d.GetType().Assembly == typeof(MainWindow).Assembly));
    }

    static async Task TrayTrimChecks(MainWindow w, ModuleEntry fan)
    {
        var core = Host.Core;
        var policy = w.TrimPolicy;
        bool wasEnabled = policy.Enabled;
        var wasDelay = policy.Delay;
        policy.Enabled = true; policy.Delay = TimeSpan.FromSeconds(1);   // the shortest delay: the check waits for the real timer
        var wasSteps = w.TrimStepsInUse;
        try
        {
            // what the app does by default: only the working set is given back, the page stays on the window
            w.TrimStepsInUse = Rigbay.App.Services.TrimSteps.Default;
            w.Navigate(fan); await Settle(1200);
            int defaultTrims = policy.Trims;
            w.HideToTray(); await Settle(2800);
            var dflt = w.LastTrim;
            R("tray trim: the default trim gives the working set back once and keeps the page",
                policy.Trims == defaultTrims + 1 && !w.PageTrimmed && w.PageContent != null && dflt is { PageDropped: false } && dflt.Steps == Rigbay.App.Services.TrimSteps.WorkingSet && dflt.After.WorkingSetMb < dflt.Before.WorkingSetMb * 0.5,
                dflt == null ? "no trim ran" : $"steps={dflt.Steps} ws {dflt.Before.WorkingSetMb:0.0} -> {dflt.After.WorkingSetMb:0.0} MB, private {dflt.Before.PrivateMb:0.0} -> {dflt.After.PrivateMb:0.0} MB, trims {defaultTrims} -> {policy.Trims}");
            w.ShowFromTray(); await Settle(1200);

            // every step (what tools\perf\measure.ps1 compares): the page is dropped and rebuilt
            w.TrimStepsInUse = Rigbay.App.Services.TrimSteps.All;
            w.Navigate(fan); await Settle(1200);
            await w.RunScriptAsync("expand-all;scroll:300");
            await Settle(400);
            double offset = w.PageScrollOffset;
            var refs = PageRefs(w);
            var scopeBefore = w.Scope;
            int subs = scopeBefore!.TotalLiveSubscriptions;

            // a hide that is undone before the delay trims nothing
            int trimsBefore = policy.Trims;
            w.HideToTray(); await Task.Delay(400);
            w.ShowFromTray(); await Settle(1500);
            R("tray trim: a window shown again before the delay is not trimmed", !w.PageTrimmed && policy.Trims == trimsBefore && w.PageContent != null && policy.Remaining() == null, $"trimmed={w.PageTrimmed} trims {trimsBefore} -> {policy.Trims}");

            // hidden past the delay: the page is gone, once
            refs = PageRefs(w);
            w.HideToTray();
            await Settle(3200);
            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            var report = w.LastTrim;
            R("tray trim: after the delay the page is dropped and the heap and working set are trimmed once",
                w.PageTrimmed && w.PageContent == null && policy.Trims == trimsBefore + 1 && report is { PageDropped: true } && report.Steps == Rigbay.App.Services.TrimSteps.All,
                $"trimmed={w.PageTrimmed} content={(w.PageContent == null ? "none" : "still there")} trims={policy.Trims} {(report == null ? "no report" : $"ws {report.Before.WorkingSetMb:0.0} -> {report.After.WorkingSetMb:0.0} MB, private {report.Before.PrivateMb:0.0} -> {report.After.PrivateMb:0.0} MB, {report.Ms:0} ms")}");
            R("tray trim: WinUI keeps no reference to the page root or the chart after the trim",
                !refs.Root.IsAlive && (refs.Chart == null || !refs.Chart.IsAlive) && report != null && report.PageElementsAlive == 0,
                $"root alive={refs.Root.IsAlive} chart alive={(refs.Chart?.IsAlive.ToString() ?? "no chart")}; the app's own elements of the page: {report?.PageElements} seen, {report?.PageElementsAlive} alive");
            R("tray trim: nothing is live in the tray", w.Scope == null && !fan.IsPageVisible && core.Metrics.SubscriberCount(WellKnownMetrics.CpuTemp) == 0 && scopeBefore.Disposed, $"scope={(w.Scope == null ? "none" : "there")} readers(cpu.temp)={core.Metrics.SubscriberCount(WellKnownMetrics.CpuTemp)} subscriptions were {subs}");

            // a page refresh and a language change while hidden build nothing
            w.SimulatePageRefresh(fan);
            await Settle(300);
            R("tray trim: a page refresh while the window is in the tray builds nothing", w.PageContent == null && w.Scope == null, $"content={(w.PageContent == null ? "none" : "built")}");

            // show: the same page, the same scroll offset, live again
            w.ShowFromTray(); await Settle(2500);
            R("tray trim: showing the window rebuilds the page that was open, scrolled to where it was",
                !w.PageTrimmed && w.PageContent != null && w.ShownEntry == fan && Math.Abs(w.PageScrollOffset - offset) < 2 && w.Scope is { Active: true } s && s.TotalLiveSubscriptions == subs && fan.IsPageVisible,
                $"trimmed={w.PageTrimmed} shown={w.ShownEntry?.Id} offset {offset:0} -> {w.PageScrollOffset:0} subscriptions {w.Scope?.TotalLiveSubscriptions} (were {subs}); rebuild {w.LastRestoreBuildMs:0} ms, first frame {w.LastRestoreMs:0} ms");

            // the setting off: no trim however long the window stays hidden
            policy.Enabled = false;
            int t0 = policy.Trims;
            w.HideToTray(); await Settle(2500);
            R("tray trim: with the setting off the page stays", !w.PageTrimmed && policy.Trims == t0 && w.PageContent != null, $"trimmed={w.PageTrimmed} trims {t0} -> {policy.Trims}");
            w.ShowFromTray(); await Settle(500);
        }
        finally
        {
            policy.Enabled = wasEnabled; policy.Delay = wasDelay; w.TrimStepsInUse = wasSteps;
            if (w.PageTrimmed || !w.AppWindow.IsVisible) { w.ShowFromTray(); await Settle(800); }
            w.Navigate(fan); await Settle(300);
        }
    }

    static async Task TrayTipChecks(MainWindow w, ModuleEntry fan)
    {
        var tray = Host.Tray;
        if (tray == null) { Skip("tray tooltip", "the tray icon is not there"); return; }
        var saved = AppState.Settings.TrayTip.Copy();
        // the module publishes every second: compare two texts built in the same instant, and try again when a reading moved in between
        async Task<bool> Same(Func<(string A, string B)> pair)
        {
            for (int i = 0; i < 4; i++) { var (a, b) = pair(); if (a == b) return true; await Settle(150); }
            return false;
        }
        try
        {
            // Automatic is the default and is what the icon had before the setting existed
            AppState.Settings.TrayTip = new TrayTipSettings();
            tray.UpdateTip();
            bool autoSame = await Same(() => { tray.UpdateTip(); return (tray.Tip, TrayTipBuilder.BuildAutomatic(tray.TipSource)); });
            var auto = tray.Tip;
            R("tray tooltip: Automatic is 'Rigbay' and the first two running modules, as before", autoSame && auto.StartsWith("Rigbay\n") && auto.Split('\n').Length <= 3 && auto.Length <= TrayHost.MaxTip, $"'{auto.ReplaceLineEndings(" | ")}' ({auto.Length} characters)");

            // Custom: two items of demo-fan; the icon's text is the preview's text
            await w.RunScriptAsync("tip:custom,state:demo-fan,cpu.temp=Chip");
            w.Navigate("settings"); await Settle(900);
            w.Scope?.RedrawNow();
            bool same = await Same(() => { w.Scope?.RedrawNow(); tray.UpdateTip(); return (TrayTipEditor.LastPreview, tray.Tip); });
            var custom = tray.Tip;
            var lines = custom.Split('\n');
            R("tray tooltip: Custom with two items of demo-fan: the icon's Tip equals the preview of Settings", same && lines.Length == 2 && lines[0] == "Rigbay" && lines[1].Contains(" · Chip ") && custom != auto, $"icon '{custom.ReplaceLineEndings(" | ")}' preview '{TrayTipEditor.LastPreview.ReplaceLineEndings(" | ")}'");
            var counterText = Find<TextBlock>(w.PageContent).Select(t => t.Text).FirstOrDefault(t => t.EndsWith(" / 127", StringComparison.Ordinal));
            R("tray tooltip: Settings shows the length as 'n / 127'", counterText != null && counterText == $"{TrayTipEditor.LastPreview.Length} / 127", $"counter '{counterText}'");
            var rowsShown = Find<SettingsCard>(w.PageContent).Count(c => Equals(c.Tag, "tray-tip-item"));
            R("tray tooltip: Settings lists the two items with move and remove buttons", rowsShown == 2 && Find<Button>(w.PageContent).Count(b => b.Content is FontIcon) >= 6, $"{rowsShown} item rows");

            // a change shows on the icon at once, not at the next 5 s tick
            await w.RunScriptAsync("tip:custom,cpu.temp=Hot");
            R("tray tooltip: a change of the setting reaches the icon at once", tray.Tip.StartsWith("Rigbay\nHot ") && !tray.Tip.Contains('·'), $"'{tray.Tip.ReplaceLineEndings(" | ")}'");

            // new line, hidden title, an item of a module that is not there: kept in the setting, silent in the text
            await w.RunScriptAsync("tip:custom,notitle,state:demo-fan,cpu.temp+nl,state:gone-module");
            var multi = tray.Tip;
            R("tray tooltip: new line, no first line, an item of an uninstalled module adds nothing and stays in the list",
                multi.Split('\n').Length == 2 && !multi.StartsWith("Rigbay") && AppState.Settings.TrayTip.Items.Count == 3 && AppState.Settings.TrayTip.Items[2].Id == "state:gone-module", $"'{multi.ReplaceLineEndings(" | ")}', {AppState.Settings.TrayTip.Items.Count} items in the list");

            // the module off: its items leave the text silently, the list keeps them
            await Task.Run(() => fan.Disable());
            await Settle(500);
            tray.UpdateTip();
            R("tray tooltip: items of a module that is turned off are left out silently and stay in the list", tray.Tip == "Rigbay" && AppState.Settings.TrayTip.Items.Count == 3, $"'{tray.Tip.ReplaceLineEndings(" | ")}'");
            await Task.Run(() => fan.Enable());
            await Settle(1500);
            tray.UpdateTip();
            R("tray tooltip: they come back when the module is turned on again", tray.Tip.Contains("°C") || tray.Tip.Split('\n').Length == 2, $"'{tray.Tip.ReplaceLineEndings(" | ")}'");

            // back to Automatic
            await w.RunScriptAsync("tip:auto");
            bool backSame = await Same(() => { tray.UpdateTip(); return (tray.Tip, TrayTipBuilder.BuildAutomatic(tray.TipSource)); });
            R("tray tooltip: back to Automatic gives the automatic text again", backSame && tray.Tip.StartsWith("Rigbay\n") && !AppState.Settings.TrayTip.IsCustom, $"'{tray.Tip.ReplaceLineEndings(" | ")}'");

            // a reading tile offers "Show in tray tooltip"
            w.Navigate(fan); await Settle(900);
            var tile = Find<ReadingTile>(w.PageContent).FirstOrDefault();
            var menu = tile?.TrayTipMenu();
            R("tray tooltip: a reading tile has the 'Show in tray tooltip' menu", menu is { Items.Count: 1 } && menu.Items[0] is ToggleMenuFlyoutItem, tile == null ? "no tile" : menu?.GetType().Name ?? "no menu");
            // what a click on it does, from Automatic: Custom starts from what Automatic shows, then the reading is added and taken out again
            AppState.Settings.TrayTip = new TrayTipSettings();
            bool pumpShownBefore = TrayTipEditor.ShownInTip("demo-fan.pump");
            TrayTipEditor.SetInTip("demo-fan", "demo-fan.pump", true);
            bool addedOk = AppState.Settings.TrayTip.IsCustom && AppState.Settings.TrayTip.Contains("demo-fan.pump") && tray.Tip.Contains("Pump ") && TrayTipEditor.ShownInTip("demo-fan.pump");
            string withPump = tray.Tip;
            TrayTipEditor.SetInTip("demo-fan", "demo-fan.pump", false);
            R("tray tooltip: Show in tray tooltip turns Custom on from what Automatic shows, adds the reading and takes it out again", addedOk && !AppState.Settings.TrayTip.Contains("demo-fan.pump") && !tray.Tip.Contains("Pump ") && AppState.Settings.TrayTip.Items.Count > 0, $"before shown={pumpShownBefore}; with '{withPump.ReplaceLineEndings(" | ")}'; after '{tray.Tip.ReplaceLineEndings(" | ")}'");

            // an app.json from before the setting reads as Automatic
            var dir = Path.Combine(Path.GetTempPath(), "rigbay-tip-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "app.json");
                File.WriteAllText(file, "{ \"version\": 1, \"data\": { \"theme\": \"dark\", \"logLevel\": \"info\", \"releaseMemoryInTray\": true, \"releaseMemoryDelaySeconds\": 60 } }");
                var old = new JsonSettingsStore(file, AppLog.For("host")).Load<AppSettings>();
                old.TrayTip = TrayTipSettings.Clean(old.TrayTip);
                R("tray tooltip: an app.json without the new fields reads as Automatic", old.Theme == "dark" && !old.TrayTip.IsCustom && old.TrayTip.ShowTitle && old.TrayTip.Items.Count == 0, $"theme={old.Theme} custom={old.TrayTip.IsCustom}");
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }
        finally
        {
            AppState.Settings.TrayTip = saved;
            AppState.Save();
            tray.UpdateTip();
            if (fan.Lifecycle != ModuleLifecycle.Running) { await Task.Run(() => fan.Enable()); await Settle(800); }
            w.Navigate(fan); await Settle(300);
        }
    }

    static async Task VisualReadingChecks(MainWindow w, ModuleEntry fan)
    {
        var core = Host.Core;
        w.Navigate(fan);
        await Settle(5500);   // the history keeps one point per 2 s: a few seconds of the module's 1 Hz readings

        var readingRows = fan.Page!.EnumerateRows().OfType<ReadingRow>().ToList();
        var r = w.LastRenderer!;
        var all = r.AllReadingsExpander;
        if (all != null) { all.IsExpanded = true; await Settle(400); all.StartBringIntoView(); await Settle(2600); }   // the lines are realized when they are in view
        var sparks = r.Sparklines;
        R("visual: All readings lists every reading of the groups as a compact line with its sparkline (the table view)", all != null && sparks.Count == readingRows.Count && sparks.Count >= 5 && sparks.All(s => s.IsLoaded),
            $"{sparks.Count} lines for {readingRows.Count} reading rows; expander={all != null}");
        var drawn = sparks.Where(s => s.PointsDrawn >= Sparkline.MinPoints).ToList();
        R("visual: the sparklines are drawn from the history (from ten points on, a line and a live end dot)", drawn.Count >= 4 && drawn.All(s => s.Children.OfType<Microsoft.UI.Xaml.Shapes.Polyline>().Any() && s.Live),
            $"{drawn.Count} of {sparks.Count} have points: " + string.Join(", ", sparks.Select(s => $"{s.Metric}={s.PointsDrawn}")));
        var readingNames = readingRows.Select(x => fan.Localisation.T(x.HeaderKey)).ToHashSet();
        R("visual: no reading is a row card of its own any more (VR-1: a tile, a line in the chart, a line in All readings)",
            !Find<SettingsCard>(w.PageContent).Any(c => readingNames.Contains(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(c))), "no SettingsCard named like a reading");
        if (all != null) { all.IsExpanded = false; await Settle(300); }

        var block = ModulePage.LastBlock;
        var tiles = block?.TileList ?? Array.Empty<ReadingTile>();
        R("visual: reading tiles at the top of the page (2 to 4, one per kind)", block?.Tiles != null && tiles.Count is >= 2 and <= 4
            && tiles.Select(t => t.ValueText).All(t => t.Length > 0 && t != core.Languages.T("core.reading.unavailable")),
            $"{tiles.Count} tiles: " + string.Join(" | ", tiles.Select(t => t.ValueText)));
        R("visual: a tile has its sparkline and a temperature or percent tile its meter", tiles.All(t => t.Spark.PointsDrawn >= Sparkline.MinPoints) && tiles.Any(t => t.HasMeter && t.MeterShare > 0),
            $"sparkline points: {string.Join(",", tiles.Select(t => t.Spark.PointsDrawn))}; meter shares: {string.Join(",", tiles.Select(t => t.MeterShare.ToString("0.00")))}");
        R("visual: the tiles come before the rows", block != null && IndexIn(w.PageContent!, block) < IndexIn(w.PageContent!, Find<SettingsCard>(w.PageContent).First()),
            "ReadingBlock is in the main card, before the first group");

        // the tiles are the chart's tabs: one list with one selected item for UI Automation (AX-4)
        var strip = block?.Tiles;
        var stripPeer = strip == null ? null : Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(strip) ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(strip);
        var selection = stripPeer?.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Selection) as Microsoft.UI.Xaml.Automation.Provider.ISelectionProvider;
        var tilePeer = tiles.Count > 0 ? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(tiles[0]) : null;
        R("visual: the tile strip is a list with one selected item, each tile a list item named with its value",
            stripPeer?.GetAutomationControlType() == Microsoft.UI.Xaml.Automation.Peers.AutomationControlType.List && selection is { CanSelectMultiple: false } && selection.GetSelection().Length == 1
            && strip!.SelectedIndex == 0 && tiles[0].IsSelected && tilePeer?.GetAutomationControlType() == Microsoft.UI.Xaml.Automation.Peers.AutomationControlType.ListItem
            && tilePeer.GetName().Contains(tiles[0].ValueText.Split(' ')[0]),
            $"type={stripPeer?.GetAutomationControlType()} selected={strip?.SelectedIndex} tile name='{tilePeer?.GetName()}'");

        var chart = block?.Chart;
        R("visual: the chart draws the selected tile's reading, one line, no legend, a time axis, the unit on the top tick", chart != null && chart.HasData && chart.SeriesDrawn == 1 && chart.FocusRow == tiles[0].Row
            && !chart.Comparing && Find<TextBlock>(chart.Plot).Any(t => t.Text == Host.T("chart.now")) && chart.TickLabels.Count >= 3 && chart.TickLabels[^1].EndsWith(" °C", StringComparison.Ordinal)
            && !Find<Microsoft.UI.Xaml.Shapes.Path>(chart.Plot).Any(),
            $"focus={chart?.FocusRow?.HeaderKey} series={chart?.SeriesDrawn} ticks={string.Join(" ", chart?.TickLabels ?? Array.Empty<string>())} span={chart?.ShownSeconds:0}s");
        {
            // UF: the time axis says each moment once and puts no label where there is no data yet (a few seconds of history on a one-minute axis)
            var hist = chart?.FocusRow is { } fr ? core.Metrics.History.Get(fr.Metric, TimeSpan.FromSeconds(chart.RangeSeconds)) : Array.Empty<HistoryPoint>();
            double age = hist.Count > 0 ? (core.Metrics.History.Now - hist[0].Time).TotalSeconds : 0;
            var times = chart?.TimeLabels ?? Array.Empty<string>();
            var axis = chart?.TimeAxisShown;
            var onPlot = chart == null ? new HashSet<string>() : Find<TextBlock>(chart.Plot).Select(t => t.Text).ToHashSet();
            R("visual: the time axis labels are distinct, start at now, lie within the data and are all on the plot", axis != null && times.Count >= 1 && times[0] == Host.T("chart.now")
                && times.Distinct().Count() == times.Count && axis.Ticks.All(t => (axis.CoversRange || t.SecondsAgo <= age + 0.5) && t.SecondsAgo <= axis.ShownSeconds) && times.All(onPlot.Contains),
                $"labels={string.Join(", ", times)} data age={age:0.0}s span={axis?.ShownSeconds:0}s step={axis?.StepSeconds}s");
        }
        bool tip =chart != null && chart.HoverAt(chart.Plot.ActualWidth - 30);
        R("visual: hover shows a crosshair and a tooltip with the time and the value", tip && Find<Microsoft.UI.Xaml.Shapes.Line>(chart!.Plot).Count() > 3
            && Find<Border>(chart.Plot).SelectMany(b => Find<TextBlock>(b)).Any(t => t.Text.Length == 8 && t.Text[2] == ':'), "tooltip text found on the plot");
        chart?.HoverAt(null);
        bool focused = chart != null && chart.Plot.Focus(FocusState.Keyboard);
        await Settle(200);
        double? x0 = chart?.HoverX;
        chart?.Nudge(Windows.System.VirtualKey.Left); chart?.Nudge(Windows.System.VirtualKey.Left);
        double? x1 = chart?.HoverX;
        R("visual: keyboard: the plot takes focus and Left moves the crosshair back in time", focused && x0 != null && x1 != null && x1 < x0 && Find<Border>(chart!.Plot).Any(b => b.Style == Ui.Style("RbChartTip")),
            $"focus={focused} hover x {x0:0} -> {x1:0}");
        chart?.Plot.Focus(FocusState.Programmatic); chart?.HoverAt(null);

        // another tile is the chart's next tab: power, with PL1 as a dashed reference
        chart?.SelectGroup(1);
        await Settle(400);
        R("visual: another tile is one click away (power: one line and PL1 as a dashed reference labelled at the line)", chart != null && strip?.SelectedIndex == 1 && tiles[1].IsSelected && !tiles[0].IsSelected
            && chart.SeriesDrawn == 1 && chart.ReferencesDrawn == 1 && chart.ReferenceLabels.Single().StartsWith("PL1 limit ", StringComparison.Ordinal)
            && Find<Microsoft.UI.Xaml.Shapes.Polyline>(chart.Plot).Any(p => p.StrokeDashArray is { Count: > 0 }),
            $"selected={strip?.SelectedIndex} series drawn={chart?.SeriesDrawn} references={chart?.ReferencesDrawn} labels={string.Join(" | ", chart?.ReferenceLabels ?? Array.Empty<string>())}");

        // Compare: the other readings of the same group (usage: CPU usage and the pump, both percent) in the categorical order, with a legend
        int usage = tiles.ToList().FindIndex(t => t.Row.Metric == WellKnownMetrics.CpuUsage);
        if (usage >= 0 && tilePeer != null && Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(tiles[usage])?.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.SelectionItem) is Microsoft.UI.Xaml.Automation.Provider.ISelectionItemProvider item)
            item.Select();   // the way a screen reader picks a tile
        await Settle(400);
        bool compareShown = chart?.CompareButton.Visibility == Visibility.Visible;
        chart?.SetCompare(true);
        await Settle(300);
        R("visual: a tile picked through UI Automation is charted; Compare adds the same-kind readings with a legend", chart != null && usage >= 0 && strip?.SelectedIndex == usage && compareShown
            && chart.Comparing && chart.SeriesDrawn == 2,
            $"usage tile={usage} selected={strip?.SelectedIndex} compare shown={compareShown} series={chart?.SeriesDrawn}");
        chart?.SetCompare(false);
        chart?.SelectGroup(0);

        // one axis, never two: every chart plan groups one unit only
        var plan = ReadingPlan.Build(fan.Page!, id => core.Metrics.UnitOf(id));
        R("visual: every chart has a single unit (no second axis)", plan.Charts.Count >= 3 && plan.Charts.All(c => c.Series.All(s => core.Metrics.UnitOf(s.Metric) == c.Unit)), string.Join("; ", plan.Charts.Select(c => $"{c.Id}[{c.Series.Count}]")));
        R("visual: tiles wrap into 4, 2 or 1 columns", TileStrip.Columns(4, 900) == 4 && TileStrip.Columns(4, 560) == 2 && TileStrip.Columns(4, 300) == 1 && TileStrip.Columns(3, 560) == 3 && TileStrip.Columns(1, 900) == 1,
            $"4@900={TileStrip.Columns(4, 900)} 4@560={TileStrip.Columns(4, 560)} 4@300={TileStrip.Columns(4, 300)} 3@560={TileStrip.Columns(3, 560)}");

        {
            // UF: a refresh that relabels the primary control (the Recorder's Start becomes Stop) gives the focus to the new primary control, and
            // keyboard focus stays keyboard focus; here the selected segment of Fan studio's mode stands in, its label changed under the key
            var oldPrimary = w.PageContent == null ? null : FocusKey.FindPrimary(w.PageContent);
            var oldMain = oldPrimary == null ? null : FocusKey.PrimaryControl(oldPrimary);
            bool took = oldMain?.Focus(FocusState.Keyboard) == true;
            await Settle(200);
            var key = w.PageContent == null ? null : FocusKey.Capture(w.Content.XamlRoot, w.PageContent);
            if (key != null) FocusKey.RememberForRefresh(key.Renamed("Stop"));
            w.RefreshPageKeepingView();
            await Settle(400);
            var now = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(w.Content.XamlRoot) as Control;
            var newPrimary = w.PageContent == null ? null : FocusKey.FindPrimary(w.PageContent);
            var newMain = newPrimary == null ? null : FocusKey.PrimaryControl(newPrimary);
            R("refresh: a relabelled primary control keeps the focus on the page's primary control (keyboard focus stays keyboard focus)",
                took && key is { InPrimary: true } && now != null && ReferenceEquals(now, newMain) && !ReferenceEquals(now, oldMain) && now.FocusState == FocusState.Keyboard,
                $"focused before: {oldMain?.GetType().Name} '{(oldMain as ContentControl)?.Content}' (took={took}, in primary={key?.InPrimary}); after: {now?.GetType().Name} '{(now as ContentControl)?.Content}' {now?.FocusState} (new primary control: {ReferenceEquals(now, newMain)})");
        }

        // a withdrawn reading loses its history and its line, and gets both back
        var pump = new MetricId("demo-fan.pump");
        await core.ExecuteCommandAsync(new[] { "demo-fan", "sensor", "off" }); await Settle(1500);
        R("visual: a withdrawn reading's history is gone", core.Metrics.History.PointCount(pump) == 0, $"points={core.Metrics.History.PointCount(pump)}");
        await core.ExecuteCommandAsync(new[] { "demo-fan", "sensor", "on" }); await Settle(1200);

        // the dashboard bay rack: a sparkline beside each key reading
        w.Navigate("dashboard"); await Settle(2500);
        var rackSparks = Find<Sparkline>(w.PageContent).ToList();
        R("visual: the dashboard bay rack has a sparkline per key reading", rackSparks.Count >= 2 && rackSparks.Any(s => s.PointsDrawn >= 2), $"{rackSparks.Count} sparklines; points {string.Join(",", rackSparks.Select(s => s.PointsDrawn))}");

        // nothing live while the window is hidden: no subscription (so the module sees no reader), no timer, no drawing
        w.Navigate(fan); await Settle(2200);
        var scope = w.Scope!;
        int subsOn = scope.TotalLiveSubscriptions, redrawsOn = scope.TotalRedraws;
        bool active = scope.Active;
        w.AppWindow.Hide(); w.UpdatePageVisibility(); await Settle(300);
        int redrawsAtHide = scope.TotalRedraws;
        int pumpReaders = core.Metrics.SubscriberCount(new MetricId("demo-fan.pump")), tempReaders = core.Metrics.SubscriberCount(WellKnownMetrics.CpuTemp);
        await Settle(2600);
        R("visual: hidden window: nothing is drawn, the timer is off and no reading is subscribed to",
            active && subsOn >= 7 && !scope.Active && scope.TotalLiveSubscriptions == 0 && !scope.TimerRunning && scope.TotalRedraws == redrawsAtHide && tempReaders == 0 && pumpReaders == 0,
            $"before: active={active} subscriptions={subsOn} redraws={redrawsOn}; hidden: active={scope.Active} subscriptions={scope.TotalLiveSubscriptions} redraws {redrawsAtHide} -> {scope.TotalRedraws} readers(cpu.temp)={tempReaders}");
        w.ShowFromTray(); await Settle(2600);
        R("visual: shown again: subscriptions and drawing are back",
            scope.Active && scope.TotalLiveSubscriptions == subsOn && scope.TotalRedraws > redrawsAtHide + 1 && core.Metrics.SubscriberCount(WellKnownMetrics.CpuTemp) > 0,
            $"active={scope.Active} subscriptions={scope.TotalLiveSubscriptions} redraws {redrawsAtHide} -> {scope.TotalRedraws}");

        // a page with no readings gets no block
        w.Navigate("settings"); await Settle(300);
        R("visual: a page without readings gets no tiles and no chart", Find<ReadingTile>(w.PageContent).Count() == 0 && Find<TrendChart>(w.PageContent).Count() == 0, "settings page");
        w.Navigate(fan); await Settle(300);
    }

    // ================================================================ host API 0.1.3: primary control, header rows, live lines, warn, secondary, confirm

    static async Task PageModelAdditionChecks(MainWindow w, ModuleEntry fan)
    {
        w.Navigate(fan);
        await Settle(2500);
        var r = w.LastRenderer!;
        var page = fan.Page!;

        // the primary: drawn large, before the tiles, a short choice as segments with one tab stop
        var choice = r.Choices.FirstOrDefault();
        R("0.1.3: the primary row is drawn first, a 3-option choice as a segmented control (RadioButtons), nothing extra in the groups",
            r.PrimaryRow == page.Primary && choice is { ShowsSegments: true, Segments.Items.Count: 3 } && choice.Segments!.SelectedIndex == 0
            && ModulePage.LastBlock != null && IndexIn(w.PageContent!, choice) < IndexIn(w.PageContent!, ModulePage.LastBlock),
            $"primary={r.PrimaryRow?.HeaderKey} segments={choice?.ShowsSegments} items={choice?.Segments?.Items.Count} selected={choice?.Segments?.SelectedIndex}");
        R("0.1.3: the primary's help text is the tooltip and the accessible help of its header",
            Find<TextBlock>(w.PageContent).Any(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(t).Contains("Auto: the pretend device", StringComparison.Ordinal)
                && Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(t).StartsWith(Host.T("row.hardware-note"), StringComparison.Ordinal) && t.Text.Contains(RowRenderer.InfoGlyph) && t.Text.Contains(RowRenderer.ShieldGlyph)),
            "shield and info glyph after 'Fan mode'; the help text says what it changes, then the explanation");
        R("0.1.3: a segmented control gives way to a drop-down where it does not fit, and back", choice != null && Narrow(choice),
            $"needs {choice?.SegmentsWidth:0} px");
        {
            // UF: in a row the segments also leave the row's name its room (a 640 px window squeezed a Vietnamese name into three lines)
            var row = r.Choices.Skip(1).FirstOrDefault(c => c.CanSegment && !double.IsNaN(c.RowWidth));   // a row on screen (its card has a width)
            bool squeezed = false, roomy = false;
            double cardWidth = row?.RowWidth ?? double.NaN;
            if (row != null)
            {
                // the card measures its control with an unbounded width: what decides is the card's own width
                var unbounded = new Windows.Foundation.Size(double.PositiveInfinity, 200);
                row.SetRowWidth(row.SegmentsWidth + 40 + 32);
                row.Measure(unbounded);
                squeezed = !row.ShowsSegments && row.Combo.Visibility == Visibility.Visible;
                row.SetRowWidth(row.SegmentsWidth + ChoiceSelector.RowLabelRoom + 32 + 10);
                row.Measure(unbounded);
                roomy = row.ShowsSegments && row.Combo.Visibility == Visibility.Collapsed;
                row.SetRowWidth(cardWidth);
            }
            R("0.1.3: a row's segmented control is a drop-down when it would leave its name less than 200 px", row != null && !double.IsNaN(cardWidth) && squeezed && roomy,
                $"row segments need {row?.SegmentsWidth:0} px, its card is {cardWidth:0} px; name room 40 px: drop-down={squeezed}; {ChoiceSelector.RowLabelRoom + 10:0} px: segments={roomy}");
        }

        // an expander with a switch in its header and a live line
        var pump = Find<SettingsExpander>(w.PageContent).FirstOrDefault(x => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(x) == "Pump");
        var pumpSwitch = pump?.Content is ContentControl cc ? Find<ToggleSwitch>(cc).FirstOrDefault() : null;
        bool lineOn = pump != null && Find<TextBlock>(pump).Any(t => t.Text.StartsWith("On · flow ", StringComparison.Ordinal) && t.Visibility == Visibility.Visible);
        R("0.1.3: an expander shows its header control and its live line while closed", pump is { IsExpanded: false } && pumpSwitch is { IsOn: true } && lineOn,
            $"expander={pump != null} switch={pumpSwitch?.IsOn} line on={lineOn}");
        if (pumpSwitch != null)
        {
            pumpSwitch.IsOn = false;
            await Settle(1500);
            bool lineOff = Find<TextBlock>(pump!).Any(t => t.Text == "Off" && t.Visibility == Visibility.Visible);
            R("0.1.3: the header switch drives the row and the line follows the module", lineOff && page.EnumerateRows().OfType<ToggleRow>().First(t => t.HeaderKey == "row.pump-on.header").Value.Value == false, $"line off={lineOff}");
            pumpSwitch.IsOn = true;
            await Settle(400);
        }

        // the temperature tile: warning tick at the module's value, its fan as the second figure, both in the accessible name
        var tile = ModulePage.LastBlock?.TileList.FirstOrDefault(t => t.Row.Metric == WellKnownMetrics.CpuTemp);
        var name = tile == null ? "" : Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(tile);
        R("0.1.3: a tile carries the warning tick (Warn = 85 of 0 to 100) and the second figure", tile is { WarnValue: 85 } && Math.Abs((tile.WarnShare ?? 0) - 0.85) < 1e-9
            && tile.SecondaryText.StartsWith("fan ", StringComparison.Ordinal) && tile.SecondaryText.EndsWith(" rpm", StringComparison.Ordinal) && name.Contains("warning at 85 °C") && name.Contains(tile.SecondaryText),
            $"warn={tile?.WarnValue} share={tile?.WarnShare} second='{tile?.SecondaryText}' name='{name}'");
        R("0.1.3: a reference is never a tile", ModulePage.LastBlock?.TileList.All(t => t.Row.ChartRole == ChartRole.Series) == true, "PL1 has no tile");

        // a button that asks first: "no" runs nothing, the question names the expander it sits in
        var deletes = r.Drivers.Where(d => d.Row.HeaderKey == "row.delete-curve.header").ToList();
        ApprovalPrompt.TestAnswer = false;
        int before = page.EnumerateRows().OfType<ExpanderRow>().Count(x => Rigbay.Abstractions.Text.IsLiteral(x.HeaderKey));
        if (deletes.Count > 0) await deletes[0].Edit(null);
        await Settle(600);
        int after = fan.Page!.EnumerateRows().OfType<ExpanderRow>().Count(x => Rigbay.Abstractions.Text.IsLiteral(x.HeaderKey));
        R("0.1.3: a ConfirmKey button asks first, names its object, and a no runs nothing", deletes.Count == 2 && RowRenderer.LastConfirmQuestion == "Delete Quiet nights?" && after == before && deletes[0].Status() == Host.T("row.cancelled"),
            $"buttons={deletes.Count} question='{RowRenderer.LastConfirmQuestion}' curves {before} -> {after} status='{(deletes.Count > 0 ? deletes[0].Status() : "")}'");
        ApprovalPrompt.TestAnswer = false;
    }

    /// <summary>Gives the segmented control less room than it needs: it must show the drop-down; given the room back, the segments again.</summary>
    static bool Narrow(ChoiceSelector choice)
    {
        choice.Measure(new Windows.Foundation.Size(choice.SegmentsWidth - 40, 200));
        bool combo = !choice.ShowsSegments && choice.Combo.Visibility == Visibility.Visible;
        choice.Measure(new Windows.Foundation.Size(choice.SegmentsWidth + 40, 200));
        bool back = choice.ShowsSegments && choice.Combo.Visibility == Visibility.Collapsed;
        choice.InvalidateMeasure();
        return combo && back;
    }

    static int IndexIn(DependencyObject root, DependencyObject target)
    {
        int i = 0;
        foreach (var x in Find<DependencyObject>(root)) { if (ReferenceEquals(x, target)) return i; i++; }
        return int.MaxValue;
    }

    // ================================================================ brand (A11)

    static async Task BrandChecks(MainWindow w)
    {
        var core = Host.Core;
        var tray = Host.Tray!;
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");

        // --- files and the pure choices
        R("brand: app icon and tray files shipped next to the exe", new[] { "rigbay.ico", "rigbay-tray-light.ico", "rigbay-tray-dark.ico", "rigbay-tray-neutral.ico", "rigbay-hc-black.ico", "rigbay-hc-white.ico" }.All(f => File.Exists(Path.Combine(assets, f))), assets);
        bool allLoad = true; var bad = new List<string>();
        foreach (var look in Enum.GetValues<TrayLook>())
            foreach (var size in new[] { 16, 20, 24, 32 })
            {
                var h = Native.LoadTrayIcon(Path.Combine(assets, TrayHost.FileFor(look)), size);
                if (h == 0) { allLoad = false; bad.Add($"{look}@{size}"); } else Native.DestroyIcon(h);
            }
        R("brand: every tray icon loads at 16, 20, 24 and 32 px", allLoad, bad.Count == 0 ? "LoadImage returned a handle for 5 files x 4 sizes" : string.Join(", ", bad));
        R("tray: icon by taskbar theme (pure choice)",
            TrayHost.Pick(false, false, 1) == TrayLook.Light && TrayHost.Pick(false, false, 0) == TrayLook.Dark && TrayHost.Pick(false, false, null) == TrayLook.Neutral
            && TrayHost.Pick(true, true, 0) == TrayLook.HighContrastBlack && TrayHost.Pick(true, false, 1) == TrayLook.HighContrastWhite,
            "SystemUsesLightTheme 1 -> light, 0 -> dark, missing -> neutral; high contrast wins, light theme -> black icon");
        var detected = TrayHost.Detect();
        R("tray: the real taskbar theme was read", tray.Look == detected, $"detected={detected} (file {TrayHost.FileFor(detected)}), on the icon now={tray.Look}@{tray.IconSize}px");

        // --- the tray follows a change at run time (the real registry is not touched: the look is forced, then the message is sent)
        int swaps = tray.Swaps;
        var results = new List<string>();
        bool swapsOk = true;
        foreach (var look in new[] { TrayLook.Dark, TrayLook.Light, TrayLook.HighContrastBlack, TrayLook.HighContrastWhite, TrayLook.Neutral })
        {
            tray.ForcedLook = look;
            tray.Refresh("self-test");
            bool ok = tray.Look == look && tray.LastSwapAccepted;
            swapsOk &= ok;
            results.Add($"{look}{(ok ? "" : "!")}");
        }
        R("tray: icon swaps for dark, light, both high contrast icons and neutral; the shell accepts each", swapsOk && tray.Swaps >= swaps + 4, string.Join(" ", results) + $" swaps {swaps}->{tray.Swaps}");
        tray.ForcedLook = TrayLook.Light;
        int changes = w.SettingChanges;
        var text = System.Runtime.InteropServices.Marshal.StringToHGlobalUni("ImmersiveColorSet");
        var other = System.Runtime.InteropServices.Marshal.StringToHGlobalUni("intl");
        Native.SendMessage(w.Hwnd, Native.WM_SETTINGCHANGE, 0, other);
        Native.SendMessage(w.Hwnd, Native.WM_SETTINGCHANGE, 0, text);
        await Settle(900);
        System.Runtime.InteropServices.Marshal.FreeHGlobal(text);
        System.Runtime.InteropServices.Marshal.FreeHGlobal(other);
        R("tray: WM_SETTINGCHANGE 'ImmersiveColorSet' is followed, other settings are ignored", w.SettingChanges == changes + 1 && tray.Look == TrayLook.Light, $"handled {w.SettingChanges - changes} of 2; look now {tray.Look}");
        tray.ForcedLook = null;
        tray.Refresh("self-test: back to the system value");
        R("tray: back on the system's own theme", tray.Look == detected && tray.IsAdded, $"{tray.Look}@{tray.IconSize}px");

        // --- the title bar mark
        var markRoot = (FrameworkElement)((FrameworkElement)w.Content).FindName("Mark");
        var paths = Find<Microsoft.UI.Xaml.Shapes.Path>(markRoot).ToList();
        var bounds = paths.Select(p => p.Data?.Bounds ?? default).ToList();
        R("title bar: the Dock mark's path data parsed into geometry", paths.Count == 4 && bounds.All(b => b.Width > 70) && bounds.Any(b => Math.Abs(b.Width - 208) < 1 && Math.Abs(b.Height - 164) < 1) && bounds.Any(b => Math.Abs(b.Width - 80) < 1 && Math.Abs(b.Height - 144) < 1),
            $"{paths.Count} paths, bounds " + string.Join(" ", bounds.Select(b => $"{b.Width:0}x{b.Height:0}")));
        R("title bar: the mark is 20 x 20 DIP", Math.Abs(markRoot.ActualWidth - 20) < 0.5 && Math.Abs(markRoot.ActualHeight - 20) < 0.5, $"{markRoot.ActualWidth}x{markRoot.ActualHeight}");
        bool rule = new[] { ("0078D4", false), ("7A5AF8", false), ("0063B1", false), ("881798", false), ("E3008C", false), ("7A7574", false), ("107C10", false), ("10893E", true), ("E81123", true), ("FFB900", true), ("F7630C", true), ("00CC6A", true) }
            .All(c => BrandMark.TryParseColor(c.Item1, out var col) && BrandMark.NeedsQuietForm(col) == c.Item2);
        R("title bar: quiet form exactly when the accent hue is within 30 degrees of green, red or amber (greys never)", rule, "Windows blue, brand violet, purple, pink, grey, and 107C10 (hue 120, 34 degrees from the state green) -> normal; 10893E, red, gold, orange, spring green -> quiet");
        R("title bar: the form shown matches the accent", w.MarkIsQuiet == BrandMark.NeedsQuietForm(w.MarkAccent), $"accent #{w.MarkAccent.R:X2}{w.MarkAccent.G:X2}{w.MarkAccent.B:X2} quiet={w.MarkIsQuiet}");

        // --- module icons
        var fan = core.Find("demo-fan")!;
        var lighting = core.Find("demo-lighting")!;
        var future = core.Find("demo-future")!;
        var devices = core.Find("demo-devices")!;
        var throwUi = core.Find("demo-throw-ui");
        R("module icons: Core resolved the declared and the conventional files",
            fan.Icons.Tile?.EndsWith("icon.svg") == true && fan.Icons.Glyph != null && fan.Icons.Unsupported != null && fan.Icons.Png.Count == 6
            && devices.Manifest!.Icon == null && devices.Icons.Tile?.EndsWith("icon.svg") == true && devices.Icons.Glyph != null
            && future.Icons.Tile != null && future.Icons.Unsupported == null && lighting.Icons.Unsupported != null,
            $"fan tile+glyph+grey+{fan.Icons.Png.Count} png; devices by convention; future only icon.svg; throw-ui icon={throwUi?.Icons.HasIcon}");
        R("module icons: the host has a neutral tile for a module with no icon", ModuleIconUi.Neutral.Tile != null && ModuleIconUi.Neutral.Glyph != null && (throwUi == null || !throwUi.Icons.HasIcon), $"neutral={Path.GetFileName(ModuleIconUi.Neutral.Tile)}");

        int opened = ModuleIconUi.Opened, failures = ModuleIconUi.Failures;
        w.Navigate("dashboard");
        await Settle(1500);
        var images = Find<Image>(w.PageContent).Where(i => i.Width == 24).ToList();
        R("dashboard: every row has a 24 DIP tile", images.Count == core.Modules.Count && images.All(i => i.Source != null), $"{images.Count} tiles for {core.Modules.Count} rows");
        R("dashboard: the tiles opened (SVG renders in WinUI here)", ModuleIconUi.Opened - opened >= core.Modules.Count - 1 && !ModuleIconUi.SvgBroken, $"{ModuleIconUi.Opened - opened} opened, {ModuleIconUi.Failures - failures} failed, SvgBroken={ModuleIconUi.SvgBroken}");
        R("dashboard: a broken icon file falls back to the neutral tile and is logged; nothing throws", ModuleIconUi.Failures > failures && Host.Core.Log.Recent().Any(e => e.Message.Contains("icon of demo-broken")), $"{ModuleIconUi.Failures - failures} failure(s) handled; log has 'icon of demo-broken'");
        var byModule = core.Modules.Zip(images, (m, i) => (m, i)).ToDictionary(x => x.m.Id, x => x.i);
        R("dashboard: unsupported modules are grey, an incompatible one without a grey file is drawn at 45 %",
            ModuleIconUi.IsUnsupported(lighting) && Math.Abs(byModule["demo-lighting"].Opacity - 1.0) < 1e-6 && ModuleIconUi.IsUnsupported(future) && Math.Abs(byModule["demo-future"].Opacity - 0.45) < 1e-6 && Math.Abs(byModule["demo-fan"].Opacity - 1.0) < 1e-6,
            $"lighting opacity {byModule["demo-lighting"].Opacity} (grey file), future {byModule["demo-future"].Opacity}, fan {byModule["demo-fan"].Opacity}");
        var navImages = w.NavItems().Select(i => i.Icon).OfType<ImageIcon>().Count();
        R("navigation pane: module items show the quiet glyph", navImages >= 3, $"{navImages} ImageIcon items");
        w.Navigate(fan);
        await Settle(700);
        var header = Find<Image>(w.PageContent).FirstOrDefault(i => i.Width == 48);
        R("module page: the header shows the 48 DIP tile", header?.Source != null, "Image 48 DIP in the page header");
        w.Navigate("modules");
        await Settle(900);
        R("modules page: one 32 DIP tile per module", Find<Image>(w.PageContent).Count(i => i.Width == 32) == core.Modules.Count, $"{Find<Image>(w.PageContent).Count(i => i.Width == 32)} tiles");
        w.Navigate("dashboard");
        await Settle(300);
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

        int shown = Host.Toast.Shown, balloons = Host.Tray!.BalloonsDisplayed;
        string mode = Host.Toast.UsesBalloon ? "tray balloon (the process is elevated, so AppNotificationManager is unsupported)" : "AppNotificationManager";
        Host.Toast.Show("Rigbay self-test", "Notification from the elevated app");
        R("toast: the notification is accepted by Windows", Host.Toast.Shown == shown + 1, $"mode={mode}; shown {shown}->{Host.Toast.Shown}; failed={Host.Toast.Failed}; AppNotificationManager.Setting={Host.Toast.Setting ?? "(not read)"}");
        // Windows itself says when it put the balloon on screen (NIN_BALLOONSHOW to the icon's callback). It does not say so while
        // Focus assist / Do not disturb holds notifications back, which is the one case this check cannot tell from a fault.
        for (int i = 0; i < 24 && Host.Tray.BalloonsDisplayed == balloons; i++) await Settle(250);
        R("toast: Windows reported the notification as displayed", !Host.Toast.UsesBalloon || Host.Tray.BalloonsDisplayed > balloons, $"NIN_BALLOONSHOW received {Host.Tray.BalloonsDisplayed - balloons}x within 6 s; if 0, Focus assist / Do not disturb may be holding it back");

        // The in-window half of the notification rule (docs/APP.md): a message at the top of the window, closing by itself.
        Host.Window.InAppNotice.Show("demo-fan", "Applied", "Message inside the window (self-test)", ToastKind.Success);
        await Settle(500);
        R("notice: a message appears at the top of the window", Host.Window.InAppNotice.IsOpen && Host.Window.InAppNotice.Last!.Contains("Message inside the window"), $"open={Host.Window.InAppNotice.IsOpen}, last='{Host.Window.InAppNotice.Last}'");
        if (w.Opt.SelfTestOut is { } outPath)
        {
            var png = Path.ChangeExtension(outPath, ".notice.png");
            try { w.Shoot(png); R("notice: the window renders with the message (own window, PrintWindow)", File.Exists(png) && new FileInfo(png).Length > 1000, png); }
            catch (Exception ex) { R("notice: the window renders with the message (own window, PrintWindow)", false, ex.Message); }
        }
        R("notice: the window-in-use flag is tracked", Host.Core.WindowInUse is not null, $"inUse now={Host.Core.WindowInUse?.Invoke()}");

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
        if (Host.Options.AllowMulti) { Skip("command: forwarded to the running instance over the pipe", SiblingReason); return; }
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
        R("language: host strings switch without restart", dash == "Tổng quan", $"Dashboard item reads '{dash}'");
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
        bool sentence = Find<TextBlock>(w.PageContent).Any(t => t.Text == "Battery guard (demo) stopped while starting.");
        bool details = Find<SettingsExpander>(w.PageContent).Any(x => x.Items.OfType<SettingsCard>().Any(c => c.Content is TextBlock { IsTextSelectionEnabled: true } t && t.Text.Contains("battery controller")));
        bool retry = Find<Button>(w.PageContent).Any(b => b.Content as string == Host.T("page.failed-retry"));
        R("fault: the page of a failed module says what happened in one sentence, offers Restart and try again, and keeps the error under Details", sentence && details && retry,
            $"sentence={sentence} details={details} retry={retry}");

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

    // ================================================================ install, update, remove (R1)

    static string NameOf(Button b) => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(b);

    static Button? FindButton(MainWindow w, string automationName) =>
        Find<Button>(w.PageContent).FirstOrDefault(b => NameOf(b) == automationName);

    /// <summary>Presses a button the way assistive technology does (the control's own click path), not by calling the handler's code.</summary>
    static bool Press(Button b)
    {
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(b);
        if (peer?.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke) is not Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider invoke) return false;
        invoke.Invoke();
        return true;
    }

    static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 20000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 100)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    /// <summary>
    /// Runs only with <c>--registry</c> (tools\selftest.ps1 builds a scratch modules folder and a local registry and passes it): the real Modules page, real
    /// controls pressed through automation, the real downloader on a <c>file:</c> source, and the real admin-only permissions on the scratch folders
    /// (this process is elevated). The restart itself is not done here (tools\restart-test.ps1 does it with two real processes).
    /// </summary>
    static async Task InstallChecks(MainWindow w)
    {
        if (!InstallServices.LocalRegistry) { R("install: skipped, no --registry (tools\\selftest.ps1 passes one)", true); return; }
        var m = InstallServices.Manager;
        var core = Host.Core;
        var paths = InstallServices.Paths;
        var security = new AdminFolderSecurity();
        const string Installable = "demo-extra", InstallableName = "Extra lighting (demo)";

        // the list: from the local registry, with every honest state
        await m.CheckAsync();
        var rows = m.AvailableRows().ToDictionary(r => r.Module.Id);
        R("install: the registry was read", m.Snapshot.Status == RegistryStatus.Fresh && rows.Count >= 5, $"status={m.Snapshot.Status} modules={string.Join(",", rows.Keys)}");
        R("install: states per module", rows[Installable].State == AvailableState.Installable && rows["demo-devices"].State == AvailableState.Installed && rows["demo-next"].State == AvailableState.Incompatible,
            $"{Installable}={rows[Installable].State} demo-devices={rows["demo-devices"].State} demo-next={rows["demo-next"].State}");
        var fan = core.Find("demo-fan")!;
        R("install: update available on an installed module", fan.Pending == PendingChange.UpdateAvailable && fan.PendingVersion is { } pv && pv != fan.VersionText(), $"demo-fan {fan.VersionText()} -> {fan.PendingVersion}");

        w.Navigate("modules");
        await Settle(700);
        var installButton = FindButton(w, "Install " + InstallableName);
        var buttonNames = string.Join(" | ", Find<Button>(w.PageContent).Select(NameOf).Where(n => n.Length > 0));
        R("install page: Install button for an installable module, none for installed or incompatible ones",
            installButton != null && FindButton(w, "Install Device list (demo)") == null && FindButton(w, "Install Keyboard lighting (next API)") == null, $"buttons: {buttonNames}");
        // E2E1: the Installed rows had no accessible name (their header is a stack of icon and text), so assistive technology could not tell them apart
        var installedRows = Find<CommunityToolkit.WinUI.Controls.SettingsExpander>(w.PageContent).Where(x => x.Tag is string t && core.Find(t) != null).ToList();
        var unnamed = installedRows.Where(x => string.IsNullOrWhiteSpace(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(x))).Select(x => (string)x.Tag).ToList();
        R("install page: every Installed row has an accessible name", installedRows.Count >= 3 && unnamed.Count == 0, $"installed rows={installedRows.Count} without a name={string.Join(",", unnamed)}");

        // --- cancel: press Install on the module with the bad hash, then Cancel while it downloads (the scratch source is slow on purpose)
        var tampered = FindButton(w, "Install Keyboard lighting (bad hash)");
        bool pressed = tampered != null && Press(tampered);
        bool downloading = await WaitFor(() => m.Operation is { Id: "demo-tampered", Phase: InstallPhase.Downloading });
        await Settle(250);
        var bar = Find<ProgressBar>(w.PageContent).FirstOrDefault();
        var cancel = FindButton(w, "Cancel Keyboard lighting (bad hash)");
        R("install page: progress and a Cancel button while downloading", pressed && downloading && bar != null && cancel != null, $"pressed={pressed} downloading={downloading} progress bar={bar != null} cancel button={cancel != null}");
        // E2E-08: the row keeps its whole description while it downloads; the progress has its own line (in the description, not beside the switch)
        var desc = m.Snapshot.Document?.Find("demo-tampered")?.Description ?? "";
        var descBlock = Find<TextBlock>(w.PageContent).FirstOrDefault(t => desc.Length > 0 && t.Text == desc && (Ancestors(t).FirstOrDefault() as FrameworkElement)?.Tag as string == "available-description:demo-tampered");
        bool progressUnderDescription = bar != null && descBlock != null && (Ancestors(descBlock).FirstOrDefault() as StackPanel)?.Tag as string == "available-description:demo-tampered" && Find<ProgressBar>(Ancestors(descBlock).First()).Any();
        R("install page: a downloading row keeps its whole description, no cut and no tooltip needed",
            descBlock != null && descBlock.TextWrapping == TextWrapping.Wrap && descBlock.TextTrimming == TextTrimming.None && !descBlock.IsTextTrimmed && progressUnderDescription,
            $"description found={descBlock != null} wrap={descBlock?.TextWrapping} trimming={descBlock?.TextTrimming} trimmed={descBlock?.IsTextTrimmed} progress under it={progressUnderDescription}; bar chain: {(bar == null ? "-" : string.Join(" > ", Ancestors(bar).Take(8).Select(a => a.GetType().Name)))}; desc chain: {(descBlock == null ? "-" : string.Join(" > ", Ancestors(descBlock).Take(8).Select(a => a.GetType().Name + ((a as FrameworkElement)?.Tag is string tg ? "[" + tg + "]" : ""))))}");
        if (cancel != null) Press(cancel);
        bool stopped = await WaitFor(() => m.Operation is null);
        int leftovers = Directory.GetFileSystemEntries(paths.Downloads).Length + Directory.GetFileSystemEntries(paths.Staging).Length + Directory.GetFileSystemEntries(paths.Ready).Length;
        R("install: cancel leaves nothing behind", stopped && m.FailureOf("demo-tampered") is null && m.PendingOps.Count == 0 && leftovers == 0, $"stopped={stopped} failure={m.FailureOf("demo-tampered")?.Code} pending={m.PendingOps.Count} leftover entries={leftovers}");

        // --- wrong hash: the failure is said in the row, nothing is installed
        await Settle(500);
        tampered = FindButton(w, "Install Keyboard lighting (bad hash)");
        if (tampered != null) Press(tampered);
        bool failed = await WaitFor(() => m.FailureOf("demo-tampered") != null);
        await Settle(800);
        var failure = m.FailureOf("demo-tampered");
        var pageTexts = Find<TextBlock>(w.PageContent).Select(t => t.Text).ToList();
        R("install: a download that does not match its SHA-256 is refused and said so",
            failed && failure!.Code == InstallErrorCode.HashMismatch && pageTexts.Any(t => t.Contains("SHA-256")) && m.PendingOps.Count == 0 && !Directory.Exists(Path.Combine(AppPaths.ModulesRoot, "demo-tampered")),
            $"code={failure?.Code} detail='{failure?.Detail}' row text shown={pageTexts.Any(t => t.Contains("SHA-256"))}");

        // --- the real install: press Install, the module is staged, the modules folder is untouched, the page says restart required
        await Settle(300);
        installButton = FindButton(w, "Install " + InstallableName);
        pressed = installButton != null && Press(installButton);
        bool queued = await WaitFor(() => m.Operation is null && m.PendingOps.Any(p => p.Id == Installable));
        await Settle(900);
        var op = m.PendingOps.FirstOrDefault(p => p.Id == Installable);
        R("install: Install downloads, verifies and queues for the restart",
            pressed && queued && op is { Op: PendingKinds.Install } && File.Exists(Path.Combine(paths.ReadyFolder(Installable), "module.json")) && !Directory.Exists(Path.Combine(AppPaths.ModulesRoot, Installable)),
            $"pressed={pressed} queued={queued} op={op?.Op} {op?.Version} ready exists={Directory.Exists(paths.ReadyFolder(Installable))} in modules folder={Directory.Exists(Path.Combine(AppPaths.ModulesRoot, Installable))}");
        var infoBars = Find<InfoBar>(w.PageContent).ToList();
        bool badge = w.NavItems().Any(i => Equals(i.Tag, "modules") && i.InfoBadge != null);
        R("install page: 'Restart required' with a Restart now button, and a dot on Modules in the pane",
            infoBars.Any(b => b.Title == Host.T("modules.restart-title") && b.ActionButton is Button { IsEnabled: true }) && badge, $"info bars: {string.Join(" | ", infoBars.Select(b => b.Title))}; pane badge={badge}");
        R("install page: the queued module reads 'Appears after the restart'", Find<TextBlock>(w.PageContent).Any(t => t.Text == Host.T("modules.queued-install")), "");

        // --- update of a module that is running: queued; applying it now (nothing restarted) must not touch its files
        var fanFolder = fan.Folder;
        var fanBefore = FileOps.TreeHash(fanFolder);
        bool updated = await m.UpdateAsync("demo-fan");
        R("install: update of an installed module is queued, its files untouched",
            updated && m.PendingOps.Any(p => p.Id == "demo-fan" && p.Op == PendingKinds.Update) && FileOps.TreeHash(fanFolder) == fanBefore && fan.Pending == PendingChange.RestartRequired,
            $"updated={updated} pending={fan.Pending} tree unchanged={FileOps.TreeHash(fanFolder) == fanBefore}");

        // --- what the next start does, run now in this process: demo-extra is put in place; demo-fan is running (its DLL is loaded), so its swap is refused and kept
        var report = await Task.Run(() => PendingRunner.Run(paths, security, AppPaths.DataRoot, line => AppLog.Info("install (self-test): " + line)));
        var extra = report.Outcomes.FirstOrDefault(o => o.Id == Installable);
        var fanOutcome = report.Outcomes.FirstOrDefault(o => o.Id == "demo-fan");
        var placed = ManifestReader.ReadFolder(Path.Combine(AppPaths.ModulesRoot, Installable)).Manifest;
        R("install: the start-up swap puts the new module in place", extra is { Applied: true } && placed?.Id == Installable, $"extra applied={extra?.Applied} message={extra?.Message}; placed manifest={placed?.Id} {placed?.Version}");
        R("install: a module whose files are loaded is never overwritten (kept for the next start)",
            fanOutcome is { Applied: false, WillRetry: true } && FileOps.TreeHash(fanFolder) == fanBefore && m.PendingOps.Any(p => p.Id == "demo-fan"),
            $"outcome applied={fanOutcome?.Applied} retry={fanOutcome?.WillRetry} message={fanOutcome?.Message}; folder unchanged={FileOps.TreeHash(fanFolder) == fanBefore}");
        bool undone = await m.UndoAsync("demo-fan");
        R("install: undo takes a queued change back", undone && !m.PendingOps.Any(p => p.Id == "demo-fan") && !Directory.Exists(paths.ReadyFolder("demo-fan")) && fan.Pending != PendingChange.RestartRequired, $"undone={undone} pending={fan.Pending}");

        // --- the real permissions on the scratch folders, read back from the disk
        string? modulesAudit = AdminFolderSecurity.Audit(paths.ModulesRoot), installAudit = AdminFolderSecurity.Audit(paths.Root);
        string? moduleWriters = AdminFolderSecurity.FindWriters(Path.Combine(AppPaths.ModulesRoot, Installable));
        R("install: the modules folder, the install folder and the installed module are writable only by Administrators and SYSTEM",
            modulesAudit is null && installAudit is null && moduleWriters is null, $"modules: {modulesAudit ?? "ok"}; install: {installAudit ?? "ok"}; installed module: {moduleWriters ?? "ok"}");

        // --- removal
        var devices = core.Find("demo-devices")!;
        bool removed = await m.RemoveAsync("demo-devices");
        R("install: remove is queued and the module keeps its files until the restart",
            removed && m.PendingOps.Any(p => p.Id == "demo-devices" && p.Op == PendingKinds.Remove) && Directory.Exists(devices.Folder) && devices.Pending == PendingChange.RestartRequired,
            $"removed={removed} pending={devices.Pending} folder exists={Directory.Exists(devices.Folder)}");
        // E2E-07: with the removal queued the row cannot show an enabled, on switch; Undo gives the real state back
        w.RefreshPageKeepingView(); await Settle(500);
        var devicesRow = Find<CommunityToolkit.WinUI.Controls.SettingsExpander>(w.PageContent).FirstOrDefault(x => (x.Tag as string) == "demo-devices");
        var queuedSwitch = Find<ToggleSwitch>(devicesRow).FirstOrDefault();
        var queuedText = string.Join(" | ", Find<TextBlock>(devicesRow).Select(t => t.Text));
        R("install page: a row with a removal queued has its switch off and disabled, and says it is removed after the restart",
            queuedSwitch != null && !queuedSwitch.IsOn && !queuedSwitch.IsEnabled && queuedText.Contains(Host.T("modules.queued-remove")),
            $"switch found={queuedSwitch != null} on={queuedSwitch?.IsOn} enabled={queuedSwitch?.IsEnabled}; text: {queuedText}");
        await m.UndoAsync("demo-devices");
        w.RefreshPageKeepingView(); await Settle(500);
        devicesRow = Find<CommunityToolkit.WinUI.Controls.SettingsExpander>(w.PageContent).FirstOrDefault(x => (x.Tag as string) == "demo-devices");
        var backSwitch = Find<ToggleSwitch>(devicesRow).FirstOrDefault();
        R("install page: Undo of the removal gives the switch back its real state",
            backSwitch != null && backSwitch.IsEnabled && backSwitch.IsOn == (devices.Lifecycle == ModuleLifecycle.Running),
            $"switch found={backSwitch != null} on={backSwitch?.IsOn} enabled={backSwitch?.IsEnabled}; module is {devices.Lifecycle}");

        w.Navigate("dashboard");
        await Settle(300);
    }

    // ================================================================ visual tree helpers

    static IEnumerable<DependencyObject> Ancestors(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p)) yield return p;
    }

    internal static IEnumerable<T> Find<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) yield break;
        if (root is T t) yield return t;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            foreach (var x in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return x;
    }
}

using System.Diagnostics;
using System.Runtime;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Rigbay.App.Services;

namespace Rigbay.App;

/// <summary>
/// Memory in the tray. While the window is hidden the page already costs no CPU (<see cref="PageScope"/> lets go of its subscriptions and timers);
/// this part gives the memory back too: <see cref="TrayTrimPolicy.Delay"/> after the window went to the tray the working set is trimmed once
/// (<see cref="TrimSteps.Default"/>). The other steps (drop the open page, clear UI caches, collect the heap) exist for measurement only: on
/// 2026-11-13 they saved nothing and made the next show slower (docs/perf/TRAY-MEMORY-2026-11.md). When the page was dropped, showing the window
/// rebuilds it and gives scroll, open expanders and focus back. Data stays: page models, the metrics history, the search index.
/// The steps are listed in <see cref="TrimSteps"/>; <c>--trim-steps</c> turns single ones off so <c>tools\perf\measure.ps1</c> can measure each.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>When to trim (the setting, the delay, the clock); see <see cref="TrayTrimPolicy"/>.</summary>
    internal readonly TrayTrimPolicy TrimPolicy = new();

    /// <summary>The steps the next trim runs: <c>--trim-steps</c>, else <see cref="TrimSteps.Default"/> (the self-test sets it to run all of them).</summary>
    internal TrimSteps TrimStepsInUse { get; set; }

    DispatcherQueueTimer? _trimTimer, _scrollAgainTimer;
    bool _trimmed;
    ViewState? _viewAtHide;
    long _restoreStart;
    bool _restoreWasTrimmed;

    /// <summary>True while the page of the window is dropped (the window is hidden and the trim ran).</summary>
    internal bool PageTrimmed => _trimmed;
    /// <summary>What the last trim did; null before the first.</summary>
    internal TrimReport? LastTrim { get; private set; }
    /// <summary>Milliseconds from "show" to the first frame drawn after it; -1 until the first show after a hide.</summary>
    internal double LastRestoreMs { get; private set; } = -1;
    /// <summary>Milliseconds the page rebuild took in the last restore.</summary>
    internal double LastRestoreBuildMs { get; private set; } = -1;
    /// <summary>Weak references to the app's own elements of the page that was dropped last (see <see cref="TrimReport"/>).</summary>
    List<WeakReference> _dropped = new();

    /// <summary>What one trim did. The element counts are how many of the page's own elements (the ones this app defines: they stay alive as long as WinUI refers to them) are still alive after the collection.</summary>
    internal sealed record TrimReport(TrimSteps Steps, bool PageDropped, int PageElements, int PageElementsAlive, bool PageRootAlive, MemSnapshot Before, MemSnapshot After, double Ms);

    // ---------------------------------------------------------------- the setting

    /// <summary>Reads the two settings (and the test switches) into the policy. Called at start and when Settings changes them.</summary>
    internal void ApplyTrimSettings()
    {
        TrimPolicy.Enabled = AppState.Settings.ReleaseMemoryInTray;
        int seconds = _opt.TrimDelaySeconds > 0 ? _opt.TrimDelaySeconds : AppState.Settings.ReleaseMemoryDelaySeconds;
        TrimPolicy.Delay = TimeSpan.FromSeconds(seconds);
        if (TrimPolicy.IsHidden) ArmTrim();   // the delay counts from the hide; a new value takes effect for the wait in progress
    }

    // ---------------------------------------------------------------- hide

    /// <summary>What the close button does when the tray icon is there: hide the window, tell the modules, and start the wait for the trim.</summary>
    internal void HideToTray()
    {
        if (_uiBuilt && WindowShown && !_trimmed) _viewAtHide = CaptureView("hide");
        AppWindow.Hide();
        UpdatePageVisibility();
        TrimPolicy.Hidden();
        ArmTrim();
    }

    /// <summary>Starts (or restarts) the one-shot timer for the time the policy says is left; nothing when nothing waits.</summary>
    void ArmTrim()
    {
        if (_trimTimer == null)
        {
            _trimTimer = DispatcherQueue.CreateTimer();
            _trimTimer.IsRepeating = false;
            _trimTimer.Tick += (_, _) => _ = OnTrimTimerAsync();
        }
        _trimTimer.Stop();
        if (TrimPolicy.Remaining() is not { } left) return;
        _trimTimer.Interval = left + TimeSpan.FromMilliseconds(50);
        _trimTimer.Start();
    }

    async Task OnTrimTimerAsync()
    {
        try
        {
            if (!TrimPolicy.TryTake()) { ArmTrim(); return; }   // asked early (a longer delay was chosen meanwhile): wait for the rest
            if (WindowShown) return;                           // never trim a window the user is looking at
            await TrimNowAsync(TrimStepsInUse);
        }
        catch (Exception ex) { AppLog.Error("memory trim failed", ex); }
    }

    // ---------------------------------------------------------------- trim

    /// <summary>
    /// Gives memory back now (UI thread). Safe to call twice; refuses while the window is on screen. Steps, in order: drop the page and what points
    /// into it, let XAML finish letting go (one dispatcher round), collect and compact the heap once, trim the working set once.
    /// </summary>
    internal async Task<TrimReport?> TrimNowAsync(TrimSteps steps)
    {
        if (WindowShown) { AppLog.Info("memory trim: the window is on screen, nothing to do"); return null; }
        var sw = Stopwatch.StartNew();
        var before = MemSnapshot.Take();
        bool dropped = false;
        int elements = 0;
        var weak = new List<WeakReference>();
        if (steps.HasFlag(TrimSteps.DropPages) && _uiBuilt && !_trimmed)
        {
            elements = DropPage(weak);
            dropped = true;
        }
        if (steps.HasFlag(TrimSteps.Caches)) ReleaseUiCaches();
        _dropped = weak;
        if (dropped) await Task.Delay(150);   // the ScrollViewer and the presenter let go of the tree in the layout pass that follows
        if (steps.HasFlag(TrimSteps.Collect))
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();   // the WinRT wrappers of the dropped elements release their native references in finalizers
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        if (steps.HasFlag(TrimSteps.WorkingSet)) Native.TrimWorkingSet();
        int alive = weak.Count(r => r.IsAlive);
        var after = MemSnapshot.Take();
        var report = new TrimReport(steps, dropped, elements, alive, weak.Count > 0 && weak[0].IsAlive, before, after, sw.Elapsed.TotalMilliseconds);
        LastTrim = report;
        AppLog.Info($"TRAYTRIM steps={steps} page_dropped={dropped} page_elements={elements} still_alive={alive} root_alive={report.PageRootAlive} ws {before.WorkingSetMb:0.0}->{after.WorkingSetMb:0.0} MB private {before.PrivateMb:0.0}->{after.PrivateMb:0.0} MB managed {before.ManagedMb:0.0}->{after.ManagedMb:0.0} MB took {report.Ms:0} ms");
        return report;
    }

    /// <summary>Self-test: what happens when a module asks for a new page (the open page is rebuilt, or nothing while the page is dropped).</summary>
    internal void SimulatePageRefresh(Rigbay.Core.ModuleEntry e) => OnPageRefreshed(e);

    /// <summary>Takes the page off the window and lets go of everything the app holds on it. Returns how many of the page's own elements were seen; weak references to them (the page root first) go to <paramref name="weak"/>.</summary>
    int DropPage(List<WeakReference> weak)
    {
        var content = PageHost.Content as DependencyObject;
        int n = 0;
        if (content != null)
        {
            weak.Add(new WeakReference(content));
            // The elements this app defines (ReadingBlock, TrendChart, Sparkline, panels...) are managed objects with a native twin: WinUI's reference
            // keeps them alive, so one that is collected proves the framework let go. A stock control is only a wrapper that may die while the native control lives on.
            foreach (var d in SelfTest.Find<DependencyObject>(content))
            {
                if (d.GetType().Assembly != typeof(MainWindow).Assembly) continue;
                weak.Add(new WeakReference(d));
                n++;
            }
        }
        _scope?.Dispose();
        _scope = null;
        LastRenderer = null;
        PageHost.Content = null;
        _trimmed = true;
        return n + (content != null ? 1 : 0);
    }

    /// <summary>The statics and boxes that pointed into the dropped page (the self-test reads these after a page is built; they are rebuilt with it).</summary>
    void ReleaseUiCaches()
    {
        ModulePage.ForgetBuilt();
        LastRevealed = null;
        Search.ItemsSource = null;   // the result list holds a row of elements per hit
    }

    // ---------------------------------------------------------------- restore

    /// <summary>Rebuilds the page that was open and gives scroll, open expanders and focus back. UI thread, window shown.</summary>
    void RestoreAfterTrim()
    {
        if (!_trimmed) return;
        _trimmed = false;
        var sw = Stopwatch.StartNew();
        ShowCurrent();
        if (_viewAtHide is { } view)
        {
            ApplyView(view, "restore");
            // The readings and the chart of a page fill in a moment after it is built, so the page may be shorter at first than it was: scroll again
            // once they are there (a window that has only just been shown is not scrolled by the user within half a second).
            double target = view.Offset;
            if (target > 1)
            {
                _scrollAgainTimer?.Stop();
                var again = _scrollAgainTimer = DispatcherQueue.CreateTimer();   // kept in a field: a timer only held by a local is collected before it fires
                again.Interval = TimeSpan.FromMilliseconds(500);
                again.IsRepeating = false;
                again.Tick += (_, _) =>
                {
                    double now = PageScroll.VerticalOffset;
                    if (_trimmed || now >= target - 1) { AppLog.Info($"restore: scroll offset {now:0} after 500 ms, nothing to do"); return; }
                    PageScroll.UpdateLayout();
                    PageScroll.ChangeView(null, Math.Min(target, PageScroll.ScrollableHeight), null, disableAnimation: true);
                    AppLog.Info($"restore: scroll offset {now:0} after 500 ms, asked for {Math.Min(target, PageScroll.ScrollableHeight):0} of {PageScroll.ScrollableHeight:0}");
                };
                again.Start();
            }
        }
        LastRestoreBuildMs = sw.Elapsed.TotalMilliseconds;
        _restoreWasTrimmed = true;
        AppLog.Info($"TRAYRESTORE page rebuilt in {LastRestoreBuildMs:0} ms");
    }

    /// <summary>Called from <see cref="ShowFromTray"/> before the window is shown: starts the stopwatch for the first frame.</summary>
    void BeginRestoreTiming()
    {
        _restoreStart = Stopwatch.GetTimestamp();
        _restoreWasTrimmed = _trimmed;
    }

    /// <summary>Called for each frame the compositor draws; the first one after a show ends the timing.</summary>
    void EndRestoreTiming()
    {
        if (_restoreStart == 0) return;
        LastRestoreMs = Stopwatch.GetElapsedTime(_restoreStart).TotalMilliseconds;
        _restoreStart = 0;
        AppLog.Info($"TRAYRESTORE first frame {LastRestoreMs:0} ms after show (page had been dropped: {_restoreWasTrimmed})");
    }

    // ---------------------------------------------------------------- the view of a page: scroll, open expanders, focus

    /// <summary>What the user sees of a page besides its content: where it is scrolled to, which expanders are open and what has the focus.</summary>
    sealed record ViewState(double Offset, FocusKey? Focus, HashSet<string> Open, double Scrollable);

    static string? ExpanderKey(SettingsExpander x) => x.Header as string ?? x.Tag as string;

    ViewState CaptureView(string what)
    {
        var focus = FocusKey.TakePending() ?? FocusKey.Capture(Content?.XamlRoot, PageHost);   // a button that disables itself while it works has already lost focus
        if (focus == null) AppLog.Info($"{what}: nothing had focus");
        var open = SelfTest.Find<SettingsExpander>(PageHost).Where(x => x.IsExpanded && ExpanderKey(x) != null).Select(x => ExpanderKey(x)!).ToHashSet();
        return new ViewState(PageScroll.VerticalOffset, focus, open, PageScroll.ScrollableHeight);
    }

    void ApplyView(ViewState view, string what)
    {
        PageScroll.UpdateLayout();
        // an open list stays open; an expander inside another one only exists once the outer one is open, so look again until nothing new opens
        for (int pass = 0; pass < 4; pass++)
        {
            bool opened = false;
            foreach (var x in SelfTest.Find<SettingsExpander>(PageHost))
                if (!x.IsExpanded && ExpanderKey(x) is { } k && view.Open.Contains(k)) { x.IsExpanded = true; opened = true; }
            PageScroll.UpdateLayout();
            if (!opened) break;
        }
        PageScroll.ChangeView(null, Math.Min(view.Offset, PageScroll.ScrollableHeight), null, disableAnimation: true);
        AppLog.Info($"{what}: scroll offset {view.Offset:0} of {view.Scrollable:0} before, {PageScroll.VerticalOffset:0} of {PageScroll.ScrollableHeight:0} now; {view.Open.Count} expander(s) were open, {SelfTest.Find<SettingsExpander>(PageHost).Count(x => x.IsExpanded)} are");
        if (view.Focus != null)
        {
            var back = view.Focus.Restore(PageHost);
            string name = back == null ? "" : Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(back) is { Length: > 0 } n ? n : (back as Microsoft.UI.Xaml.Controls.ContentControl)?.Content as string ?? "";
            AppLog.Info(back != null ? $"{what}: focus back on {back.GetType().Name} '{name}' ({back.FocusState}){(view.Focus.InPrimary ? ", the primary control" : "")}" : $"{what}: the focused control is gone");
        }
    }
}

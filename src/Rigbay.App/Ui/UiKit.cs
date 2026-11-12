using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Dispatching;

using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rigbay.Abstractions;
using Rigbay.App.Services;
using Rigbay.Core;

namespace Rigbay.App;

/// <summary>The one place the pages reach the application's services (there is exactly one window and one manager).</summary>
static class Host
{
    public static RigbayHost Core { get; set; } = null!;
    public static MainWindow Window { get; set; } = null!;
    public static Options Options { get; set; } = null!;
    public static DispatcherQueue Dq { get; set; } = null!;
    public static ToastHost Toast { get; } = new();
    public static TrayHost? Tray { get; set; }
    public static HotkeyHost? Hotkeys { get; set; }

    public static string T(string key) => Core.Languages.T(key);
    public static string F(string key, params object[] args) => Core.Languages.F(key, args);
}

static class Ui
{
    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread inside the fault boundary of <paramref name="owner"/> (when given): an
    /// exception marks that module faulted instead of reaching the dispatcher, where it would end the process.
    /// </summary>
    public static void Post(ModuleEntry? owner, Action action, string what = "UI update")
    {
        void Run()
        {
            if (owner != null) { owner.Invoke(what, action); return; }
            try { action(); } catch (Exception ex) { AppLog.Error(what + " failed", ex); }
        }
        if (Host.Dq.HasThreadAccess) Run();
        else if (!Host.Dq.TryEnqueue(Run)) AppLog.Warn(what + " dropped: the dispatcher is gone");
    }

    public static TextBlock Text(string text, string? style = null, Windows.UI.Text.FontWeight? weight = null, bool wrap = false, bool trim = false)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (style != null) t.Style = Style(style);
        if (weight != null) t.FontWeight = weight.Value;
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        if (trim) t.TextTrimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    public static void Tabular(TextBlock t) => Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);

    static Style? _compactToggle;
    static bool _compactToggleLoaded;

    /// <summary>
    /// Draws <paramref name="toggle"/> the way a <c>SettingsCard</c> draws its switches (the toolkit's <c>RightAlignedCompactToggleSwitchStyle</c>,
    /// no room for content at its right), so a switch outside a card (the header card's, the primary one) ends at the same x as the switches and
    /// buttons of the rows under it; the stock style leaves 12 px. Without that style the switch keeps the stock one.
    /// </summary>
    public static ToggleSwitch CompactToggle(ToggleSwitch toggle)
    {
        if (!_compactToggleLoaded)
        {
            _compactToggleLoaded = true;
            try
            {
                var d = new ResourceDictionary { Source = new Uri("ms-appx:///CommunityToolkit.WinUI.Controls.SettingsControls/SettingsCard/SettingsCard.xaml") };
                if (d.TryGetValue("RightAlignedCompactToggleSwitchStyle", out var s) && s is Style style) _compactToggle = style;
                else AppLog.Warn("the toolkit's RightAlignedCompactToggleSwitchStyle was not found: switches outside cards keep the stock style");
            }
            catch (Exception ex) { AppLog.Warn("the toolkit's SettingsCard styles could not be loaded: " + ex.Message); }
        }
        if (_compactToggle != null) toggle.Style = _compactToggle;
        return toggle;
    }

    /// <summary>The bay mark for a state: filled green ready, filled red failed, filled amber needs attention, outline otherwise.</summary>
    public static Rectangle Bay(BayState state, double height = 18)
    {
        var key = state switch
        {
            BayState.Ready => "RbBayReady",
            BayState.Failed => "RbBayFailed",
            BayState.UpdateAvailable or BayState.RestartRequired => "RbBayAttention",
            _ => "RbBayOutline",
        };
        return new Rectangle { Style = Style(key), Height = height, VerticalAlignment = VerticalAlignment.Center };
    }

    public static string StateWord(BayState s) => Host.T("state." + s.ToString().ToLowerInvariant());

    public static UIElement PageHeader(string title, string? meta, params UIElement[] right) => PageHeader(null, title, meta, right);

    /// <summary>Page header with an optional mark on the left (a module page shows the module's tile there).</summary>
    public static UIElement PageHeader(UIElement? lead, string title, string? meta, params UIElement[] right) =>
        PageHeaderWith(lead, title, string.IsNullOrEmpty(meta) ? null : Text(meta, "RbSecondaryCaption", trim: true), right);

    /// <summary>Page header whose second line is an element (a line with parts in different inks, such as the Dashboard's counts).</summary>
    public static UIElement PageHeaderWith(UIElement? lead, string title, UIElement? meta, params UIElement[] right)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        if (lead != null)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnSpacing = 14;
            Grid.SetColumn((FrameworkElement)lead, 0);
            g.Children.Add(lead);
        }
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int first = lead != null ? 1 : 0;
        var left = new StackPanel { Spacing = 2 };
        var h = new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
        AutomationHeading(h);
        left.Children.Add(h);
        if (meta != null) left.Children.Add(meta);
        Grid.SetColumn(left, first);
        g.Children.Add(left);
        var r = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        foreach (var x in right) r.Children.Add(x);
        Grid.SetColumn(r, first + 1);
        g.Children.Add(r);
        return g;
    }

    static void AutomationHeading(UIElement e) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(e, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);

    /// <summary>A short block that says what is wrong (or empty) and what to do: a bay mark, a title, text, optional action.</summary>
    public static UIElement StateBlock(BayState mark, string title, string body, Button? action = null)
    {
        var grid = new Grid { ColumnSpacing = 14, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var bay = Bay(mark, 44);
        bay.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(bay);
        var text = new StackPanel { Spacing = 6 };
        text.Children.Add(Text(title, weight: FontWeights.SemiBold));
        var b = Text(body, "RbSecondaryText", wrap: true);
        b.IsTextSelectionEnabled = true;
        text.Children.Add(b);
        if (action != null) { action.Margin = new Thickness(0, 6, 0, 0); text.Children.Add(action); }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    public static Button LinkButton(string text, Action click)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => { try { click(); } catch (Exception ex) { AppLog.Error("button handler failed", ex); } };
        return b;
    }

    public static string SeverityWord(Severity s) => s.ToString();
}

/// <summary>
/// Everything a page subscribed to (module values, the metrics bus, language) so that leaving the page lets go of it.
/// <para>A page is also <b>active</b> or not: it is active while the window is on screen and not minimised. Live things (a bus subscription
/// that feeds a number, a redraw timer of a chart) are registered with <see cref="Live"/> and <see cref="Tick"/>; while the page is
/// inactive they are all let go of (no subscription, so modules see no reader and slow down; no timer, nothing is drawn) and they come back, refreshed,
/// when the page is active again. A window hidden in the tray therefore costs nothing for the page that was open.</para>
/// </summary>
sealed class PageScope : IDisposable
{
    sealed class LiveItem
    {
        public required Func<IDisposable> Subscribe;
        public required Action Refresh;
        public IDisposable? Subscription;
    }

    readonly List<Action> _undo = new();
    readonly List<LiveItem> _live = new();
    readonly List<Action> _redraws = new();
    readonly List<PageScope> _children = new();
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    int _drawn;
    long _redrawTicks;
    public bool Disposed { get; private set; }

    /// <summary>True while the page is on screen (window shown, not minimised); see the class text.</summary>
    public bool Active { get; private set; } = true;

    /// <summary>Redraw timer ticks of this scope and its children (see <see cref="Redraws"/>).</summary>
    public int TotalRedraws => _drawn + _children.Sum(c => c.TotalRedraws);

    /// <summary>Live subscriptions of this scope and its children.</summary>
    public int TotalLiveSubscriptions => LiveSubscriptions + _children.Sum(c => c.TotalLiveSubscriptions);

    /// <summary>How many redraws the timer ran for this page; a hidden page does not count up (the self-test reads it).</summary>
    public int Redraws => _drawn;

    /// <summary>Milliseconds spent in this scope's redraws and its children's so far (the CPU check reads it).</summary>
    public double RedrawMs => _redrawTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency + _children.Sum(c => c.RedrawMs);

    /// <summary>Live bus subscriptions held now (0 while inactive).</summary>
    public int LiveSubscriptions => _live.Count(l => l.Subscription != null);

    /// <summary>Whether the redraw timer is running.</summary>
    public bool TimerRunning => _timer?.IsRunning == true;

    /// <summary>Calls <paramref name="apply"/> on the UI thread, inside <paramref name="owner"/>'s boundary, whenever the value changes.</summary>
    public void Bind<T>(IObservableValue<T> value, ModuleEntry? owner, Action<T> apply)
    {
        void Handler() => Ui.Post(owner, () => { if (!Disposed) apply(value.Value); });
        value.Changed += Handler;
        _undo.Add(() => value.Changed -= Handler);
    }

    public void Add(IDisposable d) => _undo.Add(d.Dispose);
    public void Add(Action undo) => _undo.Add(undo);

    /// <summary>
    /// A subscription that exists only while the page is active: <paramref name="subscribe"/> runs now (when active) and again each time the page
    /// becomes active; <paramref name="refresh"/> runs right after a re-subscribe, to show what came in meanwhile.
    /// </summary>
    public void Live(Func<IDisposable> subscribe, Action refresh)
    {
        var item = new LiveItem { Subscribe = subscribe, Refresh = refresh };
        _live.Add(item);
        if (Active && !Disposed) item.Subscription = subscribe();
    }

    /// <summary>How often the charts are drawn again: once per 2-second slot of the history, which is how often a new point can appear (the numbers follow the bus at once).</summary>
    public static readonly TimeSpan RedrawInterval = TimeSpan.FromSeconds(2);

    /// <summary>A redraw that runs every <see cref="RedrawInterval"/> while the page is active (a chart, a sparkline). Draw once yourself when you create it.</summary>
    public void Tick(Action redraw)
    {
        _redraws.Add(redraw);
        if (_timer == null && !Disposed)
        {
            _timer = Host.Dq.CreateTimer();
            _timer.Interval = RedrawInterval;
            _timer.Tick += (_, _) => RunRedraws();
            if (Active) _timer.Start();
        }
    }

    void RunRedraws()
    {
        if (Disposed || !Active) return;
        _drawn++;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var r in _redraws.ToArray())
        {
            try { r(); } catch (Exception ex) { AppLog.Warn("a chart redraw failed: " + ex.Message); }
        }
        _redrawTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>
    /// A scope that lives inside this one: it follows this page's active state and goes with it, and can be disposed alone (a part of the page that is
    /// rebuilt lets go of its subscriptions and timer without waiting for the whole page).
    /// </summary>
    public PageScope CreateChild()
    {
        _children.RemoveAll(c => c.Disposed);
        var child = new PageScope { Active = Active };
        _children.Add(child);
        return child;
    }

    /// <summary>Redraws everything now (the page became active, or the theme or width changed).</summary>
    public void RedrawNow() => RunRedraws();

    /// <summary>Tells the page whether it is on screen. Idempotent; UI thread.</summary>
    public void SetActive(bool active)
    {
        if (Disposed || active == Active) return;
        Active = active;
        foreach (var c in _children) c.SetActive(active);
        if (active)
        {
            foreach (var l in _live) { l.Subscription?.Dispose(); l.Subscription = l.Subscribe(); l.Refresh(); }
            _timer?.Start();
            RunRedraws();
        }
        else
        {
            _timer?.Stop();
            foreach (var l in _live) { l.Subscription?.Dispose(); l.Subscription = null; }
        }
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        _timer?.Stop();
        foreach (var c in _children) c.Dispose();
        _children.Clear();
        foreach (var l in _live) { try { l.Subscription?.Dispose(); } catch { } l.Subscription = null; }
        _live.Clear();
        _redraws.Clear();
        foreach (var u in _undo) { try { u(); } catch { } }
        _undo.Clear();
    }
}

/// <summary>
/// Identifies the focused control by what it is, not where it is, so focus can be given back after a page rebuild. A control inside the page's
/// primary block (<see cref="MarkPrimary"/>) is looked for in the new primary block only, and when it is gone (a module swapped "Start" for "Stop"
/// with a page refresh) the new primary control takes the focus: the user pressed the page's main control, so focus stays on the page's main control.
/// The focus comes back the way it went (keyboard focus keeps its focus rectangle).
/// </summary>
sealed class FocusKey
{
    readonly string _type, _name;
    readonly int _ordinal;
    readonly FocusState _state;
    FocusKey(string type, string name, int ordinal, bool inPrimary, FocusState state) { _type = type; _name = name; _ordinal = ordinal; InPrimary = inPrimary; _state = state; }

    /// <summary>True when the control was inside the page's primary block.</summary>
    public bool InPrimary { get; }

    /// <summary>Test hook: the same key for a control that was named <paramref name="name"/> (what a module's relabelled button looks like).</summary>
    public FocusKey Renamed(string name) => new(_type, name, _ordinal, InPrimary, _state);

    static FocusKey? _pending;
    static long _pendingAt;

    // the page's primary block -> the element that holds its control (the control, the switch with its word, the segmented choice)
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, DependencyObject> Primaries = new();

    /// <summary>Marks <paramref name="host"/> as the page's primary block and <paramref name="main"/> as what holds its control.</summary>
    public static void MarkPrimary(DependencyObject host, DependencyObject main) => Primaries.AddOrUpdate(host, main);

    /// <summary>The primary block on <paramref name="root"/>, or null.</summary>
    public static DependencyObject? FindPrimary(DependencyObject root)
    {
        if (Primaries.TryGetValue(root, out _)) return root;
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            if (FindPrimary(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    static DependencyObject? PrimaryAround(DependencyObject d, DependencyObject pageHost)
    {
        for (var p = d; p != null && !ReferenceEquals(p, pageHost); p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(p))
            if (Primaries.TryGetValue(p, out _)) return p;
        return null;
    }

    /// <summary>The control of the primary block <paramref name="primary"/>: the selected segment of a choice, else the first enabled, visible control.</summary>
    public static Control? PrimaryControl(DependencyObject primary)
    {
        if (!Primaries.TryGetValue(primary, out var main)) return null;
        bool Shown(Control c)
        {
            for (DependencyObject? p = c; p != null; p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(p))
            {
                if (p is UIElement { Visibility: Visibility.Collapsed }) return false;
                if (ReferenceEquals(p, main)) break;
            }
            return c.IsEnabled;
        }
        var all = (main is Control { IsTabStop: true } self ? new[] { self } : Array.Empty<Control>()).Concat(Controls(main)).Where(Shown).ToList();
        return all.FirstOrDefault(c => c is RadioButton { IsChecked: true }) ?? all.FirstOrDefault();
    }

    /// <summary>Remembers the focused control now (a button calls this just before it disables itself); the next page refresh uses it.</summary>
    public static void RememberForRefresh(XamlRoot? root, DependencyObject pageHost)
    {
        _pending = Capture(root, pageHost);
        _pendingAt = Environment.TickCount64;
    }

    /// <summary>Test hook: <paramref name="key"/> is what the next page refresh gives the focus back to.</summary>
    public static void RememberForRefresh(FocusKey key)
    {
        _pending = key;
        _pendingAt = Environment.TickCount64;
    }

    /// <summary>The control remembered by <see cref="RememberForRefresh(XamlRoot?, DependencyObject)"/> if that was less than 10 s ago; clears it.</summary>
    public static FocusKey? TakePending()
    {
        var p = Environment.TickCount64 - _pendingAt < 10_000 ? _pending : null;
        _pending = null;
        return p;
    }

    static string NameOf(DependencyObject d) =>
        AutomationProperties.GetName(d) is { Length: > 0 } n ? n : d is ContentControl { Content: string s } ? s : "";

    static IEnumerable<Control> Controls(DependencyObject root)
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (c is Control { IsTabStop: true } ctl) yield return ctl;
            foreach (var d in Controls(c)) yield return d;
        }
    }

    public static FocusKey? Capture(XamlRoot? root, DependencyObject pageHost)
    {
        if (root == null || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is not Control focused || focused.FocusState == FocusState.Unfocused) return null;
        var type = focused.GetType().Name; var name = NameOf(focused);
        var primary = PrimaryAround(focused, pageHost);
        if (name.Length == 0 && primary == null) return null;
        // the same type and name can repeat (two "Apply" buttons): count the ones before it on the page (in the primary block: in the block)
        int ordinal = 0; bool found = false;
        foreach (var c in Controls(primary ?? pageHost)) { if (ReferenceEquals(c, focused)) { found = true; break; } if (c.GetType().Name == type && NameOf(c) == name) ordinal++; }
        return found ? new FocusKey(type, name, ordinal, primary != null, focused.FocusState) : null;
    }

    /// <summary>Focuses the control this key names on the rebuilt page; returns it, or null when nothing took the focus.</summary>
    public Control? Restore(DependencyObject pageHost)
    {
        var state = _state == FocusState.Unfocused ? FocusState.Programmatic : _state;
        // a control of the primary block is looked for in the new primary block; the rest of the page is searched only when the page has none any more
        var primary = InPrimary ? FindPrimary(pageHost) : null;
        if (_name.Length > 0)
        {
            int seen = 0;
            foreach (var c in Controls(primary ?? pageHost))
            {
                if (c.GetType().Name != _type || NameOf(c) != _name) continue;
                if (seen++ == _ordinal) return c.Focus(state) ? c : null;
            }
        }
        // the primary control was replaced by one with another label (Start -> Stop): the new primary control takes the focus
        if (primary != null && PrimaryControl(primary) is { } main) return main.Focus(state) ? main : null;
        return null;
    }
}

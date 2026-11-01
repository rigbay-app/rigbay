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
    public static UIElement PageHeader(UIElement? lead, string title, string? meta, params UIElement[] right)
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
        if (!string.IsNullOrEmpty(meta)) left.Children.Add(Text(meta, "RbSecondaryCaption", trim: true));
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

/// <summary>Everything a page subscribed to (module values, the metrics bus, language) so that leaving the page lets go of it.</summary>
sealed class PageScope : IDisposable
{
    readonly List<Action> _undo = new();
    public bool Disposed { get; private set; }

    /// <summary>Calls <paramref name="apply"/> on the UI thread, inside <paramref name="owner"/>'s boundary, whenever the value changes.</summary>
    public void Bind<T>(IObservableValue<T> value, ModuleEntry? owner, Action<T> apply)
    {
        void Handler() => Ui.Post(owner, () => { if (!Disposed) apply(value.Value); });
        value.Changed += Handler;
        _undo.Add(() => value.Changed -= Handler);
    }

    public void Add(IDisposable d) => _undo.Add(d.Dispose);
    public void Add(Action undo) => _undo.Add(undo);

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        foreach (var u in _undo) { try { u(); } catch { } }
        _undo.Clear();
    }
}

/// <summary>Identifies the focused control by what it is, not where it is, so focus can be given back after a page rebuild.</summary>
sealed class FocusKey
{
    readonly string _type, _name;
    readonly int _ordinal;
    FocusKey(string type, string name, int ordinal) { _type = type; _name = name; _ordinal = ordinal; }

    static FocusKey? _pending;
    static long _pendingAt;

    /// <summary>Remembers the focused control now (a button calls this just before it disables itself); the next page refresh uses it.</summary>
    public static void RememberForRefresh(XamlRoot? root, DependencyObject pageHost)
    {
        _pending = Capture(root, pageHost);
        _pendingAt = Environment.TickCount64;
    }

    /// <summary>The control remembered by <see cref="RememberForRefresh"/> if that was less than 10 s ago; clears it.</summary>
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
        if (name.Length == 0) return null;
        // the same type and name can repeat (two "Apply" buttons): count the ones before it on the page
        int ordinal = 0; bool found = false;
        foreach (var c in Controls(pageHost)) { if (ReferenceEquals(c, focused)) { found = true; break; } if (c.GetType().Name == type && NameOf(c) == name) ordinal++; }
        return found ? new FocusKey(type, name, ordinal) : null;
    }

    public void Restore(DependencyObject pageHost)
    {
        int seen = 0;
        foreach (var c in Controls(pageHost))
        {
            if (c.GetType().Name != _type || NameOf(c) != _name) continue;
            if (seen++ == _ordinal) { c.Focus(FocusState.Programmatic); return; }
        }
    }
}

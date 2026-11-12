using System.Diagnostics;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Rigbay.Abstractions;
using Rigbay.App.Services;
using Rigbay.Core;

namespace Rigbay.App;

/// <summary>
/// The host log (the last 500 entries kept in memory) as a list of lines, for the question "why did it fail" (LG-1): a level filter (all,
/// warnings and errors, errors) and a source filter (the host or one module) above it, the critical or caution glyph on error and warning lines,
/// the time and the source in their own columns. New lines arrive at most every 400 ms; the list follows them while it is scrolled to the end.
/// "Copy" puts the lines shown on the clipboard in the raw format of the log file.
/// </summary>
static class LogPage
{
    public enum Filter { All, Warnings, Errors }

    /// <summary>The filters, kept while the app runs (leaving the page and coming back keeps them).</summary>
    internal static Filter Level = Filter.All;
    internal static string? Source;

    /// <summary>The list on the open Log page (self-test and shots).</summary>
    internal static ListView? LastList { get; private set; }

    /// <summary>The entries the open Log page shows, in order (self-test).</summary>
    internal static IReadOnlyList<LogEntry> Shown => _shown;
    static List<LogEntry> _shown = new();

    public const int MaxLines = 500;

    static bool Matches(LogEntry e) =>
        (Level switch { Filter.Errors => e.Level >= LogLevel.Error, Filter.Warnings => e.Level >= LogLevel.Warn, _ => true })
        && (Source == null || e.Source == Source);

    static string SourceName(string source) => Host.Core.Find(source)?.DisplayName ?? source;

    public static UIElement Build(PageScope scope)
    {
        var page = new StackPanel { Spacing = 10 };

        var folder = Ui.LinkButton(Host.T("settings.open"), () => ModulesPage.OpenFolder(Host.Core.Paths.LogDirectory));
        AutomationProperties.SetName(folder, Host.T("settings.log-folder") + ": " + Host.T("settings.open"));
        var copy = Ui.LinkButton(Host.T("log.copy"), () =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(string.Join(Environment.NewLine, _shown.Select(AppLog.FormatLine)));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        });
        var header = Ui.PageHeader(Host.T("nav.log"), Host.Core.Log.CurrentFile, folder, copy);
        ((FrameworkElement)header).Margin = new Thickness(0, 0, 0, 8);
        page.Children.Add(header);

        // ---------------------------------------------------------------- filters
        var level = new ComboBox { MinWidth = 200 };
        foreach (var (f, key) in new[] { (Filter.All, "log.filter-all"), (Filter.Warnings, "log.filter-warnings"), (Filter.Errors, "log.filter-errors") })
            level.Items.Add(new ComboBoxItem { Content = Host.T(key), Tag = f });
        level.SelectedItem = level.Items.OfType<ComboBoxItem>().First(i => (Filter)i.Tag == Level);
        AutomationProperties.SetName(level, Host.T("log.filter"));

        var source = new ComboBox { MinWidth = 200 };
        void FillSources()
        {
            var known = Host.Core.Log.Recent().Select(e => e.Source).Distinct(StringComparer.Ordinal).OrderBy(s => s == "host" ? "" : SourceName(s), StringComparer.CurrentCultureIgnoreCase).ToList();
            if (Source != null && !known.Contains(Source)) known.Add(Source);
            source.Items.Clear();
            source.Items.Add(new ComboBoxItem { Content = Host.T("log.source-all"), Tag = "" });
            foreach (var s in known) source.Items.Add(new ComboBoxItem { Content = s == "host" ? "Rigbay" : SourceName(s), Tag = s });
            source.SelectedItem = source.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (Source ?? "")) ?? source.Items[0];
        }
        FillSources();
        AutomationProperties.SetName(source, Host.T("log.source"));

        var count = Ui.Text("", "RbSecondaryCaption");
        count.VerticalAlignment = VerticalAlignment.Bottom;
        count.Margin = new Thickness(0, 0, 0, 6);
        var bar = new FlowPanel { HorizontalGap = 12, VerticalGap = 8 };
        bar.Children.Add(Labelled(Host.T("log.filter"), level));
        bar.Children.Add(Labelled(Host.T("log.source"), source));
        bar.Children.Add(count);
        page.Children.Add(bar);

        // ---------------------------------------------------------------- the lines
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Extended, IsItemClickEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 4, 0, 4), MinHeight = 240,
        };
        AutomationProperties.SetName(list, Host.T("nav.log"));
        var empty = Ui.Text(Host.T("log.empty"), "RbSecondaryText", wrap: true);
        empty.Margin = new Thickness(12, 12, 12, 12);
        var listHost = new Grid();
        listHost.Children.Add(list);
        listHost.Children.Add(empty);
        page.Children.Add(new Border { Style = Ui.Style("RbTileFrame"), Child = listHost });   // a frame from a style, so its stroke follows the window's theme
        LastList = list;

        // Ctrl+C on selected lines copies them raw
        var copyKey = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.C, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        copyKey.Invoked += (_, a) =>
        {
            var lines = list.SelectedItems.OfType<FrameworkElement>().Select(x => x.Tag).OfType<LogEntry>().Select(AppLog.FormatLine).ToList();
            if (lines.Count == 0) return;
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(string.Join(Environment.NewLine, lines));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            a.Handled = true;
        };
        list.KeyboardAccelerators.Add(copyKey);
        list.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        int total = 0;
        void ShowCount()
        {
            count.Text = Host.F("log.count", _shown.Count, total);
            empty.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        ScrollViewer? scroller = null;
        bool AtEnd() => scroller == null || scroller.VerticalOffset >= scroller.ScrollableHeight - 24;
        void ScrollToEnd() { if (list.Items.Count > 0) list.ScrollIntoView(list.Items[^1], ScrollIntoViewAlignment.Leading); }

        void Fill()
        {
            var recent = Host.Core.Log.Recent();
            total = recent.Count;
            _shown = recent.Where(Matches).TakeLast(MaxLines).ToList();
            list.Items.Clear();
            foreach (var e in _shown) list.Items.Add(Line(e));
            ShowCount();
            list.UpdateLayout();
            ScrollToEnd();
        }

        LogEntry? lastSeen = null;
        void Append()
        {
            var recent = Host.Core.Log.Recent();
            total = recent.Count;
            // the entries after the last one we drew (the in-memory list is a ring: find our last entry from the end)
            int from = 0;
            if (lastSeen != null)
            {
                from = -1;
                for (int i = recent.Count - 1; i >= 0; i--) if (ReferenceEquals(recent[i], lastSeen)) { from = i + 1; break; }
                // more lines came than the memory keeps: our last one is gone, so draw the list again instead of guessing
                if (from < 0) { Fill(); lastSeen = recent.Count > 0 ? recent[^1] : null; return; }
            }
            bool follow = AtEnd();
            foreach (var e in recent.Skip(from))
            {
                if (!Matches(e)) continue;
                _shown.Add(e);
                list.Items.Add(Line(e));
            }
            while (_shown.Count > MaxLines) { _shown.RemoveAt(0); list.Items.RemoveAt(0); }
            if (recent.Count > 0) lastSeen = recent[^1];
            ShowCount();
            if (follow) ScrollToEnd();
        }

        Fill();
        { var r = Host.Core.Log.Recent(); lastSeen = r.Count > 0 ? r[^1] : null; }
        list.Loaded += (_, _) => { scroller = SelfTest.Find<ScrollViewer>(list).FirstOrDefault(); ScrollToEnd(); };

        level.SelectionChanged += (_, _) =>
        {
            if (level.SelectedItem is ComboBoxItem { Tag: Filter f } && f != Level) { Level = f; Fill(); }
        };
        source.SelectionChanged += (_, _) =>
        {
            if (source.SelectedItem is not ComboBoxItem { Tag: string s }) return;
            var want = s.Length == 0 ? null : s;
            if (want != Source) { Source = want; Fill(); }
        };

        // the list fills the window below the filters
        void Fit()
        {
            double viewport = Host.Window.PageViewportHeight;
            if (viewport <= 0) return;
            double above = ((FrameworkElement)header).ActualHeight + 8 + bar.ActualHeight + page.Spacing * 2;
            list.Height = Math.Max(240, Math.Floor(viewport - above - 24 - 28));
        }
        Action onViewport = () => Fit();
        Host.Window.PageViewportChanged += onViewport;
        bar.SizeChanged += (_, _) => Fit();
        page.Loaded += (_, _) => Fit();

        // Throttle: at most one refresh per 400 ms while the page is open.
        var timer = Host.Dq.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        bool dirty = false;
        Action<LogEntry> onEntry = _ => dirty = true;
        Host.Core.Log.EntryWritten += onEntry;
        timer.Tick += (_, _) => { if (dirty && !scope.Disposed) { dirty = false; Append(); } };
        timer.Start();
        scope.Add(() => { Host.Core.Log.EntryWritten -= onEntry; timer.Stop(); Host.Window.PageViewportChanged -= onViewport; if (ReferenceEquals(LastList, list)) LastList = null; });
        return page;
    }

    static UIElement Labelled(string label, Control control)
    {
        var l = Ui.Text(label, "RbSecondaryCaption");
        AutomationProperties.SetName(control, label);
        return new StackPanel { Spacing = 4, Children = { l, control } };
    }

    // ---------------------------------------------------------------- one line

    static Style? _critical, _caution;

    /// <summary>A glyph style whose colour follows the theme of the element it is on (a brush fetched from code would follow the app's theme instead).</summary>
    static Style GlyphStyle(string brush) => (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
        "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBlock'>" +
        $"<Setter Property='Foreground' Value='{{ThemeResource {brush}}}'/>" +
        "<Setter Property='FontFamily' Value='Segoe Fluent Icons, Segoe MDL2 Assets'/><Setter Property='FontSize' Value='14'/></Style>");

    static ListViewItem Line(LogEntry e)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 3, 0, 3), Tag = e };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        string levelWord = e.Level switch { LogLevel.Error => Host.T("log.line-error"), LogLevel.Warn => Host.T("log.line-warning"), LogLevel.Debug => Host.T("log.line-debug"), _ => Host.T("log.line-info") };   // what a screen reader says first
        if (e.Level >= LogLevel.Warn)
        {
            bool error = e.Level >= LogLevel.Error;
            var glyph = new TextBlock { Text = error ? "" : "", Style = error ? (_critical ??= GlyphStyle("SystemFillColorCriticalBrush")) : (_caution ??= GlyphStyle("SystemFillColorCautionBrush")), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            AutomationProperties.SetAccessibilityView(glyph, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            grid.Children.Add(glyph);
        }

        var time = Ui.Text(e.Time.ToLocalTime().ToString("HH:mm:ss"), "RbSecondaryCaption");
        Ui.Tabular(time);
        time.VerticalAlignment = VerticalAlignment.Top;
        time.Margin = new Thickness(0, 2, 0, 0);
        Grid.SetColumn(time, 1);
        grid.Children.Add(time);

        var src = Ui.Text(e.Source == "host" ? "Rigbay" : SourceName(e.Source), "RbSecondaryCaption", trim: true);
        src.TextWrapping = TextWrapping.NoWrap;
        src.VerticalAlignment = VerticalAlignment.Top;
        src.Margin = new Thickness(0, 2, 0, 0);
        ToolTipService.SetToolTip(src, e.Source);
        Grid.SetColumn(src, 2);
        grid.Children.Add(src);

        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(Ui.Text(e.Message, wrap: true).With(t => t.VerticalAlignment = VerticalAlignment.Top));
        if (e.Exception is { Length: > 0 } ex)
        {
            var first = ex.Split('\n', 2)[0].Trim();
            var detail = Ui.Text(first, "RbSecondaryCaption", wrap: true);
            detail.MaxLines = 2;
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTipService.SetToolTip(detail, ex);
            text.Children.Add(detail);
        }
        Grid.SetColumn(text, 3);
        grid.Children.Add(text);

        var item = new ListViewItem { Content = grid, Tag = e, MinHeight = 0, Padding = new Thickness(12, 0, 12, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, $"{levelWord}, {e.Time.ToLocalTime():HH:mm:ss}, {(e.Source == "host" ? "Rigbay" : SourceName(e.Source))}: {e.Message}");
        return item;
    }
}

using System.Diagnostics;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Rigbay.Abstractions;
using Rigbay.App.Services;
using Rigbay.Core;
using Windows.Foundation;

namespace Rigbay.App;

/// <summary>
/// The Dashboard. First the machine: "This PC now", one tile per key reading of the running modules (DB-1), each figure once. Then the bay
/// rack: one slim row per installed module with its state line. A reading that is not available is left out, not shown as "Not available"
/// (DB-2). A key reading that did not get a tile (more than <see cref="MaxTiles"/>) stays in its module's row, in a fixed slot (DB-3); below
/// <see cref="NarrowRow"/> px the slots move under the name so the name is never squeezed out (DB-7).
/// </summary>
static class DashboardPage
{
    /// <summary>At most this many tiles in "This PC now".</summary>
    public const int MaxTiles = 6;

    /// <summary>A rack row narrower than this puts its reading slots on a second line, under the name.</summary>
    public const double NarrowRow = 760;

    /// <summary>The name and state line of a rack row never get less than this.</summary>
    public const double MinNameWidth = 160;

    /// <summary>Width of one reading slot in a rack row.</summary>
    public const double SlotWidth = 160;

    /// <summary>A key reading as the Dashboard shows it.</summary>
    internal sealed record Key(ModuleEntry Entry, SummaryReading Reading, string Short, MetricUnit Unit);

    /// <summary>What the Dashboard draws, worked out before anything is drawn (the self-test reads it).</summary>
    internal sealed record Plan(IReadOnlyList<Key> Tiles, IReadOnlyDictionary<string, IReadOnlyList<Key>> RowReadings, string Signature);

    /// <summary>The plan of the Dashboard last built.</summary>
    internal static Plan? LastPlan { get; private set; }

    public static UIElement Build(PageScope scope)
    {
        var page = new StackPanel();
        page.Children.Add(Ui.PageHeaderWith(null, Host.T("nav.dashboard"), Summary()));
        var entries = Host.Core.Modules;
        if (entries.Count == 0)
        {
            var go = Ui.LinkButton(Host.T("dash.empty-action"), () => Host.Window.Navigate("modules"));
            go.Style = Ui.Style("AccentButtonStyle");
            page.Children.Add(Ui.StateBlock(BayState.Off, Host.T("dash.empty-title"), Host.F("dash.empty-body", AppPaths.ModulesRoot) + "\n" + Host.T("dash.empty-restart"), go));
            return page;
        }

        var plan = MakePlan(entries);
        LastPlan = plan;
        if (plan.Tiles.Count > 0)
        {
            page.Children.Add(Heading(Host.T("dash.now"), top: 4));
            var strip = new DashStrip();
            foreach (var k in plan.Tiles) strip.Children.Add(Tile(k, scope));
            page.Children.Add(strip);
            ChartKit.FadeIn(strip);
        }

        page.Children.Add(Heading(Host.T("dash.rack"), top: plan.Tiles.Count > 0 ? 20 : 4));
        var rack = new StackPanel();
        foreach (var e in entries) rack.Children.Add(Row(e, plan.RowReadings.TryGetValue(e.Id, out var rs) ? rs : Array.Empty<Key>(), scope));
        page.Children.Add(rack);

        // A reading that comes or goes (a module publishes it for the first time, withdraws it, or publishes it again) changes what is drawn:
        // the bus says so with CatalogChanged. The page is rebuilt, with scroll and focus kept, only when the plan really changed.
        DispatcherQueueTimerHolder? pending = null;
        void Changed() => Ui.Post(null, () =>
        {
            if (scope.Disposed) return;
            pending ??= new DispatcherQueueTimerHolder(TimeSpan.FromMilliseconds(300), () =>
            {
                pending = null;
                if (!scope.Disposed && MakePlan(Host.Core.Modules).Signature != plan.Signature) Host.Window.RefreshPageKeepingView();
            });
            pending.Restart();
        }, "dashboard readings");
        Host.Core.Metrics.CatalogChanged += Changed;
        scope.Add(() => { Host.Core.Metrics.CatalogChanged -= Changed; pending?.Stop(); });
        return page;
    }

    static TextBlock Heading(string text, double top) =>
        new TextBlock { Text = text, Style = Ui.Style("RbGroupHeader"), Margin = new Thickness(1, top, 0, 8) }.With(h => AutomationProperties.SetHeadingLevel(h, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2));

    // ---------------------------------------------------------------- what goes where

    /// <summary>
    /// The key readings of the running modules that have a value now, in rack order: the first <see cref="MaxTiles"/> distinct ones become tiles
    /// (one per metric and one per label: the first module wins, so two modules reporting the CPU temperature give one tile); the ones left over
    /// stay in their module's row. A reading with the same label as a tile is not repeated in a row.
    /// </summary>
    internal static Plan MakePlan(IReadOnlyList<ModuleEntry> entries)
    {
        var tiles = new List<Key>();
        var rows = new Dictionary<string, IReadOnlyList<Key>>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var sig = new List<string>();
        foreach (var e in entries)
        {
            if (e.Lifecycle != ModuleLifecycle.Running) continue;
            var left = new List<Key>();
            foreach (var r in e.GetSummaryReadings())
            {
                if (!Host.Core.Metrics.TryGetLatest(r.Id, out var latest) || !ReadingFormat.IsAvailable(latest)) { sig.Add(e.Id + ":" + r.Id.Value + ":-"); continue; }
                var unit = r.Unit != MetricUnit.None ? r.Unit : latest!.Unit;
                var k = new Key(e, r, ShortLabel(r), unit);
                sig.Add(e.Id + ":" + r.Id.Value + ":" + unit);
                if (ids.Contains(r.Id.Value) || labels.Contains(k.Short)) continue;   // the same figure already has a tile
                if (tiles.Count < MaxTiles) { tiles.Add(k); ids.Add(r.Id.Value); labels.Add(k.Short); }
                else left.Add(k);
            }
            if (left.Count > 0) rows[e.Id] = left;
        }
        return new Plan(tiles, rows, string.Join("|", sig));
    }

    /// <summary>
    /// The short name of a reading for a tile or a slot: the module's own <c>&lt;label key&gt;.short</c>, else the host's <c>metric.&lt;id&gt;.short</c>,
    /// else the host's <c>metric.&lt;id&gt;</c> (written short for the shared ids: "CPU", "CPU fan"), else the long label. The long label is the tooltip.
    /// </summary>
    internal static string ShortLabel(SummaryReading r)
    {
        var metrics = Host.Core.Metrics;
        if (metrics.TryGetDescriptor(r.Id, out var d, out var owner) && Host.Core.Find(owner) is { } module
            && !Text.IsLiteral(d.LabelKey) && module.Localisation.TryGet(d.LabelKey + ".short", out var own) && !string.IsNullOrWhiteSpace(own))
            return own;
        if (Host.Core.Languages.TryGet("metric." + r.Id.Value + ".short", out var hostShort) && !string.IsNullOrWhiteSpace(hostShort)) return hostShort;
        if (Host.Core.Languages.TryGet("metric." + r.Id.Value, out var host) && !string.IsNullOrWhiteSpace(host)) return host;
        return r.Label;
    }

    // ---------------------------------------------------------------- the header line

    /// <summary>"1 failed · 3 running · 1 off": a failed module leads, in critical ink (LC-3); the other counts follow in secondary ink.</summary>
    static UIElement Summary()
    {
        var counts = Host.Core.Modules.GroupBy(e => e.Bay.State).ToDictionary(g => g.Key, g => g.Count());
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var rest = new List<string>();
        foreach (var s in new[] { BayState.Ready, BayState.UpdateAvailable, BayState.RestartRequired, BayState.Off, BayState.Unsupported })
            if (counts.TryGetValue(s, out var n) && n > 0) rest.Add($"{n} {Ui.StateWord(s).ToLowerInvariant()}");
        string text = "";
        if (counts.TryGetValue(BayState.Failed, out var failed) && failed > 0)
        {
            var f = $"{failed} {Ui.StateWord(BayState.Failed).ToLowerInvariant()}";
            line.Children.Add(Ui.Text(f, "RbErrorText"));
            text = f;
            if (rest.Count > 0) line.Children.Add(Ui.Text("·", "RbSecondaryCaption").With(s => s.Margin = new Thickness(6, 0, 6, 0)));   // spaces at the end of a TextBlock take no width
        }
        if (rest.Count > 0) line.Children.Add(Ui.Text(string.Join("  ·  ", rest), "RbSecondaryCaption", trim: true));
        text = string.Join("  ·  ", new[] { text }.Concat(rest).Where(t => t.Length > 0));
        AutomationProperties.SetName(line, text);
        return line;
    }

    /// <summary>The same text as the header line, for the self-test.</summary>
    internal static string SummaryText()
    {
        var counts = Host.Core.Modules.GroupBy(e => e.Bay.State).ToDictionary(g => g.Key, g => g.Count());
        var parts = new List<string>();
        foreach (var s in new[] { BayState.Failed, BayState.Ready, BayState.UpdateAvailable, BayState.RestartRequired, BayState.Off, BayState.Unsupported })
            if (counts.TryGetValue(s, out var n) && n > 0) parts.Add($"{n} {Ui.StateWord(s).ToLowerInvariant()}");
        return string.Join("  ·  ", parts);
    }

    // ---------------------------------------------------------------- "This PC now"

    /// <summary>A V1 reading tile (unchanged) as a button that opens the module the reading comes from.</summary>
    static UIElement Tile(Key k, PageScope scope)
    {
        var row = new ReadingRow(Text.Literal(k.Short), k.Reading.Id) { Decimals = DecimalsOf(k.Reading.FormatHint) };
        var tile = new ReadingTile(k.Entry, row, k.Unit, scope) { IsTabStop = false };
        var button = new Button
        {
            Content = tile, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = null, CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            Style = Ui.Style("SubtleButtonStyle"), Tag = "dash-tile:" + k.Reading.Id.Value,
        };
        // the name a screen reader hears is the tile's own, which carries the live value ("CPU, 82 °C"); the help text says where the click goes
        AutomationProperties.SetLabeledBy(button, tile);
        AutomationProperties.SetHelpText(button, Host.F("dash.tile-open", k.Entry.DisplayName));
        var full = k.Reading.Label != k.Short ? k.Reading.Label : k.Short;
        ToolTipService.SetToolTip(button, $"{full}  ·  {k.Entry.DisplayName}");
        button.Click += (_, _) => Host.Window.Navigate(k.Entry);
        return button;
    }

    // ---------------------------------------------------------------- the rack

    static UIElement Row(ModuleEntry e, IReadOnlyList<Key> readings, PageScope scope)
    {
        var grid = new Grid { MinHeight = 56, ColumnSpacing = 14, RowSpacing = 6, Padding = new Thickness(6, 8, 6, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = MinNameWidth });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bay = Ui.Bay(e.Bay.State, 34);
        Grid.SetRowSpan(bay, 2);
        bay.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(bay);

        // the module's tile at 24 DIP, next to the state bar and never in its place; grey when the machine does not support the module
        var tile = ModuleIconUi.Tile(e, 24);
        Grid.SetColumn(tile, 1);
        grid.Children.Add(tile);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, MinWidth = MinNameWidth };
        text.Children.Add(Ui.Text(e.DisplayName, trim: true));
        var state = Ui.Text("", "RbSecondaryCaption", trim: true);
        state.TextWrapping = TextWrapping.NoWrap; state.MaxLines = 1;
        text.Children.Add(state);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        void ShowState()
        {
            var word = Ui.StateWord(e.Bay.State);
            string detail = e.Lifecycle switch
            {
                ModuleLifecycle.Running => e.Page?.Summary?.StateLine is { } line ? e.Localisation.Resolve(line.Value) : "",
                _ => e.ShortReasonText(),
            };
            state.Text = detail.Length > 0 ? $"{word}  ·  {detail}" : word;
            var full = e.Lifecycle == ModuleLifecycle.Running ? detail : e.ReasonText();
            ToolTipService.SetToolTip(state, full.Length > 0 ? $"{word}  ·  {full}" : null);
        }
        ShowState();
        if (e.Lifecycle == ModuleLifecycle.Running && e.Page?.Summary?.StateLine is { } sl)
            scope.Bind(sl, e, _ => ShowState());

        if (readings.Count > 0)
        {
            var slots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            foreach (var k in readings.Take(2)) slots.Children.Add(new Slot(k, scope));
            grid.Children.Add(slots);
            void Place(double width)
            {
                bool narrow = width < NarrowRow;
                Grid.SetRow(slots, narrow ? 1 : 0);
                Grid.SetColumn(slots, narrow ? 2 : 3);
                Grid.SetColumnSpan(slots, narrow ? 2 : 1);
                slots.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                foreach (var s in slots.Children.OfType<Slot>()) s.Narrow = narrow;
            }
            Place(double.PositiveInfinity);
            grid.SizeChanged += (_, a) => Place(a.NewSize.Width);
        }

        var button = new Button
        {
            Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = null, CornerRadius = new CornerRadius(4),
            Style = Ui.Style("SubtleButtonStyle"),
        };
        AutomationProperties.SetName(button, $"{e.DisplayName}, {Ui.StateWord(e.Bay.State)}");
        button.Click += (_, _) => Host.Window.Navigate(e);
        var wrap = new Border { Style = Ui.Style("RbHairlineBottom"), Child = button };
        return wrap;
    }

    /// <summary>
    /// One reading in a rack row: a fixed 160 px slot, the short label above, the hairline sparkline and the number below, right-aligned so the
    /// rows read down their columns (DB-3). On a narrow row (second line) it is left-aligned under the name.
    /// </summary>
    sealed class Slot : StackPanel
    {
        readonly TextBlock _label;
        readonly StackPanel _value;

        public bool Narrow
        {
            set
            {
                var a = value ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                _label.HorizontalAlignment = a; _value.HorizontalAlignment = a;
                _label.TextAlignment = value ? TextAlignment.Left : TextAlignment.Right;
            }
        }

        public Slot(Key k, PageScope scope)
        {
            Width = SlotWidth;
            Spacing = 1;
            VerticalAlignment = VerticalAlignment.Center;
            _label = Ui.Text(k.Short, "RbSecondaryCaption", trim: true);
            _label.TextWrapping = TextWrapping.NoWrap;
            if (k.Reading.Label != k.Short) ToolTipService.SetToolTip(_label, k.Reading.Label);
            var number = Ui.Text("");
            Ui.Tabular(number);
            int decimals = DecimalsOf(k.Reading.FormatHint);
            string style = "";
            void Show(MetricSample? s)
            {
                number.Text = ReadingFormat.Format(Host.Core.Languages, s, decimals);
                var key = ReadingFormat.IsAvailable(s) ? RowRenderer.ReadingStyle(SemanticColor.Auto, s!.Unit) : "RbSecondaryText";
                if (key != style) { style = key; number.Style = Ui.Style(key); }
                AutomationProperties.SetName(this, $"{k.Short}, {number.Text}");
            }
            MetricSample? Latest() => Host.Core.Metrics.TryGetLatest(k.Reading.Id, out var latest) ? latest : null;
            Show(Latest());
            scope.Live(() => Host.Core.Metrics.Subscribe(s => Ui.Post(k.Entry, () => { if (!scope.Disposed && scope.Active) Show(s); }), k.Reading.Id), () => Show(Latest()));
            var spark = new Sparkline(k.Reading.Id, SemanticColor.Auto, 56, 18) { Tag = "rack-spark", VerticalAlignment = VerticalAlignment.Center };
            scope.Tick(spark.Redraw);
            _value = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { spark, number } };
            Children.Add(_label);
            Children.Add(_value);
        }
    }

    /// <summary>Digits after the point named by a descriptor's format hint ("0", "0.0", "F1"); the bay showed whole numbers before, so no hint means 0.</summary>
    internal static int DecimalsOf(string? hint)
    {
        if (string.IsNullOrEmpty(hint)) return 0;
        int dot = hint.IndexOf('.');
        if (dot >= 0) return hint.Length - dot - 1;
        return hint[0] is 'F' or 'f' && int.TryParse(hint.AsSpan(1), out var n) && n is >= 0 and <= 6 ? n : 0;
    }
}

/// <summary>A one-shot timer that can be pushed back (a burst of events gives one action).</summary>
sealed class DispatcherQueueTimerHolder
{
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

    public DispatcherQueueTimerHolder(TimeSpan delay, Action action)
    {
        _timer = Host.Dq.CreateTimer();
        _timer.Interval = delay;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => { try { action(); } catch (Exception ex) { AppLog.Warn("a delayed UI action failed", ex); } };
    }

    public void Restart() { _timer.Stop(); _timer.Start(); }
    public void Stop() => _timer.Stop();
}

/// <summary>
/// The tiles of "This PC now": as many columns as fit (each at least <see cref="MinTileWidth"/>), rows balanced (five tiles that fit four
/// across are 3 + 2, not 4 + 1), and the tiles of a shorter last row widened to the full width, so no row ends in a hole.
/// </summary>
sealed class DashStrip : Panel
{
    public const double MinTileWidth = 156;
    public double Gap { get; set; } = 8;

    /// <summary>Columns for <paramref name="count"/> tiles in <paramref name="width"/>: the fewest rows that fit, then as few columns as fill them.</summary>
    public static int Columns(int count, double width, double gap = 8)
    {
        if (count <= 1) return 1;
        int fit = double.IsInfinity(width) ? count : Math.Max(1, (int)Math.Floor((width + gap) / (MinTileWidth + gap)));
        fit = Math.Min(fit, count);
        int rows = (count + fit - 1) / fit;
        return (count + rows - 1) / rows;
    }

    IEnumerable<(int Start, int Count)> Rows(int n, int cols)
    {
        for (int start = 0; start < n; start += cols) yield return (start, Math.Min(cols, n - start));
    }

    protected override Size MeasureOverride(Size available)
    {
        int n = Children.Count;
        if (n == 0) return new Size(0, 0);
        int cols = Columns(n, available.Width, Gap);
        double total = 0;
        foreach (var (start, count) in Rows(n, cols))
        {
            double w = double.IsInfinity(available.Width) ? MinTileWidth : (available.Width - Gap * (count - 1)) / count;
            double rowH = 0;
            for (int i = start; i < start + count; i++) { Children[i].Measure(new Size(w, double.PositiveInfinity)); rowH = Math.Max(rowH, Children[i].DesiredSize.Height); }
            total += rowH + (start + count < n ? Gap : 0);
        }
        return new Size(double.IsInfinity(available.Width) ? MinTileWidth * cols + Gap * (cols - 1) : available.Width, total);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int n = Children.Count;
        if (n == 0) return final;
        int cols = Columns(n, final.Width, Gap);
        double y = 0;
        foreach (var (start, count) in Rows(n, cols))
        {
            double w = (final.Width - Gap * (count - 1)) / count;
            double rowH = 0;
            for (int i = start; i < start + count; i++) rowH = Math.Max(rowH, Children[i].DesiredSize.Height);
            for (int i = start; i < start + count; i++) Children[i].Arrange(new Rect((i - start) * (w + Gap), y, w, rowH));
            y += rowH + Gap;
        }
        return final;
    }
}

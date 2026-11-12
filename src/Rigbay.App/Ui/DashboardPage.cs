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

/// <summary>The Dashboard: the bay rack. One slim row per installed module, a hairline between rows.</summary>
static class DashboardPage
{
    public static UIElement Build(PageScope scope)
    {
        var page = new StackPanel();
        page.Children.Add(Ui.PageHeader(Host.T("nav.dashboard"), Summary()));
        var entries = Host.Core.Modules;
        if (entries.Count == 0)
        {
            var go = Ui.LinkButton(Host.T("dash.empty-action"), () => Host.Window.Navigate("modules"));
            go.Style = Ui.Style("AccentButtonStyle");
            page.Children.Add(Ui.StateBlock(BayState.Off, Host.T("dash.empty-title"), Host.F("dash.empty-body", AppPaths.ModulesRoot) + "\n" + Host.T("dash.empty-restart"), go));
            return page;
        }
        page.Children.Add(new TextBlock { Text = Host.T("dash.rack"), Style = Ui.Style("RbGroupHeader"), Margin = new Thickness(1, 4, 0, 4) });
        var rack = new StackPanel();
        foreach (var e in entries) rack.Children.Add(Row(e, scope));
        page.Children.Add(rack);
        return page;
    }

    static string Summary()
    {
        var counts = Host.Core.Modules.GroupBy(e => e.Bay.State).ToDictionary(g => g.Key, g => g.Count());
        var parts = new List<string>();
        foreach (var s in new[] { BayState.Ready, BayState.Off, BayState.Unsupported, BayState.Failed })
            if (counts.TryGetValue(s, out var n) && n > 0) parts.Add($"{n} {Ui.StateWord(s).ToLowerInvariant()}");
        return string.Join("  ·  ", parts);
    }

    static UIElement Row(ModuleEntry e, PageScope scope)
    {
        var grid = new Grid { MinHeight = 56, ColumnSpacing = 14, Padding = new Thickness(6, 8, 6, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bay = Ui.Bay(e.Bay.State, 34);
        grid.Children.Add(bay);

        // the module's tile at 24 DIP, next to the state bar and never in its place; grey when the machine does not support the module
        var tile = ModuleIconUi.Tile(e, 24);
        Grid.SetColumn(tile, 1);
        grid.Children.Add(tile);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
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

        if (e.Lifecycle == ModuleLifecycle.Running && e.GetSummaryReadings() is { Count: > 0 } readings)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22, VerticalAlignment = VerticalAlignment.Center };
            foreach (var reading in readings) strip.Children.Add(Reading(e, reading, scope));
            Grid.SetColumn(strip, 3);
            grid.Children.Add(strip);
            // a narrow window (the pane is open at 640 px): the sparklines make room for the name, the numbers stay
            grid.SizeChanged += (_, a) =>
            {
                var show = a.NewSize.Width >= 640 ? Visibility.Visible : Visibility.Collapsed;
                foreach (var panel in strip.Children.OfType<StackPanel>())
                    foreach (var spark in panel.Children.OfType<Sparkline>()) spark.Visibility = show;
            };
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

    static UIElement Reading(ModuleEntry e, SummaryReading reading, PageScope scope)
    {
        // The label comes from the core: the module's descriptor, else the host's "metric.<id>" text, else the raw id.
        var label = Ui.Text(reading.Label, "RbSecondaryCaption");
        var value = Ui.Text("");
        Ui.Tabular(value);
        int decimals = DecimalsOf(reading.FormatHint);
        string style = "";
        void Show(MetricSample? s)
        {
            value.Text = ReadingFormat.Format(Host.Core.Languages, s, decimals);
            var key = ReadingFormat.IsAvailable(s) ? RowRenderer.ReadingStyle(SemanticColor.Auto, s!.Unit) : "RbSecondaryText";
            if (key != style) { style = key; value.Style = Ui.Style(key); }
        }
        MetricSample? Latest() => Host.Core.Metrics.TryGetLatest(reading.Id, out var latest) ? latest : null;
        Show(Latest());
        scope.Live(() => Host.Core.Metrics.Subscribe(s => Ui.Post(e, () => { if (!scope.Disposed && scope.Active) Show(s); }), reading.Id), () => Show(Latest()));
        // the hairline sparkline the design promised, left of the number; it drops out on a narrow row (see Row)
        var spark = new Sparkline(reading.Id, SemanticColor.Auto, 64, 20);
        spark.Tag = "rack-spark";
        scope.Tick(spark.Redraw);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { label, spark, value } };
    }

    /// <summary>Digits after the point named by a descriptor's format hint ("0", "0.0", "F1"); the bay showed whole numbers before, so no hint means 0.</summary>
    static int DecimalsOf(string? hint)
    {
        if (string.IsNullOrEmpty(hint)) return 0;
        int dot = hint.IndexOf('.');
        if (dot >= 0) return hint.Length - dot - 1;
        return hint[0] is 'F' or 'f' && int.TryParse(hint.AsSpan(1), out var n) && n is >= 0 and <= 6 ? n : 0;
    }
}

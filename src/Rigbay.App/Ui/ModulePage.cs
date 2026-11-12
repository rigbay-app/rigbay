using System.Diagnostics;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Shapes;
using Rigbay.Abstractions;
using Rigbay.App.Services;
using Rigbay.Core;

namespace Rigbay.App;

/// <summary>
/// A module's page, in this order (Sketch 1 of the November 2026 UI review); every block is optional and an empty block is not drawn:
/// <list type="number">
/// <item>the <b>header card</b> (host-owned): the module's tile and name, its state with a dot and the module's live state line, the on/off switch, the
/// links, and once for the whole page what the shield after a row's name means;</item>
/// <item>the <b>main card</b>: the module's primary control (<see cref="PageModel.Primary"/>), then the reading tiles, which are the tabs of the trend
/// chart under them;</item>
/// <item>the module's <b>groups</b> of rows (rows that say nothing are not drawn), and <b>All readings</b>, folded: every reading as a compact line.</item>
/// </list>
/// A failed module gets one sentence, "Restart and try again", "View log" and its error under Details.
/// </summary>
static class ModulePage
{
    /// <summary>The tiles and chart of the page built last (the self-test looks at them); null when that page had none.</summary>
    public static ReadingBlock? LastBlock { get; private set; }
    /// <summary>The header card of the page built last (the self-test looks at it).</summary>
    public static FrameworkElement? LastHeader { get; private set; }
    /// <summary>The main card (primary, tiles, chart) of the page built last, or null.</summary>
    public static FrameworkElement? LastHero { get; private set; }

    public static UIElement Build(ModuleEntry e, PageScope scope, out RowRenderer? renderer)
    {
        renderer = null;
        LastBlock = null;
        LastHero = null;
        var page = new StackPanel();
        var shown = e.Lifecycle is ModuleLifecycle.Faulted or ModuleLifecycle.Invalid or ModuleLifecycle.Incompatible or ModuleLifecycle.Unsupported ? null : e.LastPage;
        var header = HeaderCard(e, shown, scope);
        LastHeader = header;
        page.Children.Add(header);

        switch (e.Lifecycle)
        {
            case ModuleLifecycle.Faulted:
                page.Children.Add(Failed(e));
                break;
            case ModuleLifecycle.Invalid:
            {
                var log = Ui.LinkButton(Host.T("page.view-log"), () => Host.Window.Navigate("log"));
                // short summary up top, then every manifest problem on its own line, in the user's language
                page.Children.Add(Ui.StateBlock(BayState.Failed, Host.T("page.invalid-title"), e.ShortReasonText() + "\n" + Host.T("page.invalid-hint"), log));
                page.Children.Add(new TextBlock { Text = Host.T("page.invalid-problems"), Style = Ui.Style("RbGroupHeader") });
                var list = new StackPanel { Spacing = 6, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 0, 0, 0) };
                foreach (var line in e.IssueLines())
                {
                    var t = Ui.Text(line, wrap: true);
                    t.IsTextSelectionEnabled = true;
                    list.Children.Add(t);
                }
                page.Children.Add(list);
                break;
            }
            case ModuleLifecycle.Incompatible:
            case ModuleLifecycle.Unsupported:
                page.Children.Add(Ui.StateBlock(BayState.Unsupported, Host.T(e.Lifecycle == ModuleLifecycle.Incompatible ? "page.incompatible-title" : "page.unsupported-title"), e.ReasonText()));
                break;
            default:
                if (shown == null)
                {
                    page.Children.Add(Ui.Text(Host.T(e.Lifecycle == ModuleLifecycle.Running ? "page.no-page" : "page.off-hint"), "RbSecondaryText", wrap: true).With(t => t.Margin = new Thickness(1, 14, 0, 0)));
                    break;
                }
                renderer = new RowRenderer(e, scope);
                bool running = e.Lifecycle == ModuleLifecycle.Running;

                // the main card: the module's main job first and large, then its tiles, which are the chart's tabs, then the chart (Sketch 1)
                var block = running ? ReadingBlock.For(e, shown, scope) : null;
                LastBlock = block;
                var primary = shown.Primary is { } p ? renderer.RenderPrimary(p) : null;
                if (primary != null || block != null)
                {
                    var hero = new StackPanel { Spacing = 18 };
                    if (primary != null) hero.Children.Add(primary);
                    if (block != null) { block.Margin = new Thickness(0); hero.Children.Add(block); }
                    var card = new Border
                    {
                        Style = Ui.Style("RbHeroCard"), Margin = new Thickness(0, 8, 0, 4),
                        Child = new ContentControl { Content = hero, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = running },
                    };
                    // a card of readings that have not come yet (or that the module took back) is not drawn: no empty frame above the rows
                    void Fit()
                    {
                        bool content = primary != null || block?.HasContent == true;
                        card.Visibility = content ? Visibility.Visible : Visibility.Collapsed;
                        if (block != null) block.Visibility = block.HasContent ? Visibility.Visible : Visibility.Collapsed;
                    }
                    if (block != null) block.ContentChanged += () => Ui.Post(null, () => { if (!scope.Disposed) Fit(); });
                    Fit();
                    page.Children.Add(card);
                    LastHero = card;
                }
                var groups = renderer.RenderGroups(shown);
                var host = new ContentControl { Content = groups, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = running };
                page.Children.Add(host);
                break;
        }
        return page;
    }

    // ---------------------------------------------------------------- header card (MP-3)

    /// <summary>
    /// One card for the module itself (Windows Settings' Bluetooth card): its tile and name, its state with a dot (the word too, never colour alone)
    /// and the module's live state line, the switch with its state word, the links small at the top right; while it is off, what it does; and,
    /// when the page has rows that write, once, what the shield after their names means. Id, version and author are on the Modules page.
    /// </summary>
    static FrameworkElement HeaderCard(ModuleEntry e, PageModel? shown, PageScope scope)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tile = ModuleIconUi.Tile(e, 48);
        tile.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(tile);

        var left = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = e.DisplayName, Style = Ui.Style("SubtitleTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis };
        AutomationProperties.SetHeadingLevel(name, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        left.Children.Add(name);

        // the state: a dot in the state's colour and the word, then what the module says about itself ("Running · Fan Auto, PL1 45 W")
        var stateRow = new Grid { ColumnSpacing = 8 };
        stateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stateRow.ColumnDefinitions.Add(new ColumnDefinition());
        var dot = new Ellipse { Width = 8, Height = 8, Style = Ui.Style(DotStyle(e.Bay.State)), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) };
        stateRow.Children.Add(dot);
        var state = Ui.Text("", "RbSecondaryText", wrap: true);
        Ui.Tabular(state);
        Grid.SetColumn(state, 1);
        stateRow.Children.Add(state);
        left.Children.Add(stateRow);
        var stateLine = e.Lifecycle == ModuleLifecycle.Running ? shown?.Summary?.StateLine : null;
        void ShowState()
        {
            string word = Ui.StateWord(e.Bay.State);
            string line = stateLine is { Value: { IsEmpty: false } v } ? e.Localisation.Resolve(v) : "";
            state.Text = line.Length > 0 ? word + "  ·  " + line : word;
        }
        ShowState();
        if (stateLine != null) scope.Bind(stateLine, e, _ => ShowState());

        // while it is off: what it does (the reason to turn it on); while it runs the state line says enough
        if (e.Lifecycle != ModuleLifecycle.Running && e.DisplayDescription.Length > 0)
            left.Children.Add(Ui.Text(e.DisplayDescription, "RbSecondaryCaption", wrap: true).With(t => t.Margin = new Thickness(0, 2, 0, 0)));

        // once for the whole page, instead of a line under every row that writes (MP-2)
        if (shown != null && WriteNote(shown) is { } note) left.Children.Add(note);
        Grid.SetColumn(left, 1);
        grid.Children.Add(left);

        // right: the links small at the top, the switch with its word under them
        var right = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var link in shown?.Links ?? e.LastPage?.Links ?? Array.Empty<PageLink>())
            if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                links.Children.Add(SmallLink(e.Localisation.T(link.TextKey), uri));
        if (links.Children.Count == 0 && e.Manifest?.Homepage is { } home && Uri.TryCreate(home, UriKind.Absolute, out var hu))
            links.Children.Add(SmallLink(Host.T("page.homepage"), hu));
        links.Children.Add(SmallLink(Host.T("nav.log"), null).With(b => b.Click += (_, _) => Host.Window.Navigate("log")));
        right.Children.Add(links);
        right.Children.Add(EnableSwitch(e));
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        // a narrow window (640 px): the links go under the name and the state, so a long link ("Cấu hình XTU trên GitHub") does not squeeze them
        // into a column of a few words; the right column keeps only the switch
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.SizeChanged += (_, a) => PlaceHeaderLinks(grid, right, links, a.NewSize.Width < NarrowHeaderCard);

        return new Border { Style = Ui.Style("RbHeroCard"), Child = grid, Margin = new Thickness(0, 0, 0, 4) };
    }

    /// <summary>A header card narrower than this puts its links under the module's name and state instead of above the switch.</summary>
    internal const double NarrowHeaderCard = 560;

    static void PlaceHeaderLinks(Grid grid, Panel right, StackPanel links, bool narrow)
    {
        if (narrow == ReferenceEquals(links.Parent, grid)) return;
        if (narrow)
        {
            right.Children.Remove(links);
            links.HorizontalAlignment = HorizontalAlignment.Left;
            links.Margin = new Thickness(-6, 4, 0, 0);   // the link's own padding: its text lines up with the state line
            Grid.SetRow(links, 1);
            Grid.SetColumn(links, 1);
            Grid.SetColumnSpan(links, 2);
            grid.Children.Add(links);
        }
        else
        {
            grid.Children.Remove(links);
            links.HorizontalAlignment = HorizontalAlignment.Right;
            links.Margin = new Thickness(0);
            Grid.SetRow(links, 0);
            Grid.SetColumn(links, 0);
            Grid.SetColumnSpan(links, 1);
            right.Children.Insert(0, links);
        }
    }

    static HyperlinkButton SmallLink(string text, Uri? uri)
    {
        var b = new HyperlinkButton { Content = text, FontSize = 12, Padding = new Thickness(6, 2, 6, 3) };
        if (uri != null) b.NavigateUri = uri;
        return b;
    }

    static string DotStyle(BayState s) => s switch
    {
        BayState.Ready => "RbStateDotReady",
        BayState.Failed => "RbStateDotFailed",
        BayState.UpdateAvailable or BayState.RestartRequired => "RbStateDotAttention",
        _ => "RbStateDotOutline",
    };

    /// <summary>
    /// "Settings marked [shield] change your hardware. Rigbay asks before each change." when the page has a row with a hardware effect (the Windows
    /// setting wording when every such row changes a Windows setting, both when it has both); null when nothing on the page writes.
    /// </summary>
    static TextBlock? WriteNote(PageModel page)
    {
        var effects = page.EnumerateRows().Where(r => r.HardwareEffect != null).Select(r => r.HardwareEffect!.Kind).ToList();
        if (effects.Count == 0) return null;
        bool hw = effects.Contains(EffectKind.Hardware), sys = effects.Contains(EffectKind.SystemSetting);
        var text = Host.T(hw && sys ? "page.mixed-once" : hw ? "page.hardware-once" : "page.system-once");
        var tb = new TextBlock { Style = Ui.Style("RbSecondaryCaption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        int at = text.IndexOf("{0}", StringComparison.Ordinal);
        if (at < 0) tb.Inlines.Add(new Run { Text = text });
        else
        {
            tb.Inlines.Add(new Run { Text = text[..at] });
            tb.Inlines.Add(new Run { Text = RowRenderer.ShieldGlyph, FontFamily = RowRenderer.Icons, FontSize = 11 });
            tb.Inlines.Add(new Run { Text = text[(at + 3)..] });
        }
        AutomationProperties.SetName(tb, text.Replace("{0}", Host.T("page.shield-word"), StringComparison.Ordinal));
        return tb;
    }

    /// <summary>The module's switch with its state in words at its left; off for a module that cannot be turned on.</summary>
    static UIElement EnableSwitch(ModuleEntry e)
    {
        bool canToggle = e.Lifecycle is ModuleLifecycle.Off or ModuleLifecycle.Running;
        bool suppress = false;
        var sw = Ui.CompactToggle(new ToggleSwitch { IsOn = e.Lifecycle == ModuleLifecycle.Running, OnContent = "", OffContent = "", MinWidth = 0, IsEnabled = canToggle });   // ends where the rows' switches end
        AutomationProperties.SetName(sw, Host.F("module.enable", e.DisplayName));
        var word = Ui.Text(Host.T(sw.IsOn ? "value.on" : "value.off"), "RbSecondaryText");
        sw.Toggled += async (_, _) =>
        {
            word.Text = Host.T(sw.IsOn ? "value.on" : "value.off");
            if (suppress) return;
            sw.IsEnabled = false;
            try
            {
                bool now = await ToggleAsync(e, sw.IsOn);
                if (now != sw.IsOn) { suppress = true; sw.IsOn = now; suppress = false; }
            }
            catch (Exception ex) { AppLog.Error("enable switch failed", ex); }
            finally { sw.IsEnabled = e.Lifecycle is ModuleLifecycle.Off or ModuleLifecycle.Running; }
        };
        var pair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        if (canToggle) pair.Children.Add(word);
        pair.Children.Add(sw);
        return pair;
    }

    // ---------------------------------------------------------------- failed (MP-8)

    /// <summary>
    /// A failed module: one sentence that says what happened in words ("Fan studio stopped while starting."), that nothing else was affected,
    /// "Restart and try again" (a module that faulted is loaded fresh by a restart; the switch stays on) and "View log", and the error itself
    /// folded under Details, selectable for a bug report.
    /// </summary>
    static UIElement Failed(ModuleEntry e)
    {
        var reason = e.Bay.Reason;
        string op = reason is { Key: "core.reason.fault", Args.Length: > 0 } ? reason.Args[0]?.ToString() ?? "" : "";
        string key = reason?.Key switch
        {
            "core.reason.timeout" => "page.failed-timeout",
            "core.reason.bad-page" => "page.failed-page",
            "core.reason.fault" when op == "Initialize" => "page.failed-start",
            "core.reason.fault" when op == "GetPageModel" => "page.failed-page",
            "core.reason.fault" => "page.failed-run",
            _ => "page.failed-generic",
        };
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
        var block = new Grid { ColumnSpacing = 14 };
        block.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        block.ColumnDefinitions.Add(new ColumnDefinition());
        var bay = Ui.Bay(BayState.Failed, 44);
        bay.VerticalAlignment = VerticalAlignment.Top;
        block.Children.Add(bay);
        var text = new StackPanel { Spacing = 6 };
        var title = Ui.Text(Host.F(key, e.DisplayName), weight: FontWeights.SemiBold, wrap: true);
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        text.Children.Add(title);
        text.Children.Add(Ui.Text(Host.T("page.failed-hint"), "RbSecondaryText", wrap: true));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        var retry = new Button { Content = Host.T("page.failed-retry"), Style = Ui.Style("AccentButtonStyle") };
        retry.Click += async (_, _) =>
        {
            retry.IsEnabled = false;
            try { await Host.Window.RestartAsync(); }
            catch (Exception ex) { AppLog.Error("restart from a failed page failed", ex); }
            finally { retry.IsEnabled = true; }
        };
        buttons.Children.Add(retry);
        buttons.Children.Add(Ui.LinkButton(Host.T("page.view-log"), () => Host.Window.Navigate("log")));
        text.Children.Add(buttons);
        Grid.SetColumn(text, 1);
        block.Children.Add(text);
        block.MaxWidth = 720;
        block.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(block);

        var details = Ui.Text(e.ReasonText(), wrap: true);
        details.IsTextSelectionEnabled = true;
        var expander = new SettingsExpander { Header = Host.T("page.failed-details"), Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(expander, Host.T("page.failed-details"));
        expander.Items.Add(new SettingsCard { Content = details, ContentAlignment = ContentAlignment.Vertical, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        panel.Children.Add(expander);
        return panel;
    }

    /// <summary>Turns a module on or off. The first time a module is turned on, the user sees what it declares it uses. Returns the new state.</summary>
    public static async Task<bool> ToggleAsync(ModuleEntry e, bool on)
    {
        if (on)
        {
            if (e.Lifecycle == ModuleLifecycle.Running) return true;
            if (!AppState.IsAcknowledged(e) && HasDeclarations(e))
            {
                if (!await RequiresDialog.AskAsync(e)) return false;
                AppState.Acknowledge(e);
            }
            await Task.Run(() => e.Enable());
            return e.Lifecycle == ModuleLifecycle.Running;
        }
        await Task.Run(() => e.Disable());
        return e.Lifecycle == ModuleLifecycle.Running;
    }

    public static bool HasDeclarations(ModuleEntry e) => e.Manifest is { } m && (m.Requires.Admin || m.Requires.Network.Count + m.Requires.Registry.Count + m.Requires.Services.Count > 0 || m.ReadsOutsideItsFolder);
}

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

/// <summary>A module's page: the enable row (host-owned), then the module's groups, then its links.</summary>
static class ModulePage
{
    /// <summary>The tiles and chart of the page built last (the self-test looks at them); null when that page had none.</summary>
    public static ReadingBlock? LastBlock { get; private set; }

    public static UIElement Build(ModuleEntry e, PageScope scope, out RowRenderer? renderer)
    {
        renderer = null;
        LastBlock = null;
        var page = new StackPanel();

        var right = new List<UIElement>();
        foreach (var link in e.LastPage?.Links ?? Array.Empty<PageLink>())
            if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                right.Add(new HyperlinkButton { Content = e.Localisation.T(link.TextKey), NavigateUri = uri });
        if (right.Count == 0 && e.Manifest?.Homepage is { } home && Uri.TryCreate(home, UriKind.Absolute, out var hu))
            right.Add(new HyperlinkButton { Content = Host.T("page.homepage"), NavigateUri = hu });
        right.Add(new HyperlinkButton { Content = Host.T("nav.log") }.With(b => b.Click += (_, _) => Host.Window.Navigate("log")));
        var meta = string.Join("  ·  ", new[] { e.Id, e.VersionText().Length > 0 ? "v" + e.VersionText() : "", e.AuthorText() }.Where(s => s.Length > 0));
        page.Children.Add(Ui.PageHeader(ModuleIconUi.Tile(e, 48), e.DisplayName, meta, right.ToArray()));

        page.Children.Add(EnableCard(e));

        switch (e.Lifecycle)
        {
            case ModuleLifecycle.Faulted:
            case ModuleLifecycle.Invalid:
            {
                var log = Ui.LinkButton(Host.T("page.view-log"), () => Host.Window.Navigate("log"));
                if (e.Lifecycle == ModuleLifecycle.Invalid)
                {
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
                }
                else
                    page.Children.Add(Ui.StateBlock(BayState.Failed, Host.T("page.failed-title"), e.ReasonText(), log));
                break;
            }
            case ModuleLifecycle.Incompatible:
            case ModuleLifecycle.Unsupported:
                page.Children.Add(Ui.StateBlock(BayState.Unsupported, Host.T(e.Lifecycle == ModuleLifecycle.Incompatible ? "page.incompatible-title" : "page.unsupported-title"), e.ReasonText()));
                break;
            default:
                var shown = e.LastPage;
                if (shown == null)
                {
                    page.Children.Add(Ui.Text(Host.T(e.Lifecycle == ModuleLifecycle.Running ? "page.no-page" : "page.off-hint"), "RbSecondaryText", wrap: true).With(t => t.Margin = new Thickness(1, 14, 0, 0)));
                    break;
                }
                renderer = new RowRenderer(e, scope);
                // tiles and the trend chart come before the rows: the first thing on a page with readings is the readings
                if (e.Lifecycle == ModuleLifecycle.Running && ReadingBlock.For(e, shown, scope) is { } block)
                {
                    page.Children.Add(block);
                    LastBlock = block;
                }
                var groups = renderer.RenderGroups(shown);
                var host = new ContentControl { Content = groups, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = e.Lifecycle == ModuleLifecycle.Running };
                page.Children.Add(host);
                break;
        }
        return page;
    }

    static UIElement EnableCard(ModuleEntry e)
    {
        bool canToggle = e.Lifecycle is ModuleLifecycle.Off or ModuleLifecycle.Running;
        bool suppress = false;
        var sw = new ToggleSwitch { IsOn = e.Lifecycle == ModuleLifecycle.Running, OnContent = "", OffContent = "", MinWidth = 0, IsEnabled = canToggle };
        AutomationProperties.SetName(sw, Host.F("module.enable", e.DisplayName));
        sw.Toggled += async (_, _) =>
        {
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
        return new SettingsCard { Header = Host.F("module.enable", e.DisplayName), Description = e.DisplayDescription, Content = sw };
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

static class UiExtensions
{
    public static T With<T>(this T item, Action<T> f) { f(item); return item; }
}

/// <summary>Before the first enable: what the module declares it uses (a declaration, not a sandbox).</summary>
static class RequiresDialog
{
    public static async Task<bool> AskAsync(ModuleEntry e)
    {
        var root = Host.Window?.Content?.XamlRoot;
        if (root == null) return false;
        var m = e.Manifest!;
        var body = new StackPanel { Spacing = 8, MaxWidth = 480 };
        body.Children.Add(Ui.Text(Host.F("requires.intro", e.DisplayName, e.AuthorText()), wrap: true));
        void Add(string label, IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            body.Children.Add(Ui.Text(label, weight: FontWeights.SemiBold));
            foreach (var i in list) body.Children.Add(Ui.Text(i, "RbSecondaryText", wrap: true).With(t => t.Margin = new Thickness(12, 0, 0, 0)));
        }
        if (m.Requires.Admin) Add(Host.T("requires.admin"), new[] { Host.T("requires.admin-yes") });
        Add(Host.T("requires.network"), m.Requires.Network);
        Add(Host.T("requires.registry"), m.Requires.Registry);
        Add(Host.T("requires.services"), m.Requires.Services);
        Add(Host.T(ModuleManifest.OutsideFilesLabelKey), m.DeclaredProbePaths);
        body.Children.Add(Ui.Text(Host.T("requires.note"), "RbSecondaryCaption", wrap: true));
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = Host.F("requires.title", e.DisplayName), Content = body,
            PrimaryButtonText = Host.T("requires.turn-on"), CloseButtonText = Host.T("approval.cancel"), DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ((FrameworkElement)Host.Window!.Content).ActualTheme,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}

/// <summary>Language, theme, start with Windows, log level, open the log folder, and the hotkeys of all modules.</summary>
static class SettingsPage
{
    public static UIElement Build(PageScope scope)
    {
        var page = new StackPanel();
        page.Children.Add(Ui.PageHeader(Host.T("nav.settings"), null));
        page.Children.Add(new TextBlock { Text = Host.T("settings.general"), Style = Ui.Style("RbGroupHeader"), Margin = new Thickness(1, 0, 0, 6) });
        var general = new StackPanel { Spacing = 2 };

        // language
        var lang = new ComboBox { MinWidth = 220 };
        foreach (var l in Host.Core.Languages.Languages) lang.Items.Add(new ComboBoxItem { Content = l.Name, Tag = l.Code });
        lang.SelectedItem = lang.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Host.Core.Languages.CurrentCode);
        AutomationProperties.SetName(lang, Host.T("settings.language"));
        lang.SelectionChanged += (_, _) =>
        {
            if (lang.SelectedItem is not ComboBoxItem it || (string)it.Tag == Host.Core.Languages.CurrentCode) return;
            Host.Dq.TryEnqueue(() => Host.Core.SetLanguage((string)it.Tag));   // remembers the choice; the window rebuilds itself from LanguageChanged
        };
        general.Children.Add(new SettingsCard { Header = Host.T("settings.language"), Description = Host.T("settings.language-desc"), Content = lang });

        // theme
        var theme = new ComboBox { MinWidth = 220 };
        foreach (var t in new[] { "system", "light", "dark" }) theme.Items.Add(new ComboBoxItem { Content = Host.T("theme." + t), Tag = t });
        theme.SelectedItem = theme.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == AppState.Settings.Theme) ?? theme.Items[0];
        AutomationProperties.SetName(theme, Host.T("settings.theme"));
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is not ComboBoxItem it) return;
            AppState.Settings.Theme = (string)it.Tag;
            AppState.Save();
            Host.Window.ApplyTheme((string)it.Tag);
        };
        general.Children.Add(new SettingsCard { Header = Host.T("settings.theme"), Content = theme });

        // start with Windows
        var startStatus = Ui.Text("", "RbSecondaryCaption", trim: true);
        startStatus.MaxWidth = 280;
        var start = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, IsEnabled = false };
        AutomationProperties.SetName(start, Host.T("settings.start"));
        bool suppress = false;
        async Task RefreshStart()
        {
            bool exists = await Task.Run(StartupTask.Exists);
            suppress = true; start.IsOn = exists; start.IsEnabled = true; suppress = false;
            startStatus.Text = exists ? Host.T("settings.start-on") : "";
        }
        _ = RefreshStart();
        start.Toggled += async (_, _) =>
        {
            if (suppress) return;
            start.IsEnabled = false;
            var on = start.IsOn;
            var (ok, msg) = await Task.Run(() => on ? StartupTask.Create() : StartupTask.Delete());
            if (!ok) { startStatus.Style = Ui.Style("RbErrorText"); startStatus.Text = msg; }
            await RefreshStart();
            if (!ok) { startStatus.Style = Ui.Style("RbErrorText"); startStatus.Text = msg; } else startStatus.Style = Ui.Style("RbSecondaryCaption");
        };
        var startPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { startStatus, start } };
        general.Children.Add(new SettingsCard { Header = Host.T("settings.start"), Description = Host.T("settings.start-desc"), Content = startPanel });

        // log level
        var level = new ComboBox { MinWidth = 220 };
        foreach (var l in new[] { LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error }) level.Items.Add(new ComboBoxItem { Content = Host.T("level." + l.ToString().ToLowerInvariant()), Tag = l });
        level.SelectedItem = level.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (LogLevel)i.Tag == Host.Core.Log.Minimum);
        AutomationProperties.SetName(level, Host.T("settings.log-level"));
        level.SelectionChanged += (_, _) =>
        {
            if (level.SelectedItem is not ComboBoxItem it) return;
            Host.Core.Log.Minimum = (LogLevel)it.Tag;
            AppState.Settings.LogLevel = ((LogLevel)it.Tag).ToString().ToLowerInvariant();
            AppState.Save();
        };
        general.Children.Add(new SettingsCard { Header = Host.T("settings.log-level"), Description = Host.T("settings.log-level-desc"), Content = level });

        // daily check of the module list (off by default: nothing leaves the PC unless the user asks)
        var daily = new ToggleSwitch { IsOn = AppState.Settings.CheckDaily, OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(daily, Host.T("settings.module-check"));
        daily.Toggled += (_, _) =>
        {
            AppState.Settings.CheckDaily = daily.IsOn;
            AppState.Save();
            if (daily.IsOn && InstallServices.DailyCheckDue(DateTimeOffset.UtcNow)) _ = InstallServices.CheckNowAsync();
        };
        general.Children.Add(new SettingsCard { Header = Host.T("settings.module-check"), Description = Host.T("settings.module-check-desc"), Content = daily });

        // log folder
        general.Children.Add(new SettingsCard { Header = Host.T("settings.log-folder"), Description = Host.Core.Paths.LogDirectory, Content = Ui.LinkButton(Host.T("settings.open"), () => ModulesPage.OpenFolder(Host.Core.Paths.LogDirectory)) });

        // version and where the source is
        general.Children.Add(new SettingsCard { Header = Host.T("settings.about"), Description = Host.F("settings.about-desc", AppPaths.Version, HostApi.Current), Content = new HyperlinkButton { Content = Host.T("settings.about-repo"), NavigateUri = new Uri("https://github.com/rigbay-app/rigbay") } });
        page.Children.Add(general);

        // hotkeys
        page.Children.Add(new TextBlock { Text = Host.T("settings.hotkeys"), Style = Ui.Style("RbGroupHeader") });
        var hot = new StackPanel { Spacing = 2 };
        var entries = Host.Core.Hotkeys.Entries;
        if (entries.Count == 0) hot.Children.Add(Ui.Text(Host.T("settings.hotkeys-none"), "RbSecondaryText", wrap: true).With(t => t.Margin = new Thickness(1, 0, 0, 0)));
        foreach (var h in entries)
        {
            var mod = Host.Core.Find(h.ModuleId);
            var gesture = Ui.Text(h.Gesture.Value?.ToString() ?? Host.T("row.shortcut-none"));
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            if (h.HasConflict.Value || Host.Hotkeys?.IsRefused(h) == true) content.Children.Add(Ui.Text(Host.T(Host.Hotkeys?.IsRefused(h) == true ? "settings.hotkey-refused" : "settings.hotkey-conflict"), "RbErrorText"));
            content.Children.Add(gesture);
            hot.Children.Add(new SettingsCard { Header = mod?.Localisation.T(h.NameKey) ?? h.NameKey, Description = mod?.DisplayName ?? "", Content = content });
        }
        page.Children.Add(hot);
        return page;
    }
}

/// <summary>Read-only tail of the host log (the last 500 entries kept in memory).</summary>
static class LogPage
{
    public static UIElement Build(PageScope scope)
    {
        var box = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12, MinHeight = 420, Height = 560, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(box, Host.T("nav.log"));

        void Fill()
        {
            box.Text = string.Join("\r", Host.Core.Log.Recent().Select(AppLog.FormatLine));
            box.Select(box.Text.Length, 0);
            try { box.UpdateLayout(); var sv = FindScroll(box); sv?.ChangeView(null, sv.ScrollableHeight, null, true); } catch { }
        }
        Fill();

        // Throttle: at most one refresh per 400 ms while the page is open.
        var timer = Host.Dq.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        bool dirty = false;
        void OnWritten() { dirty = true; }
        Action<LogEntry> onEntry = _ => OnWritten();
        Host.Core.Log.EntryWritten += onEntry;
        timer.Tick += (_, _) => { if (dirty && !scope.Disposed) { dirty = false; Fill(); } };
        timer.Start();
        scope.Add(() => { Host.Core.Log.EntryWritten -= onEntry; timer.Stop(); });

        var folder = Ui.LinkButton(Host.T("settings.open"), () => ModulesPage.OpenFolder(Host.Core.Paths.LogDirectory));
        var copy = Ui.LinkButton(Host.T("log.copy"), () =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(string.Join(Environment.NewLine, Host.Core.Log.Recent().Select(AppLog.FormatLine)));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        });
        var page = new StackPanel();
        page.Children.Add(Ui.PageHeader(Host.T("nav.log"), Host.Core.Log.CurrentFile, folder, copy));
        page.Children.Add(box);
        return page;
    }

    static ScrollViewer? FindScroll(DependencyObject root)
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (c is ScrollViewer sv) return sv;
            var r = FindScroll(c);
            if (r != null) return r;
        }
        return null;
    }
}

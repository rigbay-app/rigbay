using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rigbay.Abstractions;
using Rigbay.App.Services;
using Rigbay.Core;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Rigbay.App;

public sealed partial class MainWindow : Window
{
    static readonly string[] GroupOrder = { "power", "cooling", "network", "display", "monitoring", "other" };

    readonly Options _opt;
    readonly TaskCompletionSource _uiReady = new();
    InstanceServer? _server;
    PageScope? _scope;
    object _current = "dashboard";
    bool _navSuppress, _quitting, _uiBuilt;
    int _frames;

    internal nint Hwnd;
    internal RowRenderer? LastRenderer;
    internal List<SearchItem> SearchIndex = new();
    internal Options Opt => _opt;
    internal int WindowProcHotkeys, WindowProcTray;
    internal int ShowRequests;
    internal IEnumerable<NavigationViewItem> NavItems() => AllNavItems();
    internal UIElement? PageContent => PageHost.Content as UIElement;


    internal sealed record SearchItem(string Text, string Where, object Target)
    {
        public string Label => Where.Length > 0 && Where != Text ? $"{Where}" : Text;
        public override string ToString() => Label;
    }

    internal MainWindow(Options opt)
    {
        _opt = opt;
        Host.Window = this;
        Host.Options = opt;
        Host.Dq = DispatcherQueue;

        var core = new RigbayHost(AppPaths.HostOptions());
        Host.Core = core;
        AppLog.Attach(core);
        core.Languages.LoadEmbedded(typeof(MainWindow).Assembly, "Rigbay.App.lang.");   // the app's own strings join the core's
        AppState.Load(core);
        if (opt.Lang != null) core.Languages.SetLanguage(opt.Lang);

        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;   // 48 px
        SetTitleBar(TitleBar);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        int w = 1240, h = 800;
        if (opt.Size is { } s && s.Split('x') is { Length: 2 } p && int.TryParse(p[0], out var sw) && int.TryParse(p[1], out var sh)) { w = sw; h = sh; }
        AppWindow.Resize(new SizeInt32(w, h));
        AppWindow.Title = "Rigbay";
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "rigbay.ico");
        if (File.Exists(ico)) { try { AppWindow.SetIcon(ico); } catch { } }
        ApplyTheme(opt.Theme ?? AppState.Settings.Theme);

        Root.Loaded += OnLoaded;
        Root.ActualThemeChanged += (_, _) => ApplyTitleBarColors();
        TitleBar.SizeChanged += (_, _) => RightInsetColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / Math.Max(1, Root.XamlRoot?.RasterizationScale ?? 1) + 8);
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) => AppLog.Info("window closed");

        Native.Subclass(Hwnd, WindowProc);
    }

    // ---------------------------------------------------------------- startup

    internal async void Start()
    {
        try
        {
            var core = Host.Core;
            // The module's RunOnUi already runs inside its fault boundary; the dispatcher only has to queue it.
            core.UiDispatcher = action =>
            {
                if (!DispatcherQueue.TryEnqueue(() => { try { action(); } catch (Exception ex) { AppLog.Error("RunOnUi action threw outside a boundary", ex); } }))
                    AppLog.Warn("RunOnUi dropped: the dispatcher is gone");
            };
            core.ApprovalPrompt = new ApprovalPrompt();
            core.ToastSink = Host.Toast;
            core.ModulesDiscovered += () => Ui.Post(null, WatchEntries);
            core.ModuleFaulted += OnModuleFaulted;
            core.Languages.LanguageChanged += () => Ui.Post(null, ApplyLanguage);

            await Task.Run(core.Discover);
            WatchEntries();
            SetupHosts();
            await Task.Run(core.StartEnabled);

            if (!_opt.Tray || _opt.Shot != null || _opt.SelfTest || _opt.Script != null) Activate();
            if (_opt.Shot != null || _opt.SelfTest || _opt.Script != null || _opt.Page != null)
            {
                await _uiReady.Task;
                await RunScriptAsync(_opt.Script);
                if (_opt.Page != null) await RunScriptAsync("page:" + _opt.Page);
            }
            if (_opt.SelfTest) await SelfTest.Run(this);
            if (_opt.Shot != null) { await Task.Delay(1400); Shoot(_opt.Shot); }
            if (_opt.ExitAfterMs > 0) { await Task.Delay(_opt.ExitAfterMs); await QuitAsync(); }
        }
        catch (Exception ex)
        {
            AppLog.Error("startup failed", ex);
            App.ExitCode = 1;
        }
    }

    void SetupHosts()
    {
        Host.Hotkeys = new HotkeyHost(Hwnd, Host.Core.Hotkeys);
        Host.Tray = new TrayHost(Hwnd, Host.Core, () => Ui.Post(null, ShowFromTray), () => Ui.Post(null, async () => await QuitAsync()));
        Host.Tray.Add();
        Host.Toast.Register();
        if (!_opt.AllowMulti)
        {
            _server = new InstanceServer
            {
                OnShow = () => Ui.Post(null, ShowFromTray),
                OnCommand = async words =>
                {
                    var r = await Host.Core.ExecuteCommandAsync(words);
                    return (r.ExitCode, r.Output);
                },
            };
            _server.Start();
        }
        // A retry for a shell that was not ready: when the icon could not be added at start, try again a few times.
        if (!Host.Tray.IsAdded)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(3);
            int tries = 0;
            timer.Tick += (_, _) => { if (Host.Tray.IsAdded || ++tries > 20) timer.Stop(); else Host.Tray.Add(); };
            timer.Start();
        }
    }

    (bool, nint) WindowProc(uint msg, nint wParam, nint lParam, nint hwnd)
    {
        if (msg == Native.WM_HOTKEY) { WindowProcHotkeys++; return (Host.Hotkeys?.OnHotkey((int)wParam) == true, 0); }
        if (msg == Native.WM_APP_TRAY) { WindowProcTray++; return (Host.Tray?.OnCallback(lParam) == true, 0); }
        if (Host.Tray != null && msg == Host.Tray.TaskbarCreatedMessage && msg != 0) { Host.Tray.ReAdd(); return (false, 0); }
        return (false, 0);
    }

    // ---------------------------------------------------------------- show, hide, quit

    internal void ShowFromTray()
    {
        ShowRequests++;
        AppWindow.Show();
        Activate();
        Native.SetForegroundWindow(Hwnd);
    }

    void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_quitting) return;
        if (Host.Tray?.IsAdded == true)
        {
            args.Cancel = true;     // the close button hides to the tray, as in xtu-profile-switcher; Quit is in the tray menu
            AppWindow.Hide();
            AppLog.Info("window hidden to the tray");
        }
        else
        {
            args.Cancel = true;
            _ = QuitAsync();
        }
    }

    internal async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        AppLog.Info("quitting");
        try { await Task.Run(Host.Core.Shutdown); } catch (Exception ex) { AppLog.Error("shutdown failed", ex); }
        Host.Hotkeys?.UnregisterAll();
        Host.Tray?.Remove();
        Host.Toast.Unregister();
        _server?.Dispose();
        _scope?.Dispose();
        Application.Current.Exit();
    }

    // ---------------------------------------------------------------- loaded, first page

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_uiBuilt) return;
        _uiBuilt = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendered += (_, _) => { if (++_frames == 1) AppLog.Info("first frame rendered"); };
        ApplyTitleBarColors();
        ApplyLanguage();
        _uiReady.TrySetResult();
    }

    void ApplyLanguage()
    {
        if (!_uiBuilt) return;
        Search.PlaceholderText = Host.T("search.placeholder");
        BuildNav();
        RebuildSearchIndex();
        UpdateStatus();
        ShowCurrent();
    }

    readonly HashSet<string> _watched = new();

    /// <summary>Follows every module's Changed event (any thread) so bays, navigation and the open page stay true.</summary>
    void WatchEntries()
    {
        foreach (var e in Host.Core.Modules)
            if (_watched.Add(e.Id)) e.Changed += _ => Ui.Post(null, OnEntryChanged);
        OnEntryChanged();
    }

    void OnEntryChanged()
    {
        if (!_uiBuilt) return;
        BuildNav();
        RebuildSearchIndex();
        UpdateStatus();
        // Rebuild the page when it shows something this change may affect.
        ShowCurrent();
    }

    void OnModuleFaulted(ModuleEntry e)
    {
        Ui.Post(null, () =>
        {
            if (!AppWindow.IsVisible) Host.Toast.Show(Host.F("toast.faulted-title", e.DisplayName), e.ReasonText());
        });
    }

    // ---------------------------------------------------------------- theme

    internal void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };
        if (_uiBuilt) ApplyTitleBarColors();
    }

    bool Dark => Root.ActualTheme == ElementTheme.Dark;

    void ApplyTitleBarColors()
    {
        var t = AppWindow.TitleBar;
        var fg = Dark ? Colors.White : Colors.Black;
        t.ButtonBackgroundColor = Colors.Transparent; t.ButtonInactiveBackgroundColor = Colors.Transparent;
        t.ButtonForegroundColor = fg; t.ButtonHoverForegroundColor = fg; t.ButtonPressedForegroundColor = fg;
        t.ButtonHoverBackgroundColor = Dark ? Color.FromArgb(25, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
        t.ButtonInactiveForegroundColor = Dark ? Color.FromArgb(255, 160, 160, 160) : Color.FromArgb(255, 96, 96, 96);
    }

    // ---------------------------------------------------------------- navigation

    static string GroupGlyph(string g) => g switch
    {
        "power" => "", "cooling" => "", "network" => "", "display" => "", "monitoring" => "", _ => "",
    };

    static NavigationViewItem Item(string text, string glyph, object tag) =>
        new() { Content = text, Tag = tag, Icon = new FontIcon { Glyph = glyph } };

    void BuildNav()
    {
        _navSuppress = true;
        Nav.MenuItems.Clear();
        Nav.FooterMenuItems.Clear();
        Nav.MenuItems.Add(Item(Host.T("nav.dashboard"), "", "dashboard"));
        var entries = Host.Core.Modules;
        var groups = GroupOrder.Select(g => (g, items: entries.Where(e => e.GroupKey() == g).ToList())).Where(x => x.items.Count > 0).ToList();
        if (groups.Count > 0) Nav.MenuItems.Add(new NavigationViewItemSeparator());
        foreach (var (g, items) in groups)
        {
            var header = new NavigationViewItem { Content = Host.T("group." + g), SelectsOnInvoked = false, IsExpanded = true, Icon = new FontIcon { Glyph = GroupGlyph(g) } };
            foreach (var e in items)
            {
                var item = new NavigationViewItem { Content = e.DisplayName, Tag = e, Icon = new FontIcon { Glyph = ModuleGlyph(e) } };
                var badge = e.Bay.State switch
                {
                    BayState.Ready => "SuccessDotInfoBadgeStyle",
                    BayState.Failed => "CriticalDotInfoBadgeStyle",
                    _ => null,
                };
                if (badge != null) item.InfoBadge = new InfoBadge { Style = (Style)Application.Current.Resources[badge] };
                header.MenuItems.Add(item);
            }
            Nav.MenuItems.Add(header);
        }
        Nav.FooterMenuItems.Add(Item(Host.T("nav.modules"), "", "modules"));
        Nav.FooterMenuItems.Add(Item(Host.T("nav.settings"), "", "settings"));
        Nav.FooterMenuItems.Add(Item(Host.T("nav.log"), "", "log"));
        _navSuppress = false;
        SelectInNav(_current);
    }

    static string ModuleGlyph(ModuleEntry e) =>
        e.Page?.Icon is { Length: 1 } one ? one : GroupGlyph(e.GroupKey());

    IEnumerable<NavigationViewItem> AllNavItems() =>
        Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().SelectMany(i => new[] { i }.Concat(i.MenuItems.OfType<NavigationViewItem>()));

    void SelectInNav(object tag)
    {
        _navSuppress = true;
        Nav.SelectedItem = AllNavItems().FirstOrDefault(i => Equals(i.Tag, tag));
        _navSuppress = false;
    }

    void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_navSuppress) return;
        if ((args.SelectedItem as NavigationViewItem)?.Tag is { } tag) Navigate(tag);
    }

    /// <summary>Shows a page: "dashboard", "modules", "settings", "log", or a module entry.</summary>
    internal void Navigate(object target)
    {
        _current = target;
        SelectInNav(target);
        ShowCurrent();
    }

    void ShowCurrent()
    {
        if (!_uiBuilt) return;
        _scope?.Dispose();
        var scope = _scope = new PageScope();
        LastRenderer = null;
        UIElement content;
        try
        {
            content = _current switch
            {
                ModuleEntry e => ModulePage.Build(e, scope, out LastRenderer),
                "modules" => ModulesPage.Build(scope),
                "settings" => SettingsPage.Build(scope),
                "log" => LogPage.Build(scope),
                _ => DashboardPage.Build(scope),
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("building the page failed", ex);
            content = Ui.StateBlock(BayState.Failed, Host.T("page.build-failed"), ex.GetType().Name + ": " + ex.Message);
        }
        PageHost.Content = content;
    }

    // ---------------------------------------------------------------- search

    void RebuildSearchIndex()
    {
        var list = new List<SearchItem>
        {
            new(Host.T("nav.dashboard"), "", "dashboard"),
            new(Host.T("nav.modules"), "", "modules"),
            new(Host.T("nav.settings"), "", "settings"),
            new(Host.T("nav.log"), "", "log"),
        };
        foreach (var key in new[] { "settings.language", "settings.theme", "settings.start", "settings.log-level", "settings.hotkeys" })
            list.Add(new SearchItem(Host.T(key), Host.T(key) + "  ·  " + Host.T("nav.settings"), "settings"));
        foreach (var e in Host.Core.Modules)
        {
            list.Add(new SearchItem(e.DisplayName, e.DisplayName, e));
            if (e.DisplayDescription.Length > 0) list.Add(new SearchItem(e.DisplayDescription, e.DisplayName, e));
            var cachedPage = e.LastPage;
            if (cachedPage == null) continue;
            foreach (var row in cachedPage.EnumerateRows())
            {
                if (row.HeaderKey.Length == 0) continue;
                var header = e.Localisation.T(row.HeaderKey);
                var desc = row.DescriptionKey is { Length: > 0 } d ? e.Localisation.T(d) : "";
                list.Add(new SearchItem(header + (desc.Length > 0 ? " – " + desc : ""), $"{header}  ·  {e.DisplayName}", e));
            }
        }
        SearchIndex = list;
    }

    internal IEnumerable<SearchItem> SearchFor(string q) =>
        q.Length == 0 ? Enumerable.Empty<SearchItem>() : SearchIndex.Where(i => i.Text.Contains(q, StringComparison.OrdinalIgnoreCase)).DistinctBy(i => i.Label).Take(8);

    void OnSearchTextChanged(AutoSuggestBox s, AutoSuggestBoxTextChangedEventArgs a)
    {
        if (a.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        s.ItemsSource = SearchFor(s.Text.Trim()).ToList();
    }

    void OnSearchChosen(AutoSuggestBox s, AutoSuggestBoxSuggestionChosenEventArgs a) { if (a.SelectedItem is SearchItem h) Navigate(h.Target); }

    void OnSearchSubmitted(AutoSuggestBox s, AutoSuggestBoxQuerySubmittedEventArgs a)
    {
        if (a.ChosenSuggestion is SearchItem h) Navigate(h.Target);
        else if (SearchFor(a.QueryText.Trim()).FirstOrDefault() is { } first) Navigate(first.Target);
    }

    // ---------------------------------------------------------------- status line

    internal static string RuntimeVersion()
    {
        try { return Microsoft.Windows.ApplicationModel.WindowsAppRuntime.RuntimeInfo.AsString; }
        catch (Exception ex) { return "? (" + ex.GetType().Name + ")"; }
    }

    internal string StatusLine()
    {
        var winui = FileVersionInfo.GetVersionInfo(typeof(Window).Assembly.Location).ProductVersion?.Split('+')[0];
        var elev = Native.IsUserAnAdmin() ? Host.T("status.admin") : Host.T("status.not-admin");
        return $"Rigbay {AppPaths.Version}  ·  API {HostApi.Current}  ·  Windows App SDK {RuntimeVersion()} (WinUI {winui})  ·  .NET {Environment.Version}  ·  {elev}";
    }

    void UpdateStatus() => StatusText.Text = StatusLine();

    // ---------------------------------------------------------------- script steps (tests and screenshots)

    /// <summary>
    /// Steps separated by ';': <c>enable:id</c>, <c>disable:id</c>, <c>page:dashboard|modules|settings|log|id</c>, <c>lang:code</c>,
    /// <c>theme:light|dark|system</c>, <c>ack:id</c>, <c>wait:ms</c>, <c>approve:true|false</c>.
    /// </summary>
    internal async Task RunScriptAsync(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return;
        foreach (var raw in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = raw.IndexOf(':');
            var verb = i < 0 ? raw : raw[..i];
            var arg = i < 0 ? "" : raw[(i + 1)..];
            AppLog.Info("script: " + raw);
            switch (verb)
            {
                case "enable": { var e = Host.Core.Find(arg); if (e != null) { AppState.Acknowledge(e); await Task.Run(() => e.Enable()); } break; }
                case "disable": { var e = Host.Core.Find(arg); if (e != null) await Task.Run(() => e.Disable()); break; }
                case "page":
                    if (arg is "dashboard" or "modules" or "settings" or "log") Navigate(arg);
                    else if (Host.Core.Find(arg) is { } pe) Navigate(pe);
                    break;
                case "lang": Host.Core.Languages.SetLanguage(arg); break;
                case "theme": ApplyTheme(arg); break;
                case "approve": ApprovalPrompt.TestAnswer = arg == "true"; break;
                case "startup":   // startup:on | startup:off  (the same code the Settings switch runs)
                {
                    var (ok, msg) = await Task.Run(() => arg == "on" ? StartupTask.Create() : StartupTask.Delete());
                    AppLog.Info($"script: start with Windows {arg}: ok={ok} {msg}");
                    break;
                }
                case "cancel-dialog": ApprovalPrompt.CancelOpen(); break;
                case "edit":   // edit:<row header key>=<value> on the open module page; not awaited, so an approval dialog can stay open for a screenshot
                {
                    var eq = arg.IndexOf('=');
                    var driver = LastRenderer?.Drivers.FirstOrDefault(d => d.Row.HeaderKey == (eq < 0 ? arg : arg[..eq]));
                    if (driver == null) { AppLog.Warn("script: no row '" + arg + "' on this page"); break; }
                    _ = driver.Edit(eq < 0 ? null : ParseRowValue(driver.Row, arg[(eq + 1)..]));
                    break;
                }
                case "wait": if (int.TryParse(arg, out var ms)) await Task.Delay(ms); break;
            }
            await Task.Delay(250);
        }
    }

    internal static object? ParseRowValue(Row row, string text) => row switch
    {
        ToggleRow => text is "true" or "on" or "1",
        NumberRow => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
        ShortcutRow => HotkeyGesture.TryParse(text, out var g) ? (HotkeyGesture?)g : null,
        _ => text,
    };

    // ---------------------------------------------------------------- capture of this window only

    internal void Shoot(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Native.CaptureWindow(Hwnd, path, out var info);
        AppLog.Info($"shot (PrintWindow of the window) {info} -> {path}");
    }
}

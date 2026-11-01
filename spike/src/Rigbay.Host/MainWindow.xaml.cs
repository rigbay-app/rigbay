using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Rigbay.Abstractions;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Rigbay.Host;

sealed class HostServices : IHostServices
{
    readonly Loc _loc;
    public DispatcherQueue? Dq;
    public HostServices(Loc loc) => _loc = loc;
    public Version HostVersion => new(0, 1, 0);
    public Version AbstractionsVersion => AbstractionsInfo.Current;
    public void Log(string moduleId, string message) => Host.Log.W($"module {moduleId}: {message}");
    public string T(string key) => _loc.T(key);
    public void RunOnUi(Action a) => Dq?.TryEnqueue(() => { try { a(); } catch (Exception ex) { Host.Log.W($"RunOnUi action threw {ex.GetType().Name}: {ex.Message}"); } });
}

public sealed partial class MainWindow : Window
{
    static readonly string[] GroupOrder = { "Power", "Cooling", "Network", "Display", "Monitoring", "Other" };

    readonly Options _opt;
    internal Options Opt => _opt;
    internal readonly Loc Loc = new();
    internal readonly HostServices Services;
    internal List<ModuleEntry> Entries = new();
    internal string ModulesDir;
    internal nint Hwnd;
    internal PageRenderer? LastRenderer;
    internal List<(string Text, string Where, ModuleEntry Entry)> SearchIndex = new();
    public int HotkeyHits;
    int _frames;
    object? _current;   // "dashboard" or a ModuleEntry

    internal MainWindow(Options opt)
    {
        _opt = opt;
        Services = new HostServices(Loc);
        InitializeComponent();
        Services.Dq = DispatcherQueue;

        Hwnd = WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;   // 48 px
        SetTitleBar(TitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1240, 800));
        AppWindow.Title = "Rigbay";

        if (opt.Theme == "dark") Root.RequestedTheme = ElementTheme.Dark;
        else if (opt.Theme == "light") Root.RequestedTheme = ElementTheme.Light;
        if (opt.Lang != null) Loc.Set(opt.Lang);

        ModulesDir = opt.ModulesDir ?? Path.Combine(AppContext.BaseDirectory, "modules");
        var sw = Stopwatch.StartNew();
        Entries = ModuleLoader.LoadAll(ModulesDir);
        foreach (var e in Entries.Where(x => x.State == ModuleState.Ready && x.Module is IPageModelProvider))
        {
            try { e.Model = ((IPageModelProvider)e.Module!).GetPageModel(Services); }
            catch (Exception ex) { e.State = ModuleState.Failed; e.Reason = ex.GetType().Name + ": " + ex.Message; }
        }
        SearchIndex = Entries.SelectMany(e => new[] { (e.Name, e.Name, e) }
            .Concat(e.Model == null ? Enumerable.Empty<(string, string, ModuleEntry)>() : PageRenderer.Index(e.Model).Select(i => (i.Text, i.Where, e)))).ToList();
        Log.W($"modules scanned in {sw.ElapsedMilliseconds} ms: " + string.Join(", ", Entries.Select(e => $"{e.Id}={e.State}")) + $"; search index {SearchIndex.Count} entries");

        Root.ActualThemeChanged += (_, _) => { BuildNav(); };
        Root.Loaded += OnLoaded;
        TitleBar.SizeChanged += (_, _) => RightInsetColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / Math.Max(1, Root.XamlRoot?.RasterizationScale ?? 1) + 8);
        Closed += (_, _) => { Native.TrayRemove(Hwnd); Native.UnregisterHotKey(Hwnd, 1); Log.W("window closed"); };
        Native.Subclass(Hwnd, (msg, w, l) =>
        {
            if (msg == Native.WM_HOTKEY) { HotkeyHits++; Log.W("WM_HOTKEY received id=" + w); return true; }
            if (msg == Native.WM_APP_TRAY) { Log.W("tray callback lParam=0x" + l.ToString("x")); return true; }
            return false;
        });
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log.W("content loaded");
        CompositionTarget.Rendered += (_, _) => { if (++_frames == 1) Log.W("first frame rendered"); };

        // Build every page up front so a module that cannot build its page shows as failed on the dashboard, not on first click.
        foreach (var m in Entries.Where(x => x.State == ModuleState.Ready && x.Model == null).ToList()) EnsurePage(m);
        ApplyTitleBarColors();
        Root.ActualThemeChanged += (_, _) => ApplyTitleBarColors();
        ApplyLanguage();
        var start = _opt.StartPage;
        var target = Entries.FirstOrDefault(x => x.Id == start);
        if (target != null) SelectEntry(target); else SelectDashboard();

        if (_opt.SelfTest) { await Task.Delay(800); await SelfTest.Run(this); }
        if (_opt.Shot != null) { await Task.Delay(1500); await Shoot(_opt.Shot); }
        if (_opt.ExitAfterMs > 0) { await Task.Delay(_opt.ExitAfterMs); Log.W("exit requested"); Application.Current.Exit(); }
    }

    // ---------- colours (state colours have fixed meaning; everything else is theme resources) ----------
    bool Dark => Root.ActualTheme == ElementTheme.Dark;
    static Brush B(byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(255, r, g, b));
    Brush OkColor => Dark ? B(0x6C, 0xCB, 0x5F) : B(0x0F, 0x7B, 0x0F);
    Brush BadColor => Dark ? B(0xFF, 0x99, 0xA4) : B(0xC4, 0x2B, 0x1C);
    Brush MuteColor => Dark ? B(0x9D, 0x9D, 0x9D) : B(0x60, 0x60, 0x60);
    static Brush None => new SolidColorBrush(Colors.Transparent);
    Style SecCaption => (Style)Root.Resources["SecondaryCaption"];
    Style SecBody => (Style)Root.Resources["SecondaryBody"];

    /// <summary>The bay mark: filled = ready, filled red = failed, outline = unsupported/empty.</summary>
    Microsoft.UI.Xaml.Shapes.Rectangle Slot(ModuleState? s, double h = 18)
    {
        var (fill, stroke) = s switch { ModuleState.Ready => (OkColor, None), ModuleState.Failed => (BadColor, None), _ => (None, MuteColor) };
        return new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 5, Height = h, RadiusX = 1, RadiusY = 1, Fill = fill, Stroke = stroke, StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center };
    }
    string StateWord(ModuleEntry e) => e.State switch { ModuleState.Ready => Loc.T("state_ready"), ModuleState.Failed => Loc.T("state_failed"), _ => Loc.T("state_unsupported") };

    // ---------- navigation ----------
    bool _navSuppress;

    void BuildNav()
    {
        _navSuppress = true;
        Nav.MenuItems.Clear();
        Nav.MenuItems.Add(new NavigationViewItem { Content = Loc.T("dashboard"), Tag = "dashboard", Icon = new FontIcon { Glyph = "" } });
        Nav.MenuItems.Add(new NavigationViewItemSeparator());
        foreach (var g in GroupOrder)
        {
            var inGroup = Entries.Where(e => (e.Module?.Group ?? "Other") == g).ToList();
            if (inGroup.Count == 0) continue;
            var header = new NavigationViewItem { Content = g, SelectsOnInvoked = false, IsExpanded = true, Icon = new FontIcon { Glyph = GroupGlyph(g) } };
            foreach (var e in inGroup)
            {
                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                label.Children.Add(Slot(e.State));
                label.Children.Add(new TextBlock { Text = e.Name, VerticalAlignment = VerticalAlignment.Center });
                header.MenuItems.Add(new NavigationViewItem { Content = label, Tag = e });
            }
            Nav.MenuItems.Add(header);
        }
        _navSuppress = false;
        Select(_current);
    }

    static string GroupGlyph(string g) => g switch { "Power" => "", "Cooling" => "", "Network" => "", "Display" => "", "Monitoring" => "", _ => "" };

    void Select(object? tag)
    {
        _navSuppress = true;
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if (Equals(item.Tag, tag)) Nav.SelectedItem = item;
            foreach (var child in item.MenuItems.OfType<NavigationViewItem>()) if (Equals(child.Tag, tag)) Nav.SelectedItem = child;
        }
        _navSuppress = false;
    }

    void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_navSuppress) return;
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag;
        if (tag is ModuleEntry e) ShowEntry(e); else if (tag as string == "dashboard") ShowDashboard();
    }

    internal void SelectDashboard() { _current = "dashboard"; ShowDashboard(); Select(_current); }
    internal void SelectEntry(ModuleEntry e) { _current = e; ShowEntry(e); Select(e); }

    // ---------- search ----------
    void OnSearchTextChanged(AutoSuggestBox s, AutoSuggestBoxTextChangedEventArgs a)
    {
        if (a.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var q = s.Text.Trim();
        s.ItemsSource = q.Length == 0 ? null : SearchIndex.Where(i => i.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(i => new SearchHit(i.Where == i.Entry.Name ? i.Entry.Name : $"{i.Where}  ·  {i.Entry.Name}", i.Entry)).Take(8).ToList();
    }
    void OnSearchChosen(AutoSuggestBox s, AutoSuggestBoxSuggestionChosenEventArgs a) { if (a.SelectedItem is SearchHit h) SelectEntry(h.Entry); }
    void OnSearchSubmitted(AutoSuggestBox s, AutoSuggestBoxQuerySubmittedEventArgs a) { if (a.ChosenSuggestion is SearchHit h) SelectEntry(h.Entry); }
    sealed record SearchHit(string Label, ModuleEntry Entry) { public override string ToString() => Label; }

    // ---------- language, status ----------
    void ApplyLanguage()
    {
        Search.PlaceholderText = Loc.T("search");
        var other = Loc.Languages.FirstOrDefault(l => l.Code != Loc.Code);
        LangButton.Content = other.Name ?? "";
        BuildNav();
        UpdateStatus();
        if (_current is ModuleEntry e) ShowEntry(e); else ShowDashboard();
    }

    void OnLangClick(object sender, RoutedEventArgs e)
    {
        var other = Loc.Languages.FirstOrDefault(l => l.Code != Loc.Code);
        if (other.Code == null) return;
        Loc.Set(other.Code);
        ApplyLanguage();
        Log.W("language switched to " + other.Code);
    }

    internal static string RuntimeVersion()
    {
        try { return Microsoft.Windows.ApplicationModel.WindowsAppRuntime.RuntimeInfo.AsString; }
        catch (Exception ex) { return "? (" + ex.GetType().Name + ")"; }
    }

    void UpdateStatus()
    {
        var winui = FileVersionInfo.GetVersionInfo(typeof(Window).Assembly.Location).ProductVersion?.Split('+')[0];
        var elev = Native.IsUserAnAdmin() ? Loc.T("status_elevated") : Loc.T("status_not_elevated");
        StatusText.Text = $"Host 0.1.0  ·  Abstractions {AbstractionsInfo.Current}  ·  Windows App SDK {RuntimeVersion()} (WinUI {winui})  ·  .NET {Environment.Version}  ·  {elev}";
    }

    // ---------- pages ----------

    UIElement Header(string title, string meta, params UIElement[] right)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        if (meta.Length > 0) left.Children.Add(new TextBlock { Text = meta, Style = SecCaption });
        g.Children.Add(left);
        var r = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        foreach (var x in right) r.Children.Add(x);
        Grid.SetColumn(r, 1); g.Children.Add(r);
        return g;
    }

    /// <summary>The bay rack: one slim row per module with its state and, when it has one, its first live reading.</summary>
    internal void ShowDashboard()
    {
        _current = "dashboard";
        var page = new StackPanel();
        page.Children.Add(Header(Loc.T("dashboard"), ""));
        if (Entries.Count == 0)
        {
            page.Children.Add(StateBlock(Loc.T("no_modules_title"), string.Format(Loc.T("no_modules_body"), ModulesDir), MuteColor));
            PageHost.Content = page; return;
        }
        page.Children.Add(new TextBlock { Text = Loc.T("bays"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var rack = new StackPanel();
        foreach (var e in Entries) rack.Children.Add(RackRow(e));
        rack.Children.Add(EmptyBayRow());
        page.Children.Add(rack);
        PageHost.Content = page;
    }

    UIElement RackRow(ModuleEntry e)
    {
        var g = new Grid { MinHeight = 56, ColumnSpacing = 14, Padding = new Thickness(4, 6, 4, 6), Background = None };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Slot(e.State, 32));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = e.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = e.State == ModuleState.Ready ? $"{StateWord(e)} · v{e.Version}" : $"{StateWord(e)}: {e.Reason}";
        text.Children.Add(new TextBlock { Text = detail, Style = SecCaption, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1); g.Children.Add(text);
        var reading = e.Model?.Groups.SelectMany(x => x.Rows).OfType<ReadingRow>().FirstOrDefault();
        if (reading != null)
        {
            var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Style = (Style)Root.Resources["PrimaryBody"] };
            Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
            void Set() => tb.Text = $"{reading.Header}  {reading.Value.Value} {reading.Unit}";
            Set();
            reading.Value.Changed += () => Services.RunOnUi(Set);
            Grid.SetColumn(tb, 2); g.Children.Add(tb);
        }
        var wrap = new Border { Style = (Style)Root.Resources["Hairline"], Child = g };
        wrap.Tapped += (_, _) => SelectEntry(e);
        return wrap;
    }

    UIElement EmptyBayRow()
    {
        var g = new Grid { MinHeight = 56, ColumnSpacing = 14, Padding = new Thickness(4, 6, 4, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(Slot(null, 32));
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(new TextBlock { Text = Loc.T("empty_bay"), Style = SecBody });
        t.Children.Add(new TextBlock { Text = Loc.T("empty_bay_hint"), Style = SecCaption });
        Grid.SetColumn(t, 1); g.Children.Add(t);
        return g;
    }

    void ApplyTitleBarColors()
    {
        var t = AppWindow.TitleBar;
        var fg = Dark ? Colors.White : Colors.Black;
        t.ButtonBackgroundColor = Colors.Transparent; t.ButtonInactiveBackgroundColor = Colors.Transparent;
        t.ButtonForegroundColor = fg; t.ButtonHoverForegroundColor = fg; t.ButtonPressedForegroundColor = fg;
        t.ButtonHoverBackgroundColor = Dark ? Color.FromArgb(25, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
        t.ButtonInactiveForegroundColor = Dark ? Color.FromArgb(255, 160, 160, 160) : Color.FromArgb(255, 96, 96, 96);
    }

    /// <summary>Creates the level-2 page once (level-1 modules already have a model). A throw marks the module failed.</summary>
    void EnsurePage(ModuleEntry e)
    {
        if (e.State != ModuleState.Ready || e.Page != null || e.Model != null) return;
        try
        {
            var created = e.Module!.CreatePage(Services);              // fault boundary
            if (created is not UIElement) throw new InvalidCastException($"CreatePage returned {created?.GetType().FullName ?? "null"}, not a UIElement");
            e.Page = created;
            Log.W($"page created: {e.Id} ({created.GetType().FullName})");
        }
        catch (Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : ex;
            e.State = ModuleState.Failed;
            e.Reason = inner.GetType().Name + ": " + inner.Message;
            Log.W($"page FAILED: {e.Id}: {e.Reason} (HResult 0x{inner.HResult:X8}) :: {inner.ToString().Replace("\r\n", " | ")}");
        }
    }

    internal void ShowEntry(ModuleEntry e)
    {
        _current = e;
        var page = new StackPanel();
        var meta = e.Version.Length > 0 ? $"{e.Id}  ·  v{e.Version}  ·  {e.LoadContext}" : e.Id;

        EnsurePage(e);

        switch (e.State)
        {
            case ModuleState.Ready when e.Model != null:
            {
                var links = e.Model.Links.Select(l => (UIElement)new HyperlinkButton { Content = l.Text, NavigateUri = new Uri(l.Url) }).ToArray();
                page.Children.Add(Header(e.Model.Title, meta, links));
                var r = new PageRenderer(DispatcherQueue, (IPageModelProvider)e.Module!, Services, e.Id);
                try { page.Children.Add(r.Build(e.Model)); LastRenderer = r; Log.W($"declarative page rendered: {e.Id}, {r.RowsRendered} rows"); }
                catch (Exception ex) { page.Children.Add(StateBlock(Loc.T("failed_title"), ex.GetType().Name + ": " + ex.Message, BadColor)); Log.W($"render FAILED {e.Id}: {ex}"); }
                break;
            }
            case ModuleState.Ready:
                page.Children.Add(Header(e.Name, meta));
                page.Children.Add((UIElement)e.Page!);
                break;
            case ModuleState.Failed:
                page.Children.Add(Header(e.Name, meta));
                page.Children.Add(StateBlock(Loc.T("failed_title"), e.Reason, BadColor));
                break;
            default:
                page.Children.Add(Header(e.Name, meta));
                page.Children.Add(StateBlock(Loc.T("unsupported_title"), e.Reason, MuteColor));
                break;
        }
        PageHost.Content = page;
    }

    UIElement StateBlock(string title, string body, Brush mark)
    {
        var grid = new Grid { ColumnSpacing = 14, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 4, RadiusX = 1, RadiusY = 1, Fill = mark });
        var text = new StackPanel { Spacing = 6 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    // ---------- capture ----------
    internal async Task Shoot(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (_opt.ShotMode == "rtb")
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(Root);
            var buf = await rtb.GetPixelsAsync();
            var bytes = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(buf);
            for (int i = 0; i < bytes.Length; i += 4) (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
            Native.WritePng(path, bytes, rtb.PixelWidth, rtb.PixelHeight);
            Log.W($"shot (RenderTargetBitmap) {rtb.PixelWidth}x{rtb.PixelHeight} -> {path}");
        }
        else
        {
            Native.CaptureWindow(Hwnd, path, out var info);
            Log.W($"shot (PrintWindow) {info} -> {path}");
        }
    }
}

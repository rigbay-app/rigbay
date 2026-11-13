using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App.Services;

/// <summary>
/// Which of the brand's tray icons fits the taskbar (docs/BRAND.md section 7). The tray is drawn by the shell, so the icon cannot use the
/// Windows accent: a light taskbar gets the ink bay, a dark one the light bay, both with the fixed violet block; high contrast gets the
/// one-colour pair; when the taskbar theme cannot be read the all-slate fallback is used.
/// </summary>
enum TrayLook { Light, Dark, Neutral, HighContrastBlack, HighContrastWhite }

/// <summary>A node of the tray menu, kept as data so it can be tested without showing a menu.</summary>
sealed record TrayMenuNode(string Text, Action? Invoke = null, bool Checked = false, bool Enabled = true, bool Separator = false, IReadOnlyList<TrayMenuNode>? Children = null);

/// <summary>
/// The notification-area icon and its menu: Open, one submenu per module that registered tray items, Quit.
/// Left click opens the window; right click shows the menu. Built from <see cref="TrayRegistry"/> each time it is shown,
/// so a language switch or a module that was just enabled is reflected.
/// </summary>
sealed class TrayHost
{
    readonly nint _hwnd;
    readonly RigbayHost _m;
    readonly Action _open, _quit;
    nint _icon;
    uint _taskbarCreated;
    int _added;
    TrayLook _look = TrayLook.Neutral;
    int _iconSize;

    /// <summary>The look now on the icon.</summary>
    public TrayLook Look => _look;
    /// <summary>Pixel size of the frame Windows was asked for (16, 20, 24 or 32 at 100, 125, 150 and 200 %).</summary>
    public int IconSize => _iconSize;
    /// <summary>How many times the icon was swapped after the first Add (self-test).</summary>
    public int Swaps { get; private set; }
    /// <summary>Whether the shell accepted the last icon change (self-test).</summary>
    public bool LastSwapAccepted { get; private set; } = true;
    /// <summary>When set (self-test), used instead of reading the system.</summary>
    public TrayLook? ForcedLook { get; set; }

    /// <summary>File name (in Assets) of the icon for a look.</summary>
    public static string FileFor(TrayLook look) => look switch
    {
        TrayLook.Light => "rigbay-tray-light.ico",
        TrayLook.Dark => "rigbay-tray-dark.ico",
        TrayLook.HighContrastBlack => "rigbay-hc-black.ico",
        TrayLook.HighContrastWhite => "rigbay-hc-white.ico",
        _ => "rigbay-tray-neutral.ico",
    };

    /// <summary>
    /// The pure choice. <paramref name="systemUsesLightTheme"/> is the <c>SystemUsesLightTheme</c> value (0 dark taskbar, 1 light; null when missing).
    /// In high contrast <paramref name="highContrastIsLight"/> says whether the theme's window colour is light: a light theme gets the black icon.
    /// </summary>
    public static TrayLook Pick(bool highContrast, bool highContrastIsLight, int? systemUsesLightTheme)
    {
        if (highContrast) return highContrastIsLight ? TrayLook.HighContrastBlack : TrayLook.HighContrastWhite;
        return systemUsesLightTheme switch { 0 => TrayLook.Dark, 1 => TrayLook.Light, _ => TrayLook.Neutral };
    }

    /// <summary>Reads the taskbar theme (<c>HKCU\...\Themes\Personalize\SystemUsesLightTheme</c>, not <c>AppsUseLightTheme</c>) and high contrast.</summary>
    public static TrayLook Detect()
    {
        bool hc = false, hcLight = false;
        try
        {
            hc = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            if (hc)
            {
                var bg = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                hcLight = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) > 127;
            }
        }
        catch (Exception ex) { AppLog.Warn("could not read the high contrast setting", ex); }
        int? light = null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("SystemUsesLightTheme") is int v) light = v;
        }
        catch (Exception ex) { AppLog.Warn("could not read the taskbar theme", ex); }
        return Pick(hc, hcLight, light);
    }

    TrayLook CurrentLook() => ForcedLook ?? Detect();

    nint LoadFor(TrayLook look, out int size)
    {
        size = Native.TrayIconSize();
        var name = FileFor(look);
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", name);
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(path)) { AppLog.Warn("tray icon file missing: " + name); return 0; }
        var h = Native.LoadTrayIcon(path, size);
        if (h == 0) AppLog.Warn("tray icon file could not be loaded: " + name);
        return h;
    }

    /// <summary>
    /// Picks the icon again (the taskbar theme, high contrast or the display scale changed). Does nothing when look and size are unchanged.
    /// The new icon goes to the shell first, then the old handle is destroyed; if the new file cannot be loaded the old icon stays.
    /// </summary>
    public void Refresh(string why)
    {
        var look = CurrentLook();
        int size = Native.TrayIconSize();
        if (look == _look && size == _iconSize && _icon != 0) return;
        var fresh = LoadFor(look, out var loadedSize);
        if (fresh == 0) return;
        var old = _icon;
        _icon = fresh;
        bool ok = _added == 1 && Native.TrayModify(_hwnd, _icon, Tip);
        LastSwapAccepted = ok || _added != 1;
        AppLog.Info($"tray icon {FileFor(_look)}@{_iconSize} -> {FileFor(look)}@{loadedSize} ({why}); shell {(_added == 1 ? (ok ? "updated" : "REFUSED the update") : "not added yet")}");
        _look = look; _iconSize = loadedSize;
        Swaps++;
        if (old != 0) Native.DestroyIcon(old);
    }

    public TrayHost(nint hwnd, RigbayHost m, Action open, Action quit)
    {
        _hwnd = hwnd; _m = m; _open = open; _quit = quit;
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    }

    public bool IsAdded => _added == 1;
    public uint TaskbarCreatedMessage => _taskbarCreated;

    /// <summary>The tooltip now on the icon: what <see cref="Compose"/> gave last (TR-1).</summary>
    public string Tip { get; private set; } = "Rigbay";

    /// <summary>The longest tooltip the shell takes (szTip is 128 characters with the terminating zero).</summary>
    public const int MaxTip = TrayTipBuilder.MaxTip;

    TrayTipSource? _tipSource;

    /// <summary>What the tooltip reads from: the host's modules and metrics bus.</summary>
    public TrayTipSource TipSource => _tipSource ??= new TrayTipSource(_m);

    /// <summary>
    /// The tooltip for the current setting (Settings, "Tray tooltip"): Automatic is "Rigbay" and the first two running modules that have something to say
    /// ("Balanced · CPU 82 °C", "Fans Auto · CPU fan 3,448 rpm"); Custom is the owner's list. The Settings preview calls this too, so it shows exactly what
    /// the icon gets. At most <see cref="MaxTip"/> characters.
    /// </summary>
    public TipResult Compose() => TrayTipBuilder.Build(AppState.Settings.TrayTip, TipSource);

    Microsoft.UI.Dispatching.DispatcherQueueTimer? _tipTimer;

    /// <summary>Puts a new tooltip on the icon when the text changed. UI thread. Called by the timer, on a module change and when the setting changes.</summary>
    public void UpdateTip()
    {
        string tip;
        try { tip = Compose().Text; } catch (Exception ex) { AppLog.Warn("tray tip failed", ex); return; }
        if (tip == Tip) return;
        Tip = tip;
        if (_added == 1 && _icon != 0) Native.TrayModify(_hwnd, _icon, Tip);
    }

    /// <summary>Keeps the tooltip current: every 5 s (readings move), and at once when <see cref="UpdateTip"/> is called for a module change.</summary>
    public void StartTipUpdates()
    {
        if (_tipTimer != null) return;
        _tipTimer = Host.Dq.CreateTimer();
        _tipTimer.Interval = TimeSpan.FromSeconds(5);
        _tipTimer.Tick += (_, _) => UpdateTip();
        _tipTimer.Start();
        UpdateTip();
    }

    public bool Add()
    {
        Native.AllowDarkMenus();
        if (_icon == 0)
        {
            _look = CurrentLook();
            _icon = LoadFor(_look, out _iconSize);
        }
        else if (CurrentLook() != _look || Native.TrayIconSize() != _iconSize) Refresh("re-add");
        bool ok = Native.TrayAdd(_hwnd, _icon, Tip);
        _added = ok ? 1 : 0;
        AppLog.Info(ok ? "tray icon added" : "tray icon NOT added (the shell may not be ready; will retry)");
        return ok;
    }

    /// <summary>The shell restarted (or was not up when we first tried): add the icon again.</summary>
    public void ReAdd() => Add();

    public void Remove()
    {
        _tipTimer?.Stop();
        if (_added == 1) Native.TrayRemove(_hwnd);
        _added = 0;
        if (_icon != 0) { Native.DestroyIcon(_icon); _icon = 0; }
    }

    public List<TrayMenuNode> BuildModel()
    {
        var nodes = new List<TrayMenuNode> { new(Host.T("tray.open"), _open), new("", Separator: true) };
        var byModule = _m.Tray.Items.GroupBy(i => i.ModuleId).ToList();
        foreach (var g in byModule)
        {
            var entry = _m.Find(g.Key);
            if (entry is not { Lifecycle: ModuleLifecycle.Running }) continue;
            var children = new List<TrayMenuNode>();
            foreach (var te in g)
            {
                var item = te.Item;
                if (!item.Visible.Value) continue;
                var invoke = te.Invoke;   // already inside the module's fault boundary
                children.Add(new TrayMenuNode(entry.Localisation.T(item.TextKey), () => Task.Run(invoke),
                    Checked: item.Checkable && item.Checked.Value, Enabled: item.Enabled.Value));
            }
            if (children.Count > 0) nodes.Add(new TrayMenuNode(entry.DisplayName, Children: children));
        }
        if (byModule.Count > 0 && nodes.Count > 2) nodes.Add(new("", Separator: true));
        nodes.Add(new TrayMenuNode(Host.T("tray.quit"), _quit));
        return nodes;
    }

    /// <summary>Handles the callback message of the icon. Returns true when it was ours.</summary>
    public bool OnCallback(nint lParam)
    {
        int msg = (int)(lParam & 0xFFFF);
        if (msg == Native.WM_LBUTTONUP || msg == Native.WM_LBUTTONDBLCLK) _open();
        else if (msg == Native.WM_RBUTTONUP || msg == Native.WM_CONTEXTMENU) ShowMenu();
        else if (msg == Native.NIN_BALLOONSHOW) { BalloonsDisplayed++; AppLog.Info("tray balloon: Windows displayed it (NIN_BALLOONSHOW)"); }
        else if (msg == Native.NIN_BALLOONTIMEOUT) AppLog.Info("tray balloon: timed out (NIN_BALLOONTIMEOUT)");
        else if (msg == Native.NIN_BALLOONUSERCLICK) { AppLog.Info("tray balloon: clicked"); _open(); }
        return true;
    }

    /// <summary>Balloons Windows reported as displayed (NIN_BALLOONSHOW).</summary>
    public int BalloonsDisplayed { get; private set; }

    /// <summary>Balloons handed to the shell.</summary>
    public int BalloonsSent { get; private set; }

    /// <summary>
    /// Shows a notification as a balloon from the tray icon (the way an elevated app can: <c>AppNotificationManager</c> is not
    /// supported there). False when there is no icon or the shell refused. Runs the shell call on the UI thread.
    /// </summary>
    public bool ShowBalloon(string title, string body, ToastKind kind)
    {
        if (_added != 1) { AppLog.Warn($"tray balloon NOT shown (the tray icon is not there): {title} / {body}"); return false; }
        uint flags = kind switch { ToastKind.Error => 3u, ToastKind.Warning => 2u, _ => 1u };
        bool ok = false;
        void Send() { ok = Native.TrayBalloon(_hwnd, title, body, flags); if (ok) BalloonsSent++; }
        if (Host.Dq.HasThreadAccess) Send();
        else
        {
            using var done = new ManualResetEventSlim();
            if (!Host.Dq.TryEnqueue(() => { try { Send(); } finally { done.Set(); } })) { AppLog.Warn("tray balloon dropped: the dispatcher is gone"); return false; }
            if (!done.Wait(TimeSpan.FromSeconds(3))) { AppLog.Warn("tray balloon: the UI thread did not take it in 3 s"); return false; }
        }
        if (!ok) AppLog.Warn($"tray balloon NOT shown (Shell_NotifyIcon refused it): {title} / {body}");
        return ok;
    }

    public void ShowMenu()
    {
        var model = BuildModel();
        var ids = new Dictionary<uint, Action>();
        uint next = 1;
        nint Build(IReadOnlyList<TrayMenuNode> nodes)
        {
            var menu = Native.CreatePopupMenu();
            foreach (var n in nodes)
            {
                if (n.Separator) { Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null); continue; }
                if (n.Children != null)
                {
                    var sub = Build(n.Children);
                    Native.AppendMenuW(menu, Native.MF_POPUP | Native.MF_STRING, (nuint)sub, n.Text);
                    continue;
                }
                uint flags = Native.MF_STRING | (n.Enabled ? 0u : Native.MF_GRAYED) | (n.Checked ? Native.MF_CHECKED : 0u);
                if (n.Invoke != null) ids[next] = n.Invoke;
                Native.AppendMenuW(menu, flags, next++, n.Text);
            }
            return menu;
        }
        var root = Build(model);
        try
        {
            Native.GetCursorPos(out var p);
            Native.SetForegroundWindow(_hwnd);   // required, or the menu does not close when the user clicks elsewhere
            int cmd = Native.TrackPopupMenuEx(root, Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_BOTTOMALIGN, p.X, p.Y, _hwnd, 0);
            Native.PostMessage(_hwnd, Native.WM_NULL, 0, 0);
            if (cmd > 0 && ids.TryGetValue((uint)cmd, out var act))
            {
                try { act(); } catch (Exception ex) { AppLog.Error("tray menu action failed", ex); }
            }
        }
        finally { Native.DestroyMenu(root); }
    }
}

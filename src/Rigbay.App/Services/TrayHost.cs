using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App.Services;

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

    public TrayHost(nint hwnd, RigbayHost m, Action open, Action quit)
    {
        _hwnd = hwnd; _m = m; _open = open; _quit = quit;
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    }

    public bool IsAdded => _added == 1;
    public uint TaskbarCreatedMessage => _taskbarCreated;

    string Tip => "Rigbay";

    public bool Add()
    {
        Native.AllowDarkMenus();
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "rigbay.ico");
        if (!File.Exists(ico)) ico = Path.Combine(AppContext.BaseDirectory, "rigbay.ico");
        if (_icon == 0 && File.Exists(ico)) _icon = Native.LoadTrayIcon(ico);
        bool ok = Native.TrayAdd(_hwnd, _icon, Tip);
        _added = ok ? 1 : 0;
        AppLog.Info(ok ? "tray icon added" : "tray icon NOT added (the shell may not be ready; will retry)");
        return ok;
    }

    /// <summary>The shell restarted (or was not up when we first tried): add the icon again.</summary>
    public void ReAdd() => Add();

    public void Remove()
    {
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
        return true;
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

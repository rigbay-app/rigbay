using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.App.Services;

/// <summary>
/// Registers the gestures held by the core's <see cref="HotkeyRegistry"/> with the OS (RegisterHotKey on the host window).
/// A gesture claimed twice (by two modules, or a module and the host) is registered for neither; a gesture the OS refuses
/// (another program holds it) is flagged. A press goes back to <see cref="HotkeyRegistry.Press"/>, which runs the actions.
/// </summary>
sealed class HotkeyHost
{
    readonly nint _hwnd;
    readonly HotkeyRegistry _registry;
    readonly Dictionary<int, HotkeyGesture> _byId = new();
    readonly HashSet<string> _refused = new();
    bool _busy;

    public HotkeyHost(nint hwnd, HotkeyRegistry registry)
    {
        _hwnd = hwnd; _registry = registry;
        registry.Changed += () => { if (!_busy) Ui.Post(null, Rebuild); };
    }

    public int RegisteredCount => _byId.Count;
    public int Hits { get; private set; }
    public bool IsRefused(HotkeyEntry e) => _refused.Contains(e.Key);

    public void Rebuild()
    {
        _busy = true;
        try
        {
            foreach (var id in _byId.Keys.ToList()) Native.UnregisterHotKey(_hwnd, id);
            _byId.Clear();
            _refused.Clear();
            var entries = _registry.Entries;
            var conflicts = _registry.Conflicts.Select(c => c.Gesture).ToHashSet();
            int next = 1;
            foreach (var group in entries.Where(e => e.Gesture.Value != null).GroupBy(e => e.Gesture.Value!.Value))
            {
                var g = group.Key;
                if (conflicts.Contains(g)) continue;
                var (mods, vk) = Translate(g);
                bool ok = vk != 0 && Native.RegisterHotKey(_hwnd, next, mods | Native.MOD_NOREPEAT, vk);
                if (ok) _byId[next++] = g;
                else
                {
                    foreach (var e in group) { _refused.Add(e.Key); e.HasConflict.Value = true; }
                    AppLog.Warn($"hotkey {g} was refused by the system (held by another program)");
                }
            }
        }
        finally { _busy = false; }
    }

    /// <summary>Called from the window procedure. Actions run off the UI thread, each inside its module's boundary.</summary>
    public bool OnHotkey(int id)
    {
        if (!_byId.TryGetValue(id, out var gesture)) return false;
        Hits++;
        AppLog.Info($"hotkey {gesture}");
        _ = Task.Run(() => _registry.Press(gesture));
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _byId.Keys.ToList()) Native.UnregisterHotKey(_hwnd, id);
        _byId.Clear();
    }

    public static (uint Mods, uint Vk) Translate(HotkeyGesture g)
    {
        uint mods = 0;
        if (g.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= Native.MOD_ALT;
        if (g.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods |= Native.MOD_CONTROL;
        if (g.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= Native.MOD_SHIFT;
        if (g.Modifiers.HasFlag(HotkeyModifiers.Win)) mods |= Native.MOD_WIN;
        return (mods, VirtualKey(g.Key));
    }

    public static uint VirtualKey(string key)
    {
        if (key.Length == 1) return char.ToUpperInvariant(key[0]);
        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        if (key.StartsWith("Num") && key.Length == 4 && char.IsAsciiDigit(key[3])) return (uint)(0x60 + (key[3] - '0'));
        return key switch
        {
            "Space" => 0x20, "Enter" => 0x0D, "Tab" => 0x09, "Esc" => 0x1B, "Backspace" => 0x08, "Delete" => 0x2E, "Insert" => 0x2D,
            "Home" => 0x24, "End" => 0x23, "PageUp" => 0x21, "PageDown" => 0x22, "Left" => 0x25, "Up" => 0x26, "Right" => 0x27, "Down" => 0x28,
            _ => 0,
        };
    }
}

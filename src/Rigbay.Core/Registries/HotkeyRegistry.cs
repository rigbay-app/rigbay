using System.Text.Json;
using System.Text.RegularExpressions;
using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>A registered hotkey action (module id or <c>host</c> plus action id).</summary>
public sealed class HotkeyEntry : IHotkeyBinding
{
    internal HotkeyEntry(string moduleId, string actionId, string nameKey, HotkeyGesture? @default, Action invoke, HotkeyGesture? initial)
    {
        ModuleId = moduleId;
        ActionId = actionId;
        NameKey = nameKey;
        Default = @default;
        Invoke = invoke;
        Gesture = new ObservableValue<HotkeyGesture?>(initial);
    }

    public string ModuleId { get; }
    public string ActionId { get; }
    public string NameKey { get; }
    public HotkeyGesture? Default { get; }
    public Action Invoke { get; }
    public ObservableValue<HotkeyGesture?> Gesture { get; }
    public ObservableValue<bool> HasConflict { get; } = new(false);
    public string Key => ModuleId + "." + ActionId;

    IObservableValue<HotkeyGesture?> IHotkeyBinding.Gesture => Gesture;
    IObservableValue<bool> IHotkeyBinding.HasConflict => HasConflict;
}

/// <summary>Several actions (or a host reservation) claiming the same gesture.</summary>
public sealed record HotkeyConflict(HotkeyGesture Gesture, IReadOnlyList<string> Owners);

/// <summary>
/// Hotkeys as data: gestures, conflict detection across modules, and the user's rebindings (persisted). The OS
/// registration belongs to the UI host, which should skip gestures that are in conflict.
/// </summary>
public sealed class HotkeyRegistry
{
    private static readonly Regex ActionPattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly List<HotkeyEntry> _entries = new();
    private readonly Dictionary<HotkeyGesture, string> _reserved = new();
    private readonly string? _persistPath;
    private readonly ILog _log;
    private Dictionary<string, string> _overrides = new(StringComparer.Ordinal);

    public HotkeyRegistry(ILog log, string? persistPath = null)
    {
        _log = log;
        _persistPath = persistPath;
        LoadOverrides();
    }

    /// <summary>Raised after entries, gestures or conflicts changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<HotkeyEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public IReadOnlyList<HotkeyConflict> Conflicts
    {
        get
        {
            lock (_gate)
            {
                return OwnersByGesture().Where(kv => kv.Value.Count > 1).Select(kv => new HotkeyConflict(kv.Key, kv.Value)).ToArray();
            }
        }
    }

    /// <summary>Claims a gesture for the host itself (for example the show-window hotkey) so modules conflict with it.</summary>
    public void ReserveHost(string name, HotkeyGesture gesture)
    {
        lock (_gate) _reserved[gesture] = name;
        Recompute();
    }

    public HotkeyEntry Register(string moduleId, string actionId, string nameKey, HotkeyGesture? @default, Action invoke)
    {
        if (actionId is null || !ActionPattern.IsMatch(actionId)) throw new ArgumentException($"'{actionId}' is not a valid hotkey action id (lowercase letters, digits, dashes).", nameof(actionId));
        HotkeyEntry entry;
        lock (_gate)
        {
            if (_entries.Any(e => e.ModuleId == moduleId && e.ActionId == actionId))
                throw new ArgumentException($"Hotkey action '{actionId}' is already registered by module '{moduleId}'.", nameof(actionId));
            var initial = @default;
            if (_overrides.TryGetValue(moduleId + "." + actionId, out var saved))
            {
                if (saved.Length == 0) initial = null;
                else if (HotkeyGesture.TryParse(saved, out var g)) initial = g;
            }
            entry = new HotkeyEntry(moduleId, actionId, nameKey, @default, invoke, initial);
            _entries.Add(entry);
        }
        entry.Gesture.Changed += () => OnGestureChanged(entry);
        Recompute();
        return entry;
    }

    /// <summary>Removes everything a module registered.</summary>
    public void RemoveModule(string moduleId)
    {
        lock (_gate) _entries.RemoveAll(e => e.ModuleId == moduleId);
        Recompute();
    }

    /// <summary>
    /// Runs the actions bound to a gesture the OS reported. A gesture in conflict runs nothing. Returns how many actions ran.
    /// Exceptions are the caller's guard's business (module actions are already wrapped in the module's fault boundary).
    /// </summary>
    public int Press(HotkeyGesture gesture)
    {
        HotkeyEntry[] targets;
        lock (_gate)
        {
            var owners = OwnersByGesture();
            if (owners.TryGetValue(gesture, out var list) && list.Count > 1) return 0;
            targets = _entries.Where(e => e.Gesture.Value is { } g && g == gesture).ToArray();
        }
        foreach (var e in targets) e.Invoke();
        return targets.Length;
    }

    private void OnGestureChanged(HotkeyEntry entry)
    {
        lock (_gate)
        {
            var current = entry.Gesture.Value?.ToString() ?? "";
            var defaultText = entry.Default?.ToString() ?? "";
            if (current == defaultText) _overrides.Remove(entry.Key);
            else _overrides[entry.Key] = current;
        }
        SaveOverrides();
        Recompute();
    }

    private Dictionary<HotkeyGesture, List<string>> OwnersByGesture()
    {
        var map = new Dictionary<HotkeyGesture, List<string>>();
        foreach (var (gesture, name) in _reserved)
            map[gesture] = new List<string> { "host:" + name };
        foreach (var e in _entries)
        {
            if (e.Gesture.Value is not { } g) continue;
            if (!map.TryGetValue(g, out var list)) map[g] = list = new List<string>();
            list.Add(e.Key);
        }
        return map;
    }

    private void Recompute()
    {
        List<(HotkeyEntry Entry, bool Conflict)> updates;
        lock (_gate)
        {
            var owners = OwnersByGesture();
            updates = _entries.Select(e => (e, e.Gesture.Value is { } g && owners.TryGetValue(g, out var l) && l.Count > 1)).ToList();
        }
        foreach (var (entry, conflict) in updates) entry.HasConflict.Value = conflict;
        Changed?.Invoke();
    }

    private void LoadOverrides()
    {
        if (_persistPath is null || !File.Exists(_persistPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_persistPath));
            if (loaded is not null) _overrides = new Dictionary<string, string>(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"hotkeys file {_persistPath} unreadable, using defaults: {ex.Message}");
        }
    }

    private void SaveOverrides()
    {
        if (_persistPath is null) return;
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_overrides, JsonSettingsStore.Options);
            Directory.CreateDirectory(Path.GetDirectoryName(_persistPath)!);
            var temp = _persistPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllText(temp, json);
            File.Move(temp, _persistPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"hotkeys file {_persistPath} could not be saved: {ex.Message}");
        }
    }
}

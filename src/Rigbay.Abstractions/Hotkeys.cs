using System.Diagnostics.CodeAnalysis;

namespace Rigbay.Abstractions;

/// <summary>Modifier keys of a <see cref="HotkeyGesture"/>.</summary>
[Flags]
public enum HotkeyModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,
    /// <summary>Control.</summary>
    Ctrl = 1,
    /// <summary>Alt.</summary>
    Alt = 2,
    /// <summary>Shift.</summary>
    Shift = 4,
    /// <summary>The Windows key.</summary>
    Win = 8,
}

/// <summary>
/// A key combination as plain data, written <c>Ctrl+Alt+F5</c>. Keys: <c>A</c>-<c>Z</c>, <c>0</c>-<c>9</c>,
/// <c>F1</c>-<c>F24</c>, <c>Space</c>, <c>Enter</c>, <c>Tab</c>, <c>Esc</c>, <c>Backspace</c>, <c>Delete</c>,
/// <c>Insert</c>, <c>Home</c>, <c>End</c>, <c>PageUp</c>, <c>PageDown</c>, <c>Up</c>, <c>Down</c>, <c>Left</c>,
/// <c>Right</c>, <c>Num0</c>-<c>Num9</c>. <c>F12</c> is rejected: Windows reserves it for debuggers and
/// <c>RegisterHotKey</c> always refuses it (see <see cref="ReservedReason"/>). Two gestures are equal when modifiers and key are equal.
/// </summary>
public readonly record struct HotkeyGesture
{
    private static readonly string[] NamedKeys =
    {
        "Space", "Enter", "Tab", "Esc", "Backspace", "Delete", "Insert", "Home", "End", "PageUp", "PageDown",
        "Up", "Down", "Left", "Right",
    };

    /// <summary>Creates a gesture and checks the key name.</summary>
    /// <param name="modifiers">The modifier keys.</param>
    /// <param name="key">The key name (case-insensitive; stored in canonical form).</param>
    /// <exception cref="ArgumentException">The key name is not in the list, or the key is reserved by Windows (F12).</exception>
    public HotkeyGesture(HotkeyModifiers modifiers, string key)
    {
        Modifiers = modifiers;
        var canonical = Canonical(key) ?? throw new ArgumentException($"'{key}' is not a key name Rigbay knows.", nameof(key));
        if (ReservedReason(canonical) is { } reason) throw new ArgumentException(reason, nameof(key));
        Key = canonical;
    }

    /// <summary>
    /// Why a key can never be a hotkey, or null when it can. Today only <c>F12</c>: Windows keeps it for debuggers, so
    /// <c>RegisterHotKey</c> fails for it with any modifiers. <see cref="TryParse"/> returns false for it and the
    /// constructor throws, so a stored or typed <c>Ctrl+F12</c> never becomes a gesture.
    /// </summary>
    /// <param name="key">A key name in any case.</param>
    public static string? ReservedReason(string? key) =>
        string.Equals(key?.Trim(), "F12", StringComparison.OrdinalIgnoreCase)
            ? "F12 cannot be used as a hotkey: Windows reserves it for debuggers and refuses to register it."
            : null;

    /// <summary>The modifier keys.</summary>
    public HotkeyModifiers Modifiers { get; }

    /// <summary>The canonical key name.</summary>
    public string Key { get; } = "";

    /// <summary>Parses <c>Ctrl+Alt+F5</c> (case-insensitive, any order of modifiers).</summary>
    /// <param name="text">The text.</param>
    /// <param name="gesture">The gesture when the result is true.</param>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = HotkeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Ctrl; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "win" or "windows": modifiers |= HotkeyModifiers.Win; break;
                default: return false;
            }
        }
        var key = Canonical(parts[^1]);
        if (key is null || ReservedReason(key) is not null) return false;
        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    /// <summary>Parses or throws <see cref="FormatException"/>.</summary>
    /// <param name="text">The text.</param>
    public static HotkeyGesture Parse(string text) =>
        TryParse(text, out var g) ? g.Value : throw new FormatException($"'{text}' is not a hotkey like 'Ctrl+Alt+F5'.");

    /// <summary>The text form, modifiers in the order Ctrl, Alt, Shift, Win.</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Key);
        return string.Join('+', parts);
    }

    private static string? Canonical(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        key = key.Trim();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return key.ToUpperInvariant();
        foreach (var named in NamedKeys)
            if (named.Equals(key, StringComparison.OrdinalIgnoreCase)) return named;
        if (key.Equals("Escape", StringComparison.OrdinalIgnoreCase)) return "Esc";
        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24) return "F" + f;
        if (key.Length == 4 && key.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(key[3])) return "Num" + key[3];
        return null;
    }
}

/// <summary>What a module hands to <see cref="IHotkeyService.Register"/>.</summary>
/// <param name="ActionId">Id of the action inside the module (<c>[a-z0-9-]</c>); it identifies the user's rebinding.</param>
/// <param name="NameKey">Module-relative language key of the action's name, shown in the hotkey list.</param>
/// <param name="Default">The gesture before the user changes it, or null for "unbound".</param>
/// <param name="Invoke">Called when the gesture is pressed, in the module's fault boundary, on any thread.</param>
public sealed record HotkeyRegistration(string ActionId, string NameKey, HotkeyGesture? Default, Action Invoke);

/// <summary>A registered hotkey action.</summary>
public interface IHotkeyBinding
{
    /// <summary>The action id given at registration.</summary>
    string ActionId { get; }

    /// <summary>The gesture now in force (the default or the user's choice); null means unbound. The host changes it when the user rebinds.</summary>
    IObservableValue<HotkeyGesture?> Gesture { get; }

    /// <summary>True while another module's action (or the host) holds the same gesture. The host does not register conflicting gestures with the OS.</summary>
    IObservableValue<bool> HasConflict { get; }
}

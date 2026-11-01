using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Rigbay.Abstractions;

namespace Rigbay.Module.Xtu;

/// <summary>Which gestures a profile hotkey may use.</summary>
internal static class HotkeyPolicy
{
    /// <summary>
    /// Returns a language key (module-relative) saying why the gesture is refused, or null when it is fine.
    /// Refused: no Ctrl or Alt (it would steal plain typing), and every combination XTU's own hotkey pairing listens for
    /// (XTU uses a global keyboard hook, so both programs would react to the same keys).
    /// </summary>
    public static string? Refuse(HotkeyGesture gesture, IReadOnlySet<HotkeyGesture> xtuCombos)
    {
        if (!gesture.Modifiers.HasFlag(HotkeyModifiers.Ctrl) && !gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) return "hotkey.needs-modifier";
        if (xtuCombos.Contains(gesture)) return "hotkey.xtu-combo";
        return null;
    }

    /// <summary>
    /// Reads XTU's own hotkeys: nothing unless <c>HotkeyPairingOn</c> is <c>True</c> in <c>uisettings.xml</c>; the binary
    /// <c>xtu.hotkey.settings</c> is read as Latin-1 and scanned for <c>CONTROL+SHIFT+F1</c> style text.
    /// </summary>
    public static HashSet<HotkeyGesture> ReadXtuCombos(string uiSettingsPath, string hotkeySettingsPath)
    {
        var set = new HashSet<HotkeyGesture>();
        try
        {
            if (!PairingOn(uiSettingsPath)) return set;
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(hotkeySettingsPath));
            foreach (Match m in Regex.Matches(text, @"(?:(?:CONTROL|CTRL|SHIFT|ALT)\+)+[A-Za-z0-9]+"))
                if (HotkeyGesture.TryParse(m.Value, out var g)) set.Add(g.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { }
        return set;
    }

    public static bool PairingOn(string uiSettingsPath)
    {
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.Load(uiSettingsPath);
            var pairs = doc.SelectNodes("//NameValuePair");
            if (pairs is null) return false;
            foreach (XmlNode n in pairs)
                if (n.SelectSingleNode("Name")?.InnerText == "HotkeyPairingOn")
                    return string.Equals(n.SelectSingleNode("Value")?.InnerText, "True", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { }
        return false;
    }

    /// <summary>A hotkey action id for a profile name: lowercase letters, digits and dashes, unique among <paramref name="used"/>.</summary>
    public static string ActionId(string profileName, ISet<string> used)
    {
        var sb = new StringBuilder("profile-");
        bool dash = false;
        foreach (var ch in profileName.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch)) { sb.Append(ch); dash = false; }
            else if (!dash) { sb.Append('-'); dash = true; }
        }
        var baseId = sb.ToString().TrimEnd('-');
        if (baseId == "profile") baseId = "profile-x";
        if (baseId.Length > 48) baseId = baseId[..48].TrimEnd('-');
        var id = baseId;
        for (int i = 2; !used.Add(id); i++) id = baseId + "-" + i;
        return id;
    }
}

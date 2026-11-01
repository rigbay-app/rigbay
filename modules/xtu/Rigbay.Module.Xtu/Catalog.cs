using System.Globalization;
using System.Xml;

namespace Rigbay.Module.Xtu;

/// <summary>Builds the list of profiles and finds the active one.</summary>
internal static class Catalog
{
    /// <summary>
    /// "Default (BIOS)" first (from the service's default values, or 90 / 115 / 56 when the service could not be read), then
    /// the profiles from XTU's Profiles.xml, then the profiles created in Rigbay. A name XTU already uses wins over a custom one.
    /// </summary>
    public static List<XtuProfile> Build(XtuState? state, IEnumerable<XtuProfile> fromXtu, IEnumerable<XtuProfile> customs)
    {
        var list = new List<XtuProfile>
        {
            state is null
                ? new XtuProfile(XtuProfile.DefaultName, 90m, 115m, true, 56m, ProfileKind.Builtin)
                : new XtuProfile(XtuProfile.DefaultName, state.DefPl1, state.DefPl2, state.DefPl2Enable, state.DefWindow, ProfileKind.Builtin),
        };
        foreach (var p in fromXtu)
            if (!Taken(list, p.Name)) list.Add(p);
        foreach (var c in customs)
            if (!Taken(list, c.Name)) list.Add(c);
        return list;
    }

    private static bool Taken(List<XtuProfile> list, string name) =>
        list.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first profile whose four values equal the hardware's; null when none does.</summary>
    public static XtuProfile? FindActive(IEnumerable<XtuProfile> profiles, XtuState? state)
    {
        if (state is null) return null;
        return profiles.FirstOrDefault(p => p.Matches(state));
    }

    public static XtuProfile? FindByName(IEnumerable<XtuProfile> profiles, string? name) =>
        string.IsNullOrEmpty(name) ? null : profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads XTU's saved profiles (<c>Profiles.xml</c>).</summary>
internal static class ProfilesXml
{
    /// <summary>
    /// <c>//DatabaseProfile</c> elements with a <c>Name</c> and <c>ControlSettings/*</c> children (<c>Id</c>, <c>Value</c>).
    /// Only profiles that carry all four controls are kept. A missing or unreadable file gives an empty list.
    /// </summary>
    public static List<XtuProfile> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<XtuProfile>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return new List<XtuProfile>();
        }
    }

    public static List<XtuProfile> Parse(Stream xml)
    {
        var result = new List<XtuProfile>();
        var doc = new XmlDocument { XmlResolver = null };
        doc.Load(xml);
        var nodes = doc.SelectNodes("//DatabaseProfile");
        if (nodes is null) return result;
        foreach (XmlNode node in nodes)
        {
            var name = node.SelectSingleNode("Name")?.InnerText;
            var controls = node.SelectSingleNode("ControlSettings");
            if (string.IsNullOrWhiteSpace(name) || controls is null) continue;

            decimal pl1 = 0, pl2 = 0, window = 0;
            bool enable = false, h1 = false, h2 = false, he = false, hw = false;
            foreach (XmlNode c in controls.ChildNodes)
            {
                var idText = c.SelectSingleNode("Id")?.InnerText;
                var valText = c.SelectSingleNode("Value")?.InnerText;
                if (idText is null || valText is null) continue;
                if (!uint.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
                if (!decimal.TryParse(valText, NumberStyles.Any, CultureInfo.InvariantCulture, out var val)) continue;
                switch (id)
                {
                    case XtuIds.Pl1: pl1 = val; h1 = true; break;
                    case XtuIds.Pl2: pl2 = val; h2 = true; break;
                    case XtuIds.Pl2Enable: enable = val != 0m; he = true; break;
                    case XtuIds.Window: window = val; hw = true; break;
                }
            }
            if (h1 && h2 && he && hw) result.Add(new XtuProfile(name, pl1, pl2, enable, window, ProfileKind.Xtu));
        }
        return result;
    }
}

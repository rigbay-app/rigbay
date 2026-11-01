namespace Rigbay.Module.Xtu;

/// <summary>Validation of profile values and names. The ranges are the standalone app's, unchanged.</summary>
internal static class Rules
{
    /// <summary>Throws <see cref="XtuException"/> (a language key) when the values are outside the safe ranges.</summary>
    public static void Validate(XtuProfile p)
    {
        var error = Check(p.Pl1, p.Pl2, p.Window);
        if (error is not null) throw error;
    }

    /// <summary>The first problem with these values, or null when they are inside the ranges.</summary>
    public static XtuException? Check(decimal pl1, decimal pl2, decimal window)
    {
        if (pl1 < Limits.MinPl1 || pl1 > Limits.MaxPl1)
            return new XtuException("val.pl1", Limits.MinPl1, Limits.MaxPl1);
        if (pl2 < pl1 || pl2 > Limits.MaxPl2)
            return new XtuException("val.pl2", Limits.MaxPl2);
        if (window < Limits.MinWindow || window > Limits.MaxWindow)
            return new XtuException("val.window", Limits.MinWindow, Limits.MaxWindow);
        return null;
    }

    /// <summary>
    /// A profile name: 1 to 40 characters, not starting with <c>@</c>, no tab or line break, not used already
    /// (ignoring case). Returns the problem as a key, or null when the name is fine.
    /// </summary>
    /// <param name="name">The proposed name (trimmed by the caller).</param>
    /// <param name="taken">Names in use, except the one being edited.</param>
    public static XtuException? CheckName(string? name, IEnumerable<string> taken)
    {
        if (string.IsNullOrWhiteSpace(name)) return new XtuException("val.name-empty");
        if (name.Length > Limits.MaxNameLength) return new XtuException("val.name-long", Limits.MaxNameLength);
        if (name[0] == '@' || name.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0) return new XtuException("val.name-chars");
        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal)) return new XtuException("val.name-chars");
        foreach (var t in taken)
            if (string.Equals(t, name, StringComparison.OrdinalIgnoreCase)) return new XtuException("val.name-taken", name);
        return null;
    }
}

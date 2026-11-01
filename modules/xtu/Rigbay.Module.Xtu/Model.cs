using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.Module.Xtu;

/// <summary>
/// The only four XTU tuning controls this module may ever write (ids from the XTU contract's control id table).
/// This is a deliberate safety boundary: nothing else is proposed, applied or even asked for by id.
/// </summary>
internal static class XtuIds
{
    /// <summary>PL2, Turbo Boost Short Power Max.</summary>
    public const uint Pl2 = 47;
    /// <summary>PL1, Turbo Boost Power Max.</summary>
    public const uint Pl1 = 48;
    /// <summary>PL2 enable.</summary>
    public const uint Pl2Enable = 49;
    /// <summary>Turbo time window, seconds.</summary>
    public const uint Window = 66;

    /// <summary>The allow-list for every read and write by control id.</summary>
    public static readonly IReadOnlyList<uint> Controls = new uint[] { Pl2, Pl1, Pl2Enable, Window };

    public static bool IsAllowed(uint id) => id is Pl2 or Pl1 or Pl2Enable or Window;
}

/// <summary>Hard limits, independent of what XTU would accept (same numbers as the standalone app).</summary>
internal static class Limits
{
    public const decimal MinPl1 = 15m;
    public const decimal MaxPl1 = 120m;
    public const decimal MaxPl2 = 150m;
    public const decimal MinWindow = 1m;
    public const decimal MaxWindow = 128m;
    public const int MaxNameLength = 40;
    public const decimal Tolerance = 0.01m;
}

internal enum ProfileKind
{
    /// <summary>"Default (BIOS)", built from the control default values.</summary>
    Builtin,
    /// <summary>Read from XTU's Profiles.xml.</summary>
    Xtu,
    /// <summary>Created in Rigbay and kept in the module settings.</summary>
    Custom,
}

/// <summary>The values the hardware has right now (read from XTU), plus the BIOS defaults.</summary>
internal sealed record XtuState(
    decimal Pl1, decimal Pl2, bool Pl2Enable, decimal Window,
    decimal DefPl1, decimal DefPl2, bool DefPl2Enable, decimal DefWindow,
    bool RestoreOnBoot);

/// <summary>A set of power-limit values with a name.</summary>
internal sealed record XtuProfile(string Name, decimal Pl1, decimal Pl2, bool Pl2Enable, decimal Window, ProfileKind Kind)
{
    public const string DefaultName = "Default (BIOS)";

    /// <summary>Same rule as the standalone app: all four values equal, within 0.01.</summary>
    public bool Matches(XtuState s) =>
        Math.Abs(s.Pl1 - Pl1) < Limits.Tolerance
        && Math.Abs(s.Pl2 - Pl2) < Limits.Tolerance
        && s.Pl2Enable == Pl2Enable
        && Math.Abs(s.Window - Window) < Limits.Tolerance;

    /// <summary>Same text as the standalone app's command line: <c>PL1 55 W / PL2 60 W / 56 s</c>.</summary>
    public string Describe() => Fmt.Limits(Pl1, Pl2, Pl2Enable, Window);

    public static XtuProfile FromState(string name, XtuState s, ProfileKind kind = ProfileKind.Custom) =>
        new(name, s.Pl1, s.Pl2, s.Pl2Enable, s.Window, kind);
}

/// <summary>A profile created in Rigbay, as kept in the settings file.</summary>
public sealed class CustomProfile
{
    public string Name { get; set; } = "";
    public double Pl1 { get; set; } = 45;
    public double Pl2 { get; set; } = 65;
    public bool Pl2Enable { get; set; } = true;
    public double Window { get; set; } = 28;

    internal XtuProfile ToProfile() =>
        new(Name, (decimal)Pl1, (decimal)Pl2, Pl2Enable, (decimal)Window, ProfileKind.Custom);
}

/// <summary>Number formatting that never depends on the user's culture.</summary>
internal static class Fmt
{
    public static string N(decimal v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    public static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Limits(decimal pl1, decimal pl2, bool pl2Enable, decimal window) =>
        string.Format(CultureInfo.InvariantCulture, "PL1 {0:0.###} W / PL2 {1:0.###} W{2} / {3:0.###} s",
            pl1, pl2, pl2Enable ? "" : " (off)", window);

    public static string Limits(XtuState s) => Limits(s.Pl1, s.Pl2, s.Pl2Enable, s.Window);
}

/// <summary>
/// An expected failure with a language key (module-relative) and arguments. The text is resolved where it is shown, so it
/// follows the user's language; <see cref="Exception.Message"/> is a plain English line for the log.
/// </summary>
internal class XtuException : Exception
{
    public XtuException(string key, params object[] args) : base(Describe(key, args))
    {
        Key = key;
        Args = args;
    }

    public string Key { get; }
    public object[] Args { get; }

    public LocalisedText ToText() => new(Key, Args);

    private static string Describe(string key, object[] args) =>
        args.Length == 0 ? key : key + ": " + string.Join(", ", args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)));
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Rigbay.App;

/// <summary>
/// The Windows accent rule for the title bar mark (docs/BRAND.md section 6): the block of the Dock mark follows the Windows accent,
/// except when the accent could be read as a state colour. Green, red and amber are state colours in the app, so when the accent's
/// hue is within 30 degrees of one of them the mark is drawn in its quiet form (bay in slate, block in the text colour).
/// </summary>
static class BrandMark
{
    /// <summary>Hues of the state colours (docs/BRAND.md section 5): green, red, amber.</summary>
    static readonly double[] StateHues = { 154, 358, 37 };

    /// <summary>Colours with less saturation than this have no useful hue (greys): they cannot be mistaken for a state.</summary>
    const double MinSaturation = 0.12;

    /// <summary>The Windows accent colour (the base accent the user picked in Settings), or the brand violet when it cannot be read.</summary>
    public static Color WindowsAccent()
    {
        try { return new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent); }
        catch (Exception ex) { AppLog.Warn("could not read the Windows accent colour", ex); return Color.FromArgb(255, 0x7A, 0x5A, 0xF8); }
    }

    /// <summary>Hue in degrees (0 to 360) and HSL saturation (0 to 1) of an sRGB colour.</summary>
    public static (double Hue, double Saturation) HueAndSaturation(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d < 1e-9) return (0, 0);
        double l = (max + min) / 2;
        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? ((g - b) / d) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        if (h < 0) h += 360;
        return (h, s);
    }

    /// <summary>Angular distance between two hues, 0 to 180.</summary>
    static double Distance(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    /// <summary>True when the mark must be drawn in its quiet form for this accent.</summary>
    public static bool NeedsQuietForm(Color accent)
    {
        var (hue, sat) = HueAndSaturation(accent);
        if (sat < MinSaturation) return false;
        return StateHues.Any(s => Distance(hue, s) <= 30);
    }

    public static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Rigbay.Core;

namespace Rigbay.App;

/// <summary>
/// Draws a module's icon (docs/BRAND.md section 8). Core has already checked the files and says which one fits a place
/// (<see cref="ModuleIcons.Choose"/>); this turns the choice into an image. A module without an icon, or with one that cannot be
/// drawn, gets the host's neutral "?" tile. Nothing here throws: a broken file is logged once and replaced.
/// </summary>
static class ModuleIconUi
{
    /// <summary>The neutral icon every module falls back to: <c>Assets\modules\unknown</c> (a copy of the brand's host default).</summary>
    public static ModuleIcons Neutral { get; } = LoadNeutral();

    static ModuleIcons LoadNeutral()
    {
        var issues = new List<ManifestIssue>();
        var icons = ModuleIconResolver.Resolve(Path.Combine(AppContext.BaseDirectory, "Assets", "modules", "unknown"), "icon.svg", issues);
        foreach (var i in issues) AppLog.Warn("neutral module icon: " + i);
        return icons;
    }

    /// <summary>Test switch (<c>--shot-scale</c>): pretend the display scale is this, so icons are picked and rasterised for it.</summary>
    public static double? ScaleOverride { get; set; }

    static readonly HashSet<string> Reported = new();
    static bool _svgBroken;

    /// <summary>Images that opened, images that failed (before the fallback), and whether SVG had to be given up (self-test).</summary>
    public static int Opened { get; private set; }
    public static int Failures { get; private set; }
    public static bool SvgBroken => _svgBroken;
   // set when even the neutral SVG cannot be drawn: from then on every icon uses its PNG files

    /// <summary>True when the module is shown as not available on this machine (grey tile).</summary>
    public static bool IsUnsupported(ModuleEntry e) => e.Lifecycle is ModuleLifecycle.Unsupported or ModuleLifecycle.Incompatible;

    static void Report(ModuleEntry e, string path, string why)
    {
        if (Reported.Add(e.Id + "|" + path)) AppLog.Warn($"icon of {e.Id}: {path} could not be drawn ({why}); showing the neutral icon instead");
    }

    /// <summary>
    /// The image for <paramref name="e"/> at <paramref name="dip"/> wide at <paramref name="scale"/> (1.0 = 100 %), with the opacity to use.
    /// <paramref name="useNeutral"/> forces the host's tile (after the module's own file failed). <paramref name="failed"/> is called with the
    /// file's path if this very image cannot be opened or decoded. Never throws; null only if no file can be turned into an image.
    /// </summary>
    public static ImageSource? Source(ModuleEntry e, IconForm form, double dip, double scale, bool useNeutral, out double opacity, out string? path, Action<string>? failed = null)
    {
        opacity = 1.0; path = null;
        var icons = !useNeutral && e.Icons.HasIcon ? e.Icons : Neutral;
        bool unsupported = IsUnsupported(e);
        var pick = icons.Choose(form, unsupported, dip, scale, preferSvg: !_svgBroken);
        if (pick == null) return null;
        path = pick.Path;
        opacity = pick.Opacity;
        int pixels = (int)Math.Ceiling(dip * Math.Max(0.5, scale) - 1e-6);
        var file = pick.Path;
        try
        {
            var uri = new Uri(file);
            if (pick.IsSvg)
            {
                var svg = new SvgImageSource(uri) { RasterizePixelWidth = pixels, RasterizePixelHeight = pixels };
                svg.OpenFailed += (_, _) => failed?.Invoke(file);
                return svg;
            }
            var bmp = new BitmapImage(uri) { DecodePixelWidth = pixels, DecodePixelHeight = pixels };
            bmp.ImageFailed += (_, _) => failed?.Invoke(file);
            return bmp;
        }
        catch (Exception ex)
        {
            Report(e, file, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>An <see cref="Image"/> showing the module's icon that follows the display scale and falls back by itself. Decorative: screen readers skip it.</summary>
    public static Image Tile(ModuleEntry e, double dip, IconForm form = IconForm.Tile)
    {
        var image = new Image { Width = dip, Height = dip, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetAccessibilityView(image, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        bool neutral = false;
        string? current = null;

        void Apply()
        {
            double scale = ScaleOverride ?? image.XamlRoot?.RasterizationScale ?? 1.0;
            var src = Source(e, form, dip, scale, neutral, out var opacity, out current, OnFailed);
            image.Opacity = opacity;
            image.Source = src;
        }
        void OnFailed(string file)
        {
            if (file != current) return;   // an older source that was already replaced
            Failures++;
            if (!neutral && e.Icons.HasIcon)
            {
                Report(e, file, "image failed to open");
                neutral = true;
            }
            else if (!_svgBroken && file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                _svgBroken = true;   // not the module's fault: SVG does not render here at all
                AppLog.Warn("SVG icons cannot be drawn on this system; using the PNG files");
            }
            else return;   // the neutral PNG failed too: nothing left to try
            Ui.Post(null, Apply, "icon fallback");
        }
        void OnChanged(XamlRoot sender, XamlRootChangedEventArgs a) => Apply();   // display scale changed
        image.ImageOpened += (_, _) => Opened++;
        image.Loaded += (_, _) =>
        {
            Apply();
            if (image.XamlRoot != null) { image.XamlRoot.Changed -= OnChanged; image.XamlRoot.Changed += OnChanged; }
        };
        image.Unloaded += (_, _) => { if (image.XamlRoot != null) image.XamlRoot.Changed -= OnChanged; };
        return image;
    }

    /// <summary>The icon for a navigation pane item: the quiet glyph at 16 DIP (the neutral glyph if the module's file cannot be drawn).</summary>
    public static IconElement? NavIcon(ModuleEntry e, double scale)
    {
        ImageIcon? icon = null;
        void OnFailed(string file)
        {
            Failures++;
            Report(e, file, "image failed to open");
            if (icon != null) icon.Source = Source(e, IconForm.Glyph, 16, scale, useNeutral: true, out _, out _);
        }
        var src = Source(e, IconForm.Glyph, 16, scale, useNeutral: false, out var opacity, out _, OnFailed);
        if (src == null) return null;
        return icon = new ImageIcon { Source = src, Width = 16, Height = 16, Opacity = opacity };
    }
}

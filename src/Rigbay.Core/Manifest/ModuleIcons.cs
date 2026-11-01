using System.Xml;

namespace Rigbay.Core;

/// <summary>Which drawing of a module's mark a place wants: the coloured tile, or the quiet glyph (no tile) for the navigation pane and compact lists.</summary>
public enum IconForm { Tile, Glyph }

/// <summary>What <see cref="ModuleIcons.Choose"/> picked: a file, whether it is an SVG, and the opacity to draw it with.</summary>
public sealed record IconSource(string Path, bool IsSvg, double Opacity = 1.0);

/// <summary>
/// The icon files of one module, as full paths, all validated (see <see cref="ModuleIconResolver"/>); every member may be
/// null or empty. Set from the manifest's <c>icon</c> field (or the convention <c>icon.svg</c> / <c>icon-48.png</c>) plus the
/// sidecar files that sit next to it, so a module that ships only <c>icon.svg</c> is complete.
/// </summary>
/// <param name="Tile">The main icon (the tile): an <c>.svg</c> or a <c>.png</c>.</param>
/// <param name="Glyph">The quiet form (<c>icon-glyph.svg</c>), no tile.</param>
/// <param name="Unsupported">The tile in grey (<c>icon-unsupported.svg</c>), shown when the module's probe says the machine is not supported.</param>
/// <param name="Png">Raster tiles by pixel size (<c>icon-16.png</c> ... <c>icon-128.png</c>, plus the main icon when it is a PNG).</param>
/// <param name="UnsupportedPng">Raster grey tiles by pixel size (<c>icon-unsupported-24.png</c> ...).</param>
public sealed record ModuleIcons(
    string? Tile,
    string? Glyph,
    string? Unsupported,
    IReadOnlyDictionary<int, string> Png,
    IReadOnlyDictionary<int, string> UnsupportedPng)
{
    /// <summary>Opacity of the module's own tile when it has no grey variant and the module is unsupported (docs/BRAND.md section 8).</summary>
    public const double UnsupportedOpacity = 0.45;

    public static ModuleIcons None { get; } = new(null, null, null, new Dictionary<int, string>(), new Dictionary<int, string>());

    /// <summary>True when there is any file to draw.</summary>
    public bool HasIcon => Tile != null || Png.Count > 0;

    /// <summary>
    /// Picks the file for a place that is <paramref name="dip"/> device-independent pixels wide at <paramref name="scale"/> (1.0 = 100 %).
    /// An SVG is drawn at <c>dip * scale</c> pixels, so it is sharp at any scale; a raster is the smallest PNG with at least that
    /// many pixels (else the largest). An unsupported module gets its grey variant, or its own icon at 45 % opacity when it has none.
    /// Returns null when the module has no icon. Pass <paramref name="preferSvg"/> false to force rasters (the fallback when SVG rendering fails).
    /// </summary>
    public IconSource? Choose(IconForm form, bool unsupported, double dip, double scale, bool preferSvg = true)
    {
        if (!HasIcon) return null;
        int pixels = (int)Math.Ceiling(Math.Max(1, dip) * Math.Max(0.5, scale) - 1e-6);

        if (form == IconForm.Glyph && Glyph != null && preferSvg)
            return new IconSource(Glyph, true, unsupported ? UnsupportedOpacity : 1.0);

        if (unsupported && form == IconForm.Tile)
        {
            if (preferSvg && Unsupported != null) return new IconSource(Unsupported, true);
            if (!preferSvg && Smallest(UnsupportedPng, pixels) is { } grey) return new IconSource(grey, false);
        }

        double opacity = unsupported ? UnsupportedOpacity : 1.0;
        if (preferSvg && Tile != null && IsSvgFile(Tile)) return new IconSource(Tile, true, opacity);
        if (Smallest(Png, pixels) is { } png) return new IconSource(png, false, opacity);
        if (Tile != null) return new IconSource(Tile, IsSvgFile(Tile), opacity);
        return null;
    }

    private static string? Smallest(IReadOnlyDictionary<int, string> sizes, int pixels)
    {
        if (sizes.Count == 0) return null;
        var fit = sizes.Where(kv => kv.Key >= pixels).OrderBy(kv => kv.Key).Select(kv => kv.Value).FirstOrDefault();
        return fit ?? sizes.OrderByDescending(kv => kv.Key).First().Value;
    }

    internal static bool IsSvgFile(string path) => path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Finds and validates a module's icon files. The icon is untrusted input (it comes from a module folder that may have been
/// downloaded): a bad file never fails the load, it becomes a warning (<c>core.manifest.icon-*</c>) and the host shows its
/// neutral tile. Nothing is rendered or executed here; the UI draws SVG only through an image source (no script engine).
/// </summary>
public static class ModuleIconResolver
{
    public const int MaxSvgBytes = 64 * 1024;
    public const int MaxPngBytes = 256 * 1024;
    /// <summary>Smallest main PNG icon (pixels, square). Smaller sizes are fine as sidecars.</summary>
    public const int MinMainPngPixels = 48;
    public const int MaxPngPixels = 1024;

    private static readonly int[] RasterSizes = { 16, 24, 32, 48, 64, 128 };
    private static readonly int[] GreySizes = { 16, 24, 32, 48 };

    /// <summary>Elements that make an SVG more than a drawing. Compared in lower case, namespace ignored.</summary>
    private static readonly HashSet<string> BannedElements = new(StringComparer.Ordinal)
    {
        "script", "foreignobject", "image", "text", "tspan", "textpath", "style", "iframe", "object", "embed", "link", "feimage",
    };

    /// <summary>
    /// Syntax check of the manifest's <c>icon</c> value (no file access): a relative path of at most 100 characters that stays
    /// inside the module folder and ends in <c>.svg</c> or <c>.png</c>. Returns false with no reason; the caller reports one key.
    /// </summary>
    public static bool IsValidDeclaration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100) return false;
        if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || value.IndexOfAny(new[] { '*', '?', ':', '%', '$', '~' }) >= 0) return false;
        if (Path.IsPathRooted(value) || value.StartsWith('/') || value.StartsWith('\\')) return false;
        if (value.Split('/', '\\').Any(p => p.Length == 0 || p == ".." || p == "." || p.EndsWith(' ') || p.EndsWith('.'))) return false;
        var ext = Path.GetExtension(value);
        return ext.Equals(".svg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the icons of <paramref name="folder"/>. <paramref name="declared"/> is the manifest's <c>icon</c> (already
    /// syntax-checked) or null for the convention (<c>icon.svg</c>, then <c>icon-48.png</c>; a convention file that is bad is
    /// ignored without a warning, since the module never asked for it). Problems with a declared file are added to
    /// <paramref name="issues"/> as warnings; the result then has no tile.
    /// </summary>
    public static ModuleIcons Resolve(string folder, string? declared, List<ManifestIssue> issues)
    {
        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ModuleIcons.None; }

        string? main = null;
        if (declared != null)
        {
            main = Check(root, declared, issues, report: true, mainIcon: true);
            if (main == null) return ModuleIcons.None;
        }
        else
        {
            foreach (var conv in new[] { "icon.svg", "icon-48.png" })
                if ((main = Check(root, conv, issues, report: false, mainIcon: true)) != null) break;
            if (main == null) return ModuleIcons.None;
        }

        var dir = Path.GetDirectoryName(main)!;
        var stem = Path.GetFileNameWithoutExtension(main);
        bool mainIsPng = !ModuleIcons.IsSvgFile(main);
        if (mainIsPng) stem = System.Text.RegularExpressions.Regex.Replace(stem, @"-\d+$", "");   // icon-48.png -> icon

        string? Sidecar(string name) => Check(root, Path.GetRelativePath(root, Path.Combine(dir, name)), issues, report: declared != null, mainIcon: false, required: false);

        var glyph = Sidecar(stem + "-glyph.svg");
        var grey = Sidecar(stem + "-unsupported.svg");
        var png = new Dictionary<int, string>();
        foreach (var s in RasterSizes)
            if (Sidecar($"{stem}-{s}.png") is { } p) png[s] = p;
        if (mainIsPng && PngSize(main) is { } size && !png.ContainsValue(main)) png[size.Width] = main;
        var greyPng = new Dictionary<int, string>();
        foreach (var s in GreySizes)
            if (Sidecar($"{stem}-unsupported-{s}.png") is { } p) greyPng[s] = p;

        return new ModuleIcons(main, glyph, grey, png, greyPng);
    }

    /// <summary>Validates one icon file; returns its full path, or null (adding a warning when <paramref name="report"/>).</summary>
    private static string? Check(string root, string relative, List<ManifestIssue> issues, bool report, bool mainIcon, bool required = true)
    {
        void Warn(string key, params object[] args) { if (report) issues.Add(new ManifestIssue("icon", key, args, IsError: false)); }
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, relative));
            // GetFullPath normalised any '..'; the file must still be inside the folder.
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { Warn("core.manifest.icon-invalid"); return null; }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { Warn("core.manifest.icon-invalid"); return null; }

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists) { if (required) Warn("core.manifest.icon-missing", relative); return null; }
            // A link could point anywhere on the disk; the icon has to be a real file in the module folder.
            if (info.LinkTarget != null || HasLinkInPath(root, info)) { Warn("core.manifest.icon-invalid"); return null; }
            bool svg = ModuleIcons.IsSvgFile(full);
            long cap = svg ? MaxSvgBytes : MaxPngBytes;
            if (info.Length > cap) { Warn("core.manifest.icon-too-large", relative, cap / 1024); return null; }
            if (info.Length == 0) { Warn("core.manifest.icon-unsafe", relative); return null; }
            if (svg)
            {
                if (!IsSafeSvg(full)) { Warn("core.manifest.icon-unsafe", relative); return null; }
            }
            else
            {
                if (PngSize(full) is not { } size) { Warn("core.manifest.icon-unsafe", relative); return null; }
                if (size.Width != size.Height || size.Width > MaxPngPixels || (mainIcon && size.Width < MinMainPngPixels))
                { Warn("core.manifest.icon-size", relative, MinMainPngPixels, MaxPngPixels); return null; }
            }
            return full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn("core.manifest.icon-unreadable", relative);
            return null;
        }
    }

    private static bool HasLinkInPath(string root, FileInfo file)
    {
        for (var d = file.Directory; d != null && d.FullName.Length > root.Length; d = d.Parent)
            if (d.LinkTarget != null) return true;
        return false;
    }

    /// <summary>
    /// True when the file is a plain drawing: well-formed XML with an <c>svg</c> root, no DTD, none of <see cref="BannedElements"/>,
    /// no <c>on*</c> attribute, no <c>href</c> that is not a same-document <c>#id</c>, no <c>url(...)</c> that is not <c>url(#id)</c>.
    /// </summary>
    internal static bool IsSafeSvg(string path)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxSvgBytes * 2,
                IgnoreComments = true, IgnoreWhitespace = true,
            };
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, settings);
            bool first = true;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                var name = reader.LocalName.ToLowerInvariant();
                if (first)
                {
                    if (name != "svg") return false;
                    first = false;
                }
                if (BannedElements.Contains(name)) return false;
                if (!reader.HasAttributes) continue;
                for (int i = 0; i < reader.AttributeCount; i++)
                {
                    reader.MoveToAttribute(i);
                    var attr = reader.LocalName.ToLowerInvariant();
                    var value = reader.Value.Trim();
                    if (attr.StartsWith("on", StringComparison.Ordinal)) return false;
                    if (attr == "href" && !value.StartsWith('#')) return false;
                    if (HasExternalUrl(value)) return false;
                }
                reader.MoveToElement();
            }
            return !first;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    private static bool HasExternalUrl(string value)
    {
        for (int at = 0; (at = value.IndexOf("url(", at, StringComparison.OrdinalIgnoreCase)) >= 0; at += 4)
        {
            var rest = value[(at + 4)..].TrimStart(' ', '\'', '"');
            if (!rest.StartsWith('#')) return true;
        }
        return value.Contains("javascript:", StringComparison.OrdinalIgnoreCase) || value.Contains("data:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Width and height from the IHDR chunk of a PNG, or null when the file is not a PNG.</summary>
    internal static (int Width, int Height)? PngSize(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[24];
            using var stream = File.OpenRead(path);
            if (stream.Read(head) < 24) return null;
            ReadOnlySpan<byte> sig = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (!head[..8].SequenceEqual(sig) || head[12] != (byte)'I' || head[13] != (byte)'H' || head[14] != (byte)'D' || head[15] != (byte)'R') return null;
            int w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            int h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
            return w > 0 && h > 0 ? (w, h) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

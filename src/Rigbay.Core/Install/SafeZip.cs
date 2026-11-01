using System.IO.Compression;
using System.Text;

namespace Rigbay.Core.Install;

/// <summary>
/// Unpacks a release zip into an empty staging folder and refuses anything that could write outside it or confuse Windows:
/// absolute and drive paths, <c>..</c>, alternate data streams, device names, names Windows rewrites (trailing dot or space),
/// two entries that differ only by case, a file and a folder with the same name, symbolic links, too many entries and too many bytes
/// (counted while writing, not taken from the zip's own header). The zip is expected to have <c>module.json</c> at its top level.
/// </summary>
public static class SafeZip
{
    private static readonly string[] DeviceNames =
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private const int MaxSegment = 120;
    private const int MaxRelativePath = 200;

    /// <summary>Checks one entry name and returns it normalised (forward slashes, no trailing slash) or throws <see cref="InstallException"/> (UnsafeZip).</summary>
    public static string NormaliseName(string raw)
    {
        if (raw.Length == 0) throw Unsafe(raw, "empty name");
        var name = raw.Replace('\\', '/');
        if (name.StartsWith('/')) throw Unsafe(raw, "absolute path");
        if (name.Length >= 2 && name[1] == ':') throw Unsafe(raw, "drive path");
        bool isDirectory = name.EndsWith('/');
        if (isDirectory) name = name.TrimEnd('/');
        if (name.Length == 0) throw Unsafe(raw, "empty name");
        if (name.Length > MaxRelativePath) throw Unsafe(raw, "path too long");
        foreach (var seg in name.Split('/'))
        {
            if (seg.Length == 0) throw Unsafe(raw, "empty path segment");
            if (seg is "." or "..") throw Unsafe(raw, "'.' or '..' in the path");
            if (seg.Length > MaxSegment) throw Unsafe(raw, "name too long");
            if (seg.IndexOfAny(new[] { ':', '<', '>', '"', '|', '?', '*' }) >= 0 || seg.Any(c => c < ' ')) throw Unsafe(raw, "characters Windows does not allow (or a stream name)");
            if (seg.EndsWith('.') || seg.EndsWith(' ')) throw Unsafe(raw, "ends with a dot or a space");
            var stem = seg.Contains('.') ? seg[..seg.IndexOf('.')] : seg;
            stem = stem.TrimEnd(' ');
            if (DeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) throw Unsafe(raw, "a reserved device name");
            if (IsShortNameAlias(seg)) throw Unsafe(raw, "looks like an 8.3 short name");
        }
        return name;
    }

    // "PROGRA~1" and "MODULE~1.JSO": a short alias of another file
    private static bool IsShortNameAlias(string seg)
    {
        int t = seg.IndexOf('~');
        if (t < 0) return false;
        int i = t + 1;
        if (i >= seg.Length || !char.IsAsciiDigit(seg[i])) return false;
        while (i < seg.Length && char.IsAsciiDigit(seg[i])) i++;
        return i == seg.Length || seg[i] == '.';
    }

    /// <summary>Unpacks <paramref name="zipPath"/> into <paramref name="targetFolder"/> (created; must be empty or missing).</summary>
    public static void Extract(string zipPath, string targetFolder, InstallPolicy policy, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetFolder);
        if (Directory.EnumerateFileSystemEntries(targetFolder).Any()) throw new InvalidOperationException("the staging folder must be empty");
        if (FileOps.IsReparsePoint(targetFolder)) throw Unsafe(targetFolder, "the staging folder is a link");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetFolder)) + Path.DirectorySeparatorChar;

        ZipArchive archive;
        try { archive = ZipFile.OpenRead(zipPath); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            throw new InstallException(InstallErrorCode.BadZip, "not a readable zip file: " + ex.Message, ex);
        }

        using (archive)
        {
            if (archive.Entries.Count > policy.MaxEntries) throw new InstallException(InstallErrorCode.TooLarge, $"the zip has {archive.Entries.Count} entries (limit {policy.MaxEntries})");
            long declared = 0;
            foreach (var e in archive.Entries)
            {
                declared += e.Length;
                if (declared > policy.MaxUnpackedBytes) throw new InstallException(InstallErrorCode.TooLarge, $"the zip unpacks to more than {policy.MaxUnpackedBytes / (1024 * 1024)} MB");
            }

            // pass 1: names only, nothing is written yet
            var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);   // normalised path -> is a folder
            var plan = new List<(ZipArchiveEntry Entry, string Name, bool IsDirectory)>();
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                bool isDir = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                var name = NormaliseName(entry.FullName);
                if (IsSymlink(entry)) throw Unsafe(entry.FullName, "a symbolic link");
                if (isDir && entry.Length != 0) throw Unsafe(entry.FullName, "a folder entry with content");

                // every parent is a folder
                var parts = name.Split('/');
                for (int i = 1; i < parts.Length; i++)
                {
                    var parent = string.Join('/', parts.Take(i));
                    if (seen.TryGetValue(parent, out var parentIsDir) && !parentIsDir) throw Unsafe(entry.FullName, "'" + parent + "' is both a file and a folder");
                    seen[parent] = true;
                }
                if (seen.TryGetValue(name, out var existingIsDir))
                {
                    // a folder named twice is harmless; anything else with a name already taken (case ignored) is not
                    if (!(isDir && existingIsDir)) throw Unsafe(entry.FullName, "two entries with the same name (case ignored), or a file and a folder with one name");
                }
                else seen[name] = isDir;
                plan.Add((entry, name, isDir));
            }

            // pass 2: write, counting the bytes actually produced
            long written = 0;
            var buffer = new byte[81920];
            foreach (var (entry, name, isDir) in plan)
            {
                ct.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(Path.Combine(targetFolder, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw Unsafe(entry.FullName, "resolves outside the staging folder");
                if (isDir) { EnsureFolder(root, full); continue; }

                EnsureFolder(root, Path.GetDirectoryName(full)!);
                using var input = entry.Open();
                using var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += n;
                    if (written > policy.MaxUnpackedBytes) throw new InstallException(InstallErrorCode.TooLarge, $"the zip unpacks to more than {policy.MaxUnpackedBytes / (1024 * 1024)} MB");
                    output.Write(buffer, 0, n);
                    ct.ThrowIfCancellationRequested();
                }
            }
        }

        // nothing in the result may be a link (the zip cannot create one, the folder was empty: this is the belt)
        AssertNoLinks(targetFolder);
    }

    /// <summary>Creates a folder below the staging root one level at a time and refuses to walk through a link.</summary>
    private static void EnsureFolder(string root, string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(folder);
        var relative = Path.GetRelativePath(root, folder);
        if (relative == ".") return;
        var current = Path.TrimEndingDirectorySeparator(root);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current))
            {
                if (FileOps.IsReparsePoint(current)) throw Unsafe(current, "a link inside the staging folder");
                continue;
            }
            Directory.CreateDirectory(current);
        }
    }

    private static void AssertNoLinks(string folder)
    {
        foreach (var e in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
            if (FileOps.IsReparsePoint(e)) throw Unsafe(e, "a link inside the staging folder");
    }

    // ZIP "version made by" high byte 3 = Unix; the high 16 bits of the external attributes are st_mode; 0xA000 is a symbolic link
    private static bool IsSymlink(ZipArchiveEntry entry)
    {
        int mode = (entry.ExternalAttributes >> 16) & 0xF000;
        if (mode == 0xA000) return true;
        // a Windows reparse-point attribute stored by some tools
        return (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static InstallException Unsafe(string name, string why) =>
        new(InstallErrorCode.UnsafeZip, $"unsafe entry '{Printable(name)}': {why}");

    private static string Printable(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Length > 80 ? s[..80] + "…" : s) sb.Append(c < ' ' ? '?' : c);
        return sb.ToString();
    }
}

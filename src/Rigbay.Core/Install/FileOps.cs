using System.Security.Cryptography;
using System.Text;

namespace Rigbay.Core.Install;

/// <summary>File operations the installer relies on: deletes that never follow a link, moves that retry, writes that are atomic, a hash of a whole folder.</summary>
public static class FileOps
{
    public static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Deletes a file or a folder tree. A junction or symbolic link is removed as a link: what it points to is never entered or deleted.
    /// Read-only flags are cleared. A path that does not exist is fine.
    /// </summary>
    public static void SafeDelete(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // remove the link itself (a directory link is removed with Directory.Delete without recursion)
            if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(path, recursive: false);
            else { ClearReadOnly(path, attributes); File.Delete(path); }
            return;
        }
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            ClearReadOnly(path, attributes);
            File.Delete(path);
            return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) SafeDelete(child);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        Directory.Delete(path, recursive: false);
    }

    /// <summary>Deletes the contents of a folder (the folder stays). Errors are returned, not thrown.</summary>
    public static string? TryClearFolder(string path)
    {
        if (!Directory.Exists(path)) return null;
        string? first = null;
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            try { SafeDelete(child); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { first ??= ex.Message; }
        }
        return first;
    }

    private static void ClearReadOnly(string path, FileAttributes a)
    {
        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
    }

    /// <summary>Renames a folder on the same volume, retrying a few times (a scanner may hold a file open for a moment).</summary>
    public static void MoveDirectory(string from, string to, int attempts = 6, int delayMs = 250)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
                Directory.Move(from, to);
                return;
            }
            catch (Exception ex) when (i < attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    /// <summary>Writes a file so a reader sees the old or the new content, never half of it.</summary>
    public static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>
    /// A SHA-256 over the relative paths (lower-cased, '/'), sizes and content hashes of every file below <paramref name="folder"/>,
    /// in a fixed order. Used to notice that a staged module changed between verification and the swap. A link anywhere in the tree throws.
    /// </summary>
    public static string TreeHash(string folder)
    {
        var entries = new List<(string Rel, string FullPath)>();
        void Walk(string dir)
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(dir))
            {
                var attrs = File.GetAttributes(e);
                if (attrs.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("a link inside the module folder: " + e);
                if (attrs.HasFlag(FileAttributes.Directory)) Walk(e);
                else entries.Add((Path.GetRelativePath(folder, e).Replace('\\', '/').ToLowerInvariant(), e));
            }
        }
        Walk(folder);
        entries.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));
        using var total = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (rel, full) in entries)
        {
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fileHash = SHA256.HashData(fs);
            total.AppendData(Encoding.UTF8.GetBytes(rel + "\n" + fs.Length + "\n"));
            total.AppendData(fileHash);
        }
        return Convert.ToHexString(total.GetHashAndReset()).ToLowerInvariant();
    }
}

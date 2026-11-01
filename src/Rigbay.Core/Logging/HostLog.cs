using System.Globalization;
using System.Text;
using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>One log line kept in memory for the Log page.</summary>
public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Source, string Message, string? Exception);

/// <summary>
/// The host log: <c>rigbay.log</c> in the log folder, rotated at a size cap (<c>rigbay.1.log</c>, <c>rigbay.2.log</c>),
/// plus the last entries in memory. Writing never throws.
/// </summary>
public sealed class HostLog
{
    public const string FileName = "rigbay";

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly int _files;
    private readonly int _memoryEntries;
    private readonly LinkedList<LogEntry> _recent = new();
    private long _size;

    public HostLog(string directory, long maxBytes = 1024 * 1024, int files = 3, LogLevel minimum = LogLevel.Info, int memoryEntries = 500)
    {
        _directory = directory;
        _maxBytes = Math.Max(256, maxBytes);
        _files = Math.Max(1, files);
        _memoryEntries = memoryEntries;
        Minimum = minimum;
        try
        {
            Directory.CreateDirectory(directory);
            var current = CurrentFile;
            if (File.Exists(current)) _size = new FileInfo(current).Length;
        }
        catch (Exception) { /* logging must never stop the host */ }
    }

    public LogLevel Minimum { get; set; }

    public string CurrentFile => Path.Combine(_directory, FileName + ".log");

    /// <summary>Raised after an entry was written (on the writer's thread).</summary>
    public event Action<LogEntry>? EntryWritten;

    public void Write(LogLevel level, string source, string message, Exception? exception = null)
    {
        if (level < Minimum) return;
        var entry = new LogEntry(DateTimeOffset.Now, level, source, message, exception?.ToString());
        var line = Format(entry);
        lock (_gate)
        {
            _recent.AddLast(entry);
            while (_recent.Count > _memoryEntries) _recent.RemoveFirst();
            try
            {
                var bytes = Encoding.UTF8.GetByteCount(line);
                if (_size > 0 && _size + bytes > _maxBytes) Rotate();
                using var stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var data = Encoding.UTF8.GetBytes(line);
                stream.Write(data, 0, data.Length);
                _size += data.Length;
            }
            catch (Exception) { /* disk full, file locked: the in-memory copy still exists */ }
        }
        try { EntryWritten?.Invoke(entry); } catch (Exception) { }
    }

    /// <summary>An <see cref="ILog"/> that stamps <paramref name="source"/> (a module id or a component name) on each entry.</summary>
    public ILog ForSource(string source) => new SourceLog(this, source);

    public IReadOnlyList<LogEntry> Recent()
    {
        lock (_gate) return _recent.ToArray();
    }

    internal static string Format(LogEntry e)
    {
        var sb = new StringBuilder();
        sb.Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ');
        sb.Append(e.Level.ToString().ToUpperInvariant().PadRight(5)).Append(' ');
        sb.Append(e.Source.PadRight(14)).Append(' ');
        sb.Append(e.Message.ReplaceLineEndings(" "));
        sb.AppendLine();
        if (e.Exception is not null) sb.AppendLine(e.Exception);
        return sb.ToString();
    }

    private void Rotate()
    {
        try
        {
            // rigbay.(n-1).log is the oldest kept; shift everything one place up.
            for (int i = _files - 1; i >= 1; i--)
            {
                var from = i == 1 ? CurrentFile : Path.Combine(_directory, $"{FileName}.{i - 1}.log");
                var to = Path.Combine(_directory, $"{FileName}.{i}.log");
                if (File.Exists(to)) File.Delete(to);
                if (File.Exists(from)) File.Move(from, to);
            }
            if (_files == 1 && File.Exists(CurrentFile)) File.Delete(CurrentFile);
        }
        catch (Exception) { /* keep appending to the current file */ }
        _size = File.Exists(CurrentFile) ? new FileInfo(CurrentFile).Length : 0;
    }

    private sealed class SourceLog(HostLog log, string source) : ILog
    {
        public void Write(LogLevel level, string message, Exception? exception = null) => log.Write(level, source, message, exception);
    }
}

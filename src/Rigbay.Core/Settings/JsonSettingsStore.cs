using System.Text.Json;
using System.Text.Json.Serialization;
using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>
/// One JSON file holding <c>{ "version": n, "data": { ... } }</c>. Writes are atomic (temporary file, then replace);
/// a corrupt file is renamed to <c>.corrupt-&lt;time&gt;</c> and defaults are returned; file problems never throw out of
/// <c>Load</c>.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private readonly ILog _log;

    public JsonSettingsStore(string filePath, ILog log)
    {
        FilePath = filePath;
        _log = log;
    }

    public string FilePath { get; }

    public T Load<T>() where T : class, new() => Load<T>(out _);

    public T Load<T>(out int storedVersion) where T : class, new()
    {
        storedVersion = 0;
        lock (_gate)
        {
            string text;
            try
            {
                if (!File.Exists(FilePath)) return new T();
                text = File.ReadAllText(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable right now (locked, no access): not proof of corruption, so leave the file alone.
                _log.Warn($"settings {FilePath}: cannot read, using defaults: {ex.Message}");
                return new T();
            }

            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    throw new JsonException("expected an object with a 'data' object");
                int version = 1;
                if (root.TryGetProperty("version", out var v))
                {
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out version)) throw new JsonException("'version' must be an integer");
                }
                var value = data.Deserialize<T>(Options) ?? throw new JsonException("data is null");
                storedVersion = version;
                return value;
            }
            catch (JsonException ex)
            {
                MoveAside(ex.Message);
                return new T();
            }
        }
    }

    public void Save<T>(T value, int version = 1) where T : class
    {
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new Envelope<T>(version, value), Options);
            var temp = Path.Combine(dir, Path.GetFileName(FilePath) + ".tmp-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, FilePath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                throw;
            }
        }
    }

    private void MoveAside(string reason)
    {
        var backup = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        try
        {
            File.Move(FilePath, backup);
            _log.Warn($"settings {FilePath} is corrupt ({reason}); moved to {Path.GetFileName(backup)}, using defaults");
        }
        catch (Exception ex)
        {
            _log.Warn($"settings {FilePath} is corrupt ({reason}) and could not be moved aside ({ex.Message}); using defaults");
        }
    }

    private sealed record Envelope<T>(int Version, T Data);
}

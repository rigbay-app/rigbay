namespace Rigbay.Host;

/// <summary>Language files: "@code"/"@name" header, then "key = text". English is the fallback.</summary>
sealed class Loc
{
    readonly Dictionary<string, string> _en = new(), _cur = new();
    public List<(string Code, string Name)> Languages { get; } = new();
    public string Code { get; private set; } = "en";

    public Loc()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "lang");
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.lang").OrderBy(x => x))
        {
            var d = Read(f);
            Languages.Add((d.GetValueOrDefault("@code", Path.GetFileNameWithoutExtension(f)), d.GetValueOrDefault("@name", "?")));
            if (Path.GetFileNameWithoutExtension(f) == "en") foreach (var kv in d) _en[kv.Key] = kv.Value;
        }
        Set("en");
    }

    static Dictionary<string, string> Read(string file)
    {
        var d = new Dictionary<string, string>();
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq > 0) d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    public void Set(string code)
    {
        var f = Path.Combine(AppContext.BaseDirectory, "lang", code + ".lang");
        _cur.Clear();
        if (File.Exists(f)) foreach (var kv in Read(f)) _cur[kv.Key] = kv.Value;
        Code = code;
    }

    public string T(string key) => _cur.TryGetValue(key, out var v) ? v : _en.TryGetValue(key, out var e) ? e : key;
}

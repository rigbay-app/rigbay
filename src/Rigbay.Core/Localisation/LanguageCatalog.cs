using System.Globalization;
using System.Reflection;
using System.Text;
using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>A language: its code, display name and texts, merged from every file with that code.</summary>
public sealed class Language
{
    public string Code { get; init; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);
    public List<string> Sources { get; } = new();
}

/// <summary>
/// All languages known to the host. Lookup: current language, then English, then the key itself. Module files may
/// only define keys under <c>module.&lt;id&gt;.</c>; anything else is ignored and logged.
/// </summary>
public sealed class LanguageCatalog
{
    public const string Fallback = "en";

    private readonly object _gate = new();
    private readonly Dictionary<string, Language> _languages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILog _log;
    private Language _current;
    private Language _english;

    public LanguageCatalog(ILog log)
    {
        _log = log;
        _english = new Language { Code = Fallback, Name = "English" };
        _languages[Fallback] = _english;
        _current = _english;
    }

    public string CurrentCode { get { lock (_gate) return _current.Code; } }

    public event Action? LanguageChanged;

    /// <summary>English first, then the others by name.</summary>
    public IReadOnlyList<Language> Languages
    {
        get
        {
            lock (_gate)
            {
                var list = _languages.Values.ToList();
                list.Sort((a, b) => ReferenceEquals(a, _english) ? -1 : ReferenceEquals(b, _english) ? 1 : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                return list;
            }
        }
    }

    public bool Exists(string? code)
    {
        lock (_gate) return code is not null && _languages.ContainsKey(code);
    }

    /// <summary>Switches language; an unknown code changes nothing and returns false.</summary>
    public bool SetLanguage(string? code)
    {
        lock (_gate)
        {
            if (code is null || !_languages.TryGetValue(code.Trim(), out var l)) return false;
            if (ReferenceEquals(l, _current)) return true;
            _current = l;
        }
        LanguageChanged?.Invoke();
        return true;
    }

    public string T(string key)
    {
        if (Text.IsLiteral(key)) return Text.Unwrap(key);
        lock (_gate)
        {
            if (_current.Text.TryGetValue(key, out var v)) return v;
            if (_english.Text.TryGetValue(key, out v)) return v;
            return key;
        }
    }

    /// <summary>True when the key has a text in the current language or in English.</summary>
    public bool TryGet(string key, out string text)
    {
        if (Text.IsLiteral(key)) { text = Text.Unwrap(key); return true; }
        lock (_gate)
        {
            if (_current.Text.TryGetValue(key, out text!)) return true;
            return _english.Text.TryGetValue(key, out text!);
        }
    }

    public string F(string key, params object[] args)
    {
        if (Text.IsLiteral(key))
        {
            var template = Text.Unwrap(key);
            if (args.Length == 0) return template;
            try { return string.Format(CultureInfo.InvariantCulture, template, args); }
            catch (FormatException) { return template; }
        }
        string text, englishText;
        lock (_gate)
        {
            text = _current.Text.TryGetValue(key, out var v) ? v : (_english.Text.TryGetValue(key, out v) ? v : key);
            englishText = _english.Text.TryGetValue(key, out v) ? v : key;
        }
        try { return string.Format(CultureInfo.InvariantCulture, text, args); }
        catch (FormatException)
        {
            // A translation with a broken placeholder must not crash a page: use the English text.
            try { return string.Format(CultureInfo.InvariantCulture, englishText, args); }
            catch (FormatException) { return text; }
        }
    }

    /// <summary>Adds the texts of one parsed file to the language with its code. Returns how many keys were accepted.</summary>
    /// <param name="file">The parsed file.</param>
    /// <param name="requiredPrefix">When set, only keys starting with it are accepted (module files use <c>module.&lt;id&gt;.</c>).</param>
    public int Add(LanguageFile file, string? requiredPrefix = null)
    {
        foreach (var p in file.Problems) _log.Warn("language: " + p);
        if (file.Code.Length == 0) return 0;

        int accepted = 0;
        lock (_gate)
        {
            if (!_languages.TryGetValue(file.Code, out var lang))
            {
                lang = new Language { Code = file.Code, Name = file.Name };
                _languages[file.Code] = lang;
                if (file.Code.Equals(Fallback, StringComparison.OrdinalIgnoreCase)) _english = lang;
            }
            if (file.Name.Length > 0 && file.Name != file.Code && lang.Name.Length == 0) lang.Name = file.Name;
            else if (file.Name.Length > 0 && file.Name != file.Code && requiredPrefix is null) lang.Name = file.Name;
            foreach (var (key, value) in file.Text)
            {
                if (requiredPrefix is not null && !key.StartsWith(requiredPrefix, StringComparison.Ordinal))
                {
                    _log.Warn($"language {file.Source}: key '{key}' is outside '{requiredPrefix}' and was ignored");
                    continue;
                }
                lang.Text[key] = value;
                accepted++;
            }
            lang.Sources.Add(file.Source);
            if (ReferenceEquals(lang, _english)) _english = lang;
        }
        return accepted;
    }

    /// <summary>Loads every <c>*.lang</c> file of a folder. A missing folder is not an error.</summary>
    public int LoadDirectory(string directory, string? requiredPrefix = null)
    {
        if (!Directory.Exists(directory)) return 0;
        int total = 0;
        foreach (var path in Directory.GetFiles(directory, "*.lang").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try { total += Add(LanguageFile.ParseFile(path), requiredPrefix); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Warn($"language file {path}: {ex.Message}"); }
        }
        return total;
    }

    /// <summary>Loads <c>*.lang</c> files embedded in an assembly under a manifest resource name prefix.</summary>
    public void LoadEmbedded(Assembly assembly, string resourcePrefix)
    {
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(resourcePrefix, StringComparison.Ordinal) || !name.EndsWith(".lang", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Add(LanguageFile.Parse(reader, name[resourcePrefix.Length..]));
        }
    }

    public static string ModulePrefix(string moduleId) => "module." + moduleId + ".";
}

/// <summary>The <see cref="ILocalisation"/> of one module: short keys in code, <c>module.&lt;id&gt;.</c> in files.</summary>
public sealed class ModuleLocalisation : ILocalisation
{
    private readonly LanguageCatalog _catalog;
    private readonly string _prefix;

    public ModuleLocalisation(LanguageCatalog catalog, string moduleId)
    {
        _catalog = catalog;
        _prefix = LanguageCatalog.ModulePrefix(moduleId);
    }

    public string Language => _catalog.CurrentCode;

    public string T(string key) => Text.IsLiteral(key) ? Text.Unwrap(key) : _catalog.T(_prefix + key);

    public string F(string key, params object[] args) => _catalog.F(Text.IsLiteral(key) ? key : _prefix + key, args);

    public string Resolve(LocalisedText text) => text.IsEmpty ? "" : text.Args is { Length: > 0 } ? F(text.Key, text.Args) : T(text.Key);

    public bool TryGet(string key, out string text) => _catalog.TryGet(Text.IsLiteral(key) ? key : _prefix + key, out text);

    public event Action? LanguageChanged
    {
        add => _catalog.LanguageChanged += value;
        remove => _catalog.LanguageChanged -= value;
    }
}

using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.PowerRules.Tests;

// ------------------------------------------------------------------ host fakes

internal sealed class FakeBus : IMetricsBus
{
    private readonly object _gate = new();
    public readonly Dictionary<string, (double Value, MetricUnit Unit)> Latest = new();
    public readonly List<MetricDescriptor> Descriptors = new();
    public readonly List<string> Withdrawn = new();
    public readonly List<string> Published = new();
    public readonly HashSet<string> Subscribed = new();   // ids somebody reads

    public void Publish(MetricId id, double value, MetricUnit unit)
    {
        lock (_gate) { Latest[id.Value] = (value, unit); Published.Add(id.Value); }
    }

    public void Withdraw(MetricId id)
    {
        lock (_gate) { if (Latest.Remove(id.Value)) Withdrawn.Add(id.Value); }
    }

    public void Describe(MetricDescriptor descriptor) { lock (_gate) Descriptors.Add(descriptor); }
    public int SubscriberCount(MetricId id) { lock (_gate) return Subscribed.Contains(id.Value) ? 1 : 0; }
    public event Action? SubscribersChanged;
    public void RaiseSubscribersChanged() => SubscribersChanged?.Invoke();
    public IDisposable Subscribe(Action<MetricSample> handler) => new Nop();
    public IDisposable Subscribe(MetricId id, Action<MetricSample> handler) => new Nop();
    public bool TryGetLatest(MetricId id, out MetricSample sample) { sample = null!; return false; }
    public IReadOnlyList<MetricSample> GetLatest() => Array.Empty<MetricSample>();
    public bool Has(string id) { lock (_gate) return Latest.ContainsKey(id); }

    private sealed class Nop : IDisposable { public void Dispose() { } }
}

/// <summary>Reads the module's real en.lang or vi.lang, so a key mistake shows up as the key text.</summary>
internal sealed class FakeLocalisation : ILocalisation
{
    private const string Prefix = "module.power-rules.";
    private readonly Dictionary<string, string> _english;
    private Dictionary<string, string> _current;

    public FakeLocalisation(string language = "en")
    {
        _english = Load("en");
        _current = language == "en" ? _english : Load(language);
        Language = language;
    }

    public string Language { get; private set; }
    public event Action? LanguageChanged;

    public void Switch(string language)
    {
        _current = language == "en" ? _english : Load(language);
        Language = language;
        LanguageChanged?.Invoke();
    }

    public static Dictionary<string, string> Load(string language)
    {
        var d = new Dictionary<string, string>();
        foreach (var raw in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "lang", language + ".lang")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == '@') continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            d[line[..eq].Trim()] = Unescape(line[(eq + 1)..].Trim());
        }
        return d;
    }

    /// <summary>The host's rule: <c>\n</c> is a line break, <c>\t</c> a tab, <c>\X</c> is X.</summary>
    public static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { char c = s[++i]; sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c); }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public bool Has(string key) => _english.ContainsKey(Prefix + key);

    public string T(string key)
    {
        if (Text.IsLiteral(key)) return Text.Unwrap(key);
        return _current.TryGetValue(Prefix + key, out var v) ? v : _english.TryGetValue(Prefix + key, out var e) ? e : Prefix + key;
    }

    public string F(string key, params object[] args) => Text.IsLiteral(key) && args.Length == 0 ? Text.Unwrap(key) : string.Format(CultureInfo.InvariantCulture, T(key), args);
    public string Resolve(LocalisedText text) => text.IsEmpty ? "" : text.Args is { Length: > 0 } ? F(text.Key, text.Args) : T(text.Key);
}

internal sealed class FakeLog : ILog
{
    public readonly List<(LogLevel Level, string Message)> Entries = new();
    public void Write(LogLevel level, string message, Exception? exception = null) { lock (Entries) Entries.Add((level, message + (exception is null ? "" : " | " + exception.GetType().Name + ": " + exception.Message))); }
    public string All() { lock (Entries) return string.Join("\n", Entries.Select(e => e.Message)); }
}

internal sealed class FakeSettings : ISettingsStore
{
    // Like the real store: what is saved is a copy, and Load hands out a fresh object every time.
    public string? Json;
    public int Saves;
    public T Load<T>() where T : class, new() => Json is null ? new T() : System.Text.Json.JsonSerializer.Deserialize<T>(Json) ?? new T();
    public T Load<T>(out int storedVersion) where T : class, new() { storedVersion = Json is null ? 0 : 1; return Load<T>(); }
    public void Save<T>(T value, int version = 1) where T : class { Json = System.Text.Json.JsonSerializer.Serialize(value); Saves++; }
    public PowerRulesSettings Current => Load<PowerRulesSettings>();
}

/// <summary>Answers every question with <see cref="Answer"/> and remembers what was asked, word for word.</summary>
internal sealed class FakeApproval : IHardwareApproval
{
    public bool Answer;
    /// <summary>Runs while the question is open (a test edits something then).</summary>
    public Action? OnAsk;
    public readonly List<(HardwareEffect Effect, LocalisedText? Detail)> Asked = new();

    public Task<bool> ConfirmAsync(HardwareEffect effect, LocalisedText? detail = null, CancellationToken cancellationToken = default)
    {
        lock (Asked) Asked.Add((effect, detail));
        OnAsk?.Invoke();
        return Task.FromResult(Answer);
    }

    public int Count { get { lock (Asked) return Asked.Count; } }
}

internal sealed class FakeBinding : IHotkeyBinding
{
    public FakeBinding(string id, HotkeyGesture? gesture) { ActionId = id; Gesture = new ObservableValue<HotkeyGesture?>(gesture); }
    public string ActionId { get; }
    public IObservableValue<HotkeyGesture?> Gesture { get; }
    public IObservableValue<bool> HasConflict { get; } = new ObservableValue<bool>(false);
}

internal sealed class FakeHotkeys : IHotkeyService
{
    public readonly List<HotkeyRegistration> Registered = new();

    public IHotkeyBinding Register(HotkeyRegistration registration)
    {
        if (Registered.Any(r => r.ActionId == registration.ActionId)) throw new ArgumentException("duplicate action id");
        Registered.Add(registration);
        return new FakeBinding(registration.ActionId, registration.Default);
    }

    public void Press(string actionId) => Registered.Single(r => r.ActionId == actionId).Invoke();
}

internal sealed class FakeCommands : ICommandRegistry
{
    public readonly Dictionary<string, (string DescriptionKey, CommandHandler Handler)> Verbs = new();

    public void Register(string verb, string descriptionKey, CommandHandler handler)
    {
        if (Verbs.ContainsKey(verb)) throw new ArgumentException("duplicate verb");
        Verbs[verb] = (descriptionKey, handler);
    }

    public Task<CommandResult> Run(string verb, params string[] args) => Verbs[verb].Handler(new CommandContext(args, CancellationToken.None));
}

internal sealed class FakeToast : IToastService
{
    public readonly List<ToastMessage> Shown = new();
    public void Show(ToastMessage message) { lock (Shown) Shown.Add(message); }
    public ToastMessage[] All() { lock (Shown) return Shown.ToArray(); }
}

internal sealed class FakeTray : ITrayService
{
    public readonly List<TrayItem> Items = new();
    public void Add(TrayItem item) { if (Items.Any(i => i.Id == item.Id)) throw new ArgumentException("duplicate tray id"); Items.Add(item); }
    public void Remove(string id) => Items.RemoveAll(i => i.Id == id);
    public TrayItem Item(string id) => Items.Single(i => i.Id == id);
}

internal sealed class FakeHost : IHostServices
{
    private readonly CancellationTokenSource _stop = new();

    public FakeHost(string language = "en") => Localisation = new FakeLocalisation(language);

    public readonly FakeBus MetricsBus = new();
    public readonly FakeLog LogSink = new();
    public readonly FakeSettings SettingsStore = new();
    public readonly FakeApproval ApprovalService = new();
    public readonly FakeHotkeys HotkeyService = new();
    public readonly FakeCommands CommandRegistry = new();
    public readonly FakeToast ToastService = new();
    public readonly FakeTray TrayService = new();
    public readonly ObservableValue<bool> PageVisibleValue = new(false);
    public int PageRefreshRequests;
    public FakeLocalisation Loc => (FakeLocalisation)Localisation;

    public string ModuleId => "power-rules";
    public string ModuleDirectory => AppContext.BaseDirectory;
    public string DataDirectory => Path.GetTempPath();
    public Version HostVersion { get; } = new(0, 1);
    public CancellationToken StopToken => _stop.Token;
    public ISettingsStore Settings => SettingsStore;
    public ILocalisation Localisation { get; }
    public ILog Log => LogSink;
    public IHotkeyService Hotkeys => HotkeyService;
    public ITrayService Tray => TrayService;
    public ICommandRegistry Commands => CommandRegistry;
    public IToastService Toast => ToastService;
    public IMetricsBus Metrics => MetricsBus;
    public IHardwareApproval Approval => ApprovalService;
    public IObservableValue<bool> PageVisible => PageVisibleValue;
    public void RunOnUi(Action action) => action();
    public void RequestPageRefresh() => Interlocked.Increment(ref PageRefreshRequests);
}

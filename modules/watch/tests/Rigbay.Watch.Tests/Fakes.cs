using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.Watch.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => Now += by;
    public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    public Func<DateTimeOffset> Func => () => Now;
}

/// <summary>
/// A bus that remembers who asked for what. <see cref="Subscribe(Action{MetricSample})"/> and the id overload are counted and must stay at zero: Watch asks
/// only through <c>SubscribeBackground</c>.
/// </summary>
internal sealed class FakeBus : IMetricsBus
{
    private readonly object _gate = new();
    private readonly FakeClock _clock;
    private readonly Dictionary<string, MetricSample> _latest = new();
    private readonly List<Background> _background = new();

    public readonly List<MetricInfo> Catalogue = new();
    public int PlainSubscribes;
    public int Publishes;

    public FakeBus(FakeClock clock) => _clock = clock;

    private sealed class Background(string id, TimeSpan interval, Action<MetricSample> handler)
    {
        public string Id { get; } = id;
        public TimeSpan Interval { get; } = interval;
        public Action<MetricSample> Handler { get; } = handler;
        public bool Disposed;
    }

    private sealed class Handle(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    /// <summary>A module publishes: stamped with the fake clock, cached, delivered to the background subscribers of the id.</summary>
    public void Put(string id, double value, MetricUnit unit = MetricUnit.Celsius) => PutAt(id, value, _clock.Now, unit);

    public void PutAt(string id, double value, DateTimeOffset at, MetricUnit unit = MetricUnit.Celsius)
    {
        var sample = new MetricSample(new MetricId(id), value, unit, at, "test");
        List<Background> targets;
        lock (_gate) { _latest[id] = sample; targets = _background.Where(b => b.Id == id && !b.Disposed).ToList(); }
        foreach (var b in targets) b.Handler(sample);
    }

    /// <summary>A module withdraws the metric: the cache forgets it and subscribers get the notice.</summary>
    public void Take(string id)
    {
        MetricSample notice;
        List<Background> targets;
        lock (_gate)
        {
            _latest.Remove(id);
            notice = new MetricSample(new MetricId(id), double.NaN, MetricUnit.None, _clock.Now, "test") { IsAvailable = false };
            targets = _background.Where(b => b.Id == id && !b.Disposed).ToList();
        }
        foreach (var b in targets) b.Handler(notice);
    }

    /// <summary>Describes a metric in the catalogue (what GetAllMetricInfo lists), by module, in the order given.</summary>
    public void Describe(string owner, string id, string label, MetricUnit unit = MetricUnit.Celsius, bool available = true)
    {
        lock (_gate) Catalogue.Add(new MetricInfo(new MetricId(id), label, "", unit, null, owner, available));
        CatalogChanged?.Invoke();
    }

    public void ClearCatalogue() { lock (_gate) Catalogue.Clear(); }

    public IReadOnlyList<(string Id, TimeSpan Interval)> Asked
    {
        get { lock (_gate) return _background.Where(b => !b.Disposed).Select(b => (b.Id, b.Interval)).ToList(); }
    }

    public int AskedCount(string id) { lock (_gate) return _background.Count(b => b.Id == id && !b.Disposed); }
    public int EverAsked { get { lock (_gate) return _background.Count; } }

    void IMetricsBus.Publish(MetricId id, double value, MetricUnit unit) => Interlocked.Increment(ref Publishes);
    public IDisposable Subscribe(Action<MetricSample> handler) { Interlocked.Increment(ref PlainSubscribes); return new Handle(() => { }); }
    public IDisposable Subscribe(MetricId id, Action<MetricSample> handler) { Interlocked.Increment(ref PlainSubscribes); return new Handle(() => { }); }

    public bool TryGetLatest(MetricId id, out MetricSample sample)
    {
        lock (_gate) return _latest.TryGetValue(id.Value, out sample!);
    }

    public IReadOnlyList<MetricSample> GetLatest() { lock (_gate) return _latest.Values.ToList(); }

    public IDisposable SubscribeBackground(MetricId id, TimeSpan interval, Action<MetricSample> handler)
    {
        var b = new Background(id.Value, interval, handler);
        lock (_gate) _background.Add(b);
        return new Handle(() => { lock (_gate) b.Disposed = true; });
    }

    public TimeSpan? BackgroundInterval(MetricId id) => null;
    public MetricInfo? GetMetricInfo(MetricId id) { lock (_gate) return Catalogue.FirstOrDefault(m => m.Id == id); }
    public IReadOnlyList<MetricInfo> GetAllMetricInfo() { lock (_gate) return Catalogue.ToList(); }
    public event Action? CatalogChanged;
    public void RaiseCatalogChanged() => CatalogChanged?.Invoke();
}

/// <summary>Reads the module's real en.lang or vi.lang, so a key mistake shows up as the key text.</summary>
internal sealed class FakeLocalisation : ILocalisation
{
    private const string Prefix = "module.watch.";
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

    public string F(string key, params object[] args) =>
        Text.IsLiteral(key) && args.Length == 0 ? Text.Unwrap(key) : string.Format(Language == "vi" ? new CultureInfo("vi-VN") : CultureInfo.InvariantCulture, T(key), args);

    public string Resolve(LocalisedText text) => text.IsEmpty ? "" : text.Args is { Length: > 0 } ? F(text.Key, text.Args) : T(text.Key);
}

internal sealed class FakeLog : ILog
{
    public readonly List<(LogLevel Level, string Message)> Entries = new();
    public void Write(LogLevel level, string message, Exception? exception = null) { lock (Entries) Entries.Add((level, message + (exception is null ? "" : " | " + exception.GetType().Name + ": " + exception.Message))); }
    public string All() { lock (Entries) return string.Join("\n", Entries.Select(e => e.Message)); }
    public int Count(LogLevel level) { lock (Entries) return Entries.Count(e => e.Level == level); }
}

internal sealed class FakeSettings : ISettingsStore
{
    public object? Stored;
    public int Saves;
    public T Load<T>() where T : class, new() => Stored as T ?? new T();
    public T Load<T>(out int storedVersion) where T : class, new() { storedVersion = Stored is null ? 0 : 1; return Load<T>(); }
    public void Save<T>(T value, int version = 1) where T : class { Stored = value; Saves++; }
}

/// <summary>The module must never ask: any call is recorded and refused.</summary>
internal sealed class FakeApproval : IHardwareApproval
{
    public int Asked;
    public Task<bool> ConfirmAsync(HardwareEffect effect, LocalisedText? detail = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Asked);
        return Task.FromResult(false);
    }
}

internal sealed class FakeHotkeys : IHotkeyService
{
    public int Registered;
    public IHotkeyBinding Register(HotkeyRegistration registration) { Registered++; throw new NotSupportedException("Watch registers no shortcut"); }
}

internal sealed class FakeCommands : ICommandRegistry
{
    public readonly Dictionary<string, (CommandInfo Info, CommandHandler Handler)> Verbs = new();

    public void Register(string verb, string descriptionKey, CommandHandler handler) => Register(new CommandInfo(verb, descriptionKey), handler);

    public void Register(CommandInfo info, CommandHandler handler)
    {
        if (Verbs.ContainsKey(info.Verb)) throw new ArgumentException("duplicate verb");
        Verbs[info.Verb] = (info, handler);
    }

    public Task<CommandResult> Run(string verb, params string[] args) => Verbs[verb].Handler(new CommandContext(args, CancellationToken.None));
}

internal sealed class FakeToast : IToastService
{
    public readonly List<ToastMessage> Shown = new();
    public bool ThrowOnShow;
    public int Attempts;
    public void Show(ToastMessage message)
    {
        Interlocked.Increment(ref Attempts);
        if (ThrowOnShow) throw new InvalidOperationException("the notification service failed");
        lock (Shown) Shown.Add(message);
    }
    public int Count { get { lock (Shown) return Shown.Count; } }
}

internal sealed class FakeTray : ITrayService
{
    public readonly List<TrayItem> Items = new();
    public void Add(TrayItem item) => Items.Add(item);
    public void Remove(string id) => Items.RemoveAll(i => i.Id == id);
}

internal sealed class FakeHost : IHostServices
{
    private readonly CancellationTokenSource _stop = new();

    public FakeHost(FakeClock clock, string language = "en")
    {
        Localisation = new FakeLocalisation(language);
        MetricsBus = new FakeBus(clock);
    }

    public readonly FakeBus MetricsBus;
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

    public string ModuleId => "watch";
    public string ModuleDirectory => AppContext.BaseDirectory;
    public string DataDirectory => Path.GetTempPath();
    public Version HostVersion { get; } = new(0, 1, 4);
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

/// <summary>A Watch module on a fake host with a fake clock; the module's own timer is off unless asked for (the test checks).</summary>
internal sealed class Rig
{
    public readonly FakeClock Clock = new();
    public readonly FakeHost Host;
    public readonly WatchModule Module;

    public Rig(string language = "en", bool autoTick = false, WatchSettings? saved = null)
    {
        Host = new FakeHost(Clock, language);
        if (saved is not null) Host.SettingsStore.Stored = saved;
        Module = new WatchModule(new Dependencies { Now = Clock.Func, AutoTick = autoTick });
    }

    public FakeBus Bus => Host.MetricsBus;

    public Rig Start()
    {
        Assert.True(Module.Probe().Supported);
        Module.Initialize(Host);
        return this;
    }

    public PageModel Page() => Module.GetPageModel(Host)!;
    public IEnumerable<Row> Rows() => Page().EnumerateRows();
    public T Row<T>(string headerKey) where T : Row => Rows().OfType<T>().Single(r => r.HeaderKey == headerKey);
    public string Text(LocalisedText? text) => text is null ? "" : Host.Loc.Resolve(text.Value);
    /// <summary>The live line under a rule, as the page shows it.</summary>
    public string Line(string ruleId) => Text(Module.LineText(ruleId));

    /// <summary>The usual rule: CPU temperature at or above 95 for 30 s, clear at 90, quiet 15 min, checked every 10 s.</summary>
    public static WatchRule CpuRule(Action<WatchRule>? change = null)
    {
        var rule = new WatchRule { Id = "cpu1", Metric = "cpu.temp", Kind = RuleKind.Above, Threshold = 95, ClearAt = 90, Seconds = 30, QuietMinutes = 15, IntervalSeconds = 10 };
        change?.Invoke(rule);
        return rule;
    }

    /// <summary>A rig that starts with these rules saved.</summary>
    public static Rig With(params WatchRule[] rules) => new(saved: new WatchSettings { Rules = rules.ToList() });

    /// <summary>Moves the clock by <paramref name="seconds"/>, puts a reading, and checks the rules, step by step (every <paramref name="step"/> seconds), so a duration is measured the way the timer would.</summary>
    public void Run(string metric, double value, int seconds, int step = 10, MetricUnit unit = MetricUnit.Celsius)
    {
        for (int t = 0; t < seconds; t += step)
        {
            Clock.Advance(step);
            Bus.Put(metric, value, unit);
            Module.Tick();
        }
    }
}

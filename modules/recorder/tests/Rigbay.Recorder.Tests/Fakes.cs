using System.Globalization;
using Rigbay.Abstractions;
using Rigbay.Recorder.Recording;

namespace Rigbay.Recorder.Tests;

/// <summary>
/// Time under the test's control. <see cref="Delay"/> moves the clock forward by what was asked plus <see cref="Jitter"/> (a thread that
/// wakes late) and returns at once, so a recording of an hour takes a few milliseconds. After <see cref="BlockAfter"/> waits it blocks
/// until cancelled, which freezes the recording where the test wants to look at it.
/// </summary>
internal sealed class FakeClock
{
    private readonly object _gate = new();
    private DateTimeOffset _now;
    private int _delays;
    private readonly ManualResetEventSlim _blocked = new(false);

    public FakeClock(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 11, 1, 20, 35, 6, 848, TimeSpan.FromHours(7));

    /// <summary>Added to every wake-up, to show that rows do not drift.</summary>
    public TimeSpan Jitter { get; set; }

    /// <summary>The wait after which <see cref="Delay"/> blocks; the default is never.</summary>
    public int BlockAfter { get; set; } = int.MaxValue;

    /// <summary>Called at the start of every wait with its number (1-based), before the clock moves: a test changes the bus here.</summary>
    public Action<int>? OnDelay { get; set; }

    public int Delays => Volatile.Read(ref _delays);

    public DateTimeOffset Now { get { lock (_gate) return _now; } set { lock (_gate) _now = value; } }

    public Func<DateTimeOffset> Clock => () => Now;

    public bool WaitUntilBlocked(int timeoutMs = 30000) => _blocked.Wait(timeoutMs);

    public Task Delay(TimeSpan span, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int n = Interlocked.Increment(ref _delays);
        OnDelay?.Invoke(n);
        if (n > BlockAfter)
        {
            _blocked.Set();
            return Task.Delay(Timeout.Infinite, ct);
        }
        lock (_gate) _now += span + Jitter;
        return Task.CompletedTask;
    }
}

internal sealed class FakeDisk : IDiskSpace
{
    public long? Free = 10L * 1024 * 1024 * 1024;
    public readonly List<string> Asked = new();

    public long? FreeBytes(string folder)
    {
        lock (Asked) Asked.Add(folder);
        return Free;
    }
}

internal sealed class FakeMachine : IMachineFactsSource
{
    public MachineFacts Facts = new("Acme", "Laptop 1", "Fast CPU @ 3.0GHz", "Windows 11 test");
    public MachineFacts Read() => Facts;
}

internal sealed class FakeVersions : IModuleVersions
{
    public readonly Dictionary<string, string> Versions = new() { ["alpha"] = "1.2.3", ["zeta"] = "0.9.0", ["recorder"] = "0.1.0" };
    public string? VersionOf(string moduleId) => Versions.TryGetValue(moduleId, out var v) ? v : null;
}

internal sealed class FakeCatalog : IMetricCatalog
{
    public readonly Dictionary<string, MetricText> Texts = new();
    public MetricText? Describe(MetricId id) => Texts.TryGetValue(id.Value, out var t) ? t : null;
}

internal sealed class FakeShell : IShell
{
    public readonly List<string> Folders = new();
    public readonly List<string> Files = new();
    public Exception? Throws;

    public void OpenFolder(string folder) { if (Throws is { } ex) throw ex; Folders.Add(folder); }
    public void OpenFile(string path) { if (Throws is { } ex) throw ex; Files.Add(path); }
}

/// <summary>A bus that stamps samples with the fake clock, delivers to subscribers and counts them like the real one.</summary>
internal sealed class FakeBus : IMetricsBus
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _self;
    private readonly Dictionary<MetricId, MetricSample> _latest = new();
    private readonly List<Sub> _subs = new();

    public FakeBus(Func<DateTimeOffset> clock, string self = "recorder")
    {
        _clock = clock;
        _self = self;
    }

    public readonly List<MetricDescriptor> Descriptors = new();
    public readonly List<MetricId> Withdrawn = new();
    public readonly List<(MetricId Id, double Value)> OwnPublished = new();

    /// <summary>Publishes as the recorder itself (what <see cref="IMetricsBus.Publish"/> does for a module).</summary>
    public void Publish(MetricId id, double value, MetricUnit unit)
    {
        lock (_gate) OwnPublished.Add((id, value));
        PublishFrom(_self, id.Value, value, unit);
    }

    /// <summary>Publishes as another module.</summary>
    public void PublishFrom(string source, string id, double value, MetricUnit unit = MetricUnit.None, DateTimeOffset? at = null)
    {
        var sample = new MetricSample(new MetricId(id), value, unit, at ?? _clock(), source);
        Sub[] targets;
        lock (_gate)
        {
            _latest[sample.Id] = sample;
            targets = _subs.Where(s => s.Filter is null || s.Filter == sample.Id).ToArray();
        }
        foreach (var t in targets) t.Handler(sample);
    }

    public void WithdrawFrom(string source, string id)
    {
        var mid = new MetricId(id);
        MetricSample notice;
        Sub[] targets;
        lock (_gate)
        {
            if (!_latest.TryGetValue(mid, out var cur) || cur.SourceModule != source) return;
            _latest.Remove(mid);
            notice = new MetricSample(mid, double.NaN, cur.Unit, _clock(), source) { IsAvailable = false };
            targets = _subs.Where(s => s.Filter is null || s.Filter == mid).ToArray();
        }
        foreach (var t in targets) t.Handler(notice);
    }

    public void Withdraw(MetricId id)
    {
        lock (_gate) Withdrawn.Add(id);
        WithdrawFrom(_self, id.Value);
    }

    public IDisposable Subscribe(Action<MetricSample> handler) => Add(null, handler);
    public IDisposable Subscribe(MetricId id, Action<MetricSample> handler) => Add(id, handler);

    private IDisposable Add(MetricId? filter, Action<MetricSample> handler)
    {
        var sub = new Sub(this, filter, handler);
        lock (_gate) _subs.Add(sub);
        return sub;
    }

    public bool TryGetLatest(MetricId id, out MetricSample sample) { lock (_gate) return _latest.TryGetValue(id, out sample!); }
    public IReadOnlyList<MetricSample> GetLatest() { lock (_gate) return _latest.Values.OrderBy(s => s.Id.Value, StringComparer.Ordinal).ToList(); }
    public MetricSample? Latest(MetricId id) { lock (_gate) return _latest.TryGetValue(id, out var s) ? s : null; }
    public void Describe(MetricDescriptor descriptor) { lock (_gate) Descriptors.Add(descriptor); }
    public int SubscriberCount(MetricId id) { lock (_gate) return _subs.Count(s => s.Filter is null || s.Filter == id); }

    public int Subscribers { get { lock (_gate) return _subs.Count; } }

    // ---- the catalogue of task A12 -----------------------------------------------------------------------------------------
    public readonly Dictionary<string, MetricInfo> Infos = new();
    public MetricInfo? GetMetricInfo(MetricId id) { lock (_gate) return Infos.TryGetValue(id.Value, out var i) ? i : null; }
    public IReadOnlyList<MetricInfo> GetAllMetricInfo() { lock (_gate) return Infos.Values.ToList(); }
    public event Action? CatalogChanged;
    public long CatalogVersion { get; private set; }
    public void RaiseCatalogChanged() { CatalogVersion++; CatalogChanged?.Invoke(); }

    private sealed class Sub(FakeBus bus, MetricId? filter, Action<MetricSample> handler) : IDisposable
    {
        public MetricId? Filter { get; } = filter;
        public Action<MetricSample> Handler { get; } = handler;
        public void Dispose() { lock (bus._gate) bus._subs.Remove(this); }
    }
}

/// <summary>Reads the module's real en.lang or vi.lang, so key mistakes show up as the key text.</summary>
internal sealed class FakeLocalisation : ILocalisation
{
    private const string Prefix = "module.recorder.";
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
        var path = Path.Combine(AppContext.BaseDirectory, "lang", language + ".lang");
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == '@') continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            d[line[..eq].Trim()] = Unescape(line[(eq + 1)..].Trim());
        }
        return d;
    }

    /// <summary>The host's rule (LanguageFile.Unescape): <c>\n</c> is a line break, <c>\t</c> a tab, and <c>\X</c> is just X, so a backslash is written <c>\\</c>.</summary>
    public static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char c = s[++i];
                sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c);
            }
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
    public void Write(LogLevel level, string message, Exception? exception = null) { lock (Entries) Entries.Add((level, message + (exception is null ? "" : " | " + exception.Message))); }
    public bool Has(string text) { lock (Entries) return Entries.Any(e => e.Message.Contains(text, StringComparison.Ordinal)); }
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

internal sealed class FakeApproval : IHardwareApproval
{
    public int Asked;
    public Task<bool> ConfirmAsync(HardwareEffect effect, LocalisedText? detail = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Asked);
        return Task.FromResult(false);
    }
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

    public CommandResult RunSync(string verb, params string[] args) => Run(verb, args).GetAwaiter().GetResult();
}

internal sealed class FakeToast : IToastService
{
    public readonly List<ToastMessage> Shown = new();
    public void Show(ToastMessage message) { lock (Shown) Shown.Add(message); }
}

internal sealed class FakeTray : ITrayService
{
    public readonly List<TrayItem> Items = new();
    public void Add(TrayItem item) => Items.Add(item);
    public void Remove(string id) => Items.RemoveAll(i => i.Id == id);
    public TrayItem Item(string id) => Items.Single(i => i.Id == id);
}

internal sealed class FakeHost : IHostServices
{
    private readonly CancellationTokenSource _stop = new();

    public FakeHost(FakeClock clock, string language = "en")
    {
        Clock = clock;
        MetricsBus = new FakeBus(clock.Clock);
        Localisation = new FakeLocalisation(language);
    }

    public readonly FakeClock Clock;
    public readonly FakeBus MetricsBus;
    public readonly FakeLog LogSink = new();
    public readonly FakeSettings SettingsStore = new();
    public readonly FakeApproval ApprovalService = new();
    public readonly FakeHotkeys HotkeyService = new();
    public readonly FakeCommands CommandRegistry = new();
    public readonly FakeToast ToastService = new();
    public readonly FakeTray TrayService = new();
    public FakeLocalisation Loc => (FakeLocalisation)Localisation;

    public string ModuleId => "recorder";
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

    public readonly ObservableValue<bool> PageVisibleValue = new(false);
    public IObservableValue<bool> PageVisible => PageVisibleValue;
    public void RunOnUi(Action action) => action();

    public int PageRefreshRequests;
    public void RequestPageRefresh() => Interlocked.Increment(ref PageRefreshRequests);
}

internal static class Wait
{
    public static bool Until(Func<bool> condition, int timeoutMs = 30000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }
}

/// <summary>A folder under the temp folder, removed afterwards (the test's own folder, never the user's).</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rigbay-recorder-tests-" + Guid.NewGuid().ToString("N")[..10]); Directory.CreateDirectory(Path); }
    public string Path { get; }
    public string[] Csvs => Directory.GetFiles(Path, "*.csv").OrderBy(p => p, StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class Files
{
    /// <summary>Reads a file the recorder may still have open (it allows readers only).</summary>
    public static byte[] ReadBytes(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    public static string[] Lines(string path) => System.Text.Encoding.UTF8.GetString(ReadBytes(path)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// Holds every thread of the pool until it is disposed and stops the pool from growing, so that nothing queued to the pool runs meanwhile. A real
/// pool is never quite that stuck, but a CI runner with two cores and several test projects running at once gets near it for seconds (the pool adds
/// about one thread every half second, and a few tests keep threads asleep for a minute). Use it for the shortest time you can, and only in the
/// "starved-pool" collection, which runs alone.
/// </summary>
internal sealed class PoolHog : IDisposable
{
    private readonly int _oldWorkers, _oldIo;
    private volatile bool _released;
    private int _running;

    public PoolHog()
    {
        ThreadPool.GetMaxThreads(out _oldWorkers, out _oldIo);
        ThreadPool.GetMinThreads(out int min, out _);
        int threads = Math.Max(min, ThreadPool.ThreadCount);
        // The pool may not grow any more, and every thread it can have is held: whatever is queued now runs only after Dispose.
        if (!ThreadPool.SetMaxThreads(threads, _oldIo)) throw new InvalidOperationException("the pool's limit could not be lowered");
        // More items than threads: a thread that is busy with something else and frees up takes one of these, not the work of the test.
        for (int i = 0; i < threads + 8; i++)
            ThreadPool.UnsafeQueueUserWorkItem(_ => { Interlocked.Increment(ref _running); while (!_released) Thread.Sleep(5); }, null);
        // Full when items are still waiting and no thread has started one for a while.
        var end = Environment.TickCount64 + 30000;
        int last = -1;
        long since = Environment.TickCount64;
        while (true)
        {
            int now = Volatile.Read(ref _running);
            if (now != last) { last = now; since = Environment.TickCount64; }
            else if (ThreadPool.PendingWorkItemCount > 0 && Environment.TickCount64 - since > 200) break;
            if (Environment.TickCount64 > end) { Dispose(); throw new TimeoutException("the pool did not fill up"); }
            Thread.Sleep(5);
        }
    }

    public void Dispose()
    {
        _released = true;
        ThreadPool.SetMaxThreads(_oldWorkers, _oldIo);
    }
}

[CollectionDefinition("starved-pool", DisableParallelization = true)]
public sealed class StarvedPoolCollection { }

/// <summary>A context like the UI thread's or xUnit's: a continuation that finds it is queued to the pool, never run inline.</summary>
internal sealed class NotInlineContext : SynchronizationContext { }

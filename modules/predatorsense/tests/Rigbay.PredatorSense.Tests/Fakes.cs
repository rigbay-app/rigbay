using System.Globalization;
using Rigbay.Abstractions;
using Rigbay.PredatorSense.Wmi;

namespace Rigbay.PredatorSense.Tests;

/// <summary>A scriptable stand-in for the firmware. Records every call it receives.</summary>
internal sealed class FakeAcerWmi : IAcerWmi
{
    public readonly object Gate = new();
    public readonly List<int> Reads = new();
    public readonly List<(ulong Value, TimeSpan? Timeout)> Writes = new();

    /// <summary>Raw answers by index. Defaults are the values seen on the real machine in B4.</summary>
    public readonly Dictionary<int, ulong> Sensors = new()
    {
        [1] = Raw(61), [2] = Raw(2564), [3] = Raw(44), [6] = Raw(2654), [9] = Raw(2663), [10] = Raw(52),
    };

    /// <summary>Every operation-mode read in order: 0x0B (current mode) or 0x0A (supported modes). Nothing else can be recorded here.</summary>
    public readonly List<uint> MiscReads = new();

    /// <summary>Raw answer of the current-mode read. Default is what the real machine gave in B10: status 0, Default (1).</summary>
    public ulong OperationModeAnswer = 0x0B0100;

    /// <summary>Raw answer of the supported-modes read. Default is B10's: status 0, bits 0, 1, 4, 5.</summary>
    public ulong SupportedModesAnswer = 0x0B3300;
    public Exception? MiscReadThrows;

    public Exception? ReadThrows;
    public Exception? WriteThrows;
    public uint WriteAnswer;
    public Action<ulong>? OnWrite;
    public Action? InsideWrite;

    /// <summary>Called on the reading thread for every read, before the answer is given (a test uses it to hold a poll in flight).</summary>
    public Action<int>? OnRead;

    public static ulong Raw(int value, int status = 0) => (ulong)(uint)(status & 0xFF) | ((ulong)(uint)value << 8);

    public void SetFans(int cpu, int gpu1, int gpu2)
    {
        lock (Gate) { Sensors[2] = Raw(cpu); Sensors[6] = Raw(gpu1); Sensors[9] = Raw(gpu2); }
    }

    public int WriteCount { get { lock (Gate) return Writes.Count; } }

    public ulong ReadSysInfo(int index)
    {
        lock (Gate) Reads.Add(index);
        OnRead?.Invoke(index);
        if (ReadThrows is { } ex) throw ex;
        lock (Gate) return Sensors.TryGetValue(index, out var raw) ? raw : Raw(0);
    }

    public ulong ReadOperationMode()
    {
        lock (Gate) MiscReads.Add(0x0B);
        if (MiscReadThrows is { } ex) throw ex;
        lock (Gate) return OperationModeAnswer;
    }

    public ulong ReadSupportedOperationModes()
    {
        lock (Gate) MiscReads.Add(0x0A);
        if (MiscReadThrows is { } ex) throw ex;
        lock (Gate) return SupportedModesAnswer;
    }

    public uint SetFanBehavior(ulong value, TimeSpan? timeout = null)
    {
        lock (Gate) Writes.Add((value, timeout));
        InsideWrite?.Invoke();
        if (WriteThrows is { } ex) throw ex;
        OnWrite?.Invoke(value);
        return WriteAnswer;
    }
}

internal sealed class FakeMachine : IMachineInfo
{
    public bool Service = true;
    public bool WmiClass = true;
    public string? Model = "Predator PT516-51s";
    public Exception? Throws;

    public bool ServiceExists(string serviceName) => Throws is { } ex ? throw ex : Service;
    public bool WmiClassExists(string wmiNamespace, string className) => Throws is { } ex ? throw ex : WmiClass;
    public string? ModelName() => Model;
}

internal sealed class FakeMetrics : IMetricsBus
{
    private readonly object _gate = new();
    public readonly List<(MetricId Id, double Value, MetricUnit Unit)> Published = new();
    private readonly Dictionary<MetricId, MetricSample> _latest = new();

    public void Publish(MetricId id, double value, MetricUnit unit)
    {
        lock (_gate)
        {
            Published.Add((id, value, unit));
            _latest[id] = new MetricSample(id, value, unit, DateTimeOffset.Now, "predatorsense");
        }
    }

    public IDisposable Subscribe(Action<MetricSample> handler) => new Nothing();
    public IDisposable Subscribe(MetricId id, Action<MetricSample> handler) => new Nothing();
    public bool TryGetLatest(MetricId id, out MetricSample sample) { lock (_gate) return _latest.TryGetValue(id, out sample!); }
    public IReadOnlyList<MetricSample> GetLatest() { lock (_gate) return _latest.Values.ToList(); }

    // ---- A9 members: withdraw, descriptors, subscriber count --------------------------------------------------------------
    public readonly List<MetricId> Withdrawn = new();
    public readonly List<MetricDescriptor> Descriptors = new();
    private readonly Dictionary<MetricId, int> _subscribers = new();

    public void Withdraw(MetricId id)
    {
        lock (_gate)
        {
            if (!_latest.Remove(id)) return;   // like the real bus: nothing current, nothing to withdraw
            Withdrawn.Add(id);
        }
    }

    public MetricSample? Latest(MetricId id) { lock (_gate) return _latest.TryGetValue(id, out var s) ? s : null; }
    public void Describe(MetricDescriptor descriptor) { lock (_gate) Descriptors.Add(descriptor); }
    public int SubscriberCount(MetricId id) { lock (_gate) return _subscribers.TryGetValue(id, out var n) ? n : 0; }
    public event Action? SubscribersChanged;

    /// <summary>Background demands (host API 0.1.4): the interval somebody asks for an id.</summary>
    private readonly Dictionary<MetricId, TimeSpan> _background = new();
    public TimeSpan? BackgroundInterval(MetricId id) { lock (_gate) return _background.TryGetValue(id, out var t) ? t : null; }

    /// <summary>Pretends a reader asked for (or dropped) a background reading of a metric and raises <see cref="SubscribersChanged"/>, as the real bus does.</summary>
    public void SetBackground(MetricId id, TimeSpan? interval)
    {
        lock (_gate) { if (interval is { } t) _background[id] = t; else _background.Remove(id); }
        SubscribersChanged?.Invoke();
    }

    /// <summary>Pretends a reader subscribed to (or left) a metric and raises <see cref="SubscribersChanged"/>.</summary>
    public void SetSubscribers(MetricId id, int count)
    {
        lock (_gate) _subscribers[id] = count;
        SubscribersChanged?.Invoke();
    }

    public bool HasSubscribersHandler => SubscribersChanged is not null;
    public List<(MetricId Id, double Value, MetricUnit Unit)> Snapshot() { lock (_gate) return Published.ToList(); }

    private sealed class Nothing : IDisposable { public void Dispose() { } }
}

internal sealed class FakeLog : ILog
{
    public readonly List<(LogLevel Level, string Message)> Entries = new();
    public void Write(LogLevel level, string message, Exception? exception = null) { lock (Entries) Entries.Add((level, message)); }
    public bool Has(string text) { lock (Entries) return Entries.Any(e => e.Message.Contains(text, StringComparison.Ordinal)); }
}

/// <summary>Reads the module's real en.lang or vi.lang, so key mistakes show up as the key text.</summary>
internal sealed class FakeLocalisation : ILocalisation
{
    private const string Prefix = "module.predatorsense.";
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

    /// <summary>The host's rule for a language file: \n a line break, \t a tab, \\ a backslash; the backslash before any other character is dropped.</summary>
    private static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { char c = s[++i]; sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c); }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public string T(string key) =>
        _current.TryGetValue(Prefix + key, out var v) ? v : _english.TryGetValue(Prefix + key, out var e) ? e : Prefix + key;

    public string F(string key, params object[] args) => string.Format(CultureInfo.InvariantCulture, T(key), args);

    public string Resolve(LocalisedText text) => text.IsEmpty ? "" : text.Args is { Length: > 0 } ? F(text.Key, text.Args) : T(text.Key);
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
    public bool Answer;
    public readonly List<(HardwareEffect Effect, LocalisedText? Detail)> Asked = new();

    public Task<bool> ConfirmAsync(HardwareEffect effect, LocalisedText? detail = null, CancellationToken cancellationToken = default)
    {
        lock (Asked) Asked.Add((effect, detail));
        return Task.FromResult(Answer);
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
    public readonly List<FakeBinding> Bindings = new();

    public IHotkeyBinding Register(HotkeyRegistration registration)
    {
        if (Registered.Any(r => r.ActionId == registration.ActionId)) throw new ArgumentException("duplicate action id");
        Registered.Add(registration);
        var b = new FakeBinding(registration.ActionId, registration.Default);
        Bindings.Add(b);
        return b;
    }

    public void Press(string actionId) => Registered.Single(r => r.ActionId == actionId).Invoke();
}

internal sealed class FakeCommands : ICommandRegistry
{
    public readonly Dictionary<string, (string DescriptionKey, CommandHandler Handler)> Verbs = new();
    public readonly Dictionary<string, CommandInfo> Infos = new();

    public void Register(CommandInfo info, CommandHandler handler)
    {
        Register(info.Verb, info.DescriptionKey, handler);
        Infos[info.Verb] = info;
    }

    public void Register(string verb, string descriptionKey, CommandHandler handler)
    {
        if (Verbs.ContainsKey(verb)) throw new ArgumentException("duplicate verb");
        Verbs[verb] = (descriptionKey, handler);
    }

    public Task<CommandResult> Run(string verb, params string[] args) =>
        Verbs[verb].Handler(new CommandContext(args, CancellationToken.None));
}

internal sealed class FakeToast : IToastService
{
    public readonly List<ToastMessage> Shown = new();
    public void Show(ToastMessage message) { lock (Shown) Shown.Add(message); }
}

internal sealed class FakeTray : ITrayService
{
    public void Add(TrayItem item) { }
    public void Remove(string id) { }
}

internal sealed class FakeHost : IHostServices
{
    private readonly CancellationTokenSource _stop = new();

    public FakeHost(string language = "en")
    {
        Localisation = new FakeLocalisation(language);
    }

    public readonly FakeMetrics MetricsBus = new();
    public readonly FakeLog LogSink = new();
    public readonly FakeSettings SettingsStore = new();
    public readonly FakeApproval ApprovalService = new();
    public readonly FakeHotkeys HotkeyService = new();
    public readonly FakeCommands CommandRegistry = new();
    public readonly FakeToast ToastService = new();
    public FakeLocalisation Loc => (FakeLocalisation)Localisation;

    public string ModuleId => "predatorsense";
    public string ModuleDirectory => AppContext.BaseDirectory;
    public string DataDirectory => Path.GetTempPath();
    public Version HostVersion { get; } = new(0, 1);
    public CancellationToken StopToken => _stop.Token;
    public ISettingsStore Settings => SettingsStore;
    public ILocalisation Localisation { get; }
    public ILog Log => LogSink;
    public IHotkeyService Hotkeys => HotkeyService;
    public ITrayService Tray { get; } = new FakeTray();
    public ICommandRegistry Commands => CommandRegistry;
    public IToastService Toast => ToastService;
    public IMetricsBus Metrics => MetricsBus;
    public IHardwareApproval Approval => ApprovalService;

    /// <summary>The page-shown signal. A real host starts it false until the app shows the page.</summary>
    public readonly ObservableValue<bool> PageVisibleValue = new(false);
    public IObservableValue<bool> PageVisible => PageVisibleValue;
    public void RunOnUi(Action action) => action();

    /// <summary>Counts refresh requests (added when the contract gained RequestPageRefresh, task A8).</summary>
    public int PageRefreshRequests;

    public void RequestPageRefresh() => PageRefreshRequests++;
}

internal static class Wait
{
    public static bool Until(Func<bool> condition, int timeoutMs = 30000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    /// <summary>
    /// The same wait for an async test. It does not hold a thread-pool thread: the tests wait for work that itself needs the pool
    /// (timer callbacks, <c>Task.Run</c>), and a pool whose threads are all asleep in a wait starts new ones only about twice a second.
    /// </summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 30000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }
}

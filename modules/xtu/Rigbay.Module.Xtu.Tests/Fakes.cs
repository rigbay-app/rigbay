using System.Globalization;
using System.Text.Json;
using Rigbay.Abstractions;
using Rigbay.Module.Xtu;

namespace Rigbay.Module.Xtu.Tests;

/// <summary>A pretend XTU tuning service: records every call in order and behaves like a small piece of hardware.</summary>
internal sealed class FakeTuningPort : IXtuTuningPort
{
    public readonly List<string> Calls = new();
    public readonly Dictionary<uint, ControlInfo> Controls = new();
    public List<Proposal> LastProposals = new();
    public Func<IReadOnlyList<Proposal>, PortResult> OnPropose = _ => new PortResult(true, "Success");
    public Func<PortResult> OnApply = () => new PortResult(true, "Success");
    public Exception? ThrowOnConnect;
    public Exception? ThrowOnRefresh;
    public Exception? ThrowOnApply;

    public FakeTuningPort(decimal pl1 = 55, decimal pl2 = 60, bool enable = true, decimal window = 56)
    {
        Controls[XtuIds.Pl1] = new ControlInfo(XtuIds.Pl1, pl1, 90, false, true, null);
        Controls[XtuIds.Pl2] = new ControlInfo(XtuIds.Pl2, pl2, 115, false, true, null);
        Controls[XtuIds.Pl2Enable] = new ControlInfo(XtuIds.Pl2Enable, enable ? 1 : 0, 1, false, true, null);
        Controls[XtuIds.Window] = new ControlInfo(XtuIds.Window, window, 56, false, true, null);
    }

    public bool IsOpen { get; private set; }

    public void Connect()
    {
        Calls.Add("Connect");
        if (ThrowOnConnect is not null) throw ThrowOnConnect;
        IsOpen = true;
    }

    public void Close() { Calls.Add("Close"); IsOpen = false; }
    public void Dispose() => Close();

    public void Refresh()
    {
        Calls.Add("Refresh");
        if (ThrowOnRefresh is not null) throw ThrowOnRefresh;
    }

    public ControlInfo GetControl(uint id)
    {
        Calls.Add($"GetControl({id})");
        return Controls[id];
    }

    public bool GetRestoreOnBoot() { Calls.Add("GetRestoreOnBoot"); return true; }
    public void Discard() => Calls.Add("Discard");

    public PortResult Propose(IReadOnlyList<Proposal> proposals)
    {
        Calls.Add("Propose[" + string.Join(",", proposals.Select(p => $"{p.Id}={p.Value}")) + "]");
        LastProposals = proposals.ToList();
        return OnPropose(proposals);
    }

    public PortResult ApplyPending()
    {
        Calls.Add("ApplyPending");
        if (ThrowOnApply is not null) throw ThrowOnApply;
        var result = OnApply();
        if (result.Success)
            foreach (var p in LastProposals)
                Controls[p.Id] = Controls[p.Id] with { Active = p.Value };
        return result;
    }

    public bool WroteAnything => Calls.Any(c => c == "Discard" || c.StartsWith("Propose", StringComparison.Ordinal) || c == "ApplyPending");
}

internal sealed class FakeMonitorPort : IXtuMonitorPort
{
    public readonly Queue<Dictionary<uint, decimal>> Samples = new();
    public Exception? Throw;
    public int Resets;
    private bool _opened;

    public IReadOnlyDictionary<uint, decimal> GetValues(out bool justOpened)
    {
        if (Throw is not null) throw Throw;
        justOpened = !_opened;
        _opened = true;
        return Samples.Count > 1 ? Samples.Dequeue() : Samples.Peek();
    }

    public void Reset() { Resets++; _opened = false; }
    public void Dispose() { }
}

/// <summary>A pretend client for the logic above it.</summary>
internal sealed class FakeClient : IXtuClient
{
    public XtuState State = new(55, 60, true, 56, 90, 115, true, 56, true);
    public XtuException? ReadError;
    public readonly List<XtuProfile> Applied = new();
    public XtuException? ApplyError;
    public readonly Queue<(Dictionary<uint, decimal> Values, bool JustOpened)> Monitor = new();
    public int MonitorResets;
    public int MonitorReads;
    public bool Disposed;

    public XtuState ReadState() => ReadError is null ? State : throw ReadError;

    public IReadOnlyDictionary<uint, decimal> ReadMonitor(out bool justOpened)
    {
        Interlocked.Increment(ref MonitorReads);
        var (values, opened) = Monitor.Count > 1 ? Monitor.Dequeue() : Monitor.Peek();
        justOpened = opened;
        return values;
    }

    public void ResetMonitor() => MonitorResets++;

    public ApplyOutcome Apply(XtuProfile profile)
    {
        if (ApplyError is not null) throw ApplyError;
        Rules.Validate(profile);
        Applied.Add(profile);
        if (profile.Matches(State)) return new ApplyOutcome(true, State);
        State = State with { Pl1 = profile.Pl1, Pl2 = profile.Pl2, Pl2Enable = profile.Pl2Enable, Window = profile.Window };
        return new ApplyOutcome(false, State);
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeApproval : IHardwareApproval
{
    public bool Answer = true;
    public readonly List<(HardwareEffect Effect, LocalisedText? Detail)> Asked = new();

    public Task<bool> ConfirmAsync(HardwareEffect effect, LocalisedText? detail = null, CancellationToken cancellationToken = default)
    {
        Asked.Add((effect, detail));
        return Task.FromResult(Answer);
    }
}

internal sealed class FakeLog : ILog
{
    public readonly List<string> Lines = new();

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        lock (Lines) Lines.Add($"{level}: {message}" + (exception is null ? "" : " | " + exception.Message));
    }
}

internal sealed class FakeMetrics : IMetricsBus
{
    public readonly List<(MetricId Id, double Value, MetricUnit Unit)> Published = new();

    public void Publish(MetricId id, double value, MetricUnit unit)
    {
        lock (Published) { Published.Add((id, value, unit)); _current.Add(id); }
    }

    public IDisposable Subscribe(Action<MetricSample> handler) => new Nothing();
    public IDisposable Subscribe(MetricId id, Action<MetricSample> handler) => new Nothing();
    public bool TryGetLatest(MetricId id, out MetricSample sample) { sample = null!; return false; }
    public IReadOnlyList<MetricSample> GetLatest() => Array.Empty<MetricSample>();

    // ---- A9 members: withdraw, descriptors, subscriber count --------------------------------------------------------------
    public readonly List<MetricId> Withdrawn = new();
    public readonly List<MetricDescriptor> Descriptors = new();
    private readonly Dictionary<MetricId, int> _subscribers = new();

    /// <summary>Like the real bus: only an id that has a current sample can be withdrawn; publishing it again makes it current.</summary>
    public void Withdraw(MetricId id)
    {
        lock (Published)
        {
            if (!_current.Remove(id)) return;
            Withdrawn.Add(id);
        }
    }

    private readonly HashSet<MetricId> _current = new();
    public bool IsCurrent(MetricId id) { lock (Published) return _current.Contains(id); }
    public void Describe(MetricDescriptor descriptor) { lock (Published) Descriptors.Add(descriptor); }
    public int SubscriberCount(MetricId id) { lock (Published) return _subscribers.TryGetValue(id, out var n) ? n : 0; }
    public event Action? SubscribersChanged;

    public void SetSubscribers(MetricId id, int count)
    {
        lock (Published) _subscribers[id] = count;
        SubscribersChanged?.Invoke();
    }

    public double? Last(string id)
    {
        lock (Published) return Published.LastOrDefault(p => p.Id.Value == id) is { Id.Value.Length: > 0 } p ? p.Value : null;
    }

    private sealed class Nothing : IDisposable { public void Dispose() { } }
}

internal sealed class FakeSystem : ISystemProbe
{
    public string Vendor = "GenuineIntel";
    public ServiceState ServiceState = ServiceState.Running;
    public HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
    public string Fast = "Off";
    public PowerLine Line = PowerLine.Online;

    public string CpuVendor() => Vendor;
    public ServiceState Service(string name) => ServiceState;
    public bool FileExists(string path) => Files.Contains(path);
    public string FileVersion(string path) => "7.14.2.135";
    public string FastStartup() => Fast;
    public PowerLine GetPowerLine() => Line;
}

internal sealed class FakePower : IPowerEnvironment
{
    public PowerLine Line { get; set; } = PowerLine.Online;
    public event Action? Changed;
    public event Action? Resumed;
    public void RaiseChanged() => Changed?.Invoke();
    public void RaiseResumed() => Resumed?.Invoke();
    public void Dispose() { }
}

internal sealed class FakeSettings : ISettingsStore
{
    public object? Stored;
    public int Saves;

    public T Load<T>() where T : class, new() => Load<T>(out _);

    public T Load<T>(out int storedVersion) where T : class, new()
    {
        storedVersion = Stored is null ? 0 : 1;
        return Stored is null ? new T() : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(Stored))!;
    }

    public void Save<T>(T value, int version = 1) where T : class
    {
        Stored = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));
        Saves++;
    }
}

/// <summary>Reads the module's real en.lang so tests resolve texts the way the host would.</summary>
internal sealed class FakeLocalisation : ILocalisation
{
    private readonly Dictionary<string, string> _text = new();

    public FakeLocalisation(string file = "en.lang")
    {
        foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "lang", file)))
        {
            var t = line.Trim();
            if (t.Length == 0 || t[0] == '#' || t[0] == '@') continue;
            var i = t.IndexOf('=');
            if (i > 0) _text[t[..i].Trim()] = t[(i + 1)..].Trim();
        }
    }

    public IReadOnlyCollection<string> Keys => _text.Keys;
    public bool Has(string key) => _text.ContainsKey("module.xtu." + key);
    public string Language => "en";
    public string T(string key) => _text.TryGetValue("module.xtu." + key, out var v) ? v : "module.xtu." + key;

    public string F(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, T(key), args);

    public string Resolve(LocalisedText text) => text.IsEmpty ? "" : text.Args is { Length: > 0 } ? F(text.Key, text.Args) : T(text.Key);

    public event Action? LanguageChanged { add { } remove { } }
}

internal sealed class FakeHotkeys : IHotkeyService
{
    public readonly List<HotkeyRegistration> Registered = new();
    public readonly Dictionary<string, FakeBinding> Bindings = new();

    public IHotkeyBinding Register(HotkeyRegistration registration)
    {
        if (Bindings.ContainsKey(registration.ActionId)) throw new ArgumentException("already registered");
        Registered.Add(registration);
        var b = new FakeBinding(registration.ActionId, registration.Default);
        Bindings[registration.ActionId] = b;
        return b;
    }
}

internal sealed class FakeBinding : IHotkeyBinding
{
    public FakeBinding(string id, HotkeyGesture? gesture)
    {
        ActionId = id;
        Gesture = new ObservableValue<HotkeyGesture?>(gesture);
    }

    public string ActionId { get; }
    public IObservableValue<HotkeyGesture?> Gesture { get; }
    public IObservableValue<bool> HasConflict { get; } = new ObservableValue<bool>(false);
}

internal sealed class FakeTray : ITrayService
{
    public readonly Dictionary<string, TrayItem> Items = new();
    public void Add(TrayItem item)
    {
        if (!Items.TryAdd(item.Id, item)) throw new ArgumentException("already added");
    }
    public void Remove(string id) => Items.Remove(id);
}

internal sealed class FakeCommands : ICommandRegistry
{
    public readonly Dictionary<string, CommandHandler> Handlers = new();
    public void Register(string verb, string descriptionKey, CommandHandler handler) => Handlers.Add(verb, handler);

    public Task<CommandResult> Run(string verb, params string[] args) =>
        Handlers[verb](new CommandContext(args, CancellationToken.None));
}

internal sealed class FakeToast : IToastService
{
    public readonly List<ToastMessage> Shown = new();
    public void Show(ToastMessage message) { lock (Shown) Shown.Add(message); }
}

internal sealed class FakeHost : IHostServices
{
    public readonly CancellationTokenSource Stop = new();

    public FakeHost()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "xtu-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Localisation = FakeLocalisation = new FakeLocalisation();
    }

    public string ModuleId => "xtu";
    public string ModuleDirectory => AppContext.BaseDirectory;
    public string DataDirectory { get; }
    public Version HostVersion => new(0, 1);
    public CancellationToken StopToken => Stop.Token;
    public readonly FakeSettings FakeSettings = new();
    public ISettingsStore Settings => FakeSettings;
    public readonly FakeLocalisation FakeLocalisation;
    public ILocalisation Localisation { get; }
    public readonly FakeLog FakeLog = new();
    public ILog Log => FakeLog;
    public readonly FakeHotkeys FakeHotkeys = new();
    public IHotkeyService Hotkeys => FakeHotkeys;
    public readonly FakeTray FakeTray = new();
    public ITrayService Tray => FakeTray;
    public readonly FakeCommands FakeCommands = new();
    public ICommandRegistry Commands => FakeCommands;
    public readonly FakeToast FakeToast = new();
    public IToastService Toast => FakeToast;
    public readonly FakeMetrics FakeMetrics = new();
    public IMetricsBus Metrics => FakeMetrics;
    /// <summary>The page-shown signal. A real host starts it false until the app shows the page.</summary>
    public readonly ObservableValue<bool> PageVisibleValue = new(false);
    public IObservableValue<bool> PageVisible => PageVisibleValue;
    public readonly FakeApproval FakeApproval = new();
    public IHardwareApproval Approval => FakeApproval;
    public int Refreshes;
    public void RequestPageRefresh() => Refreshes++;
    public void RunOnUi(Action action) => action();
}

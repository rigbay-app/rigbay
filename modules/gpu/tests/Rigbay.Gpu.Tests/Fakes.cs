using System.Globalization;
using Rigbay.Abstractions;
using Rigbay.Gpu.Nvml;

namespace Rigbay.Gpu.Tests;

// ------------------------------------------------------------------ the fake NVML

/// <summary>
/// A GPU that answers from fields. Every function can be told to return another code (<see cref="Results"/>, by the short names below), and the
/// fake notices when two threads are inside it at once (<see cref="MaxInside"/>).
/// </summary>
internal sealed class FakeNvml : INvml
{
    private int _inside;
    private int _maxInside;
    private readonly object _gate = new();

    public readonly Dictionary<string, NvmlReturn> Results = new();   // init, shutdown, count, handle, name, util, power, limit, grclock, memclock, memory, temp, pstate, reasons
    public readonly List<string> Calls = new();
    public int InitCount, ShutdownCount;
    public int Delay;   // milliseconds each call takes, to make overlaps likely

    public uint Count = 1;
    public string Name = "NVIDIA GeForce RTX 3060 Laptop GPU";
    public uint GpuPercent = 31, MemoryControllerPercent = 12;
    public uint PowerMilliwatts = 72_400, LimitMilliwatts = 80_000;
    public uint GraphicsMhz = 1_545, MemoryMhz = 6_000;
    public ulong TotalBytes = 6UL * 1024 * 1024 * 1024, UsedBytes = 1_500UL * 1024 * 1024;
    public uint Celsius = 64;
    public int PState = 0;
    public ulong Reasons = ClockReasons.SoftwarePowerCap;

    public int MaxInside => Volatile.Read(ref _maxInside);
    public int Depth => InitCount - ShutdownCount;

    public int CallsTo(string name) { lock (_gate) return Calls.Count(c => c == name); }
    public int ReadCalls { get { lock (_gate) return Calls.Count(c => c is not ("init" or "shutdown" or "count" or "handle" or "name")); } }

    private NvmlReturn Enter(string name)
    {
        int now = Interlocked.Increment(ref _inside);
        int seen;
        do { seen = Volatile.Read(ref _maxInside); } while (now > seen && Interlocked.CompareExchange(ref _maxInside, now, seen) != seen);
        lock (_gate) Calls.Add(name);
        if (Delay > 0) Thread.Sleep(Delay);
        return Results.TryGetValue(name, out var rc) ? rc : NvmlReturn.Success;
    }

    private void Exit() => Interlocked.Decrement(ref _inside);

    public NvmlReturn Init() { try { var rc = Enter("init"); if (rc == NvmlReturn.Success) Interlocked.Increment(ref InitCount); return rc; } finally { Exit(); } }
    public NvmlReturn Shutdown() { try { var rc = Enter("shutdown"); if (rc == NvmlReturn.Success) Interlocked.Increment(ref ShutdownCount); return rc; } finally { Exit(); } }
    public NvmlReturn GetCount(out uint count) { try { var rc = Enter("count"); count = rc == NvmlReturn.Success ? Count : 0; return rc; } finally { Exit(); } }
    public NvmlReturn GetHandle(uint index, out nint device) { try { var rc = Enter("handle"); device = rc == NvmlReturn.Success ? 0x1000 + (nint)index : 0; return rc; } finally { Exit(); } }
    public NvmlReturn GetName(nint device, out string name) { try { var rc = Enter("name"); name = rc == NvmlReturn.Success ? Name : ""; return rc; } finally { Exit(); } }
    public NvmlReturn GetUtilization(nint device, out uint g, out uint m) { try { var rc = Enter("util"); g = rc == NvmlReturn.Success ? GpuPercent : 0; m = rc == NvmlReturn.Success ? MemoryControllerPercent : 0; return rc; } finally { Exit(); } }
    public NvmlReturn GetPowerUsage(nint device, out uint mw) { try { var rc = Enter("power"); mw = rc == NvmlReturn.Success ? PowerMilliwatts : 0; return rc; } finally { Exit(); } }
    public NvmlReturn GetEnforcedPowerLimit(nint device, out uint mw) { try { var rc = Enter("limit"); mw = rc == NvmlReturn.Success ? LimitMilliwatts : 0; return rc; } finally { Exit(); } }
    public NvmlReturn GetClock(nint device, NvmlClock clock, out uint mhz) { try { var rc = Enter(clock == NvmlClock.Graphics ? "grclock" : "memclock"); mhz = rc == NvmlReturn.Success ? (clock == NvmlClock.Graphics ? GraphicsMhz : MemoryMhz) : 0; return rc; } finally { Exit(); } }
    public NvmlReturn GetMemory(nint device, out ulong total, out ulong used) { try { var rc = Enter("memory"); total = rc == NvmlReturn.Success ? TotalBytes : 0; used = rc == NvmlReturn.Success ? UsedBytes : 0; return rc; } finally { Exit(); } }
    public NvmlReturn GetTemperature(nint device, out uint c) { try { var rc = Enter("temp"); c = rc == NvmlReturn.Success ? Celsius : 0; return rc; } finally { Exit(); } }
    public NvmlReturn GetPerformanceState(nint device, out int p) { try { var rc = Enter("pstate"); p = rc == NvmlReturn.Success ? PState : 0; return rc; } finally { Exit(); } }
    public NvmlReturn GetClockEventReasons(nint device, out ulong r) { try { var rc = Enter("reasons"); r = rc == NvmlReturn.Success ? Reasons : 0; return rc; } finally { Exit(); } }
    public string ErrorString(NvmlReturn code) => "error " + (int)code;
}

/// <summary>
/// A table of managed functions standing in for the exports of nvml.dll: each is turned into a real function pointer, and <see cref="NvmlApi"/> calls it
/// through the same marshalling it uses on the real library (structs, out parameters, the name buffer).
/// </summary>
internal sealed class FakeExports : INvmlExports
{
    private readonly Dictionary<string, nint> _table = new(StringComparer.Ordinal);
    private readonly List<object> _keepAlive = new();
    public readonly List<string> Asked = new();

    public FakeExports Add<T>(string name, T function) where T : Delegate
    {
        _keepAlive.Add(function);
        _table[name] = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(function);
        return this;
    }

    public nint Find(string name)
    {
        lock (Asked) Asked.Add(name);
        return _table.TryGetValue(name, out var p) ? p : 0;
    }

    /// <summary>The whole current set of exports on a fake GPU. <paramref name="newReasonsName"/> false = an older driver (only the old reasons call).</summary>
    public static FakeExports Complete(FakeNvml gpu, bool newReasonsName = true)
    {
        var e = new FakeExports();
        e.Add(NvmlAllowList.Init, new NvmlApi.NoArguments(() => (int)gpu.Init()));
        e.Add(NvmlAllowList.Shutdown, new NvmlApi.NoArguments(() => (int)gpu.Shutdown()));
        e.Add(NvmlAllowList.DeviceCount, new NvmlApi.CountFn((out uint c) => (int)gpu.GetCount(out c)));
        e.Add(NvmlAllowList.HandleByIndex, new NvmlApi.HandleFn((uint i, out nint d) => (int)gpu.GetHandle(i, out d)));
        e.Add(NvmlAllowList.DeviceName, new NvmlApi.NameFn((nint d, byte[] buffer, uint length) =>
        {
            var rc = gpu.GetName(d, out var name);
            if (rc != NvmlReturn.Success) return (int)rc;
            var bytes = System.Text.Encoding.UTF8.GetBytes(name);
            Array.Clear(buffer);
            Array.Copy(bytes, buffer, Math.Min(bytes.Length, (int)length - 1));
            return 0;
        }));
        e.Add(NvmlAllowList.Utilization, new NvmlApi.UtilizationFn((nint d, out NvmlApi.NativeUtilization u) =>
        {
            var rc = gpu.GetUtilization(d, out var g, out var m);
            u = new NvmlApi.NativeUtilization { Gpu = g, Memory = m };
            return (int)rc;
        }));
        e.Add(NvmlAllowList.PowerUsage, new NvmlApi.UIntFn((nint d, out uint v) => (int)gpu.GetPowerUsage(d, out v)));
        e.Add(NvmlAllowList.EnforcedPowerLimit, new NvmlApi.UIntFn((nint d, out uint v) => (int)gpu.GetEnforcedPowerLimit(d, out v)));
        e.Add(NvmlAllowList.ClockInfo, new NvmlApi.ClockFn((nint d, int type, out uint v) => (int)gpu.GetClock(d, (NvmlClock)type, out v)));
        e.Add(NvmlAllowList.MemoryInfo, new NvmlApi.MemoryFn((nint d, out NvmlApi.NativeMemory m) =>
        {
            var rc = gpu.GetMemory(d, out var total, out var used);
            m = new NvmlApi.NativeMemory { Total = total, Used = used, Free = total - used };
            return (int)rc;
        }));
        e.Add(NvmlAllowList.Temperature, new NvmlApi.TemperatureFn((nint d, int sensor, out uint v) =>
        {
            if (sensor != 0) { v = 0; return (int)NvmlReturn.InvalidArgument; }   // only the GPU core sensor exists
            return (int)gpu.GetTemperature(d, out v);
        }));
        e.Add(NvmlAllowList.PerformanceState, new NvmlApi.PStateFn((nint d, out int p) => (int)gpu.GetPerformanceState(d, out p)));
        var reasons = new NvmlApi.ULongFn((nint d, out ulong r) => (int)gpu.GetClockEventReasons(d, out r));
        e.Add(newReasonsName ? NvmlAllowList.ClocksEventReasons : NvmlAllowList.ClocksThrottleReasons, reasons);
        var errorText = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi("fake NVML text");
        e.Add(NvmlAllowList.ErrorString, new NvmlApi.ErrorStringFn(_ => errorText));
        return e;
    }
}

// ------------------------------------------------------------------ the host

internal sealed class FakeBus : IMetricsBus
{
    private readonly object _gate = new();
    public readonly Dictionary<string, (double Value, MetricUnit Unit)> Latest = new();
    public readonly List<MetricDescriptor> Descriptors = new();
    public readonly List<string> Withdrawn = new();       // ids that were available and were taken back
    public readonly List<string> WithdrawCalls = new();   // every call
    public readonly List<string> Published = new();
    public readonly HashSet<string> Subscribed = new();   // ids somebody reads

    public void Publish(MetricId id, double value, MetricUnit unit)
    {
        lock (_gate) { Latest[id.Value] = (value, unit); Published.Add(id.Value); }
    }

    public void Withdraw(MetricId id)
    {
        lock (_gate) { WithdrawCalls.Add(id.Value); if (Latest.Remove(id.Value)) Withdrawn.Add(id.Value); }
    }

    public void Describe(MetricDescriptor descriptor) { lock (_gate) Descriptors.Add(descriptor); }
    public int SubscriberCount(MetricId id) { lock (_gate) return Subscribed.Contains(id.Value) ? 1 : 0; }
    public event Action? SubscribersChanged;
    public void RaiseSubscribersChanged() => SubscribersChanged?.Invoke();

    /// <summary>Background demands (host API 0.1.4): id to the interval somebody asks; the shortest of a module's asks is what <see cref="BackgroundInterval"/> returns.</summary>
    public readonly Dictionary<string, TimeSpan> Background = new();
    public TimeSpan? BackgroundInterval(MetricId id) { lock (_gate) return Background.TryGetValue(id.Value, out var t) ? t : null; }
    public void SetBackground(string id, TimeSpan? interval)
    {
        lock (_gate) { if (interval is { } t) Background[id] = t; else Background.Remove(id); }
        SubscribersChanged?.Invoke();
    }
    public IDisposable Subscribe(Action<MetricSample> handler) => new Nop();
    public IDisposable Subscribe(MetricId id, Action<MetricSample> handler) => new Nop();
    public bool TryGetLatest(MetricId id, out MetricSample sample) { sample = null!; return false; }
    public IReadOnlyList<MetricSample> GetLatest() => Array.Empty<MetricSample>();
    public bool Has(string id) { lock (_gate) return Latest.ContainsKey(id); }
    public double Value(string id) { lock (_gate) return Latest[id].Value; }
    public MetricUnit UnitOf(string id) { lock (_gate) return Latest[id].Unit; }
    public int PublishCount { get { lock (_gate) return Published.Count; } }
    public bool WasWithdrawn(string id) { lock (_gate) return WithdrawCalls.Contains(id); }

    private sealed class Nop : IDisposable { public void Dispose() { } }
}

/// <summary>Reads the module's real en.lang or vi.lang, so a key mistake shows up as the key text.</summary>
internal sealed class FakeLocalisation : ILocalisation
{
    private const string Prefix = "module.gpu.";
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
    public readonly List<HardwareEffect> Asked = new();

    public Task<bool> ConfirmAsync(HardwareEffect effect, LocalisedText? detail = null, CancellationToken cancellationToken = default)
    {
        lock (Asked) Asked.Add(effect);
        return Task.FromResult(false);
    }
}

internal sealed class FakeHotkeys : IHotkeyService
{
    public readonly List<HotkeyRegistration> Registered = new();

    public IHotkeyBinding Register(HotkeyRegistration registration)
    {
        Registered.Add(registration);
        throw new NotSupportedException("the GPU module registers no shortcut");
    }
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

    public Task<CommandResult> Run(string verb, params string[] args) => Verbs[verb].Handler(new CommandContext(args, CancellationToken.None));
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

    public string ModuleId => "gpu";
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

/// <summary>A module on a fake GPU on a fake host, with the real timer switched off (the test polls).</summary>
internal sealed class Rig
{
    public readonly FakeNvml Gpu = new();
    public readonly FakeHost Host;
    public readonly GpuModule Module;
    public DateTimeOffset Now = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    public Rig(string language = "en", bool autoPoll = false, FakeNvml? gpu = null)
    {
        if (gpu is not null) Gpu = gpu;
        Host = new FakeHost(language);
        Module = new GpuModule(new Dependencies
        {
            OpenLibrary = () => new Dependencies.Opened(Gpu, null),
            Now = () => Now,
            AutoPoll = autoPoll,
        });
    }

    /// <summary>Probe, then Initialize.</summary>
    public Rig Start()
    {
        Assert.True(Module.Probe().Supported);
        Module.Initialize(Host);
        return this;
    }

    public PageModel Page() => Module.GetPageModel(Host)!;
    public string StateLine() => Host.Loc.Resolve(Page().Summary!.StateLine!.Value);
    public T Row<T>(string headerKey) where T : Row => Page().EnumerateRows().OfType<T>().Single(r => r.HeaderKey == headerKey);
}

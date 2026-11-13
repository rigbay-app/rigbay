using System.Globalization;
using System.Net;
using Rigbay.Abstractions;
using Rigbay.Tunnel.Wg;

namespace Rigbay.Tunnel.Tests;

// Fake tunnel names and addresses used everywhere in the tests come from the documentation ranges (RFC 5737, RFC 1918 examples):
// example-tunnel, 198.51.100.1, 10.9.0.2. Nothing here is a real name or address.

/// <summary>Time under the test's control: <see cref="Delay"/> moves the clock and returns at once.</summary>
internal sealed class FakeClock : IClock
{
    private readonly object _gate = new();
    private DateTimeOffset _now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    public int Delays;
    public Action<int>? OnDelay;

    public DateTimeOffset Now { get { lock (_gate) return _now; } set { lock (_gate) _now = value; } }

    public Task Delay(TimeSpan span, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int n = Interlocked.Increment(ref Delays);
        OnDelay?.Invoke(n);
        lock (_gate) _now += span;
        return Task.CompletedTask;
    }
}

/// <summary>The service control manager: a table of services by service name.</summary>
internal sealed class FakeServices : IServiceControl
{
    public readonly Dictionary<string, ServiceSnapshot> Table = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<(string Service, StartKind Kind)> StartChanges = new();
    public int SetStartResult;
    public Exception? QueryThrows;

    public ServiceSnapshot Query(string serviceName)
    {
        if (QueryThrows is { } ex) throw ex;
        lock (Table) return Table.TryGetValue(serviceName, out var s) ? s : ServiceSnapshot.Missing;
    }

    public int SetStartKind(string serviceName, StartKind start)
    {
        lock (Table)
        {
            StartChanges.Add((serviceName, start));
            if (SetStartResult == 0 && Table.TryGetValue(serviceName, out var s)) Table[serviceName] = s with { Start = start };
            return SetStartResult;
        }
    }

    public void Set(string name, ServiceState state, StartKind start = StartKind.Automatic, int win32 = 0, int specific = 0)
    {
        lock (Table) Table[TunnelNames.ServiceName(name)] = new ServiceSnapshot(state, start, win32, specific);
    }

    public void Remove(string name) { lock (Table) Table.Remove(TunnelNames.ServiceName(name)); }
    public ServiceSnapshot Of(string name) => Query(TunnelNames.ServiceName(name));
}

/// <summary>WireGuard's two programs. Install and uninstall change <see cref="FakeServices"/> the way the real ones do, unless a test says they fail.</summary>
internal sealed class FakeTools : IWireGuardTools
{
    private readonly FakeServices _services;
    public FakeTools(FakeServices services) => _services = services;

    public ToolProbe ProbeResult = ToolProbe.Yes;
    public readonly List<string> Calls = new();
    public readonly List<string> Installed = new();      // paths given to /installtunnelservice
    public readonly List<string> Uninstalled = new();    // names given to /uninstalltunnelservice
    public readonly List<(string Name, WgField Field)> Shows = new();

    /// <summary>What an install does to the service table; default: the service is running.</summary>
    public Func<string, ToolRun>? OnInstall;
    public Func<string, ToolRun>? OnUninstall;
    public Func<string, WgField, string?>? ShowOutput;
    public Action? BeforeInstall;

    public ToolProbe Probe() => ProbeResult;

    public Task<ToolRun> InstallTunnelServiceAsync(string configPath, CancellationToken ct)
    {
        Calls.Add("install");
        Installed.Add(configPath);
        BeforeInstall?.Invoke();
        var file = Path.GetFileName(configPath);
        var name = file.EndsWith(".conf.dpapi", StringComparison.OrdinalIgnoreCase) ? file[..^".conf.dpapi".Length] : file[..^".conf".Length];
        if (OnInstall is { } custom) return Task.FromResult(custom(name));
        _services.Set(name, ServiceState.Running);
        return Task.FromResult(new ToolRun(0));
    }

    public Task<ToolRun> UninstallTunnelServiceAsync(string name, CancellationToken ct)
    {
        Calls.Add("uninstall");
        Uninstalled.Add(name);
        if (OnUninstall is { } custom) return Task.FromResult(custom(name));
        _services.Remove(name);
        return Task.FromResult(new ToolRun(0));
    }

    public Task<string?> ShowAsync(string name, WgField field, CancellationToken ct)
    {
        Calls.Add("show " + field);
        Shows.Add((name, field));
        return Task.FromResult(ShowOutput?.Invoke(name, field));
    }
}

internal sealed class FakeStore : ITunnelStore
{
    public readonly List<StoredTunnel> Tunnels = new();
    public bool Readable = true;

    public FakeStore Add(string name, string extension = ".conf.dpapi")
    {
        Tunnels.Add(new StoredTunnel(name, @"C:\Store\" + name + extension));
        return this;
    }

    public StoreListing List() => new(Tunnels.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList(), Readable);
}

internal sealed class FakeNetwork : INetworkProbe
{
    public readonly Dictionary<string, IReadOnlyList<IPAddress>> Adapters = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<int> Listening = new();

    public IReadOnlyList<IPAddress> AddressesOf(string adapterName) => Adapters.TryGetValue(adapterName, out var a) ? a : Array.Empty<IPAddress>();
    public bool IsListening(IReadOnlyList<IPAddress> addresses, int port) => Listening.Contains(port);
}

/// <summary>The whole machine of one test.</summary>
internal sealed class World
{
    public readonly FakeClock Clock = new();
    public readonly FakeServices Services = new();
    public readonly FakeTools Tools;
    public readonly FakeStore Store = new();
    public readonly FakeNetwork Network = new();

    public World() => Tools = new FakeTools(Services);

    /// <summary>A sample of what wg.exe prints, with a made-up peer key (44 characters, not a real key).</summary>
    public static readonly string PeerKey = new string('A', 43) + "=";

    public static string Handshakes(DateTimeOffset? at) => PeerKey + "\t" + (at?.ToUnixTimeSeconds() ?? 0) + "\n";
    public static string Transfer(long rx, long tx) => PeerKey + "\t" + rx + "\t" + tx + "\n";
    public static string Endpoint(string endpoint) => PeerKey + "\t" + endpoint + "\n";

    public Dependencies Dependencies => new()
    {
        Tools = Tools,
        Store = Store,
        Services = Services,
        Network = Network,
        Clock = Clock,
        AutoPoll = false,
    };

    public TunnelManager Manager() => new(Tools, Store, Services, Network, Clock);
}

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

    /// <summary>Background demands (host API 0.1.4): id to the interval somebody asks.</summary>
    public readonly Dictionary<string, TimeSpan> Background = new();
    public TimeSpan? BackgroundInterval(MetricId id) { lock (_gate) return Background.TryGetValue(id.Value, out var t) ? t : null; }

    /// <summary>Pretends a reader asked for (or dropped) a background reading of a metric and raises <see cref="SubscribersChanged"/>, as the real bus does.</summary>
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

    private sealed class Nop : IDisposable { public void Dispose() { } }
}

/// <summary>Reads the module's real en.lang or vi.lang, so a key mistake shows up as the key text.</summary>
internal sealed class FakeLocalisation : ILocalisation
{
    private const string Prefix = "module.tunnel.";
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
    public object? Stored;
    public int Saves;
    public T Load<T>() where T : class, new() => Stored as T ?? new T();
    public T Load<T>(out int storedVersion) where T : class, new() { storedVersion = Stored is null ? 0 : 1; return Load<T>(); }
    public void Save<T>(T value, int version = 1) where T : class { Stored = value; Saves++; }
}

/// <summary>Answers every question with <see cref="Answer"/> and remembers what was asked, word for word.</summary>
internal sealed class FakeApproval : IHardwareApproval
{
    public bool Answer;
    public readonly List<(HardwareEffect Effect, LocalisedText? Detail)> Asked = new();

    public Task<bool> ConfirmAsync(HardwareEffect effect, LocalisedText? detail = null, CancellationToken cancellationToken = default)
    {
        lock (Asked) Asked.Add((effect, detail));
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

    public string ModuleId => "tunnel";
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

using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.SteamBypass.Tests;

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
            _latest[id] = new MetricSample(id, value, unit, DateTimeOffset.Now, "steam-bypass");
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
    private const string Prefix = "module.steam-bypass.";
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
            d[line[..eq].Trim()] = line[(eq + 1)..].Trim().Replace("\\n", "\n");
        }
        return d;
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

    public string ModuleId => "steam-bypass";
    public string ModuleDirectory => Path.Combine(AppContext.BaseDirectory, "module");
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "rigbay-steambypass-tests", Guid.NewGuid().ToString("N"));
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
}

// ---------------------------------------------------------------------------------------------------------------------------------
// Fakes for the things the module reaches outside the process through. None of them touches the real Windows setting or a real port.
// ---------------------------------------------------------------------------------------------------------------------------------

/// <summary>The Windows setting in memory. Records every write in order, so a test can check the order against the listeners.</summary>
internal sealed class MemoryProxyStore : Rigbay.SteamBypass.ISystemProxyStore
{
    public string? Value;
    public readonly List<string> Journal = new();
    public Exception? WriteThrows;
    public Exception? ReadThrows;
    public Exception? DeleteThrows;
    public int Notifications;

    public string? ReadPacUrl() => ReadThrows is { } ex ? throw ex : Value;

    public void WritePacUrl(string url)
    {
        if (WriteThrows is { } ex) throw ex;
        Value = url;
        Journal.Add("write " + url);
    }

    public void DeletePacUrl()
    {
        if (DeleteThrows is { } ex) throw ex;
        Value = null;
        Journal.Add("delete");
    }

    public void NotifyChanged() => Notifications++;
    public int Writes => Journal.Count;
}

internal sealed class MemoryStateStore : Rigbay.SteamBypass.IProxyStateStore
{
    public Rigbay.SteamBypass.ProxyState? State;
    public int Saves, Deletes;
    public Rigbay.SteamBypass.ProxyState? Load() => State;
    public void Save(Rigbay.SteamBypass.ProxyState state) { State = state; Saves++; }
    public void Delete() { State = null; Deletes++; }
}

/// <summary>Listeners that never open a socket. Records the ports asked for, in order, into a journal shared with the proxy store.</summary>
internal sealed class FakeListenerFactory : Rigbay.SteamBypass.Net.ILoopbackListenerFactory
{
    public readonly List<int> Requested = new();
    public readonly HashSet<int> Taken = new();
    public readonly List<string> Events = new();
    public int Disposed;

    public Rigbay.SteamBypass.Net.ILoopbackListener Listen(int port)
    {
        lock (Requested) Requested.Add(port);
        if (Taken.Contains(port))
        {
            Events.Add("busy " + port);
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AddressAlreadyInUse);
        }
        Events.Add("listen " + port);
        return new Fake(port, this);
    }

    private sealed class Fake(int port, FakeListenerFactory owner) : Rigbay.SteamBypass.Net.ILoopbackListener
    {
        private readonly TaskCompletionSource<System.Net.Sockets.TcpClient> _never = new();
        public System.Net.IPEndPoint LocalEndPoint { get; } = new(System.Net.IPAddress.Loopback, port);

        public async Task<System.Net.Sockets.TcpClient> AcceptAsync(CancellationToken ct)
        {
            using var reg = ct.Register(() => _never.TrySetCanceled(ct));
            return await _never.Task.ConfigureAwait(false);
        }

        public void Dispose()
        {
            owner.Events.Add("close " + port);
            Interlocked.Increment(ref owner.Disposed);
            _never.TrySetCanceled();
        }
    }
}

internal sealed class FakeResolver : Rigbay.SteamBypass.Net.IHostResolver, IDisposable
{
    public readonly Dictionary<string, System.Net.IPAddress[]> Map = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<(string Host, bool Doh)> Asked = new();
    public bool Disposed;

    public Task<IReadOnlyList<System.Net.IPAddress>> ResolveAsync(string host, bool useDoh, CancellationToken ct)
    {
        lock (Asked) Asked.Add((host, useDoh));
        if (System.Net.IPAddress.TryParse(host, out var literal)) return Task.FromResult<IReadOnlyList<System.Net.IPAddress>>(new[] { literal });
        if (Map.TryGetValue(host, out var ips)) return Task.FromResult<IReadOnlyList<System.Net.IPAddress>>(ips);
        throw new Rigbay.SteamBypass.Net.DnsMissException(host);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>A dialer that connects every host to a fake upstream server on loopback (the test's own), and records what was asked.</summary>
internal sealed class FakeDialer : Rigbay.SteamBypass.Net.IUpstreamDialer
{
    public readonly List<(string Host, int Port, bool ForceDoh)> Calls = new();
    public Func<string, int, Task<System.Net.Sockets.TcpClient>>? Connect;
    public Exception? Throws;

    public Task<System.Net.Sockets.TcpClient> ConnectAsync(string host, int port, bool forceDoh, CancellationToken ct)
    {
        lock (Calls) Calls.Add((host, port, forceDoh));
        if (Throws is { } ex) throw ex;
        return Connect!(host, port);
    }
}

internal sealed class FakeProbe : Rigbay.SteamBypass.Check.IConnectionProbe
{
    public readonly List<(string Id, int? ProxyPort)> Calls = new();
    public readonly HashSet<string> Failing = new();
    public Task? Gate;

    public async Task<Rigbay.SteamBypass.Check.CheckResult> RunAsync(Rigbay.SteamBypass.Check.CheckSpec spec, int? proxyPort, CancellationToken ct)
    {
        lock (Calls) Calls.Add((spec.Id, proxyPort));
        if (Gate is not null) await Gate.ConfigureAwait(false);
        return Failing.Contains(spec.Id)
            ? new Rigbay.SteamBypass.Check.CheckResult(spec.Id, false, Rigbay.SteamBypass.Check.CheckFailure.Reset, null, 5, "reset")
            : new Rigbay.SteamBypass.Check.CheckResult(spec.Id, true, Rigbay.SteamBypass.Check.CheckFailure.None, 200, 42, null);
    }
}

internal sealed class FakeSteam : Rigbay.SteamBypass.ISteamProbe
{
    public List<Rigbay.SteamBypass.SteamProcess> Running = new();
    public bool Connected;
    public IReadOnlyList<Rigbay.SteamBypass.SteamProcess> Processes() => Running;
    public bool AnyConnectedTo(IReadOnlyCollection<int> pids, int port) => Connected;
}

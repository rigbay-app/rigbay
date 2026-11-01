using Rigbay.Abstractions;

namespace Rigbay.Core;

/// <summary>The <see cref="IHostServices"/> of one module: every service stamps the module id and runs callbacks in its fault boundary.</summary>
internal sealed class ModuleHostServices : IHostServices
{
    private readonly RigbayHost _host;
    private readonly ModuleEntry _entry;

    public ModuleHostServices(RigbayHost host, ModuleEntry entry)
    {
        _host = host;
        _entry = entry;
        ModuleDirectory = entry.Folder;
        Settings = new JsonSettingsStore(Path.Combine(DataDirectory, "settings.json"), entry.Log);
        Hotkeys = new HotkeyService(host, entry);
        Tray = new TrayService(host, entry);
        Commands = new CommandService(host, entry);
        Toast = new ToastService(host, entry);
        Metrics = host.Metrics.ForModule(entry.Id, handler => entry.Guard("metrics subscriber", handler), action => entry.Guard("SubscribersChanged handler", action), entry.TrackSubscription);
        Approval = new ApprovalService(host, entry);
    }

    public string ModuleId => _entry.Id;
    public string ModuleDirectory { get; }

    public string DataDirectory
    {
        get
        {
            var dir = _host.Paths.ModuleDataDirectory(_entry.Id);
            try { Directory.CreateDirectory(dir); } catch (Exception) { /* Save will report the real problem */ }
            return dir;
        }
    }

    public Version HostVersion => _host.Options.HostVersion;
    public CancellationToken StopToken => _entry.StopToken;
    public ISettingsStore Settings { get; }
    public ILocalisation Localisation => _entry.Localisation;
    public ILog Log => _entry.Log;
    public IHotkeyService Hotkeys { get; }
    public ITrayService Tray { get; }
    public ICommandRegistry Commands { get; }
    public IToastService Toast { get; }
    public IMetricsBus Metrics { get; }
    public IHardwareApproval Approval { get; }

    public void RequestPageRefresh() => _entry.RequestPageRefresh();

    public IObservableValue<bool> PageVisible => _entry.PageVisibleValue;

    public void RunOnUi(Action action)
    {
        var guarded = _entry.Guard("RunOnUi", action);
        var dispatcher = _host.UiDispatcher;
        if (dispatcher is null) guarded();
        else dispatcher(guarded);
    }

    private sealed class HotkeyService(RigbayHost host, ModuleEntry entry) : IHotkeyService
    {
        public IHotkeyBinding Register(HotkeyRegistration registration)
            => host.Hotkeys.Register(entry.Id, registration.ActionId, registration.NameKey, registration.Default, entry.Guard("hotkey " + registration.ActionId, registration.Invoke));
    }

    private sealed class TrayService(RigbayHost host, ModuleEntry entry) : ITrayService
    {
        public void Add(TrayItem item) => host.Tray.Add(entry.Id, item, entry.Guard("tray item " + item.Id, item.Invoke));

        public void Remove(string id) => host.Tray.Remove(entry.Id, id);
    }

    private sealed class CommandService(RigbayHost host, ModuleEntry entry) : ICommandRegistry
    {
        public void Register(string verb, string descriptionKey, CommandHandler handler)
        {
            host.Commands.Register(entry.Id, verb, descriptionKey, async context =>
            {
                var (completed, result) = await entry.InvokeAsync("command " + verb, () => handler(context)).ConfigureAwait(false);
                if (completed && result is not null) return result;
                var why = entry.Fault is { } f ? ModuleEntry.Describe(f) : "not running";
                return CommandResult.Failed(host.Languages.F("core.command.failed", entry.Id, why));
            });
        }
    }

    private sealed class ToastService(RigbayHost host, ModuleEntry entry) : IToastService
    {
        public void Show(ToastMessage message)
        {
            var sink = host.ToastSink;
            if (sink is null) return;
            try { sink.Show(entry.Id, entry.Localisation.Resolve(message.Title), entry.Localisation.Resolve(message.Body), message.Kind); }
            catch (Exception ex) { entry.Log.Warn("toast sink failed: " + ex.Message); }
        }
    }

    private sealed class ApprovalService(RigbayHost host, ModuleEntry entry) : IHardwareApproval
    {
        public Task<bool> ConfirmAsync(HardwareEffect effect, LocalisedText? detail = null, CancellationToken cancellationToken = default)
            => host.RequestApprovalAsync(entry, effect, detail, cancellationToken);
    }
}

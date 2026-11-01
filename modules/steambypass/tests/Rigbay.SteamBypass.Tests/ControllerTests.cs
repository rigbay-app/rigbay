using Rigbay.SteamBypass.Net;
using Rigbay.SteamBypass.Proxy;

namespace Rigbay.SteamBypass.Tests;

public class ControllerTests
{
    private const string Pac = "http://127.0.0.1:18081/proxy.pac";

    private sealed class Rig
    {
        public readonly MemoryProxyStore Store = new();
        public readonly MemoryStateStore State = new();
        public readonly FakeListenerFactory Listeners = new();
        public readonly List<string> Log = new();
        public bool OtherInstance;
        public readonly ProxyEngine Engine;
        public readonly SystemProxyManager Manager;
        public readonly BypassController Controller;
        public DateTimeOffset Now = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

        public Rig(string? currentPac = null)
        {
            Store.Value = currentPac;
            Engine = new ProxyEngine(DomainSet.Default(), new FakeDialer(), Listeners, new ProxyOptions(), log: Log.Add);
            Manager = new SystemProxyManager(Store, State, Pac);
            Controller = new BypassController(Engine, Manager, Log.Add, () => Now, () => OtherInstance);
        }
    }

    [Fact]
    public void Turning_on_listens_first_and_only_then_writes_the_windows_setting()
    {
        var r = new Rig("http://old.example/p.pac");
        var result = r.Controller.TurnOn();
        Assert.True(result.Ok);
        Assert.Equal(new[] { 18081, 80 }, r.Listeners.Requested);
        Assert.Equal(Pac, r.Store.Value);
        Assert.Equal("http://old.example/p.pac", r.State.State!.Previous);
        Assert.True(r.Controller.IsOn);
        Assert.Equal(r.Now, r.Controller.OnSince);
        r.Engine.Stop();
    }

    [Fact]
    public void The_pac_address_is_never_written_before_the_listener_exists()
    {
        // If the setting were written first, browsers would be sent to a port nothing listens on for a moment.
        var order = new List<string>();
        var store = new OrderStore(order);
        var listeners = new OrderListeners(order);
        var engine = new ProxyEngine(DomainSet.Default(), new FakeDialer(), listeners);
        var controller = new BypassController(engine, new SystemProxyManager(store, new MemoryStateStore(), Pac), _ => { }, () => DateTimeOffset.Now, () => false);
        controller.TurnOn();
        Assert.Equal(new[] { "listen 18081", "listen 80", "write" }, order);
        order.Clear();
        controller.TurnOff();
        Assert.Equal(new[] { "delete", "close 18081", "close 80" }, order);   // restore first, then stop the proxy
    }

    [Fact]
    public void Port_80_busy_still_turns_on_and_says_downloads_are_unavailable()
    {
        var r = new Rig();
        r.Listeners.Taken.Add(80);
        var result = r.Controller.TurnOn();
        Assert.True(result.Ok);
        Assert.False(result.DownloadsAvailable);
        Assert.NotNull(r.Engine.DownloadsError);
        Assert.Equal(Pac, r.Store.Value);
        Assert.Contains(r.Log, l => l.Contains("127.0.0.1:80"));
    }

    [Fact]
    public void The_proxy_port_busy_fails_cleanly_and_the_windows_setting_is_never_touched()
    {
        var r = new Rig("http://old.example/p.pac");
        r.Listeners.Taken.Add(18081);
        var result = r.Controller.TurnOn();
        Assert.False(result.Ok);
        Assert.Equal(StartFailure.AddressInUse, result.Failure);
        Assert.Equal("http://old.example/p.pac", r.Store.Value);
        Assert.Equal(0, r.Store.Writes);
        Assert.Null(r.State.State);
        Assert.False(r.Controller.IsOn);
        Assert.NotNull(r.Controller.LastError);
    }

    [Fact]
    public void A_registry_write_that_fails_stops_the_proxy_again_and_leaves_nothing_behind()
    {
        var r = new Rig("http://old.example/p.pac");
        r.Store.WriteThrows = new UnauthorizedAccessException("denied");
        var result = r.Controller.TurnOn();
        Assert.False(result.Ok);
        Assert.Equal(StartFailure.SettingFailed, result.Failure);
        Assert.False(r.Controller.IsOn);
        Assert.False(r.Engine.IsRunning);
        Assert.Equal(2, r.Listeners.Disposed);   // both listeners closed
        Assert.Equal("http://old.example/p.pac", r.Store.Value);
        Assert.Null(r.State.State);
    }

    [Fact]
    public void Turning_on_twice_is_one_start()
    {
        var r = new Rig();
        r.Controller.TurnOn();
        r.Controller.TurnOn();
        Assert.Equal(new[] { 18081, 80 }, r.Listeners.Requested);
        Assert.Single(r.Store.Journal);
    }

    [Fact]
    public void Turning_off_restores_the_previous_value_and_stops_the_proxy()
    {
        var r = new Rig("http://old.example/p.pac");
        r.Controller.TurnOn();
        var result = r.Controller.TurnOff();
        Assert.True(result.Ok);
        Assert.Equal(RestoreOutcome.Restored, result.Outcome);
        Assert.Equal("http://old.example/p.pac", r.Store.Value);
        Assert.False(r.Engine.IsRunning);
        Assert.False(r.Controller.IsOn);
        Assert.Null(r.Controller.OnSince);
        Assert.Null(r.State.State);
    }

    [Fact]
    public void Turning_off_when_it_is_off_changes_nothing()
    {
        var r = new Rig("http://old.example/p.pac");
        var result = r.Controller.TurnOff();
        Assert.True(result.Ok);
        Assert.Equal(RestoreOutcome.NothingToRestore, result.Outcome);
        Assert.Equal(0, r.Store.Writes);
        Assert.Equal("http://old.example/p.pac", r.Store.Value);
    }

    [Fact]
    public void A_failed_restore_is_reported_stops_the_proxy_and_is_retried_by_the_next_off()
    {
        var r = new Rig("http://old.example/p.pac");
        r.Controller.TurnOn();
        r.Store.WriteThrows = new IOException("busy");
        var failed = r.Controller.TurnOff();
        Assert.False(failed.Ok);
        Assert.False(r.Controller.IsOn);
        Assert.False(r.Engine.IsRunning);          // nothing keeps running for a bypass that is off
        Assert.Equal(Pac, r.Store.Value);          // ... but Windows still points at us, and the saved value is kept
        Assert.NotNull(r.State.State);
        Assert.Equal("busy", r.Controller.LastError);

        r.Store.WriteThrows = null;
        var retry = r.Controller.TurnOff();        // already off, but it still looks at the setting
        Assert.Equal(RestoreOutcome.Restored, retry.Outcome);
        Assert.Equal("http://old.example/p.pac", r.Store.Value);
        Assert.Null(r.Controller.LastError);
    }

    [Fact]
    public void Changed_is_raised_after_on_and_off()
    {
        var r = new Rig();
        int changes = 0;
        r.Controller.Changed += () => changes++;
        r.Controller.TurnOn();
        r.Controller.TurnOff();
        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_subscriber_that_throws_does_not_undo_the_change()
    {
        var r = new Rig();
        r.Controller.Changed += () => throw new InvalidOperationException("boom");
        Assert.True(r.Controller.TurnOn().Ok);
        Assert.True(r.Controller.IsOn);
        Assert.True(r.Controller.TurnOff().Ok);
    }

    // ---- crash cleanup at start -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_crash_that_left_our_address_is_cleaned_up_at_start()
    {
        var r = new Rig(Pac);                                           // Windows still points at us ...
        r.State.State = new ProxyState(Pac, "http://old.example/p.pac"); // ... and the dead run saved what was there before
        var result = r.Controller.RecoverStale();
        Assert.Equal(RestoreOutcome.Restored, result.Outcome);
        Assert.Equal("http://old.example/p.pac", r.Store.Value);
        Assert.Null(r.State.State);
        Assert.Empty(r.Listeners.Requested);   // cleaning up opens no port
    }

    [Fact]
    public void Another_program_answering_on_our_port_is_left_alone()
    {
        // A running standalone Steam Bypass owns that address: restoring it would switch its bypass off behind its back.
        var r = new Rig(Pac) { OtherInstance = true };
        var result = r.Controller.RecoverStale();
        Assert.Equal(RestoreOutcome.NothingToRestore, result.Outcome);
        Assert.Equal(Pac, r.Store.Value);
        Assert.Equal(0, r.Store.Writes);
    }

    [Fact]
    public void A_clean_start_does_not_touch_the_setting_or_ask_the_network()
    {
        var r = new Rig("http://corp.example/wpad.pac");
        bool asked = false;
        var controller = new BypassController(r.Engine, r.Manager, _ => { }, () => r.Now, () => { asked = true; return true; });
        var result = controller.RecoverStale();
        Assert.Equal(RestoreOutcome.NothingToRestore, result.Outcome);
        Assert.False(asked);                         // only asked when the value is ours
        Assert.Equal(0, r.Store.Writes);
        Assert.Equal("http://corp.example/wpad.pac", r.Store.Value);
    }

    [Fact]
    public void A_stale_state_file_with_a_value_that_is_not_ours_is_just_dropped()
    {
        var r = new Rig("http://user-changed.example/p.pac");
        r.State.State = new ProxyState(Pac, "http://old.example/p.pac");
        r.Controller.RecoverStale();
        Assert.Equal("http://user-changed.example/p.pac", r.Store.Value);
        Assert.Null(r.State.State);
    }

    [Fact]
    public void Recovery_that_cannot_write_is_logged_and_does_not_throw()
    {
        var r = new Rig(Pac);
        r.State.State = new ProxyState(Pac, null);
        r.Store.DeleteThrows = new IOException("locked");
        var result = r.Controller.RecoverStale();
        Assert.Equal(RestoreOutcome.Failed, result.Outcome);
        Assert.Contains(r.Log, l => l.Contains("locked"));
    }

    [Fact]
    public void An_address_set_by_another_program_is_never_touched_by_any_way_off_or_by_exit()
    {
        // The standalone Steam Bypass app is running and owns the address: no state of this module exists, and nothing may undo it.
        var r = new Rig(Pac) { OtherInstance = false };
        Assert.Equal(RestoreOutcome.NothingToRestore, r.Controller.RecoverStale().Outcome);   // even with nobody answering, it has no state of ours
        Assert.Equal(RestoreOutcome.NothingToRestore, r.Controller.TurnOff().Outcome);
        r.Controller.RestoreOnExit();
        Assert.Equal(Pac, r.Store.Value);
        Assert.Equal(0, r.Store.Writes);
    }

    // ---- process exit ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_process_exit_path_restores_without_events_and_never_throws()
    {
        var r = new Rig("http://old.example/p.pac");
        r.Controller.TurnOn();
        int changes = 0;
        r.Controller.Changed += () => changes++;
        r.Controller.RestoreOnExit();
        Assert.Equal("http://old.example/p.pac", r.Store.Value);
        Assert.Equal(0, changes);

        var broken = new Rig(Pac);
        broken.Store.ReadThrows = new IOException("gone");
        broken.Controller.RestoreOnExit();   // does not throw
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------

    private sealed class OrderStore(List<string> order) : ISystemProxyStore
    {
        public string? Value;
        public string? ReadPacUrl() => Value;
        public void WritePacUrl(string url) { Value = url; order.Add("write"); }
        public void DeletePacUrl() { Value = null; order.Add("delete"); }
        public void NotifyChanged() { }
    }

    private sealed class OrderListeners(List<string> order) : ILoopbackListenerFactory
    {
        public ILoopbackListener Listen(int port)
        {
            order.Add("listen " + port);
            return new L(port, order);
        }

        private sealed class L(int port, List<string> order) : ILoopbackListener
        {
            private readonly TaskCompletionSource<System.Net.Sockets.TcpClient> _never = new();
            public System.Net.IPEndPoint LocalEndPoint { get; } = new(System.Net.IPAddress.Loopback, port);
            public Task<System.Net.Sockets.TcpClient> AcceptAsync(CancellationToken ct) => _never.Task;
            public void Dispose() { order.Add("close " + port); _never.TrySetCanceled(); }
        }
    }
}

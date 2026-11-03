using System.Collections.Concurrent;
using Rigbay.SteamBypass.Net;
using Rigbay.SteamBypass.Proxy;

namespace Rigbay.SteamBypass.Tests;

/// <summary>
/// The same rule as the switch (a flag or a state is cleared only by the one who set it) in the other places that have one, found by
/// reading the proxy engine, the controller and the connection check for it (task S2).
/// </summary>
public class OwnershipGuardTests
{
    private const string Corp = "http://corp.example/wpad.pac";

    private static (BypassController Controller, MemoryProxyStore Store, MemoryStateStore State, FakeListenerFactory Listeners) NewController()
    {
        var store = new MemoryProxyStore { Value = Corp };
        var state = new MemoryStateStore();
        var listeners = new FakeListenerFactory();
        var engine = new ProxyEngine(DomainSet.Default(), new FakeDialer(), listeners, new ProxyOptions(), log: _ => { });
        var controller = new BypassController(engine, new SystemProxyManager(store, state, ModuleRig.Pac), _ => { }, () => DateTimeOffset.UnixEpoch, () => false);
        return (controller, store, state, listeners);
    }

    // ---- the controller: closed by the shutdown, nothing turns on afterwards --------------------------------------------------

    [Fact]
    public void A_closed_controller_refuses_to_turn_on_and_writes_nothing()
    {
        var (controller, store, state, listeners) = NewController();

        Assert.True(controller.TurnOffAndClose().Ok);
        var on = controller.TurnOn();

        Assert.False(on.Ok);
        Assert.Equal(StartFailure.Closed, on.Failure);
        Assert.False(controller.IsOn);
        Assert.Empty(listeners.Requested);
        Assert.Equal(0, store.Writes);
        Assert.Equal(Corp, store.Value);
        Assert.Null(state.State);
    }

    [Fact]
    public void Closing_a_controller_that_is_on_restores_first_and_then_refuses_a_new_turn_on()
    {
        var (controller, store, _, listeners) = NewController();
        Assert.True(controller.TurnOn().Ok);

        Assert.True(controller.TurnOffAndClose().Ok);
        Assert.Equal(Corp, store.Value);
        Assert.Equal(2, listeners.Disposed);
        Assert.False(controller.TurnOn().Ok);
        Assert.Equal(Corp, store.Value);
        Assert.Equal(2, listeners.Requested.Count);
    }

    // ---- the connection check: its "running" flag --------------------------------------------------------------------------

    [Fact]
    public async Task Refused_runs_of_the_check_do_not_clear_the_flag_of_the_run_that_is_going()
    {
        using var rig = new ModuleRig();
        var gate = new TaskCompletionSource();
        rig.Probe.Gate = gate.Task;
        var first = rig.Run("test");
        Assert.True(Wait.Until(() => rig.Probe.Calls.Count == 4));

        for (int i = 0; i < 3; i++)
        {
            var again = await rig.Run("test");
            Assert.Equal(1, again.ExitCode);
            Assert.Equal("A test is already running", again.Output);        // still the first one's: no refused run released it
        }
        Assert.Equal(4, rig.Probe.Calls.Count);

        gate.SetResult();
        Assert.Equal(0, (await first).ExitCode);
        Assert.Equal(0, (await rig.Run("test")).ExitCode);                  // and once it is over, the next run is accepted
    }

    [Fact]
    public async Task A_cancelled_check_gives_the_flag_back_and_the_rows_go_back_to_idle()
    {
        using var rig = new ModuleRig();
        rig.Probe.Gate = new TaskCompletionSource().Task;                   // never completes: only the cancel ends it
        using var cts = new CancellationTokenSource();
        var run = rig.Module.RunChecksAsync(cts.Token);
        Assert.True(Wait.Until(() => rig.Probe.Calls.Count == 4));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(rig.Host.Loc.T("check.idle"), rig.ValueOf("check.store.title"));
        rig.Probe.Gate = null;
        var next = await rig.Module.RunChecksAsync(CancellationToken.None);
        Assert.Equal(4, next.Results.Count);                                // accepted, not "a test is already running"
    }

    [Fact]
    public async Task A_check_that_fails_while_it_is_being_set_up_does_not_leave_the_flag_set()
    {
        using var rig = new ModuleRig();
        rig.Host.Loc.ThrowFor = key => key == "check.running";
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Module.RunChecksAsync(CancellationToken.None));

        rig.Host.Loc.ThrowFor = null;
        var next = await rig.Module.RunChecksAsync(CancellationToken.None);
        Assert.Equal(4, next.Results.Count);                                // it was not stuck on "already running" for ever
    }

    // ---- the proxy engine: the token of an accept loop ------------------------------------------------------------------------

    [Fact]
    public void Start_and_Stop_in_a_tight_loop_never_leave_an_accept_loop_that_read_the_field_after_it_was_cleared()
    {
        // The accept loop used to read the engine's cancellation source from a pool thread, after Start had returned. A quick Stop sets it to
        // null (a NullReferenceException inside a task nobody awaits); Stop then Start gives the old loop the new run's token.
        var faults = new ConcurrentBag<Exception>();
        void Collect(object? _, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.InnerExceptions)
                if (inner.StackTrace?.Contains(nameof(ProxyEngine), StringComparison.Ordinal) == true) faults.Add(inner);
        }
        TaskScheduler.UnobservedTaskException += Collect;
        try
        {
            var listeners = new FakeListenerFactory();
            using var engine = new ProxyEngine(DomainSet.Default(), new FakeDialer(), listeners, new ProxyOptions(), log: _ => { });
            for (int i = 0; i < 2000; i++)
            {
                engine.Start();
                engine.Stop();
            }
            Assert.Equal(listeners.Requested.Count, listeners.Disposed);
            Assert.False(engine.IsRunning);

            Thread.Sleep(200);                    // let the pool threads that were queued run their lambdas
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Empty(faults);
        }
        finally { TaskScheduler.UnobservedTaskException -= Collect; }
    }
}

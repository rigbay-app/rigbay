using Rigbay.Abstractions;

namespace Rigbay.SteamBypass.Tests;

/// <summary>
/// A hold-and-release point for a fake: the code under test calls <see cref="Block"/> on its own thread (inside a registry write, say),
/// and the test waits for <see cref="WaitEntered"/>, does what it wants while that thread is stuck, then calls <see cref="Release"/>.
/// No sleep decides the order of anything.
/// </summary>
internal sealed class Hold
{
    private readonly ManualResetEventSlim _entered = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private int _arrivals;

    /// <summary>How many times a thread has reached the hold point.</summary>
    public int Arrivals => Volatile.Read(ref _arrivals);

    /// <summary>Called by the code under test: signals the arrival and waits to be released (30 s at most, so a broken test cannot hang the run).</summary>
    public void Block()
    {
        Interlocked.Increment(ref _arrivals);
        _entered.Set();
        _release.Wait(TimeSpan.FromSeconds(30));
    }

    /// <summary>Waits until the code under test is stuck at the hold point.</summary>
    public void WaitEntered() => Assert.True(_entered.Wait(TimeSpan.FromSeconds(30)), "the code under test never reached the hold point");

    /// <summary>Lets every blocked thread (now and later) go on.</summary>
    public void Release() => _release.Set();
}

/// <summary>
/// One change at a time (task S2). The old worker loop released its "I am working" flag in a <c>finally</c> block even when another
/// worker had taken it in the meantime (found by FL1). The rule now: a change owns a ticket; only the owner releases it; a second request
/// while a change runs is refused ("A change is already in progress"), whoever it comes from (the switch, a verb, auto-on), and the
/// switch goes back to what is in progress or real. Every test ends with the same consistency check.
/// </summary>
public class SwitchRaceTests
{
    private const string Corp = "http://corp.example/wpad.pac";

    private static bool Refused(ModuleRig rig) => rig.Host.ToastService.Shown.Any(t =>
        t.Kind == ToastKind.Warning && rig.Host.Loc.Resolve(t.Body).StartsWith("A change is already in progress", StringComparison.Ordinal));

    /// <summary>
    /// Waits for every background step, then checks that the module, the Windows setting, the listeners and the switch tell one story.
    /// </summary>
    private static void AssertConsistent(ModuleRig rig, string original, bool? expectedOn = null)
    {
        rig.WaitUntilSwitchWorkersAreDone();
        Assert.False(rig.Module.ChangeInProgress, "the change slot was left taken");
        bool on = rig.IsOn;
        if (expectedOn is { } e) Assert.Equal(e, on);
        int alive = rig.Listeners.Requested.Count - rig.Listeners.Disposed;
        Assert.Equal(on ? 2 : 0, alive);
        Assert.Equal(on, rig.Switch.Value.Value);
        if (on)
        {
            Assert.Equal(ModuleRig.Pac, rig.Proxy.Value);
            Assert.Equal(original, rig.State.State!.Previous);
        }
        else
        {
            Assert.Equal(original, rig.Proxy.Value);
            Assert.Null(rig.State.State);
        }
    }

    // ---- the primitive: only the owner releases ----------------------------------------------------------------------------

    [Fact]
    public void Only_the_owner_of_a_change_can_release_it()
    {
        using var rig = new ModuleRig();
        var first = rig.Module.TryBeginChange(wanted: true, out _);
        Assert.NotNull(first);

        var refused = rig.Module.TryBeginChange(wanted: false, out bool inProgressWanted);
        Assert.Null(refused);
        Assert.True(inProgressWanted);                       // it says what is in progress

        rig.Module.EndChange(first);
        Assert.False(rig.Module.ChangeInProgress);

        var second = rig.Module.TryBeginChange(wanted: false, out _);
        Assert.NotNull(second);

        // The first owner is late and releases again (the FL1 finding: a worker clearing the flag another worker had taken).
        rig.Module.EndChange(first);
        Assert.True(rig.Module.ChangeInProgress);
        Assert.Null(rig.Module.TryBeginChange(wanted: true, out _));

        rig.Module.EndChange(second);
        Assert.False(rig.Module.ChangeInProgress);
    }

    // ---- off during on, on during off ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Off_during_on__the_second_flip_is_refused_at_once_and_the_first_change_finishes_alone()
    {
        using var rig = new ModuleRig(arrange: r => r.Proxy.Value = Corp);
        var hold = new Hold();
        rig.Proxy.BeforeWrite = _ => hold.Block();

        rig.Switch.Value.Value = true;               // the first change: its worker is going to write the Windows setting
        hold.WaitEntered();
        Assert.True(rig.Module.ChangeInProgress);

        // The second request arrives while the first is stuck in the middle of its write. It must come back at once (the caller can be the UI thread).
        var flip = Task.Run(() => rig.Switch.Value.Value = false);
        await flip.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(Wait.Until(() => Refused(rig)), "the refusal was not shown");
        Assert.True(Wait.Until(() => rig.Switch.Value.Value), "the switch must go back to what is in progress");

        hold.Release();
        AssertConsistent(rig, Corp, expectedOn: true);
        Assert.Equal(1, hold.Arrivals);                                   // one change touched the setting
        Assert.Equal(new[] { "write " + ModuleRig.Pac }, rig.Proxy.Journal);
        Assert.Equal(new[] { 18081, 80 }, rig.Listeners.Requested);      // listened once
    }

    [Fact]
    public async Task On_during_off__the_second_flip_is_refused_and_the_setting_ends_restored()
    {
        using var rig = new ModuleRig(arrange: r => r.Proxy.Value = Corp);
        rig.FlipSwitch(true);
        var hold = new Hold();
        rig.Proxy.BeforeWrite = _ => hold.Block();          // the restore writes the previous value back

        rig.Switch.Value.Value = false;
        hold.WaitEntered();

        await Task.Run(() => rig.Switch.Value.Value = true).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Wait.Until(() => Refused(rig)));
        Assert.True(Wait.Until(() => !rig.Switch.Value.Value), "the switch must stay at what is in progress: off");

        hold.Release();
        AssertConsistent(rig, Corp, expectedOn: false);
        Assert.Equal(1, hold.Arrivals);
    }

    // ---- concurrent on and off ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Many_threads_flipping_on_and_off_at_once_end_in_one_consistent_state_with_no_stuck_slot()
    {
        using var rig = new ModuleRig(arrange: r => r.Proxy.Value = Corp);
        rig.Proxy.BeforeWrite = _ => Thread.SpinWait(2000);   // widens the windows between the steps of a change
        rig.Proxy.BeforeDelete = () => Thread.SpinWait(2000);

        var tasks = Enumerable.Range(0, 6).Select(t => Task.Run(() =>
        {
            var rnd = new Random(1000 + t);
            for (int i = 0; i < 200; i++)
            {
                rig.Switch.Value.Value = (i + t) % 2 == 0;
                if (rnd.Next(4) == 0) Thread.Yield();
            }
        })).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));

        AssertConsistent(rig, Corp);
        Assert.DoesNotContain(rig.Host.LogSink.Entries, e => e.Level == LogLevel.Error);
        // Every Listen pair that was opened was closed again except when the bypass is on.
        Assert.Equal(0, rig.Listeners.Requested.Count % 2);
    }

    // ---- verbs, auto-on and the switch share the one slot -------------------------------------------------------------------

    [Fact]
    public async Task A_verb_while_a_switch_change_runs_is_refused_and_does_not_even_ask()
    {
        using var rig = new ModuleRig(approve: true, arrange: r => r.Proxy.Value = Corp);
        var hold = new Hold();
        rig.Proxy.BeforeWrite = _ => hold.Block();
        rig.Switch.Value.Value = true;
        hold.WaitEntered();

        var result = await rig.Run("off").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("A change is already in progress. Nothing was changed.", result.Output);
        Assert.Empty(rig.Host.ApprovalService.Asked);

        hold.Release();
        AssertConsistent(rig, Corp, expectedOn: true);
    }

    [Fact]
    public async Task A_switch_flip_while_a_verb_runs_is_refused_and_the_verb_wins()
    {
        using var rig = new ModuleRig(approve: true, arrange: r => r.Proxy.Value = Corp);
        var hold = new Hold();
        rig.Proxy.BeforeWrite = _ => hold.Block();
        var verb = rig.Run("on");
        hold.WaitEntered();
        Assert.True(rig.Module.ChangeInProgress);

        await Task.Run(() => rig.Switch.Value.Value = true).WaitAsync(TimeSpan.FromSeconds(10));   // the same direction as the verb: nothing new
        Assert.False(Refused(rig));
        await Task.Run(() => rig.Switch.Value.Value = false).WaitAsync(TimeSpan.FromSeconds(10));  // the opposite: refused
        Assert.True(Wait.Until(() => Refused(rig)));

        hold.Release();
        Assert.Equal(0, (await verb).ExitCode);
        AssertConsistent(rig, Corp, expectedOn: true);
        Assert.Equal(new[] { "write " + ModuleRig.Pac }, rig.Proxy.Journal);
    }

    [Fact]
    public void A_flip_while_auto_on_runs_starts_no_second_change_and_auto_on_ends_with_the_switch_on()
    {
        using var rig = new ModuleRig(initialize: false, arrange: r =>
        {
            r.Host.SettingsStore.Stored = new SteamBypassSettings { AutoOn = true };
            r.Proxy.Value = Corp;
        });
        var hold = new Hold();
        rig.Proxy.BeforeWrite = _ => hold.Block();
        rig.Module.Initialize(rig.Host);
        hold.WaitEntered();
        Assert.True(rig.Module.ChangeInProgress);                  // claimed before Initialize returned
        Assert.False(rig.Switch.Value.Value);                      // the switch shows On only when it is real

        rig.Switch.Value.Value = true;                             // the user flips to what auto-on is already doing: nothing new (the handler ran on this thread)
        Assert.False(Refused(rig));
        Assert.Equal(1, hold.Arrivals);

        hold.Release();
        AssertConsistent(rig, Corp, expectedOn: true);
        Assert.Equal(new[] { "write " + ModuleRig.Pac }, rig.Proxy.Journal);
    }

    [Fact]
    public async Task A_verb_approved_after_the_state_moved_on_changes_nothing_more()
    {
        using var rig = new ModuleRig(approve: true, arrange: r => r.Proxy.Value = Corp);
        var dialog = new TaskCompletionSource();
        rig.Host.ApprovalService.Gate = dialog.Task;                // the user is still looking at the dialog
        var verb = rig.Run("on");
        Assert.True(Wait.Until(() => rig.Host.ApprovalService.Asked.Count == 1));

        rig.FlipSwitch(true);                                       // meanwhile the switch turned it on
        dialog.SetResult();

        var result = await verb;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("The bypass is already on.", result.Output);
        AssertConsistent(rig, Corp, expectedOn: true);
        Assert.Equal(new[] { "write " + ModuleRig.Pac }, rig.Proxy.Journal);
    }

    // ---- cancel during on ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_during_on__the_change_that_started_finishes_whole_and_the_slot_is_given_back()
    {
        using var rig = new ModuleRig(approve: true, arrange: r => r.Proxy.Value = Corp);
        var hold = new Hold();
        rig.Proxy.BeforeWrite = _ => hold.Block();
        using var cts = new CancellationTokenSource();
        var verb = rig.Host.CommandRegistry.Verbs["on"].Handler(new CommandContext(Array.Empty<string>(), cts.Token));
        hold.WaitEntered();

        cts.Cancel();                                               // the caller gives up while the write is in the middle
        hold.Release();
        try { await verb; } catch (OperationCanceledException) { /* either ending is fine; the state is what counts */ }

        // Whole or not at all, never half: the setting, the listeners and the switch agree, and the slot is free.
        AssertConsistent(rig, Corp);
        Assert.True(rig.IsOn);                                      // a started turn-on is not abandoned half way
        var later = await rig.Run("off");                           // and the next change is accepted
        Assert.Equal(0, later.ExitCode);
        AssertConsistent(rig, Corp, expectedOn: false);
    }

    [Fact]
    public async Task Cancel_before_the_work_starts_changes_nothing_and_gives_the_slot_back()
    {
        using var rig = new ModuleRig(approve: true, arrange: r => r.Proxy.Value = Corp);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Host.CommandRegistry.Verbs["on"].Handler(new CommandContext(Array.Empty<string>(), cts.Token)));
        Assert.Equal(0, rig.Proxy.Writes);
        Assert.Empty(rig.Listeners.Requested);
        AssertConsistent(rig, Corp, expectedOn: false);
        Assert.Equal(0, (await rig.Run("on")).ExitCode);
        AssertConsistent(rig, Corp, expectedOn: true);
    }

    // ---- an exception in the worker -----------------------------------------------------------------------------------------

    [Fact]
    public void An_exception_in_the_worker_gives_the_slot_back_shows_the_real_state_and_the_next_change_works()
    {
        using var rig = new ModuleRig(arrange: r => r.Proxy.Value = Corp);
        rig.Proxy.WriteThrows = new UnauthorizedAccessException("denied by policy");
        rig.Host.ToastService.Throws = new InvalidOperationException("the host failed to show the toast");   // the failure report itself throws

        rig.Switch.Value.Value = true;
        AssertConsistent(rig, Corp, expectedOn: false);
        Assert.Contains(rig.Host.LogSink.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Switching the bypass failed"));
        Assert.Contains("Something went wrong", rig.ValueOf("row.problem.header"));

        rig.Proxy.WriteThrows = null;
        rig.Host.ToastService.Throws = null;
        rig.FlipSwitch(true);
        AssertConsistent(rig, Corp, expectedOn: true);
    }

    // ---- module stop during a switch ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Stop_during_on__the_setting_is_restored_exactly_and_nothing_is_published_afterwards()
    {
        using var rig = new ModuleRig(arrange: r => r.Proxy.Value = Corp);
        var hold = new Hold();
        rig.Proxy.BeforeWrite = _ => hold.Block();
        rig.Switch.Value.Value = true;
        hold.WaitEntered();

        var stop = Task.Run(rig.Module.Shutdown);                   // waits for the change that is in the middle of its write
        Assert.NotSame(stop, await Task.WhenAny(stop, Task.Delay(200)));   // Shutdown waits for the controller, it does not run past a change in the middle of its write
        hold.Release();
        await stop.WaitAsync(TimeSpan.FromSeconds(30));
        rig.WaitUntilSwitchWorkersAreDone();

        Assert.Equal(Corp, rig.Proxy.Value);                        // the previous value, exactly
        Assert.Null(rig.State.State);
        Assert.Equal(2, rig.Listeners.Disposed);
        Assert.Equal(rig.Listeners.Requested.Count, rig.Listeners.Disposed);
        Assert.False(rig.Module.ChangeInProgress);
        Assert.Empty(rig.Host.MetricsBus.Snapshot());               // a worker that ended late did not bring the readings back
        Assert.Null(rig.Host.MetricsBus.Latest(SteamBypassModule.ActiveMetric));
    }

    [Fact]
    public async Task Stop_during_off__the_setting_ends_restored_and_stays_so()
    {
        using var rig = new ModuleRig(arrange: r => r.Proxy.Value = Corp);
        rig.FlipSwitch(true);
        var hold = new Hold();
        rig.Proxy.BeforeWrite = _ => hold.Block();
        rig.Switch.Value.Value = false;
        hold.WaitEntered();

        var stop = Task.Run(rig.Module.Shutdown);
        hold.Release();
        await stop.WaitAsync(TimeSpan.FromSeconds(30));
        rig.WaitUntilSwitchWorkersAreDone();

        Assert.Equal(Corp, rig.Proxy.Value);
        Assert.Null(rig.State.State);
        Assert.Equal(rig.Listeners.Requested.Count, rig.Listeners.Disposed);
    }

    [Fact]
    public async Task Stop_during_a_change_that_fails_leaves_a_value_the_module_did_not_set_alone()
    {
        // Another program (the standalone Steam Bypass) set the very same address and owns the port; the module's saved state does not exist.
        using var rig = new ModuleRig(arrange: r =>
        {
            r.Proxy.Value = ModuleRig.Pac;
            r.Listeners.Taken.Add(18081);
            r.PortAnswers = true;
        });
        var hold = new Hold();
        rig.Listeners.BeforeListen = port => { if (port == 18081) hold.Block(); };
        rig.Switch.Value.Value = true;
        hold.WaitEntered();

        var stop = Task.Run(rig.Module.Shutdown);
        hold.Release();
        await stop.WaitAsync(TimeSpan.FromSeconds(30));
        rig.WaitUntilSwitchWorkersAreDone();

        Assert.Equal(ModuleRig.Pac, rig.Proxy.Value);               // not ours to undo
        Assert.Equal(0, rig.Proxy.Writes);
        Assert.Null(rig.State.State);
        Assert.Equal(0, rig.Listeners.Disposed);
    }

    [Fact]
    public async Task A_switch_worker_that_starts_after_the_stop_cannot_turn_the_bypass_on()
    {
        using var rig = new ModuleRig(arrange: r => r.Proxy.Value = Corp);
        var hold = new Hold();
        rig.Listeners.BeforeListen = _ => hold.Block();             // the worker is inside TurnOn, before anything is written
        rig.Switch.Value.Value = true;
        hold.WaitEntered();

        var stop = Task.Run(rig.Module.Shutdown);
        hold.Release();
        await stop.WaitAsync(TimeSpan.FromSeconds(30));
        rig.WaitUntilSwitchWorkersAreDone();

        Assert.Equal(Corp, rig.Proxy.Value);
        Assert.Null(rig.State.State);
        Assert.Equal(rig.Listeners.Requested.Count, rig.Listeners.Disposed);
    }

    // ---- the switch worker count ----------------------------------------------------------------------------------------------

    [Fact]
    public void Flipping_quickly_on_off_on_ends_with_a_switch_and_a_windows_setting_that_agree()
    {
        // With one change at a time the middle flip may be refused (the first change was still running) or accepted (it had finished):
        // both are right. What must hold is that the end state is one state.
        using var rig = new ModuleRig(arrange: r => r.Proxy.Value = Corp);
        rig.Switch.Value.Value = true;
        rig.Switch.Value.Value = false;
        rig.Switch.Value.Value = true;
        AssertConsistent(rig, Corp);
        rig.FlipSwitch(false);
        AssertConsistent(rig, Corp, expectedOn: false);
    }
}

using Rigbay.Abstractions;
using Rigbay.Module.Xtu;
using Xunit;

namespace Rigbay.Module.Xtu.Tests;

public class ClientSequenceTests
{
    private static XtuProfile Target(decimal pl1 = 45, decimal pl2 = 65, bool enable = true, decimal window = 28) =>
        new("target", pl1, pl2, enable, window, ProfileKind.Custom);

    private static (XtuClient client, FakeTuningPort tuning) Make(FakeTuningPort? tuning = null)
    {
        tuning ??= new FakeTuningPort();
        return (new XtuClient(tuning, new FakeMonitorPort(), _ => null), tuning);
    }

    [Fact]
    public void Apply_runs_the_proven_sequence_in_order()
    {
        var (client, tuning) = Make();

        var outcome = client.Apply(Target());

        Assert.False(outcome.AlreadyActive);
        Assert.Equal(new[]
        {
            "Connect", "Refresh",
            "GetControl(48)", "GetControl(47)", "GetControl(49)", "GetControl(66)", "GetRestoreOnBoot",   // already-in-use check
            "GetControl(48)", "GetControl(47)",                                                           // locked / supported checks
            "Discard",
            "Propose[49=1,47=65,48=45,66=28]",                                                            // enable, PL2, PL1, window
            "ApplyPending",
            "Refresh",
            "GetControl(48)", "GetControl(47)", "GetControl(49)", "GetControl(66)", "GetRestoreOnBoot",   // read-back
        }, tuning.Calls);
    }

    [Fact]
    public void Read_back_is_what_the_hardware_reports_not_what_was_asked()
    {
        var tuning = new FakeTuningPort();
        tuning.OnApply = () =>
        {
            // the firmware clamps PL2 to 62
            tuning.LastProposals = tuning.LastProposals.Select(p => p.Id == XtuIds.Pl2 ? p with { Value = 62 } : p).ToList();
            return new PortResult(true, "Success");
        };
        var (client, _) = Make(tuning);

        var outcome = client.Apply(Target());

        Assert.Equal(62m, outcome.After.Pl2);
        Assert.Equal(45m, outcome.After.Pl1);
    }

    [Fact]
    public void Only_the_four_allowed_ids_are_ever_proposed()
    {
        var (client, tuning) = Make();
        client.Apply(Target(enable: false));
        Assert.All(tuning.LastProposals, p => Assert.True(XtuIds.IsAllowed(p.Id)));
        Assert.Equal(new uint[] { 49, 47, 48, 66 }, tuning.LastProposals.Select(p => p.Id));
        Assert.Equal(0m, tuning.LastProposals[0].Value);   // PL2 enable off
    }

    [Fact]
    public void Values_already_active_write_nothing()
    {
        var (client, tuning) = Make(new FakeTuningPort(45, 65, true, 28));

        var outcome = client.Apply(Target());

        Assert.True(outcome.AlreadyActive);
        Assert.False(tuning.WroteAnything);
    }

    [Theory]
    [InlineData(10, 60, 28)]
    [InlineData(45, 40, 28)]
    [InlineData(45, 65, 500)]
    public void Invalid_values_never_reach_the_service(decimal pl1, decimal pl2, decimal window)
    {
        var (client, tuning) = Make();
        Assert.Throws<XtuException>(() => client.Apply(Target(pl1, pl2, true, window)));
        Assert.Empty(tuning.Calls);
    }

    [Fact]
    public void A_locked_control_is_refused_before_anything_is_proposed()
    {
        var tuning = new FakeTuningPort();
        tuning.Controls[XtuIds.Pl1] = tuning.Controls[XtuIds.Pl1] with { ReadOnly = true };
        var (client, _) = Make(tuning);

        var ex = Assert.Throws<XtuException>(() => client.Apply(Target()));

        Assert.Equal("err.locked", ex.Key);
        Assert.False(tuning.WroteAnything);
    }

    [Fact]
    public void A_value_the_control_does_not_support_is_refused_before_anything_is_proposed()
    {
        var tuning = new FakeTuningPort();
        tuning.Controls[XtuIds.Pl2] = tuning.Controls[XtuIds.Pl2] with { Supported = new decimal[] { 60, 70 } };
        var (client, _) = Make(tuning);

        var ex = Assert.Throws<XtuException>(() => client.Apply(Target(pl2: 65)));

        Assert.Equal("err.unsupported", ex.Key);
        Assert.False(tuning.WroteAnything);
    }

    [Fact]
    public void A_rejected_proposal_is_discarded_and_never_applied()
    {
        var tuning = new FakeTuningPort { OnPropose = _ => new PortResult(false, "InvalidValue; control 48 = OutOfRange") };
        var (client, _) = Make(tuning);

        var ex = Assert.Throws<XtuException>(() => client.Apply(Target()));

        Assert.Equal("err.rejected", ex.Key);
        Assert.DoesNotContain("ApplyPending", tuning.Calls);
        Assert.Equal("Discard", tuning.Calls[^1]);
    }

    [Fact]
    public void A_proposal_that_needs_a_reboot_is_discarded()
    {
        var tuning = new FakeTuningPort { OnPropose = _ => new PortResult(true, "Success", RequiresReboot: true) };
        var (client, _) = Make(tuning);

        var ex = Assert.Throws<XtuException>(() => client.Apply(Target()));

        Assert.Equal("err.rejected-reboot", ex.Key);
        Assert.DoesNotContain("ApplyPending", tuning.Calls);
    }

    [Fact]
    public void A_failed_apply_is_discarded()
    {
        var tuning = new FakeTuningPort { OnApply = () => new PortResult(false, "Failed") };
        var (client, _) = Make(tuning);

        var ex = Assert.Throws<XtuException>(() => client.Apply(Target()));

        Assert.Equal("err.applyfail", ex.Key);
        Assert.Equal("Discard", tuning.Calls[^1]);
    }

    [Fact]
    public void A_broken_connection_becomes_an_unavailable_error_and_the_channel_is_closed()
    {
        var tuning = new FakeTuningPort { ThrowOnConnect = new IOException("pipe is being closed") };
        var (client, _) = Make(tuning);

        var ex = Assert.Throws<XtuException>(() => client.Apply(Target()));

        Assert.Equal("err.unavailable", ex.Key);
        Assert.False(tuning.IsOpen);
        Assert.False(tuning.WroteAnything);
    }

    [Fact]
    public void An_exception_after_proposing_discards_pending_changes()
    {
        var tuning = new FakeTuningPort { ThrowOnApply = new TimeoutException("send timed out") };
        var (client, _) = Make(tuning);

        Assert.Throws<XtuException>(() => client.Apply(Target()));

        Assert.Equal(new[] { "Discard", "Close" }, tuning.Calls.TakeLast(2));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void The_write_latch_stops_an_apply_before_any_service_call(string value)
    {
        var tuning = new FakeTuningPort();
        var client = new XtuClient(tuning, new FakeMonitorPort(), name => name == WriteLatch.Variable ? value : null);

        var ex = Assert.Throws<XtuException>(() => client.Apply(Target()));

        Assert.Equal("err.write-forbidden", ex.Key);
        Assert.Empty(tuning.Calls);
    }

    [Fact]
    public void The_latch_does_not_affect_reads()
    {
        var tuning = new FakeTuningPort();
        var client = new XtuClient(tuning, new FakeMonitorPort(), name => name == WriteLatch.Variable ? "1" : null);

        var state = client.ReadState();

        Assert.Equal(55m, state.Pl1);
        Assert.False(tuning.WroteAnything);
    }

    [Fact]
    public void Reading_state_never_calls_a_write_method()
    {
        var (client, tuning) = Make();
        var s = client.ReadState();
        Assert.Equal((55m, 60m, true, 56m), (s.Pl1, s.Pl2, s.Pl2Enable, s.Window));
        Assert.Equal((90m, 115m, 56m), (s.DefPl1, s.DefPl2, s.DefWindow));
        Assert.True(s.RestoreOnBoot);
        Assert.False(tuning.WroteAnything);
        Assert.Equal(new[] { "Connect", "Refresh", "GetControl(48)", "GetControl(47)", "GetControl(49)", "GetControl(66)", "GetRestoreOnBoot" }, tuning.Calls);
    }

    [Fact]
    public void A_failed_read_is_an_unavailable_error_and_the_next_read_reconnects()
    {
        var tuning = new FakeTuningPort { ThrowOnRefresh = new IOException("gone") };
        var (client, _) = Make(tuning);

        Assert.Equal("err.unavailable", Assert.Throws<XtuException>(client.ReadState).Key);
        Assert.False(tuning.IsOpen);

        tuning.ThrowOnRefresh = null;
        client.ReadState();
        Assert.Equal(2, tuning.Calls.Count(c => c == "Connect"));
    }

    [Fact]
    public void Monitor_read_failure_resets_the_monitor_channel()
    {
        var monitor = new FakeMonitorPort { Throw = new IOException("gone") };
        var client = new XtuClient(new FakeTuningPort(), monitor, _ => null);

        Assert.Equal("err.unavailable", Assert.Throws<XtuException>(() => client.ReadMonitor(out _)).Key);
        Assert.Equal(1, monitor.Resets);
    }
}

public class ApplyServiceTests
{
    private static XtuProfile Target() => new("Quiet", 35, 40, true, 28, ProfileKind.Custom);

    private static (ApplyService svc, FakeClient client, FakeApproval approval) Make(bool answer = true)
    {
        var client = new FakeClient();
        var approval = new FakeApproval { Answer = answer };
        return (new ApplyService(client, approval, new FakeLog(), _ => null), client, approval);
    }

    [Theory]
    [InlineData("Hotkey")]
    [InlineData("Command")]
    [InlineData("Tray")]
    public async Task Declined_approval_applies_nothing(string originName)
    {
        var (svc, client, approval) = Make(answer: false);

        var origin = Enum.Parse<ApplyOrigin>(originName);
        var result = await svc.ApplyAsync(Target(), origin);

        Assert.Equal(ApplyStatus.Declined, result.Status);
        Assert.Empty(client.Applied);
        Assert.Single(approval.Asked);
    }

    [Theory]
    [InlineData("Hotkey")]
    [InlineData("Command")]
    [InlineData("Tray")]
    public async Task Approved_apply_shows_the_exact_values_and_writes_once(string originName)
    {
        var (svc, client, approval) = Make();

        var origin = Enum.Parse<ApplyOrigin>(originName);
        var result = await svc.ApplyAsync(Target(), origin);

        Assert.Equal(ApplyStatus.Applied, result.Status);
        Assert.Equal("Quiet", Assert.Single(client.Applied).Name);
        var (effect, detail) = Assert.Single(approval.Asked);
        Assert.Equal(ApplyService.EffectKey, effect.DescriptionKey);
        Assert.Equal("approve.detail", detail!.Value.Key);
        Assert.Equal(new object[] { "Quiet", "35", "40", "28", "PL1 55 W / PL2 60 W / 56 s" }, detail.Value.Args!);
    }

    [Fact]
    public async Task Pl2_off_uses_its_own_text()
    {
        var (svc, _, approval) = Make();
        await svc.ApplyAsync(Target() with { Pl2Enable = false }, ApplyOrigin.Hotkey);
        Assert.Equal("approve.detail-pl2-off", approval.Asked[0].Detail!.Value.Key);
    }

    [Theory]
    [InlineData("PageConfirmed")]
    [InlineData("Auto")]
    public async Task Page_and_auto_switch_do_not_ask_a_second_time(string originName)
    {
        var (svc, client, approval) = Make();
        var origin = Enum.Parse<ApplyOrigin>(originName);
        var result = await svc.ApplyAsync(Target(), origin);
        Assert.Equal(ApplyStatus.Applied, result.Status);
        Assert.Empty(approval.Asked);
        Assert.Single(client.Applied);
    }

    [Fact]
    public async Task Invalid_values_are_refused_before_anyone_is_asked()
    {
        var (svc, client, approval) = Make();
        var result = await svc.ApplyAsync(Target() with { Pl1 = 500 }, ApplyOrigin.Hotkey);
        Assert.Equal(ApplyStatus.Invalid, result.Status);
        Assert.Empty(approval.Asked);
        Assert.Empty(client.Applied);
    }

    [Fact]
    public async Task Values_already_in_force_neither_ask_nor_write()
    {
        var (svc, client, approval) = Make();
        var result = await svc.ApplyAsync(new XtuProfile("Same", 55, 60, true, 56, ProfileKind.Xtu), ApplyOrigin.Hotkey);
        Assert.Equal(ApplyStatus.AlreadyActive, result.Status);
        Assert.Empty(approval.Asked);
        Assert.Empty(client.Applied);
    }

    [Fact]
    public async Task Unreadable_XTU_fails_before_asking()
    {
        var (svc, client, approval) = Make();
        client.ReadError = new XtuException("err.unavailable", "gone");
        var result = await svc.ApplyAsync(Target(), ApplyOrigin.Hotkey);
        Assert.Equal(ApplyStatus.Failed, result.Status);
        Assert.Empty(approval.Asked);
    }

    [Fact]
    public async Task The_write_latch_fails_before_asking()
    {
        var client = new FakeClient();
        var approval = new FakeApproval();
        var svc = new ApplyService(client, approval, new FakeLog(), n => n == WriteLatch.Variable ? "1" : null);
        var result = await svc.ApplyAsync(Target(), ApplyOrigin.Hotkey);
        Assert.Equal(ApplyStatus.Failed, result.Status);
        Assert.Equal("err.write-forbidden", result.Error!.Key);
        Assert.Empty(approval.Asked);
        Assert.Empty(client.Applied);
    }

    [Fact]
    public async Task A_service_refusal_is_a_failed_result_not_an_exception()
    {
        var (svc, client, _) = Make();
        client.ApplyError = new XtuException("err.rejected", "x");
        var result = await svc.ApplyAsync(Target(), ApplyOrigin.PageConfirmed);
        Assert.Equal(ApplyStatus.Failed, result.Status);
        Assert.Equal("err.rejected", result.Error!.Key);
    }

    [Fact]
    public async Task A_second_apply_while_one_runs_is_dropped()
    {
        var gate = new TaskCompletionSource<bool>();
        var client = new FakeClient();
        var approval = new BlockingApproval(gate.Task);
        var svc = new ApplyService(client, approval, new FakeLog(), _ => null);

        var first = svc.ApplyAsync(Target(), ApplyOrigin.Hotkey);
        var second = await svc.ApplyAsync(Target(), ApplyOrigin.Hotkey);
        Assert.Equal(ApplyStatus.Busy, second.Status);

        gate.SetResult(true);
        Assert.Equal(ApplyStatus.Applied, (await first).Status);
        Assert.False(svc.IsBusy);
    }

    [Fact]
    public async Task Completed_is_raised_with_the_origin()
    {
        var (svc, _, _) = Make();
        ApplyOrigin? seen = null;
        svc.Completed += (_, o) => seen = o;
        await svc.ApplyAsync(Target(), ApplyOrigin.Tray);
        Assert.Equal(ApplyOrigin.Tray, seen);
    }

    private sealed class BlockingApproval : IHardwareApproval
    {
        private readonly Task<bool> _answer;
        public BlockingApproval(Task<bool> answer) => _answer = answer;
        public Task<bool> ConfirmAsync(HardwareEffect effect, LocalisedText? detail = null, CancellationToken cancellationToken = default) => _answer;
    }
}

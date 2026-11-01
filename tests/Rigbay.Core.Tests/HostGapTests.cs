using Xunit;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.Core.Tests;

/// <summary>Task A9: a revert is asked about like any hardware change, the poll-rate signals, withdrawing a metric, and metric labels.</summary>
public sealed class HostGapTests
{
    private static (RigbayHost Host, ModuleEntry Module, FakeApproval Prompt) StartSample(TestEnv env, bool answer = true)
    {
        var host = env.StartHost("sample");
        var prompt = new FakeApproval { Answer = answer };
        host.ApprovalPrompt = prompt;
        return (host, host.Find("sample")!, prompt);
    }

    private static T Row<T>(ModuleEntry m, string header) where T : Row => m.Page!.EnumerateRows().OfType<T>().Single(r => r.HeaderKey == header);

    // ================================================================ 1. revert is asked about

    [Fact]
    public async Task Apply_asks_and_revert_asks_again_with_the_exact_old_and_new_value()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var row = Row<ChoiceRow>(m, "row.profile.header");
        var editor = RowEditors.For(m, row);

        Assert.Equal(EditOutcome.Applied, (await editor.SubmitAsync("max")).Outcome);
        Assert.Equal("max", row.Value.Value);
        Assert.Single(prompt.Requests);
        Assert.False(prompt.Requests[0].IsRevert);

        var result = await editor.RevertAsync();

        Assert.Equal(EditOutcome.Applied, result.Outcome);
        Assert.Equal("auto", row.Value.Value);
        Assert.Equal(2, prompt.Requests.Count);
        var question = prompt.Requests[1];
        Assert.True(question.IsRevert);
        Assert.Equal("Profile: Maximum → Auto", question.Detail);
        Assert.Equal("Pretends to put the fans back to the previous profile. Nothing on this machine changes.", question.EffectText); // RevertDescriptionKey
        Assert.Contains("Profile", result.Message);                                                                                 // the status names what was written
        Assert.Contains("Auto", result.Message);
        Assert.Contains("Maximum", result.Message);
        Assert.False(editor.CanRevert);
        host.Shutdown();
    }

    [Fact]
    public async Task A_declined_revert_writes_nothing_and_can_be_tried_again()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var row = Row<ChoiceRow>(m, "row.profile.header");
        var editor = RowEditors.For(m, row);
        await editor.SubmitAsync("max");

        prompt.Answer = false;
        var declined = await editor.RevertAsync();

        Assert.Equal(EditOutcome.Declined, declined.Outcome);
        Assert.Equal("max", row.Value.Value);   // untouched
        Assert.True(editor.CanRevert);          // still revertible
        Assert.Equal(2, prompt.Requests.Count);

        prompt.Answer = true;
        Assert.Equal(EditOutcome.Applied, (await editor.RevertAsync()).Outcome);
        Assert.Equal("auto", row.Value.Value);
        host.Shutdown();
    }

    [Fact]
    public async Task Reverting_a_revert_towards_the_higher_setting_is_asked_like_any_other_change()
    {
        // Auto -> Maximum (asked), Maximum -> Auto (asked), Revert would write Maximum again: asked, with "Auto → Maximum".
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var row = Row<ChoiceRow>(m, "row.profile.header");
        var editor = RowEditors.For(m, row);
        await editor.SubmitAsync("max");
        await editor.SubmitAsync("auto");
        Assert.Equal(2, prompt.Requests.Count);

        prompt.Answer = false;
        var declined = await editor.RevertAsync();
        Assert.Equal(EditOutcome.Declined, declined.Outcome);
        Assert.Equal("auto", row.Value.Value);
        Assert.Equal("Profile: Auto → Maximum", prompt.Requests[2].Detail);

        prompt.Answer = true;
        Assert.Equal(EditOutcome.Applied, (await editor.RevertAsync()).Outcome);
        Assert.Equal("max", row.Value.Value);
        Assert.Equal(4, prompt.Requests.Count);
        host.Shutdown();
    }

    [Fact]
    public async Task Revert_after_the_read_back_showed_another_value_asks_about_the_value_that_is_really_set()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var row = Row<NumberRow>(m, "row.limit.header");
        var editor = RowEditors.For(m, row);
        editor.SetDraft(55);
        await editor.ApplyAsync();
        row.Value.Value = 50; // the module's read-back says 50 now, not the 55 that was written

        prompt.Answer = false;
        await editor.RevertAsync();

        Assert.Equal("Limit: 50 → 45", prompt.Requests[^1].Detail);
        Assert.Equal(50, row.Value.Value);
        host.Shutdown();
    }

    [Fact]
    public async Task Reverting_to_the_value_that_is_already_set_writes_nothing_and_asks_nothing()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var row = Row<ChoiceRow>(m, "row.profile.header");
        var editor = RowEditors.For(m, row);
        await editor.SubmitAsync("max");
        row.Value.Value = "auto"; // the module went back by itself

        var result = await editor.RevertAsync();

        Assert.Equal(EditOutcome.Applied, result.Outcome);
        Assert.Single(prompt.Requests);
        Assert.False(editor.CanRevert);
        host.Shutdown();
    }

    [Fact]
    public async Task A_number_row_revert_is_asked_with_both_numbers()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var row = Row<NumberRow>(m, "row.limit.header");
        var editor = RowEditors.For(m, row);
        editor.SetDraft(55);
        Assert.Equal(EditOutcome.Applied, (await editor.ApplyAsync()).Outcome);

        prompt.Answer = false;
        Assert.Equal(EditOutcome.Declined, (await editor.RevertAsync()).Outcome);
        Assert.Equal(55, row.Value.Value);
        Assert.Equal("Limit: 55 → 45", prompt.Requests[^1].Detail);
        Assert.True(prompt.Requests[^1].IsRevert);

        prompt.Answer = true;
        Assert.Equal(EditOutcome.Applied, (await editor.RevertAsync()).Outcome);
        Assert.Equal(45, row.Value.Value);
        Assert.Equal(3, prompt.Requests.Count);
        host.Shutdown();
    }

    [Fact]
    public async Task Without_a_way_to_ask_a_hardware_revert_is_refused()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var row = Row<ChoiceRow>(m, "row.profile.header");
        var editor = RowEditors.For(m, row);
        await editor.SubmitAsync("max");

        host.ApprovalPrompt = null;
        var result = await editor.RevertAsync();

        Assert.Equal(EditOutcome.Declined, result.Outcome);
        Assert.Contains("cannot ask for confirmation", result.Message);
        Assert.Equal("max", row.Value.Value);
        host.Shutdown();
    }

    [Fact]
    public async Task The_synchronous_revert_cannot_ask_so_it_writes_nothing_on_a_hardware_row()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var row = Row<ChoiceRow>(m, "row.profile.header");
        var editor = RowEditors.For(m, row);
        await editor.SubmitAsync("max");

        var result = editor.Revert();

        Assert.Equal(EditOutcome.Declined, result.Outcome);
        Assert.Equal("max", row.Value.Value);
        Assert.True(editor.CanRevert);
        Assert.Contains("must be confirmed", result.Message);
        Assert.Single(prompt.Requests);
        host.Shutdown();
    }

    [Fact]
    public async Task A_row_that_does_not_change_hardware_reverts_without_a_question_both_ways()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var row = Row<TextRow>(m, "row.label.header");
        var editor = RowEditors.For(m, row);
        editor.SetDraft("hello");
        await editor.ApplyAsync();

        var result = await editor.RevertAsync();

        Assert.Equal(EditOutcome.Applied, result.Outcome);
        Assert.Null(result.Message);
        Assert.Equal("", row.Value.Value);
        Assert.Empty(prompt.Requests);

        editor.SetDraft("again");
        await editor.ApplyAsync();
        Assert.Equal(EditOutcome.Applied, editor.Revert().Outcome); // the synchronous one still works for these rows
        Assert.Empty(prompt.Requests);
        host.Shutdown();
    }

    [Fact]
    public async Task SafeRevert_reverts_without_a_question_and_the_status_names_what_was_written()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var row = Row<ToggleRow>(m, "row.light.header");
        Assert.True(row.HardwareEffect!.SafeRevert);
        var editor = RowEditors.For(m, row);
        await editor.SubmitAsync(true);
        Assert.Single(prompt.Requests); // applying is still asked

        var result = await editor.RevertAsync();

        Assert.Equal(EditOutcome.Applied, result.Outcome);
        Assert.False(row.Value.Value);
        Assert.Single(prompt.Requests); // no question for the revert
        Assert.Contains("Status light", result.Message);
        Assert.Contains("Off", result.Message);
        Assert.Contains("On", result.Message);

        // the synchronous revert works for a SafeRevert row, too
        await editor.SubmitAsync(true);
        var sync = editor.Revert();
        Assert.Equal(EditOutcome.Applied, sync.Outcome);
        Assert.Contains("Status light", sync.Message);
        Assert.Equal(2, prompt.Requests.Count);
        host.Shutdown();
    }

    [Fact]
    public void SafeRevert_is_off_unless_the_module_sets_it()
    {
        Assert.False(new HardwareEffect("effect.x").SafeRevert);
        Assert.False(new HardwareEffect("effect.x", Revertible: false).SafeRevert);
    }

    [Fact]
    public async Task The_value_changing_while_the_revert_question_is_open_writes_nothing()
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        var m = host.Find("sample")!;
        var row = Row<ChoiceRow>(m, "row.profile.header");
        var editor = RowEditors.For(m, row);
        host.ApprovalPrompt = new FakeApproval();
        await editor.SubmitAsync("max");

        // the user answers yes, but the module changed the value meanwhile
        host.ApprovalPrompt = new ChangingPrompt(() => row.Value.Value = "auto");
        var result = await editor.RevertAsync();

        Assert.Equal(EditOutcome.Declined, result.Outcome);
        Assert.Contains("changed while you were being asked", result.Message);
        Assert.Equal("auto", row.Value.Value);
        host.Shutdown();
    }

    private sealed class ChangingPrompt(Action change) : IApprovalPrompt
    {
        public Task<bool> ConfirmAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            change();
            return Task.FromResult(true);
        }
    }

    // ---------------------------------------------------------------- buttons

    private static string Status(ModuleEntry m) => Row<ValueRow>(m, "row.status.header").Value.Value;

    [Fact]
    public async Task A_hardware_button_asks_before_it_runs_and_again_before_its_revert()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var boost = Row<ButtonRow>(m, "row.boost.header");

        var on = await RowEditors.InvokeButtonAsync(m, boost);
        Assert.True(on.Succeeded);
        Assert.Contains("boost", Status(m));
        Assert.Single(prompt.Requests);
        Assert.False(prompt.Requests[0].IsRevert);

        prompt.Answer = false;
        var declined = await RowEditors.RevertButtonAsync(m, boost);
        Assert.False(declined.Succeeded);
        Assert.Equal("core.approval.declined", declined.Message!.Value.Key);
        Assert.Contains("boost", Status(m)); // the revert action did not run
        Assert.Equal(2, prompt.Requests.Count);
        Assert.True(prompt.Requests[1].IsRevert);
        Assert.Equal("Boost: Boost off", prompt.Requests[1].Detail);

        prompt.Answer = true;
        var off = await RowEditors.RevertButtonAsync(m, boost);
        Assert.True(off.Succeeded);
        Assert.DoesNotContain("boost", Status(m));
        Assert.Equal(3, prompt.Requests.Count);
        host.Shutdown();
    }

    [Fact]
    public async Task A_button_revert_can_carry_its_own_question_text()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var boost = Row<ButtonRow>(m, "row.boost.header");

        await RowEditors.RevertButtonAsync(m, boost, detail: "Boost: on → off");

        Assert.Equal("Boost: on → off", prompt.Requests.Single().Detail);
        host.Shutdown();
    }

    [Fact]
    public async Task A_button_revert_without_a_way_to_ask_is_refused()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var boost = Row<ButtonRow>(m, "row.boost.header");
        await RowEditors.InvokeButtonAsync(m, boost);
        host.ApprovalPrompt = null;

        var result = await RowEditors.RevertButtonAsync(m, boost);

        Assert.False(result.Succeeded);
        Assert.Contains("boost", Status(m));
        host.Shutdown();
    }

    [Fact]
    public async Task A_SafeRevert_button_reverts_without_a_question_and_says_what_it_did()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var boost = Row<ButtonRow>(m, "row.boost.header") with { HardwareEffect = new HardwareEffect("effect.boost") { SafeRevert = true }, RevertButtonKey = null };
        var plain = boost with { RevertAsync = _ => Task.FromResult(ActionResult.Ok()) };

        var result = await RowEditors.RevertButtonAsync(m, plain);

        Assert.True(result.Succeeded);
        Assert.Empty(prompt.Requests);
        Assert.Equal("core.revert.button-done", result.Message!.Value.Key); // no message from the module: the host names the revert
        Assert.Equal(new object[] { "Boost", "Revert" }, result.Message.Value.Args);

        // a message the module gives is kept
        var withMessage = await RowEditors.RevertButtonAsync(m, boost);
        Assert.Equal("boost.off", withMessage.Message!.Value.Key);
        Assert.Empty(prompt.Requests);
        host.Shutdown();
    }

    [Fact]
    public async Task A_button_without_a_hardware_effect_reverts_without_a_question()
    {
        using var env = new TestEnv();
        var (host, m, prompt) = StartSample(env);
        var called = false;
        var row = new ButtonRow("row.boost.header", "row.boost.button", _ => Task.FromResult(ActionResult.Ok()))
        {
            RevertAsync = _ => { called = true; return Task.FromResult(ActionResult.Ok()); },
        };

        var result = await RowEditors.RevertButtonAsync(m, row);

        Assert.True(result.Succeeded);
        Assert.True(called);
        Assert.Empty(prompt.Requests);
        host.Shutdown();
    }

    // ================================================================ 2. poll rate signals

    private static (RigbayHost Host, ModuleEntry Module, TestEnv Env) StartSignals(string behavior = "signals", bool enable = true)
    {
        var env = new TestEnv();
        var folder = env.AddModule("faulty", behavior);
        Directory.CreateDirectory(Path.Combine(folder, "lang"));
        File.WriteAllText(Path.Combine(folder, "lang", "en.lang"), "@code = en\n@name = English\n\nmodule.faulty.metric.temp = Core temperature\n");
        var host = env.NewHost();
        host.Discover();
        var m = host.Find("faulty")!;
        if (enable) Assert.True(m.Enable());
        return (host, m, env);
    }

    private static async Task<string> Run(RigbayHost host, params string[] args)
    {
        var outcome = await host.ExecuteCommandAsync(new[] { "faulty" }.Concat(args).ToArray());
        Assert.Equal(CommandOutcome.Ok, outcome.ExitCode);
        return outcome.Output;
    }

    [Fact]
    public async Task PageVisible_follows_what_the_app_says_and_starts_false()
    {
        var (host, m, env) = StartSignals();
        using (env)
        {
            Assert.Contains("init=False;visible=False;changes=;", await Run(host, "signals"));

            m.SetPageVisible(true);
            m.SetPageVisible(true); // the same state again: no event
            m.SetPageVisible(false);

            Assert.Contains("visible=False;changes=True,False;", await Run(host, "signals"));
            Assert.False(m.IsPageVisible);
            host.Shutdown();
        }
    }

    [Fact]
    public async Task The_module_cannot_set_PageVisible()
    {
        var (host, m, env) = StartSignals();
        using (env)
        {
            m.SetPageVisible(true);
            Assert.Equal("visible=True", await Run(host, "set-visible")); // the module tried to flip it: ignored
            Assert.Contains("changes=True;", await Run(host, "signals"));
            host.Shutdown();
        }
    }

    [Fact]
    public async Task A_page_visible_before_the_module_is_enabled_is_visible_to_it_in_Initialize()
    {
        var (host, m, env) = StartSignals(enable: false);
        using (env)
        {
            m.SetPageVisible(true); // module still off: remembered, nobody is called
            Assert.True(m.IsPageVisible);

            Assert.True(m.Enable());

            Assert.Contains("init=True;visible=True;changes=;", await Run(host, "signals"));
            host.Shutdown();
        }
    }

    [Fact]
    public async Task PageVisible_is_a_new_state_after_the_module_is_enabled_again_and_stops_calling_while_off()
    {
        var (host, m, env) = StartSignals();
        using (env)
        {
            m.SetPageVisible(true);
            Assert.True(m.Disable());
            m.SetPageVisible(false); // while off: only remembered
            Assert.True(m.Enable());

            Assert.Contains("init=False;", await Run(host, "signals"));
            host.Shutdown();
        }
    }

    [Fact]
    public void A_throwing_PageVisible_handler_faults_the_module_and_not_the_app()
    {
        var (host, m, env) = StartSignals("signal-throws");
        using (env)
        {
            m.SetPageVisible(true); // must not throw

            Assert.Equal(ModuleLifecycle.Faulted, m.Lifecycle);
            Assert.Contains("PageVisible handler", m.Reason!.Args.Select(a => a.ToString()));
            m.SetPageVisible(false); // and calling again on a faulted module is harmless
            host.Shutdown();
        }
    }

    [Fact]
    public void A_host_without_the_signal_gives_a_value_that_stays_true()
    {
        IHostServices services = new MinimalServices();
        Assert.True(services.PageVisible.Value);
        services.PageVisible.Value = false;
        Assert.True(services.PageVisible.Value);
        services.PageVisible.Changed += () => { };
    }

    [Fact]
    public void A_metrics_double_that_implements_only_the_old_members_still_works()
    {
        IMetricsBus bus = new MinimalMetrics();
        Assert.Equal(0, bus.SubscriberCount(new MetricId("a.b")));
        bus.Withdraw(new MetricId("a.b"));
        bus.Describe(new MetricDescriptor(new MetricId("a.b"), "x"));
        Assert.Null(bus.Latest(new MetricId("a.b")));
        bus.SubscribersChanged += () => { };
    }

    private sealed class MinimalMetrics : IMetricsBus
    {
        public void Publish(MetricId id, double value, MetricUnit unit) { }
        public IDisposable Subscribe(Action<MetricSample> handler) => throw new NotSupportedException();
        public IDisposable Subscribe(MetricId id, Action<MetricSample> handler) => throw new NotSupportedException();
        public bool TryGetLatest(MetricId id, out MetricSample sample) { sample = default!; return false; }
        public IReadOnlyList<MetricSample> GetLatest() => Array.Empty<MetricSample>();
    }

    private sealed class MinimalServices : IHostServices
    {
        public string ModuleId => "x";
        public string ModuleDirectory => "";
        public string DataDirectory => "";
        public Version HostVersion => new(0, 1);
        public CancellationToken StopToken => default;
        public ISettingsStore Settings => throw new NotSupportedException();
        public ILocalisation Localisation => throw new NotSupportedException();
        public ILog Log => throw new NotSupportedException();
        public IHotkeyService Hotkeys => throw new NotSupportedException();
        public ITrayService Tray => throw new NotSupportedException();
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IToastService Toast => throw new NotSupportedException();
        public IMetricsBus Metrics => throw new NotSupportedException();
        public IHardwareApproval Approval => throw new NotSupportedException();
        public void RunOnUi(Action action) { }
        public void RequestPageRefresh() { }
    }

    // ---------------------------------------------------------------- subscriber count

    private static readonly MetricId Temp = new("faulty.temp");

    [Fact]
    public void SubscriberCount_counts_filtered_and_all_samples_subscribers_and_the_event_fires_for_both_directions()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        var changes = new List<MetricId?>();
        host.Metrics.SubscribersChanged += changes.Add;

        Assert.Equal(0, host.Metrics.SubscriberCount(Temp));
        var a = host.Metrics.Subscribe(_ => { }, Temp);
        var all = host.Metrics.Subscribe(_ => { });                       // sees every id
        var other = host.Metrics.Subscribe(_ => { }, new MetricId("cpu.temp"));

        Assert.Equal(2, host.Metrics.SubscriberCount(Temp));
        Assert.Equal(2, host.Metrics.SubscriberCount("faulty.temp"));      // the string overload of the brief
        Assert.Equal(2, host.Metrics.SubscriberCount(new MetricId("cpu.temp")));
        Assert.Equal(0, host.Metrics.SubscriberCount("not a valid id"));
        Assert.Equal(new MetricId?[] { Temp, null, new MetricId("cpu.temp") }, changes);

        a.Dispose();
        a.Dispose(); // twice: counted and announced once
        all.Dispose();
        other.Dispose();
        Assert.Equal(0, host.Metrics.SubscriberCount(Temp));
        Assert.Equal(6, changes.Count);
    }

    [Fact]
    public async Task A_module_sees_the_host_and_other_subscribers_but_not_its_own()
    {
        var (host, m, env) = StartSignals();
        using (env)
        {
            Assert.Contains("subs=0", await Run(host, "signals"));

            await Run(host, "listen");   // the module's own subscription: not counted, no event for itself
            var own = await Run(host, "signals");
            Assert.Contains("subchanges=0;subs=0", own);

            var ui = host.Metrics.Subscribe(_ => { }, Temp); // the dashboard
            var s1 = await Run(host, "signals");
            Assert.Contains("subs=1", s1);
            Assert.Contains("subchanges=1", s1);

            ui.Dispose();
            var s2 = await Run(host, "signals");
            Assert.Contains("subs=0", s2);
            Assert.Contains("subchanges=2", s2);
            host.Shutdown();
        }
    }

    [Fact]
    public async Task The_module_is_not_called_back_for_subscriptions_after_it_stopped()
    {
        var (host, m, env) = StartSignals();
        using (env)
        {
            Assert.True(m.Disable());
            var ui = host.Metrics.Subscribe(_ => { }, Temp);
            ui.Dispose();
            Assert.True(m.Enable());
            Assert.Contains("subchanges=0", await Run(host, "signals")); // a fresh run: nothing from while it was off

            var again = host.Metrics.Subscribe(_ => { }, Temp);
            Assert.Contains("subchanges=1", await Run(host, "signals")); // once, so the hook of the first run is gone
            again.Dispose();
            host.Shutdown();
        }
    }

    [Fact]
    public void A_throwing_SubscribersChanged_handler_faults_the_module_and_does_not_reach_the_subscriber()
    {
        var (host, m, env) = StartSignals("signal-throws");
        using (env)
        {
            var ui = host.Metrics.Subscribe(_ => { }, Temp); // must not throw

            Assert.Equal(ModuleLifecycle.Faulted, m.Lifecycle);
            ui.Dispose();
            host.Shutdown();
        }
    }

    [Fact]
    public async Task The_sample_module_polls_slowly_until_somebody_looks_and_fast_after()
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        var m = host.Find("sample")!;
        var ticks = new MetricId("sample.ticks");

        // the first tick comes after about a second whatever the rate; it settles on the slow rate (nobody listens)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.Metrics.Latest(ticks) is null && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(1, host.Metrics.Latest(ticks)!.Value);
        await Task.Delay(2500);
        Assert.Equal(1, host.Metrics.Latest(ticks)!.Value); // the next tick is about ten seconds away

        m.SetPageVisible(true); // the page opens: read at once, then every second
        deadline = DateTime.UtcNow.AddSeconds(5);
        while ((host.Metrics.Latest(ticks)?.Value ?? 0) < 3 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(host.Metrics.Latest(ticks)!.Value >= 3);

        // and slow again when the page closes and nobody subscribes
        m.SetPageVisible(false);
        await Task.Delay(1500);
        var settled = host.Metrics.Latest(ticks)!.Value;
        await Task.Delay(2500);
        Assert.True(host.Metrics.Latest(ticks)!.Value <= settled + 1);
        host.Shutdown();
    }

    [Fact]
    public async Task The_sample_module_polls_fast_while_a_subscriber_exists()
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        var ticks = new MetricId("sample.ticks");
        var seen = 0;
        using var sub = host.Metrics.Subscribe(s => Interlocked.Increment(ref seen), ticks); // the dashboard

        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (Volatile.Read(ref seen) < 3 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(Volatile.Read(ref seen) >= 3);
        host.Shutdown();
    }

    // ================================================================ 3. withdraw

    private static MetricSample Sample(string id, double value, string source = "m", MetricUnit unit = MetricUnit.Celsius) =>
        new(new MetricId(id), value, unit, DateTimeOffset.UnixEpoch, source);

    [Fact]
    public void Withdraw_makes_the_latest_unavailable_and_tells_every_subscriber()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        var one = new List<MetricSample>();
        var all = new List<MetricSample>();
        var unrelated = new List<MetricSample>();
        host.Metrics.Subscribe(one.Add, Temp);
        host.Metrics.Subscribe(all.Add);
        host.Metrics.Subscribe(unrelated.Add, new MetricId("other.id"));
        host.Metrics.Publish(Sample("faulty.temp", 41));

        Assert.True(host.Metrics.Withdraw("m", Temp));

        Assert.False(host.Metrics.TryGetLatest(Temp, out _));
        Assert.Null(host.Metrics.Latest(Temp));
        Assert.Null(host.Metrics.Latest("faulty.temp"));
        Assert.Empty(host.Metrics.GetLatest());
        Assert.True(host.Metrics.IsWithdrawn(Temp));
        foreach (var received in new[] { one, all })
        {
            Assert.Equal(2, received.Count);
            Assert.True(received[0].IsAvailable);
            var notice = received[1];
            Assert.False(notice.IsAvailable);
            Assert.True(double.IsNaN(notice.Value));
            Assert.Equal(Temp, notice.Id);
            Assert.Equal(MetricUnit.Celsius, notice.Unit);
            Assert.Equal("m", notice.SourceModule);
        }
        Assert.Empty(unrelated);
    }

    [Fact]
    public void Withdrawing_twice_or_an_unknown_id_notifies_nobody()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        var received = new List<MetricSample>();
        host.Metrics.Subscribe(received.Add);

        Assert.False(host.Metrics.Withdraw("m", Temp));               // never published
        host.Metrics.Publish(Sample("faulty.temp", 1));
        Assert.True(host.Metrics.Withdraw("m", Temp));
        Assert.False(host.Metrics.Withdraw("m", Temp));               // already withdrawn
        Assert.Equal(2, received.Count);
    }

    [Fact]
    public void A_new_sample_makes_a_withdrawn_metric_available_again()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        host.Metrics.Publish(Sample("faulty.temp", 1));
        host.Metrics.Withdraw("m", Temp);

        host.Metrics.Publish(Sample("faulty.temp", 7));

        Assert.False(host.Metrics.IsWithdrawn(Temp));
        Assert.Equal(7, host.Metrics.Latest(Temp)!.Value);
        Assert.True(host.Metrics.Latest(Temp)!.IsAvailable);
    }

    [Fact]
    public void A_module_cannot_withdraw_a_reading_that_another_module_published_last()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        host.Metrics.Publish(Sample("faulty.temp", 1, source: "a"));
        host.Metrics.Publish(Sample("faulty.temp", 2, source: "b")); // the newest sample wins

        Assert.False(host.Metrics.Withdraw("a", Temp));
        Assert.Equal(2, host.Metrics.Latest(Temp)!.Value);
        Assert.True(host.Metrics.Withdraw("b", Temp));
    }

    [Fact]
    public void Publishing_an_unavailable_sample_is_a_withdraw_and_WithdrawAll_takes_back_one_modules_readings()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        host.Metrics.Publish(Sample("a.one", 1, source: "a"));
        host.Metrics.Publish(Sample("a.two", 2, source: "a"));
        host.Metrics.Publish(Sample("b.one", 3, source: "b"));

        host.Metrics.Publish(Sample("b.one", 0, source: "b") with { IsAvailable = false });
        Assert.Null(host.Metrics.Latest("b.one"));

        host.Metrics.WithdrawAll("a");
        Assert.Empty(host.Metrics.GetLatest());
    }

    [Fact]
    public async Task A_module_withdraws_through_its_bus()
    {
        var (host, m, env) = StartSignals();
        using (env)
        {
            await Run(host, "publish", "faulty.temp", "55");
            Assert.Equal("55", await Run(host, "latest", "faulty.temp"));
            var seen = new List<MetricSample>();
            host.Metrics.Subscribe(seen.Add, Temp);

            await Run(host, "withdraw", "faulty.temp");

            Assert.Equal("none", await Run(host, "latest", "faulty.temp"));
            Assert.Null(host.Metrics.Latest(Temp));
            Assert.False(Assert.Single(seen).IsAvailable);
            Assert.Equal("faulty", seen[0].SourceModule);
            host.Shutdown();
        }
    }

    [Fact]
    public void A_reading_row_shows_not_available_for_a_withdrawn_or_unknown_metric()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        var row = new ReadingRow("row.temp", Temp) { Decimals = 1 };

        Assert.Equal("Not available", ReadingFormat.Format(host.Languages, row, host.Metrics.Latest(Temp))); // never published

        host.Metrics.Publish(Sample("faulty.temp", 41.26));
        Assert.Equal("41.3 °C", ReadingFormat.Format(host.Languages, row, host.Metrics.Latest(Temp)));

        host.Metrics.Withdraw("m", Temp);
        Assert.Equal("Not available", ReadingFormat.Format(host.Languages, row, host.Metrics.Latest(Temp)));

        // the notification a live subscriber gets is not available, too, and so is a NaN that slipped through
        var notice = Sample("faulty.temp", double.NaN) with { IsAvailable = false };
        Assert.False(ReadingFormat.IsAvailable(notice));
        Assert.False(ReadingFormat.IsAvailable(Sample("faulty.temp", double.NaN)));
        Assert.False(ReadingFormat.IsAvailable(null));
        Assert.Equal("Not available", ReadingFormat.Format(host.Languages, row, notice));

        host.SetLanguage("vi");
        Assert.Equal("Không có số liệu", ReadingFormat.Format(host.Languages, row, null));
    }

    // ================================================================ 4. labels

    [Fact]
    public void Descriptors_resolve_to_the_language_text_of_the_module()
    {
        var (host, m, env) = StartSignals();
        using (env)
        {
            Assert.True(host.Metrics.TryGetDescriptor(Temp, out var descriptor, out var owner));
            Assert.Equal("faulty", owner);
            Assert.Equal(MetricUnit.Celsius, descriptor.Unit);
            Assert.Equal("0.0", descriptor.FormatHint);

            Assert.Equal("Core temperature", host.ResolveMetricLabel(Temp));          // module prefix added
            Assert.Equal("Core temperature", m.ResolveMetricLabel(Temp));
            Assert.Equal("Core temperature", host.ResolveMetricLabel("faulty.temp"));  // string overload
            Assert.Equal("Fan 1", host.ResolveMetricLabel(new MetricId("faulty.fan"))); // Text.Literal, no lookup
            host.Shutdown();
        }
    }

    [Fact]
    public void An_unknown_metric_or_a_missing_label_text_falls_back_to_the_id()
    {
        var (host, _, env) = StartSignals();
        using (env)
        {
            Assert.Equal("faulty.nokey", host.ResolveMetricLabel(new MetricId("faulty.nokey"))); // key not in the file
            Assert.Equal("nobody.knows", host.ResolveMetricLabel(new MetricId("nobody.knows")));
            Assert.Equal("not an id", host.ResolveMetricLabel("not an id"));
            host.Shutdown();
        }
    }

    [Fact]
    public void The_host_label_for_a_shared_id_is_the_fallback_when_no_module_described_it()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        var catalog = host.Languages;
        var path = Path.Combine(env.Root, "user-lang");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "en.lang"), "@code = en\n@name = English\n\nmetric.cpu.temp = CPU\n");
        catalog.LoadDirectory(path);

        Assert.Equal("CPU", host.ResolveMetricLabel(WellKnownMetrics.CpuTemp));
    }

    [Fact]
    public void The_label_follows_the_language()
    {
        var (host, m, env) = StartSignals();
        using (env)
        {
            File.WriteAllText(Path.Combine(m.Folder, "lang", "vi.lang"), "@code = vi\n@name = Tiếng Việt\n\nmodule.faulty.metric.temp = Nhiệt độ lõi\n");
            host.Languages.LoadDirectory(Path.Combine(m.Folder, "lang"), LanguageCatalog.ModulePrefix("faulty"));
            host.SetLanguage("vi");

            Assert.Equal("Nhiệt độ lõi", host.ResolveMetricLabel(Temp));
            host.SetLanguage("en");
            Assert.Equal("Core temperature", host.ResolveMetricLabel(Temp));
            host.Shutdown();
        }
    }

    [Fact]
    public void The_first_module_to_describe_an_id_keeps_it_and_the_same_module_may_replace_its_own()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        var id = new MetricId("shared.id");

        host.Metrics.Describe("a", new MetricDescriptor(id, "first", MetricUnit.Watt));
        host.Metrics.Describe("b", new MetricDescriptor(id, "second", MetricUnit.Rpm));
        Assert.True(host.Metrics.TryGetDescriptor(id, out var d, out var owner));
        Assert.Equal(("a", "first"), (owner, d.LabelKey));

        host.Metrics.Describe("a", new MetricDescriptor(id, "replaced", MetricUnit.Watt));
        Assert.Equal("replaced", host.Metrics.TryGetDescriptor(id, out d, out _) ? d.LabelKey : null);

        host.Metrics.Describe("a", new MetricDescriptor(new MetricId("x.y"), "  ")); // no label: dropped
        Assert.False(host.Metrics.TryGetDescriptor(new MetricId("x.y"), out _, out _));
        Assert.Single(host.Metrics.GetDescriptors());
    }

    [Fact]
    public void Summary_readings_carry_label_unit_and_format_hint_for_the_dashboard()
    {
        var (host, m, env) = StartSignals();
        using (env)
        {
            // the faulty module has no summary; read the summary logic through a page that has one
            Assert.Empty(m.GetSummaryReadings());
            host.Shutdown();
        }

        using var env2 = new TestEnv();
        var sample = env2.StartHost("sample");
        var entry = sample.Find("sample")!;
        var readings = entry.GetSummaryReadings();
        var only = Assert.Single(readings);
        Assert.Equal("sample.ticks", only.Id.Value);
        Assert.Equal("Ticks", only.Label);            // the descriptor's key, not the raw id
        Assert.Equal(MetricUnit.Count, only.Unit);
        Assert.Equal("0", only.FormatHint);
        sample.Shutdown();
    }

    [Fact]
    public void Summary_readings_use_the_latest_sample_unit_and_the_raw_id_when_nothing_was_described()
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        var entry = host.Find("sample")!;
        var summary = new PageSummary(null, new[] { new MetricId("zzz.undescribed"), new MetricId("zzz.second"), new MetricId("zzz.third") });

        host.Metrics.Publish(new MetricSample(new MetricId("zzz.undescribed"), 5, MetricUnit.Watt, DateTimeOffset.UnixEpoch, "x"));
        var readings = entry.GetSummaryReadings(summary);

        Assert.Equal(2, readings.Count); // at most two
        Assert.Equal("zzz.undescribed", readings[0].Label);
        Assert.Equal(MetricUnit.Watt, readings[0].Unit);
        Assert.Null(readings[0].FormatHint);
        Assert.Equal(MetricUnit.None, readings[1].Unit);
        Assert.Empty(entry.GetSummaryReadings(null));
        Assert.Empty(entry.GetSummaryReadings(new PageSummary()));
        host.Shutdown();
    }

    [Fact]
    public void Language_checks_still_pass_for_the_sample_and_for_Core()
    {
        // Core's own files are checked strictly by CoreFixesTests; the sample's keys are all present in both languages.
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        foreach (var code in new[] { "en", "vi" })
        {
            host.SetLanguage(code);
            var entry = host.Find("sample")!;
            foreach (var row in entry.Page!.EnumerateRows())
            {
                if (row is CustomRow) continue;
                Assert.True(entry.Localisation.TryGet(row.HeaderKey, out _), $"{code}: {row.HeaderKey}");
                if (row.HardwareEffect is { } e)
                {
                    Assert.True(entry.Localisation.TryGet(e.DescriptionKey, out _), $"{code}: {e.DescriptionKey}");
                    if (e.RevertDescriptionKey is { } rk) Assert.True(entry.Localisation.TryGet(rk, out _), $"{code}: {rk}");
                }
                if (row is ButtonRow { RevertButtonKey: { } bk }) Assert.True(entry.Localisation.TryGet(bk, out _), $"{code}: {bk}");
            }
            Assert.True(entry.Localisation.TryGet("metric.ticks", out _), code);
        }
        host.Shutdown();
    }
}

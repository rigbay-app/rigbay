using System.Text.Json;
using System.Text.RegularExpressions;
using Rigbay.Abstractions;
using Rigbay.PredatorSense.Wmi;
using Xunit;

namespace Rigbay.PredatorSense.Tests;

/// <summary>A module running against fakes: the real module class, a fake firmware, a fake host.</summary>
internal sealed class ModuleRig : IDisposable
{
    public static readonly DateTimeOffset At = new(2026, 11, 1, 18, 42, 0, TimeSpan.FromHours(7));

    public readonly FakeAcerWmi Wmi = new();
    public readonly FakeMachine Machine = new();
    public readonly FakeHost Host;
    public readonly PredatorSenseModule Module;
    private bool _stopped;

    public ModuleRig(string language = "en", bool approve = false, bool initialize = true)
    {
        Host = new FakeHost(language);
        Host.ApprovalService.Answer = approve;
        // What the real fans do (B5).
        Wmi.OnWrite = v =>
        {
            if (v == WmiAllowList.FanMax) Wmi.SetFans(5555, 5882, 5900);
            else if (v == WmiAllowList.FanAuto) Wmi.SetFans(2654, 2631, 2757);
        };
        Module = new PredatorSenseModule(Machine, () => Wmi, () => At, (_, _) => Task.CompletedTask);
        if (initialize)
        {
            Assert.True(Module.Probe().Supported);
            Module.Initialize(Host);
        }
    }

    public PageModel Page => Module.GetPageModel(Host)!;
    public IEnumerable<Row> Rows => Page.EnumerateRows();
    public ChoiceRow FanRow => Rows.OfType<ChoiceRow>().Single();
    public IEnumerable<ValueRow> ValueRows => Rows.OfType<ValueRow>();
    public string ValueOf(string headerKey) => ValueRows.Single(r => r.HeaderKey == headerKey).Value.Value;
    public List<ulong> WrittenValues => Wmi.Writes.Select(w => w.Value).ToList();

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        Module.Shutdown();
    }

    public void Dispose() => Stop();
}

public class ModuleLifecycleTests
{
    [Fact]
    public void Initialize_reads_publishes_and_writes_nothing()
    {
        using var rig = new ModuleRig();
        var ids = rig.Host.MetricsBus.Snapshot().Select(p => p.Id.Value).Distinct().OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "cpu.temp", "fan.cpu.rpm", "fan.gpu1.rpm", "fan.gpu2.rpm", "gpu.temp", "system.temp" }, ids);
        Assert.Equal(0, rig.Wmi.WriteCount);
        Assert.Equal("Sensors live", rig.Host.Loc.Resolve(rig.Page.Summary!.StateLine!.Value));
    }

    [Fact]
    public void The_constructor_does_nothing_and_the_public_one_exists()
    {
        var module = new PredatorSenseModule();   // the one the host calls: must not touch WMI or the registry
        Assert.NotNull(module);
    }

    [Fact]
    public void Probe_of_an_unsupported_machine_is_a_reason_not_an_exception()
    {
        var rig = new ModuleRig(initialize: false);
        rig.Machine.Service = false;
        var r = rig.Module.Probe();
        Assert.False(r.Supported);
        Assert.Equal("probe.no-service", r.ReasonKey);
    }

    [Fact]
    public void The_page_has_the_planned_shape()
    {
        using var rig = new ModuleRig();
        var page = rig.Page;
        Assert.Equal(new[] { "group.readings", "group.cooling", "group.settings" }, page.Groups.Select(g => g.HeaderKey).ToArray());

        var readings = page.Groups[0].Rows.OfType<ReadingRow>().Select(r => r.Metric.Value).ToArray();
        Assert.Equal(new[] { "cpu.temp", "gpu.temp", "system.temp", "fan.cpu.rpm", "fan.gpu1.rpm", "fan.gpu2.rpm" }, readings);

        var choice = rig.FanRow;
        Assert.True(choice.ChangesHardware);
        Assert.Equal("effect.fan-mode", choice.HardwareEffect!.DescriptionKey);
        Assert.Equal(new[] { "auto", "max" }, choice.Options.Select(o => o.Id).ToArray());
        Assert.Equal("auto", choice.Value.Value);

        // Only the fan row changes hardware.
        Assert.Single(page.EnumerateRows(), r => r.ChangesHardware);

        Assert.Equal(new[] { "cpu.temp", "fan.cpu.rpm" }, page.Summary!.Readings!.Select(m => m.Value).ToArray());
        Assert.Empty(page.Links);
    }

    [Fact]
    public void Parts_the_machine_does_not_have_get_no_row()
    {
        var rig = new ModuleRig(initialize: false);
        rig.Wmi.Sensors[9] = FakeAcerWmi.Raw(0);
        rig.Wmi.Sensors[3] = FakeAcerWmi.Raw(0);
        rig.Module.Probe();
        rig.Module.Initialize(rig.Host);
        using var _ = rig;
        var metrics = rig.Page.Groups[0].Rows.OfType<ReadingRow>().Select(r => r.Metric.Value).ToArray();
        Assert.Equal(new[] { "cpu.temp", "gpu.temp", "fan.cpu.rpm", "fan.gpu1.rpm" }, metrics);
    }

    [Fact]
    public void The_model_comes_from_the_machine_not_from_the_code()
    {
        var rig = new ModuleRig(initialize: false);
        rig.Machine.Model = "Predator PH315-55";
        rig.Module.Probe();
        rig.Module.Initialize(rig.Host);
        using var _ = rig;
        Assert.Equal("Predator PH315-55", rig.ValueOf("row.model.header"));

        var unknown = new ModuleRig(initialize: false);
        unknown.Machine.Model = null;
        unknown.Module.Probe();
        unknown.Module.Initialize(unknown.Host);
        using var __ = unknown;
        Assert.Equal("Unknown", unknown.ValueOf("row.model.header"));
    }

    [Fact]
    public void Last_set_starts_as_nothing_and_says_the_laptop_cannot_report_it()
    {
        using var rig = new ModuleRig();
        Assert.Equal("Nothing yet", rig.ValueOf("row.last-set.header"));
        var row = rig.ValueRows.Single(r => r.HeaderKey == "row.last-set.header");
        Assert.Contains("cannot report", rig.Host.Loc.T(row.DescriptionKey!));
    }

    [Fact]
    public async Task Five_failed_polls_show_the_warning_and_refuse_a_max_write()
    {
        using var rig = new ModuleRig(approve: true);
        var info = rig.Rows.OfType<InfoRow>().Single();
        Assert.False(info.IsOpen.Value);

        rig.Wmi.ReadThrows = new AcerWmiException(AcerWmiFailure.Failed, "gone");
        for (int i = 0; i < 5; i++) await rig.Host.CommandRegistry.Run("status");

        Assert.True(info.IsOpen.Value);
        Assert.Equal("Readings not available", rig.Host.Loc.Resolve(rig.Page.Summary!.StateLine!.Value));
        Assert.False(rig.FanRow.Enabled.Value);

        var result = await rig.Host.CommandRegistry.Run("fan", "max");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, rig.Wmi.WriteCount);
        Assert.Empty(rig.Host.ApprovalService.Asked);   // not even asked: the write is not on offer
    }

    [Fact]
    public void Poll_interval_is_clamped_and_saved()
    {
        using var rig = new ModuleRig();
        var row = rig.Rows.OfType<NumberRow>().Single();
        row.Value.Value = 500;
        Assert.Equal(30, row.Value.Value);
        row.Value.Value = 0.2;
        Assert.Equal(1, row.Value.Value);
        var saved = Assert.IsType<PredatorSenseSettings>(rig.Host.SettingsStore.Stored);
        Assert.Equal(1, saved.PollSeconds);
    }

    [Fact]
    public void Settings_are_read_at_start_and_clamped()
    {
        var rig = new ModuleRig(initialize: false);
        rig.Host.SettingsStore.Stored = new PredatorSenseSettings { PollSeconds = 9999, RestoreAutoOnExit = false };
        rig.Module.Probe();
        rig.Module.Initialize(rig.Host);
        using var _ = rig;
        Assert.Equal(30, rig.Rows.OfType<NumberRow>().Single().Value.Value);
        Assert.False(rig.Rows.OfType<ToggleRow>().Single().Value.Value);
    }

    [Fact]
    public void Enabling_again_after_disabling_works_on_the_same_instance()
    {
        using var rig = new ModuleRig();
        rig.Module.Shutdown();
        rig.Host.HotkeyService.Registered.Clear();   // the host removes registrations after Shutdown
        rig.Host.CommandRegistry.Verbs.Clear();
        rig.Module.Initialize(rig.Host);
        Assert.Contains("status", rig.Host.CommandRegistry.Verbs.Keys);
    }
}

public class ModuleFanWriteTests
{
    private static async Task<bool> Eventually(Func<bool> c) => await Task.Run(() => Wait.Until(c));

    [Fact]
    public async Task Choosing_max_in_the_row_sends_exactly_one_max_write()
    {
        using var rig = new ModuleRig();
        rig.FanRow.Value.Value = "max";   // the host does this after its own confirmation
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.ValueOf("row.check.header").Contains("sped up")));
        Assert.Equal(new[] { 0x02820019UL }, rig.WrittenValues.ToArray());
        Assert.Equal("Maximum at 18:42", rig.ValueOf("row.last-set.header"));
        Assert.Equal("GPU fan 2 sped up: 2663 to 5900 rpm", rig.ValueOf("row.check.header"));   // the biggest change is shown
        Assert.Equal("Fans Maximum, set by Rigbay", rig.Host.Loc.Resolve(rig.Page.Summary!.StateLine!.Value));
        Assert.Equal("max", rig.FanRow.Value.Value);
    }

    [Fact]
    public async Task Choosing_auto_again_sends_exactly_one_auto_write()
    {
        using var rig = new ModuleRig();
        rig.FanRow.Value.Value = "max";
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.ValueOf("row.check.header").Contains("sped up")));
        rig.FanRow.Value.Value = "auto";
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 2 && rig.ValueOf("row.check.header").Contains("slowed")));
        Assert.Equal(new[] { 0x02820019UL, 0x01410019UL }, rig.WrittenValues.ToArray());
        Assert.Equal("Auto at 18:42", rig.ValueOf("row.last-set.header"));
    }

    [Fact]
    public async Task A_refused_write_puts_the_row_back_and_says_why()
    {
        using var rig = new ModuleRig();
        rig.Wmi.WriteAnswer = 0x05;
        rig.FanRow.Value.Value = "max";
        Assert.True(await Eventually(() => rig.ValueOf("row.check.header").Contains("Refused by the laptop")));
        Assert.True(await Eventually(() => rig.FanRow.Value.Value == "auto"));
        Assert.Equal("Nothing yet", rig.ValueOf("row.last-set.header"));
        Assert.Equal(1, rig.Wmi.WriteCount);
    }

    [Fact]
    public async Task A_stalled_write_keeps_the_row_on_max_marked_not_confirmed()
    {
        using var rig = new ModuleRig();
        rig.Wmi.WriteThrows = new AcerWmiException(AcerWmiFailure.Timeout, "stalled");
        rig.FanRow.Value.Value = "max";
        Assert.True(await Eventually(() => rig.ValueOf("row.last-set.header").Contains("not confirmed")));
        Assert.Equal("max", rig.FanRow.Value.Value);
        Assert.Equal("Fans Maximum, not confirmed", rig.Host.Loc.Resolve(rig.Page.Summary!.StateLine!.Value));
    }

    // ---- hotkey

    [Fact]
    public void The_hotkey_is_registered_unbound()
    {
        using var rig = new ModuleRig();
        var reg = Assert.Single(rig.Host.HotkeyService.Registered);
        Assert.Equal("toggle-fan", reg.ActionId);
        Assert.Null(reg.Default);   // off by default
        Assert.Contains(rig.Rows.OfType<ShortcutRow>(), r => ReferenceEquals(r.Value, rig.Host.HotkeyService.Bindings[0].Gesture));
    }

    [Fact]
    public async Task Hotkey_declined_makes_no_call()
    {
        using var rig = new ModuleRig(approve: false);
        rig.Host.HotkeyService.Press("toggle-fan");
        Assert.True(await Eventually(() => rig.Host.ApprovalService.Asked.Count == 1));
        await Task.Delay(100);
        Assert.Equal(0, rig.Wmi.WriteCount);
        Assert.Equal("effect.fan-mode", rig.Host.ApprovalService.Asked[0].Effect.DescriptionKey);
        Assert.Equal("Set fan mode to Maximum", rig.Host.Loc.Resolve(rig.Host.ApprovalService.Asked[0].Detail!.Value));
    }

    [Fact]
    public async Task Hotkey_accepted_makes_exactly_one_call_with_the_exact_value_and_toggles_back()
    {
        using var rig = new ModuleRig(approve: true);
        rig.Host.HotkeyService.Press("toggle-fan");
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.ValueOf("row.check.header").Contains("sped up")));
        Assert.Equal(new[] { 0x02820019UL }, rig.WrittenValues.ToArray());
        Assert.Equal("max", rig.FanRow.Value.Value);   // the row follows
        Assert.Single(rig.Host.ToastService.Shown, t => t.Kind == ToastKind.Success);

        rig.Host.HotkeyService.Press("toggle-fan");
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 2 && rig.ValueOf("row.check.header").Contains("slowed")));
        Assert.Equal(new[] { 0x02820019UL, 0x01410019UL }, rig.WrittenValues.ToArray());
        Assert.Equal("Fan mode: Maximum -> Auto", rig.Host.Loc.Resolve(rig.Host.ApprovalService.Asked[1].Detail!.Value));
        Assert.Equal("auto", rig.FanRow.Value.Value);
    }

    // ---- restore on exit

    [Fact]
    public async Task Stopping_after_max_restores_auto_with_exactly_one_auto_write()
    {
        using var rig = new ModuleRig(approve: true);
        rig.Host.HotkeyService.Press("toggle-fan");
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.ValueOf("row.check.header").Contains("sped up")));

        rig.Stop();
        Assert.Equal(new[] { 0x02820019UL, 0x01410019UL }, rig.WrittenValues.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(2), rig.Wmi.Writes[1].Timeout);   // short, the host gives Shutdown 5 s
    }

    [Fact]
    public async Task Stopping_with_the_setting_off_leaves_the_fans_alone()
    {
        using var rig = new ModuleRig(approve: true);
        rig.Rows.OfType<ToggleRow>().Single().Value.Value = false;
        rig.Host.HotkeyService.Press("toggle-fan");
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.ValueOf("row.check.header").Contains("sped up")));
        rig.Stop();
        Assert.Equal(1, rig.Wmi.WriteCount);
        Assert.False(((PredatorSenseSettings)rig.Host.SettingsStore.Stored!).RestoreAutoOnExit);
    }

    [Fact]
    public void Stopping_when_rigbay_set_nothing_writes_nothing()
    {
        using var rig = new ModuleRig(approve: true);
        rig.Stop();
        Assert.Equal(0, rig.Wmi.WriteCount);
    }

    [Fact]
    public async Task What_rigbay_set_is_remembered_across_disable_and_enable()
    {
        using var rig = new ModuleRig(approve: true);
        rig.Rows.OfType<ToggleRow>().Single().Value.Value = false;   // do not restore: simulate a failed restore
        rig.Host.HotkeyService.Press("toggle-fan");
        Assert.True(await Eventually(() => rig.Wmi.WriteCount == 1 && rig.ValueOf("row.check.header").Contains("sped up")));
        rig.Module.Shutdown();
        rig.Host.HotkeyService.Registered.Clear();
        rig.Host.CommandRegistry.Verbs.Clear();
        rig.Module.Initialize(rig.Host);
        Assert.Equal("Maximum at 18:42", rig.ValueOf("row.last-set.header"));
        Assert.Equal("max", rig.FanRow.Value.Value);
    }

    // ---- verbs

    [Fact]
    public async Task Verb_status_prints_the_readings()
    {
        using var rig = new ModuleRig();
        var r = await rig.Host.CommandRegistry.Run("status");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Machine: Predator PT516-51s", r.Output);
        Assert.Contains("CPU temperature: 61 °C", r.Output);
        Assert.Contains("GPU temperature: 52 °C", r.Output);
        Assert.Contains("System temperature: 44 °C", r.Output);
        Assert.Contains("CPU fan: 2564 rpm", r.Output);
        Assert.Contains("GPU fan 1: 2654 rpm", r.Output);
        Assert.Contains("GPU fan 2: 2663 rpm", r.Output);
        Assert.Contains("Fan mode, last set by Rigbay: Nothing yet", r.Output);
        Assert.Equal(0, rig.Wmi.WriteCount);
    }

    [Fact]
    public async Task Verb_status_marks_missing_parts_and_fails_when_nothing_answers()
    {
        using var rig = new ModuleRig();
        rig.Wmi.Sensors[3] = FakeAcerWmi.Raw(0);
        var r = await rig.Host.CommandRegistry.Run("status");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("System temperature: not available", r.Output);

        rig.Wmi.ReadThrows = new AcerWmiException(AcerWmiFailure.Failed, "gone");
        var bad = await rig.Host.CommandRegistry.Run("status");
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains("CPU fan: not available", bad.Output);
    }

    [Theory]
    [InlineData("max")]
    [InlineData("auto")]
    public async Task Verb_fan_not_confirmed_is_exit_1_and_makes_no_call(string mode)
    {
        using var rig = new ModuleRig(approve: false);
        var r = await rig.Host.CommandRegistry.Run("fan", mode);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("Not confirmed. Nothing was changed.", r.Output);
        Assert.Equal(0, rig.Wmi.WriteCount);
        Assert.Single(rig.Host.ApprovalService.Asked);
    }

    [Fact]
    public async Task Verb_fan_max_confirmed_makes_exactly_one_call_with_the_exact_value()
    {
        using var rig = new ModuleRig(approve: true);
        var r = await rig.Host.CommandRegistry.Run("fan", "max");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(new[] { 0x02820019UL }, rig.WrittenValues.ToArray());
        Assert.Contains("Fan mode set to Maximum.", r.Output);
        Assert.Contains("sped up", r.Output);
    }

    [Fact]
    public async Task Verb_fan_auto_confirmed_makes_exactly_one_call_with_the_exact_value()
    {
        using var rig = new ModuleRig(approve: true);
        var r = await rig.Host.CommandRegistry.Run("fan", "auto");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(new[] { 0x01410019UL }, rig.WrittenValues.ToArray());
    }

    [Fact]
    public async Task Verb_fan_that_the_laptop_refuses_is_exit_1()
    {
        using var rig = new ModuleRig(approve: true);
        rig.Wmi.WriteAnswer = 0x03;
        var r = await rig.Host.CommandRegistry.Run("fan", "max");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("status 3", r.Output);
    }

    [Theory]
    [InlineData("")]
    [InlineData("turbo")]
    [InlineData("max auto")]
    [InlineData("0x02820019")]
    public async Task Verb_fan_with_other_arguments_is_usage_exit_2_and_never_asks(string argText)
    {
        using var rig = new ModuleRig(approve: true);
        var r = await rig.Host.CommandRegistry.Run("fan", argText.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(2, r.ExitCode);
        Assert.Equal("Usage: rigbay predatorsense fan auto|max", r.Output);
        Assert.Empty(rig.Host.ApprovalService.Asked);
        Assert.Equal(0, rig.Wmi.WriteCount);
    }

    [Fact]
    public void Only_the_two_verbs_exist()
    {
        using var rig = new ModuleRig();
        Assert.Equal(new[] { "fan", "status" }, rig.Host.CommandRegistry.Verbs.Keys.OrderBy(k => k).ToArray());
    }
}

public class LanguageAndManifestTests
{
    private static readonly string[] Languages = { "en", "vi" };

    [Fact]
    public void Both_files_have_the_same_keys_and_placeholders_and_the_module_prefix()
    {
        var en = FakeLocalisation.Load("en");
        var vi = FakeLocalisation.Load("vi");
        Assert.All(en.Keys.Concat(vi.Keys), k => Assert.StartsWith("module.predatorsense.", k));
        Assert.Equal(en.Keys.OrderBy(k => k), vi.Keys.OrderBy(k => k));
        foreach (var (key, text) in en)
            Assert.Equal(Placeholders(text), Placeholders(vi[key]));
    }

    private static string Placeholders(string text) =>
        string.Join(",", Regex.Matches(text, @"\{(\d+)\}").Select(m => m.Groups[1].Value).OrderBy(s => s));

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    public void Every_key_the_page_the_verbs_and_the_states_use_resolves(string lang)
    {
        using var rig = new ModuleRig(lang);
        var table = FakeLocalisation.Load(lang);
        void Has(string? key)
        {
            if (string.IsNullOrEmpty(key)) return;
            Assert.True(table.ContainsKey("module.predatorsense." + key), "missing key " + key);
        }

        var page = rig.Page;
        foreach (var g in page.Groups) Has(g.HeaderKey);
        foreach (var row in page.EnumerateRows())
        {
            Has(row.HeaderKey);
            Has(row.DescriptionKey);
            Has(row.HardwareEffect?.DescriptionKey);
            if (row is ChoiceRow c) foreach (var o in c.Options) Has(o.LabelKey);
        }
        foreach (var verb in rig.Host.CommandRegistry.Verbs.Values) Has(verb.DescriptionKey);
        foreach (var reg in rig.Host.HotkeyService.Registered) Has(reg.NameKey);
        Has(page.Summary!.StateLine!.Value.Key);
    }

    [Fact]
    public async Task Texts_are_translated_when_the_language_is_vietnamese()
    {
        using var rig = new ModuleRig("vi", approve: true);
        Assert.Equal("Chưa đặt", rig.ValueOf("row.last-set.header"));
        var status = await rig.Host.CommandRegistry.Run("status");
        Assert.Contains("Quạt CPU: 2564 rpm", status.Output);
    }

    [Fact]
    public void A_language_switch_refreshes_the_texts()
    {
        using var rig = new ModuleRig("en");
        Assert.Equal("Nothing yet", rig.ValueOf("row.last-set.header"));
        rig.Host.Loc.Switch("vi");
        Assert.Equal("Chưa đặt", rig.ValueOf("row.last-set.header"));
    }

    [Fact]
    public void Manifest_is_honest_and_matches_the_contract()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "module", "module.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("manifestVersion").GetInt32());
        Assert.Equal("predatorsense", root.GetProperty("id").GetString());
        Assert.Equal("cooling", root.GetProperty("group").GetString());
        Assert.Equal(">=0.1 <0.2", root.GetProperty("hostApi").GetString());
        Assert.Equal("Rigbay.PredatorSense.PredatorSenseModule", root.GetProperty("entry").GetProperty("type").GetString());
        var requires = root.GetProperty("requires");
        Assert.True(requires.GetProperty("admin").GetBoolean());
        var services = string.Join("|", requires.GetProperty("services").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("root\\WMI", services);
        Assert.Contains("PSSvc", services);
        Assert.True(root.GetProperty("description").GetString()!.Length <= 200);
        Assert.Empty(root.GetProperty("requires").GetProperty("network").EnumerateArray());
        // The entry type exists in the assembly under test.
        Assert.NotNull(typeof(PredatorSenseModule).Assembly.GetType(root.GetProperty("entry").GetProperty("type").GetString()!));
    }
}

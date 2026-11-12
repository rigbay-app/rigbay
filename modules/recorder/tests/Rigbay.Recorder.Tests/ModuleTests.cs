using System.Text.Json;
using System.Text.RegularExpressions;
using Rigbay.Abstractions;
using Rigbay.Recorder.Recording;

namespace Rigbay.Recorder.Tests;

/// <summary>The module on a fake host. Open-ended recordings freeze after a few waits (see <see cref="FakeClock.BlockAfter"/>).</summary>
internal sealed class ModuleRig : IDisposable
{
    public readonly TempFolder Folder = new();
    public readonly FakeClock Clock = new();
    public readonly FakeHost Host;
    public readonly FakeDisk Disk = new();
    public readonly FakeShell Shell = new();
    public readonly FakeCatalog Catalog = new();
    public RecorderModule Module;

    public ModuleRig(string language = "en", bool initialize = true)
    {
        Clock.BlockAfter = 3;
        Host = new FakeHost(Clock, language);
        Module = NewModule();
        if (initialize) Module.Initialize(Host);
    }

    public RecorderModule NewModule() => new(h => new RecorderServices
    {
        Clock = Clock.Clock,
        Delay = Clock.Delay,
        Disk = Disk,
        Machine = new FakeMachine(),
        Versions = new FakeVersions(),
        Catalog = () => Catalog,
        OwnModuleId = h.ModuleId,
        ToolVersion = "0.1.0",
        HostVersion = "0.1.0",
    }, Shell, () => Folder.Path);

    public FakeBus Bus => Host.MetricsBus;

    public void PublishSome()
    {
        Bus.PublishFrom("zeta", "z.b", 20, MetricUnit.Watt);
        Bus.PublishFrom("zeta", "z.a", 10, MetricUnit.Celsius);
        Bus.PublishFrom("alpha", "a.x", 5, MetricUnit.Percent);
    }

    public PageModel Page() => Module.GetPageModel(Host)!;
    public T Row<T>(string headerKey) where T : Row => Page().EnumerateRows().OfType<T>().Single(r => r.HeaderKey == headerKey);
    public static string Text(ValueRow row) => row.Value.Value;
    public string Value(string headerKey) => Row<ValueRow>(headerKey).Value.Value;

    /// <summary>The live line under the main button (Start or Stop), as the host resolves it now.</summary>
    public string PrimaryLine => Host.Loc.Resolve(Page().Primary!.Line!.Value);
    /// <summary>The dashboard's state line, as the host resolves it now.</summary>
    public string StateText => Host.Loc.Resolve(Page().Summary!.StateLine!.Value);
    /// <summary>"12 of 40": the line of the Selected metrics row.</summary>
    public string PickedLine => Host.Loc.Resolve(Row<ButtonRow>("row.picked.header").Line!.Value);

    public ActionResult Press(string headerKey) => Invoke(Row<ButtonRow>(headerKey));

    public static ActionResult Invoke(ButtonRow row) => row.InvokeAsync(CancellationToken.None).GetAwaiter().GetResult();

    public void SetChoice(string headerKey, string id) => Row<ChoiceRow>(headerKey).Value.Value = id;

    /// <summary>What the host does between a Shutdown and the next Initialize: forget the old registrations. Then the next module starts.</summary>
    public void Restart(RecorderModule next)
    {
        Module.Shutdown();
        Host.CommandRegistry.Verbs.Clear();
        Host.HotkeyService.Registered.Clear();
        Host.TrayService.Items.Clear();
        Module = next;
        next.Initialize(Host);
    }

    /// <summary>The recording is over and the module has reacted: IsRecording turns false just before the Ended handlers run, and the last thing they do is ask for a page refresh.</summary>
    public bool WaitUntilFinished() => Wait.Until(() => !Module.Engine!.IsRecording) && Wait.Until(() => Host.PageRefreshRequests >= 1);

    public bool WaitUntilFrozen() => Clock.WaitUntilBlocked() && Wait.Until(() => Module.Engine!.Status().Rows >= 4);

    public string[] CsvLines() => Files.Lines(Folder.Csvs.Single());

    public void Dispose()
    {
        try { Module.Shutdown(); } catch (Exception) { /* a test that shut it down already */ }
        Folder.Dispose();
    }
}

public class ModuleLifecycleTests
{
    [Fact]
    public void Every_machine_is_supported()
    {
        using var rig = new ModuleRig(initialize: false);
        Assert.True(rig.Module.Probe().Supported);
    }

    [Fact]
    public void Initialize_registers_the_verbs_the_tray_items_the_hotkey_and_the_metric_label()
    {
        using var rig = new ModuleRig();
        Assert.Equal(new[] { "start", "status", "stop" }, rig.Host.CommandRegistry.Verbs.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(new[] { "start", "stop" }, rig.Host.TrayService.Items.Select(i => i.Id).ToArray());
        Assert.True(rig.Host.TrayService.Item("start").Visible.Value);
        Assert.False(rig.Host.TrayService.Item("stop").Visible.Value);

        var hotkey = Assert.Single(rig.Host.HotkeyService.Registered);
        Assert.Equal("toggle", hotkey.ActionId);
        Assert.Null(hotkey.Default);   // off by default

        var descriptor = Assert.Single(rig.Bus.Descriptors);
        Assert.Equal("recorder.rows", descriptor.Id.Value);
        Assert.Equal(MetricUnit.Count, descriptor.Unit);
        Assert.True(rig.Host.Loc.Has(descriptor.LabelKey));
        Assert.True(rig.Host.Loc.Has(descriptor.DescriptionKey!));
    }

    [Fact]
    public void While_it_is_idle_the_recorder_does_not_listen_to_the_bus()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        _ = rig.Page();
        Assert.Equal(0, rig.Bus.Subscribers);   // otherwise every sensor module would poll fast for nothing
    }

    [Fact]
    public void Shutdown_during_a_recording_closes_the_file_withdraws_the_metric_and_the_module_can_start_again()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        Assert.True(rig.Module.StartRecording(rig.Module.RequestFromSettings()).Started);
        Assert.True(rig.WaitUntilFrozen());
        var csv = rig.Folder.Csvs.Single();

        rig.Module.Shutdown();

        Assert.Null(rig.Module.Engine);
        using (new FileStream(csv, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }   // closed
        var meta = JsonDocument.Parse(Files.ReadBytes(Path.ChangeExtension(csv, null) + ".meta.json")).RootElement;
        Assert.Equal("module-stopped", meta.GetProperty("stop_reason").GetString());
        Assert.Contains(new MetricId("recorder.rows"), rig.Bus.Withdrawn);
        Assert.Equal(0, rig.Bus.Subscribers);

        rig.Clock.Now = rig.Clock.Now.AddMinutes(1);
        rig.Host.CommandRegistry.Verbs.Clear();   // the host removes registrations after Shutdown
        rig.Host.HotkeyService.Registered.Clear();
        rig.Host.TrayService.Items.Clear();
        rig.Module.Initialize(rig.Host);          // enabling again calls Initialize on the same instance
        Assert.True(rig.Module.StartRecording(rig.Module.RequestFromSettings()).Started);
    }

    [Fact]
    public void Calls_after_shutdown_fail_politely_and_never_throw()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        var page = rig.Page();
        var start = page.EnumerateRows().OfType<ButtonRow>().First(r => r.HeaderKey == "row.start.header");
        rig.Module.Shutdown();
        var result = ModuleRig.Invoke(start);
        Assert.False(result.Succeeded);
        Assert.Equal("cmd.not-running", result.Message!.Value.Key);
        rig.Host.TrayService.Items.ToList().ForEach(i => i.Invoke());
        rig.Host.HotkeyService.Registered.ToList().ForEach(r => r.Invoke());
        Assert.Equal(1, rig.Host.CommandRegistry.RunSync("stop").ExitCode);
    }

    [Fact]
    public void The_language_switch_changes_the_texts_the_page_shows()
    {
        using var rig = new ModuleRig();
        Assert.Equal("Not recording", rig.StateText);
        Assert.Equal("Start recording", rig.Host.Loc.T(((ButtonRow)rig.Page().Primary!).ButtonKey));
        rig.Host.Loc.Switch("vi");
        Assert.Equal("Đang dừng", rig.StateText);
        Assert.Equal("Bắt đầu ghi", rig.Host.Loc.T(((ButtonRow)rig.Page().Primary!).ButtonKey));
    }

    [Fact]
    public void Settings_that_were_edited_by_hand_are_brought_into_range()
    {
        var s = new RecorderSettings { PeriodSeconds = 3, DurationMinutes = 7, Folder = "  ", ExcludedMetrics = new List<string> { "a.x", "", "a.x", " " } }.Clamped();
        Assert.Equal(2, s.PeriodSeconds);      // 3 is as close to 2 as to 5: the lower offer wins
        Assert.Equal(5, s.DurationMinutes);
        Assert.Equal("", s.Folder);
        Assert.Equal(new[] { "a.x" }, s.ExcludedMetrics.ToArray());
        Assert.Equal(10, new RecorderSettings { PeriodSeconds = 99999 }.Clamped().PeriodSeconds);
        Assert.Equal(1, new RecorderSettings { PeriodSeconds = -4 }.Clamped().PeriodSeconds);
    }

    [Fact]
    public void English_and_Vietnamese_have_the_same_keys_and_placeholders()
    {
        var en = FakeLocalisation.Load("en");
        var vi = FakeLocalisation.Load("vi");
        Assert.Empty(en.Keys.Except(vi.Keys));
        Assert.Empty(vi.Keys.Except(en.Keys));
        foreach (var (key, text) in en)
            Assert.Equal(Placeholders(text), Placeholders(vi[key]));

        static string Placeholders(string text) => string.Join(",", Regex.Matches(text, @"\{(\d+)\}").Select(m => m.Groups[1].Value).OrderBy(x => x));
    }
}

public class PageTests
{
    private static IEnumerable<string> Keys(PageModel page)
    {
        foreach (var group in page.Groups)
        {
            yield return group.HeaderKey;
            foreach (var row in group.Rows) foreach (var key in RowKeys(row)) yield return key;
        }
        foreach (var row in page.EnumerateRows()) foreach (var key in RowKeys(row)) yield return key;
    }

    private static IEnumerable<string> RowKeys(Row row)
    {
        yield return row.HeaderKey;
        if (row.DescriptionKey is not null) yield return row.DescriptionKey;
        switch (row)
        {
            case ButtonRow b: yield return b.ButtonKey; break;
            case TextRow t when t.PlaceholderKey is not null: yield return t.PlaceholderKey; break;
            case ChoiceRow c: foreach (var o in c.Options) yield return o.LabelKey; break;
        }
    }

    [Fact]
    public void Every_key_on_the_page_is_in_the_language_file_or_literal_text()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        // with a finished recording too, so the History rows are in
        Assert.True(rig.Module.StartRecording(rig.Module.RequestFromSettings()).Started);
        Assert.True(rig.WaitUntilFrozen());
        rig.Module.Engine!.Stop();
        var missing = Keys(rig.Page()).Where(k => !Text.IsLiteral(k) && !rig.Host.Loc.Has(k)).Distinct().ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void The_page_has_the_main_button_first_then_options_metrics_and_history()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        var page = rig.Page();
        // Review RC-1: one big button whose label is its state, with a live line; no status rows of dashes.
        var primary = Assert.IsType<ButtonRow>(page.Primary);
        Assert.Equal("row.start.header", primary.HeaderKey);
        Assert.Equal("row.start.button", primary.ButtonKey);
        Assert.NotNull(primary.Line);
        Assert.Equal("", rig.PrimaryLine);   // nothing to say before the first recording
        Assert.Equal(new[] { "group.options", "group.metrics", "group.history" }, page.Groups.Select(g => g.HeaderKey).ToArray());
        Assert.Null(page.Summary!.Readings);   // the bay shows the state line only
        Assert.Equal("state.idle", page.Summary.StateLine!.Value.Key);
        Assert.Empty(page.EnumerateRows().OfType<ValueRow>());
        Assert.DoesNotContain(page.EnumerateRows(), r => r.HeaderKey == "row.stop.header");

        var options = page.Groups[0].Rows;
        Assert.Equal(new[] { "row.period.header", "row.duration.header", "row.folder.header" }, options.Take(3).Select(r => r.HeaderKey).ToArray());
        Assert.Contains(options, r => r is ShortcutRow);
        // the folder in one row: Open in its header, where the files go in its line, the path inside
        var folder = Assert.IsType<ExpanderRow>(options[2]);
        Assert.Equal("row.open-folder.header", Assert.IsType<ButtonRow>(folder.HeaderRow).HeaderKey);
        Assert.Equal("row.folder-path.header", Assert.IsType<TextRow>(Assert.Single(folder.Children)).HeaderKey);
        Assert.Equal(@"Documents\Rigbay\logs (default)", rig.Host.Loc.Resolve(folder.Line!.Value));

        foreach (var row in page.EnumerateRows().Where(r => r.DescriptionKey is not null && !Text.IsLiteral(r.DescriptionKey)))
            Assert.InRange(rig.Host.Loc.T(row.DescriptionKey!).Length, 1, 70);   // the copy rule: a short fact, the rest in the help glyph
    }

    [Fact]
    public void Starting_and_stopping_the_recording_is_not_a_hardware_change()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        Assert.All(rig.Page().EnumerateRows(), r => Assert.False(r.ChangesHardware));
        Assert.Equal(0, rig.Host.ApprovalService.Asked);
    }

    [Fact]
    public void The_period_choices_are_one_two_five_and_ten_seconds()
    {
        using var rig = new ModuleRig();
        var period = rig.Row<ChoiceRow>("row.period.header");
        Assert.Equal(new[] { "1", "2", "5", "10" }, period.Options.Select(o => o.Id).ToArray());
        Assert.Equal("1", period.Value.Value);
        var duration = rig.Row<ChoiceRow>("row.duration.header");
        Assert.Equal("0", duration.Options[0].Id);   // unlimited first
        Assert.Equal("duration.0", duration.Options[0].LabelKey);
        Assert.Equal("0", duration.Value.Value);
    }

    [Fact]
    public void Start_and_stop_take_turns_as_the_main_button()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        var start = Assert.IsType<ButtonRow>(rig.Page().Primary);
        Assert.Equal("row.start.header", start.HeaderKey);
        Assert.True(start.Enabled.Value);
        int refreshes = rig.Host.PageRefreshRequests;

        var result = ModuleRig.Invoke(start);
        Assert.True(result.Succeeded);
        Assert.Matches(@"^rigbay-log-.*\.csv$", (string)result.Message!.Value.Args![0]);
        Assert.True(rig.WaitUntilFrozen());
        Assert.True(rig.Host.PageRefreshRequests > refreshes);   // the host is asked to draw Stop in its place
        var stop = Assert.IsType<ButtonRow>(rig.Page().Primary);
        Assert.Equal("row.stop.header", stop.HeaderKey);
        Assert.Equal("row.stop.button", stop.ButtonKey);
        Assert.False(start.Enabled.Value);
        Assert.True(stop.Enabled.Value);
        Assert.False(rig.Host.TrayService.Item("start").Visible.Value);
        Assert.True(rig.Host.TrayService.Item("stop").Visible.Value);

        var stopped = ModuleRig.Invoke(stop);
        Assert.True(stopped.Succeeded);
        Assert.Same(start, rig.Page().Primary);   // the same row instance comes back, so the host keeps its state
        Assert.True(start.Enabled.Value);
        Assert.False(stop.Enabled.Value);
        Assert.True(rig.Host.TrayService.Item("start").Visible.Value);
        Assert.False(rig.Host.TrayService.Item("stop").Visible.Value);

        var again = ModuleRig.Invoke(stop);   // nothing to stop
        Assert.False(again.Succeeded);
        Assert.Equal("stop.not-recording", again.Message!.Value.Key);
    }

    [Fact]
    public void The_main_line_and_the_dashboard_line_follow_the_recording()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        Assert.Equal("", rig.PrimaryLine);   // no row of dashes before the first recording

        var summary = rig.Page().Summary!;
        Assert.True(rig.Module.StartRecording(rig.Module.RequestFromSettings()).Started);
        Assert.True(rig.WaitUntilFrozen());
        rig.Module.RefreshTexts();

        // file · elapsed · rows · size, in the line under Stop
        var size = CsvFormat.Size(new FileInfo(rig.Folder.Csvs.Single()).Length);
        Assert.Matches(@"^rigbay-log-.*\.csv · 00:00:03 · 4 rows · " + System.Text.RegularExpressions.Regex.Escape(size) + "$", rig.PrimaryLine);
        Assert.Equal("Recording 00:00:03", rig.StateText);
        Assert.Equal("state.recording-line", summary.StateLine!.Value.Key);
        Assert.Equal("00:00:03", summary.StateLine.Value.Args![0]);

        // the module's own metric, for the dashboard bay
        var published = rig.Bus.OwnPublished.Where(p => p.Id.Value == "recorder.rows").Select(p => p.Value).ToList();
        Assert.Equal(4.0, published[^1]);
        Assert.Equal(published.OrderBy(v => v).ToList(), published);   // counts up, never back

        rig.Module.Engine!.Stop();
        Assert.Equal("Not recording", rig.StateText);
        Assert.StartsWith("Stopped. rigbay-log-", rig.PrimaryLine);         // how the last recording ended stays in the line
        Assert.EndsWith("was saved with 4 rows.", rig.PrimaryLine);
        Assert.Equal("state.idle", summary.StateLine.Value.Key);
        Assert.Contains(new MetricId("recorder.rows"), rig.Bus.Withdrawn);
        Assert.True(rig.Host.PageRefreshRequests >= 1);   // the History group has a new entry
    }

    [Fact]
    public void A_time_limit_shows_a_notification_and_the_recording_appears_in_the_history()
    {
        using var rig = new ModuleRig();
        rig.Clock.BlockAfter = int.MaxValue;
        rig.PublishSome();
        rig.SetChoice("row.duration.header", "1");
        Assert.True(rig.Press("row.start.header").Succeeded);
        Assert.True(rig.WaitUntilFinished());

        var toast = Assert.Single(rig.Host.ToastService.Shown);
        Assert.Equal(ToastKind.Success, toast.Kind);
        Assert.Equal("toast.ended", toast.Body.Key);
        Assert.Equal(60L, toast.Body.Args![1]);
        Assert.StartsWith("Time limit reached.", rig.PrimaryLine);

        var history = rig.Page().Groups[2].Rows;
        var entry = Assert.IsType<ButtonRow>(Assert.Single(history));
        Assert.True(Text.IsLiteral(entry.HeaderKey));
        Assert.Matches(@"^=rigbay-log-.*\.csv$", entry.HeaderKey);
        Assert.Matches(@"^=\d{4}-\d\d-\d\d \d\d:\d\d · \d+(\.\d)? (B|KB)$", entry.DescriptionKey);
        Assert.True(ModuleRig.Invoke(entry).Succeeded);
        Assert.Equal(rig.Folder.Csvs.Single(), Assert.Single(rig.Shell.Files));
    }

    [Fact]
    public void The_history_is_capped_and_the_file_being_written_is_not_in_it()
    {
        using var rig = new ModuleRig();
        for (int i = 0; i < 14; i++)
            File.WriteAllText(Path.Combine(rig.Folder.Path, $"rigbay-log-2026-10-{i + 1:00}_08-00-00.csv"), "old");
        rig.PublishSome();
        Assert.Equal(History.Limit, rig.Page().Groups[2].Rows.Count);
        Assert.True(rig.Module.StartRecording(rig.Module.RequestFromSettings()).Started);
        Assert.True(rig.WaitUntilFrozen());
        var current = Path.GetFileName(rig.Module.Engine!.Status().Path!);
        var rows = rig.Page().Groups[2].Rows.OfType<ButtonRow>().ToList();
        Assert.Equal(History.Limit, rows.Count);
        Assert.DoesNotContain(rows, r => r.HeaderKey == "=" + current);
        Assert.Equal(15, Directory.GetFiles(rig.Folder.Path, "*.csv").Length);   // nothing deleted, nothing hidden from disk
    }

    [Fact]
    public void An_empty_history_says_so()
    {
        using var rig = new ModuleRig();
        var info = Assert.IsType<InfoRow>(Assert.Single(rig.Page().Groups[2].Rows));
        Assert.Equal("row.history-empty.header", info.HeaderKey);
    }

    [Fact]
    public void The_start_button_says_why_when_it_cannot_start()
    {
        using var rig = new ModuleRig();
        // nothing on the bus
        var none = rig.Press("row.start.header");
        Assert.False(none.Succeeded);
        Assert.Equal("start.no-metrics", none.Message!.Value.Key);
        Assert.StartsWith("There is nothing to record", rig.PrimaryLine);

        rig.PublishSome();
        rig.Disk.Free = 20L * 1024 * 1024;
        var full = rig.Press("row.start.header");
        Assert.False(full.Succeeded);
        Assert.Equal("start.disk-low", full.Message!.Value.Key);
        Assert.Equal("Not enough free space on that drive: 20 MB free, at least 200 MB needed.", rig.PrimaryLine);
        Assert.Empty(rig.Folder.Csvs);
        Assert.False(rig.Module.Engine!.IsRecording);
    }

    [Fact]
    public void Running_out_of_space_tells_the_user_with_a_warning()
    {
        using var rig = new ModuleRig();
        rig.Clock.BlockAfter = int.MaxValue;
        rig.PublishSome();
        rig.Clock.OnDelay = n => { if (n == 3) rig.Disk.Free = 10L * 1024 * 1024; };
        Assert.True(rig.Press("row.start.header").Succeeded);
        Assert.True(rig.WaitUntilFinished());
        var toast = Assert.Single(rig.Host.ToastService.Shown);
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.Equal("toast.disk-low", toast.Body.Key);
        Assert.StartsWith("Stopped because less than 50 MB was free (10 MB).", rig.PrimaryLine);
        Assert.EndsWith("was saved with 3 rows.", rig.PrimaryLine);
    }

    [Fact]
    public void A_write_error_tells_the_user_with_an_error()
    {
        using var rig = new ModuleRig();
        var module = new RecorderModule(h => new RecorderServices
        {
            Clock = rig.Clock.Clock, Delay = rig.Clock.Delay, Disk = rig.Disk, Machine = new FakeMachine(), Versions = new FakeVersions(),
            OwnModuleId = h.ModuleId,
            CreateFile = p => new FailingStream(new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.Read), 3),
        }, rig.Shell, () => rig.Folder.Path);
        rig.Restart(module);
        rig.Clock.BlockAfter = int.MaxValue;
        rig.PublishSome();
        Assert.True(module.StartRecording(module.RequestFromSettings()).Started);
        Assert.True(rig.WaitUntilFinished());
        var toast = Assert.Single(rig.Host.ToastService.Shown);
        Assert.Equal(ToastKind.Error, toast.Kind);
        Assert.Equal("toast.write-error", toast.Body.Key);
        Assert.Contains("could not be written", rig.PrimaryLine);
    }
}

public class SettingsAndPickerTests
{
    [Fact]
    public void Period_duration_and_folder_are_saved_and_come_back()
    {
        using var rig = new ModuleRig();
        rig.SetChoice("row.period.header", "5");
        rig.SetChoice("row.duration.header", "30");
        rig.Row<TextRow>("row.folder-path.header").Value.Value = @"D:\Elsewhere\Logs";
        var saved = Assert.IsType<RecorderSettings>(rig.Host.SettingsStore.Stored);
        Assert.Equal(5, saved.PeriodSeconds);
        Assert.Equal(30, saved.DurationMinutes);
        Assert.Equal(@"D:\Elsewhere\Logs", saved.Folder);

        rig.Restart(rig.NewModule());
        Assert.Equal("5", rig.Row<ChoiceRow>("row.period.header").Value.Value);
        Assert.Equal("30", rig.Row<ChoiceRow>("row.duration.header").Value.Value);
        Assert.Equal(@"D:\Elsewhere\Logs", rig.Row<TextRow>("row.folder-path.header").Value.Value);
    }

    [Fact]
    public void The_choices_reach_the_recording()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        rig.SetChoice("row.period.header", "10");
        rig.SetChoice("row.duration.header", "60");
        Assert.True(rig.Module.StartRecording(rig.Module.RequestFromSettings()).Started);
        var status = rig.Module.Engine!.Status();
        Assert.Equal(TimeSpan.FromSeconds(10), status.Period);
        Assert.Equal(TimeSpan.FromHours(1), status.Duration);
    }

    [Fact]
    public void The_folder_row_wants_a_full_path_or_nothing_and_waits_for_Apply()
    {
        using var rig = new ModuleRig();
        var folder = rig.Row<TextRow>("row.folder-path.header");
        Assert.Equal(ApplyMode.ApplyRevert, folder.Apply);
        Assert.Null(folder.Validate!(""));
        Assert.Null(folder.Validate!(@"D:\Logs"));
        Assert.Null(folder.Validate!(@"\\server\share\logs"));
        Assert.Equal("error.folder-absolute", folder.Validate!(@"logs\here")!.Value.Key);
        Assert.Equal("error.folder-absolute", folder.Validate!("D:logs")!.Value.Key);
        Assert.Equal("error.folder-absolute", folder.Validate!("bad\0path")!.Value.Key);
    }

    [Fact]
    public void A_recording_goes_to_the_chosen_folder_and_the_history_lists_that_folder()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        var other = Path.Combine(rig.Folder.Path, "chosen", "deeper");
        rig.Row<TextRow>("row.folder-path.header").Value.Value = other;
        Assert.True(rig.Module.StartRecording(rig.Module.RequestFromSettings()).Started);
        Assert.True(rig.WaitUntilFrozen());
        rig.Module.Engine!.Stop();
        Assert.Single(Directory.GetFiles(other, "*.csv"));
        Assert.Empty(Directory.GetFiles(rig.Folder.Path, "*.csv"));
        Assert.Single(rig.Page().Groups[2].Rows.OfType<ButtonRow>());
    }

    [Fact]
    public void Open_folder_creates_the_folder_if_needed_and_shows_it()
    {
        using var rig = new ModuleRig();
        var target = Path.Combine(rig.Folder.Path, "not-yet");
        rig.Row<TextRow>("row.folder-path.header").Value.Value = target;
        Assert.True(rig.Press("row.open-folder.header").Succeeded);
        Assert.Equal(target, Assert.Single(rig.Shell.Folders));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Open_folder_that_fails_is_a_result_not_an_exception()
    {
        using var rig = new ModuleRig();
        rig.Shell.Throws = new InvalidOperationException("no explorer");
        var result = rig.Press("row.open-folder.header");
        Assert.False(result.Succeeded);
        Assert.Equal("open.failed", result.Message!.Value.Key);
        Assert.True(rig.Host.LogSink.Count(LogLevel.Warn) >= 1);
    }

    [Fact]
    public void Every_metric_is_picked_by_default_and_grouped_by_the_module_that_publishes_it()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        var rows = rig.Page().Groups[1].Rows;
        var groups = rows.OfType<ExpanderRow>().ToList();
        Assert.Equal(new[] { "=alpha", "=zeta" }, groups.Select(g => g.HeaderKey).ToArray());
        var toggles = groups.SelectMany(g => g.Children).OfType<ToggleRow>().ToList();
        Assert.Equal(3, toggles.Count);
        Assert.All(toggles, t => Assert.True(t.Value.Value));
        Assert.Equal(new[] { "=a.x", "=z.a", "=z.b" }, toggles.Select(t => t.HeaderKey).ToArray());   // nobody described them: the id
        Assert.Equal("=a.x · %", toggles[0].DescriptionKey);
        Assert.Equal("=z.a · °C", toggles[1].DescriptionKey);
        Assert.Equal(new[] { "1 of 1", "2 of 2" }, groups.Select(g => rig.Host.Loc.Resolve(g.Line!.Value)).ToArray());   // each source says its count while closed
        Assert.Equal("3 of 3", rig.PickedLine);
    }

    [Fact]
    public void The_picker_uses_the_label_the_catalog_gives()
    {
        using var rig = new ModuleRig();
        rig.Catalog.Texts["z.a"] = new MetricText("CPU temperature", "x");
        rig.PublishSome();
        var toggles = rig.Page().Groups[1].Rows.OfType<ExpanderRow>().SelectMany(g => g.Children).OfType<ToggleRow>().ToList();
        Assert.Contains(toggles, t => t.HeaderKey == "=CPU temperature");
    }

    [Fact]
    public void A_metric_switched_off_is_saved_and_left_out_of_the_recording_but_a_new_one_is_in_by_default()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        var toggle = rig.Page().Groups[1].Rows.OfType<ExpanderRow>().SelectMany(g => g.Children).OfType<ToggleRow>().Single(t => t.HeaderKey == "=z.a");
        toggle.Value.Value = false;
        Assert.Equal(new[] { "z.a" }, ((RecorderSettings)rig.Host.SettingsStore.Stored!).ExcludedMetrics.ToArray());
        Assert.Equal("2 of 3", rig.PickedLine);

        rig.Bus.PublishFrom("alpha", "a.new", 1);   // a metric nobody has seen yet
        Assert.True(rig.Module.StartRecording(rig.Module.RequestFromSettings()).Started);
        Assert.True(rig.WaitUntilFrozen());
        Assert.Equal("timestamp,elapsed_s,a.new,a.x,z.b", rig.CsvLines()[0]);
    }

    [Fact]
    public void The_choice_of_metrics_survives_turning_the_module_off_and_on()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        rig.Page().Groups[1].Rows.OfType<ExpanderRow>().SelectMany(g => g.Children).OfType<ToggleRow>().Single(t => t.HeaderKey == "=z.b").Value.Value = false;
        rig.Restart(rig.NewModule());
        var toggles = rig.Page().Groups[1].Rows.OfType<ExpanderRow>().SelectMany(g => g.Children).OfType<ToggleRow>().ToList();
        Assert.False(toggles.Single(t => t.HeaderKey == "=z.b").Value.Value);
        Assert.True(toggles.Single(t => t.HeaderKey == "=z.a").Value.Value);
    }

    [Fact]
    public void Select_all_and_clear_selection_change_every_switch_and_the_saved_choice()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        Assert.True(rig.Press("row.pick-none.header").Succeeded);
        var toggles = rig.Page().Groups[1].Rows.OfType<ExpanderRow>().SelectMany(g => g.Children).OfType<ToggleRow>().ToList();
        Assert.All(toggles, t => Assert.False(t.Value.Value));
        Assert.Equal(new[] { "a.x", "z.a", "z.b" }, ((RecorderSettings)rig.Host.SettingsStore.Stored!).ExcludedMetrics.OrderBy(x => x).ToArray());
        Assert.Equal("0 of 3", rig.PickedLine);

        var nothing = rig.Press("row.start.header");
        Assert.False(nothing.Succeeded);
        Assert.Equal("start.no-metrics", nothing.Message!.Value.Key);

        Assert.True(rig.Press("row.picked.header").Succeeded);   // "Select all" sits on the count's row
        Assert.All(toggles, t => Assert.True(t.Value.Value));
        Assert.Empty(((RecorderSettings)rig.Host.SettingsStore.Stored!).ExcludedMetrics);
        Assert.True(rig.Press("row.start.header").Succeeded);
    }

    [Fact]
    public void The_picker_cannot_be_changed_while_recording()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        var page = rig.Page();
        var toggles = page.Groups[1].Rows.OfType<ExpanderRow>().SelectMany(g => g.Children).OfType<ToggleRow>().ToList();
        var period = page.EnumerateRows().OfType<ChoiceRow>().First(r => r.HeaderKey == "row.period.header");
        Assert.All(toggles, t => Assert.True(t.Enabled.Value));
        Assert.True(rig.Module.StartRecording(rig.Module.RequestFromSettings()).Started);
        Assert.All(toggles, t => Assert.False(t.Enabled.Value));
        Assert.False(period.Enabled.Value);
        rig.Module.Engine!.Stop();
        Assert.All(toggles, t => Assert.True(t.Enabled.Value));
    }

    [Fact]
    public void Without_any_metric_the_page_says_so_instead_of_an_empty_list()
    {
        using var rig = new ModuleRig();
        var info = Assert.IsType<InfoRow>(Assert.Single(rig.Page().Groups[1].Rows));
        Assert.Equal("row.no-metrics.header", info.HeaderKey);
    }

    [Fact]
    public void A_metric_that_shows_up_while_the_page_is_closed_asks_for_a_rebuild_when_it_opens()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        _ = rig.Page();
        rig.Host.PageVisibleValue.Value = true;
        Assert.Equal(0, rig.Host.PageRefreshRequests);   // same set: nothing to rebuild

        rig.Host.PageVisibleValue.Value = false;
        rig.Bus.PublishFrom("newcomer", "n.fresh", 1);
        rig.Host.PageVisibleValue.Value = true;
        Assert.Equal(1, rig.Host.PageRefreshRequests);
    }
}

public class CommandTests
{
    private static CommandResult Run(ModuleRig rig, string verb, params string[] args) => rig.Host.CommandRegistry.RunSync(verb, args);

    [Fact]
    public void Start_stop_and_status_work_end_to_end()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();

        var idle = Run(rig, "status");
        Assert.Equal(0, idle.ExitCode);
        Assert.StartsWith("Not recording.", idle.Output);
        Assert.Contains("Folder: " + rig.Folder.Path, idle.Output);

        var started = Run(rig, "start");
        Assert.Equal(0, started.ExitCode);
        Assert.Contains("Recording to " + rig.Folder.Path, started.Output);
        Assert.Contains("One row every 1 s, no time limit", started.Output);
        Assert.True(rig.WaitUntilFrozen());

        var status = Run(rig, "status");
        Assert.Equal(0, status.ExitCode);
        Assert.Contains("Recording to ", status.Output);
        Assert.Contains("4 rows,", status.Output);
        Assert.Contains("one row every 1 s, 3 metrics", status.Output);

        var stopped = Run(rig, "stop");
        Assert.Equal(0, stopped.ExitCode);
        Assert.StartsWith("Stopped. ", stopped.Output);
        Assert.Contains("4 rows,", stopped.Output);

        var after = Run(rig, "status");
        Assert.Contains("Last recording: ", after.Output);
        Assert.Contains("4 rows", after.Output);
    }

    [Fact]
    public void Start_takes_the_period_the_minutes_and_the_metrics()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        var result = Run(rig, "start", "--every", "2", "--minutes", "1", "--metrics", "z.a,a.x");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("One row every 2 s, stops after 1 min", result.Output);
        var status = rig.Module.Engine!.Status();
        Assert.Equal(TimeSpan.FromSeconds(2), status.Period);
        Assert.Equal(TimeSpan.FromMinutes(1), status.Duration);
        Assert.Equal(2, status.Columns);
        rig.Module.Engine.Stop();
        Assert.Equal("timestamp,elapsed_s,a.x,z.a", rig.CsvLines()[0]);
    }

    [Fact]
    public void Start_takes_the_form_with_an_equals_sign_and_minutes_zero_means_no_limit()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        rig.SetChoice("row.duration.header", "30");   // the page says 30 minutes
        var result = Run(rig, "start", "--every=5", "--minutes=0");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("no time limit", result.Output);
        Assert.Null(rig.Module.Engine!.Status().Duration);
        Assert.Equal(TimeSpan.FromSeconds(5), rig.Module.Engine.Status().Period);
    }

    [Fact]
    public void Start_without_arguments_does_what_the_start_button_does()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        rig.SetChoice("row.period.header", "10");
        rig.SetChoice("row.duration.header", "5");
        rig.Page().Groups[1].Rows.OfType<ExpanderRow>().SelectMany(g => g.Children).OfType<ToggleRow>().Single(t => t.HeaderKey == "=z.b").Value.Value = false;
        Assert.Equal(0, Run(rig, "start").ExitCode);
        var status = rig.Module.Engine!.Status();
        Assert.Equal(TimeSpan.FromSeconds(10), status.Period);
        Assert.Equal(TimeSpan.FromMinutes(5), status.Duration);
        Assert.Equal(2, status.Columns);
    }

    [Theory]
    [InlineData("--every", "0")]
    [InlineData("--every", "abc")]
    [InlineData("--every", "-1")]
    [InlineData("--every", "3601")]
    [InlineData("--every", "1.5")]
    [InlineData("--every")]
    [InlineData("--minutes", "-5")]
    [InlineData("--minutes", "99999999")]
    [InlineData("--metrics", "")]
    [InlineData("--metrics", "Not A Metric")]
    [InlineData("--metrics", ",")]
    [InlineData("--bogus")]
    [InlineData("now")]
    [InlineData("--every=")]
    public void Bad_arguments_are_a_usage_error_and_start_nothing(params string[] args)
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        var result = Run(rig, "start", args);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Usage: rigbay recorder start", result.Output);
        Assert.Contains("Cannot use: ", result.Output);
        Assert.False(rig.Module.Engine!.IsRecording);
        Assert.Empty(rig.Folder.Csvs);
    }

    [Fact]
    public void A_metric_that_is_not_on_the_bus_is_a_failure_that_names_it()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        var result = Run(rig, "start", "--metrics", "z.a,missing.one");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("missing.one", result.Output);
        Assert.False(rig.Module.Engine!.IsRecording);
    }

    [Fact]
    public void Start_while_recording_fails_and_stop_twice_is_not_an_error()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        Assert.Equal(0, Run(rig, "start").ExitCode);
        var twice = Run(rig, "start");
        Assert.Equal(1, twice.ExitCode);
        Assert.Contains("already running", twice.Output);
        Assert.Equal(0, Run(rig, "stop").ExitCode);
        var again = Run(rig, "stop");
        Assert.Equal(0, again.ExitCode);   // a script that stops after a time limit has ended it must not see a failure
        Assert.Equal("Not recording.", again.Output);
    }

    [Fact]
    public void A_full_disk_makes_start_fail_with_the_reason()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        rig.Disk.Free = 1024;
        var result = Run(rig, "start");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Not enough free space", result.Output);
    }

    [Theory]
    [InlineData("--every 2", 2, null, null)]
    [InlineData("--minutes 10", null, 10, null)]
    [InlineData("--metrics a.x,b.y --every=3", 3, null, "a.x,b.y")]
    [InlineData("--metrics a.x,a.x,b.y", null, null, "a.x,b.y")]
    [InlineData("", null, null, null)]
    public void Arguments_are_parsed(string line, int? every, int? minutes, string? metrics)
    {
        var args = line.Length == 0 ? Array.Empty<string>() : line.Split(' ');
        var parsed = RecorderModule.ParseStart(args);
        Assert.Null(parsed.Problem);
        Assert.Equal(every, parsed.EverySeconds);
        Assert.Equal(minutes, parsed.Minutes);
        Assert.Equal(metrics, parsed.Metrics is null ? null : string.Join(",", parsed.Metrics.Select(m => m.Value)));
    }
}

public class TrayAndHotkeyTests
{
    [Fact]
    public void The_tray_items_start_and_stop()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        rig.Host.TrayService.Item("start").Invoke();
        Assert.True(rig.Module.Engine!.IsRecording);
        Assert.True(rig.WaitUntilFrozen());
        rig.Host.TrayService.Item("stop").Invoke();
        Assert.False(rig.Module.Engine.IsRecording);
        Assert.Single(rig.Folder.Csvs);
    }

    [Fact]
    public void The_hotkey_toggles_when_the_user_has_bound_one()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        rig.Host.HotkeyService.Press("toggle");
        Assert.True(rig.Module.Engine!.IsRecording);
        Assert.True(rig.WaitUntilFrozen());
        rig.Host.HotkeyService.Press("toggle");
        Assert.False(rig.Module.Engine.IsRecording);
    }

    [Fact]
    public void A_start_that_is_refused_from_the_tray_is_shown_as_a_notification()
    {
        using var rig = new ModuleRig();   // nothing on the bus
        rig.Host.TrayService.Item("start").Invoke();
        var toast = Assert.Single(rig.Host.ToastService.Shown);
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.Equal("start.no-metrics", toast.Body.Key);
        Assert.False(rig.Module.Engine!.IsRecording);
    }

    [Fact]
    public void The_hotkey_row_is_bound_to_the_registered_gesture()
    {
        using var rig = new ModuleRig();
        var row = rig.Row<ShortcutRow>("row.hotkey.header");
        Assert.Null(row.Value.Value);
        Assert.False(row.ChangesHardware);
    }
}

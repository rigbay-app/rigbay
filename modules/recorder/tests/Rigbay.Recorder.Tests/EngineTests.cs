using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rigbay.Abstractions;
using Rigbay.Recorder.Recording;

namespace Rigbay.Recorder.Tests;

/// <summary>An engine on a fake bus, a fake clock and a temp folder.</summary>
internal sealed class Rig : IDisposable
{
    public readonly TempFolder Folder = new();
    public readonly FakeClock Clock = new();
    public readonly FakeBus Bus;
    public readonly FakeDisk Disk = new();
    public readonly FakeLog Log = new();
    public readonly FakeCatalog Catalog = new();
    public readonly FakeMachine Machine = new();
    public readonly FakeVersions Versions = new();
    public Func<string, Stream>? CreateFile;
    public readonly RecorderEngine Engine;

    public Rig()
    {
        Bus = new FakeBus(Clock.Clock);
        Engine = new RecorderEngine(Bus, Log, new RecorderServices
        {
            Clock = Clock.Clock,
            Delay = Clock.Delay,
            Disk = Disk,
            Machine = Machine,
            Versions = Versions,
            Catalog = () => Catalog,
            CreateFile = path => (CreateFile ?? (p => new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096)))(path),
            OwnModuleId = "recorder",
            ToolVersion = "0.1.0",
            HostVersion = "0.1.0",
        });
    }

    public StartRequest Request(double periodSeconds = 1, double? minutes = null, string[]? only = null, string[]? excluded = null) => new(
        TimeSpan.FromSeconds(periodSeconds),
        minutes is { } m ? TimeSpan.FromMinutes(m) : null,
        Folder.Path,
        only?.Select(o => new MetricId(o)).ToArray(),
        excluded is null ? null : new HashSet<string>(excluded));

    public void Publish(string source, string id, double value, MetricUnit unit = MetricUnit.None) => Bus.PublishFrom(source, id, value, unit);

    /// <summary>Two modules with three metrics, as a typical bus looks.</summary>
    public void PublishSome()
    {
        Publish("zeta", "z.b", 20, MetricUnit.Watt);
        Publish("zeta", "z.a", 10, MetricUnit.Celsius);
        Publish("alpha", "a.x", 5, MetricUnit.Percent);
    }

    public bool WaitUntilEnded(int timeoutMs = 30000) => Wait.Until(() => !Engine.IsRecording, timeoutMs);

    public string OnlyCsv() => Folder.Csvs.Single();
    public string[] Lines() => Files.Lines(OnlyCsv());
    public JsonDocument Meta(string csv) => JsonDocument.Parse(Files.ReadBytes(Path.ChangeExtension(csv, null) + CsvFormat.MetaSuffix));
    public JsonDocument Meta() => Meta(OnlyCsv());

    public void Dispose()
    {
        Engine.Stop(StopReason.ModuleStopped);
        Folder.Dispose();
    }
}

public class ScheduleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_late_wake_up_does_not_move_the_next_slot()
    {
        var schedule = new FixedSchedule(T0, TimeSpan.FromSeconds(1));
        Assert.Equal(T0, schedule.Take(T0 + TimeSpan.FromMilliseconds(40)));
        Assert.Equal(T0.AddSeconds(1), schedule.Next);
        Assert.Equal(T0.AddSeconds(1), schedule.Take(T0.AddSeconds(1) + TimeSpan.FromMilliseconds(900)));
        Assert.Equal(T0.AddSeconds(2), schedule.Next);
    }

    [Fact]
    public void More_than_one_period_late_resets_the_slot_instead_of_bursting()
    {
        var schedule = new FixedSchedule(T0, TimeSpan.FromSeconds(1));
        schedule.Take(T0);
        var wake = T0.AddSeconds(61.3);   // the laptop slept
        Assert.Equal(wake, schedule.Take(wake));
        Assert.Equal(wake.AddSeconds(1), schedule.Next);
    }

    [Fact]
    public void Exactly_one_period_late_keeps_its_slot()
    {
        var schedule = new FixedSchedule(T0, TimeSpan.FromSeconds(2));
        schedule.Take(T0);
        Assert.Equal(T0.AddSeconds(2), schedule.Take(T0.AddSeconds(4)));
    }

    [Fact]
    public void A_period_of_zero_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedSchedule(T0, TimeSpan.Zero));
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(2.5, "2.5")]
    [InlineData(3.0, "3")]
    [InlineData(-0.0, "0")]
    [InlineData(1e-9, "0")]
    [InlineData(-1e-9, "0")]
    [InlineData(1234567.891234, "1234567.891")]
    [InlineData(12345678901.5, "12345678901.5")]
    [InlineData(0.125, "0.125")]
    [InlineData(-47.25, "-47.25")]
    public void Numbers_use_a_dot_and_at_most_three_decimals(double value, string expected)
    {
        Assert.Equal(expected, CsvFormat.Number(value));
    }

    [Fact]
    public void Numbers_ignore_the_culture_of_the_machine()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("vi-VN");
            Assert.Equal("1234.5", CsvFormat.Number(1234.5));
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1234.5", CsvFormat.Number(1234.5));
            Assert.Equal("2026-11-01T20:35:06.848+07:00", CsvFormat.Time(new DateTimeOffset(2026, 11, 1, 20, 35, 6, 848, TimeSpan.FromHours(7))));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData("cpu.temp", "cpu.temp")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    public void Text_fields_are_quoted_only_when_needed(string text, string expected) => Assert.Equal(expected, CsvFormat.Field(text));

    [Fact]
    public void Clock_and_size_texts()
    {
        Assert.Equal("00:12:31", CsvFormat.Clock(TimeSpan.FromSeconds(751)));
        Assert.Equal("26:00:05", CsvFormat.Clock(TimeSpan.FromHours(26) + TimeSpan.FromSeconds(5)));
        Assert.Equal("00:00:00", CsvFormat.Clock(TimeSpan.FromSeconds(-3)));
        Assert.Equal("312 B", CsvFormat.Size(312));
        Assert.Equal("48.0 KB", CsvFormat.Size(48 * 1024));
        Assert.Equal("3.5 MB", CsvFormat.Size((long)(3.5 * 1024 * 1024)));
    }

    [Fact]
    public void File_names_carry_date_and_time()
    {
        Assert.Equal("rigbay-log-2026-11-01_20-35-06", CsvFormat.FileStem(new DateTimeOffset(2026, 11, 1, 20, 35, 6, 848, TimeSpan.FromHours(7))));
    }
}

public class EngineScheduleTests
{
    [Fact]
    public void Rows_are_exactly_one_period_apart_even_when_every_wake_up_is_late()
    {
        using var rig = new Rig();
        rig.Clock.Jitter = TimeSpan.FromMilliseconds(7);   // a timer that always fires 7 ms late
        rig.PublishSome();

        Assert.True(rig.Engine.Start(rig.Request(1, minutes: 0.1)).Started);   // 6 s
        Assert.True(rig.WaitUntilEnded());

        var lines = rig.Lines();
        Assert.Equal(1 + 6, lines.Length);
        var rows = lines.Skip(1).Select(l => l.Split(',')).ToList();
        Assert.Equal(new[] { "0", "1", "2", "3", "4", "5" }, rows.Select(r => r[1]).ToArray());
        var times = rows.Select(r => DateTimeOffset.Parse(r[0], CultureInfo.InvariantCulture)).ToList();
        for (int i = 1; i < times.Count; i++) Assert.Equal(TimeSpan.FromSeconds(1), times[i] - times[i - 1]);
        Assert.Matches(new Regex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}\+07:00$"), rows[0][0]);
    }

    [Fact]
    public void A_period_of_five_seconds_writes_a_row_every_five_seconds()
    {
        using var rig = new Rig();
        rig.PublishSome();
        Assert.True(rig.Engine.Start(rig.Request(5, minutes: 0.5)).Started);   // 30 s
        Assert.True(rig.WaitUntilEnded());
        var elapsed = rig.Lines().Skip(1).Select(l => l.Split(',')[1]).ToArray();
        Assert.Equal(new[] { "0", "5", "10", "15", "20", "25" }, elapsed);
    }

    [Fact]
    public void A_pause_of_many_periods_leaves_a_gap_and_no_burst()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.OnDelay = n => { if (n == 3) rig.Clock.Now += TimeSpan.FromSeconds(10); };   // suspended for 10 s
        Assert.True(rig.Engine.Start(rig.Request(1, minutes: 0.5)).Started);
        Assert.True(rig.WaitUntilEnded());

        var elapsed = rig.Lines().Skip(1).Select(l => double.Parse(l.Split(',')[1], CultureInfo.InvariantCulture)).ToList();
        for (int i = 1; i < elapsed.Count; i++) Assert.True(elapsed[i] - elapsed[i - 1] >= 1 - 1e-9, $"rows {i - 1} and {i} are closer than a period");
        Assert.Contains(Enumerable.Range(1, elapsed.Count - 1), i => elapsed[i] - elapsed[i - 1] > 5);   // the gap is there, not filled
        Assert.True(elapsed.Count < 30, "no burst of catch-up rows");
    }

    [Fact]
    public void The_first_row_is_written_at_once_and_the_clock_of_a_timed_recording_starts_there()
    {
        using var rig = new Rig();
        rig.PublishSome();
        var start = rig.Clock.Now;
        Assert.True(rig.Engine.Start(rig.Request(1, minutes: 0.05)).Started);   // 3 s
        Assert.True(rig.WaitUntilEnded());
        var meta = rig.Meta();
        Assert.Equal(CsvFormat.Time(start), meta.RootElement.GetProperty("started").GetString());
        Assert.Equal(3, meta.RootElement.GetProperty("rows").GetInt64());
        Assert.Equal(CsvFormat.Time(start.AddSeconds(3)), meta.RootElement.GetProperty("ended").GetString());
    }

    [Fact]
    public void The_real_clock_works_too()
    {
        using var folder = new TempFolder();
        var bus = new FakeBus(() => DateTimeOffset.Now);
        bus.PublishFrom("alpha", "a.x", 1.5);
        var engine = new RecorderEngine(bus, new FakeLog(), new RecorderServices { OwnModuleId = "recorder", Machine = new FakeMachine(), Versions = new FakeVersions() });
        var began = DateTimeOffset.Now;
        Assert.True(engine.Start(new StartRequest(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), folder.Path)).Started);
        Assert.True(Wait.Until(() => !engine.IsRecording, 30000));
        Assert.True(DateTimeOffset.Now - began >= TimeSpan.FromSeconds(1.9));
        var lines = Files.Lines(folder.Csvs.Single());
        // Header + the rows at 0 s and 1 s. On a starved machine the first row can start more than a period late; the schedule then moves its
        // slot to the present (by design, no catch-up burst) and only one row fits before the end. Both are right; the fake-clock tests above
        // pin the exact schedule.
        Assert.InRange(lines.Length, 2, 3);
        foreach (var row in lines.Skip(1))
        {
            var cells = row.Split(',');
            Assert.True(double.TryParse(cells[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _), "elapsed seconds: " + row);
            Assert.Equal("1.5", cells[2]);
        }
    }
}

public class EngineColumnTests
{
    [Fact]
    public void Columns_are_ordered_by_source_module_then_id_whatever_the_publishing_order()
    {
        using var rig = new Rig();
        rig.Publish("zeta", "z.b", 1);
        rig.Publish("alpha", "a.y", 2);
        rig.Publish("zeta", "z.a", 3);
        rig.Publish("alpha", "a.x", 4);
        Assert.True(rig.Engine.Start(rig.Request(minutes: 0.05)).Started);
        Assert.True(rig.WaitUntilEnded());
        Assert.Equal("timestamp,elapsed_s,a.x,a.y,z.a,z.b", rig.Lines()[0]);
        Assert.Equal(new[] { "4", "2", "3", "1" }, rig.Lines()[1].Split(',').Skip(2).ToArray());
    }

    [Fact]
    public void The_same_set_in_the_same_order_every_time()
    {
        using var rig = new Rig();
        rig.PublishSome();
        for (int i = 0; i < 2; i++)
        {
            rig.Clock.Now = rig.Clock.Now.AddMinutes(5);
            Assert.True(rig.Engine.Start(rig.Request(minutes: 0.05)).Started);
            Assert.True(rig.WaitUntilEnded());
        }
        var headers = rig.Folder.Csvs.Select(c => Files.Lines(c)[0]).ToList();
        Assert.Equal(2, headers.Count);
        Assert.Equal(headers[0], headers[1]);
    }

    [Fact]
    public void A_missing_value_is_an_empty_cell_and_the_row_keeps_its_width()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.OnDelay = n =>
        {
            if (n == 1) rig.Bus.WithdrawFrom("zeta", "z.a");        // gone for row 1 and 2
            if (n == 3) rig.Publish("zeta", "z.a", 11, MetricUnit.Celsius);   // back for row 3
            rig.Publish("alpha", "a.x", 5 + n, MetricUnit.Percent);
        };
        Assert.True(rig.Engine.Start(rig.Request(1, minutes: 0.07)).Started);   // 4.2 s: rows 0 to 4
        Assert.True(rig.WaitUntilEnded());

        var rows = rig.Lines().Skip(1).Select(l => l.Split(',')).ToList();
        Assert.All(rows, r => Assert.Equal(5, r.Length));
        // columns: timestamp, elapsed, a.x, z.a, z.b
        Assert.Equal("10", rows[0][3]);
        Assert.Equal("", rows[1][3]);
        Assert.Equal("", rows[2][3]);
        Assert.Equal("11", rows[3][3]);
        Assert.Equal("20", rows[1][4]);   // the neighbours are not shifted
    }

    [Fact]
    public void A_metric_that_was_there_at_the_start_only_keeps_its_column_with_empty_cells()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.OnDelay = n => { if (n == 1) rig.Bus.WithdrawFrom("zeta", "z.b"); };
        Assert.True(rig.Engine.Start(rig.Request(1, minutes: 0.05)).Started);
        Assert.True(rig.WaitUntilEnded());
        var lines = rig.Lines();
        Assert.Contains("z.b", lines[0].Split(','));
        Assert.Equal("20", lines[1].Split(',')[4]);
        Assert.All(lines.Skip(2), l => Assert.Equal("", l.Split(',')[4]));
    }

    [Fact]
    public void A_metric_that_is_withdrawn_before_the_start_is_not_a_column()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Bus.WithdrawFrom("zeta", "z.a");
        Assert.True(rig.Engine.Start(rig.Request(minutes: 0.05)).Started);
        Assert.True(rig.WaitUntilEnded());
        Assert.Equal("timestamp,elapsed_s,a.x,z.b", rig.Lines()[0]);
    }

    [Fact]
    public void A_metric_that_appears_later_is_reported_in_the_meta_and_not_added_to_the_file()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.OnDelay = n => { if (n == 2) rig.Publish("newcomer", "n.fresh", 9, MetricUnit.Rpm); };
        Assert.True(rig.Engine.Start(rig.Request(minutes: 0.07)).Started);
        Assert.True(rig.WaitUntilEnded());

        var lines = rig.Lines();
        Assert.Equal("timestamp,elapsed_s,a.x,z.a,z.b", lines[0]);
        Assert.All(lines, l => Assert.Equal(5, l.Split(',').Length));
        var late = rig.Meta().RootElement.GetProperty("late_metrics").EnumerateArray().ToList();
        var one = Assert.Single(late);
        Assert.Equal("n.fresh", one.GetProperty("id").GetString());
        Assert.Equal("newcomer", one.GetProperty("source").GetString());
        Assert.Equal("rpm", one.GetProperty("unit").GetString());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT", one.GetProperty("first_seen").GetString());
    }

    [Fact]
    public void A_metric_the_user_switched_off_is_not_recorded_and_not_reported_as_late()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.OnDelay = n => { if (n == 1) rig.Publish("zeta", "z.skip", 1); };
        Assert.True(rig.Engine.Start(rig.Request(minutes: 0.05, excluded: new[] { "z.a", "z.skip" })).Started);
        Assert.True(rig.WaitUntilEnded());
        Assert.Equal("timestamp,elapsed_s,a.x,z.b", rig.Lines()[0]);
        Assert.Empty(rig.Meta().RootElement.GetProperty("late_metrics").EnumerateArray());
    }

    [Fact]
    public void The_verb_can_name_the_metrics_and_an_unknown_name_is_refused_with_the_list()
    {
        using var rig = new Rig();
        rig.PublishSome();
        var refused = rig.Engine.Start(rig.Request(only: new[] { "z.a", "no.such", "also.missing" }));
        Assert.False(refused.Started);
        Assert.Equal("start.unknown-metrics", refused.Message.Key);
        Assert.Equal("no.such, also.missing", refused.Message.Args![0]);
        Assert.Empty(rig.Folder.Csvs);
        Assert.Equal(0, rig.Bus.Subscribers);   // nothing is left behind

        Assert.True(rig.Engine.Start(rig.Request(minutes: 0.05, only: new[] { "z.b", "a.x" })).Started);
        Assert.True(rig.WaitUntilEnded());
        Assert.Equal("timestamp,elapsed_s,a.x,z.b", rig.Lines()[0]);
    }

    [Fact]
    public void The_recorders_own_metric_is_not_recorded()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Publish("recorder", "recorder.rows", 12, MetricUnit.Count);
        Assert.True(rig.Engine.Start(rig.Request(minutes: 0.05)).Started);
        Assert.True(rig.WaitUntilEnded());
        Assert.DoesNotContain("recorder.rows", rig.Lines()[0]);
    }

    [Fact]
    public void A_value_whose_newest_sample_is_older_than_thirty_seconds_is_an_empty_cell()
    {
        using var rig = new Rig();
        rig.Publish("alpha", "a.old", 1, MetricUnit.None);
        rig.Bus.PublishFrom("alpha", "a.stale", 2, MetricUnit.None, at: rig.Clock.Now - TimeSpan.FromSeconds(31));
        rig.Bus.PublishFrom("alpha", "a.edge", 3, MetricUnit.None, at: rig.Clock.Now - TimeSpan.FromSeconds(29));
        Assert.True(rig.Engine.Start(rig.Request(minutes: 0.05)).Started);
        Assert.True(rig.WaitUntilEnded());
        var lines = rig.Lines();
        Assert.Equal("timestamp,elapsed_s,a.edge,a.old,a.stale", lines[0]);
        Assert.Equal("3", lines[1].Split(',')[2]);   // 29 s old at the first row
        Assert.Equal("", lines[1].Split(',')[4]);    // 31 s old: the source stopped updating it
        Assert.Equal("3", lines[2].Split(',')[2]);   // exactly 30 s old at the second row: still allowed
        Assert.Equal("", lines[3].Split(',')[2]);    // 31 s old at the third: the source stopped updating it too
    }

    [Fact]
    public void An_empty_bus_has_nothing_to_record_and_no_file_is_created()
    {
        using var rig = new Rig();
        var outcome = rig.Engine.Start(rig.Request());
        Assert.False(outcome.Started);
        Assert.Equal("start.no-metrics", outcome.Message.Key);
        Assert.Empty(Directory.GetFiles(rig.Folder.Path));
        Assert.False(rig.Engine.IsRecording);
    }

    [Fact]
    public void The_recorder_listens_only_while_it_records()
    {
        using var rig = new Rig();
        rig.PublishSome();
        Assert.Equal(0, rig.Bus.Subscribers);
        rig.Clock.BlockAfter = 1;
        Assert.True(rig.Engine.Start(rig.Request()).Started);
        Assert.True(rig.Clock.WaitUntilBlocked());
        // the subscription is what wakes the modules that poll slowly while nobody reads them
        Assert.Equal(1, rig.Bus.Subscribers);
        Assert.Equal(1, rig.Bus.SubscriberCount(new MetricId("z.a")));
        rig.Engine.Stop();
        Assert.Equal(0, rig.Bus.Subscribers);
    }
}

public class EngineEndTests
{
    [Fact]
    public void A_time_limit_ends_the_recording_and_closes_the_file()
    {
        using var rig = new Rig();
        rig.PublishSome();
        Assert.True(rig.Engine.Start(rig.Request(1, minutes: 0.05)).Started);   // 3 s
        Assert.True(rig.WaitUntilEnded());

        var summary = rig.Engine.LastSummary!;
        Assert.Equal(StopReason.Duration, summary.Reason);
        Assert.Equal(3, summary.Rows);
        Assert.Equal(4, rig.Lines().Length);
        Assert.Equal("duration", rig.Meta().RootElement.GetProperty("stop_reason").GetString());
        using var exclusive = new FileStream(rig.OnlyCsv(), FileMode.Open, FileAccess.ReadWrite, FileShare.None);   // closed
        Assert.False(rig.Engine.IsRecording);
        Assert.Equal(0, rig.Bus.Subscribers);
    }

    [Fact]
    public void Without_a_limit_it_runs_until_stopped()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.BlockAfter = 4;
        Assert.True(rig.Engine.Start(rig.Request()).Started);
        Assert.True(rig.Clock.WaitUntilBlocked());
        Assert.True(rig.Engine.IsRecording);
        Assert.Equal(5, rig.Engine.Status().Rows);
        var summary = rig.Engine.Stop();
        Assert.Equal(StopReason.User, summary!.Reason);
        Assert.Equal(5, summary.Rows);
    }

    [Fact]
    public void Starting_with_less_than_200_MB_free_is_refused_and_writes_nothing()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Disk.Free = 200L * 1024 * 1024 - 1;
        var outcome = rig.Engine.Start(rig.Request());
        Assert.False(outcome.Started);
        Assert.Equal("start.disk-low", outcome.Message.Key);
        Assert.Equal(new object[] { 199L, 200L }, outcome.Message.Args!);
        Assert.Empty(Directory.GetFiles(rig.Folder.Path));
        Assert.False(rig.Engine.IsRecording);

        rig.Disk.Free = 200L * 1024 * 1024;   // exactly enough
        Assert.True(rig.Engine.Start(rig.Request(minutes: 0.05)).Started);
        Assert.True(rig.WaitUntilEnded());
    }

    [Fact]
    public void A_drive_whose_free_space_is_unknown_does_not_block_the_start()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Disk.Free = null;   // a network share
        Assert.True(rig.Engine.Start(rig.Request(minutes: 0.05)).Started);
        Assert.True(rig.WaitUntilEnded());
    }

    [Fact]
    public void Running_out_of_space_stops_the_recording_cleanly_and_says_so()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.OnDelay = n => { if (n == 3) rig.Disk.Free = 10L * 1024 * 1024; };
        Assert.True(rig.Engine.Start(rig.Request()).Started);
        Assert.True(rig.WaitUntilEnded());

        var summary = rig.Engine.LastSummary!;
        Assert.Equal(StopReason.DiskLow, summary.Reason);
        Assert.Equal("10", summary.Detail);
        Assert.Equal(3, summary.Rows);   // rows 0, 1, 2 were written; the fourth was not started
        Assert.Equal(4, rig.Lines().Length);
        var meta = rig.Meta().RootElement;
        Assert.Equal("disk-low", meta.GetProperty("stop_reason").GetString());
        Assert.False(meta.GetProperty("ended").ValueKind == JsonValueKind.Null);
        using var exclusive = new FileStream(rig.OnlyCsv(), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Exactly_50_MB_free_is_still_enough_and_one_byte_less_is_not()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.OnDelay = n => { if (n == 2) rig.Disk.Free = 50L * 1024 * 1024; };
        Assert.True(rig.Engine.Start(rig.Request(minutes: 0.05)).Started);
        Assert.True(rig.WaitUntilEnded());
        Assert.Equal(StopReason.Duration, rig.Engine.LastSummary!.Reason);

        using var rig2 = new Rig();
        rig2.PublishSome();
        rig2.Clock.OnDelay = n => { if (n == 2) rig2.Disk.Free = 50L * 1024 * 1024 - 1; };
        Assert.True(rig2.Engine.Start(rig2.Request(minutes: 0.05)).Started);
        Assert.True(rig2.WaitUntilEnded());
        Assert.Equal(StopReason.DiskLow, rig2.Engine.LastSummary!.Reason);
    }

    [Fact]
    public void A_file_that_cannot_be_written_stops_the_recording_and_does_not_throw()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.CreateFile = path => new FailingStream(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), writesBeforeFailure: 4);
        Assert.True(rig.Engine.Start(rig.Request()).Started);
        Assert.True(rig.WaitUntilEnded());

        var summary = rig.Engine.LastSummary!;
        Assert.Equal(StopReason.WriteError, summary.Reason);
        Assert.Contains("disk is full", summary.Detail);
        Assert.Equal(3, summary.Rows);   // 4 writes: the header and 3 rows
        Assert.Equal("write-error", rig.Meta().RootElement.GetProperty("stop_reason").GetString());
        Assert.Contains("disk is full", rig.Meta().RootElement.GetProperty("error").GetString());
        Assert.True(rig.Log.Count(LogLevel.Error) >= 1);
        Assert.False(rig.Engine.IsRecording);
        Assert.Equal(0, rig.Bus.Subscribers);
    }

    [Fact]
    public void A_file_that_cannot_be_created_refuses_the_start_with_a_reason()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.CreateFile = _ => throw new UnauthorizedAccessException("access denied");
        var outcome = rig.Engine.Start(rig.Request());
        Assert.False(outcome.Started);
        Assert.Equal("start.file-failed", outcome.Message.Key);
        Assert.Contains("access denied", (string)outcome.Message.Args![1]);
        Assert.False(rig.Engine.IsRecording);
        Assert.Equal(0, rig.Bus.Subscribers);
    }

    [Fact]
    public void A_folder_that_cannot_be_created_refuses_the_start()
    {
        using var rig = new Rig();
        rig.PublishSome();
        var blocker = Path.Combine(rig.Folder.Path, "a-file");
        File.WriteAllText(blocker, "x");
        var request = rig.Request() with { Folder = Path.Combine(blocker, "inside") };   // a folder under a file
        var outcome = rig.Engine.Start(request);
        Assert.False(outcome.Started);
        Assert.Equal("start.folder-failed", outcome.Message.Key);
    }

    [Fact]
    public void A_second_start_while_recording_is_refused_and_leaves_the_first_alone()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.BlockAfter = 1;
        Assert.True(rig.Engine.Start(rig.Request()).Started);
        Assert.True(rig.Clock.WaitUntilBlocked());
        var second = rig.Engine.Start(rig.Request());
        Assert.False(second.Started);
        Assert.Equal("start.already", second.Message.Key);
        Assert.Single(rig.Folder.Csvs);
        Assert.True(rig.Engine.IsRecording);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.0)]
    public void A_period_under_a_second_is_refused(double seconds)
    {
        using var rig = new Rig();
        rig.PublishSome();
        var outcome = rig.Engine.Start(rig.Request(seconds));
        Assert.False(outcome.Started);
        Assert.Equal("start.bad-period", outcome.Message.Key);
    }

    [Fact]
    public void No_file_is_ever_deleted()
    {
        using var rig = new Rig();
        rig.PublishSome();
        var keep = Path.Combine(rig.Folder.Path, "rigbay-log-2020-01-01_00-00-00.csv");
        File.WriteAllText(keep, "old recording");
        rig.CreateFile = _ => throw new IOException("nope");
        Assert.False(rig.Engine.Start(rig.Request()).Started);
        rig.CreateFile = null;
        Assert.True(rig.Engine.Start(rig.Request(minutes: 0.05)).Started);
        Assert.True(rig.WaitUntilEnded());
        Assert.Equal("old recording", File.ReadAllText(keep));
        Assert.Equal(2, Directory.GetFiles(rig.Folder.Path, "*.csv").Length);
    }
}

public class EngineFileTests
{
    [Fact]
    public void A_name_that_is_taken_gets_a_number_and_the_first_file_is_not_touched()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.BlockAfter = 0;   // freeze after the first row, so the three recordings begin in the same second
        var when = rig.Clock.Now;

        var paths = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            rig.Clock.Now = when;
            var outcome = rig.Engine.Start(rig.Request());
            Assert.True(outcome.Started);
            paths.Add(outcome.Path!);
            Assert.True(rig.Clock.WaitUntilBlocked());
            Assert.True(Wait.Until(() => rig.Engine.Status().Rows == 1));
            rig.Engine.Stop();
        }

        Assert.Equal(new[] { "rigbay-log-2026-11-01_20-35-06.csv", "rigbay-log-2026-11-01_20-35-06-2.csv", "rigbay-log-2026-11-01_20-35-06-3.csv" },
            paths.Select(Path.GetFileName).ToArray());
        Assert.All(paths, p => Assert.Equal(2, Files.Lines(p).Length));
        Assert.True(File.Exists(Path.ChangeExtension(paths[1], null) + ".meta.json"));
    }

    [Fact]
    public void A_file_the_user_already_has_under_that_name_is_never_overwritten()
    {
        using var rig = new Rig();
        rig.PublishSome();
        var taken = Path.Combine(rig.Folder.Path, "rigbay-log-2026-11-01_20-35-06.csv");
        File.WriteAllText(taken, "mine");
        File.WriteAllText(Path.Combine(rig.Folder.Path, "rigbay-log-2026-11-01_20-35-06-2.meta.json"), "{}");   // a meta without its csv counts too
        var outcome = rig.Engine.Start(rig.Request(minutes: 0.05));
        Assert.True(outcome.Started);
        Assert.True(rig.WaitUntilEnded());
        Assert.Equal("mine", File.ReadAllText(taken));
        Assert.Equal("rigbay-log-2026-11-01_20-35-06-3.csv", Path.GetFileName(outcome.Path));
    }

    [Fact]
    public void The_csv_is_utf8_without_a_byte_order_mark_and_uses_dot_decimals()
    {
        using var rig = new Rig();
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("vi-VN");
            rig.Publish("alpha", "a.x", 1234.5678);
            Assert.True(rig.Engine.Start(rig.Request(minutes: 0.05)).Started);
            Assert.True(rig.WaitUntilEnded());
        }
        finally { CultureInfo.CurrentCulture = before; }

        var bytes = Files.ReadBytes(rig.OnlyCsv());
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM in the csv");
        Assert.Equal((byte)'t', bytes[0]);   // "timestamp"
        var metaBytes = Files.ReadBytes(Path.ChangeExtension(rig.OnlyCsv(), null) + ".meta.json");
        Assert.False(metaBytes.Length >= 3 && metaBytes[0] == 0xEF && metaBytes[1] == 0xBB && metaBytes[2] == 0xBF, "no BOM in the meta");
        Assert.EndsWith("\r\n", Encoding.UTF8.GetString(bytes));
        Assert.Equal("1234.568", rig.Lines()[1].Split(',')[2]);
    }

    [Fact]
    public void While_it_records_the_file_can_be_read_by_others_but_not_written()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.BlockAfter = 2;
        Assert.True(rig.Engine.Start(rig.Request()).Started);
        Assert.True(rig.Clock.WaitUntilBlocked());
        var path = rig.OnlyCsv();
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { /* FileShare.Read of the recorder allows readers */ }
        Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        Assert.Equal(4, Files.Lines(path).Length);   // flushed after every row: header and 3 rows are already on disk
    }

    [Fact]
    public void The_size_the_status_reports_matches_the_file()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.BlockAfter = 3;
        Assert.True(rig.Engine.Start(rig.Request()).Started);
        Assert.True(rig.Clock.WaitUntilBlocked());
        Assert.Equal(new FileInfo(rig.OnlyCsv()).Length, rig.Engine.Status().Bytes);
        var summary = rig.Engine.Stop();
        Assert.Equal(new FileInfo(rig.OnlyCsv()).Length, summary!.Bytes);
    }
}

public class EngineMetaTests
{
    [Fact]
    public void The_meta_says_what_each_column_is_and_where_it_came_from()
    {
        using var rig = new Rig();
        rig.Catalog.Texts["z.a"] = new MetricText("Nhiệt độ CPU", "Package temperature, from the firmware");
        rig.Catalog.Texts["a.x"] = new MetricText("Load", "");
        rig.Publish("zeta", "z.a", 10, MetricUnit.Celsius);
        rig.Publish("zeta", "z.b", 20, MetricUnit.Watt);
        rig.Publish("alpha", "a.x", 5, MetricUnit.Percent);
        rig.Publish("alpha", "a.n", 1, MetricUnit.None);
        var start = rig.Clock.Now;
        Assert.True(rig.Engine.Start(rig.Request(2, minutes: 0.5)).Started);
        Assert.True(rig.WaitUntilEnded());

        var path = Path.ChangeExtension(rig.OnlyCsv(), null) + ".meta.json";
        var raw = File.ReadAllText(path);
        Assert.Contains("°C", raw);                // not escaped
        Assert.Contains("Nhiệt độ CPU", raw);      // UTF-8 as it is

        var meta = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("Rigbay recorder", meta.GetProperty("tool").GetString());
        Assert.Equal("0.1.0", meta.GetProperty("tool_version").GetString());
        Assert.Equal("0.1.0", meta.GetProperty("host_version").GetString());
        Assert.Equal(Path.GetFileName(rig.OnlyCsv()), meta.GetProperty("csv").GetString());
        Assert.Equal(CsvFormat.Time(start), meta.GetProperty("started").GetString());
        Assert.Equal(CsvFormat.Time(start.AddSeconds(30)), meta.GetProperty("ended").GetString());
        Assert.Equal(2, meta.GetProperty("period_s").GetDouble());
        Assert.Equal(0.5, meta.GetProperty("limit_minutes").GetDouble());
        Assert.Equal(15, meta.GetProperty("rows").GetInt64());
        Assert.Equal("duration", meta.GetProperty("stop_reason").GetString());
        Assert.Equal(JsonValueKind.Null, meta.GetProperty("error").ValueKind);

        var machine = meta.GetProperty("machine");
        Assert.Equal("Acme", machine.GetProperty("manufacturer").GetString());
        Assert.Equal("Laptop 1", machine.GetProperty("model").GetString());
        Assert.Equal("Fast CPU @ 3.0GHz", machine.GetProperty("cpu").GetString());
        Assert.Equal("Windows 11 test", machine.GetProperty("os").GetString());

        var modules = meta.GetProperty("modules");
        Assert.Equal("1.2.3", modules.GetProperty("alpha").GetString());
        Assert.Equal("0.9.0", modules.GetProperty("zeta").GetString());
        Assert.Equal("0.1.0", modules.GetProperty("recorder").GetString());

        var columns = meta.GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal(new[] { "a.n", "a.x", "z.a", "z.b" }, columns.Select(c => c.GetProperty("id").GetString()).ToArray());   // CSV order
        Assert.Equal("timestamp,elapsed_s,a.n,a.x,z.a,z.b", rig.Lines()[0]);

        var za = columns[2];
        Assert.Equal("Nhiệt độ CPU", za.GetProperty("label").GetString());
        Assert.Equal("°C", za.GetProperty("unit").GetString());
        Assert.Equal("zeta", za.GetProperty("source").GetString());
        Assert.Equal("Package temperature, from the firmware", za.GetProperty("description").GetString());
        Assert.Equal("Load", columns[1].GetProperty("description").GetString());       // no description: the label
        Assert.Equal("a.n", columns[0].GetProperty("label").GetString());              // nobody described it: the id
        Assert.Equal("", columns[0].GetProperty("unit").GetString());
        Assert.Equal("W", columns[3].GetProperty("unit").GetString());
        Assert.Equal(3, meta.GetProperty("notes").GetArrayLength());
    }

    [Fact]
    public void The_meta_exists_from_the_first_second_and_has_no_end_until_the_recording_ends()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.BlockAfter = 2;
        Assert.True(rig.Engine.Start(rig.Request()).Started);
        Assert.True(rig.Clock.WaitUntilBlocked());

        var running = rig.Meta().RootElement;
        Assert.Equal(JsonValueKind.Null, running.GetProperty("ended").ValueKind);
        Assert.Equal(JsonValueKind.Null, running.GetProperty("stop_reason").ValueKind);
        Assert.Equal(3, running.GetProperty("columns").GetArrayLength());

        rig.Engine.Stop();
        var finished = rig.Meta().RootElement;
        Assert.NotEqual(JsonValueKind.Null, finished.GetProperty("ended").ValueKind);
        Assert.Equal("user", finished.GetProperty("stop_reason").GetString());
        Assert.Equal(3, finished.GetProperty("rows").GetInt64());
    }

    [Fact]
    public void The_meta_is_named_like_the_csv()
    {
        using var rig = new Rig();
        rig.PublishSome();
        Assert.True(rig.Engine.Start(rig.Request(minutes: 0.05)).Started);
        Assert.True(rig.WaitUntilEnded());
        Assert.Equal(new[] { "rigbay-log-2026-11-01_20-35-06.csv", "rigbay-log-2026-11-01_20-35-06.meta.json" },
            Directory.GetFiles(rig.Folder.Path).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}

public class EngineCancelTests
{
    [Fact]
    public void Stop_while_waiting_for_the_next_slot_closes_the_file_at_once()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.BlockAfter = 1;
        Assert.True(rig.Engine.Start(rig.Request(10)).Started);
        Assert.True(rig.Clock.WaitUntilBlocked());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var summary = rig.Engine.Stop();
        Assert.True(watch.ElapsedMilliseconds < 2000);
        Assert.NotNull(summary);
        Assert.Equal(StopReason.User, summary.Reason);
        Assert.Equal(2, summary.Rows);
        Assert.False(rig.Engine.IsRecording);
        using var exclusive = new FileStream(rig.OnlyCsv(), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Stop_twice_and_stop_with_nothing_running_do_nothing_and_do_not_throw()
    {
        using var rig = new Rig();
        Assert.Null(rig.Engine.Stop());
        rig.PublishSome();
        rig.Clock.BlockAfter = 1;
        Assert.True(rig.Engine.Start(rig.Request()).Started);
        Assert.True(rig.Clock.WaitUntilBlocked());
        Assert.NotNull(rig.Engine.Stop());
        Assert.Null(rig.Engine.Stop());
        Assert.Single(rig.Folder.Csvs);
    }

    [Fact]
    public void Several_threads_stopping_at_once_end_it_once()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Clock.BlockAfter = 1;
        int ended = 0;
        rig.Engine.Ended += _ => Interlocked.Increment(ref ended);
        Assert.True(rig.Engine.Start(rig.Request()).Started);
        Assert.True(rig.Clock.WaitUntilBlocked());

        var errors = new List<Exception>();
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            try { rig.Engine.Stop(); } catch (Exception ex) { lock (errors) errors.Add(ex); }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Empty(errors);
        Assert.Equal(1, ended);
        Assert.False(rig.Engine.IsRecording);
    }

    [Fact]
    public void Starting_and_stopping_at_once_many_times_never_leaves_a_file_open()
    {
        using var rig = new Rig();
        rig.PublishSome();
        for (int i = 0; i < 40; i++)
        {
            rig.Clock.Now = rig.Clock.Now.AddSeconds(2);
            Assert.True(rig.Engine.Start(rig.Request()).Started);
            rig.Engine.Stop();
            Assert.False(rig.Engine.IsRecording);
            Assert.Equal(0, rig.Bus.Subscribers);
        }
        rig.Clock.BlockAfter = 0;
        foreach (var csv in rig.Folder.Csvs)
            using (new FileStream(csv, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }   // every file is closed
        Assert.Equal(40, rig.Folder.Csvs.Length);
    }

    [Fact]
    public void Handlers_that_throw_do_not_break_the_recording()
    {
        using var rig = new Rig();
        rig.PublishSome();
        rig.Engine.Changed += () => throw new InvalidOperationException("bad handler");
        rig.Engine.Ended += _ => throw new InvalidOperationException("bad handler");
        Assert.True(rig.Engine.Start(rig.Request(minutes: 0.05)).Started);
        Assert.True(rig.WaitUntilEnded());
        Assert.Equal(3, rig.Engine.LastSummary!.Rows);
        Assert.True(rig.Log.Count(LogLevel.Warn) >= 2);
    }

    [Fact]
    public void A_catalog_that_throws_refuses_the_start_without_throwing()
    {
        using var folder = new TempFolder();
        var clock = new FakeClock();
        var bus = new FakeBus(clock.Clock);
        bus.PublishFrom("alpha", "a.x", 1);
        var engine = new RecorderEngine(bus, new FakeLog(), new RecorderServices
        {
            Clock = clock.Clock, Delay = clock.Delay, Machine = new FakeMachine(), Versions = new FakeVersions(),
            Catalog = () => throw new InvalidOperationException("no catalog"),
        });
        var outcome = engine.Start(new StartRequest(TimeSpan.FromSeconds(1), null, folder.Path));
        Assert.False(outcome.Started);
        Assert.Equal("start.failed", outcome.Message.Key);
        Assert.False(engine.IsRecording);
        Assert.Equal(0, bus.Subscribers);
    }
}

public class HistoryTests
{
    [Fact]
    public void Lists_the_newest_ten_and_leaves_every_file_in_place()
    {
        using var folder = new TempFolder();
        for (int i = 0; i < 15; i++)
        {
            var path = Path.Combine(folder.Path, $"rigbay-log-2026-11-01_10-00-{i:00}.csv");
            File.WriteAllText(path, new string('x', 100 * (i + 1)));
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 1, 10, 0, i, DateTimeKind.Utc));
            File.WriteAllText(Path.ChangeExtension(path, null) + ".meta.json", "{}");
        }
        File.WriteAllText(Path.Combine(folder.Path, "notes.csv"), "not a recording");
        File.WriteAllText(Path.Combine(folder.Path, "rigbay-log-x.txt"), "not a recording");

        var list = History.List(folder.Path);
        Assert.Equal(10, list.Count);
        Assert.Equal("rigbay-log-2026-11-01_10-00-14.csv", list[0].Name);
        Assert.Equal("rigbay-log-2026-11-01_10-00-05.csv", list[9].Name);
        Assert.Equal(1500, list[0].Bytes);
        Assert.Equal(15 + 15 + 2, Directory.GetFiles(folder.Path).Length);   // nothing deleted
        Assert.Equal(3, History.List(folder.Path, 3).Count);
    }

    [Fact]
    public void Leaves_out_the_file_being_written()
    {
        using var folder = new TempFolder();
        var a = Path.Combine(folder.Path, "rigbay-log-2026-11-01_10-00-00.csv");
        var b = Path.Combine(folder.Path, "rigbay-log-2026-11-01_10-00-01.csv");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        Assert.Equal(new[] { Path.GetFileName(a) }, History.List(folder.Path, except: b).Select(e => e.Name).ToArray());
    }

    [Fact]
    public void A_missing_folder_is_an_empty_list()
    {
        Assert.Empty(History.List(Path.Combine(Path.GetTempPath(), "rigbay-no-such-folder-" + Guid.NewGuid().ToString("N"))));
    }
}

/// <summary>A stream that throws after a number of writes, like a disk that fills up.</summary>
internal sealed class FailingStream : Stream
{
    private readonly Stream _inner;
    private int _writesLeft;

    public FailingStream(Stream inner, int writesBeforeFailure) { _inner = inner; _writesLeft = writesBeforeFailure; }

    private void Check()
    {
        if (_writesLeft-- <= 0) throw new IOException("There is not enough space on the disk (the disk is full).");
    }

    public override void Write(byte[] buffer, int offset, int count) { Check(); _inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(); _inner.Write(buffer); }
    public override void Flush() => _inner.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
}

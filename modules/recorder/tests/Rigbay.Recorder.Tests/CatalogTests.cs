using System.Text.Json;
using Rigbay.Abstractions;
using Rigbay.Recorder.Recording;

namespace Rigbay.Recorder.Tests;

public class CatalogTests
{
    [Fact]
    public void The_bus_catalog_gives_the_label_and_description_the_owner_module_resolved()
    {
        var bus = new FakeBus(() => DateTimeOffset.Now);
        bus.Infos["z.a"] = new MetricInfo(new MetricId("z.a"), "CPU temperature", "Package temperature from the firmware", MetricUnit.Celsius, "0", "zeta", true);
        var catalog = new BusCatalog(bus);
        var text = catalog.Describe(new MetricId("z.a"));
        Assert.Equal("CPU temperature", text!.Label);
        Assert.Equal("Package temperature from the firmware", text.Description);
        Assert.Null(catalog.Describe(new MetricId("never.seen")));
    }

    [Fact]
    public void A_host_without_the_catalogue_leaves_the_id_as_the_label()
    {
        // the default interface member answers null; a bus that does not override it is what an old host is
        IMetricsBus old = new BareBus();
        Assert.Null(new BusCatalog(old).Describe(new MetricId("z.a")));
        Assert.Null(new BareCatalog().Describe(new MetricId("z.a")));
    }

    [Fact]
    public void The_meta_of_a_recording_carries_the_labels_the_bus_resolved()
    {
        using var rig = new ModuleRig();
        rig.Bus.PublishFrom("zeta", "z.a", 61, MetricUnit.Celsius);
        rig.Bus.Infos["z.a"] = new MetricInfo(new MetricId("z.a"), "CPU temperature", "Package temperature from the firmware", MetricUnit.Celsius, "0", "zeta", true);
        var module = new RecorderModule(h => new RecorderServices
        {
            Clock = rig.Clock.Clock, Delay = rig.Clock.Delay, Disk = rig.Disk, Machine = new FakeMachine(), Versions = new FakeVersions(),
            Catalog = () => new BusCatalog(h.Metrics),   // what the real module does
            OwnModuleId = h.ModuleId,
        }, rig.Shell, () => rig.Folder.Path);
        rig.Restart(module);
        Assert.True(module.StartRecording(module.RequestFromSettings()).Started);
        Assert.True(rig.WaitUntilFrozen());
        module.Engine!.Stop();

        var meta = JsonDocument.Parse(Files.ReadBytes(Path.ChangeExtension(rig.Folder.Csvs.Single(), null) + ".meta.json")).RootElement;
        var column = Assert.Single(meta.GetProperty("columns").EnumerateArray());
        Assert.Equal("CPU temperature", column.GetProperty("label").GetString());
        Assert.Equal("Package temperature from the firmware", column.GetProperty("description").GetString());
        // and the picker shows the same label
        Assert.Contains(rig.Page().EnumerateRows().OfType<ToggleRow>(), t => t.HeaderKey == "=CPU temperature");
    }

    [Fact]
    public void A_new_metric_announced_by_the_catalogue_rebuilds_the_open_page_once()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        _ = rig.Page();
        rig.Host.PageVisibleValue.Value = true;
        rig.Bus.RaiseCatalogChanged();
        Assert.Equal(0, rig.Host.PageRefreshRequests);   // same set of metrics

        rig.Bus.PublishFrom("newcomer", "n.fresh", 1);
        rig.Bus.RaiseCatalogChanged();
        Assert.Equal(1, rig.Host.PageRefreshRequests);
    }

    [Fact]
    public void The_recorders_own_metric_appearing_does_not_rebuild_the_page_in_a_loop()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();
        _ = rig.Page();
        rig.Host.PageVisibleValue.Value = true;
        rig.Bus.PublishFrom("recorder", "recorder.rows", 1, MetricUnit.Count);
        rig.Bus.RaiseCatalogChanged();
        Assert.Equal(0, rig.Host.PageRefreshRequests);
    }

    [Fact]
    public void Switching_language_rebuilds_the_page_so_the_metric_labels_follow()
    {
        using var rig = new ModuleRig();
        rig.Host.Loc.Switch("vi");
        Assert.Equal(1, rig.Host.PageRefreshRequests);
    }

    [Fact]
    public void The_real_services_work_on_this_machine()
    {
        // No fakes except the bus and the folder: real clock, real free-space check, real registry for the machine facts.
        using var folder = new TempFolder();
        var clock = new FakeClock();
        var host = new FakeHost(clock);
        host.MetricsBus.PublishFrom("alpha", "a.x", 3.25, MetricUnit.Percent);
        var module = new RecorderModule(null, new FakeShell(), () => folder.Path);
        module.Initialize(host);
        try
        {
            var request = module.RequestFromSettings() with { Duration = TimeSpan.FromSeconds(1.2) };
            host.MetricsBus.PublishFrom("alpha", "a.x", 3.25, MetricUnit.Percent, at: DateTimeOffset.Now);
            Assert.True(module.StartRecording(request).Started);
            Assert.True(Wait.Until(() => !module.Engine!.IsRecording, 30000));
        }
        finally { module.Shutdown(); }

        var csv = folder.Csvs.Single();
        // header + the rows at 0 s and 1 s (the slot at 2 s is past the end at 1.2 s). On a slow machine the first row can start more than a period
        // late; the schedule then moves its slot to the present (by design, no catch-up burst) and only one row fits before the end. Both are right.
        Assert.InRange(Files.Lines(csv).Length, 2, 3);
        var meta = JsonDocument.Parse(Files.ReadBytes(Path.ChangeExtension(csv, null) + ".meta.json")).RootElement;
        Assert.Equal("duration", meta.GetProperty("stop_reason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(meta.GetProperty("machine").GetProperty("os").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(meta.GetProperty("tool_version").GetString()));
    }

    /// <summary>A bus that implements only the original members, like a host written before the catalogue.</summary>
    private sealed class BareBus : IMetricsBus
    {
        public void Publish(MetricId id, double value, MetricUnit unit) { }
        public IDisposable Subscribe(Action<MetricSample> handler) => new Nothing();
        public IDisposable Subscribe(MetricId id, Action<MetricSample> handler) => new Nothing();
        public bool TryGetLatest(MetricId id, out MetricSample sample) { sample = null!; return false; }
        public IReadOnlyList<MetricSample> GetLatest() => Array.Empty<MetricSample>();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}

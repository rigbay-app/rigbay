using Xunit;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.Core.Tests;

/// <summary>A12: a module reads the descriptors of metrics other modules publish (IMetricsBus.GetMetricInfo and friends).</summary>
public class MetricInfoTests
{
    private static readonly MetricId Temp = new("faulty.temp");
    private static readonly MetricId Fan = new("faulty.fan");

    private static (RigbayHost Host, ModuleEntry Module, TestEnv Env) Start()
    {
        var env = new TestEnv();
        var folder = env.AddModule("faulty", "signals");
        Directory.CreateDirectory(Path.Combine(folder, "lang"));
        File.WriteAllText(Path.Combine(folder, "lang", "en.lang"),
            "@code = en\n@name = English\n\nmodule.faulty.metric.temp = Core temperature\nmodule.faulty.metric.temp.description = Package temperature\n");
        File.WriteAllText(Path.Combine(folder, "lang", "vi.lang"),
            "@code = vi\n@name = Tiếng Việt\n\nmodule.faulty.metric.temp = Nhiệt độ lõi\nmodule.faulty.metric.temp.description = Nhiệt độ gói CPU\n");
        var host = env.NewHost();
        host.Discover();
        var m = host.Find("faulty")!;
        Assert.True(m.Enable());
        return (host, m, env);
    }

    private static async Task<string> Run(RigbayHost host, params string[] args)
    {
        var outcome = await host.ExecuteCommandAsync(new[] { "faulty" }.Concat(args).ToArray());
        Assert.Equal(CommandOutcome.Ok, outcome.ExitCode);
        return outcome.Output;
    }

    [Fact]
    public async Task Label_and_description_resolve_in_en_and_vi_through_the_owner_module_for_a_reader()
    {
        var (host, _, env) = Start();
        using (env)
        {
            // the module-side bus (what another module sees)
            Assert.Equal("faulty.temp|Core temperature|Package temperature|Celsius|0.0|faulty|True", await Run(host, "info", "faulty.temp"));
            var info = host.Metrics.GetMetricInfo(Temp)!;
            Assert.Equal(("Core temperature", "Package temperature", MetricUnit.Celsius, "0.0", "faulty", true),
                (info.Label, info.Description, info.Unit, info.FormatHint, info.OwnerModuleId, info.IsAvailable));

            host.SetLanguage("vi");
            Assert.Equal("faulty.temp|Nhiệt độ lõi|Nhiệt độ gói CPU|Celsius|0.0|faulty|True", await Run(host, "info", "faulty.temp"));
            host.SetLanguage("en");
            Assert.Equal("Core temperature", host.Metrics.GetMetricInfo(Temp)!.Label);
            host.Shutdown();
        }
    }

    [Fact]
    public void Literal_text_is_shown_as_written_and_a_missing_description_is_empty()
    {
        var (host, _, env) = Start();
        using (env)
        {
            var fan = host.Metrics.GetMetricInfo(Fan)!;
            Assert.Equal("Fan 1", fan.Label);
            Assert.Equal("", fan.Description);
            Assert.Equal(MetricUnit.Rpm, fan.Unit);

            host.Metrics.Describe("faulty", new MetricDescriptor(new MetricId("faulty.lit"), Text.Literal("Pump A"), MetricUnit.None) { DescriptionKey = Text.Literal("From the device") });
            var lit = host.Metrics.GetMetricInfo(new MetricId("faulty.lit"))!;
            Assert.Equal(("Pump A", "From the device"), (lit.Label, lit.Description));
            host.Shutdown();
        }
    }

    [Fact]
    public void A_key_missing_from_the_language_file_falls_back_to_the_id()
    {
        var (host, _, env) = Start();
        using (env)
        {
            var info = host.Metrics.GetMetricInfo(new MetricId("faulty.nokey"))!;
            Assert.Equal("faulty.nokey", info.Label);
            Assert.Equal("", info.Description);
            host.Shutdown();
        }
    }

    [Fact]
    public void A_metric_without_a_descriptor_still_appears_with_its_id_and_the_unit_of_its_samples()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        var id = new MetricId("other.power");
        Assert.Null(host.Metrics.GetMetricInfo(id));

        host.Metrics.Publish("other", id, 12.5, MetricUnit.Watt);

        var info = host.Metrics.GetMetricInfo(id)!;
        Assert.Equal(new MetricInfo(id, "other.power", "", MetricUnit.Watt, null, "other", true), info);
        Assert.Contains(info, host.Metrics.GetAllMetricInfo());
    }

    [Fact]
    public async Task A_withdrawn_metric_is_listed_as_not_available_and_comes_back_when_published_again()
    {
        var (host, _, env) = Start();
        using (env)
        {
            await Run(host, "publish", "faulty.temp", "50");
            Assert.True(host.Metrics.GetMetricInfo(Temp)!.IsAvailable);

            await Run(host, "withdraw", "faulty.temp");
            var info = host.Metrics.GetMetricInfo(Temp)!;
            Assert.False(info.IsAvailable);
            Assert.Equal("Core temperature", info.Label);

            // a withdrawn metric with no descriptor stays in the list with the unit it had
            host.Metrics.Publish("other", new MetricId("other.x"), 1, MetricUnit.Rpm);
            host.Metrics.Withdraw(new MetricId("other.x"));
            var x = host.Metrics.GetMetricInfo(new MetricId("other.x"))!;
            Assert.Equal((false, MetricUnit.Rpm, "other"), (x.IsAvailable, x.Unit, x.OwnerModuleId));

            await Run(host, "publish", "faulty.temp", "51");
            Assert.True(host.Metrics.GetMetricInfo(Temp)!.IsAvailable);
            host.Shutdown();
        }
    }

    [Fact]
    public void The_list_is_ordered_by_owner_module_then_metric_id()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        host.Metrics.Publish("zeta", new MetricId("a.one"), 1, MetricUnit.None);
        host.Metrics.Publish("alpha", new MetricId("z.last"), 1, MetricUnit.None);
        host.Metrics.Publish("alpha", new MetricId("b.mid"), 1, MetricUnit.None);
        host.Metrics.Describe("alpha", new MetricDescriptor(new MetricId("a.first"), Text.Literal("First")));

        var order = host.Metrics.GetAllMetricInfo().Select(i => $"{i.OwnerModuleId}/{i.Id}").ToArray();
        Assert.Equal(new[] { "alpha/a.first", "alpha/b.mid", "alpha/z.last", "zeta/a.one" }, order);
        Assert.Equal(order, host.Metrics.GetAllMetricInfo().Select(i => $"{i.OwnerModuleId}/{i.Id}").ToArray()); // stable
    }

    [Fact]
    public void Keys_are_relative_to_the_owner_not_to_the_reader()
    {
        var (host, _, env) = Start();
        using (env)
        {
            // the same key described by a module that has no such text resolves to the id, never to the faulty module's text
            host.Metrics.Describe("ghost", new MetricDescriptor(new MetricId("ghost.temp"), "metric.temp"));
            Assert.Equal("ghost.temp", host.Metrics.GetMetricInfo(new MetricId("ghost.temp"))!.Label);
            Assert.Equal("ghost", host.Metrics.GetMetricInfo(new MetricId("ghost.temp"))!.OwnerModuleId);
            // and the owner's own id resolves with its prefix
            Assert.Equal("Core temperature", host.Metrics.GetMetricInfo(Temp)!.Label);
            host.Shutdown();
        }
    }

    [Fact]
    public void A_shared_id_without_a_module_label_uses_the_host_text()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        var path = Path.Combine(env.Root, "user-lang");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "en.lang"), "@code = en\n@name = English\n\nmetric.cpu.temp = CPU temperature\n");
        host.Languages.LoadDirectory(path);
        host.Metrics.Publish("cpu-source", WellKnownMetrics.CpuTemp, 40, MetricUnit.Celsius);

        Assert.Equal("CPU temperature", host.Metrics.GetMetricInfo(WellKnownMetrics.CpuTemp)!.Label);
    }

    [Fact]
    public async Task The_catalogue_event_and_version_report_changes_and_a_module_sees_them()
    {
        var (host, _, env) = Start();
        using (env)
        {
            long v0 = host.Metrics.CatalogVersion;
            int hostEvents = 0;
            host.Metrics.CatalogChanged += () => hostEvents++;
            async Task<int> ModuleSide() => int.Parse((await Run(host, "catalog")).Split("#changes=")[1].Split('#')[0]);

            int m0 = await ModuleSide();
            host.Metrics.Publish("other", new MetricId("other.a"), 1, MetricUnit.None);       // new id
            host.Metrics.Publish("other", new MetricId("other.a"), 2, MetricUnit.None);       // same id again: no change
            host.Metrics.Describe("other", new MetricDescriptor(new MetricId("other.a"), Text.Literal("A"))); // described
            host.Metrics.Describe("other", new MetricDescriptor(new MetricId("other.a"), Text.Literal("A"))); // identical: no change
            host.Metrics.Withdraw(new MetricId("other.a"));                                    // withdrawn
            host.Metrics.Publish("other", new MetricId("other.a"), 3, MetricUnit.None);       // back
            Assert.Equal(4, hostEvents);
            Assert.Equal(v0 + 4, host.Metrics.CatalogVersion);

            host.SetLanguage("vi");                                                            // labels differ
            Assert.Equal(5, hostEvents);
            host.SetLanguage("en");
            Assert.Equal(6, hostEvents);

            Assert.Equal(6, await ModuleSide() - m0); // the module's own handler got the same events
            host.Shutdown();
        }
    }

    [Fact]
    public void A_double_that_implements_only_the_old_members_gets_empty_defaults()
    {
        IMetricsBus bus = new Minimal();
        Assert.Null(bus.GetMetricInfo(new MetricId("a.b")));
        Assert.Empty(bus.GetAllMetricInfo());
        Assert.Equal(0, bus.CatalogVersion);
        bus.CatalogChanged += () => { };
    }

    private sealed class Minimal : IMetricsBus
    {
        public void Publish(MetricId id, double value, MetricUnit unit) { }
        public IDisposable Subscribe(Action<MetricSample> handler) => throw new NotSupportedException();
        public IDisposable Subscribe(MetricId id, Action<MetricSample> handler) => throw new NotSupportedException();
        public bool TryGetLatest(MetricId id, out MetricSample sample) { sample = null!; return false; }
        public IReadOnlyList<MetricSample> GetLatest() => [];
    }
}

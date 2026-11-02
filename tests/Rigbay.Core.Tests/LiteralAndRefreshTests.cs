using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.Core.Tests;

public class LiteralTextTests
{
    [Fact]
    public void Text_helper_round_trips_and_recognises_literals()
    {
        Assert.Equal("=Gaming", Text.Literal("Gaming"));
        Assert.Equal("=", Text.Literal(null));
        Assert.True(Text.IsLiteral(Text.Literal("x")));
        Assert.False(Text.IsLiteral("row.header"));
        Assert.False(Text.IsLiteral(""));
        Assert.False(Text.IsLiteral(null));
        Assert.Equal("Gaming", Text.Unwrap(Text.Literal("Gaming")));
        Assert.Equal("row.header", Text.Unwrap("row.header"));
    }

    [Fact]
    public void Catalog_returns_a_literal_without_the_marker_and_never_looks_it_up()
    {
        var catalog = new LanguageCatalog(new ListLog());
        Assert.Equal("Profile =1", catalog.T("=Profile =1"));
        Assert.True(catalog.TryGet("=abc", out var text));
        Assert.Equal("abc", text);
        Assert.Equal("Fan 3 at 40", catalog.F("=Fan {0} at {1}", 3, 40));
        Assert.Equal("a{b", catalog.F("=a{b")); // no arguments: no formatting
        Assert.Equal("a{b", catalog.F("=a{b", 1)); // a broken template is returned as it is
    }

    [Fact]
    public void Module_localisation_does_not_add_the_module_prefix_to_a_literal()
    {
        var catalog = new LanguageCatalog(new ListLog());
        catalog.Add(LanguageFile.Parse(new StringReader("@code = en\nmodule.m.name = Real\n"), "t.lang"));
        var loc = new ModuleLocalisation(catalog, "m");
        Assert.Equal("Real", loc.T("name"));
        Assert.Equal("name", loc.T("=name"));
        Assert.Equal("Home (2)", loc.F("=Home ({0})", 2));
        Assert.True(loc.TryGet("=zzz", out var t));
        Assert.Equal("zzz", t);
        Assert.Equal("My profile", loc.Resolve(LocalisedText.Of(Text.Literal("My profile"))));
        Assert.Equal("Gaming 5", loc.Resolve(LocalisedText.Of("=Gaming {0}", 5)));
    }

    [Fact]
    public void LangCheck_does_not_count_literals_as_keys_and_does_not_report_them_unused()
    {
        var english = LanguageFile.Parse(new StringReader("@code = en\nmodule.m.a = A\n"), "en.lang");
        var report = LangCheck.Check(new[] { english }, new LangCheckOptions("module.m.", new[] { "T(\"a\"); X(\"=Device 1\");" }));
        Assert.DoesNotContain("module.m.Device 1", report.Unused);
        Assert.False(report.HasErrors);

        // a literal never counts as using a key of the same name
        var unused = LangCheck.FindUnused(new[] { "module.m.a" }, new LangCheckOptions("module.m.", new[] { "Y(\"=a\")" }));
        Assert.Equal(new[] { "module.m.a" }, unused);
    }

    [Fact]
    public void The_sample_page_shows_device_names_as_literals()
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        var page = host.Find("sample")!.Page!;
        var info = page.EnumerateRows().OfType<InfoRow>().Single(r => Text.IsLiteral(r.HeaderKey));
        Assert.Equal("Sensor 1", host.Find("sample")!.Localisation.T(info.HeaderKey));
        host.Shutdown();
    }
}

public class PageRefreshTests
{
    private const string BuildPrefix = "=build ";

    private static string[] Builds(PageModel? page) =>
        page!.EnumerateRows().Select(r => r.HeaderKey).Where(h => h.StartsWith(BuildPrefix)).ToArray();

    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 1200 && !condition(); i++) await Task.Delay(25);   // up to 30 s: it returns as soon as the condition holds
        return condition();
    }

    private static ButtonRow Button(PageModel? page) => page!.EnumerateRows().OfType<ButtonRow>().First();

    [Fact]
    public async Task A_refresh_request_rebuilds_the_page_and_raises_the_events()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", "refresh");
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.True(m.Enable());
        var first = m.Page!;
        Assert.Equal(new[] { "=build 1" }, Builds(first));

        int entryEvents = 0, hostEvents = 0;
        m.PageRefreshed += _ => Interlocked.Increment(ref entryEvents);
        host.PageRefreshed += e => { Assert.Same(m, e); Interlocked.Increment(ref hostEvents); };

        await Button(first).InvokeAsync(CancellationToken.None);
        Assert.True(await WaitFor(() => Volatile.Read(ref hostEvents) == 1));
        Assert.Equal(1, entryEvents);
        Assert.NotSame(first, m.Page);
        Assert.Same(m.Page, m.LastPage);
        Assert.Equal(new[] { "=build 2" }, Builds(m.Page));
        Assert.Equal(ModuleLifecycle.Running, m.Lifecycle);
        host.Shutdown();
    }

    [Fact]
    public async Task Requests_made_while_a_rebuild_runs_are_coalesced()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", "refresh-slow");
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.True(m.Enable());
        int events = 0;
        m.PageRefreshed += _ => Interlocked.Increment(ref events);

        var button = Button(m.Page);
        for (int i = 0; i < 30; i++) await button.InvokeAsync(CancellationToken.None);

        Assert.True(await WaitFor(() => Volatile.Read(ref events) >= 1));
        await Task.Delay(1200); // let a queued second rebuild finish
        Assert.InRange(events, 1, 2);
        Assert.Equal(events + 1, int.Parse(Builds(m.Page).Single()[BuildPrefix.Length..]));
        host.Shutdown();
    }

    [Fact]
    public async Task A_refresh_after_the_module_is_off_does_nothing()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", "refresh");
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.True(m.Enable());
        var button = Button(m.Page);
        int events = 0;
        host.PageRefreshed += _ => Interlocked.Increment(ref events);

        Assert.True(m.Disable());
        await button.InvokeAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(0, events);
        Assert.Null(m.Page);
        Assert.Equal(ModuleLifecycle.Off, m.Lifecycle);
    }

    [Fact]
    public async Task A_page_that_throws_during_a_refresh_faults_the_module()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", "refresh-throws");
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.True(m.Enable());
        int refreshed = 0, faulted = 0;
        host.PageRefreshed += _ => Interlocked.Increment(ref refreshed);
        host.ModuleFaulted += _ => Interlocked.Increment(ref faulted);

        await Button(m.Page).InvokeAsync(CancellationToken.None);
        Assert.True(await WaitFor(() => m.Lifecycle == ModuleLifecycle.Faulted && Volatile.Read(ref faulted) == 1));
        Assert.Equal(0, refreshed);
        Assert.Equal(1, faulted);
        Assert.Contains("GetPageModel", m.ResolveReason(m.Reason!));
        Assert.Null(m.Page);
        Assert.Empty(host.Tray.Items); // cleaned up like any fault
    }

    [Fact]
    public async Task The_sample_adds_a_device_through_a_page_refresh()
    {
        using var env = new TestEnv();
        var host = env.StartHost("sample");
        var m = host.Find("sample")!;
        int Devices(PageModel? p) => p!.EnumerateRows().OfType<InfoRow>().Count(r => Text.IsLiteral(r.HeaderKey));
        Assert.Equal(1, Devices(m.Page));
        var add = m.Page!.EnumerateRows().OfType<ButtonRow>().Single(r => r.HeaderKey == "row.adddevice.header");

        await add.InvokeAsync(CancellationToken.None);
        Assert.True(await WaitFor(() => Devices(m.Page) == 2));
        var names = m.Page!.EnumerateRows().OfType<InfoRow>().Where(r => Text.IsLiteral(r.HeaderKey)).Select(r => m.Localisation.T(r.HeaderKey));
        Assert.Equal(new[] { "Sensor 1", "Sensor 2" }, names);
        host.Shutdown();
    }
}

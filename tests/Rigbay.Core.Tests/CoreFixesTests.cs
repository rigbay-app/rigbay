using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.Core.Tests;

/// <summary>The A4 fixes to the gaps the app shell found in Rigbay.Core and Rigbay.Abstractions.</summary>
public class CoreFixesTests
{
    private static (RigbayHost Host, ModuleEntry Module) Start(TestEnv env)
    {
        var host = env.StartHost("sample");
        return (host, host.Find("sample")!);
    }

    // ---------------------------------------------------------------- 1. approval detail

    [Fact]
    public async Task A_confirmation_shows_the_new_value_as_text_by_default()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var prompt = new FakeApproval();
        host.ApprovalPrompt = prompt;
        var row = m.Page!.EnumerateRows().OfType<NumberRow>().Single();

        Assert.Equal(EditOutcome.Applied, (await RowEditors.For(m, row).SubmitAsync(55)).Outcome);

        Assert.Equal("55", Assert.Single(prompt.Requests).Detail);
        host.Shutdown();
    }

    [Fact]
    public async Task A_confirmation_can_carry_the_callers_own_detail_text()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var prompt = new FakeApproval();
        host.ApprovalPrompt = prompt;
        var row = m.Page!.EnumerateRows().OfType<NumberRow>().Single();
        var editor = RowEditors.For(m, row);

        editor.SetDraft(60);
        Assert.Equal(EditOutcome.Applied, (await editor.ApplyAsync("Power limit: 45 W -> 60 W")).Outcome);
        Assert.Equal("Power limit: 45 W -> 60 W", prompt.Requests[0].Detail);

        Assert.Equal(EditOutcome.Applied, (await editor.SubmitAsync(50, "to 50 W")).Outcome);
        Assert.Equal("to 50 W", prompt.Requests[1].Detail);

        editor.DetailFormatter = v => $"{v:0} watts";
        Assert.Equal(EditOutcome.Applied, (await editor.SubmitAsync(40)).Outcome);
        Assert.Equal("40 watts", prompt.Requests[2].Detail);
        host.Shutdown();
    }

    [Fact]
    public async Task The_default_detail_of_a_choice_is_its_label_and_of_a_toggle_On_or_Off()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var rows = m.Page!.EnumerateRows().ToList();
        var effect = new HardwareEffect("effect.limit");
        var prompt = new FakeApproval();
        host.ApprovalPrompt = prompt;

        // Same values as the sample's rows, but marked as hardware changes.
        var choice = rows.OfType<ChoiceRow>().Single(r => r.HeaderKey == "row.mode.header") with { HardwareEffect = effect };
        Assert.Equal(EditOutcome.Applied, (await RowEditors.For(m, choice).SubmitAsync("fast")).Outcome);
        Assert.Equal(m.Localisation.T("mode.fast"), prompt.Requests[0].Detail);

        var toggle = rows.OfType<ToggleRow>().First() with { HardwareEffect = effect };
        Assert.Equal(EditOutcome.Applied, (await RowEditors.For(m, toggle).SubmitAsync(true)).Outcome);
        Assert.Equal(host.Languages.T("core.value.on"), prompt.Requests[1].Detail);
        host.Shutdown();
    }

    [Fact]
    public async Task A_button_confirmation_has_a_detail_too()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var prompt = new FakeApproval();
        host.ApprovalPrompt = prompt;
        var ran = 0;
        var button = new ButtonRow("row.reset.header", "row.reset.button", _ => { ran++; return Task.FromResult(ActionResult.Ok()); })
        { HardwareEffect = new HardwareEffect("effect.limit") };

        Assert.True((await RowEditors.InvokeButtonAsync(m, button)).Succeeded);
        Assert.Equal(m.Localisation.T("row.reset.button"), prompt.Requests[0].Detail);

        Assert.True((await RowEditors.InvokeButtonAsync(m, button, detail: "Reset to 45 W")).Succeeded);
        Assert.Equal("Reset to 45 W", prompt.Requests[1].Detail);
        Assert.Equal(2, ran);
        host.Shutdown();
    }

    // ---------------------------------------------------------------- 2. editor lifetime

    [Fact]
    public void One_editor_per_row_instance_and_a_disposed_one_stops_following_the_module()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var row = m.Page!.EnumerateRows().OfType<TextRow>().Single();
        var editor = RowEditors.For(m, row);
        Assert.Same(editor, RowEditors.For(m, row));
        Assert.NotSame(editor, RowEditors.For(m, row with { }));

        var raised = 0;
        editor.Changed += () => raised++;
        row.Value.Value = "one";
        Assert.Equal(1, raised);

        editor.Dispose();
        editor.Dispose(); // twice is fine
        row.Value.Value = "two";
        Assert.Equal(1, raised);
        host.Shutdown();
    }

    [Fact]
    public void Editors_are_disposed_when_the_module_stops_and_new_ones_come_with_the_new_page()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var row = m.Page!.EnumerateRows().OfType<TextRow>().Single();
        var editor = RowEditors.For(m, row);
        var raised = 0;
        editor.Changed += () => raised++;

        Assert.True(m.Disable());
        row.Value.Value = "while off";
        Assert.Equal(0, raised); // unsubscribed by the stop

        Assert.True(m.Enable());
        var again = m.Page!.EnumerateRows().OfType<TextRow>().Single();
        Assert.NotSame(editor, RowEditors.For(m, again));
        host.Shutdown();
    }

    [Fact]
    public void A_faulted_module_drops_its_editors()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var row = m.Page!.EnumerateRows().OfType<TextRow>().Single();
        var editor = RowEditors.For(m, row);
        var raised = 0;
        editor.Changed += () => raised++;

        m.FaultNow("test", new InvalidOperationException("x"));
        row.Value.Value = "after fault";
        Assert.Equal(0, raised);
        host.Shutdown();
    }

    // ---------------------------------------------------------------- 3. no text

    [Fact]
    public void A_text_without_a_key_is_no_text()
    {
        Assert.True(default(LocalisedText).IsEmpty);
        Assert.True(new LocalisedText(null!).IsEmpty);
        Assert.True(new LocalisedText("").IsEmpty);
        Assert.False(LocalisedText.Of("a").IsEmpty);

        using var env = new TestEnv();
        var (host, m) = Start(env);
        Assert.Equal("", m.Localisation.Resolve(default));
        host.Shutdown();
    }

    [Fact]
    public async Task A_validator_that_returns_an_empty_text_means_no_error()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var value = new ObservableValue<string>("");
        var row = new TextRow("row.label.header", value) { Apply = ApplyMode.ApplyRevert, Validate = _ => default(LocalisedText) };
        var editor = RowEditors.For(m, row);

        editor.SetDraft("anything");
        Assert.Null(editor.Error);
        Assert.Equal(EditOutcome.Applied, (await editor.ApplyAsync()).Outcome);
        Assert.Equal("anything", value.Value);

        var number = new NumberRow("row.limit.header", new ObservableValue<double>(20), 15, 65) { Validate = _ => new LocalisedText("") };
        Assert.Null(RowEditors.For(m, number).Error);
        host.Shutdown();
    }

    [Fact]
    public async Task An_empty_approval_detail_text_shows_no_detail()
    {
        using var env = new TestEnv();
        var (host, m) = Start(env);
        var prompt = new FakeApproval();
        host.ApprovalPrompt = prompt;
        await host.RequestApprovalAsync(m, new HardwareEffect("effect.limit"), default(LocalisedText), CancellationToken.None);
        await host.RequestApprovalAsync(m, new HardwareEffect("effect.limit"), LocalisedText.Of("row.reset.button"), CancellationToken.None);
        Assert.Null(prompt.Requests[0].Detail);
        Assert.Equal(m.Localisation.T("row.reset.button"), prompt.Requests[1].Detail);
        host.Shutdown();
    }

    // ---------------------------------------------------------------- 4. LastPage

    [Fact]
    public void LastPage_survives_a_stop_and_a_fault()
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        using var host = env.NewHost();
        host.Discover();
        var m = host.Find("sample")!;
        Assert.Null(m.LastPage);

        Assert.True(m.Enable());
        var page = m.Page;
        Assert.NotNull(page);
        Assert.Same(page, m.LastPage);

        Assert.True(m.Disable());
        Assert.Null(m.Page);
        Assert.Same(page, m.LastPage);

        Assert.True(m.Enable());
        Assert.NotSame(page, m.LastPage); // the page is built again
        var second = m.LastPage;

        m.FaultNow("test", new InvalidOperationException("x"));
        Assert.Null(m.Page);
        Assert.Same(second, m.LastPage);
    }

    // ---------------------------------------------------------------- 5. F12

    [Theory]
    [InlineData("F12")]
    [InlineData("f12")]
    [InlineData("Ctrl+F12")]
    [InlineData("Ctrl+Alt+Shift+Win+F12")]
    public void F12_is_not_a_hotkey(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
        Assert.Throws<FormatException>(() => HotkeyGesture.Parse(text));
    }

    [Fact]
    public void F12_gives_a_clear_reason_and_its_neighbours_still_work()
    {
        var ex = Assert.Throws<ArgumentException>(() => new HotkeyGesture(HotkeyModifiers.Ctrl, "F12"));
        Assert.Contains("F12", ex.Message);
        Assert.Contains("debugger", ex.Message);
        Assert.Contains("debugger", HotkeyGesture.ReservedReason("f12"));
        Assert.Null(HotkeyGesture.ReservedReason("F11"));
        Assert.Null(HotkeyGesture.ReservedReason("A"));
        Assert.Null(HotkeyGesture.ReservedReason(null));
        Assert.True(HotkeyGesture.TryParse("Ctrl+F11", out var f11));
        Assert.Equal("Ctrl+F11", f11!.Value.ToString());
        Assert.True(HotkeyGesture.TryParse("F13", out _));
        Assert.True(HotkeyGesture.TryParse("F1", out _));
    }

    [Fact]
    public void A_stored_F12_binding_is_ignored_and_the_default_is_used()
    {
        using var env = new TestEnv();
        var file = Path.Combine(env.Root, "hotkeys.json");
        File.WriteAllText(file, """{ "mod.toggle": "Ctrl+F12" }""");
        var registry = new HotkeyRegistry(new ListLog(), file);
        var entry = registry.Register("mod", "toggle", "k", HotkeyGesture.Parse("Ctrl+F5"), () => { });
        Assert.Equal("Ctrl+F5", entry.Gesture.Value!.Value.ToString());
    }

    // ---------------------------------------------------------------- 6. manifest messages as keys

    [Fact]
    public void Manifest_issues_carry_a_key_and_arguments_and_the_english_text_comes_from_Core()
    {
        var result = ManifestReader.Parse("""{ "manifestVersion": 1, "id": "BAD", "name": "x" }""", null);
        var id = Assert.Single(result.Issues, i => i.Path == "id");
        Assert.Equal("core.manifest.id-pattern", id.Key);
        Assert.Equal("use lowercase letters, digits and single dashes", id.Message);

        var folder = ManifestReader.Parse(TinyManifest("other"), "wanted").Issues.Single(i => i.Path == "id");
        Assert.Equal("core.manifest.id-folder", folder.Key);
        Assert.Equal(new object[] { "wanted" }, folder.Args);
        Assert.Equal("must equal the folder name 'wanted'", folder.Message);

        var longName = TinyManifest("other").Replace("\"name\": \"T\"", "\"name\": \"" + new string('n', 61) + "\"");
        var tooLong = ManifestReader.Parse(longName, null).Issues.Single(i => i.Path == "name");
        Assert.Equal("core.manifest.length-range", tooLong.Key);
        Assert.Equal("must be 1 to 60 characters", tooLong.Message);
    }

    [Fact]
    public void Manifest_issues_are_shown_in_the_users_language()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        host.SetLanguage("vi");
        var issue = ManifestReader.Parse(TinyManifest("other"), "wanted").Issues.Single(i => i.Path == "id");
        Assert.Equal("phải trùng tên thư mục 'wanted'", issue.Localise(host.Languages));
        Assert.Equal("id: phải trùng tên thư mục 'wanted'", issue.LocaliseLine(host.Languages));
        host.SetLanguage("en");
        Assert.Equal("id: must equal the folder name 'wanted'", issue.LocaliseLine(host.Languages));
    }

    [Fact]
    public void An_invalid_module_reason_follows_the_language()
    {
        using var env = new TestEnv();
        env.AddModule("faulty", editManifest: json => json.Replace("\"version\": \"0.1.0\"", "\"version\": \"nope\""));
        using var host = env.NewHost();
        host.Discover();
        var m = host.Modules.Single();
        Assert.Equal(ModuleLifecycle.Invalid, m.Lifecycle);
        Assert.Contains("must be a semantic version", m.ResolveReason(m.Reason!));
        host.SetLanguage("vi");
        var vi = m.ResolveReason(m.Reason!);
        Assert.Contains("phiên bản ngữ nghĩa", vi);
        Assert.DoesNotContain("semantic", vi);
        Assert.DoesNotContain("must be", vi);
    }

    [Fact]
    public void Every_manifest_key_in_the_reader_has_an_english_and_a_vietnamese_text()
    {
        var source = File.ReadAllText(FindFile("src/Rigbay.Core/Manifest/ModuleManifest.cs"));
        var used = System.Text.RegularExpressions.Regex.Matches(source, "\"(core\\.manifest\\.[a-z0-9-]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.True(used.Count > 20);

        using var env = new TestEnv();
        using var host = env.NewHost();
        foreach (var code in new[] { "en", "vi" })
        {
            host.SetLanguage(code);
            foreach (var key in used) Assert.True(host.Languages.TryGet(key, out _), $"{code}: {key}");
        }
    }

    [Fact]
    public void Cores_own_language_files_pass_LangCheck_strict()
    {
        var assembly = typeof(RigbayHost).Assembly;
        var files = new List<LanguageFile>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Rigbay.Core.lang.") && n.EndsWith(".lang")))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            files.Add(LanguageFile.Parse(reader, name["Rigbay.Core.lang.".Length..]));
        }
        Assert.True(files.Count >= 2);
        var report = LangCheck.Check(files);
        Assert.False(report.HasErrors, report.ToText());
        Assert.False(report.HasWarnings, report.ToText());
    }

    // ---------------------------------------------------------------- 7. version and list

    [Fact]
    public async Task The_version_verb_names_host_abstractions_and_api()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        var outcome = await host.ExecuteCommandAsync(new[] { "version" });
        Assert.Equal(CommandOutcome.Ok, outcome.ExitCode);
        var lines = outcome.Output.Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.Equal("Rigbay " + host.Options.HostVersion, lines[0]);
        Assert.Equal("Rigbay.Abstractions " + typeof(IModule).Assembly.GetName().Version!.ToString(3), lines[1]);
        Assert.Equal("Host API " + HostApi.Current, lines[2]);
    }

    [Fact]
    public async Task The_list_verb_shows_every_module_with_its_state()
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        env.AddModule("faulty", editManifest: json => json.Replace("\"version\": \"0.1.0\"", "\"version\": \"nope\""));
        using var host = env.NewHost();
        host.Discover();
        host.Find("sample")!.Enable();

        var outcome = await host.ExecuteCommandAsync(new[] { "list" });

        Assert.Equal(CommandOutcome.Ok, outcome.ExitCode);
        var lines = outcome.Output.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("faulty  Failed  -  disabled  ", lines[0]);
        Assert.Matches(@"^sample  Ready  \d+\.\d+\.\d+\S*  enabled$", lines[1]);
    }

    [Fact]
    public async Task List_with_no_modules_says_so_and_a_module_cannot_be_called_version_or_list()
    {
        using var env = new TestEnv();
        using var host = env.NewHost();
        Assert.Equal("No modules are installed", (await host.ExecuteCommandAsync(new[] { "list" })).Output);
        var bad = ManifestReader.Parse(TinyManifest("version"), null);
        Assert.Contains(bad.Issues, i => i.Key == "core.manifest.id-reserved");
    }

    private static string TinyManifest(string id) => $$"""
        { "manifestVersion": 1, "id": "{{id}}", "name": "T", "version": "1.0.0", "author": "a", "description": "d",
          "entry": { "assembly": "T.dll", "type": "T.Main" }, "hostApi": ">=0.1 <1.0", "group": "other" }
        """;

    private static string FindFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }
}

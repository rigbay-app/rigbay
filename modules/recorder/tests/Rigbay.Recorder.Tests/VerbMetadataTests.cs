using Rigbay.Abstractions;
using Xunit;

namespace Rigbay.Recorder.Tests;

/// <summary>
/// Host API 0.1.4: the verbs say what they do; the preview comes from the arguments and the page's settings, never from the bus; an undo is reported only when a recording started.
/// The recorder publishes no metric, so it has no poll rate to decide (it reads the bus through its own subscription).
/// </summary>
public class VerbMetadataTests
{
    private static CommandResult Run(ModuleRig rig, string verb, params string[] args) => rig.Host.CommandRegistry.RunSync(verb, args);

    [Fact]
    public void Status_is_read_only_and_start_and_stop_are_declared_writes_of_the_setting_kind()
    {
        using var rig = new ModuleRig();
        var infos = rig.Host.CommandRegistry.Infos;
        Assert.Equal(new[] { "start", "status", "stop" }, infos.Keys.OrderBy(k => k));

        Assert.Equal(VerbAccess.ReadOnly, infos["status"].Access);
        Assert.Null(infos["status"].Effect);

        var en = FakeLocalisation.Load("en");
        foreach (var verb in new[] { "start", "stop" })
        {
            Assert.Equal(VerbAccess.Writes, infos[verb].Access);
            Assert.Equal(EffectKind.SystemSetting, infos[verb].Effect!.Kind);   // not hardware: the question reads "Change a system setting?" and the effect text says what really happens
            Assert.NotNull(infos[verb].Preview);
            Assert.True(en.ContainsKey("module.recorder." + infos[verb].Effect!.DescriptionKey));
        }
        Assert.Equal("stop", infos["start"].UndoVerb);
        Assert.Null(infos["stop"].UndoVerb);
        Assert.True(en.ContainsKey("module.recorder." + infos["start"].UsageKey));
    }

    [Theory]
    [InlineData("en", "Record the metrics selected on the page every 1 s until stopped, to ")]
    [InlineData("vi", "Ghi các số đo đã chọn trên trang, mỗi 1 giây, đến khi dừng, vào ")]
    public void The_start_preview_with_no_arguments_is_the_page_settings(string language, string start)
    {
        using var rig = new ModuleRig(language);
        var text = rig.Host.CommandRegistry.Infos["start"].Preview!(Array.Empty<string>())!.Value;
        Assert.Equal(start + rig.Folder.Path, rig.Host.Loc.Resolve(text));
    }

    [Fact]
    public void The_start_preview_takes_the_arguments_over_the_settings()
    {
        using var rig = new ModuleRig();
        var preview = rig.Host.CommandRegistry.Infos["start"].Preview!;
        Assert.Equal("Record the metrics selected on the page every 5 s for 10 min, to " + rig.Folder.Path,
            rig.Host.Loc.Resolve(preview(new[] { "--every", "5", "--minutes", "10" })!.Value));
        Assert.Equal("Record z.a, a.x every 2 s until stopped, to " + rig.Folder.Path,
            rig.Host.Loc.Resolve(preview(new[] { "--every=2", "--minutes=0", "--metrics", "z.a,a.x" })!.Value));
        Assert.Equal("Record a.x every 1 s for 3 min, to " + rig.Folder.Path,
            rig.Host.Loc.Resolve(preview(new[] { "--minutes", "3", "--metrics", "a.x" })!.Value));
    }

    [Fact]
    public void The_start_preview_follows_the_settings_the_page_keeps()
    {
        var rig = new ModuleRig(initialize: false);
        rig.Host.SettingsStore.Stored = new RecorderSettings { PeriodSeconds = 10, DurationMinutes = 15 };
        rig.Module.Initialize(rig.Host);
        using (rig)
            Assert.Equal("Record the metrics selected on the page every 10 s for 15 min, to " + rig.Folder.Path,
                rig.Host.Loc.Resolve(rig.Host.CommandRegistry.Infos["start"].Preview!(Array.Empty<string>())!.Value));
    }

    [Fact]
    public void A_start_preview_of_arguments_the_verb_would_refuse_is_none()
    {
        using var rig = new ModuleRig();
        var preview = rig.Host.CommandRegistry.Infos["start"].Preview!;
        Assert.Null(preview(new[] { "--every", "0" }));
        Assert.Null(preview(new[] { "--bogus" }));
        Assert.Null(preview(new[] { "--metrics", "BAD ID" }));
    }

    [Fact]
    public void The_start_preview_does_not_depend_on_what_the_bus_holds_or_whether_it_is_recording()
    {
        using var rig = new ModuleRig();
        var preview = rig.Host.CommandRegistry.Infos["start"].Preview!;
        var before = preview(Array.Empty<string>())!.Value;

        rig.PublishSome();                       // the live metrics change
        Assert.Equal(0, Run(rig, "start").ExitCode);   // and so does the recording state
        var after = preview(Array.Empty<string>())!.Value;

        Assert.Equal(before.Key, after.Key);
        Assert.Equal(before.Args, after.Args);
        rig.Module.Engine!.Stop();
    }

    [Fact]
    public void The_stop_preview_says_what_it_does_in_both_languages()
    {
        foreach (var (language, expected) in new[] { ("en", "Stop the recording and close the file"), ("vi", "Dừng ghi và đóng tệp") })
        {
            using var rig = new ModuleRig(language);
            Assert.Equal(expected, rig.Host.Loc.Resolve(rig.Host.CommandRegistry.Infos["stop"].Preview!(Array.Empty<string>())!.Value));
        }
    }

    [Fact]
    public void A_start_that_began_a_recording_reports_an_empty_undo_and_one_that_did_not_reports_none()
    {
        using var rig = new ModuleRig();
        rig.PublishSome();

        var started = Run(rig, "start");
        Assert.Equal(0, started.ExitCode);
        Assert.NotNull(started.UndoArgs);   // an empty list: "run stop with no arguments"
        Assert.Empty(started.UndoArgs!);

        var again = Run(rig, "start");      // already recording: refused
        Assert.Equal(1, again.ExitCode);
        Assert.Null(again.UndoArgs);

        Assert.Null(Run(rig, "start", "--every", "0").UndoArgs);   // bad arguments
        Assert.Null(Run(rig, "stop").UndoArgs);
        rig.WaitUntilFinished();
    }

    [Fact]
    public void Status_and_stop_report_no_undo_even_when_they_ran()
    {
        using var rig = new ModuleRig();
        Assert.Null(Run(rig, "status").UndoArgs);
        Assert.Null(Run(rig, "stop").UndoArgs);   // nothing recording
    }
}

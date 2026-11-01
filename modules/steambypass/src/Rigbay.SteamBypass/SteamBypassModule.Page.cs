using Rigbay.Abstractions;
using Rigbay.SteamBypass.Check;

namespace Rigbay.SteamBypass;

public sealed partial class SteamBypassModule
{
    /// <inheritdoc />
    public PageModel? GetPageModel(IHostServices host)
    {
        // The switch: changes the Windows proxy setting, so the host shows the exact effect and asks, for On and for Off, and offers Revert.
        // No SafeRevert: reverting an Off turns the bypass on again, which is a change of the Windows setting.
        var bypass = new List<Row>
        {
            new ToggleRow("row.bypass.header", _bypass)
            {
                DescriptionKey = "row.bypass.description",
                HardwareEffect = new HardwareEffect("effect.bypass") { Kind = EffectKind.SystemSetting, RevertDescriptionKey = "effect.bypass-revert" },
            },
            new InfoRow(Severity.Warning, "info.port80.header") { DescriptionKey = "info.port80.description", IsOpen = _port80Warning },
            new InfoRow(Severity.Info, "info.steam-restart.header") { DescriptionKey = "info.steam-restart.description", IsOpen = _steamRestartHint },
        };

        var status = new List<Row>
        {
            new ValueRow("row.listening.header", _listenText) { DescriptionKey = "row.listening.description" },
            new ReadingRow("row.active.header", ActiveMetric) { DescriptionKey = "row.active.description", Decimals = 0 },
            new ReadingRow("row.errors.header", ErrorsMetric) { DescriptionKey = "row.errors.description", Decimals = 0 },
            new ValueRow("row.steam.header", _steamText) { DescriptionKey = "row.steam.description" },
            new ValueRow("row.problem.header", _problemText),
            new ValueRow("row.domains.header", _domainsText) { DescriptionKey = "row.domains.description" },
        };

        var checks = new List<Row>();
        foreach (var spec in CheckSpec.All)
            checks.Add(new ValueRow(spec.TitleKey, _checkTexts[spec.Id]) { DescriptionKey = spec.SubKey });
        checks.Add(new ButtonRow("row.test.header", "button.test", RunTestFromPageAsync) { DescriptionKey = "row.test.description" });

        var settings = new List<Row>
        {
            // It makes the module change the Windows setting by itself at every start, so turning it on is asked about like the switch.
            new ToggleRow("row.auto-on.header", _autoOn)
            {
                DescriptionKey = "row.auto-on.description",
                HardwareEffect = new HardwareEffect("effect.auto-on") { Kind = EffectKind.SystemSetting },
            },
        };

        return new PageModel
        {
            Groups = new[]
            {
                new RowGroup("group.bypass", bypass),
                new RowGroup("group.status", status),
                new RowGroup("group.check", checks),
                new RowGroup("group.settings", settings),
            },
            Summary = new PageSummary(_stateLine, new[] { ActiveMetric, ErrorsMetric }),
        };
    }

    private async Task<ActionResult> RunTestFromPageAsync(CancellationToken ct)
    {
        var outcome = await RunChecksAsync(ct).ConfigureAwait(false);
        return outcome.Failed == 0
            ? ActionResult.Ok(LocalisedText.Of("test.passed", outcome.Total))
            : ActionResult.Fail("test.failed", outcome.Failed, outcome.Total);
    }
}

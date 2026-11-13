using Rigbay.Abstractions;

namespace Rigbay.SteamBypass;

public sealed partial class SteamBypassModule
{
    // The rows that carry an editor or a button state in the host are made once and kept: the page is rebuilt when the bypass turns on or
    // off (the Status group comes and goes), and a new row instance would lose its "Applied" status and its Revert link in the host.
    private ToggleRow? _bypassRow;
    private ToggleRow? _autoOnRow;
    private ButtonRow? _testRow;

    // 1 while the page that was built last shows the Status group (the bypass was on), 0 while it does not, -1 before the first page.
    private int _pageShowsOn = -1;

    // Whether the bypass was on at the last refresh of the texts. The page reads this, never the controller: the controller's lock is held
    // for the whole of a change, and a change stuck in the registry must not hold the thread that builds the page.
    private volatile bool _knownOn;

    /// <inheritdoc />
    /// <remarks>
    /// Review of November 2026 (SA-1): the bypass switch is the page's main control (<see cref="PageModel.Primary"/>) with its state in a live
    /// line; the Status group (readings, listening address, the two warnings) exists only while the bypass is on, so a page with the bypass
    /// off shows no "Not available" tiles and no empty rows; the connection test is one row whose live line lists the four checks after a run.
    /// </remarks>
    public PageModel? GetPageModel(IHostServices host)
    {
        bool on = _knownOn;
        Volatile.Write(ref _pageShowsOn, on ? 1 : 0);

        // The switch changes the Windows proxy setting, so the host shows the exact effect and asks, for On and for Off, and offers Revert.
        // No SafeRevert: reverting an Off turns the bypass on again, which is a change of the Windows setting.
        _bypassRow ??= new ToggleRow("row.bypass.header", _bypass)
        {
            DescriptionKey = "row.bypass.description",
            HelpKey = "row.bypass.help",
            Line = _bypassLine,
            HardwareEffect = BypassEffect,
        };
        // It makes the module change the Windows setting by itself at every start, so turning it on is asked about like the switch.
        _autoOnRow ??= new ToggleRow("row.auto-on.header", _autoOn)
        {
            HelpKey = "row.auto-on.help",
            HardwareEffect = new HardwareEffect("effect.auto-on") { Kind = EffectKind.SystemSetting },
        };
        _testRow ??= new ButtonRow("row.test.header", "button.test", RunTestFromPageAsync)
        {
            DescriptionKey = "row.test.description",
            HelpKey = "row.test.help",
            Line = _testLine,
        };

        var groups = new List<RowGroup>();
        if (on)
        {
            groups.Add(new RowGroup("group.status", new Row[]
            {
                new InfoRow(Severity.Warning, "info.port80.header") { DescriptionKey = "info.port80.description", IsOpen = _port80Warning },
                new InfoRow(Severity.Info, "info.steam-restart.header") { DescriptionKey = "info.steam-restart.description", IsOpen = _steamRestartHint },
                // The open connections are the reading people watch: a tile and a chart of their own. The errors are a count that only grows,
                // so they get a tile with its trend but no line in the chart.
                new ReadingRow("row.active.header", ActiveMetric) { Decimals = 0, Tile = TileHint.Always, ChartGroupKey = "chart.connections" },
                new ReadingRow("row.errors.header", ErrorsMetric) { DescriptionKey = "row.errors.description", Decimals = 0, ChartGroupKey = "" },
                new ValueRow("row.listening.header", _listenText),
            }));
        }
        groups.Add(new RowGroup("group.test", new Row[] { _testRow }));
        groups.Add(new RowGroup("group.settings", new Row[]
        {
            _autoOnRow,
            new ValueRow("row.domains.header", _domainsText) { HelpKey = "row.domains.help" },
        }));

        return new PageModel
        {
            Primary = _bypassRow,
            Groups = groups,
            Summary = new PageSummary(_stateLine, new[] { ActiveMetric, ErrorsMetric }),
        };
    }

    /// <summary>The Status group is on the page only while the bypass is on: ask for a new page when that changed. Safe from any thread.</summary>
    private void RefreshPageIfOnChanged(bool on)
    {
        int shown = Volatile.Read(ref _pageShowsOn);
        if (shown < 0 || (shown == 1) == on) return;
        Volatile.Write(ref _pageShowsOn, on ? 1 : 0);   // one request per change; the rebuild writes the same value again
        try { _host?.RequestPageRefresh(); }
        catch (Exception ex) { _host?.Log.Warn("Asking for a new page failed", ex); }
    }

    private async Task<ActionResult> RunTestFromPageAsync(CancellationToken ct)
    {
        var outcome = await RunChecksAsync(ct).ConfigureAwait(false);
        if (outcome.Results.Count == 0) return ActionResult.Fail("test.busy");
        return outcome.Failed == 0
            ? ActionResult.Ok(LocalisedText.Of("test.passed", outcome.Total))
            : ActionResult.Fail("test.failed", outcome.Failed, outcome.Total);
    }
}

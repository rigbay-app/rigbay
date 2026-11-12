using Rigbay.Abstractions;

namespace Rigbay.Tunnel;

public sealed partial class TunnelModule
{
    /// <inheritdoc />
    public PageModel? GetPageModel(IHostServices host)
    {
        var loc = host.Localisation;
        TunnelUi[] tunnels;
        lock (_uiGate) tunnels = _ui.Values.Where(u => u.Present).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        var groups = new List<RowGroup>
        {
            new("group.info", new Row[]
            {
                new InfoRow(Severity.Warning, "info.store-unreadable.header") { DescriptionKey = "info.store-unreadable.description", IsOpen = _storeUnreadable },
                new InfoRow(Severity.Info, "info.no-tunnels.header") { DescriptionKey = "info.no-tunnels.description", IsOpen = _noTunnels },
            }),
        };

        if (tunnels.Length > 0)
        {
            var rows = new List<Row>();
            foreach (var ui in tunnels) rows.Add(TunnelRow(host, ui, expanded: tunnels.Length == 1));
            groups.Add(new RowGroup("group.tunnels", rows));
        }

        var links = new List<PageLink> { new("link.wireguard", "https://www.wireguard.com/install/") };
        foreach (var ui in tunnels)
        {
            var address = ui.PublicAddress.Value;
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                links.Add(new PageLink(Text.Literal(loc.F("link.public", ui.Name)), uri.AbsoluteUri));
        }

        return new PageModel { Groups = groups, Links = links, Summary = new PageSummary(_stateLine) };
    }

    private static Row TunnelRow(IHostServices host, TunnelUi ui, bool expanded)
    {
        var loc = host.Localisation;

        // The switch changes a Windows service, so the host shows the effect and asks, for On and for Off, and offers Revert (which asks again).
        // The text names the tunnel; the question Rigbay asks itself for the tray, a hotkey and a verb says "Turn on tunnel NAME".
        var toggleEffect = new HardwareEffect(Text.Literal(loc.F("effect.page-toggle", ui.Name))) { Kind = EffectKind.SystemSetting };
        var children = new List<Row>
        {
            new ToggleRow("row.on.header", ui.On) { DescriptionKey = "row.on.description", HardwareEffect = toggleEffect },
            new ValueRow("row.status.header", ui.Status),
            new ValueRow("row.problem.header", ui.Problem),
            new ValueRow("row.endpoint.header", ui.Endpoint) { DescriptionKey = "row.endpoint.description" },
            new ValueRow("row.transfer.header", ui.Transfer) { DescriptionKey = "row.transfer.description" },
            new ReadingRow("row.handshake.header", new MetricId(ui.HandshakeMetric)) { DescriptionKey = "row.handshake.description", Decimals = 0 },
            new ReadingRow("row.rx.header", new MetricId(ui.RxMetric)) { Decimals = 0 },
            new ReadingRow("row.tx.header", new MetricId(ui.TxMetric)) { Decimals = 0 },
            new ToggleRow("row.start.header", ui.StartWithWindows)
            {
                DescriptionKey = "row.start.description",
                Enabled = ui.StartEnabled,
                HardwareEffect = new HardwareEffect(Text.Literal(loc.F("effect.start", ui.Name))) { Kind = EffectKind.SystemSetting },
            },
            new NumberRow("row.autooff.header", ui.AutoOffHours, 0, 168, 1)
            {
                DescriptionKey = "row.autooff.description",
                Apply = ApplyMode.ApplyRevert,
                Validate = v => v == Math.Floor(v) ? null : LocalisedText.Of("validate.whole"),
                HardwareEffect = new HardwareEffect(Text.Literal(loc.F("effect.autooff", ui.Name))) { Kind = EffectKind.SystemSetting },
            },
            new NumberRow("row.port.header", ui.CheckPort, 0, 65535, 1)
            {
                DescriptionKey = "row.port.description",
                Apply = ApplyMode.ApplyRevert,
                Validate = v => v == Math.Floor(v) ? null : LocalisedText.Of("validate.whole"),
            },
            new ValueRow("row.local.header", ui.Local),
            new TextRow("row.public.header", ui.PublicAddress)
            {
                DescriptionKey = "row.public.description",
                PlaceholderKey = "row.public.placeholder",
                MaxLength = 200,
                Apply = ApplyMode.ApplyRevert,
            },
        };
        if (ui.Hotkey is { } hotkey) children.Add(new ShortcutRow("row.hotkey.header", hotkey.Gesture) { DescriptionKey = "row.hotkey.description" });

        return new ExpanderRow(Text.Literal(ui.Name), children) { IsExpanded = expanded };
    }
}

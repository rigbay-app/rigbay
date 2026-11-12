using System.Globalization;
using Rigbay.Abstractions;

namespace Rigbay.Tunnel;

public sealed partial class TunnelModule
{
    /// <summary>
    /// The choices of "Turn off after", in hours (0 = never). The label keys are spelled out so the language check sees them. An older setting
    /// that is not one of these (the number box of 0.1.0 allowed any whole number up to 168) is added to the list as it is.
    /// </summary>
    internal static readonly (int Hours, string LabelKey)[] AutoOffChoices =
    {
        (0, "autooff.0"), (1, "autooff.1"), (2, "autooff.2"), (4, "autooff.4"), (8, "autooff.8"), (24, "autooff.24"),
    };

    /// <inheritdoc />
    /// <remarks>
    /// Review of November 2026 (TN-1, TN-2): each tunnel is one row whose header carries its on switch (<see cref="ExpanderRow.HeaderRow"/>) and a
    /// live line (connected, handshake age, receive and send rates, a problem), so the list says which tunnel is on without opening anything.
    /// Inside are only its settings, and the server and traffic while it is on. No reading rows: a tile named "Receiving" could not say which
    /// tunnel it belongs to, so the rates are in each tunnel's own line (the metrics are still on the bus for the dashboard and the recorder).
    /// </remarks>
    public PageModel? GetPageModel(IHostServices host)
    {
        var loc = host.Localisation;
        TunnelUi[] tunnels;
        lock (_uiGate) tunnels = _ui.Values.Where(u => u.Present).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        // One group: the two messages (shown only when they apply) and then one row per tunnel.
        var rows = new List<Row>
        {
            new InfoRow(Severity.Warning, "info.store-unreadable.header") { DescriptionKey = "info.store-unreadable.description", IsOpen = _storeUnreadable },
            new InfoRow(Severity.Info, "info.no-tunnels.header") { DescriptionKey = "info.no-tunnels.description", IsOpen = _noTunnels },
        };
        foreach (var ui in tunnels) rows.Add(TunnelRow(host, ui));
        var groups = new List<RowGroup> { new("group.tunnels", rows) };

        var links = new List<PageLink> { new("link.wireguard", "https://www.wireguard.com/install/") };
        foreach (var ui in tunnels)
        {
            var address = ui.PublicAddress.Value;
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                links.Add(new PageLink(Text.Literal(loc.F("link.public", ui.Name)), uri.AbsoluteUri));
        }

        return new PageModel { Groups = groups, Links = links, Summary = new PageSummary(_stateLine) };
    }

    private static Row TunnelRow(IHostServices host, TunnelUi ui)
    {
        var rows = ui.Rows ??= MakeRows(host, ui);
        // Closed by default: the header already says the state and holds the switch; inside are settings, used rarely.
        return new ExpanderRow(Text.Literal(ui.Name), rows.Children) { HeaderRow = rows.On, Line = ui.Line, HelpKey = "row.tunnel.help" };
    }

    private static TunnelRows MakeRows(IHostServices host, TunnelUi ui)
    {
        var loc = host.Localisation;

        // The switch changes a Windows service, so the host shows the effect and asks, for On and for Off, and offers Revert (which asks again).
        // Its header is the tunnel's name: the switch's accessible name, and the approval reads "example-tunnel: Off → On".
        var on = new ToggleRow(Text.Literal(ui.Name), ui.On)
        {
            HardwareEffect = new HardwareEffect(Text.Literal(loc.F("effect.page-toggle", ui.Name))) { Kind = EffectKind.SystemSetting },
        };

        int hours = int.TryParse(ui.AutoOff.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : 0;
        var autoOff = AutoOffChoices.Select(c => new ChoiceOption(Id(c.Hours), c.LabelKey)).ToList();
        if (hours > 0 && AutoOffChoices.All(c => c.Hours != hours))
            autoOff.Add(new ChoiceOption(Id(hours), Text.Literal(loc.F("autooff.custom", hours))));
        autoOff.Sort((a, b) => int.Parse(a.Id, CultureInfo.InvariantCulture).CompareTo(int.Parse(b.Id, CultureInfo.InvariantCulture)));

        var children = new List<Row>
        {
            new ToggleRow("row.start.header", ui.StartWithWindows)
            {
                HelpKey = "row.start.help",
                Line = ui.StartLine,
                Enabled = ui.StartEnabled,
                HardwareEffect = new HardwareEffect(Text.Literal(loc.F("effect.start", ui.Name))) { Kind = EffectKind.SystemSetting },
            },
            new ChoiceRow("row.autooff.header", autoOff, ui.AutoOff)
            {
                HelpKey = "row.autooff.help",
                HardwareEffect = new HardwareEffect(Text.Literal(loc.F("effect.autooff", ui.Name))) { Kind = EffectKind.SystemSetting },
            },
            new TextRow("row.public.header", ui.PublicAddress)
            {
                HelpKey = "row.public.help",
                PlaceholderKey = "row.public.placeholder",
                MaxLength = 200,
                Apply = ApplyMode.ApplyRevert,
            },
            new NumberRow("row.port.header", ui.CheckPort, 0, 65535, 1)
            {
                DescriptionKey = "row.port.description",
                HelpKey = "row.port.help",
                Line = ui.LocalLine,
                Apply = ApplyMode.ApplyRevert,
                Validate = v => v == Math.Floor(v) ? null : LocalisedText.Of("validate.whole"),
            },
        };
        if (ui.Hotkey is { } hotkey) children.Add(new ShortcutRow("row.hotkey.header", hotkey.Gesture) { DescriptionKey = "row.hotkey.description" });
        // Facts while the tunnel is on; empty (and so left out by the host) while it is off.
        children.Add(new ValueRow("row.endpoint.header", ui.Endpoint) { HelpKey = "row.endpoint.help" });
        children.Add(new ValueRow("row.transfer.header", ui.Transfer) { DescriptionKey = "row.transfer.description" });
        return new TunnelRows(on, children);
    }

    private static string Id(int n) => n.ToString(CultureInfo.InvariantCulture);
}

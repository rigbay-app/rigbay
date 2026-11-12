using Rigbay.Abstractions;

namespace Rigbay.Tunnel;

/// <summary>The values the page, the tray item and the hotkey of one tunnel share. Created once per tunnel name and kept while the module runs.</summary>
internal sealed class TunnelUi
{
    public TunnelUi(string name)
    {
        Name = name;
        Slug = Wg.TunnelNames.Slug(name);
    }

    public string Name { get; }
    public string Slug { get; }

    public string RxMetric => "tunnel." + Slug + ".rx_rate";
    public string TxMetric => "tunnel." + Slug + ".tx_rate";
    public string HandshakeMetric => "tunnel." + Slug + ".handshake_age";

    // What the user sees and changes.
    public ObservableValue<bool> On { get; } = new(false);
    /// <summary>The tunnel's live line, in its closed row: "Connected · handshake 15 s ago · ↓ 17.6 KB/s ↑ 2.4 KB/s", and the problem when there is one.</summary>
    public ObservableValue<LocalisedText> Line { get; } = new(default);
    /// <summary>The server WireGuard reports; empty while the tunnel is off (the host then leaves the row out).</summary>
    public ObservableValue<string> Endpoint { get; } = new("");
    /// <summary>Bytes received and sent since the tunnel was turned on; empty while it is off.</summary>
    public ObservableValue<string> Transfer { get; } = new("");
    /// <summary>The result of the local-service check, under the port; empty when no port is set.</summary>
    public ObservableValue<LocalisedText> LocalLine { get; } = new(default);
    /// <summary>Under "Start with Windows": why it cannot be used while the tunnel is off; empty while it is on.</summary>
    public ObservableValue<LocalisedText> StartLine { get; } = new(default);
    /// <summary>The raw text of the last problem, or null when there is none. Shown in <see cref="Line"/>.</summary>
    public string? ProblemText { get; set; }
    public ObservableValue<bool> StartWithWindows { get; } = new(false);
    public ObservableValue<bool> StartEnabled { get; } = new(false);
    /// <summary>The automatic turn-off as a choice id: the hours as a whole number, "0" for never.</summary>
    public ObservableValue<string> AutoOff { get; } = new("0");
    public ObservableValue<double> CheckPort { get; } = new(0);
    public ObservableValue<string> PublicAddress { get; } = new("");

    /// <summary>Whether the tunnel was on at the last look (the switch may show a change that is still running).</summary>
    public bool KnownOn { get; set; }

    /// <summary>Bytes per second at the last read, for the line; null while not known.</summary>
    public (double Rx, double Tx)? Rates { get; set; }

    /// <summary>
    /// The rows of this tunnel, made once so a rebuilt page keeps the host's state of each (an "Applied" status, a Revert link); dropped when the
    /// language changes, because the effect texts carry the name in the language of the moment.
    /// </summary>
    public TunnelRows? Rows { get; set; }

    public TrayItem? Tray { get; set; }
    public IHotkeyBinding? Hotkey { get; set; }

    // Bookkeeping of the poll (touched only while the module's poll gate is held).
    public bool Present { get; set; } = true;
    public bool WasPresent { get; set; } = true;
    public bool MetricsDescribed { get; set; }
    public bool MetricsPublished { get; set; }
    public (DateTimeOffset At, long Rx, long Tx)? LastTransfer { get; set; }
    public WgSummary? Last { get; set; }
}

/// <summary>The row instances of one tunnel (see <see cref="TunnelUi.Rows"/>).</summary>
internal sealed record TunnelRows(ToggleRow On, IReadOnlyList<Row> Children);

/// <summary>What the last status read said, for the verbs and the texts.</summary>
internal sealed record WgSummary(DateTimeOffset? Handshake, long? Rx, long? Tx, string? Endpoint);

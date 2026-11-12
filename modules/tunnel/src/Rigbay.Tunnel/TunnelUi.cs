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
    public ObservableValue<string> Status { get; } = new("");
    public ObservableValue<string> Endpoint { get; } = new("");
    public ObservableValue<string> Transfer { get; } = new("");
    public ObservableValue<string> Local { get; } = new("");
    public ObservableValue<string> Problem { get; } = new("");
    public ObservableValue<bool> StartWithWindows { get; } = new(false);
    public ObservableValue<bool> StartEnabled { get; } = new(false);
    public ObservableValue<double> AutoOffHours { get; } = new(0);
    public ObservableValue<double> CheckPort { get; } = new(0);
    public ObservableValue<string> PublicAddress { get; } = new("");

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

/// <summary>What the last status read said, for the verbs and the texts.</summary>
internal sealed record WgSummary(DateTimeOffset? Handshake, long? Rx, long? Tx, string? Endpoint);

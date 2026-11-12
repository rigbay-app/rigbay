namespace Rigbay.Tunnel;

/// <summary>
/// The module's settings file (<c>%LOCALAPPDATA%\Rigbay\modules\tunnel\settings.json</c>, plain text). It holds the user's own choices per tunnel and
/// nothing from WireGuard: no key, no address of the tunnel, no configuration. The tunnel names are the keys (lowercase), the public address is
/// what the user typed.
/// </summary>
public sealed class TunnelSettings
{
    /// <summary>Options per tunnel, keyed by the lowercase tunnel name.</summary>
    public Dictionary<string, TunnelOptions> Tunnels { get; set; } = new();
}

/// <summary>What the user chose for one tunnel.</summary>
public sealed class TunnelOptions
{
    /// <summary>Turn the tunnel off by itself this many hours after it was turned on; 0 (the default) never. Consent for that is given when the option is set.</summary>
    public int AutoOffHours { get; set; }

    /// <summary>A TCP port to look for on the tunnel's own address (for example a remote-control app); 0 means no check.</summary>
    public int CheckPort { get; set; }

    /// <summary>Free text the user typed, shown on the page as a link when it is an http or https address. Never committed anywhere.</summary>
    public string PublicAddress { get; set; } = "";

    /// <summary>When the tunnel was turned on through Rigbay (ISO 8601, UTC), for the automatic turn-off; empty when not counting.</summary>
    public string OnSinceUtc { get; set; } = "";
}

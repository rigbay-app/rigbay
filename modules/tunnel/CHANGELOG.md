# Changelog

## 0.1.0 (pre-release)

First version. Turns WireGuard for Windows tunnels on and off (page, tray, hotkey, `rigbay tunnel list|status|on|off`), asks before every change, shows status and publishes
`tunnel.<name>.rx_rate`, `tunnel.<name>.tx_rate` and `tunnel.<name>.handshake_age`. Options per tunnel: automatic turn-off after N hours, start with Windows, local service check, public address.
Depends on the official WireGuard for Windows (signature checked); never reads a tunnel configuration.

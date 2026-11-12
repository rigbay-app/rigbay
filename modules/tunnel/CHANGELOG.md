# Changelog

## 0.1.1 (Unreleased)

The page from the November 2026 UI review (`docs/ui-review/REVIEW-2026-11.md` in the Rigbay repository, findings TN-1, TN-2, LC-1, LC-2). What the
module runs and reads does not change. The manifest now asks for host API `>=0.1.3 <0.2` (`ExpanderRow.HeaderRow`, `Row.Line`, `Row.HelpKey`).

- **Each tunnel is one row with its switch and its state in it**, readable while closed: "Off", "On · no handshake yet", "Connected · handshake 15 s
  ago · ↓ 17.6 KB/s ↑ 2.4 KB/s", "Not connected · last handshake 4 min ago", and the last problem on a line of its own. The switch is named after the
  tunnel, so the approval reads "example-tunnel: Off → On".
- **Inside only settings**: start with Windows (it says "Only while the tunnel is on" while it cannot be used), turn off after, public address, local
  service port (its result is the row's line), shortcut; then Server and Transferred, which are empty, and so left out, while the tunnel is off. The
  Status, Problem, Last handshake, Receiving, Sending and Local service rows are gone.
- **No reading tiles**: a tile named "Receiving" could not say which tunnel it was. The rates are in each tunnel's line; the metrics stay on the bus
  for the dashboard and the recorder.
- **"Turn off after" is a list** (Never, 1, 2, 4, 8, 24 hours) instead of a number box where 0 meant never (TN-2). A number from 0.1.0 that is not
  in the list stays offered as it is.
- Vietnamese: the module is "Đường hầm từ xa"; "đường hầm", "dịch vụ", "bắt tay" replace the English words. "Hotkey" is "Shortcut".

## 0.1.0 (pre-release)

First version. Turns WireGuard for Windows tunnels on and off (page, tray, hotkey, `rigbay tunnel list|status|on|off`), asks before every change, shows status and publishes
`tunnel.<name>.rx_rate`, `tunnel.<name>.tx_rate` and `tunnel.<name>.handshake_age`. Options per tunnel: automatic turn-off after N hours, start with Windows, local service check, public address.
Depends on the official WireGuard for Windows (signature checked); never reads a tunnel configuration.

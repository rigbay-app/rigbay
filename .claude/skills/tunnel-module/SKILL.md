---
name: tunnel-module
description: Working knowledge of the Rigbay Remote tunnel module (modules/tunnel, id tunnel, .NET 8, in process; turns WireGuard for Windows tunnels on and off with wireguard.exe /installtunnelservice and /uninstalltunnelservice, status through wg.exe show <name> latest-handshakes|transfer|endpoints, metrics tunnel.<slug>.rx_rate|tx_rate|handshake_age, tray item and hotkey per tunnel, rigbay tunnel list|status|on|off). Use it before reading or changing anything under modules/tunnel, when touching the approval flow, the wg.exe calls, the tunnel store listing, a language key or a test, and before any live check with a real tunnel. Saves re-deriving the layers, the safety rules and the traps below.
---

# Remote tunnel module: codebase guide

Module id `tunnel`, group `network`, `modules/tunnel/src/Rigbay.Tunnel` (net8.0), tests in `modules/tunnel/tests/Rigbay.Tunnel.Tests` (118 at the last run, version 0.1.1 unreleased, host API `>=0.1.3`).
User and design document: `docs/tunnel/MODULE.md` (it says which WireGuard facts were checked and where). This repository is PUBLIC.

## Hard constraints (check before you write code)

- **Nothing about the owner's network goes into the repository**: no tunnel name, domain, address, key, host name, in code, tests, docs, screenshots, commit messages
  or this skill. Use `example-tunnel`, `198.51.100.1`, `10.9.0.2`, `remote.example.test`. Before a commit: `git grep -nIiE "<the real name>|<the real domain>"` (ask the coordinator for the words to search; do not write them into a file).
- **Never read a configuration.** Files in `Data\Configurations` hold private keys. The module lists file names (`TunnelStore`) and passes the path to `wireguard.exe`; it opens
  nothing. `HygieneTests` fails when source mentions `File.ReadAll`, `File.Open`, `FileStream`, `StreamReader`, DPAPI and so on.
- **`wg.exe` only with a field.** `WgField` has three values (`LatestHandshakes`, `Transfer`, `Endpoints`); `WireGuardTools.ShowArguments` is the only place that writes `"show"`.
  A bare `wg show`, `dump`, `showconf` print the private key. Do not add a fourth value or a free-text argument. The output of `/install...` and `/uninstall...` is discarded unread.
  Peer public keys and endpoints in `wg show` output are never logged (a test greps the log and the settings for them).
- **Never start or stop a service yourself.** Only `wireguard.exe /installtunnelservice` and `/uninstalltunnelservice` (what the WireGuard app does), plus `ChangeServiceConfig` for the start type.
  The callers of the two commands are in `TunnelManager.ChangeAsync` only (a test checks).
- **Every change asks.** Page switch: the row carries a `HardwareEffect` (`EffectKind.SystemSetting`), the host asks, the module then passes `approved: true`. Tray, hotkey, verb:
  `RequestChangeAsync` asks through `host.Approval.ConfirmAsync` with exactly "Turn on tunnel NAME" / "Turn off tunnel NAME". Auto-off acts without asking because the user chose it
  (the row that sets it has a `HardwareEffect`). `IHardwareApproval` cannot remember an answer, so there is no "do not ask again". When the host cannot ask the answer is no.
- **Validate the name before it touches a process.** `TunnelNames.IsValid`: 1 to 32 of `A-Za-z0-9_=+.-`. The store listing, `ChangeAsync`, `UninstallTunnelServiceAsync`, `ShowAsync` and `SetStart` all check.
- **The signature gate.** `WireGuardTools.Verify` (WinVerifyTrust + subject `WireGuard LLC`) runs before every program start, cached by size and time of the file. Do not add a way to run another path.
- **Rigbay rules** (`docs/ABSTRACTIONS.md` section 12): constructor does nothing (the real dependencies are made in `Probe`), `Probe` is read only, `Initialize` writes nothing, catch everything on the
  timer and on `Task.Run` threads, all text is a language key, `Shutdown` never changes a tunnel.
- **Manifest limits** (host refuses the module otherwise): `requires` entries 1 to 200 characters, description up to 200, name up to 60, **JSON backslashes doubled**
  (`"%ProgramFiles%\\WireGuard"`: a Bash heredoc collapses `\\`, write the file with the Edit or Write tool; a test parses it).

## Layout

| File | What |
| --- | --- |
| `Wg/Contracts.cs` | the interfaces (`IWireGuardTools`, `IServiceControl`, `ITunnelStore`, `INetworkProbe`, `IClock`) and plain records |
| `Wg/WireGuardTools.cs` | the real programs: probe, signature cache, exact argument lists, `ProcessRunner` (no shell, no window, output only when asked) |
| `Wg/Native.cs` | `ServiceControl` (advapi32), `AuthenticodeCheck` (wintrust), `TunnelStore`, `NetworkProbe` |
| `Wg/TunnelNames.cs`, `Wg/WgParsers.cs` | name rules and slug; parsers of the three `wg show` fields |
| `TunnelManager.cs` | the logic without host types: list, `ChangeAsync` (one slot, install or uninstall, read back), handshake wait, port check |
| `TunnelModule*.cs` | lifecycle and polling (`.cs`), approval, toasts, edits from the page (`.Changes.cs`), page (`.Page.cs`), verbs (`.Commands.cs`) |
| `Dependencies.cs` | `Real()` is the only code that names `%ProgramFiles%\WireGuard` |

## Releasing

Repository `rigbay-app/module-tunnel`, release `v0.1.0`. Make the registry entry with a script that reads the `module.json` of the DOWNLOADED zip (a hand-typed `%ProgramFiles%\WireGuard` through Bash lost its backslash and the app refused the install: `DeclaresWider`). Run `toolsverify-module-zip` against the real registry after the merge.

## Gotchas

- **The page layout (0.1.1, UI review TN-1, TN-2).** One `ExpanderRow` per tunnel, closed, with the on switch as `HeaderRow` (header = the tunnel's
  name as a literal, so the approval reads "NAME: Off → On") and `TunnelUi.Line` (`LineText`: state, handshake age, rates from `TunnelUi.Rates`,
  the problem on its own line). Inside only settings, then Server and Transferred, empty while off (the host leaves them out). No `ReadingRow` on the
  page (the metrics stay on the bus). The rows are made once per tunnel (`TunnelUi.Rows`) and dropped on a language change (their effect texts are
  literals). "Turn off after" is a `ChoiceRow` (`AutoOffChoices`; an older value outside the list is offered as "N hours").

- **ON = the service exists and runs.** `/installtunnelservice` fails when a service of that name exists, so a stopped leftover is removed first. A failed start is cleaned up
  (uninstall) so no half-installed service stays; the reason is the service's own Win32 exit code (`err.service-failed`).
- **Turning off removes the service**, so "Start with Windows" (the service start type) disappears with it. That is how WireGuard works; the row says so and is enabled only while the tunnel is on.
- **The switch and the host.** The host sets the toggle's value after approval, on the UI thread; the module starts the change on a pool thread and puts the toggle right at the end
  (`Resync`). The module sets values itself only through `SetOwn` (a `[ThreadStatic]` flag, because `Changed` runs synchronously on the setting thread), so its own sets are not taken for the user's.
  While a change runs `ApplyList` does not overwrite the toggle.
- **Polling.** Services every 15 s; `wg.exe` (three calls per tunnel) only while `PageVisible` or somebody reads a `tunnel.*` metric (2 s). Tests call `PollOnceAsync()` and `WhenIdleAsync()`; `Dependencies.AutoPoll = false` means no timer.
- **Handshake.** Connected = newest handshake not older than 3 minutes. After a turn-on the module waits up to 30 s, once a second (fake clock in tests, no sleeping). A verb waits in the verb; the tray and hotkey wait in the background and then show one toast.
- **Metric ids and hotkey/tray ids** only accept `a-z 0-9 -` (`_` too in metrics), so `TunnelNames.Slug` lowercases and replaces; when that lost more than case, four hex digits of an FNV hash are appended.
- **A tunnel found on that Rigbay did not start** is shown as on and is never touched; auto-off counts from the first poll that sees it, only when the user set hours.
- **Notifications.** `ToastMessage.Origin` is N1's, not in this host yet. When it lands, pass `ToastOrigin.Page` for page results (see `docs/tunnel/MODULE.md`).
- `wg show` and listing `Data\Configurations` need administrator rights; from a normal shell they answer "Permission denied" / "Access is denied". Rigbay is always elevated.

## Recipes

**Build and test:** `dotnet build Rigbay.sln --no-incremental`, `dotnet test modules/tunnel/tests/Rigbay.Tunnel.Tests -c Release` (about 1 s, no sleeps, no real process or service).
**Language check:** `dotnet run --project src/Rigbay.LangCheck -c Release -- modules/tunnel/src/Rigbay.Tunnel/lang --module tunnel --source modules/tunnel/src/Rigbay.Tunnel --strict`.
**A new scenario:** `World` (in `Fakes.cs`) is the whole fake machine: `Services` (table), `Tools` (records `Calls`, `Installed`, `Uninstalled`, `ShowOutput`, `OnInstall`), `Store`, `Network`, `Clock`; `Rig` wraps a module on it.
`FakeClock.Delay` moves time and returns at once; `OnDelay` lets a test change the world at the n-th wait.
**A live check with the real app:** only after the owner's tunnel is ready and the coordinator says so; elevated `Rigbay.App` with a scratch `--data`/`--modules` folder, a script that answers the approval,
and `sc query "WireGuardTunnel$<name>"` plus `wg show <name> latest-handshakes` from an elevated shell. Leave the tunnel in the state the owner had. Never put the name, an address or a key in a log, note or file you keep.

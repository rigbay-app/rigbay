# Remote tunnel module

Rigbay module `tunnel` (group `network`, display name "Remote tunnel"): turns the WireGuard tunnels that are installed on this PC on and off from Rigbay,
from its page, the tray, a hotkey or `rigbay tunnel on|off <name>`, and shows how each one is doing. It is meant for the case "my PC is reachable from
outside through my own WireGuard server, and I want a switch for that, like the switch of a VPN app".

It is generic: any WireGuard tunnel that WireGuard for Windows knows. Nothing about anybody's network is in the module, its tests, its documents or its
screenshots (example values only: `example-tunnel`, `198.51.100.1`, `10.9.0.2`, `remote.example.test`).

Code: `modules/tunnel/src/Rigbay.Tunnel/` (net8.0). Tests: `modules/tunnel/tests/Rigbay.Tunnel.Tests/`. Notes for changing it: `.claude/skills/tunnel-module/`.

## What it depends on, and what it does not

It uses the **official WireGuard for Windows** that the user installed (<https://www.wireguard.com/install/>). It does not bundle, copy or reimplement
WireGuard, wintun or `tunnel.dll`.

`Probe` (read only) says "supported" when `%ProgramFiles%\WireGuard\wireguard.exe` and `wg.exe` both exist **and both carry a valid Authenticode
signature whose subject is `WireGuard LLC`** (checked with `WinVerifyTrust`, then the certificate's name). Otherwise the module is unsupported with a
sentence and the download address (`probe.no-wireguard`, `probe.not-signed`). The signature is checked again before a program is run whenever its size
or time changed, so a swapped program is not run as administrator. `module.json` declares `probePaths: ["%ProgramFiles%\WireGuard"]`.

## Where tunnels come from

A tunnel is a configuration in WireGuard's own store, `%ProgramFiles%\WireGuard\Data\Configurations\`. The module lists **names only**, from the file names:
`<name>.conf.dpapi` (what the WireGuard manager keeps) and `<name>.conf` (a file the manager has not converted yet), each name once (when both exist the
encrypted one is used). Names must be what WireGuard accepts, 1 to 32 characters of letters, digits and `_ = + . -`; anything else is ignored, because
the name becomes the service name and a command-line argument.

**The module never opens, reads, decrypts, copies or logs a configuration.** They hold private keys. A test locks the files exclusively and lists them
anyway; another scans the source for any call that opens a file.

A tunnel is **on** when its service `WireGuardTunnel$<name>` exists and is running.

## What turns a tunnel on and off

Exactly what the official app does on Activate and Deactivate, so both stay in step (a tunnel turned on in the WireGuard app shows as on here, and the
other way round):

| Action | Command | Then |
| --- | --- | --- |
| On | `wireguard.exe /installtunnelservice "<the existing file in the store>"` | waits up to 10 s until the service runs; a failure reads the service's last error and removes the half-installed service |
| Off | `wireguard.exe /uninstalltunnelservice <name>` | waits up to 10 s until the service is gone |

The programs are started without a shell and without a window, with the arguments as a list. A service of that name that exists but does not run (a
leftover) is removed before the install. One change runs at a time; a second request is refused, not queued, and nobody is asked about it.

### Every change asks first

Each change is a system change. The question goes through the host's approval service (`EffectKind.SystemSetting`), and a revert is asked about again.

| From | Who asks | Text |
| --- | --- | --- |
| The page switch | the host (the row carries a `HardwareEffect`) | "Turns the WireGuard tunnel NAME on or off by starting or removing its Windows service..." + On/Off |
| The tray item, a hotkey, `rigbay tunnel on|off` | the module, through `host.Approval.ConfirmAsync` | exactly **"Turn on tunnel NAME"** / **"Turn off tunnel NAME"**, with one sentence of detail |
| Automatic turn-off after N hours | nobody, at the time: the user chose it and the host asked when they set it | a notification says it happened |

When the host cannot ask (command line without a window) the answer is no and nothing changes. The approval API has no "remember my answer", so it
always asks; there is no "do not ask again".

## Status, metrics, options

Status is read only while the page is visible or somebody reads one of the module's metrics (otherwise the module only looks at the services, every 15 s):

| `wg.exe` call | Gives |
| --- | --- |
| `wg show <name> latest-handshakes` | the newest handshake. **Connected = younger than 3 minutes.** |
| `wg show <name> transfer` | bytes received and sent |
| `wg show <name> endpoints` | the server address, as WireGuard reports it |

`wg show` without a field, `dump` and `showconf` print the private key or the whole configuration, so **the code cannot call them**: the argument list is built
from a three-value enum (`WgField`), the output of install and uninstall is discarded unread, and tests scan the source for the words that would print a key.
The peers' public keys in the output are dropped at once and never logged.

Metrics (with descriptors, so charts plot them without more work): `tunnel.<slug>.rx_rate`, `tunnel.<slug>.tx_rate` (bytes per second) and
`tunnel.<slug>.handshake_age` (seconds). `<slug>` is the name in lowercase with anything outside `a-z 0-9 -` replaced by `-` (and four hex digits of a hash added when that
changed more than the case). A metric that cannot be read is withdrawn, never left at its last value.

Options per tunnel (settings file `%LOCALAPPDATA%\Rigbay\modules\tunnel\settings.json`, plain text, never committed; it holds the user's own choices and nothing from WireGuard):

- **Turn off automatically after N hours** (0 to 168, default 0 = never). Counts from the moment it was turned on through Rigbay (or from the moment the option is set,
  or from the first poll that sees the tunnel on). The host asks when the option is set.
- **Start with Windows**: the start type of the tunnel's service, Automatic or Manual (asked about). It can only be changed while the tunnel is on. **Turning a tunnel off removes
  its service, as in the WireGuard app, and with it this setting**; the page says so. The default is "leave it as WireGuard set it" (the module never touches it unasked).
- **Check a local service**: a TCP port. The module looks up the adapter that WireGuard named after the tunnel, takes its addresses and shows whether something listens on that port
  there (or on every address). Nothing is sent on the network.
- **Public address**: free text the user types; shown as a link at the bottom of the page when it starts with `http` or `https`.
- **Hotkey** per tunnel (unbound by default; rebinding is the host's).

## Notifications

N1's rule (`docs/APP.md`, "Notifications"): a toggle from the tray, a hotkey, a verb or the automatic turn-off shows a notification through the host's `IToastService` (the module has no
notification code of its own; the host shows balloons from its tray icon when it runs elevated). Every failure is shown whatever the origin. A turn-on is followed by **one**
notification once it is known whether the server answers: connected, or "the server is not answering, check the internet or the server" after 30 s without a handshake.
A turn-off or turn-on from the page is quiet unless it fails (the switch shows it).

Not done in 0.1.0: `ToastMessage.Origin` (the in-window message for page clicks) is part of N1's branch, not of the host this module is built against. When it reaches the pinned host,
pass `Origin = ToastOrigin.Page` for the results of page clicks.

## Problems, in plain words

| Situation | What the user reads |
| --- | --- |
| WireGuard missing | "WireGuard for Windows is not installed. Install it from https://www.wireguard.com/install/ and restart Rigbay." |
| Not signed by WireGuard LLC | says so and asks to reinstall from the official address |
| Configuration gone | "The configuration of tunnel NAME is not in WireGuard's store any more. Add it again in the WireGuard app." |
| Service did not start | "The service of tunnel NAME did not start: <Windows' own text for the service's last error>. Open the WireGuard app and read its Log tab." |
| Install command failed | exit code + the same log hint (the command's own output is never kept, it can quote a configuration) |
| No handshake after 30 s | "Tunnel NAME is on, but the server is not answering. Check the internet connection or the server." The tunnel stays on. |
| The store cannot be listed | "Rigbay must run as administrator, and WireGuard must be installed in its default folder." |

English and Vietnamese (`lang/en.lang`, `lang/vi.lang`), LangCheck strict.

## Command line

```
rigbay tunnel list
rigbay tunnel status [name]
rigbay tunnel on <name>      # asks, then waits up to 30 s for the server to answer
rigbay tunnel off <name>     # asks
```

Exit codes follow the host: 0 done, 1 failed or declined, 2 bad usage.

## Facts checked against WireGuard itself

Checked on 2026-11-12 against the documents of the `wireguard-windows` repository and the installed program on this machine:

- `docs/enterprise.md`: `wireguard /installtunnelservice <path>` takes a file ending `.conf` or `.conf.dpapi` and creates the service `WireGuardTunnel$<name>`;
  `wireguard /uninstalltunnelservice <name>` takes the **name**; the manager watches `Data\Configurations\` for new `.conf` files and replaces each by an encrypted
  `.conf.dpapi` that only Local System can read; `wg` on a tunnel needs Local System or Administrator. (The page does not give the service's start type or dependencies.)
- `docs/adminregistry.md`: only `LimitedOperatorUI` and `DangerousScriptExecution` exist under `HKLM\Software\WireGuard`. The module reads and writes neither.
- Installed program: `wireguard.exe` and `wg.exe` both have a valid Authenticode signature, subject `CN=WireGuard LLC`; `wg.exe --version` reports wireguard-tools v1.0.20260223 and `wg help` lists
  `show` among its subcommands.
- An installed tunnel service on this machine: start type Automatic (not delayed), account LocalSystem, image `wireguard.exe /tunnelservice <config path>`.
- `Data\` cannot be listed from a normal (non-elevated) shell, which is why the module needs the administrator rights Rigbay always has; `wg show <name> <field>` from a normal shell answers "Permission denied".

## Verification record

See the end of this file (filled in after the live check).

## Not verified

(Listed in the verification record.)

## Looking at the page without WireGuard (demo build)

`modules/tunnel/tools/demo/` is the real module on a made-up machine (two tunnels named `example-tunnel` and `example-backup`, a made-up server in the documentation range, moving
traffic). It changes nothing. `powershell -ExecutionPolicy Bypass -File modules\tunnel\tools\demo\build-demo.ps1 -OutDir tools\out\modules-tunnel-demo` lays it out as a module folder; run the app with
`tools\run-elevated.ps1 -Wait -Data <absolute scratch dir> -Modules <absolute path of that OutDir> --shot <absolute png> --allow-multi --script "enable:tunnel;wait:2500;page:tunnel;expand-all;wait:1200"`
(the elevated app resolves relative paths against its own folder, so give absolute ones). The pictures in `docs/tunnel/evidence/` come from it; each was looked at.

- `page-dark.png`: the page in the real app, one tunnel on and connected (handshake age, server, transferred, live rates), options below.
- `page-light.png`: the same in the light theme, the tunnel that is off.
- `page-vi-dark.png`: the Vietnamese texts.

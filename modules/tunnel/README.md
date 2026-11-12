# Remote tunnel

Rigbay module `tunnel` (group `network`): turns the WireGuard tunnels installed on this PC on and off from Rigbay, from its page, the tray, a hotkey or the
command line, and shows how each tunnel is doing (running, newest handshake, server, bytes received and sent). Think of the switch of a VPN app, for your own WireGuard server.

- **Needs the official WireGuard for Windows** (<https://www.wireguard.com/install/>). Nothing of WireGuard is bundled. The module checks that `wireguard.exe` and `wg.exe` are signed by WireGuard LLC before it runs them.
- **It does what the WireGuard app does**: On = `wireguard.exe /installtunnelservice` with the file in WireGuard's own store, Off = `/uninstalltunnelservice <name>`. A tunnel turned on in either place shows as on in both.
- **It never reads a configuration** (they hold private keys): it lists the names of the files in `Data\Configurations` and nothing else. Status comes from three `wg show <name> <field>` calls; `wg show` without a field is impossible in the code.
- **Every change asks first**, from the page, the tray, a hotkey or `rigbay tunnel on|off`: "Turn on tunnel NAME" / "Turn off tunnel NAME". When Rigbay cannot ask, nothing changes.
- **The page lists each tunnel as one row** with its switch and its state in it ("Connected · handshake 15 s ago · ↓ 17.6 KB/s ↑ 2.4 KB/s"); open the row for its settings.
- Options per tunnel: turn off automatically after 1, 2, 4, 8 or 24 hours (default never), start with Windows (the service's start type), check a local service on the tunnel address (a port), a public address you type, a shortcut.
- Metrics on the bus: `tunnel.<name>.rx_rate`, `tunnel.<name>.tx_rate`, `tunnel.<name>.handshake_age`.
- English and Vietnamese. Version **0.1.1** (unreleased; 0.1.0 is released) needs Rigbay host API 0.1.3, see [CHANGELOG.md](CHANGELOG.md).

Full description, the WireGuard facts that were checked and the verification record: `docs/tunnel/MODULE.md` in the [Rigbay repository](https://github.com/rigbay-app/rigbay). Notes for changing it: `.claude/skills/tunnel-module/` there.

## Using it

```
rigbay tunnel list
rigbay tunnel status [name]
rigbay tunnel on <name>       # asks, then waits up to 30 s for the server to answer
rigbay tunnel off <name>      # asks
```

## Building

```
dotnet build Rigbay.Tunnel.slnx -c Release     # 0 warnings expected
dotnet test  Rigbay.Tunnel.slnx -c Release     # the tests use fakes: no process, no service, no network, no sleeping
dotnet run --project ../../src/Rigbay.LangCheck -- src/Rigbay.Tunnel/lang --module tunnel --source src/Rigbay.Tunnel --strict
```

Copy `src/Rigbay.Tunnel/bin/Release/net8.0/` (module.json, the icon files, `lang/`, `Rigbay.Tunnel.dll`) to `<modules root>\tunnel\`.
The module declares (and the manifest says before any code runs): administrator rights, the service `WireGuardTunnel$<name>` (installed, removed, start type), and the folder `%ProgramFiles%\WireGuard`.

## What was verified, and what was not

See the verification record in `docs/tunnel/MODULE.md`. In short: the logic, the page, the commands and the safety rules are covered by tests with fakes (no real tunnel is touched by a test);
the real WireGuard was used for one live check on one machine (see the record). **Not verified**: other WireGuard versions, a tunnel with several peers or IPv6 only, other Windows versions than 11, a machine where the WireGuard store is not in its default place.

## License

MIT.

# The Steam access module (task S1)

`modules/steambypass/` is the Rigbay port of the standalone app `minhdevtree/steam-bypass` (read only for us; its project skill and
`references/blocking-evidence.md` hold the measurements the design rests on). Module id `steam-bypass`, name "Steam access", group `network`,
`net8.0`, no package besides `Rigbay.Abstractions`. It keeps to its own folder (own solution `Rigbay.SteamBypass.sln`, own
`Directory.Build.props`) so it can move to a `module-steam-bypass` repository like the other two.

It restores access to Steam on a network that blocks it by DNS poisoning plus SNI and HTTP `Host` header filtering: a local proxy that
resolves Steam names over DNS-over-HTTPS, cuts the first two bytes off a TLS hello, respells the HTTP `Host:` header as `HoSt:`, and a PAC
file that sends only Steam addresses to it. No driver, no administrator rights of its own (the host runs elevated, the module only needs
the current user's registry and loopback sockets).

## What it does

| Part | Behaviour |
| --- | --- |
| Probe | Always supported (`ProbeResult.Yes()`). It needs only HKCU and loopback sockets, so there is nothing to look for. |
| Switch | `Steam bypass` (page row, group Bypass) is a `ToggleRow` with a `HardwareEffect`. The host shows the exact effect text (the `AutoConfigURL` value, the address, the two ports, "turning it off puts back the value that was there before"), the exact change ("Steam bypass: Off → On"), asks, then sets the value; the module does the work on a pool thread. The host's Revert is asked about too (no `SafeRevert`): reverting an Off turns the bypass on again, which is a change of the Windows setting. |
| Status | Listening (127.0.0.1:18081 and :80, or why not), open connections and errors (`ReadingRow`s on the bus metrics, "not available" while off), Steam (five states), last problem, the domain list in use. |
| Connection check | The four checks of the old app (Store, Community, Static content through the proxy; Game downloads through `127.0.0.1:80` with `Host: cache8-hkg1.steamcontent.com`), each row shows `Reachable · HTTP 200 · 340 ms` or the reason (refused, timed out, DNS failed, blocked (reset)). The `Run test` button and `rigbay steam-bypass test` run them in parallel, 12 s each, no redirects. With the bypass off the requests go straight out, so the result says whether the network blocks Steam. |
| Settings | `Turn on when Rigbay starts` (`autoOn`, default off). It is a `HardwareEffect` row: turning it on is the approval for every later start ("without asking again"), with the same exact text. |
| Dashboard bay | State line `Bypass off`, `Bypass on · N open`, `On, no game downloads · N open` or `Could not turn on`, plus the two readings below. |
| Metrics | `steam.proxy.active` (open connections) and `steam.proxy.errors` (failed connections since the module started), both `MetricUnit.Count`, with descriptors (`Steam proxy connections`, `Steam proxy errors`). Published while the bypass is on, withdrawn while it is off. Refreshed every 1 s while the page is visible or a subscriber exists, every 10 s otherwise. |
| Verbs | `rigbay steam-bypass status` (five lines, exit 0), `on` and `off` (ask the approval service with the exact effect and "Steam bypass: Off → On"; no answer or no window means exit 1 and nothing changed; already in that state means exit 0 and no question), `test` (the four checks, exit 0 only when all pass), extra arguments exit 2. |
| Cleanup | Shutdown (module disabled, host exit) restores the Windows value first and then stops the proxy; a process-exit hook does the same when the host ends without `Shutdown`; a crash is cleaned up at the next start (`RecoverStale`). `rigbay steam-bypass off` works with the page closed (the module runs without it). Disabling the module is the unconditional way off: it needs no approval. |
| Languages | `lang/en.lang`, `lang/vi.lang`, 100 keys each under `module.steam-bypass.`; `Rigbay.LangCheck --strict` is clean. |

## Layers

```
SteamBypassModule (IModule; page, texts, verbs in partial files)       the only class that knows about the host
  |-- BypassController        on/off rules: listen first, then write Windows; restore first, then stop; crash cleanup; process exit
  |     |-- ProxyEngine       the two listeners and every connection handler (CONNECT, absolute-URI HTTP, PAC, the :80 forwarder)
  |     |     |-- ILoopbackListenerFactory   the only way to open a socket; no address parameter, always 127.0.0.1
  |     |     |-- IUpstreamDialer            UpstreamDialer: DoH for Steam names, never to loopback/any/multicast
  |     |     |     '-- IHostResolver        DohResolver: HttpClient, TLS 1.2+, no system proxy, 4 endpoints, cache
  |     |     '-- DomainSet                  the list, matching, the PAC text
  |     '-- SystemProxyManager  apply/restore with the saved previous value
  |           |-- ISystemProxyStore          RegistryProxyStore (key path is a constructor argument)
  |           '-- IProxyStateStore           FileProxyStateStore (proxy-state.json in the module's data folder)
  |-- IConnectionProbe        HttpConnectionProbe: the four checks
  '-- ISteamProbe             SteamProbe: steam.exe processes + the IPv4 TCP table
```

`Dependencies` is the production wiring; every test replaces the parts it needs. No test constructs `RegistryProxyStore` on the real key (a
test scans the test sources for it).

## Rules kept from the standalone app

- **Only Steam hostnames leave through DoH.** `UpstreamDialer` asks DoH only for names in the domain list; everything else uses the system
  resolver. The one exception is the same as before: names that arrive on `127.0.0.1:80` (Steam's downloader) are resolved over DoH whatever
  they are, because the ISP's resolver already answered 127.0.0.1 for them.
- **Loopback only.** The listener factory has no address parameter; the real one is `new TcpListener(IPAddress.Loopback, port)` with exclusive
  use. A test asserts the bound endpoints are `127.0.0.1`, and another that no other source file creates a listener. The proxy also checks that
  the peer of each connection is a loopback address.
- **Every path that turns the bypass on has a restore.** Listed under Cleanup above; each path has a test.
- **The saved previous value** is written (and flushed through a temp file and a move) before the registry value, and removed after the restore.
- **Windows is pointed at the proxy only after the proxy listens**, and is pointed away before the proxy stops. A failed registry write stops the
  proxy again; a failed restore keeps the saved value and is retried by the next off, the next start and the process exit.
- **TLS 1.2 or newer with `HttpClient`** (`SocketsHttpHandler`, `EnabledSslProtocols = Tls12 | Tls13`), no `ServicePointManager`, no
  `HttpWebRequest`, no certificate callback. The DoH client has `UseProxy = false`: a DoH request must never go through our own PAC.

## What is different from the standalone app (on purpose)

1. **The module only undoes what it set.** The old app restored whenever the value "started with our address". Here `Restore` also needs the
   module's own saved state. This was found on the author's machine, where the standalone app was running at the time: loading the first
   version of this module there would have restored (deleted) the old app's value at shutdown. Now an address that looks like ours but was set by
   another program is never touched, by an off, by Shutdown, by the exit hook or by the start-up cleanup (which also asks whether anything
   answers on port 18081 before it touches anything). Cost: if the module's state file is lost, a leftover address stays until the user removes
   it (see "Last resort").
2. **The proxy port accepts only Steam hosts.** `CONNECT` and absolute-URI requests to a host that is not on the list get `403` (counted as
   refused, not as an error). The old app forwarded anything. The PAC sends only listed hosts here, so nothing legitimate changes.
3. **The proxy never connects to itself or to a special address.** A name or literal that resolves to loopback, `0.0.0.0`, multicast or
   broadcast is refused (`Host: 127.0.0.1` on `127.0.0.1:80` would otherwise connect the forwarder to the forwarder).
4. **Bad input is answered, not thrown.** A malformed request line, port, `Host` or `Content-Length` gets `400` and counts as refused. Errors
   (`steam.proxy.errors`) are connections that failed for a real reason: upstream unreachable, an unexpected DNS miss, an internal fault.
5. **Limits.** A client must send its first request head within 15 s, a head is at most 64 KB, at most 1024 connections at once, and open
   connections are closed when the proxy stops. The engine's warnings reach the log at most 20 lines a minute.
6. **The domain list** is read from the module's data folder first (a user's own `steam-domains.txt`), then the module folder, then the built-in
   list. Lines that are not plain domain names are dropped (they are written into the PAC script).
7. **System DNS answers of 127.0.0.1 or 0.0.0.0 are never used as an address** (same as before), and an `HttpClient` replaces the .NET Framework
   web request classes, so the `ServicePointManager` settings of the old app have no equivalent here.

## Evidence (2026-11-01, this machine, Windows 11, elevated host, bypass never turned on)

- `dotnet build Rigbay.sln --no-incremental`: 0 warnings, 0 errors.
- `dotnet test Rigbay.sln`: PredatorSense 102, XTU 190, **Steam access 248**, Core 285, all passed (the module alone: 248).
- `Rigbay.LangCheck --strict` on the module: ok, 100 keys, 100/100 in `en` and `vi`.
- The tests that exercise real code on real sockets use ephemeral loopback ports chosen by the system (never 80 or 18081) and a fake upstream
  of their own; the registry code runs against a scratch key `HKCU\Software\RigbayTests\SteamBypass\<guid>`, removed after each test, with no
  WinINet notification. Fuzz: 21 hand-made hostile inputs and 60 random byte streams (seed 20261101) at both ports, then the proxy still serves
  the PAC and the download port and every connection is closed.
- The built module folder (no `Rigbay.Abstractions.dll` in it) was loaded by the real app from a scratch modules folder with a scratch data
  folder (`tools/run-elevated.ps1`). **The real host found a manifest error that the unit tests had not** (a `requires` entry over 200
  characters); it is fixed and a test now checks the limits.
  PNGs of the app window (PrintWindow of its own HWND, each one looked at) in `docs/steambypass/evidence/`: `page-off-dark`, `page-off-light`,
  `page-off-vi-dark`, `dashboard-dark`, `dashboard-light`, `approval-dark` (the dialog for the switch: effect text, "Steam bypass: Off → On",
  Cancel is the default button), `approval-auto-on-light`.
- On this machine the standalone app was running (listening on 127.0.0.1:18081 and :80, `AutoConfigURL` set to its address). Loading the module,
  opening its page and running `rigbay steam-bypass status` from the app's script did not touch it: the log says "Windows points at the Steam
  proxy address and another program answers there; left as it is.", and `AutoConfigURL` was read before and after the runs and was unchanged.
  The approval dialog was opened and nobody answered it.

## Not verified

- **The bypass turned on, on the real machine** (a real `AutoConfigURL` write and real listeners on 18081 and 80). Not done, by the task's rule.
  The same code ran against a scratch registry key and fake listeners (module level) and real loopback sockets on free ports (engine level).
- The real network: DoH to 1.1.1.1 / 8.8.8.8 / cloudflare-dns.com / dns.google (the resolver is tested with a fake handler), the real Steam
  servers, whether the middlebox still behaves as measured in October 2026 (`blocking-evidence.md` of the old repo), the TLS split against a
  real filter (tested: the upstream sees a 2-byte first segment, then the rest, in order).
- A live Steam client: the five Steam states are tested on a fake probe and the TCP-table code (`GetExtendedTcpTable`, 24-byte rows) is a port
  that has not run here. The old app did not verify the row against a live Steam either.
- Game download speed through the forwarder (the old app measured about 570 Mbit/s; this port copies the same loops but was not measured).
- The "Run test" button and `rigbay steam-bypass test` against the real network.
- `rigbay steam-bypass on|off` through a real running host: the verbs are tested on the fake host; a headless run is refused by the host
  because there is nobody to ask.
- Windows 10, 125 % and 150 % scaling, high contrast, screen readers, other ISPs, sleep and resume, a crash or kill of the host with the
  bypass on (the crash cleanup is tested by running two managers over the same state file and by the scratch registry key).
- A host whose elevation uses a different account than the signed-in user: HKCU is then another user's hive, and the setting would be written
  for the wrong user.

## Things to know

- **Two programs, one address.** The standalone Steam Bypass and this module use the same ports and the same PAC address. Run one at a time.
  If the old one is running, the switch shows "Port 18081 is already in use. Is the Steam Bypass app running? Close it first." and nothing is
  written. The module also never undoes the old app's setting.
- **Steam reads the proxy setting only when it starts.** A Steam opened before the bypass has no connection to the proxy; the Steam row says
  "Steam was opened before the bypass: restart it" after 45 s (the same rule as the old app). The module does not restart Steam by itself.
- **Port 80 busy is not fatal.** The bypass turns on, an info row and a toast say game downloads will not work, the state line says so.
- **The Windows value is only restored while the module runs.** If Rigbay is killed with the bypass on, the next start of the module restores
  it (when it owns the value and nothing else answers on 18081). Until then Windows has a PAC address with nothing behind it. A PAC that cannot
  be fetched should mean a direct connection, and a cached one says `PROXY ...; DIRECT`, but neither was tried here.
- **The host's wording.** The host's approval dialog is titled "Change your hardware?" and the row says "Changes your hardware. Rigbay asks
  before it applies." (`HardwareEffect` is the only approval path a page row has). The text is true to the contract but odd for a proxy
  setting; a neutral host string would help (mailed to the coordinator).
- **Last resort** if Windows still points at `http://127.0.0.1:18081/proxy.pac` and nothing runs: the previous value (if there was one) is in
  `%LOCALAPPDATA%\Rigbay\modules\steam-bypass\proxy-state.json`. Put it back, or remove the value:
  `Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' AutoConfigURL`.

## The live test the owner can approve afterwards

One session, about five minutes, on this machine. It changes the current user's Windows proxy setting and opens two loopback ports.

**Before:** close the standalone Steam Bypass app (it owns 127.0.0.1:18081, :80 and the current `AutoConfigURL`; at the time of this report it is
running). Note the current value:
`(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings').AutoConfigURL`.

**Exact change** (everything the approval dialog says): `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\AutoConfigURL` becomes
`http://127.0.0.1:18081/proxy.pac`; the module listens on 127.0.0.1:18081 and 127.0.0.1:80; the previous value is saved in
`%LOCALAPPDATA%\Rigbay\modules\steam-bypass\proxy-state.json`. Nothing else is written, nothing listens on another address.

1. In Rigbay, enable "Steam access" and set the switch "Steam bypass" to on; answer the dialog. Expect the row "Listening" to show both
   ports, the state line "Bypass on · 0 open", and `Get-NetTCPConnection -State Listen -LocalPort 18081,80` to show only `127.0.0.1`.
2. Press "Run test" (or `rigbay steam-bypass test`). Expect four `Reachable` lines on the owner's network. Then start Steam (after the bypass)
   and watch "Open connections" rise and the Steam row say "Steam is using the bypass".
3. Optional: `curl.exe -s -o NUL -m 15 -x http://127.0.0.1:18081 -w "%{http_code}`n" https://store.steampowered.com/` expects 200 or a redirect.

**Undo:** set the switch to off and answer the dialog (or press Revert, or disable the module, or close Rigbay: all four restore it). Verify the
`AutoConfigURL` line printed before is back and `Get-NetTCPConnection -State Listen -LocalPort 18081,80` is empty. If anything is off, the
"Last resort" above puts it right. The owner can then start the standalone app again.

**What to look for / what a failure means:** "Port 18081 is already in use" means the old app is still running. A Steam row stuck on "restart it"
means Steam was started before the bypass. A red "Blocked (connection reset)" on one row with the others green names the layer that failed
(see the old repo's `blocking-evidence.md`).

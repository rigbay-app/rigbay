---
name: steambypass-module
description: Working knowledge of the Rigbay Steam access module (modules/steambypass, id steam-bypass, .NET 8, in process; a local proxy on 127.0.0.1:18081 and :80, DNS-over-HTTPS for Steam names, a PAC file, the current user's HKCU AutoConfigURL, the four-check connection test, metrics steam.proxy.active and steam.proxy.errors, rigbay steam-bypass status|on|off|test). Use it before reading or changing anything under modules/steambypass, when touching the proxy, the registry restore logic, the approval flow, the domain list, a language key or a test, and before any live check on the real machine. Saves re-deriving the layers, the safety rules and the gotchas below.
---

# Steam access module: codebase guide

Module id `steam-bypass`, group `network`, `modules/steambypass/src/Rigbay.SteamBypass` (net8.0) with tests in
`modules/steambypass/tests/Rigbay.SteamBypass.Tests` (248 at the last run). It is the port of the standalone app `minhdevtree/steam-bypass`
(`D:\Documents\Projects\steam-bypass`, read only for us; its skill `.claude/skills/steam-bypass` and `references/blocking-evidence.md` hold the
measurements: what the ISP blocks, what passes the filter, how to re-measure). User docs, evidence and the live test request:
`docs/steambypass/MODULE.md`. Method index: `references/code-map.md`.

## Hard constraints (check before you write code)

- **Never turn the bypass on, or write `HKCU\...\Internet Settings\AutoConfigURL`, or listen on port 80 or 18081, on the real machine without
  the owner** (`csm gate open`). Tests and tools use `ProxyOptions { ProxyPort = 0, DownloadPort = 0 }`, `FakeListenerFactory` and a scratch
  registry key (`ScratchKey` in the tests). A test fails if a test source mentions `new RegistryProxyStore(` or `Dependencies.Real()`.
  Loading the module in the real app and looking at its page is fine: with nothing turned on it only reads.
- **The standalone app may be running on this machine** (it was on 2026-11-01: listening on 18081 and 80, `AutoConfigURL` its address). It uses
  the same address and ports. The module must never undo a value it did not set: `SystemProxyManager.Restore` needs the module's own saved state
  (`proxy-state.json`) as well as an address that looks like ours. Do not weaken that.
- **Order matters.** On: listen, then write Windows. Off: restore Windows, then stop the proxy. A failed registry write stops the proxy again.
  A failed restore keeps the saved state and is retried (next off, next start, process exit). `BypassController` has the tests for each.
- **Loopback only.** `ILoopbackListenerFactory.Listen(int port)` has no address parameter; only `Net/Listeners.cs` may use `TcpListener`
  (a test scans for it). Do not add a way to bind another address.
- **Only listed Steam names go through DoH** (`UpstreamDialer`), except what arrives on the download port (forced DoH, as in the old app).
  The proxy port refuses hosts that are not on the list (403). Never connect to loopback/any/multicast (`UpstreamDialer.IsForbidden`).
- **TLS 1.2 or newer with `HttpClient`**; no `ServicePointManager`, `HttpWebRequest`, certificate callbacks (a test scans the source). The DoH
  client has `UseProxy = false` (or our own PAC would catch it).
- **Rigbay rules** (`docs/ABSTRACTIONS.md` section 12): constructor does nothing, `Probe` is read-only, UI thread only through `RunOnUi` (the
  module has no UI code), catch everything on threads and timers the module starts, all text is a language key, no static state that survives
  `Shutdown`. The one `AppDomain.ProcessExit` hook is added in `Initialize` and removed in `Shutdown`.
- **Page rows that change Windows carry a `HardwareEffect`** (the switch and `autoOn`): the host asks, the verbs ask through `host.Approval`.
  Never add a path that calls `controller.TurnOn()` or `TurnOff()` without one of: the host-confirmed row, `ChangeWithApprovalAsync`, the
  `autoOn` setting (consent given when it was switched on), `Shutdown`/exit (restore only).
- **Manifest limits** (the host refuses the module otherwise, found the hard way): `requires` entries 1 to 200 characters, description up to
  200, name up to 60, JSON backslashes doubled. A test checks them. No `icon` field until task A11 lands it.
- Language: keys `module.steam-bypass.*` in `lang/en.lang` (reference) and `vi.lang`. Run
  `dotnet run --project src/Rigbay.LangCheck -- modules/steambypass/src/Rigbay.SteamBypass/lang --module steam-bypass --source modules/steambypass/src/Rigbay.SteamBypass --strict`.
  Keys must appear as string literals (LangCheck's unused check), so `CheckSpec` carries its keys instead of building them with `$""`. A test
  also scans the source for key literals and fails when one is missing from either file.
- The module must not edit `src/*`, `tests/*` or the old repo; host gaps go to the coordinator by mail.

## Gotchas

- **Races on the switch.** `OnBypassChanged` starts one `SwitchWorker`; it reads `_bypass.Value` when it works (not when the flip happened) and
  loops until the controller matches, so the last flip wins. `OnControllerChanged` does not sync the switch (it would undo a newer flip). Only a
  failure, a verb or auto-on sets the switch to what is real (`SyncSwitch`).
- **The host's Revert** of the switch is a toggle back: after "on", Off restores; after "off", On turns it on again (asked about).
- **Steam state** is only worked out while the page is visible (and by the `status` verb); `NeedsRestart` needs both the bypass and steam.exe to be older than 45 s.
- **`Changed` on `ObservableValue` fires on the setter's thread**: the verbs set `_bypass.Value` from a pool thread, the host sets it from the UI thread.
- **FakeCommands** in the tests does not clear verbs on `Shutdown` like the real host; a test that enables twice clears them by hand.
- **Real-host check found two things unit tests did not**: the manifest limits (above) and the host's hardware wording on the dialog.
  Always load the built folder in the real app after changing `module.json` (recipe below).
- **Port 80 busy is a warning, not a failure**; port 18081 busy is a failure and writes nothing.

## Recipes

**Build and test:** `dotnet build Rigbay.sln --no-incremental`, `dotnet test modules/steambypass/tests/Rigbay.SteamBypass.Tests/...csproj`
(about 8 s; the fuzz and timeout tests are most of it). The engine tests open loopback sockets on free ports.

**Look at the page in the real app without touching anything:**
`tools\build.ps1` once, then `dotnet build modules/steambypass/src/Rigbay.SteamBypass -c Release -o tools/out/modules-steam/steam-bypass`
(delete the `Rigbay.Abstractions.*` files from that folder), then for each picture
`powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-elevated.ps1 -Wait -Data <scratch data dir> -Modules tools\out\modules-steam --shot <png> --exit-after 300 --allow-multi --theme dark --size 1240x1500 --script "enable:steam-bypass;wait:1500;page:steam-bypass;wait:600"`.
Never use `edit:row.bypass.header` with `approve:true`. Read the PNG. Before and after, read `AutoConfigURL` (read only) and check it did not change.

**Add a connection check:** add a `CheckSpec` (with literal title and sub keys) to `CheckSpec.All`, add the four language entries, update
the count in the tests.

**Add a domain:** edit `steam-domains.txt` (and `DomainSet.BuiltIn`; a test compares them).

**Change a port:** `ProxyOptions.DefaultProxyPort` is the only definition; the PAC address, the ownership prefix and the effect texts in
`lang/*.lang` (they spell the address and ports) must change with it.

**The blocking changes (Steam fails on a new network):** do not guess; follow `blocking-evidence.md` "Re-measuring" in the old repo's skill.

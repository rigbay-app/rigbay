# Code map (method level)

Namespace `Rigbay.SteamBypass`; everything `internal` except the module class and `SteamBypassSettings`. Paths are under
`modules/steambypass/src/Rigbay.SteamBypass/`.

## Threads

| Thread | What runs there | Rule |
| --- | --- | --- |
| pool (host) | `Probe`, `Initialize`, `Shutdown`, verbs | `Initialize` and `Shutdown` have 10 s and 5 s; both only do registry and socket work that takes milliseconds |
| UI thread | the host sets `ObservableValue`s when the user edits a row | `OnBypassChanged` and `OnAutoOnChanged` return at once |
| pool (module) | `SwitchWorker`, `AutoTurnOn`, the poll timer's `Tick`, every connection handler | catch everything; log through `host.Log` |
| process exit | `BypassController.RestoreOnExit` | registry only, never throws, waits at most 2 s for the lock |

Counters in `ProxyEngine` are `Interlocked` ints read without a lock. The controller serialises on and off with one lock; `TurnOff` always
looks at the Windows value, even when it is already off (a failed restore needs the retry).

## Files

| File | What lives there |
| --- | --- |
| `SteamBypassModule.cs` | Lifecycle, wiring, the switch worker, polling, metrics publishing, the log throttle |
| `SteamBypassModule.Page.cs` | `GetPageModel`: groups Bypass, Status, Connection check, Settings; summary |
| `SteamBypassModule.Texts.cs` | `RefreshTexts` (every text and flag from the state), `RunChecksAsync`, `FormatCheck` |
| `SteamBypassModule.Commands.cs` | verbs `status`, `on`, `off`, `test`; `ChangeWithApprovalAsync` is the only approval path of a verb |
| `BypassController.cs` | `TurnOn`, `TurnOff`, `RecoverStale`, `RestoreOnExit`; results `TurnOnResult`, `TurnOffResult` |
| `SystemProxy.cs` | `ISystemProxyStore` + `RegistryProxyStore`, `IProxyStateStore` + `FileProxyStateStore`, `SystemProxyManager.Apply/Restore` |
| `Proxy/ProxyEngine.cs` | listeners, `HandleProxyAsync` (CONNECT with the 2-byte split, absolute URI with `HoSt:`, PAC), `HandleDownloadAsync` (the :80 forwarder, upstream switch per request), limits, counters |
| `Net/DomainSet.cs` | list parsing and validation, `Contains` (whole labels), `BuildPac` (ES3) |
| `Net/HttpHead.cs` | `ReadAsync` (Latin-1, 64 KB cap), `Request`, `Header`, `TrySplitHostPort` |
| `Net/Resolver.cs` | `DohResolver` (4 endpoints, `ParseAnswer`, cache 30 to 300 s, system fallback without loopback answers), `DnsMissException` |
| `Net/Dialer.cs` | `UpstreamDialer` (DoH for listed names, `IsForbidden`, 4 addresses x 5 s) |
| `Net/Listeners.cs` | `ILoopbackListenerFactory` (no address parameter) and the real one |
| `Check/ConnectionCheck.cs` | `CheckSpec.All` (the four checks), `HttpConnectionProbe`, failure classification |
| `SteamLink.cs` | `SteamLink.Evaluate` (pure), `SteamProbe` (processes, `GetExtendedTcpTable`) |
| `Dependencies.cs` | the production wiring and the seams the tests replace |
| `LogThrottle.cs` | 20 lines a minute for engine warnings |

## Tests (`tests/Rigbay.SteamBypass.Tests`)

`DomainAndHttpTests` (matching, PAC text, head parsing, address policy), `DohTests` (parsing, fall-through, cache, source scan for legacy
TLS APIs), `SystemProxyTests` (apply/restore, crash recovery, state file, scratch registry key), `ControllerTests` (order, failures, cleanup,
exit), `ProxyEngineTests` (real loopback sockets on free ports: bind address, PAC, TLS split, host respelling, download port, limits, fuzz),
`ModuleTests` (page shape, manifest, switch, verbs and approval, auto-on, Steam row, polling, language, log throttle), `ModuleRig.cs` and
`Fakes.cs` (the fakes: `MemoryProxyStore`, `MemoryStateStore`, `FakeListenerFactory`, `FakeDialer`, `FakeResolver`, `FakeProbe`, `FakeSteam`).

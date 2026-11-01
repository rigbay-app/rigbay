# Code map (method level), `modules/xtu/Rigbay.Module.Xtu`

Names are exact, line numbers left out on purpose. Everything is `internal` except `XtuModule`, `XtuSettings` and `CustomProfile`
(the settings are serialised by the host).

| File | What lives there |
| --- | --- |
| `XtuModule.cs` | `IModule`: `Probe` (`ProbeLogic.Run`), `Initialize` (create the runtime, `Start`, `StartTimers`), `GetPageModel` (`BuildPage`), `Shutdown` (dispose). `CreateRuntime` is the only place that news up the Intel ports, the latch and the Windows power events |
| `Model.cs` | `XtuIds` (the four ids, `IsAllowed`), `Limits`, `ProfileKind`, `XtuState`, `XtuProfile` (`Matches`, `Describe`, `FromState`), `CustomProfile`, `Fmt` (invariant formatting), `XtuException` (language key + args, `ToText`) |
| `Rules.cs` | `Validate`/`Check` (ranges, returns a key), `CheckName` (1 to 40, not `@`, no tab/newline, unique ignoring case) |
| `Catalog.cs` | `Catalog.Build/FindActive/FindByName`, `ProfilesXml.Load/Parse` |
| `Client.cs` | ports and DTOs, `IXtuClient`, `WriteLatch`, `XtuClient` (`ReadState`, `ReadMonitor`, `Apply` = the sequence; every failure becomes `XtuException`) |
| `Intel/IntelPorts.cs` | `Pipes` (binding with 64 MB quotas), `PipeCheck` (probe: does an endpoint accept a connection; missing, listening or unknown), `IntelTuningPort`, `IntelMonitorPort` (registers its pid with `Connect`/`Start`, `Stop` on dispose). Only file that calls the XTU contracts or WCF |
| `Intel/Contracts.cs` | the module's own copies of the XTU contracts: `IXtuTuning` (7 of 29 operations), `IXtuMonitoring` (4 of 12), `XtuTuningControl`, `XtuTuningProposal`, `XtuTuningProposalResult`, `SdkClientToken`, `TuningResult`, `TuningErrorCode`. Names and namespaces are the wire format: do not tidy |
| `Apply.cs` | `ApplyOrigin`, `ApplyStatus`, `ApplyResult`, `ApplyService.ApplyAsync` (one apply at a time; `Busy` is dropped), `Detail` (the approval text), `Completed` event |
| `Monitoring.cs` | `Mon` ids, `XtuMetrics` ids, lists (`Monitored`, `PerCore`, `All`) and `Descriptors` (labels), `MonitorSampler.Tick/Reset/WithdrawAll/PerCore` (warm-up, unit conversion) |
| `AutoSwitch.cs` | `PowerLine`, `IPowerEnvironment`, `AutoSwitcher.Check` (pure decision) |
| `Hotkeys.cs` | `HotkeyPolicy.Refuse`, `ReadXtuCombos`, `PairingOn`, `ActionId` |
| `System.cs` | `XtuPaths` (ProgramData folder only), `ISystemProbe` and `WindowsSystemProbe` (CPU vendor from the registry, `ServiceController`, `Pipe` via `PipeCheck`, `ServiceVersion` from the service image path, Fast Startup flag, `GetSystemPowerStatus`), `ProbeLogic`, `WindowsPowerEnvironment` (`SystemEvents.PowerModeChanged`, all exceptions caught) |
| `Commands.cs` | `XtuCommands`: `status` (same lines as the old CLI), `list`, `apply <name>` |
| `Settings.cs` | `XtuSettings` (the settings object the host serialises) |
| `Runtime.cs` | `XtuRuntime` lifecycle: `Start`, `StartTimers`, `Dispose`, `LoadProfiles`, `Refresh`, `StatusTick`, `MonitorLoop`/`MonitorOnce`/`MonitorWanted`/`MonitorFailed`, `ApplyByNameAsync`, `OnApplyCompleted`/`Announce`, `AutoCheck`, `OnPowerChanged`/`OnResumed`, `Guard`, `RequestPageRefresh` |
| `Page.cs` | `XtuRuntime` page side: observable values, `ShowState`, `OnProfileChosen`, `OnAutoSettingChanged`, `RegisterProfileEntries`/`GuardBinding` (hotkeys and tray), `BuildPage`, `NewProfileRow`/`CreateAsync`, `CustomProfileRow`/`DeleteAsync`, `ReloadAsync` |

## Threads

| Thread | What runs there | Rule |
| --- | --- | --- |
| host UI thread | value `Changed` handlers when the user edits a row | do no slow work: `Fire` hands it to the pool |
| `xtu-monitor` | `MonitorLoop` | owns nothing but the monitor port (own lock in `XtuClient`); catches everything |
| timer pool | `StatusTick` (5 s), resume re-apply | `Guard` around everything; `StatusTick` is not re-entrant |
| system thread | `SystemEvents.PowerModeChanged` | `WindowsPowerEnvironment` catches everything |
| pool (Task.Run) | `ApplyAsync` work | the tuning port is serialised by `XtuClient._tuningGate`; `ApplyService` allows one apply at a time |

## Things that look wrong but are intended

- `ApplyService` does not ask for `PageConfirmed` and `Auto` (explained in `SKILL.md`).
- `rigbay xtu status` exits 0 even when XTU is unreachable (the old app does; the `connected:` line says so).
- The profile `ChoiceRow` has no label for "no profile matches" other than the first option `-` ("Custom values"); selecting it does nothing.
- Hotkey registrations for deleted profiles stay until the module is turned off (no unregister in the host API).

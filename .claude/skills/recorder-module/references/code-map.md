# Recorder module: code map

All under `modules/recorder/src/Rigbay.Recorder/`.

## Module

| File | What is in it |
| --- | --- |
| `RecorderModule.cs` | `IModule`: `Probe` (always yes), `Initialize` (settings, `RecorderServices`, engine, handlers, verbs, tray, hotkey, the `recorder.rows` descriptor), `Shutdown` (stop with `ModuleStopped`, unsubscribe, withdraw `recorder.rows`). Reactions: `OnEngineChanged` (publish rows, flip idle/recording, tray visibility), `OnEngineEnded` (withdraw, last message, toast, page refresh), the 1 s `Timer` that keeps the elapsed text moving (`StartClock`/`StopClock`), `RefreshTexts` (every status `ObservableValue`), settings handlers, `ResolveFolder`, `RequestFromSettings`. |
| `RecorderModule.Page.cs` | `GetPageModel` (groups `recording`, `metrics`, `history`), `StartFromPage`, `StopFromPage`, `StartRecording` (remembers a refusal as the last message), `OpenFolder`/`OpenFile` (through `IShell`), `ValidateFolder`, the picker (`OfferedMetrics`, `SyncPicks`, `OnPickChanged`, `SetAllPicks`, `UpdatePickedText`), `RefreshPageIfStale` (page visible or catalogue changed: rebuild when the set of metrics differs), `HistoryRows`. |
| `RecorderModule.Commands.cs` | `RegisterCommands` (`start`, `stop`, `status`), `ParseStart`/`StartArgs`, the three handlers, `RegisterShortcuts` (tray items `start`/`stop`, hotkey `toggle` with no default gesture), `ToggleFromShortcut`. |
| `RecorderSettings.cs` | Period, duration, folder, excluded metric ids; `PeriodChoices`/`DurationChoices` with their label keys; `Snap`, `Clamped`. |

## Engine (`Recording/`)

| File | What is in it |
| --- | --- |
| `RecorderEngine.cs` | `Start` (validate, folder, free space, subscribe, snapshot of `GetLatest()`, columns, `OpenFiles`), `Stop`, `Status`, `RunAsync` (the schedule loop, disk check, `WriteRow`, late metrics), `Finish` (flush, close, final meta, summary, `Ended`), `WriteMeta`. Types: `StopReason`, `StartRequest`, `StartOutcome`, `RecordingSummary`, `RecorderStatus`, `RecorderServices`. Constants: `MinFreeToStart`, `MinFreeToContinue`, `StaleAfter`. |
| `FixedSchedule.cs` | `Take(now)` returns the slot to stamp and moves `Next` by one period; resets when more than a period late. |
| `CsvFormat.cs` | Number, time, field quoting, file stem, `Clock` (`hh:mm:ss`), `Size` (`48.0 KB`), `Utf8NoBom`, the file name constants. |
| `MetaFile.cs` | `MetaDocument`, `ColumnInfo`, `LateMetric`, the three standard notes. |
| `MetricCatalog.cs` | `IMetricCatalog`, `BusCatalog` (A12's `GetMetricInfo`), `BareCatalog` (label = id). |
| `Environment.cs` | Seams and their real versions: `IDiskSpace`, `IShell`, `IMachineFactsSource` (registry), `IModuleVersions` (sibling `module.json`). |
| `History.cs` | The newest 10 `rigbay-log-*.csv`, newest first, optionally without the file being written. |

## Resources

`module.json` (id, group, icon `icon.svg`, registry reads declared), `icon.svg` and `icon-glyph.svg` (the brand's tile and quiet glyph, indigo `#4F5BD5`), `lang/en.lang`, `lang/vi.lang`.

## Tests (`tests/Rigbay.Recorder.Tests/`)

| File | Covers |
| --- | --- |
| `EngineTests.cs` | `Rig` (engine + fake bus/clock/disk + temp folder), schedule, formats, columns, end conditions, disk, files, meta, cancel safety, history. `FailingStream` is a disk that fills up. |
| `ModuleTests.cs` | `ModuleRig` (the module on a fake host): lifecycle, page, buttons, status rows, toast, history rows, settings, picker, verbs, tray, hotkey. |
| `CatalogTests.cs` | `BusCatalog`, the labels in meta and picker, page refresh on catalogue or language change, the real services on this machine (a 1.2 s recording with the real clock, disk and registry). |
| `ManifestTests.cs` | `module.json` is valid JSON and matches the code, the icons are plain drawings, the language files have no lone backslash. |
| `Fakes.cs` | `FakeClock`, `FakeBus` (stamps with the clock, delivers, counts subscribers, has the A12 members), `FakeHost` and the other host services, `FakeLocalisation` (unescapes like the host), `TempFolder`, `Files.ReadBytes`. |

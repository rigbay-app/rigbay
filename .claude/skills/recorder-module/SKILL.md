---
name: recorder-module
description: Working knowledge of the Rigbay recorder module (modules/recorder, net8.0, writes the metrics bus to CSV plus a .meta.json, page with start/stop, period, duration, metric picker, folder and history, tray, optional hotkey, rigbay recorder start/stop/status verbs, disk safety). Use it before reading or changing anything under modules/recorder, when adding a column rule, a verb, a language key or a page row, when a CSV or meta looks wrong, and before a live check with the real app. Saves re-deriving the engine, the timing and staleness rules and the traps below.
---

# Recorder module: codebase guide

Module id `recorder`, group `monitoring`, `modules/recorder/` (net8.0, no NuGet dependency), tests in `modules/recorder/tests/Rigbay.Recorder.Tests` (version 0.1.3 unreleased, host API `>=0.1.4`; 0.1.3 added `CommandInfo` to the three verbs: status ReadOnly, start/stop Writes with the setting kind because they write files, not hardware; `PreviewStart` reads only the arguments and the page settings; `start` reports an empty `UndoArgs` when it began a recording; there is no poll rate to decide, the recorder publishes no metric; tests: `VerbMetadataTests.cs`).
User-facing description, formats and the verification record: `docs/recorder/README.md`. Method index: `references/code-map.md`. Tests, live script and the traps of writing files here: `references/testing.md`.
The old app's recorder is described in `docs/xtu/RECORDER-NOTES.md` (columns, units, what the bus cannot carry).

## Hard rules (check before you write code)

- **The recorder knows no module by name.** It records what is on the bus: columns come from `GetLatest()`, labels from `IMetricsBus.GetMetricInfo` (A12) through `BusCatalog`, units and sources from the samples.
  Never add a special case for an id or a module. If a source needs something (a text channel, units), that is a host or contract change: mail the coordinator.
- **It subscribes to the bus only while it records** (`RecorderEngine.Start` subscribes, `Finish` disposes). A subscription is what makes polling modules read fast (`SubscriberCount`), so an idle recorder must hold none.
  The picker reads `GetLatest()`, which is free. Test: `The_recorder_listens_only_while_it_records`, `While_it_is_idle_the_recorder_does_not_listen_to_the_bus`.
- **Never delete or overwrite a file.** Files are opened with `FileMode.CreateNew`, a taken name (csv or meta) gets `-2`, `-3`; the history only lists. The only file the engine rewrites is its own `.meta.json`, via a temp file and a rename.
- **Never throw to the host.** Every handler, callback and the writing task catch everything; a failure ends the recording, is logged and shows up in the summary (`StopReason`). `RecorderEngine.Start` returns a refusal as a `StartOutcome`, not an exception.
- **One thread writes.** After `Start` returns only the engine's task touches the file; `Stop` signals and waits (4 s at most, the host allows 5 for `Shutdown`).
- **Disk:** 200 MB free to start, under 50 MB stops (`MinFreeToStart`, `MinFreeToContinue`), checked before every row through `IDiskSpace`. A drive whose free space is unknown does not block.
- **Rows are on a fixed schedule** (`FixedSchedule`: `Next += period`, reset to now when more than a period late) and stamped with the slot. Do not stamp with the wake-up time.
- **A value counts only if its sample is at most 30 s old** (`StaleAfter`) and the metric is not withdrawn; otherwise the cell is empty. Do not "carry forward" a missing value.
- **Columns are fixed at the start**; later metrics go to `late_metrics` in the meta. Order: source module, then id, both ordinal.
- **The recorder's own metric (`recorder.rows`) is not recorded** (source filter), and it is not in the dashboard summary (a reading "Not available" next to "Idle" looks like an error). It is published while recording and withdrawn at the end.
- Rigbay rules (`docs/ABSTRACTIONS.md` section 12): constructor does nothing, `Probe` is read-only, no dispatcher of its own, all text is a language key, no static state that survives `Shutdown`. Start and stop are not hardware changes: no `HardwareEffect`, no approval.

## Layers

`RecorderModule` (partial: `RecorderModule.cs` lifecycle, status texts, settings; `.Page.cs` page, picker, history; `.Commands.cs` verbs, tray, hotkey) builds a `RecorderEngine` (`Recording/RecorderEngine.cs`) from `RecorderServices`
(clock, delay, disk, machine facts, module versions, catalog, file creation: all replaceable, tests replace them). The engine uses `FixedSchedule`, `CsvFormat`, `MetaDocument`, `History`.
`RecorderSettings` is the user's choice (period, duration, folder, **excluded** metric ids). The module's internal constructor takes a services factory, an `IShell` (Explorer) and the default folder, which is how tests host it.

## Adding things

- **A language key:** `lang/en.lang` first, then `vi.lang`, in the same order; keys are module-relative in code (`row.start.header`) and `module.recorder.*` in the files. A backslash is written `\\` (the host drops a single one). Run LangCheck with `--strict`
  (it lists keys that no string literal in the source uses, so do not build keys by concatenation: `RecorderSettings.PeriodChoices` and `DurationChoices` spell the label keys out for that reason). `PageTests.Every_key_on_the_page_...` fails on a key that is not in `en.lang`.
- **A page row:** add it in `GetPageModel`, bound to an `ObservableValue` kept as a field (values live as long as the instance). Rows that depend on the state use `Enabled = _idle` or `_recording`. A list that changes (history, picker) is rebuilt with `host.RequestPageRefresh()`.
- **A verb:** register it in `RegisterCommands`; arguments go through a parser with a test per bad form (`ParseStart`). `stop` when nothing runs is exit 0 on purpose.
- **A meta field:** `MetaDocument` (property names are `[JsonPropertyName]` snake_case); keep it additive and update `docs/recorder/README.md`.
- **A stop reason:** add it to `StopReason`, the meta's `stop_reason` mapping, `EndedNotice`, the `ended.*` keys (en and vi) and a toast if nobody is looking.

## Gotchas

- **The page layout (0.1.1, UI review RC-1).** `PageModel.Primary` is a Start row while idle and a Stop row while recording, made new for every
  page on purpose (the host keeps a button's last message on the row instance; "Recording to x.csv" must not reappear beside Start), while the
  option rows are made once (their Revert must survive the rebuilt page). A page-start refusal is the button's message only (`StartFromPage`
  puts the previous `_lastNotice` back); from the tray, hotkey or verb it lands in the line. Their `Line` is `_primaryLine` (file · elapsed · rows · size, or how the last one ended, or nothing). `ApplyRecordingState` calls
  `RefreshPageIfRecordingChanged`, which asks the host for a new page when the main button must change. There are no status rows: put a new fact in
  that line, never in a row that reads "–" while idle. The folder is an `ExpanderRow` (Open as `HeaderRow`, path `TextRow` inside, `_folderLine`);
  "Select all" sits on the "Selected metrics" row, and each source expander has its own "n of m" line (`_sourceLines`).

- `IsRecording` turns false just **before** the `Ended` handlers run (the toast, the page refresh). A test that checks those must wait for them (`ModuleRig.WaitUntilFinished`).
- A reader of the csv while it is open must use `FileShare.ReadWrite` (the engine allows readers only: `Files.ReadBytes` in the tests).
- `Delay` in the tests is a fake clock that returns at once; an open-ended recording needs `BlockAfter`, or it spins until the disk is full.
- `module.json` must be valid JSON with `\\` in the registry strings: `ManifestTests` parses it, because the host lists a bad manifest as "Invalid" and nothing else notices.
- The host calls `Initialize` again on the same instance after a disable: handlers are re-subscribed in `Initialize` and removed in `Shutdown`; `_picks` (the switches) are kept and re-synced from the settings.
- `RecorderModule.Engine` is `internal` for tests; never use it from the page code for anything the module's own methods do (they keep the texts and the toast in step).

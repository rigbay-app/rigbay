# Recorder module

Rigbay module `recorder` (group `monitoring`): writes every metric the other modules publish on the metrics bus to a CSV file, with a `.meta.json` next to it
that says what each column is (id, label, unit, source module, description), the machine, the module versions and when the recording started and ended.
Rows are one period apart exactly (1, 2, 5, 10 s or any number from the verb), a missing or withdrawn metric is an empty cell, and it refuses to start below
200 MB free and stops cleanly below 50 MB. The recorder knows no other module: whatever publishes to the bus is recorded.

- Module: `src/Rigbay.Recorder/` (net8.0). Tests (a fake bus, a fake clock, a temp folder; no hardware, no other module): `tests/Rigbay.Recorder.Tests/`.
  Live check with the real app and PredatorSense (reads only): `tools/live-record.ps1`.
- Solution: `Rigbay.Recorder.slnx`. This folder is self-contained (own `Directory.Build.props`); its only dependency outside it is the `ProjectReference` to
  `Rigbay.Abstractions` (in a separate repository, replace it by the NuGet package). It has no NuGet dependency of its own.
- Version **0.1.2** (0.1.0 and 0.1.1 are released): 0.1.1 is the page of the November 2026 UI review, 0.1.2 fixes timing problems of the writing loop and three races of the page, see [CHANGELOG.md](CHANGELOG.md). Needs host API 0.1.3
  (`PageModel.Primary`, `Row.Line`, `Row.HelpKey`, `ExpanderRow.HeaderRow`); the metric catalogue of task A12 (`IMetricsBus.GetMetricInfo`) gives the labels.
- Full description (page, CSV and meta format, timing rules, disk safety, verification record, findings): `docs/recorder/README.md` in the Rigbay repository.
  Notes for changing it: `.claude/skills/recorder-module/` there.

## Using it

```
rigbay recorder start [--every S] [--minutes N] [--metrics a,b]
rigbay recorder status
rigbay recorder stop
```

Or the page (Monitoring, Recorder: one big "Start recording" / "Stop recording" button), the tray items "Start recording" / "Stop recording",
or a shortcut you set on the page (off by default).
Files: `Documents\Rigbay\logs\rigbay-log-<date>_<time>.csv` and `.meta.json`. Nothing is ever deleted or overwritten.

## Building

```
dotnet build Rigbay.Recorder.slnx -c Release     # 0 warnings expected
dotnet test  Rigbay.Recorder.slnx -c Release     # 146 tests, about 2 s
dotnet run --project ../../src/Rigbay.LangCheck -- src/Rigbay.Recorder/lang --module recorder --source src/Rigbay.Recorder --strict
```

Copy `src/Rigbay.Recorder/bin/Release/net8.0/` (module.json, the two icon files, `lang/`, `Rigbay.Recorder.dll`) to `<modules root>\recorder\`.

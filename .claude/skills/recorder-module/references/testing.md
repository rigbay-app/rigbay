# Recorder module: testing and live checks

## Unit tests

```
dotnet test modules/recorder/tests/Rigbay.Recorder.Tests            # 146, about 2 s
dotnet test Rigbay.sln                                              # the whole repository
dotnet run --project src/Rigbay.LangCheck -- modules/recorder/src/Rigbay.Recorder/lang --module recorder --source modules/recorder/src/Rigbay.Recorder --strict
```

- `dotnet build Rigbay.sln --no-incremental` must be 0 warnings (the module has `TreatWarningsAsErrors`; the tests do not block on tasks inside test methods: xUnit1031, use the `ModuleRig.Press`, `ModuleRig.Invoke` and `FakeCommands.RunSync` helpers).
- Time is a `FakeClock`: `Delay` advances the clock by the wait plus `Jitter` and returns at once. `OnDelay(n)` runs at the start of wait n, before the clock moves: change the bus there to script what happens between two rows.
  `BlockAfter = n` makes wait n+1 block until cancelled, so a test can look at a recording that is frozen mid-way (`WaitUntilBlocked`). Without a time limit or `BlockAfter` a recording never ends.
- To wait for "the recording is over" in a module test use `ModuleRig.WaitUntilFinished()`; `IsRecording` is false slightly before the toast and the page refresh happen.
- A recording is read while open with `Files.ReadBytes` (`FileShare.ReadWrite`). After the end, open the csv with `FileShare.None` to prove it is closed.
- Real services are used in exactly one test (`The_real_services_work_on_this_machine`, 1.2 s) and in the live script.

## Live check with the real app (reads only)

`modules/recorder/tools/live-record.ps1` (needs `tools/build.ps1 -SkipDemo` once; it builds the two modules and lays them out in `tools/out/modules-live` with `-Rebuild`):

```
.\modules\recorder\tools\live-record.ps1 record -Name record-20s -Seconds 18 -Rebuild
.\modules\recorder\tools\live-record.ps1 shot recorder-recording-dark -Theme dark -Recording
.\modules\recorder\tools\live-record.ps1 shot recorder-idle-light -Theme light
.\modules\recorder\tools\live-record.ps1 shot ps-page-recording-dark -Page predatorsense -Recording
```

- `record` starts the app elevated (own data folder `tools/out/data-live`, the real `%LOCALAPPDATA%\Rigbay` is not touched; the app is stopped at the end), enables PredatorSense and the recorder, starts a recording through the forwarded verb `recorder start`,
  asks `predatorsense status` in the middle, stops, and copies the CSV, meta and verb output to `docs/recorder/live/`. The recording goes to `Documents\Rigbay\logs` (the default folder), so it also shows that path works.
- `shot` renders the window itself with `--shot` (PrintWindow of the app's own HWND). With `-Recording` it seeds the folder in the module's `settings.json` (per-shot folder under `tools/out/live-logs`) and starts the recording by the script step `cmd:recorder start`.
  The window is clamped to about 1620 px high. Look at every PNG; one came out 160 x 28 once: run it again.
- Only PredatorSense **reads** are involved. Do not enable any write in another module for a recorder check. XTU only with its write latch on (`modules/xtu/tools/live-read.ps1`).
- A recorder started by a headless verb ends with that short-lived process: drive it against a running app.

## Writing files in this folder: traps

- A bash heredoc in this environment collapses `\\` to `\`. Files with backslashes (`module.json` registry strings, the language files, regexes in tests) must be written with the editor tools, not `cat <<EOF`. The live run once found a `module.json` that was not valid JSON because of this.
- In a `.lang` file a lone backslash is dropped by the host: write `\\`. `LanguageFileTests` fails on a lone one.
- No Python on this machine; `sed -i` with a backslash in the replacement eats it (`\R` becomes `R`, `\l` lowercases): use the Edit tool for those lines.
- Do not leave test recordings in `Documents\Rigbay\logs` that look like the user's: the live script's `record` mode writes one on purpose; the shot runs use their own folder.

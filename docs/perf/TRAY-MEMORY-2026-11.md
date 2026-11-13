# Memory while Rigbay sits in the tray (task M2, 2026-11-13)

The owner measured the installed 0.1.2 with four modules on (xtu, predatorsense, steam-bypass, tunnel) and the window open: working set 256 MB,
private bytes 169 MB, 59 threads, 1519 handles, CPU 1.6 % of one core. Goal of M2: when the window has been in the tray for a while, hand memory back to
Windows, and bring the window back fast. The target (to measure, not to promise) was about 100 to 150 MB working set while hidden.

**Result.** One step pays: trimming the working set once per hide takes the process from about 205 MB to 30 to 45 MB while it is in the tray. Private bytes
(about 150 MB), threads and handles do not change. Dropping the page, clearing the UI caches and collecting the heap did not move the working set or the
private bytes by more than the run-to-run noise, and dropping the page made the next show slower. The app therefore does only the working set trim
(`TrimSteps.Default`); the other steps stay behind `--trim-steps` so the comparison can be repeated.

## How it was measured

`tools\perf\measure.ps1` starts the app elevated (`tools\run-elevated.ps1`) on its own scratch `--data` folder and a built-in module set, with `--allow-multi`,
so the owner's running Rigbay is never touched. Per run: modules on, window open on a module page (25 s), 60 s more, hide to the tray (script step `hide`), marks at
50 s, 2 min and 3 min hidden, show again, first frame, 20 s more. A mark (`mem:<label>`) is one log line with the working set and private bytes of the process,
`GC.GetTotalMemory`, `GC.GetGCMemoryInfo`, threads, handles and CPU time. CPU % is CPU time over wall time between two marks, in percent of one core (open: 60 s after the
page was up; hidden: between the 2 and 3 minute marks). The trim runs after `--trim-delay` (here 60 s, the setting's default). Each step set is one run of the app,
one run per cell (no repeats: the differences below that matter are far larger than the noise of one run, the ones that do not are called noise).

Module sets (test fakes only, no hardware read or write, no service, no Windows setting, no network):

- **released**: the four released modules on the fake machines of their own tests (`tools\demo-modules\released\build.ps1`) plus the Remote tunnel demo
  (`modules\tunnel\tools\demo\build-demo.ps1`), all four on, page `xtu` open.
- **demo**: `demo-fan`, `demo-devices`, `demo-lighting`, page `demo-fan` open.

This machine: Windows 11 10.0.26300, 100 % DPI, framework-dependent build, `--allow-multi` next to the owner's Rigbay. The fakes are not the real modules: the numbers
describe the host, not the modules' own memory (the real PredatorSense and tunnel modules hold more native handles).

## Before and after (set released; megabytes unless stated)

"Before" is the app without the change (`--trim-steps none`: the timer runs and does nothing).

| | open | hidden 2 min, before | hidden 2 min, after (working set trim) | reopen: first frame after show |
|---|---|---|---|---|
| working set | 201 to 204 | 203 | **31 to 45** (29 to 40 across the three runs with that step) | |
| private bytes | 141 to 145 | 142 | 146 to 150 (no change) | |
| managed heap (live objects) | 4 | 4 | 2.4 to 4 | |
| threads | 65 | 44 | 43 | |
| handles | 1280 | 1190 | 1180 | |
| CPU, % of one core | 2.5 to 3 (open, demo fakes polling) | 0.4 | 0.3 to 0.5 (noise) | |
| time to first frame after show, ms | | 19 | 24 to 29 | |
| working set 3 s after show | | 204 | 69 to 76 | |

Threads and handles fall from open to hidden in the same way with and without the change (seen in every run, also `none`); the change adds nothing to them. The owner's 256 MB
figure was taken on the installed app with the real modules, and 201 to 204 MB here is lower; it was not investigated why (the fakes, the run length and the build are all different).
The relative result (about 85 % of the working set given back) is what these runs show; the absolute numbers on the owner's machine were not measured.

## Every variant (set released, one run each)

| steps run at the trim | hidden 2 min WS | hidden 2 min private | after trim (log line) WS | first frame after show, ms | page rebuild, ms | WS 3 s after show |
|---|---|---|---|---|---|---|
| none (before) | 202.8 | 141.9 | 202.9 to 202.9 | 19 | - | 203.7 |
| drop (page and statics dropped) | 207.9 | 148.2 | 208.1 to 208.1 | 109 | 83 | 215.4 |
| drop + caches | 210.7 | 152.4 | 211.0 to 211.1 | 116 | 96 | 216.1 |
| drop + caches + gc | 205.4 | 143.7 | 204.9 to 204.8 | 132 | 109 | 211.6 |
| drop + caches + gc + ws (all) | 31.0 | 150.4 | 207.3 to 3.2 | 136 | 102 | 103.7 |
| ws | 38.8 | 146.1 | 210.6 to 1.6 | 24 | - | 68.7 |
| gc + ws | 39.8 | 145.2 | 206.3 to 1.8 | 29 | - | 69.8 |
| drop + ws | 29.2 | 151.1 | 209.4 to 3.2 | 134 | 103 | 114.1 |

Set demo (demo-fan page, one run each): before 206 MB hidden, all steps 35 MB; first frame after show 23 ms before, 174 ms with all steps (page rebuild 135 ms).

## What did not help

- **Dropping the page** (navigate to an empty host, dispose the page scope and the tree, forget the page statics). It works as designed: a `WeakReference` to the page
  root and to the app's own elements of the page (14 to 16 of them) is dead after one collection, so WinUI keeps no tree alive (the self-test checks this,
  "WinUI keeps no reference to the page root or the chart after the trim"). It just does not save memory: the managed heap holds 4 MB in all, and the private
  bytes did not move by more than the noise (about 5 MB) when the page was dropped, so the page's native tree is not a large part of the 150 MB that are private. It costs time: the page is rebuilt on show (80 to 135 ms) and the first frame comes after about 110 to 175 ms instead
  of 20 to 30 ms. After the show the working set is higher with the drop (104 to 114 MB against 69 to 76 MB), because building the page touches many pages that the trim had taken out.
- **Clearing the UI caches** (search results, the statics that point at the last built page): inside the noise.
- **One `GC.Collect` with large-object-heap compaction**: the managed heap goes from about 4 MB to 2.5 MB, the private bytes and the working set do not change visibly. With the working
  set trim after it, it is the same as the trim alone (39.8 against 38.8 MB). It costs 50 ms.
- **The working set alone does not lower private bytes.** Private bytes are committed memory (the .NET runtime, WinUI, DirectX and the Windows App SDK native
  code and heaps, about 150 MB); the trim moves pages to the standby list or the page file, they come back on first touch. Windows can use the RAM for other
  programs, but the commit charge does not go down. That is why the working set shows 30 to 45 MB and private bytes still 150 MB.
- **The target.** About 100 to 150 MB was a target to measure; the working set trim does better (30 to 45 MB) because it takes everything out, not only what the UI needs.
  It comes back as the app is used: 69 to 76 MB three seconds after a show and still 75 MB 20 s later in the runs with only the working set trim.

## What the app does now

- Setting **Release memory in the tray** (Settings > General, on by default; `ReleaseMemoryInTray` in `app.json`) with a **delay** (30 s, 1, 2, 5 or 10 minutes; default 60 s; `ReleaseMemoryDelaySeconds`).
- `TrayTrimPolicy` (`src\Rigbay.App\Services\TrayTrimPolicy.cs`, no WinUI in it, unit-tested with a fake clock in `TrayTrimPolicyTests`): the delay counts from the hide;
  showing the window first cancels it; it fires once per hide; the setting off means never. The window arms one dispatcher timer for the time left.
- When the timer fires and the window is still hidden, `EmptyWorkingSet` (`K32EmptyWorkingSet`) on the process, once. A window started with `--tray` (the logon start) is treated as hidden from the start, so the start-up
  working set is given back too. Nothing else changes: the page stays on the window, the modules keep running, the metrics history stays.
- A line `TRAYTRIM steps=WorkingSet ... ws A->B MB private C->D MB ...` in the log says what each trim did.
- The code for the other steps (`DropPages`, `Caches`, `Collect`, the restore of scroll position, open expanders and focus, `MainWindow.Trim.cs`) stays, off by default, behind `--trim-steps`:
  it is what the table above was measured with, and the self-test runs it with all steps (page gone, `WeakReference`s dead, page back at the same scroll offset, subscriptions back).
  Turn on a step again only with a measurement that shows it pays.

## Checks run

- `TrayTrimPolicyTests` (fake clock, 21 tests with the steps-switch tests): delay counted from the hide, cancelled when the window reopens first, once per hide, a second hide call does not restart the wait, the setting off, a longer delay chosen while hidden, limits of the delay. Whole `Rigbay.Core.Tests` 823 passed, `Rigbay.LangCheck.Tests` 19 passed, `Rigbay.App.exe --langcheck` exit 0.
- In-app self-test (elevated, `--allow-multi`, scratch data): 176 passed, 0 failed, 2 skipped (the pipe and the single-instance checks, as always under `--allow-multi`). Seven new checks, "tray trim: ...": the default trim gives the working set back once and keeps the page (ws 244 to 2 MB, private unchanged); a window shown before the delay is not trimmed; with every step the page is dropped after the delay, once; a `WeakReference` to the page root and to the chart is dead after the trim (16 of the app's own elements seen, 0 alive); nothing is live in the tray (no scope, no subscription); a page refresh while hidden builds nothing; showing the window rebuilds the same page at the same scroll offset (300 to 300) with the 12 subscriptions back; with the setting off nothing is trimmed.
  Finding while testing the opt-in drop: right after the rebuild the page was taller (2153 against 1867 px; the readings fill in) and the scroll landed at 281, so the restore scrolls once more after 500 ms.

## Not verified

- The real modules (the numbers use the test fakes of the released modules and the demo modules).
- A real tray on a real desktop session over hours; one run per cell; no run on the owner's installed copy (never touched).
- Whether Windows keeps the standby pages long enough on a machine under memory pressure for the "reopen" numbers to hold (here there was plenty of free RAM).
- The focus part of the restore (opt-in drop mode): the code gives focus back as the page refresh does, but the self-test had no focused control at the hide, so only scroll offset, open expanders and subscriptions were checked.
- The Settings row was looked at in a dark-theme render of the window (`--shot-mode rtb`, English); the Vietnamese strings were not rendered.
- Private bytes: nothing was done about them; the 150 MB are native and out of reach of the app's own code.

To repeat: `tools\build.ps1` (or the `dotnet publish` line in it), `tools\demo-modules\released\build.ps1 -OutDir tools\out\modules-released`,
`modules\tunnel\tools\demo\build-demo.ps1 -OutDir tools\out\modules-released`, then
`powershell -ExecutionPolicy Bypass -File tools\perf\measure.ps1 -Set released -Variants none,ws,all`. The report is written to `tools\out\perf\`. About 5 minutes per variant.

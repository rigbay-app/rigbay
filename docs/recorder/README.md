# Recorder module

Rigbay module `recorder` (group `monitoring`, task M2): writes whatever the other modules publish on the metrics bus to a CSV file, with a
`.meta.json` next to it that says what each column is. It replaces the CSV recording of the old standalone XTU app, and works for any module:
the recorder knows no other module by name, so PredatorSense, XTU or a module nobody has written yet is recorded the same way.

- Code: `modules/recorder/` (own `Directory.Build.props`, `Rigbay.Recorder.slnx`; the only reference outside the folder is `Rigbay.Abstractions`).
  Tests: `modules/recorder/tests/Rigbay.Recorder.Tests` (146, no hardware, no other module). Live check: `modules/recorder/tools/live-record.ps1`.
- Working knowledge for people changing it: `.claude/skills/recorder-module/`. What the old XTU recorder wrote and how the columns map to bus ids: `docs/xtu/RECORDER-NOTES.md`.
- Evidence of the live run (CSV, meta, verb output, window PNGs): `docs/recorder/live/`.

## What the user sees

**Page** (`Monitoring > Recorder`; the host draws the enable row first):

| Group | Rows |
| --- | --- |
| Recording | Start, Stop (one is enabled at a time), then the status: State, Rows written, File size, Elapsed (`00:02:05`, or `00:02:05 of 00:30:00` with a limit), File, Last message (why the last recording ended, or why one would not start). Then the options: Time between rows (1, 2, 5, 10 s), Stop after (no limit, 1, 5, 10, 15, 30 min, 1, 2, 4 h), Folder (empty is `Documents\Rigbay\logs`; Apply and Discard because a path is typed one character at a time), Open folder, and the shortcut. |
| Metrics to record | "n of m" selected, All and None, then one expander per source module with one switch per metric (the label the owner gave it, `id · unit` below). Everything is on by default; a metric that appears later is on too (the settings keep what the user switched **off**). The switches are locked while recording. |
| History | The newest 10 `rigbay-log-*.csv` in the folder with date and size, each with "Open". Files are only listed; the recorder never deletes one. |

Starting and stopping is not a hardware change: no approval, no revert. **Dashboard bay:** `Recording 00:12:31` or `Idle`. **Tray:** "Start recording" while idle,
"Stop recording" while recording. **Hotkey:** `toggle`, **off until the user sets one** (the Start/stop shortcut row), starts with the page's settings or stops.
A refusal from the tray or the hotkey (no metrics, disk full) is shown as a notification, because nobody looks at the page then.

**Verbs** (`rigbay recorder ...`; exit 0 ok, 1 failed, 2 bad usage):

```
rigbay recorder start [--every S] [--minutes N] [--metrics a,b]    --every 1..3600 s, --minutes 0 (no limit) to a week, --metrics exact ids
rigbay recorder stop                                               exit 0 also when nothing is recording (a time limit may have ended it)
rigbay recorder status                                             recording to ..., rows, size, elapsed, period, metrics; or the last recording
```

What `start` does not say comes from the page, so a bare `start` equals the Start button. A named metric that is not on the bus is refused with the list of
unknown ids (a typo must not give a file without the column). The recording belongs to the running app: `start` run headless (no app running) is
stopped again when that short-lived host exits, so use it against a running Rigbay.

## The CSV

`Documents\Rigbay\logs\rigbay-log-<yyyy-MM-dd>_<HH-mm-ss>.csv`, plus `<same name>.meta.json`. UTF-8 **without** a byte order mark, `FileShare.Read` (other programs
can read it while it grows, nothing can write to it), lines end with CRLF, one flush per row so a crash loses at most the row being written. A name that is taken (a second
start in the same second, or a file the user put there) gets `-2`, `-3`, ...; an existing file is never opened for writing.

```
timestamp,elapsed_s,cpu.temp,fan.cpu.rpm,fan.gpu1.rpm,...
2026-11-01T22:36:50.442+07:00,0,65,2884,3125,...
2026-11-01T22:36:51.442+07:00,1,65,2884,3125,...
```

- **Header** = metric ids (`cpu.temp`), the same ids on every machine and from every source. Units, labels and sources are in the meta, not in the header (no `°` in a header Excel may misread).
- **Columns** are fixed when the recording starts: the metrics on the bus then that are available, from modules other than the recorder, that the user did not switch off (or that `--metrics` names).
  Order: source module (ordinal), then id (ordinal), so one module's columns stay together and the order is the same every time. **A metric that appears later is not added to the file**; it is listed
  in the meta (`late_metrics`: id, source, unit, first seen). A metric that was withdrawn at the start is not a column; one withdrawn later keeps its column with empty cells.
- **Numbers:** `.` as decimal separator whatever the culture of the machine, at most 3 decimals, no thousands separator, no exponent, no `-0`. **An empty cell** means no value at that moment.
- **`timestamp`:** local time, ISO 8601 with offset and milliseconds. **`elapsed_s`** is seconds since the first row.
- **A row is a snapshot:** the latest value on the bus of each column at the row's time (it does not wait for a new sample, so a module that publishes about once a second repeats its value
  when the period is shorter; PredatorSense's default of 2 s shows in the file as pairs of equal rows). A value counts only if its sample is not older than **30 seconds** at the row's time and
  the metric is not withdrawn; otherwise the cell is empty. That is what makes a module that stopped updating (or an idle module that still has a value from last time) show as empty and not as a flat line.

### Timing

Rows are on a fixed schedule: the first row is written at the start, slot n is at `start + n * period` (`next += period`, never `next = now + period`), so rows are exactly one period apart however late the thread wakes up.
The row is stamped with its **slot**, not with the (slightly later) moment it was taken. If the thread is more than one period late (the laptop slept) the slot is reset to the present: there is a gap, never a burst of catch-up rows. A time limit counts from the first row and ends at `start + limit`: with 1 s and 1 minute that is 60 rows
(`00:00` to `00:59`), the file is closed at `01:00`.

### The meta

```
tool, tool_version, host_version     "Rigbay recorder", the module's version, the host's
csv, started, ended, stop_reason     file name; ISO times (ended is null while recording); user | duration | disk-low | write-error | module-stopped
period_s, limit_minutes, rows, error seconds between rows; the limit or null; rows written; the error text of a write error, else null
machine                              manufacturer, model, cpu, os   (registry: HARDWARE\DESCRIPTION\System\BIOS and CentralProcessor\0, and the .NET OS description)
modules                              version of each source module (read from the sibling folder's module.json; the module is installed next to this one) and of the recorder
columns                              [{ id, label, unit, source, description }] in CSV order; label and description come from the owner module in the language the recording started in
late_metrics                         metrics that appeared after the start (not in the file)
notes                                three plain sentences: what a row is, what an empty cell means, columns are fixed at the start
```

Written at the start, again when a late metric appears, and at the end. It is written to a temporary file and renamed over the old one, so a full disk cannot leave half of it.

## Why it listens only while it records

`Subscribe` is what tells a module that somebody wants its readings (`SubscriberCount`): PredatorSense polls at 2 s with a subscriber and at 10 s without; XTU reads only while somebody subscribes or its page is
shown. A recorder that stayed subscribed would make every sensor module poll fast for ever, so it subscribes (to all samples) in `Start` and drops the subscription when the recording ends. While idle it reads the
bus cache (`GetLatest`) to fill the metric picker, which does not count as a subscriber. Tested with the fake bus (`The_recorder_listens_only_while_it_records`: 0 subscribers idle, 1 while recording, 0 after Stop). In the live run PredatorSense, whose
default is a read every 2 s, delivered a new value every 2 s during the recording (pairs of equal rows); its idle rate (10 s) was not measured.

## Disk safety

- **Start:** less than 200 MB free on the target drive is refused ("Not enough free space on that drive: 20 MB free, at least 200 MB needed"), before any file is created. A drive whose free space cannot be told (a network share) is allowed.
- **While recording:** checked before every row. Under 50 MB free, or any write that fails, ends the recording cleanly: the file is closed, the meta says `disk-low` or `write-error`, a notification says so and the page's Last message keeps it.
- Nothing is ever thrown to the host (every callback and the writing task catch everything), and **no file is ever deleted or overwritten**. The history is a list, capped at 10 entries; older files stay in the folder.

## Differences from the old XTU recorder

The old CSV had fixed XTU columns (`pl1_w`, `pkg_temp_c`, ...), a `status` column and a `profile` column. This one is generic. Mapping of old columns to bus ids: `docs/xtu/RECORDER-NOTES.md`. What changes for a reader of the files:
header names are bus ids; units are the bus units (the XTU module publishes clocks in MHz, the old file had GHz: divide by 1000); `status` is gone (an unavailable source is an empty cell); `profile` and `power_source` are not on the bus
(a text channel would be needed), so they are not recorded; the CPU usage column is the bus's `cpu.usage`, which is a different measurement from the old Windows counter (see the notes).

## Verification (2026-11-01, this machine, Windows 11 10.0.26300)

- `dotnet build Rigbay.sln --no-incremental`: 0 warnings, 0 errors. `dotnet test Rigbay.sln`: 791 passed (Core 353, XTU 190, PredatorSense 102, Recorder 146). The recorder tests ran 12 times in a row without a failure.
  `dotnet run --project src/Rigbay.LangCheck -- modules/recorder/src/Rigbay.Recorder/lang --module recorder --source modules/recorder/src/Rigbay.Recorder --strict`: ok, 106 keys, en and vi complete.
- The tests cover: schedule without drift (7 ms late wake-ups, 5 s period, a 10 s pause), column order, empty cells, a withdrawn metric, a metric present at the start only, a metric that appears later, the 30 s rule, time-limit stop,
  disk-full (start refused at 199 MB, stop under 50 MB, exactly 50 MB allowed, a failing stream), file-name collision (three in one second, a file already there), meta content, no BOM / dot decimals under vi-VN and de-DE,
  cancel safety (stop while waiting, twice, from 8 threads, 40 start/stop pairs, throwing handlers), the verbs and their bad arguments, tray, hotkey, shutdown during a recording, language switch, the manifest, the icons, the language files.
- **Live** (`modules/recorder/tools/live-record.ps1`, the real app elevated with its own data folder, the real PredatorSense module reading its sensors, nothing written to the machine, XTU not loaded):
  a recording of 20 rows through the running app's verbs (`record-20s.csv`): every gap exactly 1.000 s, `elapsed_s` 0 to 19, 6 columns from PredatorSense with their labels, no empty cell, no BOM in the CSV or the meta.
  The row at 22:37:01.442 equals what `rigbay predatorsense status` printed at 22:37:01.416 (65 / 46 / 54 °C, 2884 / 3125 / 3004 rpm). In a second run the PredatorSense page, rendered at 22:29:52.131, showed the same numbers as the row at 22:29:52.414
  (76 / 57 / 45 °C, 3896 / 4347 / 4340 rpm), and the recording that was still running when the app quit was closed properly (`stop_reason: module-stopped`).
- PNGs of the window itself (PrintWindow of the app's own HWND, never the screen), each looked at: `recorder-recording-dark/light`, `recorder-idle-dark/light` (with the History group), `recorder-vi-dark`, `dashboard-recording-dark`, `dashboard-idle-light`, `ps-page-recording-dark`.
- The live run found two defects the tests had not: `module.json` was not valid JSON (the host listed the module as Invalid) and a backslash in a language file is dropped by the host (`\L` becomes `L`). Both now have tests.

### Not verified

XTU as a source (no write latch run, by the task); a recording longer than a few minutes or more than 2000 rows; the disk-full path on a real full disk (a fake free-space provider and a failing stream only); a click on a button, a toggle or the Apply of the folder row by a real mouse (the same code
was driven by the app's script and by the verbs); Open and Open folder opening Explorer or Excel (the shell seam is faked in the tests; the live run did not press them); the tray items and the hotkey in the real app (tested through the host contract); a real toast; 150 % and 200 % DPI, high contrast, screen readers;
the picker with XTU's ~30 metrics (the page was seen with 6); the label and description from a module that gives a description (PredatorSense gives none, so the file shows the label twice); an installed module under `%ProgramData%\Rigbay\modules` (the live run used a folder next to the app);
the recording surviving a host crash (flushed per row, not killed on purpose); Windows 10.

### Findings for the coordinator

1. A **single backslash in a language file is dropped** by the host (`LanguageFile.Unescape`: `\X` is `X`; write `\\`). PredatorSense's `probe.no-wmi` has `root\WMI` in `en.lang` and `vi.lang`, which shows as `rootWMI`. The recorder's test reads language files with the host's rule and fails on a lone backslash.
2. The host's **`TextRow` gives the label little room when the text is a long path** (the header wrapped to "Folde / r" with a 90-character path, and a description beside the box was squeezed to a sliver). The recorder dropped the folder row's description and moved it to the "Open folder" row; a layout with the box on its own line would fix it for every module.
3. The app's script step **`edit:<row>=<value>` on an apply-or-revert row only sets the draft**; there is no step that presses Apply. The live script writes the module's `settings.json` instead.
4. `rigbay <module> start` run **headless** loads the module, runs the verb and stops it, so a recording started that way ends with the process. Fine for `status` and `stop`; documented here and in the verb help.
5. `Subscribe` to all samples is what wakes polling modules, and there is **no way to subscribe "weakly"**. The recorder subscribes only while recording; a dashboard-like consumer would have the same cost.

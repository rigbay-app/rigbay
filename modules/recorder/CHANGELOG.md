# Changelog

## 0.1.3 (Unreleased)

Needs Rigbay 0.1.5 or newer (host API 0.1.4, `hostApi` `>=0.1.4 <0.2`). The recording, the CSV, the `.meta.json` and `requires` do not change.

- **Verb metadata** (`CommandInfo`), so Game scenes can list the verbs and the approval dialog can say what a step does. `status` is read-only. `start` and `stop` are declared as writes
  of the setting kind: they write files (a CSV and a `.meta.json` in the recordings folder, until stopped or the time limit), not hardware and not a Windows setting. Read-only was
  rejected because a read-only verb may be called by any module without the owner's approval, which would let a module start filling a disk or end the owner's recording; a
  hardware kind was rejected because nothing on the machine is configured. The effect text (`effect.start`, `effect.stop`) says what really happens.
- `start` is previewed from its arguments and the page's settings only ("Record the metrics selected on the page every 1 s until stopped, to <folder>", or the named metrics, or with
  a time limit); the bus is not looked at, so the text is the same whether or not metrics exist or a recording runs. A change of the period, the limit or the folder on the page changes the
  preview, so the host asks again. `stop` is previewed as "Stop the recording and close the file".
- `start` has `UndoVerb = "stop"` and reports an empty `UndoArgs` when it began a recording (not when it was refused); `stop` has no undo (starting again is not putting back).
- **No poll rate to decide:** the recorder publishes no metric, it reads the bus through its own subscription (which, as before, holds the modules it records at their fast rate while it records).

## 0.1.2

Fixes for timing problems found when two tests failed on a slow CI machine. The recording, the CSV, the `.meta.json` and the manifest (host API `>=0.1.3 <0.2`, same `requires`) do not change.

- **The writing loop no longer needs the thread pool.** It runs on a thread of its own and waits for the next slot there, so Stop closes the file at once even when the pool is busy (before, Stop could give up after 4 s and report no summary).
- **Two races on the page are fixed.** The texts of the page are refreshed one call at a time (the clock timer, the engine and the end handler all refresh them, and an older result could overwrite a newer one, so the line under the Stop button stayed empty instead of saying why the recording stopped). A start that ends at once (a write error, no free space) no longer loses its message: the last notice is cleared before the start, not after it.

## 0.1.1

The page from the November 2026 UI review (`docs/ui-review/REVIEW-2026-11.md` in the Rigbay repository, findings RC-1, DB-5, LC-2). The recording
itself, the CSV and the `.meta.json` do not change. The manifest now asks for host API `>=0.1.3 <0.2` (`PageModel.Primary`, `Row.Line`,
`Row.HelpKey`, `ExpanderRow.HeaderRow`).

- **One big button whose label is its state**: "Start recording" while idle, "Stop recording" while a recording runs, drawn large under the page
  header. Its line says the file, the time (and the limit), the rows and the size while recording, and how the last recording ended (or why one
  could not start) afterwards. The module asks the host for a new page when a recording starts or ends;
  the option rows are kept (their Apply and Revert survive), Start and Stop are new each time (no stale "Recording to ..." beside Start). A
  refusal from the button is said beside it, once.
- **The six status rows are gone** (State, Rows written, File size, Elapsed, File, Last message): while idle they all read "–".
- **The folder is one row**: where the files go in its line, "Open" in its header, the path to change inside.
- **Metrics to record**: "Selected metrics 12 of 40" carries "Select all", then "Clear"; each source module says how many of its metrics are picked
  while it is closed.
- The dashboard says "Not recording" instead of "Idle" (DB-5). Shorter words: explanations moved to the info glyph (`HelpKey`).

## 0.1.0 (pre-release)

First version: records every metric on the bus to a CSV file with a `.meta.json` beside it, on a fixed schedule, with disk safety; page, tray items,
an optional shortcut, and `rigbay recorder start | stop | status`. See `docs/recorder/README.md` in the Rigbay repository.

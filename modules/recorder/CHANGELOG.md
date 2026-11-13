# Changelog

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

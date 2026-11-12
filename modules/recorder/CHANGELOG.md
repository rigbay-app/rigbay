# Changelog

## 0.1.1 (Unreleased)

The page from the November 2026 UI review (`docs/ui-review/REVIEW-2026-11.md` in the Rigbay repository, findings RC-1, DB-5, LC-2). The recording
itself, the CSV and the `.meta.json` do not change. The manifest now asks for host API `>=0.1.3 <0.2` (`PageModel.Primary`, `Row.Line`,
`Row.HelpKey`, `ExpanderRow.HeaderRow`).

- **One big button whose label is its state**: "Start recording" while idle, "Stop recording" while a recording runs, drawn large under the page
  header. Its line says the file, the time (and the limit), the rows and the size while recording, and how the last recording ended (or why one
  could not start) afterwards. The module asks the host for a new page when a recording starts or ends and keeps its rows, so nothing piles up.
- **The six status rows are gone** (State, Rows written, File size, Elapsed, File, Last message): while idle they all read "–".
- **The folder is one row**: where the files go in its line, "Open" in its header, the path to change inside.
- **Metrics to record**: "Selected metrics 12 of 40" carries "Select all", then "Clear"; each source module says how many of its metrics are picked
  while it is closed.
- The dashboard says "Not recording" instead of "Idle" (DB-5). Shorter words: explanations moved to the info glyph (`HelpKey`).

## 0.1.0 (pre-release)

First version: records every metric on the bus to a CSV file with a `.meta.json` beside it, on a fixed schedule, with disk safety; page, tray items,
an optional shortcut, and `rigbay recorder start | stop | status`. See `docs/recorder/README.md` in the Rigbay repository.

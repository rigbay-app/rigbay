---
name: watch-module
description: Working knowledge of the Rigbay Watch module (modules/watch, id watch, group monitoring, .NET 8, in process, READ ONLY; rules over any metric on the bus - at or above / at or below / rises by / has bits set, for N seconds, hysteresis, quiet period, "only while another reading", one Warning notification per episode; asks for readings with SubscribeBackground, never Subscribe; a reading older than twice its interval is "no data"; six suggestions; pause (page toggle, tray item, rigbay watch status|pause|resume)). Use it before reading or changing anything under modules/watch, when adding a rule kind, a suggestion, a language key or a test, when touching the engine's timing, the subscriptions or the pause, and before any live check. Saves re-deriving the layers, the rules of the engine and the traps below.
---

# Watch module: codebase guide

Module id `watch`, group `monitoring`, `modules/watch/src/Rigbay.Watch` (net8.0), tests in `modules/watch/tests/Rigbay.Watch.Tests`, version 0.1.0 unreleased, host API
`>=0.1.4 <0.2` (background demand and verb metadata are revision 4), `requires.admin` false. Written by task WA of job rigbay-15 (2026-11-13). This repository is PUBLIC.

## Hard constraints (check before you write code)

- **Read only.** The module reads the metrics bus and shows notifications. It never calls `Publish`, `Withdraw`, `Describe`, `Approval`, `Verbs`, `Hotkeys`, never starts a program,
  opens a file, touches the registry, WMI, a service or the network. `HygieneTests` scans the source (comment lines skipped) for each of these words and fails on one. Its verbs
  (`status`, `pause`, `resume`) are declared `VerbAccess.ReadOnly` on purpose: pause and resume change only Watch's own state (whether it sends notifications), nothing on the machine,
  so another module may call them without an approved plan. Do not add a verb that writes to the machine; that needs an owner gate (`docs/plan/WORKER-RULES.md` rule 1).
- **`SubscribeBackground`, never `Subscribe`.** A `Subscribe` counts as a reader, so every publisher would poll at its fast rate all day (the reason H1 exists). A test and the hygiene scan
  fail on `.Subscribe(`. Interval per rule (5 s to 1 h, default 10 s); one subscription per metric at the shortest interval of the enabled rules that use it (the condition metric counts).
  At most 64 per module in the host: 24 rules x 2 metrics = 48 (a test pins it).
- **A reading older than twice the interval is "no data".** Old publishers (built before 0.1.4) ignore a background demand and may not publish while nobody looks (XTU's monitor,
  PredatorSense's operation mode); the GPU module (G1) still polls only for a reader. Their rules show "Waiting for <metric>". Never treat an old value as the value.
- **Rules of the contract** (`docs/ABSTRACTIONS.md` section 12): constructor sets nothing, `Probe` is always yes (the bus is the same everywhere), `Initialize` registers, the check
  timer is one-shot and re-armed after the check, every thread catches everything, `Shutdown` disposes the subscriptions and the timers and unhooks every handler (the host removes verbs and
  tray items itself; the test fake does not, so a test that turns the module on twice clears them).
- **Nothing about the owner's machine in the repository.** The suggestions use public metric ids only.

## Layout

| File | What |
| --- | --- |
| `WatchSettings.cs` | `WatchRule` (plain data, JSON), `RuleKind`, limits (24 rules, 5 s to 1 h, quiet up to 1440 min), `Normalised()` (repair or drop), `WatchSettings.Clamped()` |
| `RuleEngine.cs` | the whole logic, no bus, no timer: `SetRules`, `Wanted`, `CheckEvery`, `Record(sample)`, `Evaluate(paused)` returns the alerts, `View(ruleId)` |
| `RuleText.cs` | numbers, durations, the one-line summary of a rule, the state line (pure, takes `ILocalisation`) |
| `Suggestions.cs` | the six built-in suggestions, offered only when the catalogue has the metric |
| `WatchModule.cs` | lifecycle, rules (add / update / remove -> `Commit`), subscriptions, the check timer, alerts, pause, catalogue / page-visible hooks |
| `WatchModule.Page.cs` | the page: picker, one expander per rule, add form, suggestions, settings |
| `WatchModule.Texts.cs` | `RefreshTexts`: live line per rule, state line, pause line |
| `WatchModule.Commands.cs` | `rigbay watch status|pause <minutes>|resume` |
| `tools/demo`, `tools/live-check.ps1` | a demo publisher of two made-up readings and the live check in an own instance |

## The engine in one page (`RuleEngine.Step`)

- **No data** (reading missing, withdrawn, or older than 2 x interval; also the condition reading): `NoData = true`, pending time and the rise history dropped, an open episode **kept** (it can
  neither fire again nor clear). State `Waiting`, `WaitingFor` = the metric.
- **Entry** (the reading is in the alert zone) and **recovered** per kind: above `v >= T` / recovered `v <= ClearAt` (no `ClearAt`: `v < T`); below mirrored; bits `(long)v & (long)T != 0`
  (recovered = none set); rises: the rise = value now minus the lowest value in the last `Seconds` seconds (kept at each check), entry `rise >= T`, recovered `rise <= ClearAt ?? 0`.
  A condition that does not hold makes `entry` false and `recovered` true.
- **Episode:** not firing + entry -> `PendingSince` (set once); firing when `now - PendingSince >= Seconds` (a rise fires at once). Firing + recovered -> ends. Time is the check clock, so the duration
  is exact to within one check interval.
- **One alert per episode:** `Firing && !Notified && !paused && now >= QuietUntil && fresh data` -> alert, `Notified`, `LastAlert`, `QuietUntil = now + QuietMinutes`. So an episode that began during a pause or
  a quiet period is announced when that ends if it is still open, and never if it ended first. Editing a rule's meaning (`Signature`) starts its state over but keeps `LastAlert` / `QuietUntil`.
- The page edits go through `WatchModule.UpdateRule` -> `Normalised()` (a clearing limit on the wrong side, a window under a second, ... are repaired) -> `Commit()` (engine, save, resubscribe, re-arm, check now).
  The row echo is guarded in `RuleRow.Edit` so the repaired value does not look like another edit.

## Decisions

- **Page.** `Primary` = "Send alerts" toggle (off = pause for 1 hour, with a live "Alerts paused until ..." line). One `ExpanderRow` per rule: header = the reading's label, `HeaderRow` = its switch,
  `Line` = the live line ("At or above 95 °C for 30 s · Quiet · now 61 °C"), children = reading, kind, limit, clear at, for, quiet period, check every, only while (switch + reading + kind + limit), remove (`ConfirmKey`).
  The picker is a `ChoiceRow` of "module · label (value unit)", values as of the last page build; the page is rebuilt when it opens (`PageVisible`) and 3 s after a catalogue change while it is open, and after
  structure edits (reading changed, rule added or removed), with the rule being edited kept open (`_expanded`). A number edit changes the live line only, not the page.
- **Suggestions** are not rules until clicked (`Suggestion` key stored in the rule so the page stops offering it): CPU temperature 95 °C 30 s (clear 90), GPU `gpu.nvml.temp` 87 °C 30 s (clear 82), one per
  `tunnel.*.handshake_age` above 180 s for 15 s (the tunnel module withdraws it while the tunnel is off, so "while the tunnel is on" is the no-data rule; a tunnel with no traffic and no keep-alive also gets old),
  `steam.proxy.errors` rises by 5 within 60 s, `gpu.throttle` has bits 0x60 (software and hardware thermal) for 30 s, `fan.cpu.rpm` at or below 0 for 20 s only while `cpu.temp` is at or above 80.
- **Tray item** "Pause alerts for 1 hour": registered always, `Visible` follows a setting that is off by default (the owner asks for it); `Checked` = paused. Pause is in memory only: it ends when Rigbay restarts.
- **Notification:** `ToastKind.Warning`, `ToastOrigin.Background`, title = the reading's label (literal), body = "<rule sentence>. Now <value>." The host decides the place; elevated, a Warning is a tray balloon
  (`docs/APP.md` "Notifications"). The log has `alert: ...` from the module, `notice [...]` and `tray balloon: ...` from the host.
- **Mask limits** are shown in hexadecimal (`0x60`), everything else with the unit symbol from the catalogue.

## Traps

- `FakeCommands`/`FakeTray` in the tests do not remove what a module registered when it stops; the real host does. A test that starts a module twice must clear them.
- Every `Rig.Row<T>()` call builds a **new** page: two rows from two calls do not share observables. Take one `Page()` when a picker and its button belong together.
- `ObservableValue` raises `Changed` on the setter's thread, also when a handler sets the value again (the echo guard in `RuleRow.Edit` is what keeps that from looping).
- The page must never have a `ChoiceRow` with no options (the host's validator refuses the page and the module faults): the add form is left out when the catalogue is empty and a rule's own reading is always in its picker.
- `LangCheck --strict` sees only string literals: keep keys as literals (`"info.suggest-done"` in a ternary is fine), not built by concatenation.
- The module's own settings file is plain JSON under the module's data folder (`settings.json`, camelCase, enums as strings); a corrupt file loads as no rules (the host renames it).

---
name: scenes-module
description: Working knowledge of the Rigbay Game scenes module (modules/scenes, id scenes, group power, .NET 8, in process; when a chosen program runs it runs OTHER modules' verbs under a host-approved VerbPlan and puts them back when the program ends; poll of the process list every 5 s only while a scene is on and approved, 10 s grace, last started ends first, adoption of runs after a Rigbay restart; verbs rigbay scenes list|status, read only). Use it before reading or changing anything under modules/scenes, when touching the approval flow, the fingerprint, the poll, the end order or the restart adoption, when adding a language key, a page row or a test, and before any live check. Saves re-deriving the layers, the safety rules and the traps below.
---

# Game scenes module: codebase guide

Module id `scenes`, group `power`, `modules/scenes/src/Rigbay.Scenes` (net8.0), tests in `modules/scenes/tests/Rigbay.Scenes.Tests` (135 at the last run), version 0.1.0 unreleased,
host API `>=0.1.4 <0.2` (it needs `IHostServices.Verbs`, `VerbPlan`, `CommandInfo`), `requires.admin` false. Written by task SC of job rigbay-15 (2026-11-13). This repository is PUBLIC.
Read first: `docs/ABSTRACTIONS.md` 9.1 and `docs/plan/H1-DESIGN.md` (the host side is the contract this module builds on; the code is `src/Rigbay.Core/Verbs/PlanEngine.cs`).

## Hard constraints (check before you write code)

- **The module decides WHEN, the host decides WHETHER.** Every start goes through `Verbs.StartAsync(ScenePlans.Build(scene))` and every undo through `Verbs.EndAsync(runId)`; the host runs a plan only when its
  hash equals the one the owner approved in the host's one dialog (`RequestApprovalAsync`). Never add a second way to run another module's verb: no `Verbs.InvokeAsync` (a hygiene test fails on it), no
  `Commands` call, no `HardwareEffect` row (the plan dialog is the only question; a row-level one would ask twice). `StartAsync` is called in one place (`Runtime.StartSceneAsync`), `EndAsync` in two.
- **Never run a real module's write verb in a test or a live check** (XTU, Steam bypass, tunnel, PredatorSense). The live check uses `demo-plan-target` (in-memory) and a copy of `ping.exe` named `scenesgame.exe`.
  The owner's first real scene gets its own gate (`csm gate`), see `docs/plan/WORKER-RULES.md` rule 1.
- **Editing a scene asks again, in two layers.** The host's hash covers the steps, arguments, undo and the target module versions. The programs are NOT in the host's plan, so the module keeps
  `SceneSettings.ApprovedFingerprint` (`ScenePlans.Fingerprint`: id, programs lower-cased, every step with its undo; NOT the name) and a scene is armed only when the host says `Approved` **and** the
  fingerprint equals the stored one (`IsApproved`). A hand edit of `settings.json` therefore never arms anything. The fingerprint is stored when the owner's yes comes back, from the scene as it was when
  the question was built, so an edit made while the dialog is open leaves the scene needing approval (a test pins this).
- **The poll exists only while needed** (`NeedsPolling`: a scene that is `Enabled` and approved, or a scene with an open run). `UpdatePolling` runs after every change (`AfterChange`). It reads by name
  (`Process.GetProcessesByName`) for the programs of the wanted scenes; the full process list (`GetProcesses`, windows only) is read only for the picker, on page open or on *Refresh list*. Only
  `Windows/WindowsProcesses.cs` may mention `System.Diagnostics`; nothing may kill, suspend or touch a process (hygiene test). WMI events were not used and not tried (README says why).
- **Nothing is retried in a loop.** A refused start sets `SceneState.Block` (RateLimited, Changed, NeedsApproval, TooManyRuns, Invalid, Failed, Stopped); the block is lifted when the game has been seen gone
  (or, for `Invalid`, when a module started or stopped: `Verbs.Changed`). A failed undo sets `EndFailed` and is retried only by the owner's *Stop*.
- **One operation at a time**: `_work` (a `SemaphoreSlim`, not re-entrant) guards tick, start, end, adopt and shutdown. The question to the owner (`ApproveAsync`) is deliberately NOT under it, so an open dialog
  never holds up the end of a game. Code that already holds `_work` must call `ReconcileLockedAsync`, never `ReconcileAsync` (a deadlock, found by a test).
- **`GetApproval` blocks** (the host runs the previews, up to 2 s per step): call it only from `RefreshApprovals` inside `Task.Run`, never from the page build or a UI handler.
- **Rigbay rules** (`docs/ABSTRACTIONS.md` 12): constructor does nothing; `Probe` is read only; `Shutdown` stops the timer and puts back open runs within 3.5 s (the host allows 5 s); all text is a language key
  (`Text.Literal` for names that are data); threads catch everything (`Track`); no secrets in settings or log.

## Layout

| File | What |
| --- | --- |
| `Settings.cs` | `ScenesSettings`, `SceneSettings`, `StepSettings`, `UndoMode` (none / recorded / fixed) |
| `Model.cs` | `SceneRules` (the host's limits: 8 steps, 16 args of 256 chars; module id, verb pattern; never the own module), `Games` (normalise and match programs), `CommandLine` (split and join, quotes), `ScenePlans` (plan, fingerprint, lines), `UndoGuess` |
| `Ports.cs` | `IProcessSource`, `ITicker`, `Dependencies` (poll 5 s, grace 10 s, target wait 30 s, clock, delay) |
| `Windows/WindowsProcesses.cs` | the only real process code |
| `Runtime.cs` | `SceneState`, `BlockKind`, lifecycle, approvals, the poll, `TickAsync`, start / end / run now / stop / disable / delete, `ReconcileAsync` (restart adoption), events |
| `Page.cs` | `SceneView` (the values behind a scene's rows, wired once in `EnsureView`), the texts (`LineOf`, `AttentionOf`, `LastOf`), the step editor, `BuildPage` |
| `Commands.cs` | `list` and `status`, both `VerbAccess.ReadOnly` |
| `tools/live-check.ps1` | the live check (own instance; `-Restart` adds the restart case) |

## The engine in six lines

1. `TickAsync` (under `_work`): the wanted scenes are armed ones and ones with an open run; look for their programs; start scenes whose program runs, not blocked, armed; set `Missing` on runs whose game is gone.
2. Ends go newest-started first; a run that is not due (game still runs, or a manual run) stops the loop, so an older run is not put back from under a newer one (`WaitingFor`); `EndFailed` runs are skipped.
3. Due = not manual, `Missing` set, and `now - Missing >= Grace` (10 s). A game seen again resets `Missing`.
4. `RunNow` makes a manual run (the poll never ends it); `Stop` ends it and, if the game still runs, blocks the scene until the game has gone and come back.
5. `Start` reads `ActiveRuns()` and adopts a run whose game still runs, ends one whose game is gone or whose scene was deleted, shows one whose undo failed before; it first waits up to 30 s for the modules named in the run's steps.
6. `Stop()` (module off / Rigbay closing) ends open runs, 3.5 s at most; the host's journal keeps the rest.

## Traps

- `PlanRun.Message` for `NeedsApproval` reads `not approved (Changed) ...`; the module tells *Changed* (host already warned) from the rest by that text. If the host changes the wording the test `A_plan_the_host_says_changed...` fails.
- The host notifies Done, Failed, UndoFailed, RateLimited and Changed by itself. The module toasts only `NeedsApproval` (revoked elsewhere), `TooManyRuns` and `Invalid`, so nothing is announced twice (tests pin both lists).
- `ExpanderRow` header text is a `Text.Literal` of the scene name, so a rename needs `RequestPageRefresh`. A closed `InfoRow` still takes a band, so the attention row is only on the page while it has something to say
  (`AttentionOpen` is part of `PageKey`; `RefreshTexts` asks for a rebuild when the key changes, `BuildPage` starts with `_builtKey = null` so it does not ask for a rebuild of itself).
- The move / remove choice next to a step is a `ChoiceRow` used as an action (it springs back to *Keep*); step rows are rebuilt on every page build, the draft of the new step lives in `SceneView`.
- `Games.Normalise` is the only place that decides what a program name is; `Check` accepts only the stored form (so a hand-edited `game` without `.exe` is a problem, not a silent fix).
- Keys built in code: `problem.<SceneProblem lower case>` and `block.<BlockKind lower case>`; the hygiene test lists them. A new enum value needs both language files.
- The plans folder of a `--data` run is admin-only (`<data>\plans`): remove such folders with `tools\remove-elevated.ps1` (the live check does).
- Stop only YOUR app instance by pid from an elevated PowerShell (`Stop-Process -Id <pid>`); never `tools\stop-elevated.ps1` while another Rigbay runs. `--exit-after` ends an instance by itself.

## Tests and live check

- `dotnet test modules\scenes\tests\Rigbay.Scenes.Tests -c Release -m:2 -nodeReuse:false` (fast, no machine access). `Rig` builds a started module on `FakeVerbs` (a stand-in for the host's `PlanEngine`: hash-checked approval, one open run per plan,
  at most four, forced outcomes, a call log), `FakeProcesses`, `FakeTicker` and a fake clock; `rig.Tick(seconds)` moves the clock and looks once. Language files are read from the build output, so a missing key shows as the key text.
- LangCheck: `dotnet run --project src\Rigbay.LangCheck -c Release -- modules\scenes\src\Rigbay.Scenes\lang --source modules\scenes\src\Rigbay.Scenes --strict --module scenes` (a backslash in a text is `\\`).
- Live: `tools\build.ps1`, build the module in Release, then `powershell -File modules\scenes\tools\live-check.ps1 -Restart`; the report is `tools\out\live-scenes\report.txt`, shots in `...\shots`. Memory is tight with
  several workers: `-m:2 -nodeReuse:false`, one build at a time, `dotnet build-server shutdown` after.
- Adding a language key: both `.lang` files, `Row_headers_are_at_most_four_words...` (headers 4 words, descriptions 70 characters) and the key scan in `HygieneTests` (add the prefix there if it is new).

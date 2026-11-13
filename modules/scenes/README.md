# Game scenes (Rigbay module)

When a game you chose runs, Rigbay runs commands of other modules for you, and puts things back when the game ends. A scene is a game (or several
programs) plus an ordered list of steps; a step is one command of another module with its exact arguments (`xtu apply Gaming`, `steam-bypass on`,
`recorder start`) and how it is undone.

Module id `scenes`, group power, .NET 8, host API `>=0.1.4 <0.2` (it uses `IHostServices.Verbs`, `VerbPlan` and `CommandInfo`). Version 0.1.0, **not released**.

## The approval model, in plain words

A scene writes to your machine with nobody at the window, so it never starts on its own authority.

1. **You approve each scene once.** Switching a scene on (or pressing *Approve*) opens one question from Rigbay itself, not from this module. It lists every
   step: the command, what the module says it changes, the exact values when the module can say them, and how the step is undone.
2. **Rigbay stores what you approved** (a hash of the scene's steps, the version of every module they call, the arguments and the undo) in an admin-only
   folder. The host runs a scene only when what it is asked to run now is exactly what you approved. This module cannot add, change or reorder a step.
3. **Every run shows a notification**, from Rigbay (not from this module, so it cannot skip it), when the steps run and when they are put back.
4. **Editing a scene asks again.** A new step, a changed argument or undo, a different program, or an update of a module the scene calls (the host sees that) ends
   the approval. The scene then waits, says *Needs your approval* and stops polling. A rename does not ask again.
5. **You can revoke at any time:** the *Revoke approval* button, `rigbay plans revoke scenes/<scene id>`, or turning the module off. Undoing a run that is already open
   stays possible after a revoke (`rigbay plans undo <run>`).
6. **The modules keep their own limits.** A plan removes the dialog a module would ask, not the checks the module does (allow-lists, ranges, write latches).
   Today the other modules do not yet declare what their commands change, so for them the question says *the module did not say what the step changes* and
   *the module decides the exact values*. That is the honest state, and a reason to read the list before approving.

What approval does **not** protect against: a module that is malicious (modules run as administrator in the host anyway), and a scene you approved that you should not have.
The rate limit (6 starts of one scene per hour, 20 of all plans) is the guard against a game that crashes and restarts in a loop.

## How it works

* **Trigger.** While at least one scene is switched on *and* approved, or a run is open, Rigbay looks at the running programs **every 5 seconds**, by name
  (`Process.GetProcessesByName` for the programs of those scenes only; it never lists all processes while a scene waits). With nothing armed there is no timer and no read.
  The picker on the page lists programs that have a window, read when the page opens or on *Refresh list*, never by the poll.
  WMI process start/stop events were **not tried**: a poll is needed anyway to find a game that already runs when Rigbay starts and to adopt a run after a restart, and a
  read by name every 5 s costs nothing worth a second mechanism. The process source is one small interface (`IProcessSource`), so an event-based one can replace it.
* **Matching.** The file name, case-insensitive (`game.exe`), or a full path (`D:\Games\game.exe`) that matches only that file. A bare name gets `.exe`. Relative paths,
  shares, `..` and anything that is not an `.exe` are refused.
* **Start.** The first look that finds a program of the scene calls `StartAsync(plan)`. The host checks the approval, rate limit and open runs, writes its journal, runs the steps in
  order (the first failure puts back the ones done) and notifies.
* **End.** When no program of the scene has run for **10 seconds** (so a launcher that restarts the game does not flap the machine), `EndAsync(run)` puts the steps back in reverse order.
* **Two scenes.** They start in the order of the page; the last started wins; they are put back in reverse order of starting. A scene whose game ended first waits (*Waiting for X to end first*)
  until the newer one is put back. A run whose undo failed does not hold the others up.
* **Rigbay restarted while a game runs.** At start the module reads the host's open runs (the host never undoes by itself, it only tells you). A run whose game still runs is **adopted**
  (the scene goes on, ends with the game); a run whose game is gone, or whose scene was deleted, is ended; a run whose undo failed before is shown as *Could not put everything back*.
  The module waits up to 30 s for the modules the run needs to be up before it tries.
* **Turning the module off, or closing Rigbay,** puts back what an open scene changed (it waits up to 3.5 s; if it cannot, the host's journal keeps the run for the next start or `rigbay plans undo`).
* **The host's answers are shown and never retried in a loop.** `RateLimited`, `Changed`, `NeedsApproval`, `TooManyRuns`, a failed step: the scene says why and does not try again until the game
  has ended and started again. *Stop* while the game runs also holds the scene until the next start of the game. A failed undo is retried only when you press *Stop*.
* **Nothing else.** The module never starts, stops or changes a process, never calls a verb of another module on its own (no `InvokeAsync`), has no hotkey, tray item, metric, file access or registry access.

## The page

*Add a scene*, then one expander per scene: the switch is in its header, its state is the line under its name (*Off*, *Needs your approval*, *Waiting for game.exe*, *Running since 21:04 for game.exe*,
*Did not run: ...*). Inside: last run, name, programs (a comma-separated list, or pick a running program), the steps (each with a choice to move it up or down or remove it, and how it is undone),
the new-step editor (module, command with its description, usage, what it changes and whether it can report its own undo; arguments; *Put back by*: the value it had before / a command you write / nothing),
*Approve the scene*, *Run now* (a test: runs the steps once without the game), *Stop*, *Revoke approval*, *Delete the scene*.

Only commands that change something, or that did not say, are offered (a read-only command has nothing to approve). Choosing `steam-bypass on` suggests `steam-bypass off` as its undo; a command that
reports its own undo (`UndoVerb`) suggests *the value it had before*.

**No "browse" button.** The declarative page has no file picker; type or paste a full path in *Programs*.

Command line (both read only): `rigbay scenes list`, `rigbay scenes status`. Starting, stopping and approving are on the page only, because approval is a question to the person at the window.

## Files

| File | What |
| --- | --- |
| `Settings.cs` | the stored scenes (`ScenesSettings`); nothing in it can make a step run |
| `Model.cs` | rules a scene must keep (the host's limits), program names, command lines, the plan and its fingerprint, the undo suggestion |
| `Ports.cs`, `Windows/WindowsProcesses.cs` | the process list (`IProcessSource`), the poll timer, the clock |
| `Runtime.cs` | the engine: approvals, poll, start, grace, end order, adoption after a restart, shutdown |
| `Page.cs`, `Commands.cs` | the page and `rigbay scenes list|status` |
| `lang/en.lang`, `lang/vi.lang` | English and Vietnamese |
| `tools/live-check.ps1` | the live check described below |

## Verified

* **135 unit tests on fakes** (`dotnet test modules\scenes\tests\Rigbay.Scenes.Tests`): a fake host with a fake plan engine (hash-checked approval, one open run per plan, four open at most, every
  outcome forcible), a fake process list, a fake clock and a fake timer. They cover: the poll exists only while a scene is on and approved or a run is open; start once; the grace (9 s is not
  enough, 10 s is); a launcher restart inside the grace; several programs; a full path; two scenes (order of start and end, the older one waits, a failed undo does not block); a restart with an open run
  (adopted, ended, deleted scene, undo failed before, waiting for modules); every refusal of the host (rate limit, changed, revoked elsewhere, too many runs, failed step, undo failed on start and on end, an
  open run found by surprise) shown and not retried; an edit asks again (a step, the programs, a hand edit of the settings file; a rename does not); approve, decline, revoke, run now, stop, switch off, delete; shutdown;
  hand-edited settings (bad or duplicate ids, nulls); the page in both languages; the editor; the two verbs; and hygiene scans of the source (no registry, file, process start/kill, network, native call, free
  verb call; the manifest; the icons; both language files).
* **Strict LangCheck** on `lang\` (146 keys, en and vi).
* **Live, in my own Rigbay instance** (`modules\scenes\tools\live-check.ps1 -Restart`: own `--data`, `--allow-multi`, the demo module `demo-plan-target` that changes a number in its own memory, and `scenesgame.exe`,
  a copy of `ping.exe` under its own name, as the game): the page shows *Needs your approval*; *Approve* opens the host's plan question (answered by the test harness) and it is stored; when the game starts the
  steps run (`set 5`, `mark on`), the host logs one notification and the tray balloon; when the game ends the steps are put back in reverse (`mark off`, then `set` with the value recorded before) about 15 to 20 s later
  (grace plus poll), with a second notification; and with Rigbay stopped and started again while the game ran, the host announced the interrupted run, the module adopted it and ended it when the game ended.
  Shots of the page: dark theme, 1240 px, the window's own render.

## Not verified

* **A real module's verb.** No XTU, Steam, tunnel, recorder or PredatorSense command was ever run by a scene, in a test or live (a rule of the task). The first real scene needs its own gate.
* **A real game**: launchers that re-spawn the game, anti-cheat that hides a process, a game that runs under another user, or a game whose path Windows will not tell (a path entry then never matches).
* **The plan question was answered by the harness, not clicked**, and the tray balloon was counted in the log (`Windows displayed it`), not seen on a screen.
* The poll's CPU cost over hours (it reads a few process names every 5 s; not measured), and many scenes at once beyond the unit tests.
* Other widths, light theme, 150 % scaling and screen readers on the page (only dark at 1240 px was looked at). The move/remove choice next to a step is an action that springs back to *Keep*; it was not tried with a keyboard only.
* The Vietnamese text was written by the author of the module and not read by a native speaker.
* Turning the module off or closing Rigbay with a scene running was tested with fakes only, not live.
* WMI process events (see above).

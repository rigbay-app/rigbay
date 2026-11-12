# Changelog

## 0.1.2 (Unreleased)

The page from the November 2026 UI review (`docs/ui-review/REVIEW-2026-11.md` in the Rigbay repository, findings SA-1, LC-1, LC-2). Nothing the module
reads or writes changes (`requires` is the same); the manifest now asks for host API `>=0.1.3 <0.2`, because the page uses `PageModel.Primary`,
`Row.Line` and `Row.HelpKey`.

- **The bypass switch is the page's main control**, drawn large under the page header, with a live line under it: what Steam is doing ("Steam is
  using the bypass", "Steam is running without the bypass"), or the problem when turning it on failed. The rows "Steam" and "Last problem" are gone.
- **The Status group is there only while the bypass is on**: open connections (a tile and a chart of their own), failed connections (a tile, out of
  the chart: it is a count that only grows), the listening address and the two warnings (port 80 busy, restart Steam). With the bypass off the page
  shows no "Not available" tile, no empty chart and no "Not listening" row. The module asks the host for a new page when the bypass turns on or off
  and keeps its row instances, so the switch's "Applied" status and Revert survive the rebuild; the page reads a cached state and never waits for a
  change that is running.
- **The connection test is one row**, "Test the connection", whose line lists the four results during and after a run ("Store: Reachable · HTTP 200
  · 340 ms"); before the first run it says nothing. A second run while one is going says "A test is already running".
- **Shorter words**: headers of four words or fewer, descriptions only for a short fact, the explanations in the info glyph (`HelpKey`).
  "Turn on when Rigbay starts" is "Turn on with Rigbay". Vietnamese says "Vượt chặn Steam" for the bypass.

## 0.1.1 (pre-release)

Fixes the switch-worker race that the flaky-test review (FL1) noticed in 0.1.0, and the same pattern in four other places. No new feature, no
change to what the module reads or writes (`module.json` `requires` is the same).

**The rule now: one change at a time, and only its owner can end it.**

- The switch, the `on` / `off` commands and "Turn on when Rigbay starts" share one change slot. Whoever holds the slot holds a ticket; only the
  holder can give the slot back. In 0.1.0 a switch worker cleared the "I am working" flag in a `finally` block even when it had left because
  another worker had taken the flag, so a flip in that window could start a third worker next to the second.
- A second request while a change runs is **refused**, not queued: the switch goes back to what is in progress (or real), a toast says "A change
  is already in progress", and a command answers the same with exit code 1 (and does not even ask for approval). A flip to what is already in
  progress is not a new request. Refusing is the safer choice: a queued change would run later than the confirmation it was given for.
- A flip of the switch never waits for a change that is running (before, it could wait for a stuck registry write, on the UI thread).
- An exception in a change no longer leaves the switch showing something that is not real, and the slot is always given back.
- Stopping the module while a change is in the middle of its work waits for that change, restores the Windows setting exactly when the module
  owned it, and nothing can turn the bypass on after that (the controller is closed in the same step) and no reading is published afterwards.

**Same pattern, found by reading the rest of the code:**

- `ProxyEngine.Start`: the accept loop read the engine's cancellation source from a pool thread after `Start` had returned, so a quick `Stop` gave
  it a null (an unobserved `NullReferenceException`) and a `Stop` then `Start` gave the old loop the new run's token. The token is read under the lock.
- The connection check ("Run test", `rigbay steam-bypass test`): the "a test is running" flag is set and cleared by the same run only, and a run
  that fails while it is being set up no longer leaves the flag set for ever.

**Tests:** 269 (was 248). New: the switch race and its contract (`SwitchRaceTests`: off during on, on during off, concurrent on/off from six
threads, verbs and auto-on against the switch, cancel during on, an exception in the worker, stop during on / off / a failing change / before the
work starts) and the other places (`OwnershipGuardTests`). They hold a fake in the middle of its work with a hold-and-release point; no sleep
decides an outcome.

Not verified, as in 0.1.0: the bypass has never been turned on for real (no real `AutoConfigURL` write, no listener on ports 18081 or 80).

## 0.1.0

First release: the port of the standalone Steam Bypass app as a Rigbay module.

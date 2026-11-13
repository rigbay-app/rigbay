# Watch (Rigbay module)

Alerts when a reading on Rigbay's metrics bus stays past a limit: "CPU temperature at or above 95 °C for 30 seconds", "the CPU fan at 0 rpm while the CPU is above 80 °C",
"steam proxy errors went up by 5 within a minute". One Windows notification per episode (a tray balloon, because Rigbay runs elevated). **It only reads.** It subscribes to
readings, keeps its rules in its own settings and shows notifications; it publishes nothing, starts nothing, writes nothing to the machine and asks the host to approve nothing.

Status: version 0.1.0, not released. Needs Rigbay 0.1.5 or newer (host API 0.1.4: background metric demand; app 0.1.4 still has API 0.1.3). Tried only in a test instance of the app with a made-up publisher (below); everything else is **not verified**.

## Rules

A rule reads one metric (any reading in the catalogue: pick it from the list, grouped by the module that publishes it, with its value) and has:

| Part | Meaning |
| --- | --- |
| Alert when | **At or above**, **at or below**, **rises by** (a counter that went up by X within the time, for errors), **has bits set** (a status mask such as the GPU clock-limit reasons; the limit is the mask, shown as `0x60`) |
| Limit, For | the reading must stay past the limit for this many seconds (0 = at once). For *rises by*, "For" is the window the rise is measured in |
| Clear at | a second limit that ends the alert (hysteresis). The same as the limit = no band. It must be on the other side of the limit; the page refuses anything else |
| Quiet period | after an alert, no new one for this many minutes (default 15) |
| Check every | how often Watch asks the publisher for the reading, 5 s to 1 h (default 10 s) |
| Only while | optional: count the rule only while another reading is at or above / at or below a limit |

An **episode** starts when the reading has been past the limit for the whole time (checked at each check, so to within one check interval) and ends when it is back past the clearing limit.
**One notification per episode**: a Warning from the background, titled with the reading's name ("CPU temperature"), body "At or above 95 °C for 30 s. Now 97 °C.".
An episode that starts while alerts are paused, or inside the quiet period of the previous alert, is announced when that ends if it is still open, and never if it ended first.

**No data.** A reading older than twice the check interval (or withdrawn, or never seen) is "no data": the rule neither fires nor clears, an open episode stays open, the time already counted is dropped, and the
page says "Waiting for <reading>". Modules built before Rigbay 0.1.5 (host API 0.1.4) ignore a background request and may not publish at all while nobody looks at them (the XTU monitor, PredatorSense's operation mode), and the GPU module
(first version) polls only for a reader: rules on their readings stay Waiting until those modules adopt the new rule. Watch shows that instead of hiding it.

## Why it does not hold your PC awake

Watch asks for readings with `SubscribeBackground(id, interval)` and never with `Subscribe`. A background request is not counted as a reader, so a module that polls "fast while somebody looks" is **not** held at its fast
rate all day because Watch exists. A module built for host API 0.1.4 (`MetricDemand.PollInterval`) polls at Watch's interval instead. With no enabled rule Watch has no timer and no subscription at all.

## Suggestions

Six rules the page offers with one click, each only when a module that publishes its reading is on. **None is a rule, and nothing is subscribed, until you add it.**

| Suggestion | Rule |
| --- | --- |
| CPU temperature (`cpu.temp`) | at or above 95 °C for 30 s, clear at 90 |
| GPU temperature (`gpu.nvml.temp`, GPU module) | at or above 87 °C for 30 s, clear at 82 |
| one per tunnel (`tunnel.<name>.handshake_age`) | at or above 180 s for 15 s. The tunnel module withdraws the reading while the tunnel is off, so the rule waits then. A tunnel with no traffic and no keep-alive also gets an old handshake |
| Steam proxy errors rising (`steam.proxy.errors`) | rises by 5 within 60 s |
| GPU thermal throttling (`gpu.throttle`) | has bits `0x60` (software and hardware thermal) set for 30 s |
| Fan stopped while hot (`fan.cpu.rpm`) | at or below 0 for 20 s, only while `cpu.temp` is at or above 80 |

The numbers are starting points; edit them after adding. (They were chosen for a laptop with a 45 W CPU, not measured.)

## Pause

"Send alerts" at the top of the page: off pauses alerts for 1 hour (the line says until when), on resumes. Rules keep checking meanwhile. An optional tray item "Pause alerts for 1 hour" (a setting, off by default)
does the same. `rigbay watch status` prints the rules and their state, `rigbay watch pause <minutes>` (1 to 1440) and `rigbay watch resume` pause and resume. All three verbs are declared **read only**: pause and resume change only Watch's
own state (whether it sends notifications), nothing on the machine, so another module may call them without an approved plan. A pause is kept in memory only and ends when Rigbay restarts. "Test alert" shows a Warning notification the way a real one is shown.

## Read only, and how that is enforced

Tests scan the source for `Publish`, `Withdraw`, `Describe`, `Subscribe(` (only `SubscribeBackground` is allowed), the host's approval, verbs and shortcut services, processes, files, the registry, WMI, services, sockets and
native calls, and fail on any. The manifest declares no administrator right, no network, no registry, no service; the one `services` line says it reads the bus.

## Verified, not verified

Tried on 2026-11-13 in its own instance of the app (own data and modules folder, `--allow-multi`, no pipe to any other Rigbay), elevated, on one machine (Windows 11).

**Verified (ran it, saw it work):**

- 112 unit tests with a fake clock and a fake bus: threshold, duration, a dip that restarts the time, hysteresis (the band between the two limits neither ends nor starts an episode), one alert per episode, the quiet period (an episode inside it is announced
  when it ends if still open, never if it ended first), no data (stale, withdrawn, never seen, a gap drops the counted time, an open episode is neither ended nor announced twice), the condition reading, pause, rises (including a counter that starts over), bits,
  the saved-rule repair, what is subscribed (background only, per-rule interval, shortest wins, nothing for a rule that is off), the timer (none without an enabled rule), the verbs, the page (valid for the host's rules, picker, edits, suggestions, add and remove), every
  language key in English and Vietnamese, and the read-only scan. The strict language check passes.
- In the test instance with a made-up publisher (`tools/demo`: a reading that is calm, hot from 10 s to 70 s, then calm, published only while something asks, through `MetricDemand`) and a saved rule "at or above 90 °C for 20 s, clear at 80, quiet 1 min, check every 5 s":
  the rule had data without any page being open on the publisher, **one** Warning notification was raised at 35 s (`notice [Warning, Background, window in use: True] -> toast + in-window` in the log, then `tray balloon: Windows displayed it (NIN_BALLOONSHOW)`),
  the window showed the message and the rule "Firing since ..."; `rigbay watch status|pause 5|resume` (run inside that instance) printed and changed the state; after the reading fell to 30 °C the rule went back to Quiet with no second notification.
  Screenshot of the window's own render: https://art.minhdevtree.tech/a/CESA6a3z5EijbWP3CPLCLg
- The app's own self-test (`tools\selftest.ps1 -AllowMulti`, own data folder, the log's first line shows host 0.1.4 / API 0.1.4 of this build) passed: 206 checks, 0 failed, 1 skipped (single instance, because of `--allow-multi`). It does not load this module; it shows the host the module runs on is sound.

**Not verified:**

- A banner on the screen. `NIN_BALLOONSHOW` is Windows saying it took the balloon; whether a banner was visible (Focus assist, Do not disturb) was not looked at (`docs/APP.md`, "Notifications").
- Rules on real readings: CPU temperature, fan, GPU, tunnel, Steam. No real module was run with Watch. Today those modules do not adopt the background request, so their rules show Waiting unless the page or a recorder asks for them; this is the expected result, not a bug, and was not
  run live either.
- Editing a rule on the real page by clicking (the edits are tested through the page's own row objects, and the page is drawn in the test instance, but no row was clicked). The picker's values are those at the time the page was built.
- The Vietnamese page on screen (the texts are tested, the window was shot in English); the tray item on the real tray menu; the page rebuild after a catalogue change while the page is open (the 3 s delay is not tested).
- A long run: memory, CPU and the cost of the check timer over hours were not measured. With a rule on, one timer fires every check interval (10 s by default); with none, nothing runs.
- Behaviour across a clock change (sleep, daylight saving, a manual change): the engine uses the system clock and was tested with a fake one only.

## Install

Not published yet. For a local try: `powershell -ExecutionPolicy Bypass -File modules\watch\tools\demo\build-demo.ps1 -OutDir <scratch modules folder>` lays out `watch` and the demo publisher `watch-demo-feed`; start Rigbay with
`--data <scratch data> --modules <scratch modules> --allow-multi`, turn both on. `tools\live-check.ps1` does all of it and prints the log lines. Never develop against the real modules folder.

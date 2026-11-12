# Continuous integration (task CI1)

What runs on GitHub for `rigbay-app/rigbay`, what does not, and how to run all of it on your own machine. The workflow is `.github/workflows/ci.yml`; the
scripts it calls are in `tools/ci/`.

## What runs

Triggers: a push to `main`, and every pull request. A newer run for the same branch or pull request cancels the older one (`concurrency`). Runner:
`windows-latest`. Two jobs run in parallel.

| Job | Step | What it checks |
| --- | --- | --- |
| `build-test` (30 min limit) | Build | `dotnet build Rigbay.sln --no-incremental -warnaserror`: the whole solution (host, App, samples, four modules, tests), net10.0 and net8.0, **0 warnings**. |
| | Test | `dotnet test Rigbay.sln --no-build` with TRX logs: every test project (Core, LangCheck, and the tests of the four modules). On a failure the `.trx` files are uploaded as the artifact `test-results` (14 days). |
| `checks` (20 min limit) | LangCheck | `tools/ci/langcheck.ps1`: strict LangCheck (missing translation, unused key, wrong prefix are all errors) on all 10 lang folders: Core, App, the sample, the four modules, the three demo modules. It also fails when a folder with `.lang` files is not in the script's list, so a new module cannot skip the check. |
| | Repository hygiene | `tools/ci/scan.ps1`: no tracked `.dll .exe .pdb .nupkg .zip`; no strings that look like secrets (private keys, GitHub, AWS, Slack, Google and OpenAI/Anthropic-style keys, bearer headers, `password = <long value>`). |
| | Links | `tools/ci/check-links.ps1`: every relative link and image in the tracked Markdown files points to an existing file, and an `#anchor` to an existing heading. External links are not fetched. |

The workflow has `permissions: contents: read`, uses no secrets, publishes nothing, and does not persist the checkout token. The actions are pinned by full commit
SHA with the version in a comment (`actions/checkout` v7.0.1, `actions/setup-dotnet` v6.0.0, `actions/upload-artifact` v7.0.2), the same way the module
repositories do it. To update one: look up the SHA of the new tag (`gh api repos/<owner>/<action>/git/ref/tags/<tag> --jq .object.sha`; if the object is a tag and
not a commit, dereference it), change SHA and comment together.

## What does not run in CI

- **The in-app self-test** (`tools/selftest.ps1`, 136 checks at the last run). It starts the real WinUI app as an elevated process on an interactive desktop, which a
  hosted runner does not give. Run it locally before a pull request that touches the App (`tools/ci/local-check.ps1` does).
- **Anything that talks to hardware.** The runner has no Predator laptop, no Intel XTU, and no Steam block. Module tests run against fakes; whether a module
  works on a machine is what the hardware reports are for ([CONTRIBUTING.md](../CONTRIBUTING.md)).
- **The personal-name scan.** A check that no machine-specific string (the user profile name, the machine name of the developer) is in the tree cannot be written
  into a public workflow without writing those names into it. `tools/ci/scan.ps1 -Personal` builds the needles from the environment of the machine that runs it
  (`USERNAME`, `COMPUTERNAME`, case-sensitive; the lower-case public GitHub handle in the licence and in links is not a finding), so the script stays clean. CI cannot do this,
  because the runner has other names. Run it locally; `local-check.ps1 -Personal` includes it.
- **Releases, signing, publishing.** Releasing is manual ([RELEASING.md](RELEASING.md)); nothing is published from CI.
- **Link checking of external URLs**, spell checking and style checks.

## Running it locally

```powershell
powershell -ExecutionPolicy Bypass -File tools\ci\local-check.ps1                  # everything CI does + build the app + in-app self-test
powershell -ExecutionPolicy Bypass -File tools\ci\local-check.ps1 -SkipSelfTest    # only what CI does
powershell -ExecutionPolicy Bypass -File tools\ci\local-check.ps1 -Personal        # also the machine-specific-string scan
```

The pieces alone: `tools\ci\langcheck.ps1`, `tools\ci\scan.ps1 [-Personal]`, `tools\ci\check-links.ps1 [-Files a.md,b.md]`. All exit 0 when clean and 1 on a finding.
The self-test needs an administrator account (UAC auto-elevation, or an elevated shell); it uses scratch folders under `tools\out` and cleans them up.

## When CI fails

- **Build:** a warning is an error here. Fix the cause; do not suppress it unless the reason is written next to the suppression.
- **A test:** download the `test-results` artifact from the run page, or run `dotnet test Rigbay.sln --logger trx` locally.
- **LangCheck:** run `tools\ci\langcheck.ps1`; the report names the folder, the key and the problem.
- **Scan:** the report names the file and line. A real secret that was committed is not fixed by deleting the line: it is revoked, and then removed from history.
- **Links:** the report names the file, line and target.

## Flaky tests

Task FL1 (2026-11-02). A test is flaky when it fails without a change in the code. The method, the findings, the fixes and the proof are here, so the next
hunt starts from them.

### Method

The machine has 16 logical processors. Every test project (1353 tests: PredatorSense 121, LangCheck 19, Recorder 146, XTU 190, SteamBypass 248, Core 629) was run through
`dotnet test Rigbay.sln --no-build --logger trx`, 30 times in a row, in two conditions, and every non-passing result was read from the TRX files:

- **alone**: nothing else running;
- **stress**: two busy loops (a PowerShell process each, pinned to cores 0 and 1) and the whole test run pinned to cores 0 to 3, so the tests really compete for the
  two loaded cores. That is the load of a slow shared runner, on at most two cores. The loops were stopped after each series.

Hunts for one project: the old PredatorSense tests alone, 60 runs pinned to cores 0 and 1 under the two busy loops, and 40 runs pinned to core 0 (one busy loop on it).

To repeat it, loop `dotnet test --no-build --logger trx --results-directory <new folder per run>` and read `//UnitTestResult[@outcome != 'Passed']` in the TRX files
(the namespace is `http://microsoft.com/schemas/VisualStudio/TeamTest/2010`). A test that fails once in 30 loaded runs is a finding.

### Findings (before the fixes, commit `0b48b02`)

Alone: 30 runs, 0 failures. Under stress: 30 runs, 31 failing results in these tests:

| Test | Failures | Message | Cause |
| --- | --- | --- | --- |
| `SteamBypass ProxyEngineTests.Only_listed_http_hosts_in_absolute_form_are_forwarded` (9 cases) | 28 | `Assert.Contains() Failure: Sub-string not found, String: ""` | The rig gave clients 600 ms to finish the request head. A test client that is late by a second is closed without an answer (designed behaviour). |
| `SteamBypass ProxyEngineTests.A_proxied_http_request_gets_its_host_header_respelled_and_connection_close` | 2 | `Sub-string not found, String: "" Not found: "200 OK"` | The same head timeout. |
| `SteamBypass ModuleSwitchTests.Flipping_it_off_puts_the_saved_value_back_closes_the_listeners_and_withdraws_the_metrics` | 1 | `Assert.Contains() Failure: Filter not matched in collection, Collection: []` | The test went on when the Windows setting changed; the worker publishes the metrics after that. |

A second round (30 runs under stress after the first fixes) found two more:

| Test | Failures | Message | Cause |
| --- | --- | --- | --- |
| `Core FaultBoundaryTests.A_hanging_Initialize_times_out_and_the_host_goes_on` | 2 | `Sub-string not found, String: "The module did not answer in constructor ..." Not found: "Initialize"` | The 300 ms timeout of the test also guarded loading and constructing the module; a starved machine used it up before `Initialize` ran. |
| `Recorder EngineScheduleTests.The_real_clock_works_too` | 1 | `Assert.Equal() Failure: Expected: 3, Actual: 2` | Real clock, exactly two data rows. A late first row moves the slot by design (the same flaw that was fixed in `module-recorder` for `The_real_services_work_on_this_machine`). |

**The PredatorSense timing test of the failed GitHub run was not found.** `gh run list -R rigbay-app/rigbay` lists one run of `ci` (on `0b48b02`, green) and no failed
run; the module repositories have no failed run except the recorder one above. So the log of that failure could not be read. The old PredatorSense tests did not fail
in 30 + 30 runs of the solution, nor in the 60 + 40 runs of the project alone under load. The tests with real timing there were
`Scheduler_polls_repeatedly_and_stops` and `Scheduler_PollSoon_reads_now_even_with_the_slow_interval_and_does_nothing_when_stopped`: both stopped the scheduler and
slept 80 ms before asserting that no read happened any more, but `Stop()` lets a poll that is already running finish (documented), so on a slow runner the poll
could end after the sleep and add reads. They are the likely ones, found by reading and not by a failure, and they are rewritten (below). If the failure comes
back, its name is in the `test-results` artifact of the run.

### Fixes

No sleep decides an outcome that must hold; events, counters and generous deadlines do.

- **SteamBypass proxy tests:** the rig's `HeadTimeout` is 30 s (a deadline for the client, not a pace). The one test of the timeout itself has its own 600 ms rig and
  counts `Counters.Total` (it only goes up) instead of waiting for `Active == 30`, which the first connections leaving could miss.
- **SteamBypass switch tests:** the module counts the switch workers it started and finished (`SwitchWorkersIdleForTests`, internal, tests only; it changes no behaviour);
  `ModuleRig.FlipSwitch` waits until they are all done, so what it returns after is the end state (texts, metrics), not the middle. The two `Thread.Sleep` calls that
  asserted "nothing happened" wait for the workers instead.
- **Recorder real clock:** one or two data rows, every row checked; the exact schedule is pinned by the fake-clock tests.
- **Core:** `TestEnv.NewHost(..., probeTimeout)` separates the load and constructor timeout from the short `Initialize` timeout of the hang test.
- **PredatorSense scheduler tests:** a read hook (`FakeAcerWmi.OnRead`) holds a poll in flight, `Stop()` is called with it running, and the `Polled` event, not a
  sleep, says when it is over; then at most that one poll happened. `PollSoon` is checked with the `Polled` event and an exact count of six reads.
- **Waits:** every `Wait.Until` / `WaitFor` / deadline that waits for something to happen is 30 s (it returns as soon as the condition holds, so a pass costs nothing).

Production code: one internal property and two counters in `SteamBypassModule` (above). No race in production code was fixed. One was noticed and not changed: in
`SteamBypassModule.SwitchWorker` the `finally` block clears the `_switching` flag even when the worker leaves because another worker already took it, so a flip in that
window could start a third worker next to the second. It did not show up in any run.

*Resolved in Steam access 0.1.1 (task S2):* one change at a time with an owner token, a second request refused, the same pattern fixed in `ProxyEngine.Start` and the
connection-check flag; see `docs/steambypass/MODULE.md`, "Version 0.1.1".

### Not flaky by design (left as they are)

A few tests wait a fixed time and then assert that nothing happened (`Task.Delay(100)` then "no write was made", and the like). A wait that is too short can only make such
a test miss a bug, never fail a correct build. They are in `PredatorSense ModuleTests` and `FanModeStateTests`, `SteamBypass ProxyEngineTests` and
`ModuleTests`, `Core InstallFlowTests`, `LiteralAndRefreshTests` and `HostGapTests`, and the XTU `MonitorGateTests`. The Core sample-module tests run on the real clock
(one tick a second); they have 30 s deadlines now.

### Proof (after the fixes)

| Series | Runs | Tests per run | Failures |
| --- | --- | --- | --- |
| alone | 30 | 1353 | 0 |
| stress (two busy cores) | 30 | 1353 | 0 |

`dotnet build Rigbay.sln --no-incremental -warnaserror`: 0 warnings, 0 errors. A series that finds a failure has to be repeated after the fix; this one was (round one
of the fixes still had the two findings of the second table).

### FL2 (2026-11-13): two failures on GitHub Actions

Each failed once, on a different commit, on the windows runner (2 cores, all test projects running at the same time). Neither could be reproduced locally in
about 65 runs of the PredatorSense project under pool and CPU limits (`DOTNET_PROCESSOR_COUNT` 1 and 2, two busy loops pinned to the cores the tests ran on), so
the causes below come from reading the code and the logs, and are said to be that.

| Test | Message | Cause | Fix |
| --- | --- | --- | --- |
| `Core HostGapTests.The_sample_module_polls_slowly_until_somebody_looks_and_fast_after` (run 43287929386) | `Expected: 1, Actual: 2` at the first reading, test time 3 s | The sample module's poll timer was periodic. A periodic `Timer` does not wait for its callback: when the pool is late, the callback of the first second and the one of the second second are both queued, so the module ticks twice before the first tick has slowed the rate down. | Production fix in `samples/Rigbay.SampleModule`: the timer fires once and every tick arms the next one at the rate that applies then (`ArmNextTick`, under one lock with `_fast`). |
| `PredatorSense PollSchedulerIdleTests.An_infinite_idle_interval_means_no_poll_at_all_until_somebody_looks` (run 43292629669) | `no polling after the scheduler was watched`, after 10 s | Three `Eventually` helpers ran `Task.Run(() => Wait.Until(...))`: every wait held a pool thread asleep for the whole wait, for work that itself needs the pool (`Task.Run` in the module, timer callbacks). The pool adds threads about twice a second, so on two cores the scheduler's 30 ms timer callbacks could wait for seconds; the test allowed 10 s (every other wait had 30 s since FL1). `PollScheduler` has no race: its timer is one-shot, re-armed after each poll. | `Wait.UntilAsync` (`Task.Delay`, no pool thread held) replaces the three `Task.Run` wrappers; the test waits the 30 s default; its "and then it stops" check no longer sleeps 150 ms and compares counts (a poll running late could end after it): it counts from the moment `Watched` goes false and allows at most one more poll. |

Not achieved: a test that fails before the fix and passes after it. For the sample timer I wrote one that held the whole pool busy for 2.6 s, and it passed on the
old code too (the first timer callback of a batch runs on the timer thread itself, not on the pool, so the queued double callback needs a second timer due at the
same moment, which cannot be arranged reliably). It was dropped, not kept as a test that proves nothing. The PredatorSense change is test-only.
`docs/ABSTRACTIONS.md` ("Polling modules") still shows `_timer.Change(wanted ? Zero : Slow, wanted ? Fast : Slow)`, a periodic timer; the coordinator was told.

Proof (build with `-m:2 -nodeReuse:false`, `--no-build`, each test alone by `--filter`, one `dotnet test` per run):

| Series | Runs | Failures |
| --- | --- | --- |
| `PollSchedulerIdleTests` alone | 50 | 0 |
| `The_sample_module_polls_slowly_...` alone | 50 | 0 |
| `PollSchedulerIdleTests` under stress | 30 | 0 |
| `The_sample_module_polls_slowly_...` under stress | 30 | 0 |

Stress here: two PowerShell busy loops pinned to cores 0 and 1, and the test run pinned to the same two cores; the loops were stopped afterwards (checked: none left).
Then `dotnet build Rigbay.sln --no-incremental -m:2`: 0 warnings, 0 errors; one full `dotnet test Rigbay.sln --no-build`: 1710 passed, 0 failed (LangCheck 19,
Tunnel 117, Recorder 146, PredatorSense 184, XTU 208, SteamBypass 269, Core 767). The runs before the fix also passed, so the loops show no regression, not that
the failures are gone; only the next weeks of CI can show that.

## Settings that are not in this repository

Branch protection ("require the `ci` checks before merging"), Dependabot for the action pins, the labels the issue templates ask for (`bug`, `module-request`,
`hardware-report`) and private vulnerability reporting for [SECURITY.md](../SECURITY.md) are repository settings. They are decisions of the owner and are not
changed by anything here; until they are on, the files work but nothing enforces them.

# Testing the XTU module

## Unit tests (no hardware, no admin)

```
dotnet test modules/xtu/Rigbay.Xtu.slnx -c Release          # also on a machine without XTU (CI)
dotnet test Rigbay.sln -c Release          # also the Core tests; the module is part of the main solution and needs no Intel file
dotnet run --project src/Rigbay.LangCheck -- modules/xtu/Rigbay.Module.Xtu/lang --module xtu --source modules/xtu/Rigbay.Module.Xtu --strict
```

Fakes are in `Fakes.cs`: `FakeTuningPort` (records calls, simulates the four controls), `FakeMonitorPort`, `FakeClient`, `FakeApproval`, `FakeHost`
(all host services), `FakePower`, `FakeSystem`. `Rig` (in `RuntimeTests.cs`) builds a started `XtuRuntime` with a temp XTU folder. Tests wait for the
background work with `Rig.WaitFor`.

What the tests cover: profile loading and matching; the validation ranges and refusals; the apply sequence in order, and that locked, unsupported, rejected,
failed and invalid inputs never leave a pending change; declined approval writes nothing; page and auto-switch do not ask twice; auto-switch decisions and
resume; hotkey policy and XTU's combinations; metric ids, units and warm-up; probe outcomes; commands and exit codes; language keys (page, source scan, Vietnamese
placeholders); the manifest; `WireContractTests` (20, no XTU: the contract copies against the recording in `wire/`); M1 (`MonitorGateTests.cs`): a descriptor and en/vi label for every id, revert texts on every hardware row, `MonitorWanted` and the monitor thread idle until the page is shown or something subscribes, withdraw after three failures and republish. `FakeMetrics` implements `Withdraw`, `Describe`, `SubscriberCount`, `SetSubscribers`; `FakeHost.PageVisibleValue`.

## Live check on the real machine (read-only)

`modules/xtu/tools/live-read.ps1 -AppDir <published app> [-OldApp <xtu-switcher exe>]`: builds the module, lays it out with a `no-write.flag`, enables it in
the real app (own data folder), prints `rigbay xtu status` and `list`, optionally the old app's `--status`, and renders the module page dark and light.
It never applies. Publish the app first (command in the script header) into a scratch folder, not into the repo.

Rules for any live run: elevated only through `Start-Process -Verb RunAs` with short-lived processes (`--exit-after`); the latch file in the data folder;
no `edit:` script step that the script answers with `approve:true`; never a screen capture; look at the PNGs.

To compare monitor values with the old app at the same moment: run the old app `--log <csv> --seconds 16 --every 1 --no-percore --no-gpu` (it records XTU's
monitor values and the Windows CPU counter) and render the page 3 s later; match the page's numbers with the CSV row nearest to the shot's time in `rigbay.log`.

## Rendering a long page

The window is capped by the display height (about 1100 px at 100 %). To see lower groups, build a throw-away copy of the module in a temporary folder inside
`modules/xtu/` (the relative project reference must resolve), add `groups.RemoveRange(0, N);` before `return new PageModel` in `Page.cs`, build with `-o` into
a scratch modules folder, render, and delete the copy. Do not commit it.

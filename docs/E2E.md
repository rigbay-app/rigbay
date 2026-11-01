# E2E1: the first hour, from the real registry, in scratch folders

Task E2E1, 2026-11-02, this machine (Windows 11 10.0.26300, 100 % DPI, an Acer Predator Triton 500 SE). It proves the product pipeline end to end the way a
user's first hour goes: the **released app** (`v0.1.0`), the **real registry** (`rigbay-app/registry` main) and the **four real module releases**, driven through
the app's own Modules page with UI Automation. Everything ran in scratch folders; the real `C:\Program Files\Rigbay`, `C:\ProgramData\Rigbay`, the real
`Rigbay` scheduled task and the user's proxy settings were never touched (the driver compares them before and after every run). No hardware write was made.

## 1. Result in one table

| Run | What | Result |
| --- | --- | --- |
| Released zip, main flow, dark | `tools\e2e\flow.ps1` | **73 passed, 1 failed** |
| Released zip, main flow, light | same, on fresh scratch folders | **73 passed, 1 failed** (the same one) |
| Released zip, failure paths | `tools\e2e\hooks.ps1` | **41 passed, 0 failed** |
| Released zip, normal user vs. the admin-only folders | `tools\e2e\acl-user-check.ps1` (not elevated) | **10 passed, 0 failed** |
| Build with the fixes of section 6, main flow dark and light | `run.ps1 -AppDir tools\out\app` | **74 passed, 0 failed** each |
| In-app self-test with the fixes | `tools\selftest.ps1` | 130 of 130 PASS (129 before, one new check) |

The one failure on the released build is a real defect (E2E-04, the removal message names the module by its id). It is fixed in this branch; the fixed build passes.
Reports: `docs/e2e/report-*.txt` (one line per step, paths shortened to `<repo>`). Pictures: `docs/e2e/*.png` (a selection; the full set of 22 steps in dark and light and the
nine failure-path shots is in `tools\out\e2e\evidence`, git-ignored). Every picture was looked at.

## 2. Commands

```powershell
# everything: downloads the released zip, checks its SHA-256 against the digest GitHub reports for the asset, extracts it to tools\out\e2e\app,
# runs the flow (dark, light), the failure paths and the normal-user ACL check, removes the admin-only scratch folders at the end
powershell -ExecutionPolicy Bypass -File tools\e2e\run.ps1
powershell -ExecutionPolicy Bypass -File tools\e2e\run.ps1 -Only flow-dark          # one stage (one name per call)
powershell -ExecutionPolicy Bypass -File tools\e2e\run.ps1 -Only flow-dark -AppDir tools\out\app -EvidenceName evidence-fixed   # another build, same checks
```

The driver scripts are started **elevated** (`tools\e2e\invoke-elevated.ps1`; UAC auto-elevates for this admin account): the app is elevated, and a normal process
cannot drive an elevated window (UIPI). `tools\e2e\lib.ps1` has the helpers: out-of-process UI Automation (`System.Windows.Automation`: find by name and type,
Invoke, Toggle, Expand, Value), and a `PrintWindow` of the app window's own HWND (never the screen). It resizes only that window. Stop leftovers with
`tools\stop-elevated.ps1`; remove the admin-only scratch folders with `tools\remove-elevated.ps1 <folder under tools\out>`.

**Isolation from other Rigbay processes.** Other workers run `Rigbay.App` on this machine. The driver selects, drives, waits for and stops **only** the processes whose command
line names this run's scratch data folder (`--data <run>\`); if a foreign instance exists it adds `--allow-multi` so the Global single-instance mutex is not shared.

## 3. How the released exe is started with scratch folders

The released `Rigbay.App.exe` needs no special build: `--data <dir>` and `--modules <dir>` are product switches (`docs/APP.md`), and `Restart now` carries them into the new
process (`--data D --modules M --theme dark --wait-pid <old pid>`; the one-run test switches are dropped). The **real registry is used** (no `--registry`), so the only thing
that makes it a scratch run is where the data, the modules folder and the install folder (`<modules>.install`, next to it) live. `--theme dark|light` picks the theme for the run.
`--registry <file>` and `--slow-download <ms>` are the app's own test switches; they are honoured only with scratch `--data` and `--modules` (`docs/INSTALL.md` section 7)
and are used only in the failure-path scenarios H3 to H5.

## 4. The flow (`flow.ps1`) and what was seen

Fresh scratch folders each time. `Shot` names refer to `tools\out\e2e\evidence\<theme>-<n>-<name>.png`; the ones in `docs/e2e` are marked *.

| # | Step (all through real controls) | Checked and seen | Picture |
| --- | --- | --- | --- |
| 1 | Start the released exe elevated with scratch folders | The window appears and **opens the Modules page by itself** (first start); it shows the scratch folder, not ProgramData; nothing was created yet (only `data`). | 01 * |
| 2 | **Get the module list** | Fetched from `raw.githubusercontent.com` in 0.6 s. Four rows: PredatorSense, Recorder, Steam access, XTU profiles, each with size and "Needs administrator / Registry (n) / Services (n)" as the registry says; details show id, version, release date, host API, every declaration, source link, SHA-256 and "Works on this PC: not known until it is installed". | 02 *, 03 * |
| 3 | **Check for updates** again | The log says `registry: not modified` (conditional request, GitHub answered 304). | |
| 4 | **Install Recorder**, watching the states | `Busy` -> `Downloading 0 B / 73.5 KB` with a Cancel button -> "Installed. Appears after the restart, turned off." in 0.9 s. Amber **Restart required** bar with Restart now, a dot on Modules in the pane. `pending.json` has the install with the registry's SHA-256; `modules\` is still empty, the files are parked in `ready\recorder`; `downloads\` and `staging\` are empty; the scratch folders' ACL has only Administrators and SYSTEM. | 04 *, 05 * |
| 5 | **Undo** | The change is gone, `ready\recorder` deleted, the Install button is back, no restart bar. | 06 |
| 6 | Install Recorder again and **Install PredatorSense** | Two changes queued; both carry the registry hashes. Steam access and XTU are never installed or queued. | 07 * |
| 7 | **Restart now** | A new process with the same exe and the same `--data`, `--modules`, `--theme` plus `--wait-pid <old pid>`; the old process ended; no UAC prompt on this account (see section 8). | |
| 8 | After the restart | Dashboard lists both modules **Off**; both folders are in `modules\`, `ready\` and `pending.json` are empty; the Modules page says "At the last start: Recorder 0.1.0 was installed. It is off until you turn it on. PredatorSense 0.1.0 was installed..."; both switches are off; `host.json` has nothing enabled. | 08, 09 * |
| 9 | **Enable PredatorSense** through the dialog | The dialog "Turn on PredatorSense?" lists Administrator rights, the two registry reads, WMI `root\WMI AcerGamingFunction: sensor reads, and a fan mode write only when you ask`, and `PSSvc: looked up, never started, stopped or called`. Turn on -> Running. The page shows three temperatures (for example 72 / 72 / 54 °C), three fan speeds (about 2900 to 3100 rpm), the machine string and "Fan mode: Last set by Rigbay: Nothing yet / Speed check: No change made yet". **Only reads: the fan mode box, the shortcut and the Restore row were never touched.** | 10, 11 *, 12 *, 13 |
| 10 | **Enable Recorder** through the dialog, set the Folder to a scratch folder (Apply), **Start**, 10 s, **Stop** | Dialog lists its two registry reads. The CSV has 11 rows, header `timestamp,elapsed_s,cpu.temp,fan.cpu.rpm,fan.gpu1.rpm,fan.gpu2.rpm,gpu.temp,system.temp`, every gap **1.000 s** (min and max), no empty cell, UTF-8 without BOM; the `.meta.json` has rows = 11, stop reason `user`, the machine, both module versions and the real `°C` unit. The user's real `Documents\Rigbay\logs` did not change. A sample is `docs/e2e/recording.csv` and `recording.meta.json`. | 14 *, 15, 16, 17 * |
| 11 | **Remove** the Recorder (Installed row -> Remove) | A confirmation "Remove Recorder? ... Its settings are kept." The removal is queued, nothing deleted; the row says "Restart required · Is removed after the restart" with Undo. | 18, 19 *, 20 |
| 12 | **Restart now** again | PredatorSense (enabled) comes back Running; Recorder is gone from Installed and from the nav pane and is offered again under Available. `modules\recorder` deleted; `previous\`, `ready\`, `staging\`, `downloads\`, `trash\` empty, no pending change; `host.json` and `app.json` forgot the recorder (its accepted declarations too). **By design the recorder's settings (`data\modules\recorder\settings.json`) and the recording stay.** The log has no ERROR. | 21 *, 22 |

Both modules stopped cleanly at each restart (`predatorsense stopped`, `recorder stopped` in the log before the successor started).

## 5. Failure paths (`hooks.ps1`, scratch folders, dark)

| # | Scenario | How | Seen |
| --- | --- | --- | --- |
| H1 | **Offline, nothing saved** | The real registry address, but this one process gets `HTTPS_PROXY=http://127.0.0.1:9` (a dead port). .NET reads that variable before the system proxy. No adapter, no hosts file, no system proxy was touched. | "You are offline, and no list is saved on this PC yet." and a **Try again** button, no module rows. (`hooks-h1-offline-no-list.png` *) |
| H2 | **Offline with a saved list** | List fetched from GitHub first (cache is in `<modules>.install\registry`, not LOCALAPPDATA), then the same folders restarted with the dead proxy. | The saved list is on the page at once ("Module list saved on this PC"); **Check for updates** -> "You are offline. Showing the list saved on <date>."; all four rows stay. Installing Recorder fails honestly in the row ("Failed. You are offline.", Try again) and leaves no pending change, no `ready\`, no download. (`h2-*` *) |
| H3 | **Corrupted downloads** | The app's own `--registry <file>` switch (scratch folders only) lists damaged copies of the **real** release zips: one byte flipped (same size), cut in half, 4 KB of random bytes with the matching hash, the real file with a list size 10 bytes too large. | "Failed. The download does not match its SHA-256 in the list. It was deleted and nothing was installed." / "The download is not the size the list promised." (cut, wrong size) / "The file is not a valid zip." Nothing queued, parked, unpacked or left in `downloads\`; no restart bar. (`h3-all-failed` *) |
| H4 | **Cancel** | `--slow-download 1500` on the real PredatorSense zip (16 KB per 1.5 s). | "Downloading 64 KB / 267 KB" with a moving bar (24 % after 5 s) and Cancel; the partial file is in `downloads\`; Cancel -> the Install button is back at once, no failure text, partial file deleted, nothing queued. (`h4-*` *) |
| H5 | **A list that is not valid** | `--registry` pointing at a broken file. | "The module list is not valid, so nothing from it is shown. The registry is not valid: modules[0].id: lowercase words joined by '-'; ..." and Try again. (`h5-invalid-list` *) |
| H6 | **Admin-only folders in the real layout** | The modules folder is `...\h6-acl\Rigbay\modules`, i.e. inside a parent **named `Rigbay`**, which is the layout where the app also hardens the parent (`docs/INSTALL.md` section 3). | `Rigbay`, `modules` and `modules.install`: exactly Administrators and SYSTEM, owner Administrators, protected from the parent; `pending.json`, the cached registry and the parked module file inherit only those two. From a **non-elevated** shell (`acl-user-check.ps1`): cannot list, plant a folder or a file, edit `pending.json`, replace the cached registry, swap a parked DLL, create anything in `Rigbay`, or delete `modules`; a control folder next to it stays writable. |

## 6. Defects

Severity is the effect on a user, not the effort. **Fixed** = fixed in this branch with a test; **Reported** = for the coordinator, not changed.

| Id | Finding | Evidence | Status |
| --- | --- | --- | --- |
| E2E-04 | After a **removal** the Modules page's "At the last start" line says "**recorder** was removed." (the id, lower case) instead of "Recorder was removed.": the folder and its manifest are gone, so the name was not known. Low. | released build, dark and light: `FAIL ... display name "Recorder"`, `dark-21-after-remove-modules.png`; fixed build: `Recorder was removed.` (`fixed-light-21-after-remove-modules.png`) | **Fixed** (in 0.1.1; `ModuleManager.RegistryNameOf`, used by `ModulesPage.ReportLine`; test `A_module_the_host_no_longer_knows_is_named_by_the_registry_not_by_its_id`) |
| E2E-03 | The **Installed** rows (SettingsExpander) had **no accessible name**: their header is a stack of icon and text, so UI Automation saw an unnamed group and an unnamed expander button, while the Available rows are named. A screen reader could not tell the rows apart. Medium (accessibility). | `probe` of the UIA tree; self-test check added | **Fixed** (in 0.1.1; `AutomationProperties.SetName` on the expander and its header; self-test `install page: every Installed row has an accessible name`, 9 of 9 rows) |
| E2E-05 | `dotnet test` **failed from a normal shell** while any admin-only scratch folder existed under `tools\out`: `F1Tests.No_language_file_in_the_repo_has_mojibake` walked the whole repo and threw `UnauthorizedAccessException` on the folders the installer made admin-only (the self-test and E2E runs leave them until their cleanup). Low (test robustness). | `Access to the path '...\tools\out\e2e\run-dark\modules' is denied` | **Fixed** (the test now ignores inaccessible folders and skips `tools\out`, which is build output) |
| E2E-02 | The **Recorder's default folder is `Documents\Rigbay\logs` on the real profile** whatever `--data` says, and its History lists the files already there. A recording started in a scratch or test run therefore writes into the user's real Documents (the flow sets the Folder to a scratch path first, and checks that the real folder did not change). Medium for testing and for anyone who runs with a separate data folder. | `Documents\Rigbay\logs` listed on the Recorder page of a scratch run | **Reported** (module `rigbay-app/module-recorder`: a default under the host's data folder, or an explicit "default folder" note, is a module decision) |
| E2E-06 | On the Recorder page the **Folder box shows only the first ~35 characters** of a long path ("D:\Documents\Projects\rigbay\.claude\w"), and "Last message" is cut ("... was sa..."). Known (`TextRow`, `docs/recorder/README.md`). Low. | `dark-17-recorder-stopped.png` | **Fixed in 0.1.1** (`RowRenderer.TextEntry` takes half of the row, 280 to 560 px, with the whole text as a tooltip; `ValueText` and the status next to a control wrap; self-test: 3 checks) |
| E2E-07 | An Installed row with a **removal queued** still shows an enabled, **on** switch next to Undo. Harmless (the module is stopped at the restart) but suggests it will stay. Low. | `dark-20-remove-queued.png` | **Fixed in 0.1.1** (`ModulesPage.Switch`: shown off and disabled while a removal is queued, Undo restores it; self-test: 2 checks) |
| E2E-08 | While a module downloads, its row's description is truncated ("...with a...") to make room for the progress; the full text is only a tooltip. Cosmetic. | `dark-04-installing-recorder.png` | **Fixed in 0.1.1** (the description wraps, the progress and Cancel have their own line under it; self-test: 1 check) |
| E2E-09 | The first start of a **fresh** modules folder writes `WARN host modules folder '...' does not exist` to the log. `install.ps1` creates the folder so a product install does not see it. Cosmetic. | `<run>\data\logs\rigbay.log` | **Fixed in 0.1.1** (`RigbayHost.Discover` makes the folder through `AdminFolderSecurity` and logs INFO; if that fails, one INFO line with what to do; 3 tests) |
| E2E-10 | The PredatorSense page shows the Fan mode box as **Auto** before Rigbay has set anything; the row below says "Nothing yet" and that the laptop cannot report its mode. Honest in the text, a little suggestive in the control. Low. | `light-12-predatorsense-readings.png` | Reported (module) |

Nothing else broke: no crash, no hang, no ERROR in any log, every failure path ended in a clear row, and every failed or cancelled install left no file behind.

Driver artefact, not a defect: after the driver typed into the Recorder's Folder box through the Value pattern the page was scrolled by about 35 px (the title is clipped in the later
Recorder shots); a focused text box scrolls into view.

## 7. Changes made to the product in this task (each small, each with a check)

1. `src/Rigbay.Core/Install/ModuleManager.cs`: `RegistryNameOf(id)` (the name the saved registry gives a module). Test: `ModuleManagerTests.A_module_the_host_no_longer_knows_is_named_by_the_registry_not_by_its_id` (also after `LoadCache()` with no network, which is what the app does at start).
2. `src/Rigbay.App/Ui/ModulesPage.cs`: the start-up report names a module by its display name, else the registry name, else the id; Installed rows get an accessible name. Checked by the self-test (new check) and by the E2E flow on the rebuilt app (74 of 74).
3. `src/Rigbay.App/SelfTest.cs`: the check "every Installed row has an accessible name".
4. `tests/Rigbay.Core.Tests/F1Tests.cs`: the language-file walk ignores inaccessible folders and `tools\out`.

Verification after the changes (2026-11-02): `dotnet build Rigbay.sln --no-incremental` **0 warnings, 0 errors**; `dotnet test Rigbay.sln`: Rigbay.Core.Tests **626 passed** (625 + the new one), PredatorSense 103, Recorder 146, XTU 190, Steam 248, **0 failed**;
`tools\selftest.ps1` **130 of 130 PASS**.

The released `v0.1.0` exe was built from `557d091`; the fixes are on this branch only, so a user of the released zip still sees E2E-03 and E2E-04 until the next release.

## 8. Not verified

- **A UAC prompt on Restart now.** This account auto-elevates, so the successor started without a prompt. An account that has to type a password was not tried.
- **The real `C:\ProgramData\Rigbay` ACL.** The same code ran on scratch folders, including the layout with a parent named `Rigbay` (H6) and the non-elevated planting attempts, but the real folder was deliberately not created.
- The installed app (`install.ps1` into `C:\Program Files\Rigbay`, the real Start menu entry and `Rigbay` task); the released exe was run from an extracted folder.
- Steam access and XTU: listed, details read, **not installed** (task rule); their real release zips were not downloaded by the page, only the two modules named in the task.
- Any hardware write (fan Auto/Max, Cool Booster, XTU apply): none was pressed. The fan mode box, the shortcut and "Restore Auto" rows of PredatorSense were never touched.
- Offline by **network adapter, hosts file or firewall**: not allowed here. A dead-proxy environment variable of one process was used instead; it makes the connect fail the same way (`Offline`) but a real DNS failure, a captive portal or TLS interception were not seen.
- A real 64 MB download, a slow real connection, a GitHub outage (HTTP 5xx), an antivirus scanner holding a file during the swap, a full disk.
- 150 % and 200 % scale, high contrast, a screen reader (E2E-03 was found by UI Automation, not by Narrator), Windows 10, a non-admin account, Vietnamese in the flow.
- Real mouse and keyboard clicks: the driver presses controls through UI Automation (Invoke, Toggle, Expand, Value), the same way assistive technology does.
- That the 10-second recording matches the sensors' true values (they are the module's reads; only their plausibility and the file's format and timing were checked).

## 9. Release 0.1.1 (task V011, 2026-11-02)

The defects E2E-03, -04 (already on main), -06, -07, -08 and -09 are fixed in `v0.1.1`; E2E-02 and E2E-10 belong to modules (E2E-10 is fixed in the PredatorSense source on `main` by PS011, released separately; E2E-02 is still open). Each fix has a test or a self-test
check and was rendered in dark and light and looked at (the Modules page while a module downloads, with a removal queued; a module page with a long path in the Folder box at 700, 1240 and 1800 px).
Totals before packaging: `dotnet build Rigbay.sln --no-incremental` 0 warnings, 0 errors; `dotnet test`: Core 629 (626 + 3), PredatorSense 121 (103 + 18 from PS011), Recorder 146, XTU 190, Steam 248, LangCheck 19, 0 failed;
`tools\selftest.ps1` 136 of 136 PASS (133 before the last 3 text-width checks; 130 on 0.1.0 + E2E-03). The verification of the zip itself and of the released asset is in the release notes of `v0.1.1`.

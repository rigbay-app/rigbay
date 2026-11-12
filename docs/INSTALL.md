# Install, update and remove modules (task R1)

The Modules page installs a module from a release zip on GitHub, updates it, removes it and rolls it back, from inside the app. This file is how it
works, what it protects and what it does not (section 9), and what was verified (section 10). The list of modules is `docs/REGISTRY.md`; the page is
described in `docs/APP.md`; the design decision is the paragraph "Modules are published as release assets" in `docs/DESIGN.md`.

## 1. In one paragraph

A module repository publishes a GitHub release whose asset is a zip. The `rigbay-app/registry` repository lists that zip with its SHA-256, size, host API
range and what the module declares it uses. The app reads that list (only when you ask, or once a day if you switched that on), downloads the zip over
HTTPS from GitHub only, **compares the SHA-256 before it unpacks anything**, unpacks into a staging folder with every path trick refused, checks that
the `module.json` inside is the module the list described, and parks it for the next start. At the next start, before the loader runs, a small file of
pending changes is applied by renaming folders. The module is installed **turned off**; the Modules page shows what it declares and the existing turn-on
dialog asks before it ever runs.

## 2. Where the code is

| What | Where |
| --- | --- |
| Everything that does not need a window | `src/Rigbay.Core/Install/` (namespace `Rigbay.Core.Install`) |
| Registry model and strict reader | `Registry.cs` (`RegistryReader`, `RegistryDeclares`, `RegistryVersion`) |
| Rules for addresses, sizes, hosts | `InstallPolicy.cs` |
| Download (HTTPS, manual redirects, caps, hash while writing) | `SafeDownloader.cs` |
| Unpack (zip-slip, devices, case, links, bombs) | `SafeZip.cs` |
| Admin-only folders | `AdminFolder.cs` (`AdminFolderSecurity`, `IFolderSecurity`) |
| Fetch, ETag, cache, offline | `RegistryClient.cs` |
| Stage and queue; queue a removal or a roll-back | `ModuleInstaller.cs` |
| The swap at start, crash recovery, commit | `PendingOperations.cs` (`PendingRunner`, `PendingStore`) |
| What the page needs (rows, states, progress, cancel) | `ModuleManager.cs` |
| The page, the start-up call, restart, daily check | `src/Rigbay.App/Ui/ModulesPage.cs`, `Services/InstallServices.cs`, `MainWindow.xaml.cs` |

## 3. Folders and files

Everything the installer works in is **next to** the modules folder, never inside it (the loader treats every folder in the modules folder as a module).
For the default modules folder `%ProgramData%\Rigbay\modules`:

| Path | Content |
| --- | --- |
| `%ProgramData%\Rigbay\modules\<id>\` | The installed modules (what the loader reads). |
| `%ProgramData%\Rigbay\modules.install\downloads\` | The zip being downloaded. Emptied at every start. |
| `...\staging\<op>\<id>\` | A zip being unpacked and checked. Emptied at every start. |
| `...\ready\<id>\` | A verified module waiting for the next start. |
| `...\previous\<id>\` | The version that was replaced or removed, kept until a healthy start (section 6). |
| `...\trash\` | A folder being thrown away after a rename. Emptied at every start. |
| `...\registry\registry.json` and `registry.meta.json` | The last good registry and its ETag and time. |
| `...\pending.json` | The changes waiting for the next start. |
| `...\last-run.json` | What the last start applied (the page shows it once). |

The modules folder, the install folder and, when it is the parent and is called `Rigbay`, `%ProgramData%\Rigbay` itself are given a **protected DACL with
FullControl for BUILTIN\Administrators and NT AUTHORITY\SYSTEM and nothing else**, inherited by everything below (`AdminFolderSecurity`). A folder that
already exists must be owned by Administrators or SYSTEM (Rigbay tries to take it over when it is not, and refuses with a message when it cannot) and
must not be a link. The DACL is read back after it is set and the installer refuses to go on if any other account could write. The registry cache is in
this folder on purpose: a copy in `%LOCALAPPDATA%` could be edited by a program running as the same user without elevation, which would be a way to
plant a module.

Nothing is created before the first install, registry check or a start that finds an install folder; a user who never installs a module has the same
folders as before.

## 4. What happens when you press Install

1. **Before any request:** the module is not installed, no change to it is waiting, the release fits this host API (`hostApi` of the registry entry, revision included), and
   the zip address is `https://github.com/<owner>/<repo>/releases/download/<tag>/<file>.zip` of the entry's own repository. One change runs at a time.
2. **Download** to `downloads\<op>.zip`, HTTPS only, from `github.com`; at most 3 redirects, each only to `github.com`, `objects.githubusercontent.com` or
   `release-assets.githubusercontent.com` over HTTPS (a redirect anywhere else, to plain HTTP, or a fourth one ends the download). Nothing is sent but
   `User-Agent: Rigbay-Installer` (no version, no machine name), and `If-None-Match` for the registry. No cookies, no credentials. The file is hashed and
   counted while it is written, and the download stops at **64 MB** whatever the server says; a server that announces another size than the registry,
   sends fewer bytes (`Incomplete`), more bytes, or goes quiet for 30 s is refused. The partial file is deleted on every failure and on cancel.
3. **SHA-256** of the file equals the registry value, or the install ends here (`HashMismatch`), before the zip is opened.
4. **Unpack** into a new staging folder named like the module (section 5).
5. **Check the manifest** inside: valid by the loader's own reader; same `id` and `version` as the registry entry (said as "the zip holds module X, the
   registry entry is Y"); its `hostApi` range contains this host's API (revision included: "needs host API 0.1.3 or newer, this is 0.1.2"); and what it declares (`requires.admin`, `network`, `registry`, `services`,
   `probePaths`) is **equal to or narrower than** the entry's `declares`. Anything wider ends the install (`DeclaresWider`, with what was extra).
6. **Park:** `staging\<op>\<id>` is renamed to `ready\<id>`, a hash of the whole tree is recorded, and the change is written to `pending.json`. The modules folder
   has not been touched. The page shows **Restart required** (an amber bar with Restart now, a dot on Modules in the pane); the module's row says it
   appears after the restart, turned off. **Undo** takes the change back and deletes the staged files.
7. **Restart now** starts a new copy of the program (elevated, as this one is) with `--wait-pid <this pid>`, then quits the way Quit does. The new process waits
   for the old one to end (up to a minute), takes the single-instance slot, applies the pending changes (section 6) and starts. Switches that drive one run
   (`--script`, `--shot`, `--selftest`, `--exit-after`, ...) and `--tray` are not carried over. If the new process cannot be started the page says why and
   the app keeps running.

**Update** is the same with a release newer than the installed one; the old files stay where they are until the swap. **Remove** and **Go back to
<version>** only queue a change. A module whose update asks for more than the installed version declared (admin, a port, a registry key, ...) shows what it
asks for in a dialog before the download, and is **turned off and forgotten as accepted** by the swap, so it cannot start with more rights than the user
agreed to; the page says so after the restart.

Failure states in the row (all in English and Vietnamese): offline; list unreachable; list not valid; not in the list; no release for this host API;
already installed; a change already waits for a restart; busy; address not allowed; download failed, too large, incomplete or the wrong size;
**hash mismatch**; not a zip; unsafe zip; the module in the zip is not the one the list describes; it declares more than the list says; the folder could not
be made admin-only; the change could not be completed. The reason is the localised sentence; the exact English detail is the row's tooltip and the log.

## 5. The zip

A release zip has `module.json` at its top level next to the module's files (`docs/ABSTRACTIONS.md` section 2); no extra top-level folder, no
`Rigbay.Abstractions.dll`. `SafeZip` reads all names first and writes nothing until every one is acceptable. It refuses:

- absolute paths, drive paths (`C:\x`, `C:x`), UNC, a leading `/` or `\`, any `.` or `..` segment, empty segments;
- `:` anywhere (alternate data streams), `< > " | ? *`, control characters, a segment that ends in a dot or a space, a segment longer than 120
  characters or a path longer than 200;
- Windows device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1..9`, `LPT1..9` and the superscript forms, with or without an extension);
- 8.3 short names (`PROGRA~1`, `MODULE~1.JSO`) that could alias another file;
- two entries that differ only by case, the same name twice, a file and a folder with one name;
- a symbolic link (the Unix mode in the entry) or the Windows reparse-point attribute, and, after writing, any link found in the staging folder; a staging
  folder that is itself a link;
- more than 5000 entries, more than **256 MB** unpacked (counted from the headers first, then again while writing), a folder entry with content.

Backslashes are accepted as separators (old tools write them) and checked after conversion. Deleting anything in Rigbay's install folders removes a link as a
link and never walks into what it points to.

## 6. The swap at the next start

`PendingRunner.Run` is called from `Program.Main` before `Application.Start`, so before the host exists and before any module code is loaded. It
(1) makes the folders admin-only again, (2) empties `downloads`, `staging` and `trash`, (3) applies each change in `pending.json`, (4) deletes any
`ready\<id>` that no change refers to, (5) writes `last-run.json`. After the modules have started, `PendingRunner.CommitHealthy` deletes `previous\<id>`
for every module whose installed copy loaded fine (Off, Running or Unsupported; it is also deleted when the module is gone after a removal).

The runner decides from **what is on disk**, not from a journal, so it can be started again after a crash at any step. Each change is a few renames; the
order is chosen so that every state between two steps is one the next run recognises:

| Change | Steps (each a rename or a file edit) | If it died after step... |
| --- | --- | --- |
| install | 1 turn the id off and forget its approval (`host.json`, `app.json`); 2 `ready\<id>` -> `modules\<id>` | 1: the module is simply off, `ready` is still there, step 2 runs. 2: `ready` is gone, the change is done. |
| update | 1 turn the id off and forget the approval, only if it declares more than before; 2 delete a stale `previous\<id>`, `modules\<id>` -> `previous\<id>`; 3 `ready\<id>` -> `modules\<id>` | 1: nothing moved yet. 2: `modules\<id>` is missing, `previous` and `ready` are complete, step 3 runs (the old manifest is read from `previous`). 3: `ready` is gone, done. |
| remove | 1 turn off and forget; 2 delete a stale `previous`, `modules\<id>` -> `previous\<id>` | 2: `modules\<id>` is missing, done. |
| roll back | 1 turn off; 2 `modules\<id>` -> `trash\...`; 3 `previous\<id>` -> `modules\<id>`; 4 delete the trash | 2: `modules\<id>` is missing and `previous` is intact, step 3 runs. 3: `previous` is gone, done (the trash is emptied at the next start). |

A change is removed from `pending.json` only after it is applied. `PendingRunnerTests` kills the runner at **every one of these steps and at the two points
around forgetting the change**, for all four kinds, starts it again (twice), and checks the final state; a loop that crashes three starts in a row loses
nothing.

Failures: a rename that fails because something holds a file (a scanner, a second process) is retried 6 times 250 ms apart, then **kept for the next
start** (`WillRetry`, the page says so); a change that is wrong (the staged files changed after they were verified: the recorded tree hash differs, the id
is already taken, the folder is a link) is dropped, its staged files deleted and the reason logged and shown. A `pending.json` that cannot be read is
moved aside as `pending.json.corrupt-<time>` and nothing in it is trusted; entries with a bad id or an unknown kind are ignored.

**Never overwrite a running module's files.** Windows lets a folder be renamed while the DLLs in it are loaded (the self-test showed it), so this is not
left to the file system: the swap runs only before the loader, and `PendingRunner` also refuses any change to a module whose load context (`module:<id>`) exists
in the process (the change is kept for the next start). Queueing a change never touches the module's folder (the self-test compares a hash of the folder
before and after).

**Keep the previous version for rollback.** After an update or a removal the old folder is in `previous\<id>`. When the module that is installed now is
Invalid, Incompatible or Faulted after the start, the folder is kept and its row offers **Go back to <version>** (a queued change like any other). When it is healthy the
folder is deleted. Removing a module keeps its settings in `%LOCALAPPDATA%\Rigbay\modules\<id>`.

## 7. The registry fetch

`https://raw.githubusercontent.com/rigbay-app/registry/main/registry.json`, from `raw.githubusercontent.com` only (a redirect elsewhere is refused). It is read
when the user presses **Check for modules** (the same button in the page header and in the empty list), and once a day if **Check for modules every day** (Settings; **off by
default**) is on: at the start and then every hour the app asks whether the last attempt is more than a day old. Nothing is requested in the background
otherwise. A good answer is validated (`RegistryReader`, any problem makes the whole file invalid), cached with its ETag, and the next request is conditional
(a 304 keeps the cache). If the network fails the page shows the cache and says **why** (offline: the connection could not be made; unreachable: the server
answered with an error or timed out; not valid: the new file was refused and the old copy is shown) and when the list was saved. With no cache and no
network the page says that and offers Try again. A cache that fails validation when it is read is ignored. The cache is keyed to the address it came from.

The app has **no setting for another registry address** and the product build has no way to change it. Why: the registry is the trust anchor, one list
that maintainers review; a user-defined list would let a web page, a chat message or a "how to install X" guide move that trust to a stranger, and a module
runs as administrator. There is one hidden test switch, `--registry <file or url>`: it is honoured only together with `--data` and `--modules` and only when
the modules folder is **not** `%ProgramData%\Rigbay\modules` (otherwise it is ignored and the log says so); it allows `file:` and loopback `http:` sources
and any zip address. Anyone who can start Rigbay with arguments as administrator already controls the machine; the switch exists to render and test the
page with a local registry.

## 8. Test switches and tools

| Switch or script | Use |
| --- | --- |
| `--registry <file or url>` | A local registry (scratch `--data` and `--modules` only). |
| `--slow-download <ms>` | With `--registry`: wait this long before each 16 KB read of a local file, so progress and cancel can be seen. |
| `--first-run` | Behave as on the first start without modules (opens the Modules page). Without it, any of `--shot`, `--script`, `--page`, `--selftest`, `--exit-after` keeps the dashboard. |
| `--wait-pid <pid>` | Set by Restart now; the successor waits for that process. |
| script steps | `check`, `install:<id>`, `install-bg:<id>` (not awaited), `update:<id>`, `remove:<id>`, `rollback:<id>`, `undo:<id>`, `cancel-install`, `restart`. |
| `tools\make-local-registry.ps1` | A scratch modules folder and a local registry from the demo modules: an update, an installed one, an installable one, one with a wrong hash, one for another host API. `-BaseUrl` addresses the zips by HTTP. |
| `tools\serve-registry.ps1` | Serves that registry on `http://127.0.0.1:<port>` (ETag, a slow zip) for a real-HTTP check. |
| `tools\shots-install.ps1` | The Modules page in every state, dark and light, English and Vietnamese, into `tools\shots\install-*.png`. |
| `tools\install-http-test.ps1` | Install over real HTTP on loopback, the start-up swap, offline with a cache, and `icacls` of the real folders (`tools\shots\install-http-*`). |
| `tools\restart-test.ps1` | Restart now with two real elevated processes (`tools\shots\restart-test.txt`). |
| `tools\remove-elevated.ps1` | Deletes the admin-only scratch folders (only under `tools\out`). |
| `tools\selftest.ps1` | Runs the install checks (section 10) on a scratch copy of the demo modules. |

These scripts only write under `tools\out` and `tools\shots`; none of them touches `%ProgramData%\Rigbay`.

## 9. Threat model

What a module is: code that runs inside Rigbay, as administrator, with the user's rights and no sandbox. Installing one is giving it your machine.
**The registry and the hash are the trust anchor**, not the installer's checks: the installer makes sure that what runs is exactly what the registry
promised, and the registry's reviewers promise that it is what its repository published.

Protected:

- **A different file than the one listed**: tampering in transit, a compromised mirror or CDN edge, a replaced release asset. The zip is not opened until
  its SHA-256 equals the registry value; the address must be a release download of the entry's own repository over HTTPS; redirects only to GitHub's
  asset hosts; size and download time are capped.
- **A zip that attacks the unpacker**: path traversal, drive and device names, streams, short names, case collisions, links, bombs (section 5).
- **A zip that is not the module the list said**: id, version, host API range and declarations are compared with the entry; the declarations may be narrower,
  never wider.
- **Another account on the machine**: the modules folder, the install folder, the staged files, the pending file and the registry cache can be written only by
  Administrators and SYSTEM, the owner is checked, links are refused, and the staged files carry a tree hash that is checked again right before the swap.
  A process that is not elevated cannot plant a module, change the list that was cached or swap what was verified.
- **A change that widens what a module may do without the user knowing**: an update that declares more is turned off until accepted again; a new module
  starts off; the registry's declarations are shown before the download and the manifest's before the first turn-on.
- **A crash or a power cut in the middle**: section 6.
- **Privacy**: no telemetry, no identifier, no cookies; the only requests are the registry file, the zip, and their redirects.

Not protected (and what to do about it):

- **A malicious or mistaken module that the registry lists.** `declares` is information, not enforcement. The reviewers of `rigbay-app/registry` pull
  requests are the defence; users read what is declared and the source link before installing. A module can be removed from the list (yanked); installed
  copies are not removed by that.
- **The accounts that publish.** Anyone who can merge to the registry's `main`, or who controls a module repository's release that a maintainer approved
  at a hash, decides what users install. The CI check refuses a changed hash for a published release, but that is a check on pull requests, not a signature.
- **GitHub and TLS.** The list is trusted because it comes from `raw.githubusercontent.com` over a TLS connection validated by the Windows certificate
  store; there is no pinning and no signature on the registry file. A stale list (a cache, or a server that serves an old copy) can only offer an older release
  that was in the list once; it is still hash-checked.
- **Code signing.** The module's DLLs are not Authenticode-checked.
- **Something already running as administrator.** It can do anything, including editing these folders.
- **A user who passes `--registry`** with scratch folders (section 7).
- **The module's own data.** Removing a module keeps its settings; they are not touched by install or update.

## 10. Verification (2026-11-01, this machine, Windows 11 10.0.26300)

- `dotnet build Rigbay.sln --no-incremental`: 0 warnings, 0 errors.
- `dotnet test Rigbay.sln`: **Rigbay.Core.Tests 600 passed** (343 before this task; 257 are new), PredatorSense 102, XTU 190; 0 failed.
  New tests, all with a fake HTTP handler or local files, no real GitHub download: the registry reader (every rule, duplicates, ordering, host API
  selection, declarations covering), the policy (zip address, repo, redirect hosts), **34 parity tests** over a copy of the registry repository's fixtures
  (the host and the pull request check reach the same verdict on each file), the zip (good zip, zip-slip with 30 spellings, absolute, drives, streams,
  devices, trailing dots, short names, case collisions, a file and a folder with one name, symbolic links, a reparse attribute, a junction as staging
  folder, too many entries, a bomb by header, a header that lies about the size), file operations (a junction is removed as a link and its target kept, read-only
  files, the tree hash), the installer (good install, redirect to an allowed host, to another host, to HTTP, three redirects and a fourth, wrong hash, one flipped
  byte, wrong size, too large, partial download, stall, cancel, offline, 404, zip-slip with the right hash, case collision, over the unpacked limit,
  manifest with another id, another version, another host API range, wider declarations, narrower declarations, already installed, already pending, busy,
  update to a newer release only, update of a missing module, remove, roll back, undo), the registry client (cache and conditional request, no identifier
  in the request, offline with and without a cache, server error, invalid file, redirect to another host, edited cache, cache for another address, huge file),
  the manager (every state of the list, offline from the cache, update available marks, failure remembered, cancel is not a failure, one change at a time,
  remove and undo), the swap (all four changes, other modules' settings kept, an update that asks for more is turned off, a staged file edited after verification,
  a file added, an id taken, a module folder that is a link, a file held open and then released, folders that cannot be made safe, leftovers cleaned, a corrupt
  `pending.json`, a loaded module is not touched, **a simulated crash at every step of every change, then two more starts**, a crash loop), an end-to-end test
  (release zip of the sample module -> installer -> swap -> the real loader finds it, off, not enabled), and the DACL on scratch folders (exactly two entries,
  no inheritance, read back; run unelevated for the DACL and **elevated** for the owner check: 4 of 4 passed in an elevated shell).
- `tools\selftest.ps1` (elevated): **129 of 129 PASS**, 16 of them install checks on a scratch copy of the demo modules and a local registry, through the real page
  controls pressed with UI Automation: the list and its states, the Install button only where it belongs, progress and Cancel while downloading, cancel
  leaves nothing, a wrong hash is refused and said so, Install queues and the modules folder is untouched, Restart required with its button and the dot in the
  pane, an update is queued without touching the running module's folder, the swap puts the new module in place and **refuses the one that is loaded**, undo,
  **the real DACL of the scratch modules folder, install folder and installed module (only Administrators and SYSTEM)**, remove queued and undone.
- `tools\cli-test.ps1`: 0 failed; `--langcheck`: host 209/209 keys translated (en, vi), demo modules ok.
- `tools\install-http-test.ps1`: a real HTTP server on loopback: first request 200 with only `Host` and `User-Agent`, second request conditional (**304**), the zip
  downloaded in slices and verified, queued; the next start applied it ("install demo-lighting: applied") and the page said so; with the server gone
  the page said offline and showed the saved list; `icacls` from an elevated shell: modules folder and install folder `SYSTEM:(OI)(CI)(F)` and
  `BUILTIN\Administrators:(OI)(CI)(F)` only, the installed module and the cache the same, inherited (`tools\shots\install-http-icacls.txt`).
- `tools\restart-test.ps1`: two real elevated processes: process 1 installed, logged "restarting", quit; process 2 started with `--wait-pid`, applied the
  install before the loader ran and discovered the module (`tools\shots\restart-test.txt`).
- PNGs looked at, dark and light unless noted: `install-modules-*` (list with every state, progress, failed, restart required, details, Vietnamese, narrow 640),
  `install-modules-welcome-*` (first run), `install-modules-offline-none-dark`, `install-http-*`, `install-settings-install-dark`.
- The registry repository `rigbay-app/registry` was created and its CI run is green (section 6 of `docs/REGISTRY.md`).

### Not verified

A real download from GitHub (the real redirect to `release-assets.githubusercontent.com`, real TLS, a real 64 MB asset, a proxy); the real
`%ProgramData%\Rigbay` (the DACL logic was verified on scratch folders under `tools\out` with the same code, elevated; that `Rigbay` itself is hardened as the
parent is only unit-tested by name); a UAC prompt on "Restart now" (this account auto-elevates); installing from a shell that is not elevated (the app always is);
a disk that is full; an antivirus scanner holding a file during the swap (a held file is tested, the retry timing on a real scanner is not); 150 % and 200 %
display scale and high contrast on the new page; a screen reader and keyboard-only use of the new controls (the self-test presses the buttons through UI
Automation); more than about 10 registry entries on the page; Windows 10; the "go back" button on a module that really failed after an update (the Core path is
tested, the page path was not driven); the pull-request-only step of the registry workflow (it runs only on a pull request; the same comparison is unit-tested
and was run by hand with `--base`); a module that is updated while the user has its page open.

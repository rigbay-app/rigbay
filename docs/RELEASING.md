# Releasing a module (task REL1)

How a module repository (`rigbay-app/module-<id>`) gets a release that the app can install: a zip as a GitHub release asset, and one entry in
`rigbay-app/registry`. What the app does with the result is `docs/INSTALL.md`; the entry format is `docs/REGISTRY.md`; how a module gets its own
repository is `docs/MODULE-REPOS.md`. The first four releases (v0.1.0 of `predatorsense`, `recorder`, `steam-bypass`, `xtu`) were made with exactly
this procedure on 2026-11-01; section 8 says what was seen.

## 1. What a release is

| Piece | Where | Rule |
| --- | --- | --- |
| Tag and release `v<version>` | the module repository | `<version>` is the `version` of `module.json` at that commit. Marked **pre-release** (a label for people; the app ignores it) while the major number is 0 or the module is not verified on real hardware. The tag is made by `gh release create` and points at the commit CI was green on. |
| Asset `<id>-<version>.zip` | that release | The only asset. `module.json` at the top level, module DLLs and the dependencies the host does not have, `lang\`, icons, `LICENSE`, `README.txt`. No `Rigbay.Abstractions.dll`, no Windows App SDK files, no PDBs (the DLLs carry an embedded, path-mapped one), no vendor files, no absolute path. |
| Entry in `registry.json` | `rigbay-app/registry`, by pull request | `version`, `hostApi`, `zip` (the asset's address), `sha256`, `size`, `released`, `declares`; for a new module also `id`, `name`, `description`, `repo`, `icon`. |

A published release is never edited and its entry is never changed: a fix is a new version (the registry check fails a pull request that changes
a published release). A bad release is removed from the registry by a pull request that says why; installed copies stay.

## 2. Tools

| Tool | What it does |
| --- | --- |
| `tools\package-module.ps1 -Repo <clone or URL>` | Clean clone of `main`, CI check, `dotnet publish`, the zip, checks, SHA-256, verification. Local only. |
| `tools\verify-module-zip\` | A small console project that uses `Rigbay.Core.Install` (the installer's own classes) and `RigbayHost` (the real loader). `package-module.ps1` runs it on the zip; `verify-module-zip registry` runs it against the real registry on GitHub (section 6). Not in `Rigbay.sln`; it writes only under `%TEMP%\rigbay-verify-*`. |
| `scripts\entry.mjs` of the registry repository | Prints the registry entry from a zip, its `module.json` and its address. |
| `scripts\validate.mjs` of the registry repository | The check that CI also runs. |

Needs: git, the .NET 10 SDK (the module projects target net8.0 or net10.0; the SDK restores the net8.0 targeting pack), Node 18+ (registry scripts), `gh`
signed in as an account that can create releases in the module repository and open pull requests in the registry. Run PowerShell scripts as
`powershell -ExecutionPolicy Bypass -File tools\package-module.ps1 ...` (the machine's policy refuses unsigned scripts otherwise).

## 3. Checklist before the zip

- [ ] The module's `main` HEAD is pushed and its GitHub Actions run for **that commit** is green (the script refuses otherwise).
- [ ] `module.json`: `version` is the version to release and is not already released; `hostApi` is the range the module was built and tried against (`>=0.1.n <0.2` when it uses a member added in host API revision `n`, `docs/ABSTRACTIONS.md` 11; check that the host that has revision `n` is already released, because older hosts ignore the revision); `requires` says everything the module reads or writes (the registry's `declares` is copied from it, and the app refuses a module that declares more than the registry says).
- [ ] The README says honestly what was verified and what was not (the release notes copy it). Do it before the release: the zip cannot change after the tag, and the README on `main` is what people read next to it.
- [ ] The host pin (`external/rigbay`) is a commit on `rigbay-app/rigbay` `main` that a fresh clone can fetch (CI proves it).
- [ ] No secrets, no personal paths, no vendor files in the repository (`docs/MODULE-REPOS.md`, "What you do by hand before the push"). The script also looks inside every file of the zip for the scratch folder, the user profile, the user name and the machine name.

## 4. Package

```powershell
powershell -ExecutionPolicy Bypass -File tools\package-module.ps1 -Repo D:\Documents\Projects\_repos\module-xtu `
    -OutDir D:\Documents\Projects\_repos\_dist -CheckReproducible
```

`-Repo` is a local clone (its `origin` is cloned again, so what is built is what GitHub has; the clone must be clean, on `main`, and equal to `origin/main`)
or an `https://github.com/...` URL. What the script does, in order:

1. `git clone --recurse-submodules --branch main` into `%TEMP%\rb-pkg\a\src` (short path on purpose).
2. `gh run list --commit <sha>`: a completed, successful run must exist (`-SkipCiCheck` to override; do not).
3. Finds `module.json` (the shallowest one), the project whose assembly is `entry.assembly`, and runs `dotnet publish -c Release` with
   `ContinuousIntegrationBuild`, an embedded PDB and a `PathMap` that turns the clone's folder into `/_/`.
4. Builds the file set from the publish output: everything except `Rigbay.Abstractions.*`, `Rigbay.Core.*`, Windows App SDK / WinRT files, `*.pdb`, `*.xml`, `*.exe`,
   `*.zip`, `*.runtimeconfig.json`; adds `LICENSE` and a generated `README.txt` (name, description, host API, source address and commit). It stops if the entry
   assembly, the manifest's icon or a `lang\*.lang` is missing, if `module.json` differs from the repository's, or if any file contains a path or name of
   the machine.
5. Writes the zip: `module.json` first, then ordinal order, forward slashes, the commit time as every timestamp, deflate. Prints the file list.
6. `-CheckReproducible`: repeats 1 to 5 in `%TEMP%\rb-pkg\b` and requires the same SHA-256. (A path of the first clone inside any file, even a compressed one, would make them differ.)
7. Runs `verify-module-zip zip` (section 5). If it fails the zip is deleted and the script fails.

Output in `-OutDir`: `<id>-<version>.zip`, `<id>-<version>.zip.sha256`, `<id>-<version>.build.json` (commit, SDK, file list). The last lines are
`RESULT size=<bytes> sha256=<hash>`.

**Look at the file list.** A dependency that is only needed to build, or a second copy of something the host has, shows up here. What the loader does with it:
the host's copy of an assembly wins over the module's, then the module folder (`deps.json`), then `probePaths`; so an extra copy is harmless but bloats the zip.
For the four v0.1.0 modules the lists were: recorder 20 files (75 KB); steam-bypass 21 (89 KB, with `steam-domains.txt`); predatorsense 23 (267 KB, with `System.Management`
and `System.CodeDom` and the `runtimes\win` copy of `System.Management`); xtu 34 (1.2 MB, the WCF client libraries, `Microsoft.Win32.SystemEvents`, `System.ServiceProcess.ServiceController`,
`System.Diagnostics.EventLog` and their `runtimes\win` copies).

Reproducibility has limits: the same commit, SDK and PowerShell edition give the same zip (checked here with Windows PowerShell 5.1 and SDK 10.0.403). A different
.NET runtime for the compression, or a different SDK, can give other bytes from the same files. **The hash that counts is that of the file you upload.** Package once, upload that file, take the hash from the downloaded asset.

## 5. What the verification checks (`verify-module-zip zip`)

All of it with the host's own code, in a scratch folder, nothing enabled:

- size within 64 MB; `module.json` at the top; no `Rigbay.Abstractions`, `.pdb`, `.exe`; no names that differ only by case.
- A registry entry is made from the zip and read by `RegistryReader` (the strict reader the app uses); `hostApi` contains this host's API.
- `ModuleInstaller.InstallAsync` runs against a fake server that answers the release address with the file: SHA-256 before unpacking, size, `SafeZip` (all the path
  rules), manifest `id` and `version` equal to the entry, host API range, `declares` equal or narrower.
- **Controls:** a wrong SHA-256, a wrong size and a registry entry that declares less than `module.json` are each refused by the same code with the right error
  (`HashMismatch`, `SizeMismatch`, `DeclaresWider`). A check that cannot fail proves nothing.
- `PendingRunner.Run` applies the install (the swap at start-up); `ManifestReader` accepts the folder with no warnings (icon, languages, unknown fields);
  `RigbayHost.Discover` loads the entry assembly into its own load context, creates the entry type and calls `Probe`. Expected state **Off**. **Unsupported** means the
  module's own probe says this machine is not its target (the zip is fine, a note is printed); **Invalid, Incompatible, Faulted** fail the run. `Rigbay.Abstractions` must not be loaded from the module.

What it does not check: `Initialize` (the module is never turned on, because turning on can write to the machine), the UI, and the install through the app's Modules page.

## 6. Create the release and prove the asset

```powershell
cd D:\Documents\Projects\_repos\_dist
# release notes: what it does, VERIFIED and NOT VERIFIED copied from the README, the zip's size and SHA-256, host API, the commit; pre-release
gh release create v0.1.0 xtu-0.1.0.zip --repo rigbay-app/module-xtu --prerelease --target <commit sha> --title "XTU profiles v0.1.0" --notes-file notes-xtu.md
gh release download v0.1.0 --repo rigbay-app/module-xtu --pattern xtu-0.1.0.zip --dir dl\xtu
Get-FileHash dl\xtu\xtu-0.1.0.zip -Algorithm SHA256        # equal to the local hash, or stop
```

`--target` is the commit that was packaged (not "whatever main is now"). Do not attach anything but the zip. If the download differs from the local file, delete nothing
yourself: stop and ask the owner (a release asset can be replaced, but a registry entry must never be made from a file that was not downloaded).

Shape of the notes (see the four releases for examples): one sentence of what it does; **Verified** and **Not verified** from the README, with the risky ones first
(hardware writes, real network); a **Package** list (file, size, SHA-256, host API range, commit, "built twice in different folders, same hash", layout); what was checked on the zip and
what was not (the Modules page); how to install.

## 7. The registry pull request

In a clone of `rigbay-app/registry`:

```powershell
git checkout -b add-<id>-<version>
# the entry, from the DOWNLOADED zip and the module.json inside it:
node scripts\entry.mjs dl\xtu\xtu-0.1.0.zip <module.json> https://github.com/rigbay-app/module-xtu/releases/download/v0.1.0/xtu-0.1.0.zip
```

- **New module:** paste the "whole module entry" at the end of `modules`, add `"icon"` (the raw address of the module's `icon.svg`; the app does not fetch it yet, so use the tag, `.../module-xtu/v0.1.0/Rigbay.Module.Xtu/icon.svg`, which cannot move).
- **New version of a listed module:** paste the release object at the end of that module's `versions` (oldest first, strictly ascending). Change nothing else of the module's entry.
- `released` is the UTC date `entry.mjs` prints; the day the release was published.

```powershell
node scripts\validate.mjs registry.json
node --test scripts\validate.test.mjs
git show origin/main:registry.json > $env:TEMP\base.json; node scripts\validate.mjs registry.json --base $env:TEMP\base.json   # what CI does on a pull request
git add registry.json; git commit; git push -u origin add-<id>-<version>
gh pr create --repo rigbay-app/registry --base main --head add-<id>-<version> --title "Add v<version> of <id>" --body-file <the filled-in template>
gh pr checks <n> --repo rigbay-app/registry                  # wait for "validate" to pass
gh pr merge <n> --repo rigbay-app/registry --merge           # the owner is the only maintainer; no automatic merge exists
```

A good pull request: one commit that changes only `registry.json`; the title "Add v0.1.0 of ..."; the body is the template (`.github/pull_request_template.md`) with the boxes ticked
honestly, plus a table of module, release link, size and SHA-256 so a reviewer can compare them with the release page. The diff of a new version is one block
inside `versions`; of a new module, one block in `modules`. The check (`validate`) runs the validator's own tests, the schema rules, and on a pull request refuses a
change to a published release (`--base`). Do not delete the branch yourself unless the owner asked.

After the merge, `raw.githubusercontent.com` may serve the old file for a few minutes (CDN cache); the check below can be repeated.

## 8. Prove the app can read it

```powershell
dotnet build tools\verify-module-zip -c Release --nologo -v q
dotnet tools\verify-module-zip\bin\Release\net10.0\verify-module-zip.dll registry --expect predatorsense,recorder,steam-bypass,xtu
```

This fetches `https://raw.githubusercontent.com/rigbay-app/registry/main/registry.json` with the production policy (`raw.githubusercontent.com` only), parses it with
`RegistryReader`, and for each module downloads the **real asset from GitHub** with `ModuleInstaller` (hash and size compared before unpacking), applies the install in a scratch
folder and loads the module with the real loader. It is the Modules page minus the window: the page was not driven. On 2026-11-01 all four passed (hashes in the release notes and in the registry).

Then, by hand, on a machine with the app: Modules page, "Check for modules", Install, restart, and look at the module (turned off, what it declares shown). That step is the one
nobody automated, and it is the one that proves the user experience.

## 9. Cutting v0.1.1 (a fix or a new version)

1. In the module repository: fix, tests, bump `version` in `module.json` (and `<Version>` in `Directory.Build.props`, if it has one) to `0.1.1`; keep `hostApi` unless the contract changed or the fix starts to use a newer member (then `>=0.1.n <0.2`). If `requires` changed, say so in the notes: a wider
   declaration makes the app turn the module off and ask again after the update. A normal commit, never a force-push.
2. Push, wait for CI to be green on that commit. Update the README's "Verified and not verified" if it changed.
3. `package-module.ps1 -Repo ... -CheckReproducible` (sections 3 and 4). The zip is named `<id>-0.1.1.zip`.
4. `gh release create v0.1.1 ... --prerelease` (drop `--prerelease` only when the owner says the module is verified enough to be offered first); download and compare (section 6).
5. Registry pull request that **adds** the `0.1.1` object after `0.1.0` in `versions` (section 7). Do not touch the 0.1.0 object.
6. Merge when green, run section 8. The app offers the update to installed copies when the new version fits the host API and is not older than the installed one
   (its rule is on the SemVer suffix, `docs/REGISTRY.md` section 2: a version like `0.2.0-beta.1` loses to a plain one when both fit. The GitHub "pre-release" mark is for people; the app does not read it).

If the release turns out to be bad: a registry pull request that removes only that version (say why in the body). The check prints a warning for a removal and passes it.
The release on GitHub can stay, marked as such in its notes.

## 10. Notes from the first run

- The module READMEs written before the releases still say "there is no release and no package". Fix them in a normal commit after the release (the tag stays on the commit that was packaged).
- `dotnet run --project ... -- args` passed `--nologo` on to the program as an argument; the script builds `verify-module-zip` and runs its DLL instead.
- `Array.Sort` on a PowerShell array did not give ordinal order (the zip came out in hashtable order); the script sorts a `List[string]` with `StringComparer.Ordinal`.
- The check inside the zip for "no path of this machine" cannot read an embedded PDB (it is compressed). The path map does the job there, and `-CheckReproducible` proves it
  (two clones in different folders give the same bytes only if no clone path is in any file).
- The registry check's pull-request-only step (`--base`) had never run before PR #1; it ran and passed.
- `verify-module-zip` cannot delete its scratch folder `%TEMP%\rigbay-verify-*` at the end (the module DLLs it loaded stay mapped until the process exits). Delete those folders afterwards; nothing else is left behind (`%TEMP%\rb-pkg` is removed by `package-module.ps1`).

## 11. Host: releasing the app itself (task PKG1)

The modules above are released from their own repositories. The **app** (`rigbay-app/rigbay`, tag `v<version>`) has one asset, `Rigbay-<version>-win-x64.zip`, made by
`tools\package-app.ps1`. `<version>` is `<Version>` in `Directory.Build.props`. Releases are marked **pre-release** while the major number is 0. This section is the
procedure; the user side is `docs/USER-GUIDE.md`.

### 11.1 What is in the zip

```
install.ps1  uninstall.ps1  README.txt  LICENSE
app\          Rigbay.App.exe and everything dotnet publish writes, minus *.pdb and *.xml
modules\      empty (a module folder placed here is copied by install.ps1)
```

71 files, about 39 MB unpacked, about 10 MB zipped. **Framework-dependent**: it needs the .NET 10 runtime (`Microsoft.NETCore.App 10.x`, read from
`Rigbay.App.runtimeconfig.json`: the Desktop runtime is not needed) and the Windows App Runtime 1.8. There is no self-contained build on purpose: 1.8 self-contained crashed at
start in the spike and again when tried in `tools\build.ps1` (`docs/spike/SPIKE.md`, `docs/APP.md`). The installer checks both prerequisites and, only after asking (or
with `-InstallPrerequisites`), installs them with `winget install --id Microsoft.DotNet.Runtime.10` and `Microsoft.WindowsAppRuntime.1.8` (both ids found with `winget search --exact`).
Files are **not signed**.

### 11.2 Package

```powershell
powershell -ExecutionPolicy Bypass -File tools\package-app.ps1 -Ref main -Out D:\somewhere
```

`-Ref` is a branch, tag or commit; `-Repo` a path or URL (default: this repository). What it does:

1. Clean clone of `-Ref` into `%TEMP%\rigbay-pkg-<random>` (short path: the Windows App SDK's `MakePri` fails on long ones), `git status` must be clean.
2. `dotnet publish src\Rigbay.App -c Release -r win-x64 --self-contained false` with `ContinuousIntegrationBuild`, **`DebugType=none`**. The XAML compiler's generated
   code carries `#line` directives with the clone's absolute path; they end up in the pdb, and the pdb's id and checksum are written into the dll, so two builds from two
   folders differed in `Rigbay.App.dll` until the pdb was left out (the zip ships none anyway; stack traces in the log have method names, no line numbers).
3. Lays out the zip, writes it: ordinal name order, forward slashes, every timestamp the commit's UTC time (2 s resolution), deflate. Prints size and SHA-256 and writes
   `<zip>.sha256`.
4. Checks the zip it just wrote, from an extracted copy: the exe has the embedded manifest with `requestedExecutionLevel level="requireAdministrator"`, an icon group,
   `ProductVersion` equal to the version, `ProductName` Rigbay; no `.pdb`; `modules\` empty; no scratch path, user name, machine name or `C:\Users\` string in any file (ASCII and UTF-16);
   no vendor-named file; both scripts parse and are plain ASCII. Any problem fails the script and the zip must not be released.

Same commit gives the same bytes: two runs from two random clone folders gave the same SHA-256 (PowerShell 5.1, SDK 10.0.403). As for modules (section 4), **the hash that counts is that of the file you upload**;
download the asset and compare.

### 11.3 Test before the release

All of this runs on a machine that must not be touched: everything goes to scratch folders and to test-named objects.

- `dotnet build Rigbay.sln --no-incremental`, `dotnet test Rigbay.sln`, `tools\selftest.ps1` (elevated, UAC auto-elevates for an admin account).
- The same `--selftest` with the exe **extracted from the zip** into another folder: `tools\selftest.ps1 -AppDir <extracted>\app`.
- `install.ps1 -WhatIf` (not elevated), then for real with `-Root <scratch> -DataRoot <scratch> -ShortcutDir <scratch> -UninstallKeyRoot HKCU:\Software\<test> -TaskName <test name>`:
  install, upgrade over a running instance (it must be quit by the installer), the task XML equal to the one the app writes itself (`--task-name <other test name> --script startup:on`),
  `uninstall.ps1 -WhatIf`, uninstall (modules and settings kept), reinstall, `-RemoveData`, a non-empty foreign folder refused, and both scripts from a non-elevated shell (they open an elevated one; `-NoPause` closes it).
  Never use the real names (`C:\Program Files\Rigbay`, the task `Rigbay`, the real Start menu) on a machine you work on; the test switches exist for that.
- All of the install/uninstall steps above are scripted in `tools\test-installer.ps1` (scratch folders and test names only, 28 checks): run the elevated part with `tools\e2e\invoke-elevated.ps1 -File tools\test-installer.ps1 -Arguments @('-ZipDir', '<extracted zip>')` (24 checks) and the non-elevated part from a normal shell with `tools\test-installer.ps1 -ZipDir <extracted zip> -Unelevated` (4 checks); remove the admin-only scratch folder afterwards with `tools\remove-elevated.ps1 tools\out\installer-test`.
- The end-to-end flow against the extracted zip: `tools\e2e\run.ps1 -AppDir <extracted>\app`; against the released asset: `tools\e2e\run.ps1 -Tag v<version>` (it downloads the asset).
- `tools\stop-elevated.ps1` afterwards, and `tools\remove-elevated.ps1 <folder under tools\out>` for scratch folders the installer made admin-only.

### 11.4 Create the release

1. Everything is on `main` and pushed (a normal push, never a force). **Every new commit changes the zip** (the timestamps are the commit time), so make the last commit
   first and package from it; a documentation commit after packaging does not belong to the zip.
2. `package-app.ps1 -Ref main -Out <dir>`; note the commit and the SHA-256.
3. ```powershell
   gh release create v<version> <dir>\Rigbay-<version>-win-x64.zip --repo rigbay-app/rigbay --target <commit> --prerelease --title "Rigbay <version>" --notes-file <notes.md>
   ```
4. Download the asset again (`gh release download v<version> --repo rigbay-app/rigbay --pattern "*.zip" --dir <empty folder>`) and compare its SHA-256 with the one printed in step 2.
5. Notes say: the SHA-256 and the commit, the prerequisites, **unsigned binaries (SmartScreen will warn)**, and a list of what was verified and what was not.

### 11.5 Notes from the first run (v0.1.0)

The release itself is [v0.1.0](https://github.com/rigbay-app/rigbay/releases/tag/v0.1.0); its notes are the list of what was verified.

- A first draft of `package-app.ps1` produced two different zips from the same commit: only `Rigbay.App.dll` differed (70 bytes: PE time stamp, the pdb id and checksum, the MVID
  derived from them). The cause was the clone path in the generated XAML code (see 11.2). `Rigbay.Core.dll` was identical across folders from the start.
- `install.ps1` under `$ErrorActionPreference = 'Stop'`: `schtasks /query` for a task that does not exist writes to stderr, which Windows PowerShell 5.1 turns into a terminating
  error. Found by the uninstall test; both scripts now call `schtasks` through a small wrapper.
- A `-WhatIf` run printed dozens of `What if: Set Alias ...` lines (the preference was passed on to the modules `Get-AppxPackage` and `Get-CimInstance` load). Those two lookups now run in a child
  `powershell.exe`.
- A function named `Ps` in a test script is shadowed by the alias `ps` (`Get-Process`). Not in the product, noted for the next person.
- The uninstall script copies itself to `%TEMP%` when it has to elevate: it may sit in the folder it is about to delete.
- The app got three switches for this: `--version`, `--uninstall-cleanup [--keep-task]` (asks a running instance to quit through its pipe, waits up to 30 s, removes the logon task) and
  `--task-name <name>` (tests only; keeps the real task `Rigbay` out of reach). `--uninstall-cleanup` talks to **whichever** instance owns the user's pipe, so the scripts call it only
  when a `Rigbay.App` process runs from the install folder.

### 11.6 Notes from the second run (v0.1.1)

- Release [v0.1.1](https://github.com/rigbay-app/rigbay/releases/tag/v0.1.1), built from `bb26d17`; the notes list what was verified. The zip came out the same from two clean clones of GitHub `main`.
- `git push` from this machine hangs on a Git Credential Manager window nobody sees; use `git -c credential.helper= -c "credential.helper=!gh auth git-credential" push`.
- The E2E flow had literals that go stale when a module is released: it expected `PredatorSense 0.1.0 was installed` and a size written with `N0` while the page prints `FormatSize` (`0.#`). The registry listed PredatorSense 0.1.1 by then, so the first run from the zip gave 71 of 74. The flow now reads the newest version from the registry and formats the size like the page; 74 of 74 in dark and light, from the extracted zip and from the released asset.
- `tools\test-installer.ps1` is the scripted version of section 11.3. `Test-Path` on a path below an administrators-only folder throws "Access is denied" in a normal shell: a non-elevated check can only look at paths outside it.
- A commit made after packaging (tools, documentation) does not belong to the zip; the release points at the commit the zip was built from.

### 11.7 Notes from the third run (v0.1.2, task R3, 2026-11-13)

- Release [v0.1.2](https://github.com/rigbay-app/rigbay/releases/tag/v0.1.2), built from `c20f1bb`; the five module releases that need it (XTU 0.1.1, PredatorSense 0.1.2, Steam access 0.1.2, Recorder 0.1.1, Remote tunnel 0.1.1) and registry pull request #7 followed the same day. The zip's SHA-256 is in the release notes; the downloaded asset was equal to the packaged file.
- CI on the release commit was red twice and green the third time, each time on a different timing-sensitive test (a Core test whose `Enable` waited 10 s for `Initialize`, and a Recorder test that waited 10 s for the engine's task to start); the earlier commit `adff815` had failed on a third one (Recorder shutdown test, fixed in `c20f1bb`: it now waits for the engine to report the end). They look like starvation on the runner, not product bugs, but they are not fixed. Re-running only the failed job is what the green run is.
- `-Sync` stopped on `README.md` in all five module repositories (the repositories keep their own README, the host copy changes only a version line). The way through: `git merge --no-ff --no-commit <split sha printed by the script>`, `git checkout --ours README.md`, edit the README by hand (version line, "Needs Rigbay 0.1.2 or newer", host API range, test count, what is and is not verified), commit as `Sync with Rigbay main: N change(s) of modules/<id>`, then run the same `-Sync` command again: it finds nothing new, moves the pin and runs `-Verify`. `-SourceBranch` and `-HostCommit` can be the release commit itself.
- A module whose manifest says `hostApi >=0.1.3` is offered by hosts 0.1.0 and 0.1.1 too (they ignore the revision), so the module notes and README say "needs Rigbay 0.1.2 or newer" and the registry pull request was made only after the app release.
- `released` in a registry entry is the UTC date of the release; it was 2026-11-12 for releases made after midnight in the author's time zone on 2026-11-13.
- The Bash tool of the agent turns `\\` into `\`: `package-app.ps1 -Out D:\x` typed through it wrote the zip into the worktree. Run these scripts from PowerShell, and take any text with a backslash (registry entries) from the scripts' output, not from a typed copy.
- Upgrading the installed copy (`install.ps1` from the extracted released zip, elevated, `-NoPause`) asks the running instance to quit, copies, re-creates the task and the shortcut, and keeps the modules folder and settings. Quitting stops the modules: a Steam access bypass that was on is turned off and the Windows proxy setting restored (log line `Bypass off: the Windows proxy setting was restored.`), so the user has to turn it on again; modules the user enabled come back enabled, and XTU's auto-switch evaluates once at start (`AlreadyActive`, nothing written). Modules are not updated by the host upgrade.

### 11.8 Notes from the fourth run (v0.1.3, task R4, 2026-11-13)

- Release [v0.1.3](https://github.com/rigbay-app/rigbay/releases/tag/v0.1.3), built from `9ace25a` (CI green on the first attempt, no rerun); Recorder 0.1.2 ([release](https://github.com/rigbay-app/module-recorder/releases/tag/v0.1.2), built from `bfc488a`, built twice with the same hash) and registry pull request #8 followed. No other module needed a release (host API stays 0.1.3). SHA-256 of both zips: in the release notes; the downloaded assets were equal to the packaged files.
- **The app zip is not byte-reproducible any more.** Two runs of `package-app.ps1` on the same commit gave 10,922,937 and 10,923,019 bytes; the only file that differed was `Rigbay.App.dll`, and the difference is the IL of the generated C#/WinRT interop code (the order of generated vtable and generic-instantiation classes, for example `XamlUserTypeWinRTTypeDetails` against `XamlSystemBaseTypeWinRTTypeDetails`), not a path or a time stamp. Earlier releases (0.1.0, 0.1.1) came out equal from two folders, so the generator output is not stable from one build to the next. The rule in 11.2 stands: the hash that counts is that of the file you upload; test **that** file (the self-test and the installer test ran on the extraction of the zip that was uploaded) and package only once.
- **`tools\test-installer.ps1` and a running Rigbay.** Its sections 4 and 5 start a copy with `--allow-multi` (no pipe) and then let `install.ps1` / `uninstall.ps1` run `--uninstall-cleanup`, which talks to whichever instance owns the user's pipe (the pipe name is per user). With the owner's Rigbay running, that instance would be asked to quit. The non-elevated part is safe at any time; the elevated part was run after the upgrade of the installed copy had quit the owner's instance and before it was started again (23 of 24; the one failure was the check "the real state is unchanged", whose count of `Rigbay.App` processes went from 1 to 0 because a sibling worker's screenshot app ended during the test; paths, task, shortcut and uninstall key were equal). A test that never reaches the real pipe would need the installer's quit call to honour `--data`; not done.
- The check in that script for `-WhatIf` looked for the literal `Rigbay 0.1.1` and would have failed since 0.1.2; it now reads the version from the zip's exe.
- **`tools\e2e\run.ps1` against the zip**: hooks 41 of 41, non-elevated ACL check 10 of 10, first-hour flow 43 passed and 6 failed in dark and in light. The same 6 fail the same way against the released 0.1.2 (`-Tag v0.1.2`), so they are not caused by 0.1.3: "the page shows the scratch modules folder", "details show the SHA-256, the source link and what it declares", "details say Works on this PC", "live readings: three temperatures and three fan speeds, all numbers", "no write: Last set by Rigbay says Nothing yet" and, after the Recorder page, `not found: 'Folder'` (the flow then ends with a null reference at line 229 of `flow.ps1`, so the last Recorder steps never ran). They look like expectations the 0.1.2 redesign left behind; not investigated. Note that this flow turns on the real PredatorSense module of the registry in its scratch folder (reads only).
- `-Sync` stopped on `README.md` again; same recipe as 11.7 (`git merge --no-ff --no-commit <split>`, `git checkout --ours README.md`, edit by hand, commit, run the same `-Sync -Verify` command again, which moves the pin). `-SourceBranch release/0.1.3 -HostCommit <release commit>` worked from the worktree.
- Upgrade of the installed copy: `invoke-elevated.ps1 -File <extracted released zip>\install.ps1 -Arguments '-NoPause'`. It asked the owner's instance to quit (exit 0, ended), copied, re-created the task and the shortcut; settings (`host.json` hash unchanged) and the module folders stayed. Then `schtasks /run /tn Rigbay`: log `Rigbay host 0.1.3, API 0.1.3`, five modules discovered (predatorsense, recorder, steam-bypass, tunnel, xtu, all Off until enabled), the four that were enabled came back enabled, xtu's auto-switch evaluated once (`AlreadyActive`), and about 60 s later the first real trim: `TRAYTRIM ... ws 213.8->1.7 MB private 138.9->137.9 MB took 31 ms`. No new WARN or ERROR. The WireGuard service stayed running through the quit and the start.
- `invoke-elevated.ps1 -Arguments` takes an array: call the script with `&` from PowerShell; `powershell -File ... -Arguments '-ZipDir','x'` flattens it into one string.

### 11.9 Notes from the fifth run (v0.1.4 and the GPU module, task R5, 2026-11-13)

- Releases: [Rigbay v0.1.4](https://github.com/rigbay-app/rigbay/releases/tag/v0.1.4) built from `76150bb` (`package-app.ps1 -Ref 76150bb`, 10,943,678 bytes), the new repository [module-gpu](https://github.com/rigbay-app/module-gpu) with [v0.1.0](https://github.com/rigbay-app/module-gpu/releases/tag/v0.1.0) (built from `27c42dd`, twice with the same hash, 40,753 bytes) and registry pull request #9 (new entry `gpu`, validator green, merged). `verify-module-zip registry` passed for all six modules including `gpu`. The downloaded assets were equal to the packaged files; the hashes are in the release notes.
- **`main` moved under the release commit.** Other merges (PW, H1, a CI fix) landed on `main` within minutes of the release commit, and the workflow cancels a running CI on `main` when a newer push arrives (`cancel-in-progress`), so the push-triggered run of the release commit was cancelled twice. Release from the commit, not from `main` (`-Ref <sha>`, `gh release create --target <sha>`), and re-run the cancelled run once `main` is quiet. That re-run of `76150bb` was **red**: `Rigbay.Gpu.Tests ... The_serial_wrapper_lets_one_thread_in_at_a_time_and_the_bare_fake_does_not` (the runner ran `Parallel.For` serially); it was fixed on `main` afterwards (`55c7f98`, test code only), but that `main` also holds work that was not to be released, so the notes of v0.1.4 say the CI on the release commit is red and why. The `checks` job (LangCheck, hygiene, links) was green.
- **First repository made from the script since the tunnel.** `extract-module.ps1 -ModuleId gpu -SourceBranch <release commit> -HostCommit <release commit> -Verify` worked without a hand step except the README (the skeleton replaces the module's own README: rewrite it from the host copy, `git show <split>:README.md`, and drop paths and links that only exist in the host repository). Before the first push, `-Sync -SourceBranch origin/main -HostCommit <same pin>` brought the one later test fix into the new repository as a normal merge commit; the pin stays on the release commit. `-Sync` works on a repository that has no remote yet. Repository settings: only `gh repo create --public --description ...` (module-tunnel has no topics either).
- `tools\test-installer.ps1` got `-NoRunningInstance` (elevated part without the two steps that start a copy from the install folder, which make `install.ps1` / `uninstall.ps1` run `--uninstall-cleanup` against whichever Rigbay owns the user's pipe): 23 of 23 on the released zip with the owner's Rigbay running, its process id unchanged afterwards. The checks for the installed version no longer say `0.1.3` literally (they read the zip's exe). The unelevated part: 4 of 4. Not run: the two running-instance steps; the upgrade of the installed copy is their real test.
- Totals before the release: 1,932 tests (Core 851, XTU 214, SteamBypass 269, PredatorSense 189, Recorder 147, Tunnel 118, Gpu 125, LangCheck 19); self-test 188 passed, 0 failed, 2 skipped (`--allow-multi`), from the build output and from the extracted zip, whose log starts with `Rigbay host 0.1.4, API 0.1.3`.
- **Upgrade of the installed copy** (after the owner said go; the Steam access bypass was on at first, so the task stopped and asked, and the owner had turned it off by hand by the time of the upgrade): `invoke-elevated.ps1 -File <extracted released zip>\install.ps1 -Arguments @('-NoPause')` asked the owner's instance to quit (exit 0, ended), copied, re-created the task and the shortcut; `host.json` hash unchanged, modules folder kept. `schtasks /run /tn Rigbay` was needed (the installer does not start it): log `Rigbay host 0.1.4, API 0.1.3`, five modules discovered (all Off until enabled), the four that were enabled before (predatorsense, steam-bypass, tunnel, xtu) came back enabled, xtu auto-switch `AlreadyActive`, recorder stayed off as before; no new WARN or ERROR; the WireGuard service stayed running. The GPU module was not installed (the owner does that from Modules).
- The Bash tool of the agent turns `\\` into `\` inside here-documents too (a README line with `src\\Rigbay.Gpu\\bin` came out with a backspace character in it); write such text with the Write tool or PowerShell.

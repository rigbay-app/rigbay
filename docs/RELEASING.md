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
- [ ] `module.json`: `version` is the version to release and is not already released; `hostApi` is the range the module was built and tried against; `requires` says everything the module reads or writes (the registry's `declares` is copied from it, and the app refuses a module that declares more than the registry says).
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

Then, by hand, on a machine with the app: Modules page, "Get the module list", Install, restart, and look at the module (turned off, what it declares shown). That step is the one
nobody automated, and it is the one that proves the user experience.

## 9. Cutting v0.1.1 (a fix or a new version)

1. In the module repository: fix, tests, bump `version` in `module.json` (and `<Version>` in `Directory.Build.props`, if it has one) to `0.1.1`; keep `hostApi` unless the contract changed. If `requires` changed, say so in the notes: a wider
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

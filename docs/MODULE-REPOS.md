# Module repositories

A module is written in `modules/<id>/` of this repository and, when it is finished and verified, moves to its own repository
`rigbay-app/module-<id>` (public, MIT). `tools/extract-module.ps1` does the mechanical part. The first use was `module-predatorsense`.

## What a module repository looks like

```
module-<id>/
  external/rigbay        git submodule: https://github.com/rigbay-app/rigbay.git at a pinned commit (the host contract)
  src/<Project>/         the module project; its ProjectReference points to external/rigbay/src/Rigbay.Abstractions
  tests/                 unit tests
  docs/                  documentation of the module (docs/<id>/ of this repository) and evidence
  module.json            the manifest (the project copies it into the build output)
  <Module>.sln           one solution at the root (it also lists Rigbay.Abstractions from the submodule)
  Directory.Build.props  the module's own build settings (it must not depend on this repository's)
  .github/workflows/build.yml, .gitignore, LICENSE, README.md
```

Why a submodule and not a NuGet package: no package is published yet (publishing is an owner decision), and a pinned commit is
exact. `Rigbay.Abstractions` is built from source into the module's own `bin` folder; the project reference sets `Private=false` and
`ExcludeAssets=runtime`, so `Rigbay.Abstractions.dll` stays out of the module's output (the host supplies its own copy).
When the package exists, replace the `ProjectReference` by the `PackageReference` and drop the submodule.

## Preconditions

- The module is finished: built with 0 warnings, tests green, looked at in the app, documented. Its folder is self-contained
  (own `.sln`, own `Directory.Build.props`, `module.json` in the folder, no reference to anything else of this repository except
  `Rigbay.Abstractions` and, for tests, `Rigbay.Core`).
- The code is merged into `-SourceBranch` (default `main`); the script reads that ref and never the working tree.
- `git`, `gh` (signed in, only used to read: the commit SHAs of the two actions) and the .NET SDK are installed. `git subtree` is part of
  Git for Windows.
- The pin: by default the tip of `main` on `https://github.com/rigbay-app/rigbay.git`, so it exists remotely and a fresh clone can
  fetch it. Check that it contains the host fixes the module needs, or pass `-HostCommit <sha>`.

## Steps

```powershell
powershell -ExecutionPolicy Bypass -File tools\extract-module.ps1 -ModuleId predatorsense `
    -OutDir D:\Documents\Projects\_repos\module-predatorsense -Verify
```

`-OutDir` must be outside this repository (the new repository is not a worktree of it) and empty or missing. The script
(it never pushes, never creates a GitHub repository and never changes this repository: no ref, no file):

1. `git subtree split -P modules/<id> <SourceBranch>`: the module's history, rooted at the module folder. The result is the new `main`.
2. Adds the submodule `external/rigbay` at the pin and rewrites every `ProjectReference` and solution path that went to
   `..\..\src\Rigbay.Abstractions` or `Rigbay.Core` to point into it. Commit 1.
3. Adds `.gitignore`, `LICENSE` (this repository's), and `.github/workflows/build.yml`: `windows-latest`, checkout with submodules,
   `setup-dotnet` 8.x and 10.x, `dotnet build` and `dotnet test` of the solution; `permissions: contents: read`, no secrets, no publishing; both
   actions pinned by full commit SHA with a version comment (the latest release, looked up with `gh api`). Commit 2.
4. Writes a README skeleton from `module.json` (build, install path `%ProgramData%\Rigbay\modules\<id>\`, what the manifest declares) and
   copies `docs/<id>/` of this repository into the new `docs/` (files matching `-ExcludeDocs`, default `GATE-*`, are left out). Commit 3.
5. With `-Verify`: clones the new repository into a temp folder with `--recurse-submodules`, runs `dotnet build -c Release` and
   `dotnet test`, and fails on any error. Read the output yourself: the goal is 0 warnings, 0 errors, all tests green.

## What you do by hand before the push

1. **README.** The skeleton has TODO sections: supported machines (honest, "tested on one machine"), safety notes (what the module can write,
   what is allowed, what is not verified), a screenshot or two from the module's `docs/evidence`, links to the host repository and to the
   documents. English. Commit it.
2. **Docs.** They reference paths of this repository (`tools/...`, `modules/<id>/...`). Fix them or say where they live. Check that they are
   written in our own words (rule 2 of `docs/plan/WORKER-RULES.md`).
3. **Scan the whole repository and its history** (the history is pushed too; the script already refuses to commit files that name the Windows user, the machine or a worktree path, and rewrites worktree paths in copied logs to `<worktree>`):
   - binaries: `git ls-files` should list only source, text, and the evidence PNGs; no `.dll`, `.exe`, `.zip`;
   - vendor files (Acer, Intel and so on): none, in the tree or in the history;
   - secrets: `git grep -nIiE "token|secret|password|api[_-]?key|BEGIN (RSA|PRIVATE)"` and read each hit;
   - personal data: the Windows user name, the machine name, absolute paths of a developer machine (`git grep -nIF -e 'C:\' -e 'D:\'
     -e '.claude'`, and the same over `git log -p`), serial numbers, e-mail addresses (commits use the GitHub no-reply address);
   - the screenshots: look at each one.
4. **Fresh clone** in another folder: `git clone --recurse-submodules <OutDir> <other>`, then `dotnet build` and `dotnet test`.

## The push (only after the owner's or coordinator's go)

Creating the repository is the one GitHub action this procedure allows, and only for a finished, verified module:

```powershell
gh repo create rigbay-app/module-<id> --public --description "<one line>"
git -C <OutDir> remote add origin https://github.com/rigbay-app/module-<id>.git
git -C <OutDir> -c credential.helper= -c "credential.helper=!gh auth git-credential" push -u origin main
gh run list --repo rigbay-app/module-<id>        # the first CI run; fix a failure by a normal commit, never a force-push
```

Not part of it: organisation or repository settings, releases, packages, transfer of the old repositories, deleting, force-push.
If the host moved since the pin was taken, move the pin first: `git -C external/rigbay fetch && git -C external/rigbay checkout <sha>`,
then `git add external/rigbay` and commit, and run the fresh-clone check again.

## Updating the pin later

A module repository stays on the contract it was built against until someone moves the submodule. Bump it in a normal commit, build and
test, and raise `hostApi` in `module.json` only if the contract's version range changed (`docs/ABSTRACTIONS.md`, API version).

## Notes from the first run (predatorsense)

- Two helper functions must not be named like the program they call (PowerShell resolves `Git` to the function, `git` to the same
  function: infinite recursion).
- XML comments in a `.csproj` cannot contain `--`; the comment the script writes avoids spelling out `--recurse-submodules`.
- `git subtree split` of a 54-commit history takes about 15 seconds and yields only the commits that touched the module folder.
- The new repository gets the same `user.name` and `user.email` as this one (it is not a worktree of it, so it would have none).

## Notes from the second run (xtu, `rigbay-app/module-xtu`)

- The script now accepts both module layouts: `module.json` anywhere under `modules/<id>/` (the shallowest one; xtu keeps it next to the
  project, predatorsense at the root), and one solution at the module root that is `.sln` or `.slnx`. The rewrite of the paths to
  `Rigbay.Abstractions` and `Rigbay.Core` works with either slash and any depth (`.slnx` uses `/`). The README skeleton takes the target
  framework and the build output folder from the module project. Re-running it for predatorsense gave the same repository as before (same
  split commit, same layout and CI trees; only the submodule pin and two README lines differ).
- The module keeps its own layout: `Rigbay.Module.Xtu/` and the tests project stay at the root of the new repository (a subtree split
  moves nothing), so the README's "Build" section says where the output folder is. The docs the script copies still speak of `modules/<id>/`
  and `docs/<id>/`: add a short note at the top of each copied document and fix the commands (done by hand for xtu, in one commit after the skeleton).
- A path longer than about 260 characters breaks `git submodule add` (`Filename too long` while it writes the pack): use short `-OutDir` and
  scratch folders, not a folder under the agent's temp directory.
- Scanning for the machine name must be case sensitive (`git grep -F`), because the GitHub login is the same word in lower case and is in the LICENSE and in
  the commit authors on purpose.
- The first CI run of `module-xtu` was green (build 0 warnings, 190 tests) with no change to the generated workflow.

## Notes from the third and fourth run (`module-steam-bypass`, `module-recorder`)

- A module whose id differs from its folder (`steam-bypass` lives in `modules/steambypass/`, `docs/steambypass/`): pass `-ModuleId steam-bypass
  -Folder steambypass`. `-ModuleId` is the id in `module.json` (it names the repository, `module-steam-bypass`, and the install folder);
  `-Folder` is the folder of this repository, and defaults to the id.
- The personal-data check now ignores `C:\Users\USER\`, the placeholder the host's logs and verb output use for the home folder. Every other
  hit still stops the script. The machine name is matched case sensitively (the GitHub login is the same word).
- The check cannot see inside PNGs. **Look at every screenshot before the push**: two recorder screenshots showed the folder of the author's
  development checkout in a text box, and one was an all-black capture. They were left out of the new repository by amending the local,
  unpublished docs commit (before the first push, so they are not in the history), and the copied document says so at the top.
- The READMEs of the two repositories are written by hand after the script (steps above), with a "Verified and not verified" section that only
  claims what was run. The generated workflow needed no change. The first CI run of `module-steam-bypass` was green (248 tests); the first run of
  `module-recorder` failed on one test, `The_real_services_work_on_this_machine`: it uses the real clock and asserted exactly two rows in a
  1.2 s recording, and on the slow runner the first row started more than a period late, so the schedule moved the slot (designed behaviour)
  and one row fitted. It was fixed in the module repository by a normal commit that accepts one or two rows (green after that). The same test
  in `modules/recorder` here has the same flaw; port the change by hand when the module is next touched (the next `-Sync` would otherwise
  conflict on that line, and the fix is to keep the module repository's version).
- `tools/live-record.ps1` (recorder) and the other host tooling files that a module folder carries do not run from the module repository alone;
  the README says so instead of hiding them.

## Sync: bringing later changes into an existing module repository

When the module changed in this repository after its extraction (a fix, new icons, language files), the module repository gets the same
changes as a **normal merge commit**, never a force-push or a rewrite. `-Sync` automates it and is still local only:

```powershell
powershell -ExecutionPolicy Bypass -File tools\extract-module.ps1 -ModuleId xtu -OutDir D:\Documents\Projects\_repos\module-xtu -Sync -Verify
# a module whose id differs from its folder: -ModuleId steam-bypass -Folder steambypass
```

`-OutDir` is a clone of the module repository with a clean working tree (an existing scratch clone is fine; `git pull` it first). The script:

1. Splits `modules/<id>` of `-SourceBranch` again. `git subtree split` is deterministic, so the commits that were extracted before come out with
   the same ids and are ancestors of the new split: the merge base is the last host state the repository already has. If it is not (the
   repository was not made by this script), it stops.
2. Lists the new module commits (`merge-base..split`) and merges the split into `main` with `--no-ff`, as `Sync with Rigbay main: N change(s)
   of modules/<id>`. Hand edits of the module repository (README, docs, CI, layout commit) stay: they are on its side of the merge. A
   conflict aborts the merge, leaves the repository as it was, and names the files; resolve those by hand (merge the split commit yourself).
3. Runs the host-path rewrite again on what came in (a new project or reference that still points at `..\..\src\Rigbay.Abstractions`) and
   commits it as `Layout: ...` if anything changed.
4. Scans the files that came in for personal data (same check as the extraction).
5. Moves the submodule pin to `-HostCommit` (default: tip of `main` on `https://github.com/rigbay-app/rigbay.git`). The pin must be a commit
   on `origin/main` there, so a fresh clone can fetch it; a pin that is not stops the script. It is a separate commit, `Host contract: move the
   submodule pin from <old> to <new>`.
6. Does **not** merge `docs/<id>` of this repository, because the copies in the module repository carry hand-made path fixes. It prints which
   host doc files changed since the merge base (`git diff --name-status`); compare those by hand.
7. With `-Verify`: fresh `git clone --recurse-submodules`, `dotnet build`, `dotnet test`; any failure stops the script.

Afterwards: `git log --graph` in the module repository, check the diff, then push `main` with the same credential line as above, and read
`gh run list --repo rigbay-app/module-<id>`. If the host contract changed in a way that needs a new `hostApi` range, edit `module.json` by
hand in a normal commit (see "Updating the pin later").

First use (2026-11-01, task P3): `module-predatorsense` (the A11 icons and manifest `icon`, the F1 language fixes and test fix: 2 module
commits, merge, pin move, 103 tests) and `module-xtu` (the same icons and F1 fix: 1 module commit, merge, pin move, 190 tests). Both builds
had 0 warnings and the first CI run after the push was green. The host docs of both had no change that was not already in the module
repository (`docs/xtu/CLITE-PLAN.md` changed, but before the extraction).

Notes: the merge keeps the module's own commit ids and authors in the history of the module repository, so `git blame` stays useful. The
first draft of the check for personal data in this mode passed an empty option list to `git grep`, which silently skipped three of the four
patterns (git said `fatal: unable to resolve revision` and the script went on): when a native command's output is the safety check, test
that it can fail. The final check was tried with planted hits: a user name, the machine name, a home path and a worktree path are each refused; the placeholder home path and the lower-case GitHub login are not.

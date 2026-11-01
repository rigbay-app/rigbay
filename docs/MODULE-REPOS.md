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
3. **Scan the whole repository and its history** (the history is pushed too):
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

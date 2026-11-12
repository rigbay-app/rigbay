# The module registry (task R1)

`https://github.com/rigbay-app/registry` holds one file, `registry.json`, that lists the modules the app can install. This is the host's side of the
contract: what the app reads and how. The contributor's side (how to submit a module, the checklist of a review) is the `README.md` of that repository, and
the machine-readable rules are its `registry.schema.json` and `scripts/validate.mjs`. What the app does with an entry is `docs/INSTALL.md`.

The registry is the **trust anchor** of the installer (`docs/INSTALL.md` section 9). Changes to it are pull requests that a maintainer reads and merges by
hand; there is no automatic merge. (Requiring a review before merge is a setting of the GitHub repository that the owner chooses; this task did not
change any GitHub setting.)

## 1. File

```json
{
  "schemaVersion": 1,
  "modules": [
    {
      "id": "steam-access",
      "name": "Steam access",
      "description": "DNS and DPI workaround for networks that block Steam.",
      "repo": "https://github.com/rigbay-app/module-steam-access",
      "icon": "https://raw.githubusercontent.com/rigbay-app/module-steam-access/main/icon.svg",
      "versions": [
        {
          "version": "0.3.1",
          "hostApi": ">=0.1 <0.2",
          "zip": "https://github.com/rigbay-app/module-steam-access/releases/download/v0.3.1/steam-access-0.3.1.zip",
          "sha256": "<64 lowercase hex digits>",
          "size": 482113,
          "released": "2026-11-01",
          "declares": { "admin": true, "network": ["tcp:18081 (local proxy)"], "registry": [], "services": [], "outsideFiles": [] }
        }
      ]
    }
  ]
}
```

| Field | Meaning and rule (the host refuses the whole file when one is broken) |
| --- | --- |
| `schemaVersion` | `1`. Another number: "needs a newer Rigbay" and the file is not used. |
| `id` | The module's manifest `id` (`docs/ABSTRACTIONS.md` 2.1): lowercase words joined by `-`, 2 to 40 characters, reserved ids refused, unique in the file. |
| `name`, `description` | Shown on the Modules page before anything is installed; the manifest's own text replaces them after install. 1 to 60 and 1 to 200 characters. |
| `repo` | `https://github.com/<owner>/<repo>`, nothing more. Shown as the source link. |
| `icon` | Optional. A `.svg` or `.png` on `raw.githubusercontent.com` or `github.com`. **Read and validated but not fetched in this version** (the host would have to download and sanitise an image from the network before drawing it); after install the module's own icon files are used. |
| `versions` | At least one, at most 200, **oldest first, strictly ascending** semantic versions. |
| `version` | Semantic Version 2.0; equal to the `version` of the `module.json` in the zip. |
| `hostApi` | The host API range the release supports (`docs/ABSTRACTIONS.md` 11), the same text as the `module.json` in the zip. `major.minor` means any revision (`>=0.1 <0.2`); `major.minor.revision` needs a host of that revision or more (`>=0.1.2 <0.2`, for a module that uses a member added in revision 2). A release whose range does not contain this host's API is not offered ("Needs host API 0.1.2 or newer" when only the revision is too low, else "Needs host API ..."). **Hosts 0.1.0 and 0.1.1 ignore the revision** and would offer such a release anyway, so a module that needs a revision is released only after a host that has it. The entry needs no extra field. |
| `zip` | `https://github.com/<owner>/<repo>/releases/download/<tag>/<file>.zip` where owner and repo are those of `repo` (case ignored); no query, no port, no user, no encoded dots or slashes. |
| `sha256` | 64 lowercase hex digits; compared with the download **before** it is unpacked. |
| `size` | 1 to 64 MB; the download must be exactly this long. |
| `released` | `YYYY-MM-DD`. |
| `declares` | `admin` (required boolean) and the string lists `network`, `registry`, `services`, `outsideFiles` (at most 32 entries of at most 260 characters). They are the manifest's `requires.admin`, `requires.network`, `requires.registry`, `requires.services` and `probePaths` as written. The task text calls the network list "ports"; the manifest's entries are free text such as `tcp:18081 (local proxy)` and may name endpoints too, so the field keeps the manifest's name. |

Unknown fields are ignored by the app (a newer registry may add some) and refused by the check (a typo in a pull request is caught).

## 2. How the app chooses a release

For each module the app takes the newest release whose `hostApi` contains the host API and that is not a prerelease; when a module has only prereleases that
fit, the newest of those. A module with no fitting release is listed as "Needs host API ..." with no Install button. The revision counts: a release of `>=0.1.3 <0.2` is not chosen by a host whose API is `0.1.2`, and an older release that fits is chosen instead. An installed module is offered an
update when that release is newer than the installed version. A release with a lower version than the installed one is never an update.

## 3. The check (`rigbay-app/registry`, `.github/workflows/validate.yml`)

On every pull request and every push to `main`: the validator's own tests (`scripts/validate.test.mjs`, 39 tests over `scripts/fixtures`), then
`node scripts/validate.mjs registry.json`: valid JSON, the schema's rules, https GitHub URLs of the same repository, unique ids, semantic versions
ascending, sha256 format, host API syntax, dates, sizes, declarations. On a pull request the file of the target branch is fetched and
`--base` makes the check fail when a release that is already published was **changed** (a removal is allowed and printed as a warning). The workflow has a
read-only token, no secrets, both actions pinned by commit SHA, and publishes nothing.

## 4. Keeping the host and the check in agreement

`scripts/fixtures` of the registry repository (one file per rule, plus `expectations.json`) is copied to `tests/Rigbay.Core.Tests/registry-fixtures`.
`RegistryParityTests` reads every file with `RegistryReader` and requires the same verdict as the check (and an error that mentions the same field). The one
planned difference is the unknown field above. When a rule changes in one place: change the fixtures in the registry repository, copy them here, run the
tests; a rule that exists in only one of the two fails a test.

## 5. Why there is no way to add another registry

A user-defined registry address is not supported in this version, and the product build has no hidden way to set one. The registry is where the trust
is: one list, reviewed. An address field would let a web page, a chat message or a "how to install X" guide move that trust to a stranger for a program that
runs as administrator. A fork of Rigbay can change the constant (`InstallPolicy.DefaultRegistryUrl`). The only override is the test switch described in
`docs/INSTALL.md` section 7, which works only with scratch data and modules folders.

## 6. The repository

`https://github.com/rigbay-app/registry`, public, MIT. Contents: `registry.json` (**empty: `"modules": []`**, there are no releases yet; entries are added from the
owner's releases), `registry.schema.json`, `scripts/validate.mjs`, `scripts/validate.test.mjs`, `scripts/entry.mjs` (makes an entry from a zip and its `module.json`),
`scripts/fixtures`, `README.md`, `LICENSE`, `.github/workflows/validate.yml`, `.github/pull_request_template.md`.

Created and pushed for this task (the one GitHub action it allowed: create and push, nothing else). Its scratch copy is `D:\Documents\Projects\_repos\registry`
(not part of this repository). Before the push: the file list was read (45 files, text only, no binary), a case-sensitive search for the Windows user
name, the machine name, drive paths and worktree paths found nothing (the owner's GitHub login is in `LICENSE`), a search for tokens and passwords found only the
word in code, and a fresh clone ran the tests and the validator. The first CI run **failed** (the runner's Node 22 does not scan a folder for `node --test`;
Node 26 here did) and was fixed by a normal follow-up commit that names the test file; the second run is **green**.

### Not verified here

The pull-request-only step of the workflow (it runs only on a pull request, and no pull request was opened); a registry with real entries (there are none; the
tests and the self-test use local registries); a review requirement on `main` (a GitHub setting, not changed).

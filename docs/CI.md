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

## Settings that are not in this repository

Branch protection ("require the `ci` checks before merging"), Dependabot for the action pins, the labels the issue templates ask for (`bug`, `module-request`,
`hardware-report`) and private vulnerability reporting for [SECURITY.md](../SECURITY.md) are repository settings. They are decisions of the owner and are not
changed by anything here; until they are on, the files work but nothing enforces them.

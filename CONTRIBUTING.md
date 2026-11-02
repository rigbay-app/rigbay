# Contributing to Rigbay

Thanks for helping. Rigbay is a Windows host app plus separate module repositories, so most contributions are one of three kinds: a fix or feature in the host
(this repository), a new module (its own repository), or a hardware report ("it works / it does not work on my machine"). Please read the
[Code of Conduct](CODE_OF_CONDUCT.md) first.

## Build and test

You need Windows 11 (x64), the .NET 10 SDK, the .NET 8 SDK (the modules `recorder` and `steambypass` target net8.0) and the Windows App Runtime 1.8 to *run* the app.

```powershell
dotnet build Rigbay.sln --no-incremental -warnaserror   # must finish with 0 warnings: CI builds exactly this
dotnet test Rigbay.sln                                  # every test project
powershell -ExecutionPolicy Bypass -File tools\ci\local-check.ps1
```

`tools\ci\local-check.ps1` runs everything CI runs (build, tests, strict LangCheck on every language folder, the repository scan, the Markdown link check) and
then builds the app and runs the in-app self-test, which needs an interactive desktop and an elevated process and therefore cannot run in CI.
Use `-SkipSelfTest` if you cannot elevate, and say so in your pull request. What CI does and does not cover: [docs/CI.md](docs/CI.md).

Running the app, screenshots, and where logs and settings live: [docs/APP.md](docs/APP.md).

## Writing a module

- The contract a module implements (lifecycle, page model, metrics bus, host services): [docs/ABSTRACTIONS.md](docs/ABSTRACTIONS.md), and its walk-through
  [How to write a module](docs/ABSTRACTIONS.md#13-how-to-write-a-module). The template is `samples/Rigbay.SampleModule`.
- Giving a finished module its own repository (`rigbay-app/module-<id>`): [docs/MODULE-REPOS.md](docs/MODULE-REPOS.md).
- Releasing it so the app can install it, and adding it to the registry: [docs/RELEASING.md](docs/RELEASING.md) and [docs/REGISTRY.md](docs/REGISTRY.md).
- Text shown to the user is a language key, never a literal; `en` is the fallback and every key must be translated. Run LangCheck on your `lang` folder (see CI.md).

## The safety rule

Modules run inside the app **as administrator**, and some of them change real hardware. Therefore:

1. **Reading is free. Every write to the machine needs the user's explicit approval for that specific call and value.** That covers fan speed, performance or
   turbo mode, power or charge limits, EC or WMI methods that change state, drivers, services, and registry values that control hardware. Use the host's
   approval flow ([docs/ABSTRACTIONS.md](docs/ABSTRACTIONS.md), section 5.4); do not build your own.
2. **A write is confirmed by a decoded read-back, or the module says "not verified".** If you cannot read the state back and decode it, do not show "applied";
   show what was sent and that the result is not verified. Every write has a way to undo it, and the pull request says what it is.
3. If you are not sure whether a call is read-only, treat it as a write.
4. Do not add vendor files, decompiled vendor code, secrets, or personal paths. Findings about vendor software are written in your own words and your own code;
   short identifiers (method names, ids, numeric ranges) are fine as facts.

Please do not test a hardware write on a machine you cannot afford to restart or lose. If a pull request changes hardware behaviour, say on which machine and
Windows build it was tried, and what was not tried.

## Pull requests

- Small and focused. One change, one reason; explain the reason in the description.
- Tests for new behaviour; a bug fix gets a test that failed before.
- 0 build warnings, LangCheck clean, and for UI changes a screenshot of the window (render the window; do not capture your whole screen: it may show private data).
- Code, comments, documentation and commit messages in English; the app itself is localised through `.lang` files.
- The pull request template has the checklist. CI must be green before review.

## Reporting a problem

Use the [issue templates](https://github.com/rigbay-app/rigbay/issues/new/choose): bug, module request, hardware report. Do not paste secrets, tokens or
private paths in an issue; logs live in the folder named in [docs/APP.md](docs/APP.md), and you can trim them first. Security problems go through
[SECURITY.md](SECURITY.md), not a public issue.

## License

By contributing you agree that your contribution is licensed under the MIT License of this repository ([LICENSE](LICENSE)).

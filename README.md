# Rigbay

A small Windows app that manages **modules** for your own machine: power limits, fan and performance
modes, monitoring and logging, network fixes, and anything else a module can do. Each module lives in its
own repository and can be installed, updated or removed from inside the app.

Status: **early development (v0.1, not released).** The host app, the module contract and the core run and are
tested; the first modules (XTU profiles, PredatorSense) are being written. Nothing is published as a release yet.

<!-- screenshots: docs/spike and tools/shots hold the current renders -->

## Why

Tools like Intel XTU, Acer PredatorSense and one-off fixes each have their own window, their own tray icon and
their own way of starting with Windows. Rigbay gives them one place, one look (WinUI 3, Fluent, Mica), one
settings store, one set of hotkeys and one log, and lets people write a module for *their* machine (other brands,
other CPUs and GPUs, other features) without forking the app.

## What is here

| Path | What |
| --- | --- |
| `src/Rigbay.Abstractions` | The small, versioned contract a module references (module lifecycle, declarative page model, metrics bus, host services). No UI-framework types. |
| `src/Rigbay.Core` | Module discovery and loading (one load context per module), fault boundary, settings, localisation, logging, hotkey/tray/command registries. |
| `src/Rigbay.App` | The WinUI 3 shell: dashboard, module pages, search, tray, hotkeys, toast, command line, start with Windows. Unpackaged, runs as administrator. |
| `src/Rigbay.LangCheck` | Checks language files (`key = text`). |
| `samples/Rigbay.SampleModule` | A minimal module, the template for module authors. |
| `tests/` | Core tests (`dotnet test`). |
| `docs/` | Design notes, the module contract (`ABSTRACTIONS.md`), the UI direction, the app guide, spike reports. |
| `tools/` | App build, run and screenshot scripts, demo modules. |
| `spike/` | The UI-framework and XTU runtime spikes (kept as evidence). |

## Planned and in-progress modules

| Module | Source | State |
| --- | --- | --- |
| XTU profiles (Intel power limits, hotkeys, auto-switch, monitor) | [xtu-profile-switcher](https://github.com/minhdevtree/xtu-profile-switcher) | standalone app works; module port in progress |
| Acer PredatorSense (fan mode, sensors) for Predator laptops | new | interface read, first writes verified on one machine; module in progress |
| Steam access (DNS / DPI workaround for networks that block Steam) | [steam-bypass](https://github.com/minhdevtree/steam-bypass) | standalone app works |

## Build and run

Needs Windows 11, the .NET 10 SDK, and the Windows App Runtime 1.8 installed.

```powershell
dotnet build Rigbay.sln
dotnet test Rigbay.sln
.\tools\build.ps1            # app + demo modules into tools\out
.\tools\selftest.ps1         # elevated in-app self-test
```

See [docs/APP.md](docs/APP.md) for running, screenshots and where logs and settings live, and
[docs/ABSTRACTIONS.md](docs/ABSTRACTIONS.md) for writing a module.

## Safety

Modules run **inside the app as administrator**, so install only modules you trust. Rigbay shows what a module
declares it uses, but this is information, not a sandbox. Anything that changes hardware (fans, power limits,
modes) asks first and shows what it will do. The PredatorSense work is based on reading software installed on the
author's own machine; no vendor files or decompiled code are included here, and the findings are written
in our own words.

## License

MIT, see [LICENSE](LICENSE).

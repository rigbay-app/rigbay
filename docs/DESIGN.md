# Rigbay: design notes

Written at the start of the project from the discussion that led to it. Change it as decisions are made.

## Goal

One host app that manages modules for many kinds of machines and features, not just the author's laptop
(Acer Predator Triton 500 SE, i7-11800H, RTX 3060, Windows 11). The author does not write Windows apps for a
living, so choices favour the simplest thing that works and is easy to read.

## Decided

- **Name: Rigbay.** A "bay" is a slot a module plugs into. GitHub organisation: **`rigbay-app`** (https://github.com/rigbay-app), created, MIT, everything public. Moving the two existing repos into it is the owner's step.
- **The UI is new and modern.** Target: **WinUI 3** (Windows App SDK, Fluent, Mica), unpackaged, running elevated, loading module DLLs in-process. This holds unless the spike (below) finds a hard blocker; in that case the fallback is WPF on modern .NET with the Fluent theme, and the owner decides. UI principles come from the `impeccable` and `design-taste-frontend` skills, applied to a native app: flat surfaces and hairlines, colour carries data, honest empty and error states, text at least 4.5:1, no decorative cards/pills/gradients/glow, short copy, one product-specific element.
- **Bilingual UI, English default**, language files not code (format and `--langcheck` as in xtu-profile-switcher).
- **Hardware writes are gated.** Reading is free. Every write to the machine (fan, performance mode, power or charge limits, EC/WMI methods that change state, drivers, services) needs the owner's explicit approval for that specific call and value.
- **Reverse engineering scope.** Only software installed on the owner's own machine, only to make a compatible module for that machine. No Acer or Intel files or decompiled code in any repo; installed files are referenced in place; findings are written in our own words and our own code.
- **One process, run as administrator.** Hardware modules (XTU, PredatorSense) need admin, so the host runs elevated and loads modules in-process. (An earlier idea, an unelevated host plus an elevated helper, was dropped for simplicity.) The Steam module does not need admin; it simply runs elevated too.
- **Everything is public source.** That does not make a third-party module trustworthy: a module runs as admin inside the host. So install is explicit and checked (see below).
- **Modules live in their own repos** and are optional: browse, install, enable/disable, update, remove from inside the app. A module repo can also keep shipping a standalone app.
- **Modules are published as release assets** (zip). A separate `registry` repo lists them with the SHA256 of each release; changes to it go through pull requests. The host verifies the hash, unpacks into an admin-only folder (for example under `Program Files` or `ProgramData` with a locked ACL), shows what the module declares it uses (admin, network ports, registry, services; informational, not a sandbox) and asks before enabling. Install, update and removal need a restart (DLLs cannot be unloaded cleanly).
- **A small versioned `Abstractions` assembly** is the only thing a module references. It defines the host services and the module interface; the host API has a version and a module declares the range it supports.
- **Host services:** per-module settings, localisation (language files, keys prefixed by module id, English fallback), hotkeys (conflicts detected across modules), tray menu items, command-line verbs (`rigbay <module> <verb>`), notifications (toast), log.
- **Probe:** every module has a check "does this machine support me" (a service exists, a WMI class exists, ...). The host shows supported modules and lists the others in the catalogue with the reason.
- **Metrics bus:** modules publish readings (temperature, power, usage, ...) with unit and id; a recorder module consumes the bus and writes CSV. A new sensor source (AMD, another vendor) is just another module.
- **Fault boundary:** every call into a module is wrapped; a module that throws is disabled and logged instead of taking the app down.
- **Language files, not code:** `key = text`, `@code`/`@name` header, English as the reference, `--langcheck` to verify (same format as in xtu-profile-switcher).

## Open (to decide before writing code)

1. **UI framework.** Target is **WinUI 3**; a spike must prove (a) an unpackaged, elevated WinUI 3 window runs reliably, (b) a module DLL loaded at run time can contribute a page, (c) the app starts at logon through a scheduled task with highest privileges. A hard blocker is reported with evidence and the alternatives (WPF on modern .NET with the Fluent theme); the owner decides. Toolchain on the author's machine (checked 2026-11-01): only the .NET 8 runtimes are installed, **no .NET SDK, no Visual Studio**; installing an SDK needs approval. Windows App Runtime packages 1.6, 1.8 and 2.x are already installed (from Store apps).
2. **Runtime and build.** The existing apps are C# 5, .NET Framework 4.8, built with the `csc.exe` that ships with Windows and no project file. A public platform with other authors wants SDK-style projects and a NuGet package for `Abstractions`. Open question: stay on net48 or move to modern .NET. Intel's contract DLLs (`XtuInterface.dll`, `XtuCommon.dll`) are .NET Framework assemblies that talk to the XTU service over WCF named pipes, so the XTU module has to keep working wherever the host runs.
3. **Org and repo layout.** Proposed: `rigbay` (host, Abstractions, shared UI), `registry`, `module-xtu`, `module-steam-bypass`, `module-predatorsense`, `module-template`, `.github`.
4. **Elevation at start.** The existing XTU app starts at logon through a scheduled task with highest privileges, no prompt. Rigbay would do the same.

## Existing code to bring over

- `xtu-profile-switcher`: localisation loader (`Loc`, `lang/*.lang`), theme and owner-drawn controls, settings file handling, hotkeys, tray, toast, the XTU client (`XtuClient`, `MonitorService`) and the CSV recorder. It has a project skill under `.claude/skills/xtu-switcher` that documents the XTU protocol and the gotchas.
- `steam-bypass`: local proxy, DNS-over-HTTPS and PAC based workaround; its own skill under `.claude/skills/steam-bypass`. Its UI uses a card style while xtu-profile-switcher uses flat hairlines, so the shared UI kit has to pick one.

## Order of work

1. Decide the open points above; create the org; transfer the two existing repos.
2. Host skeleton plus `Abstractions` (settings, localisation, hotkeys, tray, toast, log).
3. XTU as the first module; split monitoring into the metrics bus and a recorder module.
4. Modules tab, registry, install and verify.
5. Steam module.
6. PredatorSense module, after reading its interface with REA.

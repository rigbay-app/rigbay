# Write your first module

From nothing to a module that other people can install from Rigbay's Modules page, in ten steps. The steps use the real commands; every
one of them was run against the template repository [`rigbay-app/module-template`](https://github.com/rigbay-app/module-template).

This page is the path. The details live in the contract documents: [`ABSTRACTIONS.md`](ABSTRACTIONS.md) (the module contract: manifest, lifecycle,
page model, metrics, localisation), [`MODULE-REPOS.md`](MODULE-REPOS.md) (repository layout), [`RELEASING.md`](RELEASING.md) (zip, release, registry
pull request), [`INSTALL.md`](INSTALL.md) (what the app does with your zip) and [`REGISTRY.md`](REGISTRY.md) (the list). The brand rules for your icon are
`BRAND.md` section 10.

## Before you start: what you are asking people to trust

**A module runs as administrator, inside Rigbay's process.** Rigbay itself always runs elevated, and a module is ordinary .NET code loaded into it.
Nothing sandboxes it: a module can read and write anything an administrator can. The contract cannot change that. What Rigbay does instead:

- shows the manifest (`requires`, `probePaths`) **before any of your code runs**, and again before the first enable;
- refuses an install whose manifest declares more than the registry entry says (`declares`), and turns a module off when an update declares more;
- installs only a zip whose SHA-256 and size match an entry in the reviewed registry, from the module's own GitHub repository, nothing else;
- asks the user before a change to the machine, and keeps a module that throws from taking the app down (the fault boundary).

So the trust is placed in two things: the registry review (a maintainer reads your pull request and your source) and **you**. Treat that as a
responsibility. If you cannot explain every line your module runs as administrator, it is not ready.

## The ten steps

### 1. Set up

You need Windows 10 or 11, Git, the .NET 10 SDK (your module targets net8.0, the SDK builds it, and the host's language checker is net10.0), Windows
PowerShell 5.1 or newer, and the GitHub CLI `gh` (signed in) for releasing. To try the module in the app you also need Rigbay installed
([USER-GUIDE.md](USER-GUIDE.md)); never develop against the real modules folder.

### 2. Copy the template

```powershell
git clone --recurse-submodules https://github.com/rigbay-app/module-template module-fan-tools
cd module-fan-tools
git remote remove origin
```

(or use GitHub's *Use this template* button if the repository offers it). The template is a complete module that talks to a **simulated** device, so it
cannot change anything on your machine. `external/rigbay` is a git submodule: the host contract at a pinned commit (no NuGet package exists yet).

If you work inside a clone of the Rigbay repository instead (a contributor adding a module to the monorepo),
`powershell -ExecutionPolicy Bypass -File tools\new-module.ps1 -Id fan-tools` creates `modules/fan-tools/` from `samples/Rigbay.SampleModule`.

### 3. Rename it

```powershell
powershell -ExecutionPolicy Bypass -File tools\rename.ps1 -Id fan-tools -Name "Fan tools" -Author "A. Person" -WhatIf
powershell -ExecutionPolicy Bypass -File tools\rename.ps1 -Id fan-tools -Name "Fan tools" -Author "A. Person" -Description "Sets my fans." -Build
```

The id is lowercase words joined by `-`, 2 to 40 characters. It is the folder name, the prefix of every language key (`module.fan-tools.*`), the
settings folder and the `rigbay fan-tools <verb>` word. Choose it once: changing it later makes the host treat it as a different module. The script
renames the id, the namespace (`FanTools`), the folders and the texts, refuses to run on a dirty tree, and `-Build` builds and tests the result.

### 4. Read the manifest and write an honest `requires`

`module.json` is read before any code is loaded. The fields are in [ABSTRACTIONS.md](ABSTRACTIONS.md) section 2.1. The ones people get wrong:

- `hostApi`: `">=0.1 <0.2"` while the host API is 0.x; any minor version of 0.x may break modules, so you rebuild for each.
  If you use a member added after the first release (a revision of the host API: `ToastMessage.Origin` is `0.1.1`, the `ReadingRow` hints are `0.1.2`), declare `">=0.1.n <0.2"`; the list is in [ABSTRACTIONS.md](ABSTRACTIONS.md) section 11.
- `group`: one of `power`, `cooling`, `network`, `display`, `monitoring`, `other`. You cannot add a group.
- `requires`: **write down everything the module reads or writes**: `admin` (does the module depend on being elevated), `network` (ports, hosts), `registry`
  (keys, and whether read or write), `services` (Windows services it talks to), and `probePaths` for files it loads from another product's folder.
  The user sees it first; the registry entry copies it; the installer refuses a module that declares more than its entry. A manifest that hides what the
  code does is the one thing that gets a module removed from the list.
- `icon`: `icon.svg`, from the neutral tile in the template until you draw your own (see `BRAND.md` section 10: one accent colour, a plain drawing, no
  third-party marks).

### 5. Understand the entry class

Open `src/<Namespace>/Module.cs`. It is commented to be read top to bottom.

| Method | Rule |
| --- | --- |
| constructor | Sets fields. No device access, no files, no threads. |
| `Probe()` | "Does this machine support me?" Read-only, quick (10 s). `ProbeResult.No("probe.no-service")` carries a language key with a sentence the user can read. Throwing is a fault, not "unsupported". |
| `Initialize(host)` | Register things: settings, metrics, commands, timers. Never write to the machine here, except to restore something the user chose. |
| `GetPageModel(host)` | The page, as data: groups of rows. The host draws it, translates it, makes it searchable and owns the enable switch. |
| `Shutdown()` | Stop timers, unhook. 5 s limit. The host removes hotkeys, tray items, commands and subscriptions itself. |

Everything you hand the host (a value setter, a button, a command, a timer callback) runs inside a fault boundary: an exception **faults the module**
(it is switched off, the exception goes to the log). Expected failures ("the service is not running") are results, not exceptions. Threads and timers
you start yourself are *outside* the boundary: catch exceptions there, or the process dies. Reach the UI thread only through `host.RunOnUi`.

### 6. Put the device behind an interface

Your module will talk to something: a WMI class, a service, a driver, a named pipe, a file. Put that behind a small interface (`IDevice` in the
template) and keep the module class free of it. The tests then use a fake that can go missing, fail, lie or throw. Reads are free; writes follow the
safety rules below.

### 7. Build the page, the metrics and the language files

A page is `RowGroup`s of rows (`ToggleRow`, `ChoiceRow`, `NumberRow`, `TextRow`, `ShortcutRow`, `ButtonRow`, `ValueRow`, `ReadingRow`, `ExpanderRow`,
`InfoRow`; [ABSTRACTIONS.md](ABSTRACTIONS.md) section 5). Every text is a **language key** relative to your module: `"row.level.header"` is
`module.fan-tools.row.level.header` in `lang/en.lang`. English is the reference; add `vi.lang` or your own language. Names that come from data
(profile names, device names) go through `Text.Literal(name)`.

Publish readings on the metrics bus and say once what they are called: `host.Metrics.Describe(new MetricDescriptor(id, "metric.level", MetricUnit.Count, "0"))`.
Metric ids are lowercase dotted words; start yours with the module id unless you publish a shared one such as `cpu.temp`. A reading that fails is
**withdrawn** (`host.Metrics.Withdraw(id)`), not left at its last value. Poll fast only while somebody can see the result (`host.PageVisible`,
`host.Metrics.SubscriberCount`); the template has the pattern.

A `ReadingRow` costs you nothing to make visual: the host draws a sparkline in the row, up to four tiles and one trend chart above the rows, from the readings you publish (a unit in
the `MetricDescriptor` is all it needs; temperatures, power and usage are grouped by unit). To steer it, set the optional `Tile`, `ChartGroupKey`, `RangeMin` and `RangeMax` on the row
([ABSTRACTIONS.md](ABSTRACTIONS.md) 5.7); a chart has one axis, so readings in different units never share a group. The host keeps the history, you keep nothing.

#### Words on the page

People open your page to do one thing, often in a hurry. Every line they have to read before they find the control is a cost. Write the
language files to these rules; a registry reviewer reads them too.

| Text | Rule | Good | Not |
| --- | --- | --- | --- |
| Row header | A noun phrase of at most 4 words, sentence case. | `Fan mode`, `Power limit PL1` | `Set The Fan Mode Here` |
| Row description | Only when it adds a fact the header does not have. One line, at most 70 characters. Leave it out otherwise. | `Wait before the fan follows a temperature change` | `This setting changes the fan mode of your laptop.` |
| Button | A verb, with its object when the row does not already name it. | `Run test`, `Start recording`, `Delete profile` | `OK`, `Go`, `Submit` |
| Choice options | Short labels, at most 16 characters, so the host can draw 2 to 4 of them as a segmented control. | `Auto`, `Maximum` | `Automatic fan control (recommended)` |
| Status value | Say the state. With nothing to report, publish an empty value. | `Connected`, `Set to Maximum at 14:02` | `None`, `–`, `No change made yet` |
| Effect sentence (`HardwareEffect.DescriptionKey`) | One sentence: what changes on the machine and what the user will notice. At most 90 characters. | `Sets the fans to maximum. They get loud.` | `Rigbay will ask before it changes your hardware.` |
| Notification (`ToastMessage`) | Title: the thing (1 to 3 words). Body: the result in one sentence. | `Fan mode` / `Set to Maximum.` | `Success!` / `The operation completed successfully.` |

- **Say a thing once.** Do not repeat the module's name, the header or the group in a description. Do not write "changes your hardware" or "Rigbay asks
  first" anywhere: the host marks every row that has a `HardwareEffect` itself.
- **The host builds the question.** The approval dialog's title is made from the row: "Set *header* to *new value*?", with the first letter of the header in
  lower case ("Set fan mode to Maximum?"). The capital stays when the first word is an acronym or mixed case (`CPU`, `PL1`) or a name your effect sentence also
  writes with a capital in mid-sentence (`Steam`). The dialog adds the module's name, the current value and whether it can be reverted, so the effect sentence
  must not.
- **One word per idea, the host's words** (in text the user reads; the API keeps its own names such as `Hotkeys`): module (not plugin, add-on), turn on /
  turn off (not enable, activate), apply, revert (not undo, roll back, reset), shortcut (not hotkey), PC (not machine, device, computer) when you mean the
  user's computer.
- **No marketing, no filler.** No "simply", "easily", "powerful", no exclamation marks, no emoji.
- **Translations follow the meaning.** Write each language as a person who speaks it would say it, to the same rules and lengths; do not translate word for word.
  Keep every `{0}` placeholder.

Check the language files with the host's own tool; it finds missing keys, typos, placeholder mismatches and keys nothing uses:

```powershell
dotnet run --project external/rigbay/src/Rigbay.LangCheck -c Release -- src/FanTools/lang --module fan-tools --source src --strict
```

### 8. Test it, then try it in the app

Unit tests use a fake `IHostServices` (the template's `Fakes.cs`): drive `Probe`, `Initialize`, the rows' values and `Shutdown` the way the host does, and
assert what the module did. Test every write path: allowed, refused, ignored by the device, unreadable afterwards, throwing. The template also tests
that every key on the page exists in every language file, that the manifest is valid and honest, and that the icons are plain drawings.

```powershell
dotnet build FanTools.slnx          # 0 warnings, 0 errors
dotnet test FanTools.slnx
```

Then load it in a **scratch** copy of the app:

```powershell
powershell -ExecutionPolicy Bypass -File tools\stage.ps1 -ModulesDir D:\scratch\rigbay-modules
Rigbay.App.exe --data D:\scratch\rigbay-data --modules D:\scratch\rigbay-modules
```

`--data` and `--modules` move Rigbay's settings, logs and modules folder, so nothing real is touched. The module is listed *Off*; read what it declares, turn
it on, use the page. When something does not appear, read `rigbay.log` in the data folder first: manifest problems, refused API ranges and faults all
end there. `Rigbay.App.exe --data ... --modules ... list` prints every module's state without a window.

### 9. Release

Push your repository (public, MIT) and let CI run; the template's workflow builds, tests and runs the language check, with a read-only token and actions
pinned by commit SHA. Raise `version` in `module.json` and `Directory.Build.props`, commit, wait for a green run on that commit, then:

```powershell
powershell -ExecutionPolicy Bypass -File tools\package.ps1 -CheckReproducible -Verify
gh release create v0.1.0 dist\fan-tools-0.1.0.zip --repo <you>/module-fan-tools --prerelease --target <commit> --title "Fan tools v0.1.0" --notes-file notes.md
gh release download v0.1.0 --repo <you>/module-fan-tools --pattern fan-tools-0.1.0.zip --dir dl
Get-FileHash dl\fan-tools-0.1.0.zip -Algorithm SHA256       # must equal the hash package.ps1 printed
```

`package.ps1` builds from a fresh clone of your committed `HEAD`, lays out the zip the installer expects (`module.json` at the top, your DLLs, `lang\`, icons,
`LICENSE`, `README.txt`; no `Rigbay.Abstractions`, no PDBs), refuses a zip that holds a path or name of your machine, builds twice for the same hash, and with
`-Verify` runs the host's own installer code and loader on it (the module is loaded and probed, never turned on). It is the logic of `tools\package-module.ps1`
in this repository, described in [RELEASING.md](RELEASING.md). Write the release notes honestly: what the module does, what you **verified**, what you did **not**
(hardware first), the hash. A published release is never edited; a fix is a new version.

### 10. Get listed

Open a pull request against [`rigbay-app/registry`](https://github.com/rigbay-app/registry) that changes only `registry.json`: run
`node scripts/entry.mjs <the DOWNLOADED zip> module.json <release asset address>`, paste the entry (a new module at the end of `modules`, a new release at the
end of its `versions`), run `node scripts/validate.mjs`, and fill in the pull request template. The check (CI runs the same) verifies: valid JSON and schema;
`id` as in the manifest, not reserved, unique; `repo` and `zip` are `https://github.com/...` addresses of the same repository; semantic versions, strictly
ascending; `sha256` is 64 lowercase hex digits; `size` at most 64 MB; `hostApi` a valid range; `released` a date; `declares` well formed; and that no
published release was changed. A maintainer then reads the source, the hash against the asset, and whether `declares` and the text are honest. Nothing
merges automatically. Details and the exact commands: [RELEASING.md](RELEASING.md) section 7, [REGISTRY.md](REGISTRY.md).

## Safety expectations for modules that change hardware

Reading is free. Anything that **writes** to the machine (fan speed, performance or turbo mode, power or charge limits, EC or WMI methods that change
state, drivers, services, registry values that control hardware) is held to these rules. They are what a reviewer looks for.

1. **Write only calls you have decoded and understood.** You know what the method does, what each argument means and what range is valid, from the
   vendor's documentation, from your own reading of an installed component for compatibility, or from measurements. You do not guess a method name, a register
   or a value, and you do not "try one to see what happens". Do not copy a vendor's files or code into your repository.
2. **An allow-list in code.** The module lists every value it will ever send (the template's `Level.Allowed`). Anything else is refused inside the module,
   whatever the page, a command, a hotkey or a settings file says. Clamp and validate numbers there too; the host's range check is not your safety net.
3. **Read back, or say unknown.** After a write, read the state again and show what the device reports, not what you asked for. If you cannot read it,
   the page says "unknown" and the metric is withdrawn. Never show a value you did not read.
4. **A revert.** Every change has a way back that you have tried, and the row offers it (`RevertDescriptionKey`, `RevertAsync`). **Never run a write you
   cannot undo.** The revert is a write too: the host asks again, naming the exact current and previous value. `SafeRevert = true` skips the second
   question and is for changes that are harmless in both directions (a lighting colour), never for fans, power, voltage or charge.
5. **Mark the effect.** A row that changes the machine sets `HardwareEffect` (with `Kind = EffectKind.SystemSetting` for a proxy or registry setting that is not hardware),
   so the host shows the effect, asks for confirmation and remembers the previous value. A write the module starts itself (a hotkey, a command) asks through
   `host.Approval.ConfirmAsync`; when the host has no way to ask, the answer is no and you do nothing.
6. **No surprises at start.** `Initialize` does not write, except to restore a value the user chose. A headless command never changes hardware unasked.
7. **Failure is a result.** A device that is missing, busy or refuses gives a failed result and a sentence for the user; the row goes back to what the device
   says. Exceptions are for bugs.
8. **The owner of the machine approves live writes.** Testing a write on your own machine is your choice; asking anyone else to run it, or shipping a
   module that writes on a model you never tried, is not. Say in the README and the release notes which machines it was tried on and which writes were run
   live. "Tested on one machine" is an honest sentence; "works on all" is not. Mark the rest **not verified**.

## Where to look next

- A fuller module: `samples/Rigbay.SampleModule` (hotkey, tray item, toast, commands, expander with a refreshing list, `SafeRevert` and button revert) and the real
  modules `rigbay-app/module-recorder` (the smallest) and `module-predatorsense`, `module-xtu`, `module-steam-bypass`.
- The module contract: [ABSTRACTIONS.md](ABSTRACTIONS.md), section 12 (rules for module authors) and section 13 (how to write a module).
- How a module gets its own repository when it grows out of the monorepo: [MODULE-REPOS.md](MODULE-REPOS.md).

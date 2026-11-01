# Rigbay UI direction

Status: proposal, written 2026-11-01 after reading the source of Microsoft PowerToys (MIT, `src/settings-ui`). It
sets the look and the page model of the Rigbay host. It does not depend on the spike result except where it says so.

## 1. What PowerToys Settings does (read from its source, our words)

Structure
- A `WindowEx` (WinUIEx) with `MicaBackdrop`, no other chrome. One `ShellPage` inside it.
- A custom 48 px title bar: app icon, **a search box in the title bar** (Ctrl+F), then the caption buttons.
- Under it a `NavigationView`: left pane, no back button, no settings item. Order: Dashboard, General, a
  separator, then **groups** (System Tools, Windows & Layouts, ...) that expand to the modules. Footer: What's new.
  The pane collapses to icons below about 1000 px.
- The page is a "sheet": a 1 px hairline border, the theme's layer fill, rounded only at the top corners (8 px),
  max width 1000, sitting on the Mica background.
- Every module page has the same skeleton (`SettingsPageControl`): title, an image of the module, the module content,
  then "Learn more" links at the bottom. The content is **one enable card first** (module name, one line of help,
  a toggle at the right), then **groups of setting cards**. When the module is off, the groups below are disabled.
- A setting is a `SettingsCard` (CommunityToolkit): small icon, header, one-line description in secondary text, the
  control at the right (toggle, combo box with a minimum width of 240, number box, shortcut keys). Related options
  fold into a `SettingsExpander`. Cards are 2 px apart.
- The Dashboard lists modules with their on/off toggle and their activation shortcuts, with a sort option.
- Colours: almost none of their own. Text uses the theme's secondary text brush (12 px) for descriptions and 12 px
  bold secondary for small headings; surfaces use the theme's layer and card-stroke brushes; there is one custom
  pair for info bars. Font is the system UI font (Segoe UI Variable). Dark, light and high contrast all come from
  theme resources. Strings come from resource files (`x:Uid`), search is built from an index of the XAML.
- Packages worth knowing: `CommunityToolkit.WinUI.Controls.SettingsControls`, `Segmented`, `Animations`,
  `Converters`, `WinUIEx`, Windows App SDK.

Why it feels the way it does: it uses stock Fluent almost untouched, so it looks like part of Windows 11. The
"vibe" is restraint, uniform rows, and the same page skeleton everywhere.

## 2. What Rigbay takes, and what it changes

Take
- Mica window, 48 px title bar with search, left `NavigationView` with expandable groups, sheet with top-rounded
  corners, theme resources only (dark/light/high contrast for free), system font, 12 px secondary text.
- One page skeleton for every module: **enable row, then groups of setting rows, then links**.
- Row anatomy: header, one-line description, control at the right. Expander for related options.
- Dashboard as the home page, global search over modules and their settings.

Change (the `impeccable` and `design-taste-frontend` rules, applied to a native app)
- **Rows, not cards.** A `SettingsCard` is already a flat row with a hairline; keep it that way: no shadow, no
  per-row icon tile, no rounded decorative boxes. Icons only when they help scanning (modules in the pane), not on
  every setting.
- **No module hero image.** PowerToys shows a picture per module; Rigbay shows the module's live state there.
- **Colour carries data.** Accent = the user's Windows accent, used for the primary action and the selected item
  only. Fixed meaning for state colours (ok / warning / error), and one hue family for readings (temperature warm,
  power another). Everything else stays neutral. Text at least 4.5:1 in light and dark.
- **Honest states.** Every module row shows one of: running, off, unsupported on this machine (with the Probe's
  reason), failed (with the error and a "view log" action), update available, restart required.
- **A product-specific element:** the Dashboard is a **bay rack**: one slim row per installed module with its state
  and its one or two most important live readings on the right (for example `CPU 61 °C`, `PL1 45 W`, `Fan auto`),
  tabular figures, a hairline sparkline. This is what PowerToys does not have, because Rigbay's modules produce
  readings (metrics bus). Empty rack = a clear "No modules installed" with one button to the Modules page.
- Short copy; verbs on buttons; no marketing text in the app.

## 3. Window and navigation

```
+--------------------------------------------------------------------------------+
| [icon] Rigbay        [ Search modules and settings          Ctrl+F ]   _  []  x |  48 px, Mica
+--------------+-----------------------------------------------------------------+
| Dashboard    |  Dashboard                                                       |
| Modules      |  ----------------------------------------------------------------
| ------------ |  CPU            61 C  ~~~~~~~~     PL1  45 W   PL2  65 W            |  live strip (metrics bus)
| Power        |  ----------------------------------------------------------------
|   XTU        |  Installed modules                                  Sort: Name  v |
|   Predator.. |  XTU profiles           Running    Profile "Quiet"        [on ]  |
| Network      |  PredatorSense          Off        Probe: supported       [off]  |
|   Steam      |  Steam access           Running    Proxy on :18081        [on ]  |
| ------------ |  Sensors recorder       Unsupported  Needs an XTU source          |
| Settings     |                                                                  |
| Log          |  (sheet: hairline border, layer fill, top corners 8 px)          |
+--------------+-----------------------------------------------------------------+
```

- Pane groups come from the module manifest (`group`), so modules can sit under Power, Cooling, Network, Display,
  Monitoring, Other. A group with no installed module is not shown.
- Fixed items: Dashboard, Modules (browse, install, update, remove), Settings (language, theme, start with
  Windows, log level), Log. Pane footer: version and "restart required" badge when relevant.
- Tray menu and hotkeys are host features; their settings live on the module page and on a Hotkeys list under
  Settings (shows conflicts across modules).

## 4. Module page skeleton

```
 PredatorSense                                              [ Learn more ]  [ Log ]
 --------------------------------------------------------------------------------
 Enable PredatorSense                     Fan, performance mode, lighting      [on]
 --------------------------------------------------------------------------------
 Cooling                                                                  (group)
   Fan mode                  Auto, max, custom                    [ Auto      v ]
   Fan curve                 Edit the CPU and GPU curve                  [ Edit ]
   > Advanced                                                       (expander)
 Performance                                                              (group)
   Mode                      Quiet, default, performance, turbo   [ Default   v ]
 --------------------------------------------------------------------------------
 Readings                                                               (live)
   CPU temp   61 C    CPU fan   2800 rpm    GPU temp   54 C    GPU fan  2400 rpm
```

Rules: enable row first and always; groups below are disabled while it is off; a write that changes hardware shows
what it will do before it applies, and a "Revert" action next to the current value. Hardware write rows follow the
approval rules in `docs/DESIGN.md`.

## 5. What this means for `Abstractions`

PowerToys pages are, almost without exception, rows made of: icon, header, description, one control. That suggests the
module-contributes-a-page contract should have **two levels**:

1. **Declarative page model (default).** The module returns a tree of `Page { Groups { Rows } }` where a row is one
   of: toggle, choice list, number, text, shortcut, button, read-only value, live reading, expander, info bar. No
   WinUI types in `Abstractions`; values bind through a small observable interface. The host renders it with
   `SettingsCard`-style rows, so every module looks the same, follows theme and DPI, is searchable (the host indexes
   header and description), and is localised by key. It also avoids the hardest part of the spike (XAML resources of a
   dynamically loaded assembly).
2. **Custom page (escape hatch).** For things a row cannot express (fan curve editor, a graph, the Steam activity
   log) the module supplies a custom control through a host-provided factory, built in code, using the host's
   styles. Used sparingly. Whether this works across the load-context boundary is exactly what spike A1 tests.

The spike result decides the exact shape of level 2; level 1 does not depend on it.

## 6. Implementation notes

- Stock controls first: `NavigationView`, `SettingsCard`/`SettingsExpander` (Community Toolkit), `ToggleSwitch`,
  `ComboBox`, `NumberBox`, `InfoBar`, `TeachingTip` for first-run hints. No custom control theme unless a reading
  display needs one.
- Language files drive every string, including module rows (`module.<id>.<key>`); English fallback; `--langcheck`.
- Widths: sheet max 1000 px, comfortable at 640. Pane collapses to icons under 1000 px. Check 100 %, 150 %, 200 %.
- Motion: only what Fluent gives (page transition, expander). Nothing animates while idle.
- Verification: render the real window to PNG (PrintWindow) in dark and light, empty, error and populated states,
  and read the images.

## 7. Open points

- Does the Community Toolkit `SettingsControls` package work in an unpackaged, elevated app with dynamically loaded
  modules (spike A1 will say).
- Search: index built at runtime from the page models (cheap) versus build-time XAML index (what PowerToys does).
  Runtime is the only option for modules we do not compile.
- Whether a module may add its own pane group. Proposed: no, only choose from the fixed list.

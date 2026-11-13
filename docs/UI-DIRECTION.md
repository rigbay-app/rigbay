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
  tabular figures, a hairline sparkline, built in V1: section 8. This is what PowerToys does not have, because Rigbay's modules produce
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

(The first skeleton, kept for the record. Since the UI review of November 2026 the page is built as section 9 describes.)

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

## 8. Readings made visual (V1)

Owner feedback, 2026-11-12, on the real machine: the XTU page was a long list of text rows ("no charts, too much text, nothing visual"). Acer's own app shows
round gauges for CPU, GPU and system temperature. The host now draws a reading four ways, from its own history of the metric (`docs/ABSTRACTIONS.md` 5.7), so every module gets it without a release.

| Where | What | Why this form (data-viz rules) |
| --- | --- | --- |
| Row | A hairline **sparkline** left of the number, the last 5 minutes at most | "Change over time" next to a single current value; no axes, the number beside it is the label. 1.5 px line, one end dot. |
| Page top | Up to four **tiles**: name, big number + small unit, sparkline, a thin meter for a temperature or a percent | A single current value is a stat tile, not a bar. The number wears the text colour; the hue is on the marks. |
| Page | **One trend chart** with a tab per group (temperature, power, usage, clock, fan speed) and a 1 / 5 / 10 minute range | One axis, never two: a group is one unit. 2 px lines, faint solid hairline grid, a crosshair with a tooltip (time and every value), a legend with the latest values for two or more lines, end dot with a surface ring. |
| Dashboard | A sparkline between the label and the number of each key reading | What this document promised in section 2; it drops out of a row narrower than 640 px. |

**Tile, not round gauge.** A gauge was considered and left out. It spends about four times the area of a tile on one number, and it shows nothing about where the number is going; at idle a CPU sits
at 40 to 60 °C, where the needle looks the same all day. A tile gives the same number as large as Acer's, the trend next to it, and a thin meter (0 to 100 °C, or a module's own range) for the
"how close to the limit" question. A temperature at 90 °C or more adds the word "High" and an icon, so the state is never colour alone. A module that wants a gauge can still build one as a `CustomRow`.

**Colour.** One hue per kind of reading, as before (temperature warm, power blue, speed teal, load amber: `RbReading*Brush`), used for the line and the meter (no area since section 9: an area says "a quantity from zero" and the scale is zoomed) of a
single series. A chart with two or more lines uses the documented categorical order in fixed order (blue, orange, aqua, amber, magenta, green; `RbSeries1..6Brush`), checked with the data-viz validator
(`validate_palette.js`, light surface `#f9f9f9`, dark `#272727`): all six lightness, chroma, adjacent colour-blind and normal-vision floors pass in both themes; aqua and magenta (light) and amber (light) are below 3:1
on the light surface, which the rule allows because every line is also named in the legend with its value, in the tooltip and in the page's reading rows (the table view). Text never wears a series colour in the chart.
Under Windows high contrast every hue becomes the window text colour and the lines are told apart by dash patterns.

**Honest scale.** The scale follows the data with 6 % room but never below a smallest span per unit (10 °C, 10 W, 20 %, 500 rpm, 400 MHz), so 1 W of noise is a flat line, not a full-height one; a percent stays
inside 0 to 100 and units that cannot go negative stay above zero. A hole in the data (a module that sleeps while nobody looks) is a gap in the line, never an interpolated one. A chart is not drawn until one of its lines has two points (section 9).

**Motion.** The block fades in once (160 ms, ease-out) unless the Windows "Animation effects" setting is off; numbers do not tween and lines do not animate. Nothing is drawn or timed while the page is not on screen.

## 9. The module page after the UI review (November 2026, package B)

The review (`docs/ui-review/REVIEW-2026-11.md`) found the module page long and wordy for three reasons: the module's main job was not at the top, the
same fact was said several times (a reading as a tile, in the chart and again as a row; the hardware sentence under every row that writes), and rows
said nothing ("Last action: No change made yet", "–"). The host now builds every module page in this order; every block is optional and an empty
block is not drawn (Sketch 1 of the review):

```
 [tile] XTU profiles                                                        Learn more  Log
        ● Running · Balanced · PL1 55 W, PL2 60 W, 56 s                               On [●]
        Settings marked (shield) change your hardware. Rigbay asks before each change.
 ----------------------------------------------------------------------------------------
 Profile (shield) (i)                                             Applied. Now Balanced  Revert
 [ Quiet | Balanced | Performance | Custom ]
 [CPU temperature 79 °C] [Package power 49.2 W] [CPU usage 36 %] [Clock 4,200 MHz]   <- the chart's tabs
 Package power                                                  [Compare]  1  5  10 min
 ~~~~~~~~~~~~~~~ one line in the tile's hue, PL1 55 W - - - - - - - - - - - dashed, labelled
 ----------------------------------------------------------------------------------------
 Groups of rows (a row that says nothing is not drawn; a group with nothing left neither)
 > All readings  (every reading as a 32 px line: name, sparkline, value)
```

- **Header card** (Windows Settings' Bluetooth card). The module's tile and name, a state dot with the state word and the module's own live state line,
  the switch with its state in words, the links small at the top right. While the module is off the card also says what it does. Id, version and
  author belong to the Modules page. A failed module gets one sentence by the phase that failed, "Restart and try again", "View log" and its error
  folded under Details.
- **Main card**: the module's `PageModel.Primary` first and large (a short choice is a segmented control, the stock `RadioButtons` drawn as
  segments: one tab stop, arrow keys, "2 of 3" for a screen reader; a drop-down when the window is too narrow for the segments or the options are many
  or long; no "Not set" option, nothing is selected while the value is not an option), then the tiles, then the chart.
- **The tiles are the chart's tabs** (Task Manager). The selected tile wears a 1.5 px accent border over a faint accent tint; the chart draws its
  reading in the tile's hue and the limits of its group as dashed reference lines labelled at the line; Compare adds the other readings of the group
  in the categorical order with a legend. One subject keeps one hue on the screen; one chart, one axis.
- **Said once.** A row that writes wears a small shield after its name; its tooltip and accessible help say what it changes. The header card explains
  the shield once for the page (the hardware wording, the Windows-setting wording, or both). A longer explanation of a row goes to an info glyph
  (`Row.HelpKey`), not to a third line under it.
- **Rows that say nothing are not drawn**: a `ValueRow` whose value is empty or only a dash, and a group with nothing left. A live state belongs in
  `Row.Line` (on any row, and in an expander's closed header, with an optional `ExpanderRow.HeaderRow` control there).
- **Numbers** in the current language's format, thousands grouped, byte rates scaled to KB/s and MB/s; the chart's top tick names the unit.
- **Narrow window (640 px)**: the header card wraps, the segmented control becomes a drop-down below its width, the tiles go 2 by 2, the chart's range
  goes under its title. Nothing is cut.

Colour and contrast (`dataviz`): a single line wears its reading's hue (`RbReading*Brush`, 5.5 to 8.7:1 on the cards in both themes); the
categorical order (`RbSeries1..6Brush`) is used only when Compare draws several lines, each named in the legend with its value and in the tooltip,
so the light amber, aqua and magenta under 3:1 never carry identity alone.

Kept on purpose: the tile (not a round gauge, section 8), the approval and Revert flow, the 1/5/10 minute range, the crosshair. Not done: big
"cards" for each option of a primary choice (Sketch 3's two large Auto/Maximum cards need a per-option description the page model does not have; the
segmented control at 38 px is the big picker), and an observable button label (a module swaps `ButtonKey` with a page refresh).

## 10. Lessons from the second PowerToys study (November 2026)

The second study (`docs/research/POWERTOYS-2026-11.md`, section 2) read PowerToys' runner, Settings and Quick Access. These are the lessons Rigbay
adopts; each is size S. Not adopted, with reasons in the study: an OOBE window, a Quick Access flyout in its own process, Group Policy.

| Lesson | Rule for Rigbay |
| --- | --- |
| Search ignores accents | The query and the index are folded the same way: lower case, Unicode form KD, combining marks dropped, `đ` mapped to `d`. "nhiet do" finds "Nhiệt độ". A match at the start of a word ranks before one inside a word. |
| A shortcut is checked with Windows while it is captured | The capture dialog tries the chord with the OS before Set is pressed (register, then release at once) and says "Windows or another app already uses this shortcut" next to Set. While any shortcut conflict exists, the Dashboard header says "1 shortcut conflict" and opens Settings > Hotkeys; when there is none, nothing is shown. |
| What changed, once | After the host or a module changed version, one bar at the top of the Dashboard names the new version and links to its release notes; it closes for good with its close button. No window and no download at start-up. |
| The tray carries the main controls | Each running module's tray submenu shows its `PageModel.Primary`: a short choice as radio items with the current one marked, a toggle as a check item, a button as an item. The same approval and Revert apply; an approval opens the window. Left click still opens the window. |
| Landmarks and ids for UI Automation | The pane is a navigation landmark, the page the main landmark, the search box a search landmark. Every control the self-test drives has an `AutomationId`, so tests do not depend on English names. "Copied" and "Applied" are announced with `RaiseNotificationEvent`. |
| One hint the first time a module is on | The first time a module is turned on, a `TeachingTip` points at its main control: what it does, that Rigbay asks before each hardware change, where Revert is. Once per module, remembered in `app.json`. |

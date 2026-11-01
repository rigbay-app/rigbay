# Rigbay brand: logo concepts (checkpoint)

Status: **concept checkpoint, task L1, 2026-11-01.** Nothing here is final and no kit has been built. The owner picks a
direction first; the section "What the kit contains" lists what gets built after that.

Files: `assets/brand/concepts/` (SVGs, the three overview sheets as HTML + PNG).

| Look at | File |
| --- | --- |
| Rigbay, three concepts | `assets/brand/concepts/overview-rigbay.png` |
| Module marks, two directions | `assets/brand/concepts/overview-modules.png` |
| One-colour, reversed, squint, mirror, shelf, family | `assets/brand/concepts/overview-tests.png` |

## 1. Brief and assumptions

Rigbay is a Windows tuning and control host: one window, one tray icon, modules that plug into "bays". The look is
Windows 11 Fluent (Mica, flat, hairlines), close to PowerToys Settings in structure (`docs/UI-DIRECTION.md`).

Nobody was available to answer questions, so these are my assumptions:

- **Audience:** one owner today, later other people who write modules for their own machine. Technical, Windows 11,
  dark and light themes. The mark is seen small (title bar, tray, pane, module rows) far more often than large.
- **Adjectives:** precise, calm, modular, trustworthy with hardware, modern but not flashy. Not "gamer", not
  "enterprise".
- **Name** is `Rigbay`, lowercase `rigbay` in the wordmark. The idea of the name (a bay is a slot a module plugs
  into) is the idea of the mark. The app already shows three vertical bays in the title bar.
- **Colour is a function in the app.** Green, red and amber are state colours (running, failed, warning), so none of
  them is a brand hue. The app follows the Windows accent, so the mark has one accent element that can be bound to the
  accent colour in the UI; the fixed brand accent is only for icons, the README and the website.
- **Trademarks:** no Intel, Acer, Predator or Steam logo, shape, colour pairing or word inside any mark. The module
  marks show what the module does (sliders, a fan, a detour), not whose product it works with.
- **Not in scope here:** the full kit, guidelines, website, a final wordmark, trademark clearance.

### What I found in the repo that matters for the brand

- The title bar draws three bays: two outlined, one filled with the Windows accent (`tools/shots/dashboard-dark.png`).
  At 16 px the outlines are 1 px lines.
- The **module rows already use a vertical bar as state** (outlined = off, filled green or red = running or failed).
  A module icon must therefore not be a tall thin vertical bar, or it fights the state bar next to it. Concept A is the
  closest to that and the reason for its risk note below.
- `tools/make-icon.ps1` (placeholder icon) fills the third bay with **green** (47,182,124). Green is a state colour in
  the app, so the placeholder should change once a direction is chosen. That file is outside my area; noting it only.

## 2. Category audit

I could not run `search_library.py` (no Python on this machine, see section 8), so I queried the library's
`classifications.json` with Node and looked at the SVGs directly.

- **Rigbay's neighbours** (Windows utilities): PowerToys is a bright, multi-colour toolbox tile; MSI Center and
  Armoury Crate are black and red, angular, aggressive; HWiNFO is a dense chip-and-graph look. None of them is in the
  library, and I do not copy any of them. For the shelf test I drew four **generic stand-ins** from those habits
  (clearly labelled on the sheet) and, for my own eyes only, used real marks from the library that sit near the ideas
  (Windows, VS Code, Terminal, Docker, retool, gitter, Power BI, Data Studio, Stencil).
- **Clichés to avoid:** a gear, a wrench, a chip with pins, a lightning bolt, a shield, a flame or dragon, a
  speedometer, green-on-black "hardware" colours.
- **Library finds:** three upright bars are crowded (retool-icon, gitter, Power BI, Data Studio, Stencil). Nothing
  resembles a U-shaped bay with a block lowered into it; the nearest is Stack Overflow's tray with fanned bars, a
  different gesture. Many R marks exist (Remix, Riot, Rust, R language, Rax, Rollup, Heroku Redis).
- **Rigbay's own gap:** the neighbours are saturated or aggressive tiles. A flat, mostly-ink mark with one calm accent
  is distinct on the shelf and sits comfortably in the Windows 11 look.

## 3. Shared construction (all marks)

- Canvas 256, grid unit 8. Stroke weight 24 (wordmark and glyphs) or 20 (outlined bays); **all terminals are pills**
  (round ends, radius = half the stroke); containers use **outer radius 56 and inner radius 32** (concentric, stroke 24).
- **One accent per mark**, flat, no gradients. Ink is `#1F2430` on light and `#EEF1F8` on dark.
- Every mark has a **small cut** for 24 px and below (heavier strokes, no hollow bays, mid-slate ink).

### Colours

All checked for contrast as graphic objects against Mica dark (`#202020`, `#2B2B2B`) and light (`#F3F3F3`, `#FFFFFF`).

| Role | Name | HEX | on `#202020` | on `#F3F3F3` |
| --- | --- | --- | --- | --- |
| Rigbay accent (fixed brand) | Bay Violet | `#7A5AF8` | 3.6 : 1 | 4.1 : 1 |
| XTU profiles | Blue | `#2F7BFF` | 4.2 : 1 | 3.5 : 1 |
| PredatorSense | Magenta | `#D946B8` | 4.3 : 1 | 3.4 : 1 |
| Steam access | Cyan | `#0A8FB0` | 4.3 : 1 | 3.4 : 1 |
| Template, tray ink | Slate | `#7B8497` | 4.3 : 1 | 3.4 : 1 |
| Ink (light / dark) | | `#1F2430` / `#EEF1F8` | | |

Hues sit at about 265°, 215°, 320°, 190° and neutral: none is green, red or amber, and none is the default Windows
blue (`#0078D4`). Blue and cyan are neighbours; the glyph, not the hue, tells them apart. Tint values for CMYK and
Pantone are not done (kit phase). Slate lands between 3 : 1 on both Mica extremes, so a **single fixed tray file** is
possible without switching icons by theme.

## 4. Rigbay: three concepts

Each concept comes as symbol, lockup (symbol + wordmark), small cut and app-icon tile. The wordmark is the same for
all three: lowercase `rigbay`, a monoline geometric build with 24-wide strokes and pill ends, single-storey `a` and
`g`, the **dot of the i in the accent** (the lit bay). It is built from strokes in these concept files and gets
expanded to outlines in the kit.

| | Concept | Type | Idea |
| --- | --- | --- | --- |
| A | **Rack** | abstract | Three bays: two empty (outline), one occupied (solid accent). The state language of the module rows, drawn as a mark. |
| B | **Dock** | abstract | A U-shaped bay seen from the front with a module block lowered into it. "Plug a module in" in one gesture. |
| C | **R-bay** | letterform | A rounded R whose leg is the module in the accent. |

Files: `rigbay-a-rack-*.svg`, `rigbay-b-dock-*.svg`, `rigbay-c-r-*.svg`, each with `-symbol`, `-lockup`, `-small`,
`-appicon`.

### Recommendation: **B, Dock**

One line: it is the only concept that says what Rigbay does (a module goes into a bay) without a letter or a generic
bar shape, and its single accent block is exactly the element that can follow the Windows accent.

- **A** is the continuation of the current title-bar mark, which is a real advantage, but three bars is the most
  crowded shape in the library and, rotated 90°, becomes a menu icon. Its outlined bays do not survive 16 px (needs
  the small cut with solid bays).
- **C** is the strongest at 16 px because it is a letter, but R is one of the most used initials, and in the lockup the
  R repeats the r of the wordmark.
- **B** risk: it can read as an inbox or download tray. Keep the block a plain block (no arrow) and keep it floating
  inside the bay.

## 5. Module marks: two directions as a family

Module marks: XTU profiles, PredatorSense, Steam access, plus the **module template** (neutral slot, a plus: "an
empty bay, plug something in"). Glyphs share the grid above and are drawn once for both directions.

| Mark | Glyph (what it says) | Accent |
| --- | --- | --- |
| XTU profiles | Two sliders with knobs: power limits and profiles | Blue `#2F7BFF` |
| PredatorSense | A three-blade fan with a hub: fan and performance | Magenta `#D946B8` |
| Steam access | A route that hops over a blocked post, with an arrow: a network workaround | Cyan `#0A8FB0` |
| Template | A plus: an empty bay waiting for a module | Slate `#7B8497` |

- **Direction 1, Tile** (`module-d1-*.svg`): a solid accent tile (outer radius 56) with the glyph knocked out in white.
  The tile radius matches the Rigbay app-icon tile and the Dock mark.
- **Direction 2, Slot** (`module-d2-*.svg`): the empty-bay frame in neutral ink with the glyph lit in the accent.

### Recommendation: **Direction 1, Tile** (and keep a glyph-only quiet form)

One line: it is the only direction that survives 16 px, the glyph is large, and the tile ties directly to the Rigbay
app icon.

- Slot looks calmer on paper but fails the 16 px test: the frame is the heaviest thing in the icon and the glyph
  shrinks by about a third, so below 24 px the frame has to be dropped anyway.
- Tile adds colour to a screen where colour means state. The bay-row mock-up (24 px icon next to the state bar, light
  and dark) holds up; if it feels loud in the real app, use the glyph alone in the accent colour (no tile). The glyphs
  are shared, so both forms come out of the same drawing.
- **Weakest glyph: Steam access.** The arch with an arrow over a post can read as "redo". The kit phase should try a
  second drawing (for example a key or an open lock) before this is frozen.
- The module marks must not become tall thin bars (state bar clash, see section 1).

## 6. Test results

Run on every concept; the evidence is in the PNGs, all looked at.

| Test | A Rack | B Dock | C R-bay | Where |
| --- | --- | --- | --- | --- |
| 16 px, full drawing | fails: outlined bays close to 1 px lines and mush | holds: U and block both visible | holds: reads as R | `overview-rigbay.png`, ×6 pixel view |
| 16 / 24 px, small cut | holds (solid bays) | holds (heavier dock) | holds | same |
| Tray, one fixed slate file, light and dark taskbar | holds | holds | holds | same |
| One colour, black and white | holds (solid vs outline) | holds (block vs dock) | holds, leg merges with R | `overview-tests.png` |
| Reversed on the accent tile | holds | holds | holds (leg in a tint) | same |
| Squint (blur 3 px) | three shapes, last one coloured | U and block | R, leg fades | same |
| Mirror, rotate 180 / 90 | 90° gives a stack of bars (menu icon) | 180° gives an arch with a block, no bad reading | mirror gives a reversed R (expected) | same |
| Greyscale | holds | holds | holds | `overview-rigbay.png` |
| Shelf (generic stand-ins, then real library marks) | crowded by bar marks | stands apart | stands apart, R is common | `overview-tests.png`, scratch image not committed |
| Family: app icon plus four module tiles at 64 / 32 / 16 px | n/a | holds, one radius, one accent each | n/a | `overview-tests.png` |

Module marks: Tile holds at 48, 32, 24 and 16 px in light and dark (fan and sliders are crisp, Steam access is the
busiest). Slot holds at 48 to 24 px, fails at 16 px.

### What I did not do or could not do

- **`svg_audit.py`, `preview_sheet.py`, `concept_sheet.py`, `search_library.py`, `render_png.py`, `export_variants.py`
  did not run: Python is not installed** and I was told not to install it. I replaced them with Node (library query),
  headless Microsoft Edge (all rendering) and hand-built HTML sheets. Nothing essential was blocked at the concept
  checkpoint. For the kit phase the scripts that matter are `export_variants.py` (variant and icon set) and
  `svg_audit.py`; if the owner approves Python 3, they run as they are. Otherwise the same exports can be done with
  Edge and Node, but the `.ico` build needs a small .NET or PowerShell step (as `tools/make-icon.ps1` already does).
- Not tested: real Windows tray rendering at 125 / 150 / 200 % scale, Fluent title-bar placement, high contrast,
  CMYK, embroidery or print. The tray row is a simulation on light and dark strips.
- The SVGs are drawn with fills where simple (A, B, template frame) and with **strokes in places** (wordmark, concept C
  R, module glyphs). Strokes get expanded to outlines in the kit. The marks have no text, rasters, filters or masks.
- Clearance: no trademark or reverse-image search was done. Please search before launch.
- Fonts: none used. The wordmark is drawn; it is not a licensed typeface.

## 7. Using the accent in the app

The app accent colour (Windows) should drive **the one accent element** in the in-app mark (the module block in B, the
third bay in A, the leg in C, the i-dot in the wordmark) and nothing else. The fixed Bay Violet is for the app icon
tile, the README, the website and anywhere the Windows accent does not exist. State colours stay out of the mark.
Module tiles keep their own fixed hue because they identify a module, not the user's theme.

## 8. What the kit contains (after the owner picks a direction)

For the chosen Rigbay concept:

1. Final geometry with optical corrections and the thinned reversed version; wordmark outlined.
2. Colour: palette with HEX / RGB / CMYK, one-colour and reversed files, on-accent version.
3. Lockups: horizontal, stacked, symbol only, wordmark only; clear space and minimum sizes.
4. Small cut, favicon, app icon tile; `rigbay.ico` (16, 24, 32, 48, 64, 256 as in `tools/make-icon.ps1`) and the PNG set.
5. In-app: title-bar mark (follows the Windows accent), tray icon, pane icons for module groups.

For the module marks (chosen direction):

6. Tile and glyph-only forms of XTU, PredatorSense, Steam access and the template; 16 / 20 / 24 / 32 / 48 exports.
7. A **module-author page**: how to make a mark that fits (grid, one accent, tile radius 56, no third-party logos),
   shipped with the `module-template` repo as `icon.svg` plus the template mark.
8. A short usage page (`docs/BRAND.md` grows into it), a README header image, and a test sheet for the final files.

Open decisions for the owner: the concept (B recommended), the module direction (Tile recommended), whether the
Steam access glyph stays, whether Bay Violet stays as the fixed brand accent, and whether Python 3 may be installed
for the kit phase.

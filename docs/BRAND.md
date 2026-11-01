# Rigbay brand guidelines

Status: **kit built for review, task L2, 2026-11-01.** The owner picked the coordinator's recommendations from the L1
concept round: the Rigbay mark is **B Dock** and the module marks are **direction 1 Tile**. This file is the guideline;
the files are in `assets/brand/`. Nothing here is frozen until the owner has commented.

Look first at these (all rendered from the SVG masters and looked at):

| Look at | File |
| --- | --- |
| Whole kit on one sheet | `assets/brand/tests/test-01-kit-overview.png` |
| 16 px pixel test | `assets/brand/tests/test-02-pixel-16.png` |
| The social images | `assets/brand/social/social-preview-1280x640.png`, `org-avatar-512.png`, `readme-banner.png` |
| Steam access: six drawings, one kept | `assets/brand/tests/test-10-steam-explorations.png` |
| Module family | `assets/brand/tests/test-11-module-family.png` |

## 1. The idea

Rigbay is a host with **bays**; a module is something you **plug into a bay**. The mark is that gesture, once: a
U-shaped bay seen from the front, and a block lowered into it. The block is the only coloured element and is the one
thing that can follow the user's Windows accent. Every module mark is then an **occupied bay of its own colour** (a
tile) with one glyph that says what the module does.

Adjectives it has to carry (from the brief): precise, calm, modular, trustworthy with hardware, modern but not flashy.
Not "gamer", not "enterprise". So: flat, rounded, one idea, one accent, no gradient, no effect.

## 2. Decisions taken in this round

| Question left open by L1 | Decision | Why |
| --- | --- | --- |
| Rigbay mark | **B Dock** (as recommended) | The only concept that says what Rigbay does; one accent element. |
| Module direction | **1 Tile** (as recommended), plus a glyph-only quiet form | Survives 16 px; the quiet form is the fallback if tiles feel loud next to state bars (tested, section 8). |
| Steam access glyph | **A key** (`steam-access.svg`) | Six drawings tried at 16 px, see section 9 and `test-10-steam-explorations.png`. |
| Fixed brand accent | **Bay Violet `#7A5AF8`** kept | Hue 252°, 98° from the nearest state colour (green 154°); passes 3:1 on every surface tested. |
| Python | Not used. Node 26 + headless Edge replaced every script of the skill | See section 14. |

Open for the owner: whether Bay Violet stays, whether the key is the final Steam access glyph (the open padlock is
the drop-in alternative), the licence terms for the logo files (section 12), and a trademark search before launch.

## 3. The mark: construction

One grid for everything: **208 × 208 units, cells of 8** (26 × 26). Stroke weight 24. Every terminal is a pill (round
end, radius = half the stroke). Containers use **outer radius 56 and inner radius 32**, concentric.

```
Dock symbol (208 x 208)
  bay    U-shape, wall 24, outer R56, inner R32, arm caps Ø24, arm tops 48 below the block top
  block  80 x 144, corner radius 24, centred, floats: gap 40 to both arms and to the floor
```

`assets/brand/tests/test-09-construction.png` draws it on the grid. Optical decisions:

- The block floats (a gap of 40 on three sides) so it never reads as a download tray: no arrow, no contact with the bay.
- Footprint is exactly square, so the symbol sits centred in every container without adjustment.
- **Reversed** (light on dark) is thinned by 1 unit on every edge (wall 22, block 78 × 142) because light shapes on dark
  look heavier. Compare black and white in `test-03-one-colour-reversed.png`: they read as equal weight.
- **Small cut** for 16 to 24 px (use it below 32): wall 32, inner radius 24, same block. The gaps open from 40 to 32
  and the wall stays 2 px or more at 16 px.
- **Quiet form**: bay in slate `#7B8497`, block in the ink colour, no hue. For places where colour would compete with
  state colours (footers, about boxes, watermarks).
- **One-colour** (black or white) and **accent-variable** files are in section 11.

### Forms of the symbol

| Form | File (in `assets/brand/rigbay/`) | Use |
| --- | --- | --- |
| Colour, light | `rigbay-symbol.svg` | Default on light surfaces |
| Reversed | `rigbay-symbol-reversed.svg` | Default on dark surfaces |
| Black / white | `rigbay-symbol-black.svg`, `-white.svg` | One-colour print, stamps, embroidery, HC |
| Accent variable | `rigbay-symbol-accent.svg` | Inline in HTML or app UI; `currentColor` ink, block = `--rigbay-accent` |
| Small cut | `rigbay-symbol-small.svg`, `-small-reversed.svg` | 16 to 24 px |
| Quiet | `rigbay-symbol-quiet.svg`, `-quiet-reversed.svg` | Where hue must stay out |

### The wordmark

Lowercase `rigbay`, a monoline geometric drawing: stroke 24, round ends, single-storey `a` and `g`, the dot of the `i`
is the second use of the accent (the lit bay). Round letters overshoot the x-height by 1 unit. Built from strokes, then
**converted to outlines** (proved against the stroked drawing: 693 of 609,216 pixels differ by more than 30/255, none by
more than 100, all along the round edges, i.e. anti-aliasing only; `tools/verify.mjs`). The x-height is 42 % of the
lockup height; the wordmark spans exactly the symbol's height (ascender of `b` = block top, descender of `g` and `y` =
bay floor).

Known craft limits, kept on purpose: the rounded stem caps of `r`, `a` and `g` leave a small notch where they meet the
bowl, normal for a rounded monoline. The `y` strokes are at 63.43° (a 1 : 2 slope), a deliberate angle, not a near-miss.

### Lockups

| Lockup | File | Rule |
| --- | --- | --- |
| Horizontal | `rigbay-lockup-horizontal.svg` (+ `-reversed`, `-black`, `-white`, `-accent`) | Symbol and wordmark 80 units apart (one block width). Default. |
| Stacked | `rigbay-lockup-stacked.svg` (+ `-reversed`) | Symbol above, wordmark at 62 % size, 56 units apart. For square spaces. |
| Wordmark only | `rigbay-wordmark.svg` (+ `-reversed`, `-black`, `-white`) | Only where the symbol is already on the page. |

## 4. Clear space and minimum sizes

**Clear space `x` = the width of the module block = 80 units = 38 % of the symbol height**, on all four sides of the
symbol and of any lockup. It is measured on the mark itself, so it scales with it (`test-09-construction.png`).

| Item | Minimum | Rule |
| --- | --- | --- |
| Symbol, small cut | **16 px** | Never smaller; 16 to 24 px use the small cut (`test-02-pixel-16.png`). |
| Symbol, full drawing | 32 px | Below 32 px use the small cut. |
| Horizontal lockup | **24 px tall** (108 px wide) | At 16 and 20 px the `i`-dot and `y` merge; use the symbol alone. |
| Stacked lockup | 48 px wide | Same reason. |
| App icon | 16 px | Small cut up to 24 px, in `rigbay.ico`. |
| Module tile | 16 px | Small cuts exist for the XTU, PredatorSense and Steam access tiles. |
| Quiet glyph | 16 px | No small cut; the glyph is drawn larger (section 9). |
| Print (provisional, **not tested**) | symbol 8 mm, lockup 25 mm wide | Screen product; nothing printed yet. |

## 5. Colour

Only five hues plus two inks. Contrast ratios are computed (WCAG 2.x relative luminance) by `tools/contrast.mjs`;
the full table is `assets/brand/tests/contrast.txt`. A graphic object needs **3 : 1** (WCAG 1.4.11), text 4.5 : 1.

| Role | Name | HEX | RGB | on `#202020` | on `#2B2B2B` | on `#F3F3F3` | on `#FFFFFF` |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Brand accent (fixed) | **Bay Violet** | `#7A5AF8` | 122 90 248 | 3.60 | 3.13 | 4.07 | 4.52 |
| Ink, light theme | Ink | `#1F2430` | 31 36 48 | n/a | n/a | 13.98 | 15.52 |
| Ink, dark theme | Ink Light | `#EEF1F8` | 238 241 248 | 14.41 | 12.52 | n/a | n/a |
| Quiet / template / tray neutral | Slate | `#7B8497` | 123 132 151 | 4.34 | 3.77 | 3.39 | 3.76 |
| XTU profiles | Blue | `#2F7BFF` | 47 123 255 | 4.19 | 3.64 | 3.51 | 3.89 |
| PredatorSense | Magenta | `#D946B8` | 217 70 184 | 4.28 | 3.72 | 3.43 | 3.81 |
| Steam access | Cyan | `#0A8FB0` | 10 143 176 | 4.32 | 3.75 | 3.40 | 3.77 |

White glyph on its tile: violet 4.52, blue 3.89, magenta 3.81, cyan 3.77, slate 3.76. Everything passes; the weakest
pairing is Bay Violet on the lighter dark card `#2B2B2B` at **3.13**. The bay and the block next to each other in the
colour logo: 3.43.

Hues (HSL): violet 252°, blue 218°, magenta 313°, cyan 192°. The state colours in the app are green 154°, red 358°,
amber 37°. (The L1 note said violet was about 265°; the computed value is 252°. Blue and cyan are 26° apart, so the
glyph, not the hue, tells those two modules apart; in greyscale the tiles are almost the same grey for the same reason,
see `test-04-squint-mirror.png`.)

**Not done on purpose:** CMYK and Pantone. Rigbay is a screen product; a computed CMYK would be a guess that a printer
would not trust. If print ever matters, proof the violet on stock first.

## 6. The accent rule

1. **Green, red and amber are state colours** (running, failed, warning). They are never brand colours and never part
   of any mark. No brand hue is within 30° of them.
2. **One accent element per mark**: the block in the Rigbay mark, the tile colour in a module mark, the dot of the `i`
   in the wordmark. Nothing else is coloured.
3. **In the app window** the block follows the user's **Windows accent** (title bar mark, About page). Everywhere there
   is no Windows accent (the exe icon, Start, README, repo, website, store listing) it is fixed Bay Violet.
4. **The tray is not rendered with the accent**, so tray icons use fixed colours (section 7).
5. **Module tiles keep their own fixed hue**: a tile identifies a module, not the user's theme.
6. Proposal for the integration: if the user's Windows accent hue is within 30° of the state green, red or amber, show
   the **quiet form** of the mark in the title bar so the block can never be mistaken for a state.

The accent-variable files use a CSS custom property: `fill: var(--rigbay-accent, #7A5AF8)`. It works when the SVG is
inline in HTML (shown teal in `test-01-kit-overview.png`); an SVG loaded through `<img>` ignores the page's variables
and shows the fallback, which is the right behaviour for the README. The XAML equivalent is in
`assets/brand/integration/`.

## 7. App icon, tray, high contrast

- **App icon** (`app-icon/rigbay-appicon.svg`): Bay Violet tile, corner radius 68 on 256 (26.6 %, the proportion of
  the module tiles' 56 on 208), white symbol at 62 % of the tile. Small cut at 70 % for 16 to 24 px
  (`rigbay-appicon-small.svg`). The symbol sits exactly in the centre of every frame (verified: margins equal at 16, 24,
  64 and 256 px).
- **`rigbay.ico`**: 16, 20, 24, 32, 40, 48, 64, 256 px, PNG entries. 16, 20 and 24 are the small cut, the rest the
  full drawing. Windows' own loader (`LoadImage`, what `Native.LoadTrayIcon` calls) returns the requested size for each
  (`tests/ico-load-check.txt`).
- **PNG exports** (`app-icon/png/rigbay-N.png`): 16, 20, 24, 32, 40, 48, 64, 128, 256, 512.
- **Tray pair** (`app-icon/tray/`): `rigbay-tray-light.ico` for a **light** taskbar (ink `#1F2430` bay) and
  `rigbay-tray-dark.ico` for a **dark** taskbar (ink `#EEF1F8` bay), 16 / 20 / 24 / 32 px (100, 125, 150, 200 %),
  small cut, and **both keep the block in fixed Bay Violet**, which passes 3 : 1 on `#202020` (3.60), `#2B2B2B` (3.13),
  `#F3F3F3` (4.07) and white (4.52). `rigbay-tray-neutral.ico` (all slate, 3.77 on dark, 3.39 on light) is the fallback
  when the taskbar theme cannot be read.
- **High contrast** (`app-icon/high-contrast/`): one-colour, small cut, `rigbay-hc-black.ico` for light high contrast
  themes and `rigbay-hc-white.ico` for dark ones. In one colour the block is told from the bay by the gap alone, which
  holds (`test-03-one-colour-reversed.png`).

## 8. Module family (Tile)

Each module mark is a **solid tile** (corner radius 56 on 208, so the same shape as the app icon and the Dock's outer
corner) in the module's colour with a **white glyph**. Strokes are 24, all terminals are pills.

| Module | Glyph | Tile | Files (`assets/brand/modules/`) |
| --- | --- | --- | --- |
| XTU profiles | two sliders with knobs | Blue `#2F7BFF` | `xtu.svg`, `xtu-small.svg`, `xtu-glyph.svg` |
| PredatorSense | a three-blade fan with a hub | Magenta `#D946B8` | `predatorsense.svg`, `-small`, `-glyph` |
| Steam access | a key | Cyan `#0A8FB0` | `steam-access.svg`, `-small`, `-glyph` |
| Module template | a plus: an empty bay | Slate `#7B8497` | `module-template.svg`, `-glyph` |
| Unknown module | a "?" | Slate `#7B8497` | `module-unknown.svg`, `-glyph` |

For each: `<id>.svg` (tile), `<id>-small.svg` (heavier drawing for 16 and 24 px, only where it differs), `<id>-glyph.svg`
(quiet form, glyph in the accent colour, no tile), `<id>-glyph-currentcolor.svg` (same, `currentColor`),
`<id>-unsupported.svg`. PNG sizes **16, 24, 32, 48, 64, 128** for the tile, the glyph and the unsupported variant:
`modules/png/<id>[-glyph|-unsupported]-<size>.png`. Overview with every size: `tests/test-11-module-family.png`.

- **Small cuts**: XTU gets heavier lines and bigger knobs; the fan gets fatter blades and a bigger hub (at 16 px the
  first fan read as a "Y" and was redrawn); the key loses its second tooth and gets a thicker shaft.
- **Quiet glyph form**: the glyph alone in the module colour, drawn in a 176 × 176 box (glyph 80 to 90 % of it) so it is as
  large as possible at 16 px. Use it where colour fights state colours (the dashboard row, test below) or where the
  tile is too loud. Glyph colour passes 3 : 1 on all four surfaces (table above).
- **Unknown module**: the neutral slate tile with a "?" slot. Used by the host when a module has no icon, a bad icon or
  fails to load.
- **Unsupported variant** (a module whose Probe says the machine does not support it): the *same drawing* with the tile
  colour replaced by the **grey of equal relative luminance** (`#818181` XTU, `#838383` PredatorSense and Steam access,
  `#848484` template and unknown). Built by the same script from the same glyph, so it can never drift; white glyph on
  grey is 3.74 to 3.90, the same contrast as on the colour tile. For a third-party icon the host cannot regenerate, the
  documented fallback is the module's own icon at **45 % opacity**.
- **Dashboard test** (`test-07-dashboard-rows.png`): the 24 px tile sits next to the 4 px state bar in light and dark
  without confusing it (the glyph is never a tall thin bar); the quiet form is calmer and also works. Recommendation:
  tiles on the Modules page (48 px) and in the dashboard rows (24 px), quiet glyphs in the navigation pane (16 px) and
  compact lists. Changing it is a one-line decision for the integration.

## 9. Steam access: the glyph that was flagged weak

The L1 drawing (an arc with an arrow over a post) read as "redo". Six replacements were drawn with the same primitives
and put through the same 16 px test (`assets/brand/modules/steam-explorations/`, sheet `test-10-steam-explorations.png`):

| Drawing | Verdict |
| --- | --- |
| **Key** (kept) | Reads as "access" at 24 px and above; the hole in the bow survives at 16 px. Risk: it also reads as "keys / passwords" elsewhere; the cyan tile and its place in the Steam access row remove the doubt. |
| Open padlock | The clearest at 16 px and says "unlocked", but reads as a security module in a tuning app, and "open padlock" can mean "insecure". **Drop-in alternative** if the owner prefers it. |
| Signpost | "Choose a route"; becomes a cross-like block at 16 px, the arrows are the busiest detail. |
| Detour (square route over a block) | Clean at 16 px but abstract: reads as a castle top. |
| Bridge (the old arch, arrow replaced by two nodes) | The arrow went, but the arch on two round feet now reads as headphones. |
| Shield with a gap | Says "protected", the opposite of a workaround; the gap is lost at 16 px. |

No Steam, Valve, Intel, Acer or Predator mark, shape, colour pairing or word is used or imitated. The Steam logo is a
circle with a piston and a smaller circle; a key shares nothing with that structure. The glyphs show what a module
does (sliders, a fan, a key), not whose product it works with.

## 10. How a module gets its mark: the `icon` convention (proposal)

For module authors, in the module folder next to `module.json`:

| File | Required | What |
| --- | --- | --- |
| `icon.svg` | for a mark | The tile, **208 × 208** viewBox, full-bleed tile with corner radius 56, one fill colour, white glyph, stroke 24 expanded to paths. Must not contain scripts, raster images, text, filters, gradients or external references. ≤ 64 KB. |
| `icon-glyph.svg` | optional | The quiet form (glyph only, in the module colour). Used in the navigation pane and compact lists when present, otherwise `icon.svg`. |
| `icon-16.png`, `icon-24.png`, `icon-32.png`, `icon-48.png`, `icon-64.png`, `icon-128.png` | optional | Raster fallbacks at exactly those sizes (the built-in modules ship all six). |

Manifest, a field **proposed** for Core (not implemented here; section "manifest" of `assets/brand/integration/README.md`):

```json
{ "id": "xtu", "icon": "icon.svg" }
```

Rules for module authors: one accent colour, not green, red or amber, not within 30° of them; the tile radius is 56 of
208; use the glyph grid (stroke 24, pill ends, glyph inside the central 160 × 160 of the tile); **no third-party logos, no
shapes taken from a vendor's mark, no vendor names**; test it at 16 px before publishing. The `module-template` repo
should ship `icon.svg` from `modules/module-template.svg` as the starting point. Without an icon, the host shows the
"?" tile.

## 11. File inventory (`assets/brand/`)

```
rigbay/               symbol, wordmark, lockups (SVG) and rigbay/png (symbol and lockup PNGs, light and dark)
app-icon/             rigbay-appicon(.svg|-small.svg), rigbay.ico, png/ (16..512)
  tray/               rigbay-tray-{light,dark,neutral}.svg + .ico + png/ (16 20 24 32)
  high-contrast/      rigbay-hc-{black,white}.svg + .ico + png/
modules/              tile, -small, -glyph, -glyph-currentcolor, -unsupported SVGs; png/ (16..128); steam-explorations/
social/               social-preview-1280x640.png, org-avatar-512.png, readme-banner(.svg|.png|-reversed.*)
tests/                test-01 .. test-11 sheets, svg-audit.txt, contrast.txt, verification.txt, ico-load-check.txt
integration/          what the app needs, with a README (file names, sizes, proposal for the manifest icon)
tools/                the Node scripts that build and check everything (below)
concepts/             the L1 concept round, kept as the record
```

Scripts (all dependency-free Node, Edge for rendering): `build-all.mjs` runs `build-svgs`, `build-raster` (PNG + ICO
packer), `build-social`, three sheet builders, `build-integration`, `svg-audit`, `contrast`, `verify`. `check-ico.ps1`
loads each `.ico` through Windows' `LoadImage`. The geometry lives in `tools/lib/marks.mjs`; **change a mark there and
rebuild, never edit generated SVGs by hand.**

Social images (assets only; GitHub settings were not touched): `social-preview-1280x640.png` (repo social preview,
Open Graph), `org-avatar-512.png` for the `rigbay-app` organisation (Bay Violet square, symbol at 60 %, inside the
circle-safe zone), `readme-banner.svg` (+ `.png`, reversed). None carries a tagline (that would need a typeface); the
repo description beside the image supplies the words.

## 12. Fonts and licence

**No typeface is used, embedded or required.** The wordmark is a custom drawing, converted to outlines; the SVG files
contain no `<text>`, no strokes, no raster, no filter, no mask, no gradient (`tools/svg-audit.mjs`: 61 files, 0
failures; the only warning is the rejected shield exploration). So there is no font licence to honour for the logo.

The test sheets (PNG) show labels rendered in the system UI font (Segoe UI on the machine that built them) as
screenshots; the font file is not distributed, and the labels are documentation, not part of any mark.

The repository is MIT. Whether the **name and logo** are covered by the same licence or by a trademark policy is a
decision for the owner; nothing here decides it. No trademark or reverse-image search was done: do that before launch.

## 13. What not to do

- Do not use green, red or amber in or beside the mark as a brand colour; do not use the accent block as a status light.
- Do not stretch, rotate, outline, add shadows, gradients, glows or a stroke; do not rearrange the lockup.
- Do not put an arrow, a plug, a lightning bolt or any drawing on the block; it must stay a plain floating block.
- Do not use the full drawing below 32 px or the small cut above 48 px.
- Do not use the light file on a dark surface (the bay vanishes: `test-03-one-colour-reversed.png`, third row).
- Do not place the colour logo on a busy photo or pattern; use the white or black one-colour file, or the tile.
- Do not colour the tray icon with the Windows accent.
- Do not add a vendor's logo, name or recognisable shape to a module mark.
- Do not recolour a module tile to show state; use the unsupported variant or the state bar.

## 14. Tests and what changed because of them

Evidence is in `assets/brand/tests/`; every PNG sheet was opened and looked at.

| Test (skill checklist) | Result | Sheet |
| --- | --- | --- |
| 16 px, every mark | Holds. Changed: fan redrawn with fatter blades for 16 px (read as "Y"), key given a heavier small cut. | `test-02-pixel-16.png` |
| 24 to 32 px | Holds in all marks. | `test-02`, `test-11` |
| One colour, black and white | Holds; white file thinned 1 unit per edge so it does not look heavier. | `test-03` |
| On brand colour, pattern, photo stand-in | Holds with the white/black file; the colour file is not for photos. | `test-03` |
| Greyscale | Dock and every glyph hold by shape and value. The four tiles end up at almost the same grey: modules are told apart by glyph, not hue. | `test-04` |
| Squint (blur 3 and 6 px) | Dock reads as U plus block; tiles keep their silhouette. | `test-04` |
| Mirror, rotate 90 and 180 | No accidental letter or hazard sign. Dock at 90° is a "C" with a pill, at 180° an arch with a block. The fan is not the radiation trefoil: asymmetric swept blades joined to the hub. Key mirrors as a key. "?" mirrors as a reversed "?", expected. | `test-04` |
| Windows 11 taskbar and tray, light and dark, 100 to 200 % | Holds in the mock; tray block 3.13 to 4.52 : 1 on all four surfaces. Mock only. | `test-05` |
| Start menu (32, 24, 48 px) | Holds, stands apart from generic neighbours. | `test-06` |
| Dashboard row, nav pane, Modules page | Tiles hold next to the state bar; quiet glyph form is the calm fallback; unsupported and "?" read clearly. | `test-07` |
| Shelf test against six generic stand-ins (toolbox, black and red, chip and graph, dial, terminal, shield) | Rigbay stays findable at 64, 32, 24 and 16 px, light and dark, and belongs to none of them. | `test-08` |
| Construction, clear space, size ladder | Done; minimum lockup height set to 24 px. | `test-09` |
| Geometry audit | 61 SVG files, 0 failures. An early audit found a 1.9° near-miss in the "?" join (fixed to an exact 45° by ending the hook at 45°) and my own tool miscounting transforms (tool fixed). | `svg-audit.txt` |
| Bugs found by looking | Fan hub and the signpost boards cancelled where outlines overlapped (winding direction; fixed in `shapes.mjs`); the first banner overlapped its own tiles; the first taskbar ×3 view overflowed. All fixed and re-rendered. | |
| ICO structure and Windows loading | 8 sizes in `rigbay.ico`, PNG entries of the declared size; `LoadImage` returns each requested size. | `verification.txt`, `ico-load-check.txt` |

### What was not verified

- **The real taskbar and tray**: all taskbar, Start and tray images are mocks with generic neighbours drawn here; no real
  Windows surface was captured. Whether Windows 11 draws the tray icon on its own tinted backgrounds as assumed is open.
- **High-DPI screens** (125, 150, 200 %): the frames exist and load, but nobody looked at them on such a display.
- **The app itself**: no file was put into `src/*`. The XAML path data was not compiled; `AppWindow.SetIcon`, the tray
  path with a real shell, and `SvgImageSource` on the module icons are all untested. The renderer used for every image
  is Chromium (Edge), not WinUI.
- **Print, CMYK, embroidery, engraving**: not tested, not produced.
- **High contrast themes** on a real machine: the mono files were looked at on black, white and yellow, not in the OS.
- **Trademark clearance and reverse-image search**: not done.
- **Other renderers**: the SVGs were rendered by Edge only (no Inkscape, Illustrator or the Explorer thumbnailer).
- **Python tooling** (`svg_audit.py`, `preview_sheet.py`, `export_variants.py`, ...): not run (no Python). The Node
  scripts in `tools/` reproduce the audit, the sheets and the exports, but they are not the skill's scripts, so scores
  are not comparable with its library.

## 15. Appendix: the L1 concept round (kept for the record)

Three Rigbay concepts were drawn (A Rack: three bays, one filled; B Dock: a bay with a module lowered in; C R-bay: a
rounded R whose leg is the module) and two module directions (1 Tile, 2 Slot). The record is in
`assets/brand/concepts/` (SVGs, `overview-rigbay.png`, `overview-modules.png`, `overview-tests.png`). Findings from
that round that still hold:

- The title bar currently draws three bays (two outlined, one in the Windows accent). A is the continuation of that and
  its outlined bays fail at 16 px; B replaces it.
- **The module rows use a vertical bar as state** (outlined = off, filled green or red = running or failed). A module
  icon must therefore never be a tall thin vertical bar. None of the five is.
- `tools/make-icon.ps1` (placeholder icon) fills its third bay with green `(47, 182, 124)`. Green is a state colour in
  the app, so the placeholder must be replaced by `assets/brand/app-icon/rigbay.ico` (see the integration README).
- Category: neighbours are saturated or aggressive tiles (PowerToys multi-colour, MSI Center and Armoury Crate black
  and red, HWiNFO dense). Clichés avoided: gear, wrench, chip with pins, lightning bolt, shield, flame, speedometer.
  Three upright bars is the most crowded abstract shape in the skill's library; nothing there resembles a U-shaped bay
  with a block lowered into it.
- Slot (direction 2) fails at 16 px because the frame is the heaviest thing in the icon; it stays a documented
  alternative only.

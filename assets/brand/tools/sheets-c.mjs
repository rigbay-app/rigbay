// Test sheets 08 to 11: shelf, construction and sizes, Steam explorations, module family.
// Run: node assets/brand/tools/sheets-c.mjs [sheet-name ...]
import { modules } from './build-svgs.mjs';
import { COLOR, dock } from './lib/marks.mjs';
import { grey, contrast, fmt } from './lib/color.mjs';
import { svgImg, svgText, pxImg, page, saveSheet, read } from './lib/sheet.mjs';
import { renderBatch } from './lib/raster.mjs';
import { encodePng, decodePng, upscale } from './lib/png.mjs';

const only = process.argv.slice(2);
const want = (n) => !only.length || only.includes(n);
const mod = (id) => modules.find((m) => m.id === id);
const FAM = ['xtu', 'predatorsense', 'steam-access', 'module-template', 'module-unknown'];

const dataPng = (img) => 'data:image/png;base64,' + encodePng(img).toString('base64');
/** Render an SVG file at size px with Edge and return an <img> enlarged k times with hard pixels. */
function zoomed(rels, size, k, bg = null) {
  const jobs = rels.map((rel) => ({ svg: read(rel).toString('utf8'), w: size, h: size }));
  return renderBatch(jobs).map((img) => `<img src="${dataPng(upscale(img, k))}" width="${size * k}" height="${size * k}" style="image-rendering:pixelated;${bg ? `background:${bg}` : ''}">`);
}

// ---------------------------------------------------------------- 08 shelf test
if (want('08-shelf')) {
  // Generic stand-ins drawn here from the HABITS of the category. None copies a product's mark.
  const tileSvg = (s, inner, bg, r = 0.22) => `<svg width="${s}" height="${s}" viewBox="0 0 100 100"><rect width="100" height="100" rx="${r * 100}" fill="${bg}"/>${inner}</svg>`;
  const stand = {
    'toolbox: bright multi-colour tool shapes': (s) => tileSvg(s, '<rect x="14" y="40" width="46" height="46" rx="8" fill="#FFC83D"/><rect x="40" y="14" width="46" height="46" rx="8" fill="#3D8BFF" opacity=".92"/><rect x="48" y="52" width="38" height="34" rx="8" fill="#FF6B4A"/>', '#F4F4F6'),
    'black and red, angular': (s) => tileSvg(s, '<path d="M12 78 L48 18 L58 18 L34 78 Z M44 78 L70 30 L80 30 L58 78 Z" fill="#E5262B"/>', '#141414', 0.12),
    'chip with pins and a graph': (s) => tileSvg(s, '<rect x="28" y="28" width="44" height="44" rx="5" fill="none" stroke="#6CE0A0" stroke-width="5"/><path d="M36 60 L44 50 L52 56 L64 38" fill="none" stroke="#6CE0A0" stroke-width="5"/><path d="M38 14v10M50 14v10M62 14v10M38 76v10M50 76v10M62 76v10M14 38h10M14 50h10M14 62h10M76 38h10M76 50h10M76 62h10" stroke="#6CE0A0" stroke-width="5"/>', '#1B2B2E'),
    'dial / speedometer': (s) => tileSvg(s, '<path d="M18 68 A32 32 0 0 1 82 68" fill="none" stroke="#fff" stroke-width="9"/><path d="M50 68 L68 42" stroke="#fff" stroke-width="9" stroke-linecap="round"/>', '#F07A1C'),
    'terminal prompt': (s) => tileSvg(s, '<path d="M24 32 L48 50 L24 68" fill="none" stroke="#fff" stroke-width="9" stroke-linecap="round" stroke-linejoin="round"/><path d="M54 70h24" stroke="#6CE0A0" stroke-width="9" stroke-linecap="round"/>', '#1E1E1E'),
    'shield': (s) => tileSvg(s, '<path d="M50 16 L80 28 V50 C80 68 66 80 50 86 C34 80 20 68 20 50 V28 Z" fill="#fff"/>', '#1F6FEB'),
  };
  const names = Object.keys(stand);
  const rigbay = (s) => pxImg(s >= 48 ? 'app-icon/png/rigbay-64.png' : s >= 32 ? 'app-icon/png/rigbay-32.png' : s >= 24 ? 'app-icon/png/rigbay-24.png' : 'app-icon/png/rigbay-16.png', 1);
  const shelf = (bg, size, label) => {
    const items = names.map((n) => stand[n](size));
    items.splice(3, 0, rigbay(size).replace(/width="\d+" height="\d+"/, `width="${size}" height="${size}"`));
    return `<div class="panel" style="background:${bg};margin-bottom:12px;${bg === '#202020' ? 'color:#EEF1F8;border-color:#2b2b2b' : ''}"><h2 style="${bg === '#202020' ? 'color:#c9cfdd' : ''}">${label}</h2><div class="row" style="align-items:center;gap:${Math.max(12, size / 2)}px">${items.join('')}</div></div>`;
  };
  const ownRow = (bg, size, label) => {
    const ids = ['xtu', 'predatorsense', 'steam-access'];
    const items = [stand[names[0]](size), pxImg(`modules/png/${ids[0]}-${size > 48 ? 64 : size}.png`), stand[names[2]](size), pxImg(`modules/png/${ids[1]}-${size > 48 ? 64 : size}.png`), stand[names[4]](size), pxImg(`modules/png/${ids[2]}-${size > 48 ? 64 : size}.png`), stand[names[5]](size)];
    return `<div class="panel" style="background:${bg};margin-bottom:12px;${bg === '#202020' ? 'color:#EEF1F8;border-color:#2b2b2b' : ''}"><h2 style="${bg === '#202020' ? 'color:#c9cfdd' : ''}">${label}</h2><div class="row" style="align-items:center;gap:${Math.max(12, size / 2)}px">${items.join('')}</div></div>`;
  };
  const body = `<div class="wrap"><h1>Shelf test</h1>
  <p class="sub">Rigbay's app icon (4th position) among generic stand-ins for its neighbours. The stand-ins are drawn here from the <em>habits</em> of the category, in order: bright multi-colour toolbox shapes (the PowerToys habit), black and red angular (gaming control centres), chip with pins and a graph (hardware monitors), dial or speedometer (tuners), terminal prompt, shield (security). None copies a product's mark. Judged: does Rigbay stay findable, and does it look like it belongs to none of them?</p>
  ${shelf('#F3F3F3', 64, 'Light, 64 px')}${shelf('#202020', 64, 'Dark, 64 px')}${shelf('#F3F3F3', 32, 'Light, 32 px')}${shelf('#202020', 32, 'Dark, 32 px')}${shelf('#F3F3F3', 24, 'Light, 24 px (taskbar)')}${shelf('#202020', 16, 'Dark, 16 px')}
  <h2 style="margin:18px 0 8px">The module tiles on the same shelf</h2>
  ${ownRow('#F3F3F3', 48, 'Light, 48 px: XTU profiles, PredatorSense, Steam access in positions 2, 4, 6')}${ownRow('#202020', 32, 'Dark, 32 px')}</div>`;
  console.log(saveSheet('test-08-shelf', page(body), 900));
}

// ---------------------------------------------------------------- 09 construction, clear space, sizes
if (want('09-construction')) {
  const U = 3, pad = 120;
  const grid = (() => { let g = ''; for (let i = 0; i <= 208; i += 8) g += `<path d="M${i} 0V208M0 ${i}H208" stroke="${i % 40 === 0 ? '#bfc6d6' : '#e3e7ef'}" stroke-width="${i % 40 === 0 ? 0.5 : 0.35}"/>`; return g; })();
  const T = (x, y, t, a = 'start') => `<text x="${x}" y="${y}" font-size="6.5" fill="#39404f" text-anchor="${a}" font-family="Segoe UI,sans-serif">${t}</text>`;
  const construction = `<svg width="${(208 + 160) * 3}" height="${(208 + 80) * 3}" viewBox="-70 -24 ${208 + 160} ${208 + 80}" style="background:#fff;border-radius:8px">
    ${grid}
    <path fill="${COLOR.inkLight}" d="${dock.full.bay}"/><path fill="${COLOR.accent}" d="${dock.full.block}"/>
    <g stroke="#E5484D" stroke-width="0.6" fill="none">
      <path d="M-8 60 H0 M-8 0 H64"/><path d="M0 214 H24 M0 210 V218 M24 210 V218"/>
      <path d="M64 -8 V0 M144 -8 V0 M64 -6 H144"/><path d="M-14 152 H-4"/>
      <path d="M24 152 A32 32 0 0 0 56 184" stroke-dasharray="1.5 1.5"/><path d="M0 152 A56 56 0 0 0 56 208" stroke-dasharray="1.5 1.5"/>
    </g>
    ${T(104, -10, 'block 80 × 144, corner 24', 'middle')}${T(12, 222, 'wall 24', 'middle')}${T(-10, 108, 'arm cap Ø24', 'end')}${T(212, 160, 'outer R56', 'start')}${T(212, 172, 'inner R32 (concentric)', 'start')}${T(104, 174, 'gap 40 on three sides', 'middle')}
    <path d="M24 100 H64 M144 100 H184" stroke="#E5484D" stroke-width="0.6"/>${T(44, 98, '40', 'middle')}
    ${T(104, 244, '208 × 208 units, 8-unit grid; the arm tops sit 48 below the block top', 'middle')}
  </svg>`;
  const x = 80; // clear space = one block width
  const clear = `<svg width="520" height="${Math.round(((208 + 2 * x) / (936 + 2 * x)) * 520)}" viewBox="${-x} ${-x} ${936 + 2 * x} ${208 + 2 * x}" style="background:#fff;border-radius:8px"><rect x="${-x}" y="${-x}" width="${936 + 2 * x}" height="${208 + 2 * x}" fill="#F1ECFF"/><rect x="0" y="0" width="936" height="208" fill="#fff"/><rect x="0" y="0" width="936" height="208" fill="none" stroke="#7A5AF8" stroke-dasharray="6 6" stroke-width="2"/>${svgText('rigbay/rigbay-lockup-horizontal.svg').replace(/<svg[^>]*>|<\/svg>|<title>.*<\/title>/g, '')}<path d="M0 -40 H80 M0 -48 V-32 M80 -48 V-32" stroke="#E5484D" stroke-width="3"/><text x="40" y="-56" font-size="28" text-anchor="middle" fill="#E5484D" font-family="Segoe UI,sans-serif">x</text></svg>`;
  const ladder = (bg, rev) => `<div class="panel" style="background:${bg};${bg === '#202020' ? 'border-color:#2b2b2b;color:#EEF1F8' : ''}"><div class="row" style="align-items:flex-end;gap:22px">${[16, 20, 24, 32, 48, 64, 128].map((s) => {
    const rel = s <= 24 ? (rev ? 'rigbay/rigbay-symbol-small-reversed.svg' : 'rigbay/rigbay-symbol-small.svg') : (rev ? 'rigbay/rigbay-symbol-reversed.svg' : 'rigbay/rigbay-symbol.svg');
    return `<div class="cell">${svgImg(rel, s)}<div class="cap">${s}${s <= 24 ? ' small' : ''}</div></div>`; }).join('')}</div></div>`;
  const lockLadder = (bg, rev) => `<div class="panel" style="background:${bg};${bg === '#202020' ? 'border-color:#2b2b2b;color:#EEF1F8' : ''}"><div class="row" style="align-items:flex-end;gap:26px">${[16, 20, 24, 32, 48, 64].map((h) => `<div class="cell">${svgImg(rev ? 'rigbay/rigbay-lockup-horizontal-reversed.svg' : 'rigbay/rigbay-lockup-horizontal.svg', Math.round(h * 4.5), h)}<div class="cap">height ${h}</div></div>`).join('')}</div></div>`;
  const body = `<div class="wrap"><h1>Construction, clear space, size ladder</h1>
  <p class="sub">Construction: the 208-unit grid. Clear space: x = the width of the module block (80 units, 38 % of the symbol height) on every side of the lockup or symbol. Ladders use the real SVGs at the given heights; the small cut is used up to 24 px.</p>
  <div class="row" style="align-items:flex-start;gap:28px"><div>${construction}</div><div><h2>Clear space (x = block width)</h2>${clear}</div></div>
  <h2 style="margin:22px 0 8px">Symbol size ladder (px)</h2><div class="grid" style="grid-template-columns:1fr 1fr">${ladder('#FFFFFF', false)}${ladder('#202020', true)}</div>
  <h2 style="margin:22px 0 8px">Horizontal lockup, height in px (wordmark x-height = 42 % of the height)</h2><div class="grid" style="grid-template-columns:1fr">${lockLadder('#FFFFFF', false)}${lockLadder('#202020', true)}</div></div>`;
  console.log(saveSheet('test-09-construction', page(body), 1180));
}

// ---------------------------------------------------------------- 10 Steam access explorations
if (want('10-steam-explorations')) {
  const cands = [
    ['key', 'CHOSEN', 'A key: bow with a hole, shaft, two teeth. Reads as "access" at 24 px and above, the hole survives at 16 px; the small cut drops the second tooth. Risk: also reads as "passwords / keys" in other products; the tile hue and its place in the Steam access row remove the doubt.'],
    ['padlock', 'second', 'An open padlock: the clearest of all at 16 px and says "unlocked". Not chosen: it reads as a security module in a tuning app, and "open padlock" can also mean "insecure". Drop-in alternative if the owner prefers it.'],
    ['signpost', 'no', 'Pole with two boards: "choose a route". Reads at 32 px, becomes a cross-like block at 16 px; the arrows are the busiest detail of all.'],
    ['detour', 'no', 'A route stepping over a block (square detour). Clean at 16 px but abstract: it reads as a castle top or a "Π" and does not say route or access.'],
    ['bridge', 'no', 'The old arch over a post, arrow replaced by two end nodes. The arrow was removed, but the arch with two round feet now reads as headphones.'],
    ['shield', 'no', 'Shield with a gap and a route through it. Says "protected / verified", the opposite of a workaround; the gap detail is lost at 16 px.'],
  ];
  const f = (k) => `modules/steam-explorations/steam-${k}.svg`;
  const z16 = zoomed(cands.map(([k]) => f(k)), 16, 8);
  const row = (c, i) => `<div class="panel" style="margin-bottom:12px${c[1] === 'CHOSEN' ? ';border:2px solid #7A5AF8' : ''}"><div class="row" style="align-items:center;gap:24px;flex-wrap:nowrap">
    <div style="width:70px;font-weight:600">${c[0]}<br><span class="${c[1] === 'CHOSEN' ? 'pass' : 'cap'}">${c[1]}</span></div>
    <div class="cell">${svgImg(f(c[0]), 128)}<div class="cap">128</div></div>
    <div class="cell">${svgImg(f(c[0]), 48)}<div class="cap">48</div></div>
    <div class="cell">${svgImg(f(c[0]), 24)}<div class="cap">24</div></div>
    <div class="cell"><div style="display:flex;gap:10px;align-items:center"><span style="background:#F3F3F3;padding:6px;border-radius:4px">${svgImg(f(c[0]), 16)}</span><span style="background:#202020;padding:6px;border-radius:4px">${svgImg(f(c[0]), 16)}</span></div><div class="cap">16 true size</div></div>
    <div class="cell">${z16[i]}<div class="cap">16 px ×8</div></div>
    <div style="flex:1;font-size:12px;color:#39404f">${c[2]}</div></div></div>`;
  const body = `<div class="wrap"><h1>Steam access: six drawings, one kept</h1><p class="sub">The concept drawing (an arc with an arrow over a post) was flagged as reading "redo". These are the replacements tried, all on the same cyan tile (#0A8FB0), all built from the same outline primitives, all tested at 16 px. No Steam, Valve or other trademark shape is used or imitated: the Steam mark is a circle with a piston and a smaller circle; none of these has that structure.</p>
  ${cands.map(row).join('')}</div>`;
  console.log(saveSheet('test-10-steam-explorations', page(body), 1180));
}

// ---------------------------------------------------------------- 11 module family
if (want('11-module-family')) {
  const sizes = [16, 24, 32, 48, 64, 128];
  const pad = (id, s, kind) => kind === 'tile' ? `modules/png/${id}-${s}.png` : kind === 'glyph' ? `modules/png/${id}-glyph-${s}.png` : `modules/png/${id}-unsupported-${s}.png`;
  const rowFor = (id) => `<div class="panel" style="margin-bottom:12px"><div class="row" style="align-items:flex-end;gap:14px;flex-wrap:nowrap"><div style="width:110px;font-weight:600">${mod(id).name}<br><span class="cap">${mod(id).color}</span></div>
    ${sizes.map((s) => `<div class="cell">${pxImg(pad(id, s, 'tile'))}<div class="cap">${s}</div></div>`).join('')}
    <div style="width:18px"></div>
    ${[24, 48].map((s) => `<div class="cell"><span style="background:#F3F3F3;padding:6px;border-radius:4px;display:inline-block">${pxImg(pad(id, s, 'glyph'))}</span><div class="cap">glyph ${s}</div></div><div class="cell"><span style="background:#202020;padding:6px;border-radius:4px;display:inline-block">${pxImg(pad(id, s, 'glyph'))}</span><div class="cap">glyph ${s} dark</div></div>`).join('')}
    <div style="width:18px"></div>
    ${[24, 48].map((s) => `<div class="cell">${pxImg(pad(id, s, 'unsup'))}<div class="cap">unsupported ${s}</div></div>`).join('')}</div></div>`;
  const rows = ['xtu', 'predatorsense', 'steam-access', 'module-template', 'module-unknown'].map(rowFor).join('');
  const table = modules.map((m) => `<tr><td>${m.name}</td><td>${m.color}</td><td>${grey(m.color)}</td><td>${fmt(contrast(m.color, '#FFFFFF'))}</td><td>${fmt(contrast(grey(m.color), '#FFFFFF'))}</td></tr>`).join('');
  const body = `<div class="wrap"><h1>Module family (Tile): sizes, quiet glyph, unsupported rule</h1>
  <p class="sub">Per module: the tile at 16 / 24 / 32 / 48 / 64 / 128 (16 and 24 use the small cut where one exists: XTU and PredatorSense and Steam access have a heavier small drawing), the quiet glyph on light and dark, and the unsupported variant (same drawing, tile colour replaced by the grey of equal luminance).</p>
  ${rows}
  <div class="panel"><h2>Unsupported rule: tile colour → grey with the same relative luminance</h2><table style="border-collapse:collapse;font-size:12px"><tr style="text-align:left;color:#5a6273"><th style="padding:2px 18px 2px 0">Module</th><th style="padding:2px 18px 2px 0">Tile</th><th style="padding:2px 18px 2px 0">Unsupported</th><th style="padding:2px 18px 2px 0">White glyph on tile</th><th>White glyph on grey</th></tr>${table}</table></div></div>`;
  console.log(saveSheet('test-11-module-family', page(body), 1180));
}

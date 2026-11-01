// Builds every SVG of the kit from tools/lib/marks.mjs. Run: node assets/brand/tools/build-svgs.mjs
// Output is plain paths: no <text>, no strokes in the masters, no raster, no filters, no masks.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { COLOR, TILE_R, APP_R, dock, wordmark, glyph, steamCandidates, steamKeySmall } from './lib/marks.mjs';
import { bbox } from './lib/pathgeom.mjs';
import { grey } from './lib/color.mjs';
import { n, roundRect } from './lib/shapes.mjs';

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = (rel, text) => { const f = path.join(ROOT, rel); fs.mkdirSync(path.dirname(f), { recursive: true }); fs.writeFileSync(f, text.replace(/\r?\n/g, '\n')); return rel; };
export const written = [];
const W = (rel, text) => written.push(out(rel, text));

const svg = (vb, title, inner, { w, h, desc } = {}) => {
  const [, , vw, vh] = vb.split(' ').map(Number);
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${vb}" width="${w ?? vw}" height="${h ?? vh}" role="img" aria-label="${title}">
  <title>${title}</title>${desc ? `\n  <desc>${desc}</desc>` : ''}
${inner}
</svg>
`;
};
const P = (d, fill, extra = '') => `  <path fill="${fill}"${extra} d="${d}"/>`;
const accentVar = (fallback = COLOR.accent) => `var(--rigbay-accent,${fallback})`;
const VB = '0 0 208 208';

// ---------------- Rigbay symbol ----------------
const sym = (set, ink, accent, title) => svg(VB, title, P(set.bay, ink) + '\n' + P(set.block, accent));
W('rigbay/rigbay-symbol.svg', sym(dock.full, COLOR.inkLight, COLOR.accent, 'Rigbay symbol'));
W('rigbay/rigbay-symbol-reversed.svg', sym(dock.reversed, COLOR.inkDark, COLOR.accent, 'Rigbay symbol, reversed'));
W('rigbay/rigbay-symbol-black.svg', sym(dock.full, '#000000', '#000000', 'Rigbay symbol, black'));
W('rigbay/rigbay-symbol-white.svg', sym(dock.reversed, '#FFFFFF', '#FFFFFF', 'Rigbay symbol, white'));
W('rigbay/rigbay-symbol-accent.svg', svg(VB, 'Rigbay symbol, accent follows --rigbay-accent',
  P(dock.full.bay, 'currentColor') + '\n' + `  <path style="fill:${accentVar()}" d="${dock.full.block}"/>`,
  { desc: 'Ink is currentColor. The block takes --rigbay-accent and falls back to Bay Violet. Works inline in HTML or XAML-side, not through an img tag.' }));
W('rigbay/rigbay-symbol-small.svg', sym(dock.small, COLOR.inkLight, COLOR.accent, 'Rigbay symbol, small cut (16 and 24 px)'));
W('rigbay/rigbay-symbol-small-reversed.svg', sym(dock.smallReversed, COLOR.inkDark, COLOR.accent, 'Rigbay symbol, small cut, reversed'));
W('rigbay/rigbay-symbol-quiet.svg', sym(dock.full, COLOR.slate, COLOR.inkLight, 'Rigbay symbol, quiet (no hue)'));
W('rigbay/rigbay-symbol-quiet-reversed.svg', sym(dock.reversed, COLOR.slate, COLOR.inkDark, 'Rigbay symbol, quiet, reversed'));

// ---------------- wordmark and lockups ----------------
const wm = wordmark();
const GAP = 80;              // distance between symbol and wordmark: one block width
const WM_X = 208 + GAP;      // 288
const lockW = WM_X + wm.width;
function lockupHorizontal({ ink, accent, set, title, accentVarMode = false }) {
  const fillAcc = accentVarMode ? `style="fill:${accentVar()}"` : `fill="${accent}"`;
  return svg(`0 0 ${lockW} 208`, title,
    `  <path fill="${ink}" d="${set.bay}"/>\n  <path ${fillAcc} d="${set.block}"/>\n  <path fill="${ink}" transform="translate(${WM_X} 0)" d="${wm.body}"/>\n  <path ${fillAcc} transform="translate(${WM_X} 0)" d="${wm.dot}"/>`);
}
const L = (rel, o) => W(rel, lockupHorizontal(o));
L('rigbay/rigbay-lockup-horizontal.svg', { ink: COLOR.inkLight, accent: COLOR.accent, set: dock.full, title: 'Rigbay logo' });
L('rigbay/rigbay-lockup-horizontal-reversed.svg', { ink: COLOR.inkDark, accent: COLOR.accent, set: dock.reversed, title: 'Rigbay logo, reversed' });
L('rigbay/rigbay-lockup-horizontal-black.svg', { ink: '#000000', accent: '#000000', set: dock.full, title: 'Rigbay logo, black' });
L('rigbay/rigbay-lockup-horizontal-white.svg', { ink: '#FFFFFF', accent: '#FFFFFF', set: dock.reversed, title: 'Rigbay logo, white' });
L('rigbay/rigbay-lockup-horizontal-accent.svg', { ink: 'currentColor', set: dock.full, title: 'Rigbay logo, accent follows --rigbay-accent', accentVarMode: true });
{
  // stacked: symbol above, wordmark below at 0.62 of its horizontal size; both centred
  const s = 0.62, ww = wm.width * s, width = Math.max(208, ww), gapV = 56;
  const symX = (width - 208) / 2, wmX = (width - ww) / 2, wmY = 208 + gapV, height = wmY + 208 * s;
  const stacked = (ink, set, accent, title) => svg(`0 0 ${n(width)} ${n(height)}`, title,
    `  <path fill="${ink}" transform="translate(${n(symX)} 0)" d="${set.bay}"/>\n  <path fill="${accent}" transform="translate(${n(symX)} 0)" d="${set.block}"/>\n  <path fill="${ink}" transform="translate(${n(wmX)} ${n(wmY)}) scale(${s})" d="${wm.body}"/>\n  <path fill="${accent}" transform="translate(${n(wmX)} ${n(wmY)}) scale(${s})" d="${wm.dot}"/>`);
  W('rigbay/rigbay-lockup-stacked.svg', stacked(COLOR.inkLight, dock.full, COLOR.accent, 'Rigbay logo, stacked'));
  W('rigbay/rigbay-lockup-stacked-reversed.svg', stacked(COLOR.inkDark, dock.reversed, COLOR.accent, 'Rigbay logo, stacked, reversed'));
}
const wmSvg = (ink, dot, title) => svg(`0 0 ${wm.width} 208`, title, `  <path fill="${ink}" d="${wm.body}"/>\n  <path fill="${dot}" d="${wm.dot}"/>`);
W('rigbay/rigbay-wordmark.svg', wmSvg(COLOR.inkLight, COLOR.accent, 'rigbay wordmark'));
W('rigbay/rigbay-wordmark-reversed.svg', wmSvg(COLOR.inkDark, COLOR.accent, 'rigbay wordmark, reversed'));
W('rigbay/rigbay-wordmark-black.svg', wmSvg('#000000', '#000000', 'rigbay wordmark, black'));
W('rigbay/rigbay-wordmark-white.svg', wmSvg('#FFFFFF', '#FFFFFF', 'rigbay wordmark, white'));

// ---------------- app icon, tray, high contrast ----------------
function appIcon(set, scale, title, tileColor = COLOR.accent, ink = '#FFFFFF') {
  const s = (scale * 256) / 208, off = (256 - 208 * s) / 2;
  return svg('0 0 256 256', title,
    P(roundRect(0, 0, 256, 256, APP_R), tileColor) + `\n  <g transform="translate(${n(off)} ${n(off)}) scale(${n(s)})">\n    <path fill="${ink}" d="${set.bay}"/>\n    <path fill="${ink}" d="${set.block}"/>\n  </g>`);
}
export const APP_SCALE = { full: 0.62, small: 0.7 };
W('app-icon/rigbay-appicon.svg', appIcon(dock.full, APP_SCALE.full, 'Rigbay app icon'));
W('app-icon/rigbay-appicon-small.svg', appIcon(dock.small, APP_SCALE.small, 'Rigbay app icon, small cut (16 to 24 px)'));
const mono = (set, color, title) => svg(VB, title, P(set.bay, color) + '\n' + P(set.block, color));
// tray: fixed colours. Dock in the ink that suits the taskbar, block in Bay Violet (3:1 or better on both). Small cut only.
W('app-icon/tray/rigbay-tray-light.svg', sym(dock.small, COLOR.inkLight, COLOR.accent, 'Rigbay tray icon for a light taskbar'));
W('app-icon/tray/rigbay-tray-dark.svg', sym(dock.smallReversed, COLOR.inkDark, COLOR.accent, 'Rigbay tray icon for a dark taskbar'));
W('app-icon/tray/rigbay-tray-neutral.svg', sym(dock.small, COLOR.slate, COLOR.slate, 'Rigbay tray icon, one fixed neutral colour'));
W('app-icon/high-contrast/rigbay-hc-black.svg', mono(dock.small, '#000000', 'Rigbay mark for high contrast, black'));
W('app-icon/high-contrast/rigbay-hc-white.svg', mono(dock.smallReversed, '#FFFFFF', 'Rigbay mark for high contrast, white'));

// ---------------- module tiles ----------------
export const modules = [
  { id: 'xtu', name: 'XTU profiles', color: COLOR.xtu, glyph: glyph.xtu, small: glyph.xtuSmall },
  { id: 'predatorsense', name: 'PredatorSense', color: COLOR.predator, glyph: glyph.fan, small: glyph.fanSmall },
  { id: 'steam-access', name: 'Steam access', color: COLOR.steam, glyph: steamCandidates.key, small: steamKeySmall },
  { id: 'module-template', name: 'Module template', color: COLOR.template, glyph: glyph.template, small: glyph.template },
  { id: 'module-unknown', name: 'Unknown module', color: COLOR.template, glyph: glyph.unknown, small: glyph.unknown },
];
const tile = (color, d, title) => svg(VB, title, P(roundRect(0, 0, 208, 208, TILE_R), color) + '\n' + P(d, '#FFFFFF'));
export const GLYPH_BOX = 176;
function glyphSvg(d, fill, title) {
  const [x0, y0, x1, y1] = bbox(d), cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, s = GLYPH_BOX;
  return svg(`${n(cx - s / 2)} ${n(cy - s / 2)} ${s} ${s}`, title, P(d, fill), { w: s, h: s });
}
for (const m of modules) {
  W(`modules/${m.id}.svg`, tile(m.color, m.glyph, `${m.name} module mark`));
  if (m.small !== m.glyph) W(`modules/${m.id}-small.svg`, tile(m.color, m.small, `${m.name} module mark, small cut`));
  W(`modules/${m.id}-glyph.svg`, glyphSvg(m.glyph, m.color, `${m.name} glyph, quiet form`));
  W(`modules/${m.id}-glyph-currentcolor.svg`, glyphSvg(m.glyph, 'currentColor', `${m.name} glyph, current colour`));
  W(`modules/${m.id}-unsupported.svg`, tile(grey(m.color), m.glyph, `${m.name} module mark, unsupported on this machine`));
  if (m.small !== m.glyph) W(`modules/${m.id}-small-unsupported.svg`, tile(grey(m.color), m.small, `${m.name} module mark, small cut, unsupported on this machine`));
}
for (const [k, d] of Object.entries(steamCandidates)) W(`modules/steam-explorations/steam-${k}.svg`, tile(COLOR.steam, d, `Steam access exploration: ${k}`));

// ---------------- repo images ----------------
{
  // README banner 1280 x 320: lockup left, the four module tiles right as occupied bays.
  const H = 320, k = 0.64;
  const tiles = ['xtu', 'predatorsense', 'steam-access', 'module-template'].map((id) => modules.find((m) => m.id === id));
  const ts = 88, tg = 20, rowW = tiles.length * ts + (tiles.length - 1) * tg, left = 96;
  const rowX = 1280 - 96 - rowW, ly = (H - 208 * k) / 2;
  const tilesSvg = tiles.map((m, i) => `  <g transform="translate(${rowX + i * (ts + tg)} ${(H - ts) / 2}) scale(${n(ts / 208)})">\n    <path fill="${m.color}" d="${roundRect(0, 0, 208, 208, TILE_R)}"/>\n    <path fill="#FFFFFF" d="${m.glyph}"/>\n  </g>`).join('\n');
  const banner = (ink, set, title) => svg(`0 0 1280 ${H}`, title,
    `  <g transform="translate(${left} ${n(ly)}) scale(${k})">\n    <path fill="${ink}" d="${set.bay}"/>\n    <path fill="${COLOR.accent}" d="${set.block}"/>\n    <path fill="${ink}" transform="translate(${WM_X} 0)" d="${wm.body}"/>\n    <path fill="${COLOR.accent}" transform="translate(${WM_X} 0)" d="${wm.dot}"/>\n  </g>\n${tilesSvg}`);
  W('social/readme-banner.svg', banner(COLOR.inkLight, dock.full, 'Rigbay banner'));
  W('social/readme-banner-reversed.svg', banner(COLOR.inkDark, dock.reversed, 'Rigbay banner, reversed'));
}
export { lockW, WM_X, wm };
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) console.log(`wrote ${written.length} SVG files`);

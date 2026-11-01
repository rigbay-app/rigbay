// Social preview (1280 x 640), organisation avatar (512 x 512) and README banner PNGs. Assets only: nothing is uploaded anywhere.
// Run: node assets/brand/tools/build-social.mjs   (after build-svgs.mjs)
import fs from 'node:fs';
import path from 'node:path';
import { ROOT, modules, wm, WM_X, lockW } from './build-svgs.mjs';
import { COLOR, dock, TILE_R } from './lib/marks.mjs';
import { roundRect, n } from './lib/shapes.mjs';
import { shootHtml } from './lib/edge.mjs';
import { encodePng } from './lib/png.mjs';

const BG = '#171A22';   // darker than the ink so the reversed logo has the room of a Mica dark surface
const mod = (id) => modules.find((m) => m.id === id);

// --- social preview: reversed lockup, then the four module tiles as occupied bays. No tagline: GitHub shows the repo description beside it.
function social() {
  const W = 1280, H = 640, k = 1.15, lw = lockW * k, lh = 208 * k;
  const lx = (W - lw) / 2, ly = 105;
  const ts = 120, tg = 40, tiles = ['xtu', 'predatorsense', 'steam-access', 'module-template'].map(mod), rowW = tiles.length * ts + (tiles.length - 1) * tg, rx = (W - rowW) / 2, ry = 450;
  const tileSvg = tiles.map((m, i) => `<g transform="translate(${rx + i * (ts + tg)} ${ry}) scale(${n(ts / 208)})"><path fill="${m.color}" d="${roundRect(0, 0, 208, 208, TILE_R)}"/><path fill="#fff" d="${m.glyph}"/></g>`).join('');
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}"><rect width="${W}" height="${H}" fill="${BG}"/>
  <g transform="translate(${n(lx)} ${ly}) scale(${k})"><path fill="${COLOR.inkDark}" d="${dock.reversed.bay}"/><path fill="${COLOR.accent}" d="${dock.reversed.block}"/><path fill="${COLOR.inkDark}" transform="translate(${WM_X} 0)" d="${wm.body}"/><path fill="${COLOR.accent}" transform="translate(${WM_X} 0)" d="${wm.dot}"/></g>
  ${tileSvg}</svg>`;
}
// --- organisation avatar: Bay Violet square (full bleed: GitHub rounds or circles it), symbol well inside the circle-safe zone
function avatar() {
  const S = 512, size = 0.6 * S, k = size / 208, o = (S - size) / 2;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${S}" height="${S}" viewBox="0 0 ${S} ${S}"><rect width="${S}" height="${S}" fill="${COLOR.accent}"/>
  <g transform="translate(${n(o)} ${n(o)}) scale(${n(k)})"><path fill="#fff" d="${dock.reversed.bay}"/><path fill="#fff" d="${dock.reversed.block}"/></g></svg>`;
}
function render(svg, w, h, outRel) {
  const html = `<!doctype html><body style="margin:0;background:transparent">${svg}</body>`;
  const img = shootHtml(html, w, h);
  const f = path.join(ROOT, outRel); fs.mkdirSync(path.dirname(f), { recursive: true }); fs.writeFileSync(f, encodePng(img));
  console.log(outRel, `${w}x${h}`, fs.statSync(f).size + ' bytes');
}
render(social(), 1280, 640, 'social/social-preview-1280x640.png');
render(avatar(), 512, 512, 'social/org-avatar-512.png');
// README banner PNGs (the SVG is the master; GitHub renders SVG in a README, the PNG is for places that do not)
for (const [name, bg] of [['readme-banner', '#FFFFFF'], ['readme-banner-reversed', BG]]) {
  const svg = fs.readFileSync(path.join(ROOT, `social/${name}.svg`), 'utf8').replace('role="img"', `role="img" style="background:${bg}"`);
  render(svg, 1280, 320, `social/${name}.png`);
}

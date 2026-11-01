// Renders PNG exports and packs the ICO files from the SVGs. Run after build-svgs.mjs: node assets/brand/tools/build-raster.mjs
import fs from 'node:fs';
import path from 'node:path';
import { ROOT, modules } from './build-svgs.mjs';
import { renderBatch } from './lib/raster.mjs';
import { encodePng } from './lib/png.mjs';
import { packIco, readIco } from './lib/ico.mjs';

const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');
const exists = (rel) => fs.existsSync(path.join(ROOT, rel));
const jobs = [];    // { svg, w, h, file }
const add = (svgRel, size, outRel) => jobs.push({ svg: read(svgRel), w: size, h: size, file: outRel });

// ---- app icon: small cut up to 24 px, full drawing from 32 px ----
const APP_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256, 512];
for (const s of APP_SIZES) add(s <= 24 ? 'app-icon/rigbay-appicon-small.svg' : 'app-icon/rigbay-appicon.svg', s, `app-icon/png/rigbay-${s}.png`);

// ---- tray (fixed colours, small cut) and high contrast ----
const TRAY_SIZES = [16, 20, 24, 32];
for (const v of ['light', 'dark', 'neutral']) for (const s of TRAY_SIZES) add(`app-icon/tray/rigbay-tray-${v}.svg`, s, `app-icon/tray/png/rigbay-tray-${v}-${s}.png`);
const HC_SIZES = [16, 20, 24, 32, 48, 64, 256];
for (const v of ['black', 'white']) for (const s of HC_SIZES) add(`app-icon/high-contrast/rigbay-hc-${v}.svg`, s, `app-icon/high-contrast/png/rigbay-hc-${v}-${s}.png`);

// ---- the Rigbay symbol itself (title bar, README, docs): light and dark, small cut up to 24 px ----
for (const s of [16, 20, 24, 32, 48, 64, 128, 256, 512]) {
  const small = s <= 24;
  add(small ? 'rigbay/rigbay-symbol-small.svg' : 'rigbay/rigbay-symbol.svg', s, `rigbay/png/rigbay-symbol-${s}.png`);
  add(small ? 'rigbay/rigbay-symbol-small-reversed.svg' : 'rigbay/rigbay-symbol-reversed.svg', s, `rigbay/png/rigbay-symbol-reversed-${s}.png`);
}
// lockup rasters keep the aspect ratio (936 x 208)
for (const [name, rel] of [['rigbay-lockup-horizontal', 'rigbay/rigbay-lockup-horizontal.svg'], ['rigbay-lockup-horizontal-reversed', 'rigbay/rigbay-lockup-horizontal-reversed.svg']])
  for (const w of [468, 936, 1872]) jobs.push({ svg: read(rel), w, h: Math.round((w * 208) / 936), file: `rigbay/png/${name}-${w}.png` });

// ---- module family: tile, quiet glyph, unsupported; 16 24 32 48 64 128 ----
const MOD_SIZES = [16, 24, 32, 48, 64, 128];
for (const m of modules) for (const s of MOD_SIZES) {
  const small = s <= 24 && exists(`modules/${m.id}-small.svg`);
  add(small ? `modules/${m.id}-small.svg` : `modules/${m.id}.svg`, s, `modules/png/${m.id}-${s}.png`);
  add(`modules/${m.id}-glyph.svg`, s, `modules/png/${m.id}-glyph-${s}.png`);
  add(small ? `modules/${m.id}-small-unsupported.svg` : `modules/${m.id}-unsupported.svg`, s, `modules/png/${m.id}-unsupported-${s}.png`);
}

console.log(`rendering ${jobs.length} PNGs ...`);
const imgs = renderBatch(jobs);
jobs.forEach((j, i) => { const f = path.join(ROOT, j.file); fs.mkdirSync(path.dirname(f), { recursive: true }); fs.writeFileSync(f, encodePng(imgs[i])); });

// ---- ICO files (PNG entries) ----
const png = (rel) => fs.readFileSync(path.join(ROOT, rel));
const ico = (rel, sizes, srcFn) => {
  const f = path.join(ROOT, rel); fs.mkdirSync(path.dirname(f), { recursive: true });
  fs.writeFileSync(f, packIco(sizes.map((s) => ({ size: s, png: png(srcFn(s)) }))));
  const info = readIco(fs.readFileSync(f));
  console.log(rel.padEnd(46), fs.statSync(f).size + ' bytes', info.map((e) => `${e.w}${e.isPng && e.pngW === e.w ? '' : '!'}`).join(' '));
};
ico('app-icon/rigbay.ico', [16, 20, 24, 32, 40, 48, 64, 256], (s) => `app-icon/png/rigbay-${s}.png`);
for (const v of ['light', 'dark', 'neutral']) ico(`app-icon/tray/rigbay-tray-${v}.ico`, TRAY_SIZES, (s) => `app-icon/tray/png/rigbay-tray-${v}-${s}.png`);
for (const v of ['black', 'white']) ico(`app-icon/high-contrast/rigbay-hc-${v}.ico`, [16, 20, 24, 32, 48, 64, 256], (s) => `app-icon/high-contrast/png/rigbay-hc-${v}-${s}.png`);
console.log('done');

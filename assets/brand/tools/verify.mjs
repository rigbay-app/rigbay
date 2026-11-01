// Checks the built files: ICO structure, PNG sizes, wordmark outline vs the stroked drawing, centring of the 16 to 64 px frames.
// Writes tests/verification.txt. Run: node assets/brand/tools/verify.mjs
import fs from 'node:fs';
import path from 'node:path';
import { ROOT } from './build-svgs.mjs';
import { readIco } from './lib/ico.mjs';
import { decodePng, diff } from './lib/png.mjs';
import { wordmark, wordmarkStroked } from './lib/marks.mjs';
import { renderBatch } from './lib/raster.mjs';

const out = []; let bad = 0;
const log = (s) => out.push(s); const check = (ok, s) => { if (!ok) bad++; log((ok ? 'ok    ' : 'FAIL  ') + s); };
const png = (rel) => decodePng(fs.readFileSync(path.join(ROOT, rel)));

log('ICO files');
const icos = { 'app-icon/rigbay.ico': [16, 20, 24, 32, 40, 48, 64, 256], 'app-icon/tray/rigbay-tray-light.ico': [16, 20, 24, 32], 'app-icon/tray/rigbay-tray-dark.ico': [16, 20, 24, 32], 'app-icon/tray/rigbay-tray-neutral.ico': [16, 20, 24, 32], 'app-icon/high-contrast/rigbay-hc-black.ico': [16, 20, 24, 32, 48, 64, 256], 'app-icon/high-contrast/rigbay-hc-white.ico': [16, 20, 24, 32, 48, 64, 256] };
for (const [rel, sizes] of Object.entries(icos)) {
  const buf = fs.readFileSync(path.join(ROOT, rel)), info = readIco(buf);
  check(JSON.stringify(info.map((e) => e.w)) === JSON.stringify(sizes), `${rel}: ${buf.length} bytes, entries ${info.map((e) => e.w).join(' ')}`);
  check(info.every((e) => e.isPng && e.pngW === e.w && e.pngH === e.h && e.bpp === 32), `${rel}: every entry is a 32-bit PNG of its declared size`);
}
log('');
log('PNG exports present with the right pixel size');
const want = [];
for (const s of [16, 20, 24, 32, 40, 48, 64, 128, 256, 512]) want.push([`app-icon/png/rigbay-${s}.png`, s, s]);
for (const v of ['light', 'dark', 'neutral']) for (const s of [16, 20, 24, 32]) want.push([`app-icon/tray/png/rigbay-tray-${v}-${s}.png`, s, s]);
for (const id of ['xtu', 'predatorsense', 'steam-access', 'module-template', 'module-unknown']) for (const s of [16, 24, 32, 48, 64, 128]) for (const k of ['', '-glyph', '-unsupported']) want.push([`modules/png/${id}${k}-${s}.png`, s, s]);
want.push(['social/social-preview-1280x640.png', 1280, 640], ['social/org-avatar-512.png', 512, 512]);
let miss = 0;
for (const [rel, w, h] of want) { try { const i = png(rel); if (i.w !== w || i.h !== h) { miss++; log(`FAIL  ${rel} is ${i.w}x${i.h}, expected ${w}x${h}`); } } catch { miss++; log('FAIL  missing ' + rel); } }
check(miss === 0, `${want.length} PNG files checked, ${miss} wrong or missing`);

log('');
log('Wordmark: outlines against the stroked drawing (rendered by Edge, mean absolute difference per channel, 0..255)');
{
  const wm = wordmark();
  const a = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="-10 -10 668 228"><rect x="-10" y="-10" width="668" height="228" fill="#fff"/><path d="${wm.body}"/><path d="${wm.dot}"/></svg>`;
  const b = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="-10 -10 668 228"><rect x="-10" y="-10" width="668" height="228" fill="#fff"/>${wordmarkStroked()}</svg>`;
  const [ia, ib] = renderBatch([{ svg: a, w: 1336, h: 456 }, { svg: b, w: 1336, h: 456 }]);
  const d = diff(ia, ib);
  check(d.mean < 0.3, `outline vs stroked: mean ${d.mean.toFixed(3)}, max ${d.max.toFixed(0)} (edge pixels only)`);
}

log('');
log('Centring of the small frames (alpha bounding box of the pixels, in px; target: the same margin left and right, top and bottom)');
for (const rel of ['app-icon/png/rigbay-16.png', 'app-icon/png/rigbay-24.png', 'app-icon/png/rigbay-32.png', 'app-icon/tray/png/rigbay-tray-light-16.png', 'app-icon/tray/png/rigbay-tray-dark-24.png', 'modules/png/xtu-16.png', 'modules/png/steam-access-24.png', 'modules/png/module-unknown-glyph-16.png']) {
  const i = png(rel); let x0 = 99, y0 = 99, x1 = -1, y1 = -1;
  for (let y = 0; y < i.h; y++) for (let x = 0; x < i.w; x++) if (i.data[(y * i.w + x) * 4 + 3] > 40) { x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y); }
  const l = x0, r = i.w - 1 - x1, t = y0, b = i.h - 1 - y1;
  check(Math.abs(l - r) <= 1 && Math.abs(t - b) <= 1, `${rel}: margins left ${l} right ${r} top ${t} bottom ${b}`);
}
log('');
log(bad ? `${bad} FAILURES` : 'all checks passed');
fs.mkdirSync(path.join(ROOT, 'tests'), { recursive: true });
fs.writeFileSync(path.join(ROOT, 'tests', 'verification.txt'), out.join('\n') + '\n');
console.log(out.join('\n'));
process.exit(bad ? 1 : 0);

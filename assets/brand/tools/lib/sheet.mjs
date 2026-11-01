// HTML sheet helpers for the test sheets: embed files as data URIs, render with headless Edge, save as PNG.
import fs from 'node:fs';
import path from 'node:path';
import { ROOT } from '../build-svgs.mjs';
import { shootHtml } from './edge.mjs';
import { encodePng, crop } from './png.mjs';

export const read = (rel) => fs.readFileSync(path.join(ROOT, rel));
export const svgText = (rel) => read(rel).toString('utf8');
const b64 = (buf, mime) => `data:${mime};base64,${buf.toString('base64')}`;
export const svgUri = (rel) => b64(read(rel), 'image/svg+xml');
export const pngUri = (rel) => b64(read(rel), 'image/png');

/** <img> of an SVG file at w x h CSS px. */
export const svgImg = (rel, w, h = w, style = '') => `<img src="${svgUri(rel)}" width="${w}" height="${h}" style="${style}">`;
/** <img> of a PNG file at its own size times k (nearest neighbour when k > 1 so every pixel is visible). */
export const pxImg = (rel, k = 1, style = '') => {
  const buf = read(rel), w = buf.readUInt32BE(16), h = buf.readUInt32BE(20);
  return `<img src="${b64(buf, 'image/png')}" width="${w * k}" height="${h * k}" style="image-rendering:${k > 1 ? 'pixelated' : 'auto'};${style}">`;
};

export const CSS = `
*{box-sizing:border-box}
body{margin:0;font:12px/1.35 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;color:#1F2430;background:#E9EBF0}
h1{font-size:22px;font-weight:600;margin:0 0 4px}
h2{font-size:13px;font-weight:600;margin:0 0 8px;color:#39404f}
.sub{color:#5a6273;margin:0 0 18px;max-width:1100px}
.wrap{padding:28px 32px}
.panel{background:#fff;border:1px solid #d6d9e0;border-radius:10px;padding:14px 16px}
.dark{background:#202020;color:#EEF1F8;border-color:#2b2b2b}
.light{background:#F3F3F3}
.row{display:flex;gap:16px;align-items:flex-end;flex-wrap:wrap}
.cell{display:flex;flex-direction:column;align-items:center;gap:6px}
.cap{font-size:10px;color:#6a7285;text-align:center;line-height:1.25;max-width:150px}
.dark .cap{color:#9aa3b5}
.dark h2{color:#c9cfdd}
.grid{display:grid;gap:14px}
.note{font-size:11px;color:#5a6273;margin-top:8px}
.pass{color:#1d7a4a;font-weight:600}.fail{color:#b3261e;font-weight:600}
`;

export function page(body, extraCss = '') {
  return `<!doctype html><meta charset="utf-8"><style>${CSS}${extraCss}</style><body>${body}</body>`;
}

/** Render `html` to tests/<name>.png at the given width; height measured from content (render twice: first to measure). */
export function saveSheet(name, html, width, height = 5000) {
  let img = shootHtml(html, width, height);
  // trim to content: last row that differs from the page background (top-left pixel), plus 28 px
  const bg = img.data.slice(0, 4); let last = 0;
  for (let y = img.h - 1; y >= 0 && !last; y--) for (let x = 0; x < img.w; x++) { const i = (y * img.w + x) * 4; if (img.data[i] !== bg[0] || img.data[i + 1] !== bg[1] || img.data[i + 2] !== bg[2]) { last = y; break; } }
  if (last && last + 28 < img.h) img = crop(img, 0, 0, img.w, last + 28);
  const f = path.join(ROOT, 'tests', name + '.png');
  fs.mkdirSync(path.dirname(f), { recursive: true });
  fs.writeFileSync(f, encodePng(img));
  return f;
}

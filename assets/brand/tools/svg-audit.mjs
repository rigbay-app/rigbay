// SVG audit for the shipped masters: live text, raster, filters/masks/gradients, strokes, colour count, near-miss angles,
// tiny details, anchors, bounding box centring. Node only. Run: node assets/brand/tools/svg-audit.mjs [file ...]
// With no arguments it audits every .svg under rigbay/, app-icon/, modules/ and social/ and writes tests/svg-audit.txt.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { flatten, parsePath } from './lib/pathgeom.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const walk = (d) => fs.readdirSync(d, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? walk(path.join(d, e.name)) : e.name.endsWith('.svg') ? [path.join(d, e.name)] : []));

function applyT(pts, tr) { // tr: list of {t:'translate'|'scale', a, b}
  for (let i = tr.length - 1; i >= 0; i--) { const { t, a, b } = tr[i]; pts = pts.map(([x, y]) => (t === 'translate' ? [x + a, y + b] : [x * a, y * b])); }
  return pts;
}
const parseTransform = (s) => { const out = []; for (const m of (s || '').matchAll(/(translate|scale)\(([^)]*)\)/g)) { const v = m[2].split(/[ ,]+/).filter(Boolean).map(Number); out.push({ t: m[1], a: v[0], b: v[1] ?? (m[1] === 'scale' ? v[0] : 0) }); } return out; };

export function audit(file) {
  const src = fs.readFileSync(file, 'utf8');
  const rel = path.relative(ROOT, file).replace(/\\/g, '/');
  const res = { file: rel, fails: [], warns: [], info: {} };
  const has = (re) => re.test(src);
  if (has(/<text[\s>]|<tspan|<textPath/i)) res.fails.push('live <text>');
  if (has(/<image[\s>]|data:image\/(png|jpe?g|gif|webp)/i)) res.fails.push('raster image');
  if (has(/<filter[\s>]|filter=|<mask[\s>]|<foreignObject|<script|<use[\s>]|<clipPath/i)) res.fails.push('filter / mask / clip / use / script');
  if (has(/<(linear|radial)Gradient|url\(#/i)) res.fails.push('gradient or url() reference');
  if (!/viewBox="[^"]+"/.test(src)) res.fails.push('no viewBox');
  if (!/xmlns="http:\/\/www\.w3\.org\/2000\/svg"/.test(src)) res.fails.push('no xmlns');
  const strokes = [...src.matchAll(/stroke-width="([^"]+)"/g)].map((m) => m[1]);
  if (strokes.length || has(/\sstroke="(?!none)/)) res.fails.push('strokes in a master (expand to outlines): ' + [...new Set(strokes)].join(', '));
  // colours
  const cols = new Set(); const vars = new Set();
  for (const m of src.matchAll(/(?:fill|stroke)="(#[0-9a-fA-F]{3,8})"/g)) cols.add(m[1].toUpperCase());
  for (const m of src.matchAll(/var\(--[\w-]+,\s*(#[0-9a-fA-F]{3,8})\)/g)) { cols.add(m[1].toUpperCase()); vars.add(m[0]); }
  const cc = /currentColor/.test(src); const isBannerFile = /banner/.test(rel);
  res.info.colours = [...cols].join(' ') + (cc ? ' +currentColor' : '') + (vars.size ? ' +css var' : '');
  res.info.nColours = cols.size + (cc ? 1 : 0);
  if (res.info.nColours > 3 && !isBannerFile) res.warns.push(`${res.info.nColours} colours`);
  // geometry from every <path d>, applying translate/scale from the enclosing <g> and the element itself
  const vb = (src.match(/viewBox="([^"]+)"/) || [])[1]?.split(/[ ,]+/).map(Number);
  let anchors = 0, minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity; const angles = []; const tiny = [];
  // paths inside a <g transform> get that transform; others none
  const items = []; // { attrs, d, tr }
  let rest = src;
  for (const g of src.matchAll(/<g\b([^>]*)>([\s\S]*?)<\/g>/g)) {
    const tr = parseTransform((g[1].match(/transform="([^"]+)"/) || [])[1]);
    for (const p of g[2].matchAll(/<path\b([^>]*)\sd="([^"]+)"/g)) items.push({ attrs: p[1], d: p[2], tr });
    rest = rest.replace(g[0], '');
  }
  for (const p of rest.matchAll(/<path\b([^>]*)\sd="([^"]+)"/g)) items.push({ attrs: p[1], d: p[2], tr: [] });
  const isBanner = /banner/.test(rel);
  for (const it of items) {
    const tr = [...it.tr, ...parseTransform((it.attrs.match(/transform="([^"]+)"/) || [])[1])];
    const { segs, polys } = flatten(it.d);
    anchors += segs.filter((s) => s.t !== 'M' && s.t !== 'Z').length;
    for (const p of polys) for (const [x, y] of applyT(p, tr)) { minX = Math.min(minX, x); minY = Math.min(minY, y); maxX = Math.max(maxX, x); maxY = Math.max(maxY, y); }
    const unit = isBanner ? 0 : (vb ? Math.min(vb[2], vb[3]) : 208) / 48; // details below 1/48 of the artwork
    for (const s of segs) {
      if (s.t === 'L') { const L = Math.hypot(s.x - s.x0, s.y - s.y0); if (L > 0.01) { let a = (Math.atan2(s.y - s.y0, s.x - s.x0) * 180) / Math.PI; a = ((a % 180) + 180) % 180; angles.push(a); if (L < unit) tiny.push(`segment ${L.toFixed(1)}`); } }
      if (s.t === 'A' && s.rx < unit) tiny.push(`arc r${s.rx}`);
    }
  }
  res.info.anchors = anchors;
  const nm = []; for (const a of angles) { const d = Math.min(...[0, 15, 30, 45, 60, 75, 90, 105, 120, 135, 150, 165, 180].map((k) => Math.abs(a - k))); if (d > 0.05 && d <= 2.5) nm.push(a.toFixed(2)); }
  res.info.nearMiss = [...new Set(nm)].join(' ') || 'none';
  if (nm.length) res.warns.push('near-miss angles ' + res.info.nearMiss);
  const distinctAngles = [...new Set(angles.map((a) => a.toFixed(1)))];
  res.info.angles = distinctAngles.join(' ');
  if (tiny.length) res.warns.push('tiny details: ' + [...new Set(tiny)].join(', '));
  if (vb && isFinite(minX)) {
    const cx = (minX + maxX) / 2 - (vb[0] + vb[2] / 2), cy = (minY + maxY) / 2 - (vb[1] + vb[3] / 2);
    res.info.bbox = `${minX.toFixed(1)},${minY.toFixed(1)} ${maxX.toFixed(1)},${maxY.toFixed(1)}`;
    res.info.centre = `${((cx / vb[2]) * 100).toFixed(1)}%, ${((cy / vb[3]) * 100).toFixed(1)}%`;
    res.info.fill = `${(((maxX - minX) / vb[2]) * 100).toFixed(0)}% x ${(((maxY - minY) / vb[3]) * 100).toFixed(0)}%`;
    // a symbol or glyph should sit within 1.5% of the centre; banners and lockups are laid out, not centred
    if (/symbol|glyph|appicon|tray|hc-|steam-|\/modules\//.test(rel) && !/lockup|wordmark|banner/.test(rel) && (Math.abs(cx / vb[2]) > 0.02 || Math.abs(cy / vb[3]) > 0.02)) res.warns.push(`off centre by ${res.info.centre}`);
  }
  res.info.bytes = Buffer.byteLength(src);
  return res;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const files = args.length ? args.map((a) => path.resolve(a)) : ['rigbay', 'app-icon', 'modules', 'social'].flatMap((d) => (fs.existsSync(path.join(ROOT, d)) ? walk(path.join(ROOT, d)) : []));
  const lines = []; let fails = 0, warns = 0;
  for (const f of files) {
    const r = audit(f); fails += r.fails.length; warns += r.warns.length;
    const state = r.fails.length ? 'FAIL' : r.warns.length ? 'WARN' : 'PASS';
    lines.push(`${state}  ${r.file.padEnd(58)} colours ${r.info.nColours}  anchors ${String(r.info.anchors).padStart(3)}  fill ${r.info.fill ?? '-'}  centre ${r.info.centre ?? '-'}  angles [${r.info.angles}]  ${r.info.bytes} B${r.fails.length ? '  !! ' + r.fails.join('; ') : ''}${r.warns.length ? '  ~ ' + r.warns.join('; ') : ''}`);
  }
  lines.push('', `${files.length} files, ${fails} failures, ${warns} warnings`);
  const text = lines.join('\n');
  console.log(text);
  if (!args.length) { fs.mkdirSync(path.join(ROOT, 'tests'), { recursive: true }); fs.writeFileSync(path.join(ROOT, 'tests', 'svg-audit.txt'), text + '\n'); }
  process.exit(fails ? 1 : 0);
}

// Render an HTML string or file to a PNG with headless Microsoft Edge (never the real screen).
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { decodePng } from './png.mjs';

const EDGE_CANDIDATES = [
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
];
export const EDGE = EDGE_CANDIDATES.find((p) => fs.existsSync(p));

/** Screenshot of `htmlPath` at w x h CSS px, device scale 1 (or `scale`), transparent unless the page paints. Returns decoded image. */
export function shoot(htmlPath, w, h, { scale = 1, outPng } = {}) {
  if (!EDGE) throw new Error('msedge.exe not found');
  const out = outPng ?? path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'rbshot-')), 'shot.png');
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'rbedge-'));
  const r = spawnSync(EDGE, [
    '--headless', '--disable-gpu', '--hide-scrollbars', '--no-first-run', `--user-data-dir=${profile}`,
    `--force-device-scale-factor=${scale}`, '--default-background-color=00000000',
    `--window-size=${w},${h}`, `--screenshot=${out}`, pathToFileURL(htmlPath).href,
  ], { encoding: 'utf8', timeout: 90000 });
  fs.rmSync(profile, { recursive: true, force: true });
  if (!fs.existsSync(out)) throw new Error('Edge produced no screenshot: ' + (r.stderr || '').slice(-400));
  const img = decodePng(fs.readFileSync(out));
  if (img.w !== w * scale || img.h !== h * scale) throw new Error(`screenshot is ${img.w}x${img.h}, expected ${w * scale}x${h * scale}`);
  return img;
}

export function shootHtml(html, w, h, opts = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'rbhtml-'));
  const f = path.join(dir, 'page.html');
  fs.writeFileSync(f, html);
  try { return shoot(f, w, h, opts); } finally { fs.rmSync(dir, { recursive: true, force: true }); }
}

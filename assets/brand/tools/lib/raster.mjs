// Batch SVG -> PNG at exact pixel sizes through one headless Edge run per chunk. Jobs: { svg: string (markup), w, h, bg? } -> images.
import { shootHtml } from './edge.mjs';
import { crop } from './png.mjs';

const uri = (svg) => 'data:image/svg+xml;base64,' + Buffer.from(svg).toString('base64');

export function renderBatch(jobs, { pad = 4, maxH = 7000 } = {}) {
  const results = new Array(jobs.length);
  let chunk = [], used = pad;
  const flush = () => {
    if (!chunk.length) return;
    const W = Math.max(...chunk.map((c) => c.job.w)) + pad * 2, H = used;
    let y = pad, html = '<!doctype html><meta charset="utf-8"><body style="margin:0;background:transparent;overflow:hidden">';
    const pos = [];
    for (const c of chunk) {
      pos.push({ i: c.i, x: pad, y });
      html += `<img style="position:absolute;left:${pad}px;top:${y}px;width:${c.job.w}px;height:${c.job.h}px" src="${uri(c.job.svg)}">`;
      y += c.job.h + pad;
    }
    const sheet = shootHtml(html, W, H);
    for (const p of pos) results[p.i] = crop(sheet, p.x, p.y, jobs[p.i].w, jobs[p.i].h);
    chunk = []; used = pad;
  };
  jobs.forEach((job, i) => {
    if (used + job.h + pad > maxH) flush();
    chunk.push({ i, job }); used += job.h + pad;
  });
  flush();
  return results;
}

/** Render one HTML page (string) to a PNG at CSS size w x h. */
export { shootHtml };
export const svgUri = uri;

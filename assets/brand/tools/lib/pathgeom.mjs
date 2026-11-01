// Path parsing and flattening (M L H V A C Q Z, absolute and relative) for bounding boxes, straight-segment angles and anchor counts.
export function parsePath(d) {
  const toks = d.match(/[a-zA-Z]|-?\d*\.?\d+(?:e[-+]?\d+)?/g) || [];
  const segs = []; let i = 0, cmd = '', x = 0, y = 0, sx = 0, sy = 0;
  const num = () => parseFloat(toks[i++]);
  const flag = () => parseInt(toks[i++], 10);
  while (i < toks.length) {
    if (/[a-zA-Z]/.test(toks[i])) cmd = toks[i++];
    const rel = cmd === cmd.toLowerCase(), C = cmd.toUpperCase();
    if (C === 'Z') { segs.push({ t: 'Z', x: sx, y: sy }); x = sx; y = sy; continue; }
    if (C === 'M') { let nx = num(), ny = num(); if (rel) { nx += x; ny += y; } x = sx = nx; y = sy = ny; segs.push({ t: 'M', x, y }); cmd = rel ? 'l' : 'L'; continue; }
    if (C === 'L') { let nx = num(), ny = num(); if (rel) { nx += x; ny += y; } segs.push({ t: 'L', x0: x, y0: y, x: nx, y: ny }); x = nx; y = ny; continue; }
    if (C === 'H') { let nx = num(); if (rel) nx += x; segs.push({ t: 'L', x0: x, y0: y, x: nx, y }); x = nx; continue; }
    if (C === 'V') { let ny = num(); if (rel) ny += y; segs.push({ t: 'L', x0: x, y0: y, x, y: ny }); y = ny; continue; }
    if (C === 'A') { const rx = num(), ry = num(), rot = num(), la = flag(), sw = flag(); let nx = num(), ny = num(); if (rel) { nx += x; ny += y; } segs.push({ t: 'A', x0: x, y0: y, rx, ry, rot, la, sw, x: nx, y: ny }); x = nx; y = ny; continue; }
    if (C === 'C') { let a = [num(), num(), num(), num(), num(), num()]; if (rel) a = a.map((v, k) => v + (k % 2 ? y : x)); segs.push({ t: 'C', x0: x, y0: y, c: a, x: a[4], y: a[5] }); x = a[4]; y = a[5]; continue; }
    if (C === 'Q') { let a = [num(), num(), num(), num()]; if (rel) a = a.map((v, k) => v + (k % 2 ? y : x)); segs.push({ t: 'Q', x0: x, y0: y, c: a, x: a[2], y: a[3] }); x = a[2]; y = a[3]; continue; }
    throw new Error('unsupported path command ' + cmd);
  }
  return segs;
}

function arcPoints(s, steps = 48) {
  // SVG endpoint -> centre parameterisation (circular/elliptical, rot 0 assumed for our marks; rot handled).
  let { x0, y0, x, y, rx, ry, rot, la, sw } = s; const phi = (rot * Math.PI) / 180, cp = Math.cos(phi), sp = Math.sin(phi);
  if (!rx || !ry) return [[x, y]];
  const dx = (x0 - x) / 2, dy = (y0 - y) / 2, x1p = cp * dx + sp * dy, y1p = -sp * dx + cp * dy;
  rx = Math.abs(rx); ry = Math.abs(ry);
  const lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry); if (lam > 1) { rx *= Math.sqrt(lam); ry *= Math.sqrt(lam); }
  const num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p, den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
  let co = Math.sqrt(Math.max(0, num / den)); if (la === sw) co = -co;
  const cxp = (co * rx * y1p) / ry, cyp = (-co * ry * x1p) / rx;
  const cx = cp * cxp - sp * cyp + (x0 + x) / 2, cy = sp * cxp + cp * cyp + (y0 + y) / 2;
  const ang = (ux, uy, vx, vy) => { const a = Math.atan2(ux * vy - uy * vx, ux * vx + uy * vy); return a; };
  const th1 = ang(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
  let dth = ang((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
  if (!sw && dth > 0) dth -= 2 * Math.PI; if (sw && dth < 0) dth += 2 * Math.PI;
  const pts = [];
  for (let k = 1; k <= steps; k++) { const th = th1 + (dth * k) / steps; pts.push([cx + rx * Math.cos(th) * cp - ry * Math.sin(th) * sp, cy + rx * Math.cos(th) * sp + ry * Math.sin(th) * cp]); }
  return pts;
}

/** Flatten to polylines (array of arrays of [x,y]) plus per-segment info. */
export function flatten(d) {
  const segs = parsePath(d), polys = []; let cur = null;
  for (const s of segs) {
    if (s.t === 'M') { cur = [[s.x, s.y]]; polys.push(cur); }
    else if (s.t === 'Z') { /* closed */ }
    else if (s.t === 'L') cur.push([s.x, s.y]);
    else if (s.t === 'A') cur.push(...arcPoints(s));
    else if (s.t === 'C') { for (let k = 1; k <= 24; k++) { const t = k / 24, u = 1 - t, [a, b, c2, d2, e, f] = s.c; cur.push([u ** 3 * s.x0 + 3 * u * u * t * a + 3 * u * t * t * c2 + t ** 3 * e, u ** 3 * s.y0 + 3 * u * u * t * b + 3 * u * t * t * d2 + t ** 3 * f]); } }
    else if (s.t === 'Q') { for (let k = 1; k <= 16; k++) { const t = k / 16, u = 1 - t, [a, b, e, f] = s.c; cur.push([u * u * s.x0 + 2 * u * t * a + t * t * e, u * u * s.y0 + 2 * u * t * b + t * t * f]); } }
  }
  return { segs, polys };
}

export function bbox(d) {
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const p of flatten(d).polys) for (const [x, y] of p) { x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y); }
  return [x0, y0, x1, y1];
}

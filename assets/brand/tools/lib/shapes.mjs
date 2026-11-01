// Outline primitives for stroke-free SVG masters. Screen coordinates (y down). Every outer contour is traced counter-clockwise on
// screen and every hole clockwise, so overlapping shapes can be concatenated into one path and filled with the default nonzero rule.
export const n = (v) => { const s = (+v.toFixed(3)).toString(); return s === '-0' ? '0' : s; };
const P = (x, y) => `${n(x)} ${n(y)}`;
const rad = (d) => (d * Math.PI) / 180;

/** Stroke of a straight line with round caps, as a closed outline. */
export function pill(x1, y1, x2, y2, w) {
  const r = w / 2, dx = x2 - x1, dy = y2 - y1, L = Math.hypot(dx, dy), nx = (-dy / L) * r, ny = (dx / L) * r;
  // d = (1,0): n points down; bottom edge to the right, right cap round to the top, back along the top: counter-clockwise on screen.
  return `M${P(x1 + nx, y1 + ny)} L${P(x2 + nx, y2 + ny)} A${n(r)} ${n(r)} 0 0 0 ${P(x2 - nx, y2 - ny)} L${P(x1 - nx, y1 - ny)} A${n(r)} ${n(r)} 0 0 0 ${P(x1 + nx, y1 + ny)} Z`;
}
export function disc(cx, cy, r) {
  return `M${P(cx + r, cy)} A${n(r)} ${n(r)} 0 0 0 ${P(cx - r, cy)} A${n(r)} ${n(r)} 0 0 0 ${P(cx + r, cy)} Z`;
}
/** Ring: outer contour counter-clockwise, hole clockwise. */
export function ring(cx, cy, ro, ri) {
  return disc(cx, cy, ro) + ` M${P(cx + ri, cy)} A${n(ri)} ${n(ri)} 0 0 1 ${P(cx - ri, cy)} A${n(ri)} ${n(ri)} 0 0 1 ${P(cx + ri, cy)} Z`;
}
/** Arc stroke of width w along the circle (cx,cy,r) from angle a0 to a1 (degrees, increasing = clockwise on screen, 0 = +x), round caps. */
export function sector(cx, cy, r, w, a0, a1) {
  const ro = r + w / 2, ri = r - w / 2, rc = w / 2, big = Math.abs(a1 - a0) > 180 ? 1 : 0;
  const pt = (rr, a) => P(cx + rr * Math.cos(rad(a)), cy + rr * Math.sin(rad(a)));
  return `M${pt(ro, a1)} A${n(ro)} ${n(ro)} 0 ${big} 0 ${pt(ro, a0)} A${n(rc)} ${n(rc)} 0 0 0 ${pt(ri, a0)} A${n(ri)} ${n(ri)} 0 ${big} 1 ${pt(ri, a1)} A${n(rc)} ${n(rc)} 0 0 0 ${pt(ro, a1)} Z`;
}
/** Polyline stroke with round joins and caps = pills plus discs at the corners. */
export function poly(points, w) {
  let d = '';
  for (let i = 0; i < points.length - 1; i++) d += pill(...points[i], ...points[i + 1], w);
  for (let i = 1; i < points.length - 1; i++) d += disc(...points[i], w / 2);
  return d;
}
/** Rotate a path made of absolute M/L/A/C commands? Kept simple: callers rotate by SVG transform on a <g> only for the fan blades. */

/**
 * The Dock symbol on a 208 x 208 canvas: a U-shaped bay (outer radius 56, wall w) and the module block lowered into it.
 * t thins every edge by t units (used for the reversed version); w is the wall of the U.
 */
export function dockBay({ w = 24, t = 0, ro = 56 } = {}) {
  const x0 = t, x1 = 208 - t, yb = 208 - t, rr = ro - t, cap = w / 2, wl = w - 2 * t, c = wl / 2, ri = rr - wl;
  const top = 60; // centre of the arm caps
  return [
    `M${n(x0)} ${n(top)} V${n(yb - rr)} A${n(rr)} ${n(rr)} 0 0 0 ${n(x0 + rr)} ${n(yb)} H${n(x1 - rr)} A${n(rr)} ${n(rr)} 0 0 0 ${n(x1)} ${n(yb - rr)} V${n(top)}`,
    `A${n(c)} ${n(c)} 0 0 0 ${n(x1 - wl)} ${n(top)} V${n(yb - rr)} A${n(ri)} ${n(ri)} 0 0 1 ${n(x1 - rr)} ${n(yb - wl)} H${n(x0 + rr)} A${n(ri)} ${n(ri)} 0 0 1 ${n(x0 + wl)} ${n(yb - rr)} V${n(top)}`,
    `A${n(c)} ${n(c)} 0 0 0 ${n(x0)} ${n(top)} Z`,
  ].join(' ');
}
/** The module block: a rounded rectangle as a path (no <rect>, so the whole mark is plain paths). */
export function dockBlock({ t = 0, x = 64, y = 0, w = 80, h = 144, r = 24 } = {}) {
  const X = x + t, Y = y + t, W = w - 2 * t, H = h - 2 * t, R = r - t / 2;
  // counter-clockwise on screen, like every other outline in this file
  return `M${n(X + R)} ${n(Y)} A${n(R)} ${n(R)} 0 0 0 ${n(X)} ${n(Y + R)} V${n(Y + H - R)} A${n(R)} ${n(R)} 0 0 0 ${n(X + R)} ${n(Y + H)} H${n(X + W - R)} A${n(R)} ${n(R)} 0 0 0 ${n(X + W)} ${n(Y + H - R)} V${n(Y + R)} A${n(R)} ${n(R)} 0 0 0 ${n(X + W - R)} ${n(Y)} Z`;
}
export function roundRect(x, y, w, h, r) { return dockBlock({ x, y, w, h, r }); }

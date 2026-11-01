// Geometry of every Rigbay mark. One source of truth: build-svgs.mjs turns these into files.
// Grid: 208 x 208 units (26 x 26 cells of 8). The Dock symbol and every module tile fill the whole canvas.
import { pill, disc, ring, sector, poly, dockBay, dockBlock, roundRect, n } from './shapes.mjs';

export const COLOR = {
  inkLight: '#1F2430', inkDark: '#EEF1F8', accent: '#7A5AF8', slate: '#7B8497', white: '#FFFFFF',
  xtu: '#2F7BFF', predator: '#D946B8', steam: '#0A8FB0', recorder: '#6B7794', template: '#7B8497',
};
export const TILE_R = 56;       // module tile corner radius on the 208 canvas (27%)
export const APP_R = 68;        // app icon tile corner radius on the 256 canvas (26.6%, the same proportion)

// ---------- Rigbay Dock symbol ----------
export const dock = {
  full: { bay: dockBay({ w: 24 }), block: dockBlock({}) },
  reversed: { bay: dockBay({ w: 24, t: 1 }), block: dockBlock({ t: 1 }) },         // thinned 1 unit per edge: light on dark looks heavier
  small: { bay: dockBay({ w: 32 }), block: dockBlock({}) },                           // heavier wall, bigger gaps, for 16 and 24 px
  smallReversed: { bay: dockBay({ w: 32, t: 1 }), block: dockBlock({ t: 1 }) },
};

// ---------- wordmark "rigbay", expanded to outlines. Origin: ink starts at x = 0, x-height centre on y = 104 ----------
const Y = (y) => y - 24; // concept coordinates were drawn on a 256 canvas
const ov = 1; // overshoot of the round letters, per side
export function wordmark() {
  const body = [
    // r
    pill(12, Y(84), 12, Y(172), 24), sector(56, Y(128), 44, 24, 180, 270),
    // i (the dot is separate: it carries the accent)
    pill(104, Y(84), 104, Y(172), 24),
    // g
    ring(196 - ov, Y(128), 56 + ov, 32 + ov), pill(240, Y(84), 240, Y(184), 24), sector(204, Y(184), 36, 24, 0, 90),
    // b
    pill(284, Y(36), 284, Y(172), 24), ring(328 + ov, Y(128), 56 + ov, 32 + ov),
    // a
    ring(460 - ov, Y(128), 56 + ov, 32 + ov), pill(504, Y(84), 504, Y(172), 24),
    // y
    pill(548, Y(84), 592, Y(172), 24), pill(636, Y(84), 568, Y(220), 24),
  ].join(' ');
  return { body, dot: disc(104, Y(40), 16), width: 648, height: 208 };
}
/** The same wordmark drawn with strokes, only to prove the outlines match (verify step). */
export function wordmarkStroked() {
  return `<g fill="none" stroke="#000" stroke-width="24" stroke-linecap="round" stroke-linejoin="round" transform="translate(0 -24)">
    <path d="M12 84 V172 M12 128 A44 44 0 0 1 56 84"/><path d="M104 84 V172"/><circle cx="${196 - ov}" cy="128" r="${44 + ov}"/>
    <path d="M240 84 V184 A36 36 0 0 1 204 220"/><path d="M284 36 V172"/><circle cx="${328 + ov}" cy="128" r="${44 + ov}"/>
    <circle cx="${460 - ov}" cy="128" r="${44 + ov}"/><path d="M504 84 V172"/><path d="M548 84 L592 172 M636 84 L568 220"/>
    <circle cx="104" cy="40" r="16" fill="#000" stroke="none"/></g>`;
}

// ---------- module glyphs (white on the tile, accent colour in the quiet form), drawn on the 208 canvas ----------
const rot = (pt, deg, cx = 104, cy = 104) => { const a = (deg * Math.PI) / 180, dx = pt[0] - cx, dy = pt[1] - cy; return [cx + dx * Math.cos(a) - dy * Math.sin(a), cy + dx * Math.sin(a) + dy * Math.cos(a)]; };
const BLADE = [[94, 96], [68, 74], [74, 38], [104, 28], [128, 21], [142, 46], [128, 70], [123, 80], [118, 88], [116, 96]];
const BLADE_FAT = [[92, 98], [60, 76], [66, 32], [104, 22], [134, 14], [152, 46], [130, 72], [124, 82], [120, 90], [118, 98]];
function blade(deg, pts = BLADE) {
  // one fan blade, swept; the hub disc closes the middle
  const r = pts.map((p) => rot(p, deg)).map(([x, y]) => [n(x), n(y)]);
  // pts: 0 start, 1-3 first curve, 4-6 second, 7-9 third. Traced backwards so it is counter-clockwise like the hub disc.
  return `M${r[0].join(' ')} L${r[9].join(' ')} C${r[8].join(' ')} ${r[7].join(' ')} ${r[6].join(' ')} C${r[5].join(' ')} ${r[4].join(' ')} ${r[3].join(' ')} C${r[2].join(' ')} ${r[1].join(' ')} ${r[0].join(' ')} Z`;
}
export const glyph = {
  xtu: pill(44, 68, 164, 68, 24) + pill(44, 140, 164, 140, 24) + disc(124, 68, 28) + disc(84, 140, 28),
  xtuSmall: pill(40, 64, 168, 64, 28) + pill(40, 144, 168, 144, 28) + disc(124, 64, 32) + disc(84, 144, 32),
  fan: blade(0) + blade(120) + blade(240) + disc(104, 104, 16),
  fanSmall: blade(0, BLADE_FAT) + blade(120, BLADE_FAT) + blade(240, BLADE_FAT) + disc(104, 104, 18),
  // recorder: a record dot inside a ring (the dot is the "on air" mark, the ring keeps it a lens, not a status light). Stroke 24.
  recorder: ring(104, 104, 76, 52) + disc(104, 104, 28),
  recorderSmall: ring(104, 104, 80, 52) + disc(104, 104, 30),
  template: pill(104, 48, 104, 160, 28) + pill(48, 104, 160, 104, 28),
  // "?" for an unknown module: hook, stem, dot
  // the hook ends at 45 degrees and the diagonal continues it exactly, so the join is smooth and the angle is a true 45
  unknown: sector(104, 72, 28, 24, 180, 405) + poly([[123.8, 91.8], [104, 111.6], [104, 126]], 24) + disc(104, 160, 14),
};

// ---------- Steam access: six drawings, the winner is chosen in the review (see docs/BRAND.md) ----------
// Names say the idea. All are white-on-tile glyphs on the same 208 canvas, stroke 24 unless noted.
const roundedPoly = (pts, r) => poly(pts.concat([pts[0]]), 2 * r) + polyFill(pts);
function polyFill(pts) { let a = 0; pts.forEach((p, i) => { const q = pts[(i + 1) % pts.length]; a += p[0] * q[1] - q[0] * p[1]; }); if (a > 0) pts = pts.slice().reverse(); return 'M' + pts.map((p) => `${n(p[0])} ${n(p[1])}`).join(' L') + ' Z'; }
export const steamKeySmall = ring(60, 60, 42, 18) + pill(88, 88, 166, 166, 30) + pill(142, 142, 118, 166, 26);
export const steamCandidates = {
  // 1: path past a barrier: two nodes, an arch hops over a post. The old drawing minus the arrowhead, ends on nodes.
  bridge: sector(104, 128, 60, 24, 180, 360) + disc(44, 134, 20) + disc(164, 134, 20) + pill(104, 104, 104, 156, 28),
  // 2: path past a barrier, squared: ground line is cut by a wall, the route steps over it.
  detour: poly([[36, 148], [60, 148], [60, 64], [148, 64], [148, 148], [172, 148]], 24) + roundRectFill(84, 108, 40, 60, 12),
  // 3: a key
  key: ring(66, 66, 38, 16) + pill(92, 92, 172, 172, 24) + pill(131, 131, 109, 153, 18) + pill(157, 157, 135, 179, 18),
  // 4: a shield with a gap (the route passes through)
  shield: poly([[168, 76], [168, 108], [104, 176], [40, 108], [40, 52], [104, 30], [146, 44]], 24) + disc(104, 100, 18) + pill(104, 100, 176, 40, 20),
  // 5: a signpost: two boards on a pole
  signpost: pill(104, 24, 104, 184, 24) + roundedPoly([[52, 44], [138, 44], [160, 64], [138, 84], [52, 84]].map(([x, y]) => [x, y]), 6) + roundedPoly([[156, 104], [70, 104], [48, 124], [70, 144], [156, 144]], 6),
  // 6: an open padlock (access granted)
  padlock: roundRect(44, 92, 120, 92, 14) + sector(104, 70, 34, 24, 180, 360) + pill(70, 70, 70, 120, 24) + pill(138, 70, 138, 52, 24),
};
function roundRectFill(x, y, w, h, r) { return roundRect(x, y, w, h, r); }

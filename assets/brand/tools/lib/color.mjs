// Colour maths: sRGB hex <-> linear, WCAG contrast, alpha compositing, luminance-preserving grey.
export const hex2rgb = (h) => { h = h.replace('#', ''); return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16)); };
export const rgb2hex = (c) => '#' + c.map((v) => Math.round(Math.max(0, Math.min(255, v))).toString(16).padStart(2, '0')).join('').toUpperCase();
const lin = (v) => { v /= 255; return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; };
const unlin = (v) => 255 * (v <= 0.0031308 ? v * 12.92 : 1.055 * v ** (1 / 2.4) - 0.055);
export const luminance = (hex) => { const [r, g, b] = hex2rgb(hex); return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b); };
export const contrast = (a, b) => { const la = luminance(a), lb = luminance(b); return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05); };
/** Composite fg (hex) at alpha over bg (hex). */
export const over = (fg, bg, a) => rgb2hex(hex2rgb(fg).map((v, i) => v * a + hex2rgb(bg)[i] * (1 - a)));
/** Grey with the same relative luminance as `hex` (so contrast against any background is unchanged). */
export const grey = (hex) => { const y = luminance(hex); const v = unlin(y); return rgb2hex([v, v, v]); };
/** HSL hue in degrees, for the "no green/red/amber" rule. */
export const hue = (hex) => {
  const [r, g, b] = hex2rgb(hex).map((v) => v / 255); const mx = Math.max(r, g, b), mn = Math.min(r, g, b), d = mx - mn;
  if (!d) return 0; let h = mx === r ? ((g - b) / d) % 6 : mx === g ? (b - r) / d + 2 : (r - g) / d + 4; h *= 60; return h < 0 ? h + 360 : h;
};
export const fmt = (x) => x.toFixed(2);

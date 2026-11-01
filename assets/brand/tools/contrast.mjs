// Contrast ratios (WCAG 2.x relative luminance) for every colour role of the kit. Writes tests/contrast.txt.
// Graphic objects (logo parts, icon glyphs) need 3:1 (WCAG 1.4.11); text needs 4.5:1. Run: node assets/brand/tools/contrast.mjs
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { COLOR } from './lib/marks.mjs';
import { contrast, hue, grey, over } from './lib/color.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const BG = [['Mica dark #202020', '#202020'], ['card dark #2B2B2B', '#2B2B2B'], ['Mica light #F3F3F3', '#F3F3F3'], ['white #FFFFFF', '#FFFFFF']];
const roles = [
  ['Bay Violet (accent block, tray block)', COLOR.accent],
  ['Ink, light theme', COLOR.inkLight],
  ['Ink, dark theme', COLOR.inkDark],
  ['Slate (quiet bay, tray neutral, template tile)', COLOR.slate],
  ['XTU blue', COLOR.xtu],
  ['PredatorSense magenta', COLOR.predator],
  ['Steam access cyan', COLOR.steam],
  ['Unsupported grey, XTU', grey(COLOR.xtu)],
  ['Unsupported grey, PredatorSense', grey(COLOR.predator)],
  ['Unsupported grey, Steam access', grey(COLOR.steam)],
];
const lines = [];
const row = (label, col, cells) => lines.push(label.padEnd(50) + col.padEnd(9) + cells.join('  '));
lines.push('Colour against each surface (graphic object needs >= 3.00). "ok" = passes 3:1; for the inks, which sit on their own theme, the other theme is shown as n/a.');
lines.push('');
lines.push('role'.padEnd(50) + 'hex'.padEnd(9) + BG.map(([n]) => n.padEnd(22)).join(''));
let fails = 0;
for (const [label, col] of roles) {
  const onlyDark = col === COLOR.inkDark, onlyLight = col === COLOR.inkLight;
  const cells = BG.map(([, bg]) => {
    const dark = bg === '#202020' || bg === '#2B2B2B';
    if ((onlyDark && !dark) || (onlyLight && dark)) return 'n/a'.padEnd(20);
    const c = contrast(col, bg), ok = c >= 3;
    if (!ok) fails++;
    return `${c.toFixed(2)} ${ok ? 'ok ' : 'FAIL'}`.padEnd(20);
  });
  row(label, col, cells);
}
lines.push('');
lines.push('White glyph on a tile (the module marks and the app icon): >= 3.00 needed');
const tiles = [['App icon tile, Bay Violet', COLOR.accent], ['XTU profiles', COLOR.xtu], ['PredatorSense', COLOR.predator], ['Steam access', COLOR.steam], ['Template / unknown', COLOR.template], ['Unsupported XTU', grey(COLOR.xtu)], ['Unsupported PredatorSense', grey(COLOR.predator)], ['Unsupported Steam access', grey(COLOR.steam)]];
for (const [label, col] of tiles) { const c = contrast('#FFFFFF', col); if (c < 3) fails++; lines.push(label.padEnd(50) + col.padEnd(9) + `${c.toFixed(2)} ${c >= 3 ? 'ok' : 'FAIL'}`); }
lines.push('');
lines.push('Ink on tile / on the other surfaces it meets');
for (const [label, a, b] of [['Ink light on white', COLOR.inkLight, '#FFFFFF'], ['Ink light on Mica light', COLOR.inkLight, '#F3F3F3'], ['Ink dark on Mica dark', COLOR.inkDark, '#202020'], ['Ink dark on card dark', COLOR.inkDark, '#2B2B2B'], ['Ink dark on social background #171A22', COLOR.inkDark, '#171A22'], ['Bay Violet on social background #171A22', COLOR.accent, '#171A22'], ['Bay Violet on ink light (block next to bay)', COLOR.accent, COLOR.inkLight], ['Quiet bay slate vs quiet block ink (light)', COLOR.slate, COLOR.inkLight]])
  lines.push(label.padEnd(50) + `${contrast(a, b).toFixed(2)}`);
lines.push('');
lines.push('Hues (HSL degrees). Brand colours must stay clear of the state colours: green ~150, red ~357, amber ~38.');
for (const [label, col] of [['Bay Violet', COLOR.accent], ['XTU blue', COLOR.xtu], ['PredatorSense magenta', COLOR.predator], ['Steam access cyan', COLOR.steam]]) lines.push(label.padEnd(50) + col.padEnd(9) + hue(col).toFixed(0) + '°');
for (const [label, col] of [['(state) running green', '#2FB67C'], ['(state) failed red', '#E5484D'], ['(state) warning amber', '#F5A524']]) lines.push(label.padEnd(50) + col.padEnd(9) + hue(col).toFixed(0) + '°');
lines.push('');
lines.push(fails ? `${fails} FAILURES` : 'all listed pairs pass 3:1');
const text = lines.join('\n');
console.log(text);
fs.mkdirSync(path.join(ROOT, 'tests'), { recursive: true });
fs.writeFileSync(path.join(ROOT, 'tests', 'contrast.txt'), text + '\n');
process.exit(fails ? 1 : 0);

// Test sheets from the logo-design checklist. Run: node assets/brand/tools/build-sheets.mjs [sheet-name ...]
import { modules } from './build-svgs.mjs';
import { COLOR } from './lib/marks.mjs';
import { svgImg, svgText, pxImg, page, saveSheet, svgUri } from './lib/sheet.mjs';

const only = process.argv.slice(2);
const want = (n) => !only.length || only.includes(n);
const mod = (id) => modules.find((m) => m.id === id);
const FAM = ['xtu', 'predatorsense', 'steam-access', 'module-template', 'module-unknown'];

// ---------------------------------------------------------------- 01 overview
if (want('01-kit-overview')) {
  const cell = (inner, cap) => `<div class="cell">${inner}<div class="cap">${cap}</div></div>`;
  const body = `<div class="wrap">
  <h1>Rigbay logo kit: overview</h1>
  <p class="sub">Direction B Dock (symbol) and direction 1 Tile (modules). Everything on this sheet is rendered from the SVG masters in <code>assets/brand/</code>.</p>
  <div class="grid" style="grid-template-columns:1fr 1fr">
    <div class="panel"><h2>Logo, light</h2><div class="row" style="align-items:center;gap:32px">${svgImg('rigbay/rigbay-symbol.svg', 160)}${svgImg('rigbay/rigbay-lockup-horizontal.svg', 520, 116)}</div></div>
    <div class="panel dark"><h2>Logo, reversed</h2><div class="row" style="align-items:center;gap:32px">${svgImg('rigbay/rigbay-symbol-reversed.svg', 160)}${svgImg('rigbay/rigbay-lockup-horizontal-reversed.svg', 520, 116)}</div></div>
    <div class="panel"><h2>Forms of the symbol</h2><div class="row" style="align-items:center">
      ${cell(svgImg('rigbay/rigbay-symbol.svg', 96), 'colour')}${cell(svgImg('rigbay/rigbay-symbol-black.svg', 96), 'black')}${cell(svgImg('rigbay/rigbay-symbol-quiet.svg', 96), 'quiet')}${cell(svgImg('rigbay/rigbay-symbol-small.svg', 96), 'small cut (16, 24 px)')}${cell(`<div style="width:96px;height:96px;color:#1F2430;--rigbay-accent:#0F7B6C">${svgText('rigbay/rigbay-symbol-accent.svg').replace(/width="208" height="208"/, 'width="96" height="96"')}</div>`, 'accent variable, inline SVG, accent set by CSS (teal here)')}</div></div>
    <div class="panel dark"><h2>Forms of the symbol, dark</h2><div class="row" style="align-items:center">
      ${cell(svgImg('rigbay/rigbay-symbol-reversed.svg', 96), 'reversed (thinned 1 unit per edge)')}${cell(svgImg('rigbay/rigbay-symbol-white.svg', 96), 'white')}${cell(svgImg('rigbay/rigbay-symbol-quiet-reversed.svg', 96), 'quiet')}${cell(svgImg('rigbay/rigbay-symbol-small-reversed.svg', 96), 'small cut')}</div></div>
    <div class="panel"><h2>App icon (256 / 64 / 32 / 16 true size)</h2><div class="row" style="align-items:flex-end">
      ${cell(pxImg('app-icon/png/rigbay-256.png'), '256')}${cell(pxImg('app-icon/png/rigbay-64.png'), '64')}${cell(pxImg('app-icon/png/rigbay-32.png'), '32')}${cell(pxImg('app-icon/png/rigbay-24.png'), '24 small cut')}${cell(pxImg('app-icon/png/rigbay-16.png'), '16 small cut')}</div></div>
    <div class="panel"><h2>Module family (Tile)</h2><div class="row" style="align-items:center">
      ${FAM.map((id) => cell(pxImg(`modules/png/${id}-64.png`), mod(id).name)).join('')}</div>
      <div class="row" style="margin-top:12px;align-items:center">${FAM.map((id) => cell(pxImg(`modules/png/${id}-glyph-48.png`), 'quiet glyph')).join('')}${FAM.slice(0, 4).map((id) => cell(pxImg(`modules/png/${id}-unsupported-48.png`), 'unsupported')).join('')}</div></div>
  </div></div>`;
  console.log(saveSheet('test-01-kit-overview', page(body), 1280));
}

// ---------------------------------------------------------------- 02 pixel test, 16 px
if (want('02-pixel-16')) {
  const Z = 10;
  const tile = (inner, cap, bg) => `<div class="cell"><div style="background:${bg};padding:6px;border-radius:6px">${inner}</div><div class="cap">${cap}</div></div>`;
  const L = '#F3F3F3', D = '#202020';
  const both = (rel, cap) => tile(pxImg(rel, Z), cap + ' on light', L) + tile(pxImg(rel, Z), cap + ' on dark', D);
  const body = `<div class="wrap"><h1>16 px pixel test (each pixel ×${Z})</h1>
  <p class="sub">Every image is the real 16 px (or 24 px) PNG that ships in the kit, enlarged with nearest neighbour so every pixel is visible. The question for each: does the idea survive?</p>
  <div class="panel"><h2>Rigbay symbol and app icon at 16 px</h2><div class="row">
    ${tile(pxImg('rigbay/png/rigbay-symbol-16.png', Z), 'symbol small cut, light', L)}${tile(pxImg('rigbay/png/rigbay-symbol-reversed-16.png', Z), 'symbol small cut, dark', D)}
    ${tile(pxImg('app-icon/png/rigbay-16.png', Z), 'app icon 16', L)}${tile(pxImg('app-icon/png/rigbay-20.png', Z), 'app icon 20', L)}${tile(pxImg('app-icon/png/rigbay-24.png', 6), 'app icon 24 (×6)', L)}
    ${tile(pxImg('app-icon/tray/png/rigbay-tray-light-16.png', Z), 'tray light', L)}${tile(pxImg('app-icon/tray/png/rigbay-tray-dark-16.png', Z), 'tray dark', D)}${tile(pxImg('app-icon/tray/png/rigbay-tray-neutral-16.png', Z), 'tray neutral on dark', D)}${tile(pxImg('app-icon/tray/png/rigbay-tray-neutral-16.png', Z), 'tray neutral on light', L)}
  </div></div>
  <div class="panel" style="margin-top:14px"><h2>Module tiles at 16 px, and the quiet glyphs</h2><div class="row">
    ${FAM.map((id) => tile(pxImg(`modules/png/${id}-16.png`, Z), mod(id).name, L)).join('')}
  </div><div class="row" style="margin-top:12px">
    ${FAM.map((id) => tile(pxImg(`modules/png/${id}-glyph-16.png`, Z), mod(id).name + ' glyph, light', L)).join('')}</div>
  <div class="row" style="margin-top:12px">
    ${FAM.map((id) => tile(pxImg(`modules/png/${id}-glyph-16.png`, Z), mod(id).name + ' glyph, dark', D)).join('')}</div></div></div>`;
  console.log(saveSheet('test-02-pixel-16', page(body), 1500));
}

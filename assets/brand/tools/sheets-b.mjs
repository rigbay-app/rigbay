// Test sheets 03 to 07. Run: node assets/brand/tools/sheets-b.mjs [sheet-name ...]
import { modules } from './build-svgs.mjs';
import { svgImg, pxImg, page, saveSheet } from './lib/sheet.mjs';

const only = process.argv.slice(2);
const want = (n) => !only.length || only.includes(n);
const mod = (id) => modules.find((m) => m.id === id);
const FAM = ['xtu', 'predatorsense', 'steam-access', 'module-template', 'module-unknown'];
const gi = (c, s) => `<span style="display:inline-block;width:${s}px;height:${s}px;border-radius:${s * 0.22}px;background:${c}"></span>`;

// ---------------------------------------------------------------- 03 one colour, reversed, backgrounds
if (want('03-one-colour-reversed')) {
  const box = (bg, inner, cap, w = 200, h = 150) => `<div class="cell"><div style="background:${bg};width:${w}px;height:${h}px;border-radius:8px;display:flex;align-items:center;justify-content:center;border:1px solid rgba(0,0,0,.08)">${inner}</div><div class="cap" style="max-width:${w}px">${cap}</div></div>`;
  const stripes = 'repeating-linear-gradient(45deg,#c9ced9 0 14px,#a7aebd 14px 28px)';
  const photo = 'linear-gradient(135deg,#2c4a6e,#7a8f5a 45%,#d9b87a 70%,#6b3f4f)';
  const S = (rel, s = 84) => svgImg(rel, s);
  const rows = [
    ['Colour on light and on the brand colour', [['#FFFFFF', S('rigbay/rigbay-symbol.svg'), 'colour on white'], ['#F3F3F3', S('rigbay/rigbay-symbol.svg'), 'colour on Mica light'], ['#7A5AF8', S('rigbay/rigbay-symbol-white.svg'), 'white one-colour on Bay Violet'], [stripes, S('rigbay/rigbay-symbol-black.svg'), 'black on a busy pattern'], [photo, S('rigbay/rigbay-symbol-white.svg'), 'white on a photo stand-in']]],
    ['One colour', [['#FFFFFF', S('rigbay/rigbay-symbol-black.svg'), 'black on white'], ['#000000', S('rigbay/rigbay-symbol-white.svg'), 'white on black (thinned file)'], ['#FFFFFF', S('app-icon/high-contrast/rigbay-hc-black.svg'), 'high contrast black (small cut)'], ['#000000', S('app-icon/high-contrast/rigbay-hc-white.svg'), 'high contrast white (small cut)'], ['#FFF200', S('app-icon/high-contrast/rigbay-hc-black.svg'), 'black on a high contrast yellow']]],
    ['Reversed (and one deliberate failure)', [['#202020', S('rigbay/rigbay-symbol-reversed.svg'), 'on Mica dark #202020'], ['#2B2B2B', S('rigbay/rigbay-symbol-reversed.svg'), 'on card dark #2B2B2B'], ['#1F2430', S('rigbay/rigbay-symbol-reversed.svg'), 'on the ink colour'], ['#202020', S('rigbay/rigbay-symbol.svg'), 'WRONG FILE on purpose: the light file on dark, the dock vanishes'], ['#202020', S('rigbay/rigbay-symbol-quiet-reversed.svg'), 'quiet, reversed']]],
  ];
  const lock = (bg, rel, cap) => box(bg, svgImg(rel, 360, 80), cap, 420, 120);
  const body = `<div class="wrap"><h1>One colour, reversed, busy backgrounds</h1><p class="sub">Checklist 2: black on white, white on black without looking heavier, on the brand colour, on photos and patterns, high contrast.</p>
  ${rows.map(([t, cs]) => `<div class="panel" style="margin-bottom:14px"><h2>${t}</h2><div class="row">${cs.map(([bg, inner, cap]) => box(bg, inner, cap)).join('')}</div></div>`).join('')}
  <div class="panel"><h2>Lockups</h2><div class="row">
    ${lock('#FFFFFF', 'rigbay/rigbay-lockup-horizontal.svg', 'colour on white')}${lock('#202020', 'rigbay/rigbay-lockup-horizontal-reversed.svg', 'reversed on dark')}${lock('#FFFFFF', 'rigbay/rigbay-lockup-horizontal-black.svg', 'black')}${lock('#000000', 'rigbay/rigbay-lockup-horizontal-white.svg', 'white')}
    ${lock('#7A5AF8', 'rigbay/rigbay-lockup-horizontal-white.svg', 'white on Bay Violet')}${lock(stripes, 'rigbay/rigbay-lockup-horizontal-black.svg', 'black on a pattern')}</div></div></div>`;
  console.log(saveSheet('test-03-one-colour-reversed', page(body), 1180));
}

// ---------------------------------------------------------------- 04 squint, mirror, rotation, greyscale
if (want('04-squint-mirror')) {
  const items = [['Dock symbol', 'rigbay/rigbay-symbol.svg'], ['App icon', 'app-icon/rigbay-appicon.svg'], ...FAM.map((id) => [mod(id).name, `modules/${id}.svg`])];
  const variant = (rel, style, cap) => `<div class="cell"><div style="width:96px;height:96px;display:flex;align-items:center;justify-content:center;background:#fff;border-radius:6px">${svgImg(rel, 80, 80, style)}</div><div class="cap">${cap}</div></div>`;
  const body = `<div class="wrap"><h1>Squint, mirror, rotate, greyscale</h1><p class="sub">Squint = blur 3 and 6 px at 80 px (silhouette only). Mirror and rotations look for accidental letters or bad readings. Greyscale checks that nothing relies on hue.</p>
  ${items.map(([name, rel]) => `<div class="panel" style="margin-bottom:10px"><h2>${name}</h2><div class="row" style="align-items:center">
    ${variant(rel, '', 'original')}${variant(rel, 'filter:blur(3px)', 'squint')}${variant(rel, 'filter:blur(6px)', 'squint, harder')}${variant(rel, 'transform:scaleX(-1)', 'mirror')}${variant(rel, 'transform:rotate(90deg)', 'rotate 90')}${variant(rel, 'transform:rotate(180deg)', 'rotate 180')}${variant(rel, 'filter:grayscale(1)', 'greyscale')}</div></div>`).join('')}</div>`;
  console.log(saveSheet('test-04-squint-mirror', page(body), 960));
}

// ---------------------------------------------------------------- 05 taskbar and tray (Windows 11 mock)
if (want('05-taskbar-tray')) {
  const generic = ['#4C8BF5', '#E8A33D', '#3FB68B', '#C65D7B', '#6C7A89'];
  const sysGlyph = (fg, s) => `<span style="display:inline-block;width:${s * 0.8}px;height:${s * 0.5}px;border:${Math.max(1, s / 12)}px solid ${fg};border-radius:${s / 8}px;opacity:.85"></span>`;
  const taskbar = (bg, fg, theme, k, trayRel, label) => `<div class="cell" style="align-items:flex-start"><div style="zoom:${k}"><div style="display:flex;align-items:center;justify-content:space-between;height:48px;width:${k > 1 ? 300 : 640}px;background:${bg};color:${fg};padding:0 8px;font-size:12px;border-top:1px solid ${theme === 'dark' ? '#333' : '#d8d8d8'}">
      <div style="display:flex;align-items:center;gap:${k > 1 ? 8 : 14}px;margin-left:${k > 1 ? 4 : 120}px"><span style="display:inline-grid;grid-template-columns:repeat(2,8px);gap:2px">${'<i style="width:8px;height:8px;background:#4C8BF5;display:block"></i>'.repeat(4)}</span>
        ${gi(generic[0], 24)}${gi(generic[1], 24)}
        <span style="position:relative;display:inline-block;width:24px;height:24px">${pxImg('app-icon/png/rigbay-24.png', 1)}<i style="position:absolute;left:8px;bottom:-6px;width:8px;height:3px;border-radius:2px;background:${fg}"></i></span>
        ${k > 1 ? '' : gi(generic[2], 24) + gi(generic[3], 24)}</div>
      <div style="display:flex;align-items:center;gap:10px;margin-right:6px"><span style="opacity:.8">^</span>${sysGlyph(fg, 16)}${pxImg(trayRel, 1)}${sysGlyph(fg, 16)}${k > 1 ? '' : '<span style="text-align:right;line-height:1.1;font-size:11px">21:14<br>1/11/2026</span>'}</div></div></div><div class="cap" style="max-width:none;text-align:left">${label}</div></div>`;
  const spec = [
    ['#202020', '#EEF1F8', 'dark', 'dark taskbar #202020', 'app-icon/tray/png/rigbay-tray-dark-16.png'],
    ['#2B2B2B', '#EEF1F8', 'dark', 'dark taskbar, lighter #2B2B2B', 'app-icon/tray/png/rigbay-tray-dark-16.png'],
    ['#F3F3F3', '#1F2430', 'light', 'light taskbar #F3F3F3', 'app-icon/tray/png/rigbay-tray-light-16.png'],
    ['#FFFFFF', '#1F2430', 'light', 'light taskbar, white #FFFFFF', 'app-icon/tray/png/rigbay-tray-light-16.png'],
  ];
  const ladder = (bg, trayV, label) => `<div style="background:${bg};padding:10px 14px;border-radius:6px;display:flex;gap:18px;align-items:center">${[16, 20, 24, 32].map((s) => `<span>${pxImg(`app-icon/tray/png/rigbay-tray-${trayV}-${s}.png`, 1)}</span>`).join('')}<span style="font-size:10px;color:${bg === '#202020' || bg === '#2B2B2B' ? '#9aa3b5' : '#6a7285'}">${label}</span></div>`;
  const body = `<div class="wrap"><h1>Windows 11 taskbar and tray mock</h1>
  <p class="sub">A mock, not a capture: generic neighbours, nothing real was screen-grabbed. Tray icon = the small cut with fixed colours, taskbar icon = the 24 px app icon. First block ×1 (true pixels at 100 % scale), second ×3 (the same pixels enlarged).</p>
  <div class="panel"><h2>×1: true size, 100 % display scale</h2><div class="grid">${spec.map(([bg, fg, th, lab, tr]) => taskbar(bg, fg, th, 1, tr, lab)).join('')}</div></div>
  <div class="panel" style="margin-top:14px"><h2>×3: enlarged (Rigbay taskbar icon centre-left, tray icon right)</h2><div class="grid" style="grid-template-columns:auto;justify-content:start">${spec.filter((x) => x[0] === '#202020' || x[0] === '#F3F3F3').map(([bg, fg, th, lab, tr]) => taskbar(bg, fg, th, 3, tr, lab)).join('')}</div></div>
  <div class="panel" style="margin-top:14px"><h2>Tray at 100 / 125 / 150 / 200 % (16 / 20 / 24 / 32 px) on each taskbar</h2><div class="row">
    ${ladder('#202020', 'dark', 'dark #202020')}${ladder('#2B2B2B', 'dark', 'dark #2B2B2B')}${ladder('#F3F3F3', 'light', 'light #F3F3F3')}${ladder('#FFFFFF', 'light', 'white #FFFFFF')}
    ${ladder('#202020', 'neutral', 'neutral on dark')}${ladder('#F3F3F3', 'neutral', 'neutral on light')}</div></div>
  <div class="panel" style="margin-top:14px"><h2>Taskbar icon frames used at 100 / 125 / 150 / 200 % (24, 32, 40, 48 px from the .ico)</h2><div class="row" style="align-items:center">
    ${[24, 32, 40, 48].map((s) => `<div style="background:#202020;padding:10px;border-radius:6px">${pxImg(`app-icon/png/rigbay-${s}.png`, 1)}</div><div style="background:#F3F3F3;padding:10px;border-radius:6px">${pxImg(`app-icon/png/rigbay-${s}.png`, 1)}</div>`).join('')}</div></div></div>`;
  console.log(saveSheet('test-05-taskbar-tray', page(body), 1000));
}

// ---------------------------------------------------------------- 06 Start tile
if (want('06-start-tile')) {
  const generic = ['#4C8BF5', '#E8A33D', '#3FB68B', '#C65D7B', '#6C7A89', '#8A6BD6', '#2E9CCA', '#D1773B'];
  const names = ['Editor', 'Calc', 'Photos', 'Files', 'Mail', 'Shell', 'Clock', 'Maps'];
  const app = (inner, name, fg) => `<div style="width:84px;display:flex;flex-direction:column;align-items:center;gap:6px;color:${fg};font-size:11px">${inner}<span>${name}</span></div>`;
  const start = (bg, fg, sub, theme) => {
    const apps = names.map((n, i) => app(gi(generic[i], 32), n, fg));
    apps.splice(3, 0, app(pxImg('app-icon/png/rigbay-32.png'), 'Rigbay', fg));
    const list = [['Rigbay', 'app-icon/png/rigbay-24.png'], ['Editor', 0], ['Files', 1]];
    return `<div style="width:560px;background:${bg};color:${fg};border-radius:10px;padding:18px 18px 14px;box-shadow:0 0 0 1px ${theme === 'dark' ? '#3a3a3a' : '#d0d0d0'}">
      <div style="font-weight:600;margin-bottom:10px">Pinned</div><div style="display:flex;flex-wrap:wrap;row-gap:12px">${apps.join('')}</div>
      <div style="font-weight:600;margin:16px 0 8px">All apps</div>
      ${list.map(([n, ic]) => `<div style="display:flex;align-items:center;gap:12px;height:34px;padding:0 6px;border-radius:5px;${n === 'Rigbay' ? `background:${theme === 'dark' ? '#333' : '#E6E6E6'}` : ''}">${typeof ic === 'string' ? pxImg(ic) : gi(generic[ic + 4], 24)}<span>${n}</span></div>`).join('')}
      <div style="font-weight:600;margin:16px 0 8px">Search result (48 px)</div><div style="display:flex;align-items:center;gap:14px;padding:6px">${pxImg('app-icon/png/rigbay-48.png')}<div><div>Rigbay</div><div style="color:${sub};font-size:11px">App</div></div></div></div>`;
  };
  const body = `<div class="wrap"><h1>Start menu mock</h1><p class="sub">Pinned (32 px), All apps (24 px) and search (48 px) with generic neighbours (flat squares with plain-word labels). The Rigbay icons are the real PNG frames of <code>rigbay.ico</code>.</p>
  <div class="row" style="align-items:flex-start">${start('#F3F3F3', '#1F2430', '#5a6273', 'light')}${start('#2B2B2B', '#EEF1F8', '#9aa3b5', 'dark')}</div></div>`;
  console.log(saveSheet('test-06-start-tile', page(body), 1260));
}

// ---------------------------------------------------------------- 07 dashboard rows and navigation
if (want('07-dashboard-rows')) {
  const bar = (state) => state === 'on' ? '<i style="display:block;width:4px;height:32px;border-radius:2px;background:#2FB67C"></i>' : state === 'fail' ? '<i style="display:block;width:4px;height:32px;border-radius:2px;background:#E5484D"></i>' : '<i style="display:block;width:4px;height:32px;border-radius:2px;border:1px solid #8a8f9c;box-sizing:border-box"></i>';
  const T = (theme) => theme === 'dark' ? { bg: '#2B2B2B', page: '#202020', fg: '#EEF1F8', sec: '#A8AFBD', hair: '#3a3a3a', sel: '#3a3a3a' } : { bg: '#FFFFFF', page: '#F3F3F3', fg: '#1F2430', sec: '#5a6273', hair: '#E3E3E3', sel: '#E4E4E4' };
  const rows = (theme, kind) => {
    const t = T(theme);
    const ic = (id, s = 24) => kind === 'tile' ? pxImg(`modules/png/${id}-${s}.png`) : pxImg(`modules/png/${id}-glyph-${s}.png`);
    const unsup = () => kind === 'tile' ? pxImg('modules/png/module-unknown-unsupported-24.png') : pxImg('modules/png/module-unknown-glyph-24.png', 1, 'opacity:.45');
    const list = [
      ['on', () => ic('xtu'), 'XTU profiles', 'Running  ·  Profile "Quiet"', 'PL1 45 W'],
      ['off', () => ic('predatorsense'), 'PredatorSense', 'Off  ·  Probe: supported', ''],
      ['on', () => ic('steam-access'), 'Steam access', 'Running  ·  Proxy on :18081', ''],
      ['off', unsup, 'Sensors recorder', 'Unsupported  ·  Needs an XTU source', ''],
      ['fail', () => ic('module-unknown'), 'Third-party module', 'Failed  ·  View log (no icon of its own: the "?" tile)', ''],
    ];
    return `<div style="width:560px;background:${t.page};padding:14px;border-radius:10px"><div style="background:${t.bg};color:${t.fg};border-radius:8px;box-shadow:0 0 0 1px ${t.hair}">
      ${list.map(([st, f, name, sub, read], i) => `<div style="display:flex;align-items:center;gap:12px;height:56px;padding:0 14px;${i ? `border-top:1px solid ${t.hair}` : ''}">${bar(st)}${f()}<div style="flex:1"><div style="font-size:13px">${name}</div><div style="font-size:11px;color:${t.sec}">${sub}</div></div><div style="font-variant-numeric:tabular-nums;color:${t.sec};font-size:12px">${read}</div></div>`).join('')}</div></div>`;
  };
  const nav = (theme, kind) => {
    const t = T(theme), ic = (id, s) => kind === 'tile' ? pxImg(`modules/png/${id}-${s}.png`) : pxImg(`modules/png/${id}-glyph-${s}.png`);
    return `<div style="width:250px;background:${t.page};color:${t.fg};border-radius:8px;padding:8px;font-size:13px">${['xtu', 'predatorsense', 'steam-access'].map((id, i) => `<div style="display:flex;align-items:center;gap:12px;height:36px;padding:0 10px;border-radius:5px;${i === 0 ? `background:${t.sel}` : ''}">${ic(id, 16)}<span>${mod(id).name}</span></div>`).join('')}</div>`;
  };
  const mp = (theme) => {
    const t = T(theme);
    return `<div style="width:560px;background:${t.page};padding:14px;border-radius:10px"><div style="background:${t.bg};color:${t.fg};border-radius:8px;box-shadow:0 0 0 1px ${t.hair}">${['xtu', 'predatorsense', 'steam-access'].map((id, i) => `<div style="display:flex;align-items:center;gap:14px;height:72px;padding:0 16px;${i ? `border-top:1px solid ${t.hair}` : ''}">${pxImg(`modules/png/${id}-48.png`)}<div style="flex:1"><div style="font-size:14px">${mod(id).name}</div><div style="font-size:12px;color:${t.sec}">Installed  ·  1.0.0</div></div></div>`).join('')}</div></div>`;
  };
  const body = `<div class="wrap"><h1>Dashboard rows, navigation and Modules page</h1><p class="sub">The rack from <code>docs/UI-DIRECTION.md</code>: a state bar (outlined = off, green = running, red = failed) next to the module icon. Left: Tile form (24 px). Right: quiet glyph form (accent colour, no tile), the fallback if tiles feel loud next to the state bars. The fourth row shows the unsupported rule, the fifth the "?" tile for a module without an icon.</p>
  <div class="row" style="align-items:flex-start">${rows('light', 'tile')}${rows('light', 'glyph')}</div>
  <div class="row" style="align-items:flex-start;margin-top:14px">${rows('dark', 'tile')}${rows('dark', 'glyph')}</div>
  <h2 style="margin:22px 0 8px">Navigation pane, 16 px icons</h2><div class="row" style="align-items:flex-start">${nav('light', 'tile')}${nav('light', 'glyph')}${nav('dark', 'tile')}${nav('dark', 'glyph')}</div>
  <h2 style="margin:22px 0 8px">Modules page, 48 px tiles</h2><div class="row" style="align-items:flex-start">${mp('light')}${mp('dark')}</div></div>`;
  console.log(saveSheet('test-07-dashboard-rows', page(body), 1200));
}

// Draws the Battery Manager app icon: a graphite tile carrying the in-app battery glyph (outline, gap, continuous fill).
// Below 48 px every frame comes from a hand table so each edge lands on a whole pixel, as BatteryGlyph does;
// from 48 px up the drawing is smooth, with a soft shadow inside the frame.
//
// usage (needs Node and Playwright with Chromium):
//   node app-icon.js <out-dir>
//   python make-ico.py <out-dir> ../../src/BatteryManager.App/Assets/AppIcon.ico
// then copy <out-dir>/{16,20,24,28,32,36,40,48}.png to src/BatteryManager.App/Assets/AppIcon/.
const fs = require('fs');

const SIZES = [16, 20, 24, 28, 30, 32, 36, 40, 48, 56, 60, 64, 72, 80, 96, 128, 256];
const PALETTE = { top: '#4A4F57', bottom: '#25282D', ink: '#FFFFFF', rim: 'rgba(255,255,255,0.10)', highlight: 0.14, shadow: 0.35 };

// m tile margin, tr tile radius, sw/sh battery shell, st outline, gp gap, tg/tw/th terminal gap/width/height, fill width.
const TABLE = {
  16: { m: 0, tr: 3, sw: 10, sh: 8, st: 1, gp: 1, tg: 1, tw: 1, th: 4, fill: 4 },
  20: { m: 1, tr: 4, sw: 12, sh: 8, st: 1, gp: 1, tg: 1, tw: 1, th: 4, fill: 6 },
  24: { m: 1, tr: 5, sw: 14, sh: 8, st: 1, gp: 1, tg: 1, tw: 1, th: 4, fill: 7 },
  28: { m: 2, tr: 5, sw: 15, sh: 10, st: 2, gp: 1, tg: 1, tw: 2, th: 4, fill: 6 },
  30: { m: 2, tr: 6, sw: 17, sh: 10, st: 2, gp: 1, tg: 1, tw: 2, th: 4, fill: 8 },
  32: { m: 2, tr: 6, sw: 17, sh: 10, st: 2, gp: 1, tg: 1, tw: 2, th: 4, fill: 8 },
  36: { m: 2, tr: 7, sw: 19, sh: 12, st: 2, gp: 1, tg: 1, tw: 2, th: 6, fill: 9 },
  40: { m: 2, tr: 8, sw: 21, sh: 12, st: 2, gp: 1, tg: 1, tw: 2, th: 6, fill: 11 },
};

// Smooth frames: the glyph uses the in-app proportions (24 x 14 grid, shell 20.5 x 11.5) at 66% of the tile.
function smooth(S) {
  const m = S * 0.0625, T = S - 2 * m, u = T * 0.66 / 23;
  const sw = 20.5 * u, sh = 11.5 * u, st = 1.6 * u, gp = 1.2 * u;
  return { m, tr: T * 0.22, sw, sh, st, gp, tg: u, tw: 1.6 * u, th: 4.5 * u, r: 3.25 * u, fill: (sw - 2 * (st + gp)) * 0.7 };
}

const n = v => Math.round(v * 1000) / 1000;
function rounded(x, y, w, h, r) {
  r = Math.max(0, Math.min(r, w / 2, h / 2));
  if (r === 0) return `M${n(x)},${n(y)}h${n(w)}v${n(h)}h${n(-w)}Z`;
  return `M${n(x + r)},${n(y)}H${n(x + w - r)}A${n(r)},${n(r)} 0 0 1 ${n(x + w)},${n(y + r)}V${n(y + h - r)}` +
    `A${n(r)},${n(r)} 0 0 1 ${n(x + w - r)},${n(y + h)}H${n(x + r)}A${n(r)},${n(r)} 0 0 1 ${n(x)},${n(y + h - r)}` +
    `V${n(y + r)}A${n(r)},${n(r)} 0 0 1 ${n(x + r)},${n(y)}Z`;
}

function svg(S) {
  const big = S >= 48;
  const p = big ? smooth(S) : TABLE[S];
  const T = S - 2 * p.m;
  const total = p.sw + p.tg + p.tw;
  const x = big ? (S - total) / 2 : Math.floor((S - total) / 2), y = (S - p.sh) / 2;
  const r = big ? p.r : Math.min(Math.max(1, Math.round(p.sh * 0.28)), 3);
  let defs = `<linearGradient id="tile" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${PALETTE.top}"/><stop offset="1" stop-color="${PALETTE.bottom}"/></linearGradient>` +
    `<linearGradient id="light" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity="${PALETTE.highlight}"/><stop offset="0.5" stop-color="#fff" stop-opacity="0"/></linearGradient>`;
  if (big) defs += `<filter id="shadow" x="-20%" y="-20%" width="140%" height="150%"><feDropShadow dx="0" dy="${n(S * 0.016)}" stdDeviation="${n(S * 0.022)}" flood-color="#000" flood-opacity="${PALETTE.shadow}"/></filter>`;
  let tile = `<path fill="url(#tile)" d="${rounded(p.m, p.m, T, T, p.tr)}"/>`;
  if (big) tile += `<path fill="url(#light)" d="${rounded(p.m, p.m, T, T, p.tr)}"/>`;
  const k = big ? Math.max(1, S / 128) : 1;   // inner rim: keeps the tile edge visible on a dark taskbar
  tile += `<path fill-rule="evenodd" fill="${PALETTE.rim}" d="${rounded(p.m, p.m, T, T, p.tr)}${rounded(p.m + k, p.m + k, T - 2 * k, T - 2 * k, Math.max(0, p.tr - k))}"/>`;
  let body = big ? `<g filter="url(#shadow)">${tile}</g>` : tile;
  let glyph = `<path fill-rule="evenodd" d="${rounded(x, y, p.sw, p.sh, r)}${rounded(x + p.st, y + p.st, p.sw - 2 * p.st, p.sh - 2 * p.st, Math.max(0, r - p.st))}"/>`;
  glyph += `<path d="${rounded(x + p.sw + p.tg, y + (p.sh - p.th) / 2, p.tw, p.th, big ? p.tw / 2 : 0)}"/>`;
  const inset = p.st + p.gp;
  glyph += `<path d="${rounded(x + inset, y + inset, p.fill, p.sh - 2 * inset, big ? Math.max(0, r - inset) : 0)}"/>`;
  body += `<g fill="${PALETTE.ink}">${glyph}</g>`;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${S}" height="${S}" viewBox="0 0 ${S} ${S}"><defs>${defs}</defs>${body}</svg>`;
}

if (require.main === module) {
  const out = process.argv[2];
  if (!out) { console.error('usage: node app-icon.js <out-dir>'); process.exit(2); }
  fs.mkdirSync(out, { recursive: true });
  (async () => {
    const { chromium } = require('playwright');
    const browser = await chromium.launch();
    const page = await browser.newPage({ deviceScaleFactor: 1 });
    for (const S of SIZES) {
      const text = svg(S);
      fs.writeFileSync(`${out}/${S}.svg`, text);
      await page.setContent(`<body style="margin:0;background:transparent">${text}</body>`);
      fs.writeFileSync(`${out}/${S}.png`, await page.locator('svg').screenshot({ omitBackground: true }));
    }
    await browser.close();
  })();
}

module.exports = { svg, SIZES };

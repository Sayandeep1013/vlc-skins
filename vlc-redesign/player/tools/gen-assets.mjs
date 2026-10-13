// Renders the cone app icon (Assets/cone.ico, PNG-compressed entries) and the MSIX/Store
// logo set (packaging/msix/Assets) from one drawing.
import { Resvg } from '@resvg/resvg-js';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const CONE = `
  <path d="M42 8h16l5.5 20h-27z" fill="#e8710a"/>
  <path d="M34.5 36h31l6 22h-43z" fill="#e8710a"/>
  <path d="M26.5 66h47l5.2 19H21.3z" fill="#e8710a"/>
  <rect x="10" y="86" width="80" height="9" rx="3" fill="#8a8a8a"/>`;

// Cone centred in a w×h canvas, occupying `fill` of the shorter side.
function png(w, h, fill = 1) {
  const s = Math.min(w, h) * fill;
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}">
    <g transform="translate(${(w - s) / 2} ${(h - s) / 2}) scale(${s / 100})">${CONE}</g></svg>`;
  return new Resvg(svg).render().asPng();
}

// ---------------------------------------------------------------- app icon
const sizes = [16, 24, 32, 48, 64, 256];
const pngs = sizes.map((s) => png(s, s));
const header = Buffer.alloc(6 + 16 * sizes.length);
header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(sizes.length, 4);
let offset = header.length;
sizes.forEach((s, i) => {
  const e = 6 + i * 16;
  header.writeUInt8(s >= 256 ? 0 : s, e); header.writeUInt8(s >= 256 ? 0 : s, e + 1);
  header.writeUInt16LE(1, e + 4); header.writeUInt16LE(32, e + 6);
  header.writeUInt32LE(pngs[i].length, e + 8); header.writeUInt32LE(offset, e + 12);
  offset += pngs[i].length;
});
fs.writeFileSync(path.resolve(here, '..', 'Assets', 'cone.ico'), Buffer.concat([header, ...pngs]));

// ---------------------------------------------------------------- MSIX logos
const out = path.resolve(here, '..', '..', 'packaging', 'msix', 'Assets');
fs.mkdirSync(out, { recursive: true });
const scales = [100, 125, 150, 200, 400];
const write = (name, buf) => fs.writeFileSync(path.join(out, name), buf);
for (const sc of scales) {
  const k = sc / 100;
  write(`Square44x44Logo.scale-${sc}.png`, png(Math.round(44 * k), Math.round(44 * k), 0.84));
  write(`Square150x150Logo.scale-${sc}.png`, png(Math.round(150 * k), Math.round(150 * k), 0.5));
  write(`Wide310x150Logo.scale-${sc}.png`, png(Math.round(310 * k), Math.round(150 * k), 0.5));
  write(`StoreLogo.scale-${sc}.png`, png(Math.round(50 * k), Math.round(50 * k), 0.9));
}
// Taskbar / Start list / file-type icons at exact pixel sizes (plated and unplated).
for (const t of [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256]) {
  write(`Square44x44Logo.targetsize-${t}.png`, png(t, t, 0.94));
  write(`Square44x44Logo.targetsize-${t}_altform-unplated.png`, png(t, t, 0.94));
  write(`Square44x44Logo.targetsize-${t}_altform-lightunplated.png`, png(t, t, 0.94));
}
console.log('wrote Assets/cone.ico and packaging/msix/Assets');

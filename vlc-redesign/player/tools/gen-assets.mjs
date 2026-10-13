// Renders the cone app icon (Assets/cone.ico) with PNG-compressed entries.
import { Resvg } from '@resvg/resvg-js';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const cone = (size) => `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 100 100">
  <path d="M42 8h16l5.5 20h-27z" fill="#e8710a"/>
  <path d="M34.5 36h31l6 22h-43z" fill="#e8710a"/>
  <path d="M26.5 66h47l5.2 19H21.3z" fill="#e8710a"/>
  <rect x="10" y="86" width="80" height="9" rx="3" fill="#8a8a8a"/></svg>`;
const sizes = [16, 24, 32, 48, 64, 256];
const pngs = sizes.map((s) => new Resvg(cone(s)).render().asPng());
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
console.log('wrote Assets/cone.ico');

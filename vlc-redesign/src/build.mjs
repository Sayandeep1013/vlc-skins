// Builds the "VLC Redesign" skins2 skin (dist/VLC-Redesign.vlt).
// Everything visual is generated from SVG here so colors and sizes live in one place.
import { Resvg } from '@resvg/resvg-js';
import { zipSync } from 'fflate';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const OUT = path.join(ROOT, 'build', 'VLC-Redesign');
const DIST = path.join(ROOT, 'dist');
const LUCIDE = path.join(ROOT, 'node_modules', 'lucide-static', 'icons');

// Palette sampled from the redesign video frames.
const C = {
  bar: '#181818',
  frame: '#121212',
  video: '#0b0b0b',
  track: '#050505',
  fill: '#3b1f05',
  accent: '#e8710a',
  accentHi: '#ff8a24',
  accentLo: '#b85500',
  icon: '#a3a3a3',
  iconHi: '#ececec',
  text: '#9a9a9a',
  title: '#b4b4b4',
  panel: '#141414',
  panelSel: '#262626',
  divider: '#232323',
  volLo: '#4f9a00',
  volHi: '#e8710a',
};

// Base geometry (skin coordinates; everything stretches from here).
const W = 960, H = 540;
const TOP = 36, BOT = 48;
const QUEUE_W = 300;
const SEEK = { x: 212, y: 10, h: 28, rightGap: 12 };
const RIGHT_W = 356;
const SEEK_W = W - SEEK.x - RIGHT_W - SEEK.rightGap;
const FSC = { w: 1000, h: 100 };
const FSC_SEEK_W = FSC.w - SEEK.x - RIGHT_W - SEEK.rightGap;
const SEEK_FRAMES = 200;
const R = 12; // window corner radius
const SEEK_R = 10;
const MENU_W = 260, GEAR_W = 280;
const EDGE = 5; // video inset; the right/bottom edges are resize handles
const VOL = { w: 84, h: 24, frames: 101 };

// Hotkey labels shown in tooltips/menus. VLC 3 defaults, overridden by whatever the
// user has customised in their vlcrc (skins use the same config, so labels match).
const KEYS = {
  'play-pause': 'Space', 'next': 'N', 'prev': 'P', 'toggle-fullscreen': 'F', 'leave-fullscreen': 'Esc',
  'vol-mute': 'M', 'audio-track': 'B', 'subtitle-track': 'V', 'subtitle-toggle': '',
  'faster': '+', 'slower': '-', 'frame-next': 'E',
  'snapshot': 'Shift+S', 'record': 'Shift+R', 'quit': 'Ctrl+Q',
};
const VLCRC = process.env.VLCRC ?? path.join(process.env.APPDATA ?? '', 'vlc', 'vlcrc');
if (fs.existsSync(VLCRC)) {
  for (const m of fs.readFileSync(VLCRC, 'utf8').matchAll(/^key-([\w-]+)=(.*)$/gm)) {
    if (m[1] in KEYS) KEYS[m[1]] = m[2].trim().replace(/(^|\+)([a-z])$/,(_, p, c) => p + c.toUpperCase());
  }
}
const key = (name) => (KEYS[name] ? ` (${KEYS[name]})` : '');
const tab = (name) => (KEYS[name] ? `&#9;${KEYS[name]}` : '');

fs.rmSync(OUT, { recursive: true, force: true });
fs.mkdirSync(OUT, { recursive: true });
fs.mkdirSync(DIST, { recursive: true });

const FONT = path.join(ROOT, 'src', 'fonts', 'Inter.ttf');
function render(svg, file) {
  const png = new Resvg(svg, {
    fitTo: { mode: 'original' },
    font: { fontFiles: [FONT], loadSystemFonts: false, defaultFontFamily: 'Inter' },
  }).render().asPng();
  fs.writeFileSync(path.join(OUT, file), png);
}

function svgDoc(w, h, body) {
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">${body}</svg>`;
}

// Inner markup of a lucide icon, optionally post-processed.
function lucide(name, tweak = (s) => s) {
  const src = fs.readFileSync(path.join(LUCIDE, `${name}.svg`), 'utf8');
  return tweak(src.slice(src.indexOf('>', src.indexOf('<svg')) + 1, src.lastIndexOf('</svg>')));
}

// skins2 blends partial alpha badly (dark fringes), so anything that sits on a
// known solid surface is rendered opaque onto that surface instead.
const onBg = (bg, w, h) => (bg ? `<rect width="${w}" height="${h}" fill="${bg}"/>` : '');

// Stroke icon drawn on a 24-unit grid, rendered at `size` px.
function strokeIcon(inner, color, bg, size = 20, stroke = 1.9) {
  return svgDoc(size, size, onBg(bg, size, size) +
    `<g transform="scale(${size / 24})" fill="none" stroke="${color}" stroke-width="${stroke}" stroke-linecap="round" stroke-linejoin="round">${inner.replaceAll('currentColor', color)}</g>`);
}

const ICONS = {
  prev: (c) => `<path d="M6 5v14" stroke="${c}" stroke-width="2.2"/><path d="M19 5.5v13L8.5 12z" fill="${c}" stroke="${c}" stroke-width="1.2"/>`,
  next: (c) => `<path d="M18 5v14" stroke="${c}" stroke-width="2.2"/><path d="M5 5.5v13L15.5 12z" fill="${c}" stroke="${c}" stroke-width="1.2"/>`,
  more: () => lucide('ellipsis'),
  volume: () => lucide('volume-2'),
  muted: () => lucide('volume-x'),
  audio: () => lucide('audio-lines'),
  subs: () => lucide('captions'),
  // The redesign's "player settings" gear has a play glyph instead of the hub.
  gear: (c) => lucide('settings', (s) => s.replace(/<circle[^>]*\/>/, '')) +
    `<path d="M10.4 9.3v5.4l4.4-2.7z" fill="${c}" stroke-width="1.2"/>`,
  queue: () => lucide('list'),
  min: () => lucide('minus'),
  max: () => '<rect x="5" y="5" width="14" height="14" rx="1.5"/>',
  restore: () => '<rect x="5" y="8" width="11" height="11" rx="1.5"/><path d="M8.5 5H17.5A1.5 1.5 0 0 1 19 6.5V15.5"/>',
  fullscreen: () => lucide('scan'),
  unfullscreen: () => lucide('minimize'),
  close: () => lucide('x'),
  sort: () => lucide('arrow-down-up'),
  repeat: () => lucide('repeat'),
  shuffle: () => lucide('shuffle'),
  add: () => lucide('plus'),
  remove: () => lucide('minus'),
};

// up / over / down (and "on" for toggles) states for every icon.
const PANEL_ICONS = new Set(['sort', 'repeat', 'shuffle', 'add', 'remove']);
for (const [name, body] of Object.entries(ICONS)) {
  const bg = PANEL_ICONS.has(name) ? C.panel : C.bar;
  for (const [state, color] of [['', C.icon], ['_over', C.iconHi], ['_down', C.accent], ['_on', C.accent]]) {
    render(strokeIcon(body(color), color, bg), `${name}${state}.png`);
  }
}

// Orange play/pause pill.
const PILL = { w: 52, h: 28 };
function pill(fill, glyph) {
  const g = glyph === 'play'
    ? `<path d="M22 8.5v11l9.5-5.5z" fill="#111" stroke="#111" stroke-width="1.4" stroke-linejoin="round"/>`
    : `<rect x="20" y="8.5" width="4" height="11" rx="1" fill="#111"/><rect x="28" y="8.5" width="4" height="11" rx="1" fill="#111"/>`;
  return svgDoc(PILL.w, PILL.h, `${onBg(C.bar, PILL.w, PILL.h)}<rect width="${PILL.w}" height="${PILL.h}" rx="${PILL.h / 2}" fill="${fill}"/>${g}`);
}
for (const g of ['play', 'pause']) {
  render(pill(C.accent, g), `${g}.png`);
  render(pill(C.accentHi, g), `${g}_over.png`);
  render(pill(C.accentLo, g), `${g}_down.png`);
}

// The VLC cone, flat and modernised like the redesign's logo.
function cone(size, bg, body = C.accent, base = '#7a7a7a') {
  const s = size / 100;
  return svgDoc(size, size, `${onBg(bg, size, size)}<g transform="scale(${s})">
    <path d="M42 8h16l5.5 20h-27z" fill="${body}"/>
    <path d="M34.5 36h31l6 22h-43z" fill="${body}"/>
    <path d="M26.5 66h47l5.2 19H21.3z" fill="${body}"/>
    <rect x="10" y="86" width="80" height="9" rx="3" fill="${base}"/>
  </g>`);
}
render(cone(20, C.bar), 'logo.png');
render(cone(120, C.video, '#3a3a3a', '#2a2a2a'), 'idle_cone.png');

// Thick seek bar: one frame per position, stacked vertically for SliderBackground.
// Corners are square here; rounded caps are overlaid as separate images so they
// don't get stretched when the window is resized.
function seekStrip(width, file) {
  const inset = 6, span = width - inset * 2;
  let body = '';
  for (let i = 0; i < SEEK_FRAMES; i++) {
    const y = i * SEEK.h;
    const fx = Math.round(inset + span * (i / (SEEK_FRAMES - 1)));
    body += `<rect y="${y}" width="${width}" height="${SEEK.h}" fill="${C.track}"/>`;
    body += `<rect y="${y}" width="${fx}" height="${SEEK.h}" fill="${C.fill}"/>`;
  }
  render(svgDoc(width, SEEK.h * SEEK_FRAMES, body), file);
  return `(${inset},${SEEK.h / 2}),(${width - inset},${SEEK.h / 2})`;
}
const seekPoints = seekStrip(SEEK_W, 'seek_bg.png');

// Hidden-UI mode keeps only a thin progress line (redesign: "option to completely hide the UI").
const THIN = { h: 14, line: 4 };
{
  const w = W - 32;
  let body = '';
  for (let i = 0; i < SEEK_FRAMES; i++) {
    const y = i * THIN.h, ly = y + (THIN.h - THIN.line) / 2;
    body += `<rect y="${y}" width="${w}" height="${THIN.h}" fill="${C.video}"/>`;
    body += `<rect y="${ly}" width="${w}" height="${THIN.line}" rx="2" fill="#262626"/>`;
    body += `<rect y="${ly}" width="${Math.max(4, Math.round((w * i) / (SEEK_FRAMES - 1)))}" height="${THIN.line}" rx="2" fill="${C.accent}"/>`;
  }
  render(svgDoc(w, THIN.h * SEEK_FRAMES, body), 'thin_bg.png');
  render(svgDoc(2, THIN.h, ''), 'thin_handle.png');
  const chevron = (c) => svgDoc(32, THIN.h, `<rect width="32" height="${THIN.h}" fill="${C.video}"/><path d="M11 9l5-4 5 4" fill="none" stroke="${c}" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>`);
  render(chevron(C.icon), 'showui.png');
  render(chevron(C.iconHi), 'showui_over.png');
  render(chevron(C.accent), 'showui_down.png');
}
const fscSeekPoints = seekStrip(FSC_SEEK_W, 'seek_bg_fsc.png');

function seekCap(side, bg) {
  const r = SEEK_R;
  const d = side === 'l'
    ? `M0 0H${r}A${r} ${r} 0 0 0 0 ${r}ZM0 ${SEEK.h}H${r}A${r} ${r} 0 0 1 0 ${SEEK.h - r}Z`
    : `M${r} 0H0A${r} ${r} 0 0 1 ${r} ${r}ZM${r} ${SEEK.h}H0A${r} ${r} 0 0 0 ${r} ${SEEK.h - r}Z`;
  return svgDoc(r, SEEK.h, `<path d="${d}" fill="${bg}"/>`);
}
render(seekCap('l', C.bar), 'seek_cap_l.png');
render(seekCap('r', C.bar), 'seek_cap_r.png');

const handle = (fill) => svgDoc(10, 26, `<rect x="1" y="1" width="8" height="24" rx="4" fill="${fill}"/>`);
render(handle(C.accent), 'seek_handle.png');
render(handle(C.accentHi), 'seek_handle_over.png');
render(handle(C.accentHi), 'seek_handle_down.png');

// Cone-shaped volume slider (the redesign turns VLC's volume wedge into a full cone):
// narrow on the left, tall on the right, green-to-orange as it fills.
{
  const { w, h, frames } = VOL;
  const shape = `M2 ${h / 2 - 3}L${w - 3} 1Q${w} 1 ${w} 4V${h - 4}Q${w} ${h - 1} ${w - 3} ${h - 1}L2 ${h / 2 + 3}Q0 ${h / 2 + 2.5} 0 ${h / 2}Q0 ${h / 2 - 2.5} 2 ${h / 2 - 3}Z`;
  let body = `<defs><linearGradient id="g" x1="0" x2="${w}" gradientUnits="userSpaceOnUse">
    <stop offset="0" stop-color="${C.volLo}"/><stop offset="0.55" stop-color="#9a8a00"/><stop offset="1" stop-color="${C.volHi}"/></linearGradient>`;
  for (let i = 0; i < frames; i++) body += `<clipPath id="c${i}"><rect width="${(w * i) / (frames - 1)}" height="${h}"/></clipPath>`;
  body += '</defs>';
  for (let i = 0; i < frames; i++) {
    body += `<g transform="translate(0 ${i * h})">${onBg(C.bar, w, h)}<path d="${shape}" fill="${C.track}"/><path d="${shape}" fill="url(#g)" clip-path="url(#c${i})"/></g>`;
  }
  render(svgDoc(w, h * frames, body), 'volume_bg.png');
  render(svgDoc(2, h, ''), 'volume_handle.png');
}

// Solid fills and small chrome pieces.
const solid = (color, w = 8, h = 8) => svgDoc(w, h, `<rect width="${w}" height="${h}" fill="${color}"/>`);
render(solid(C.bar), 'bar.png');
render(solid(C.video), 'video_bg.png');
render(solid(C.panel), 'panel.png');
render(solid(C.divider, 8, 1), 'divider.png');
render(svgDoc(12, 12, [[9, 9], [5, 9], [9, 5]].map(([x, y]) => `<circle cx="${x}" cy="${y}" r="1.1" fill="#4a4a4a"/>`).join('')), 'grip.png');
render(svgDoc(FSC.w, FSC.h, `<rect width="${FSC.w}" height="${FSC.h}" rx="18" fill="${C.bar}"/>`), 'fsc_bg.png');
render(svgDoc(6, 60, `<rect x="1" width="4" height="60" rx="2" fill="#3a3a3a"/>`), 'scroll.png');

// Rounded window corners: the bar ends are separate pieces whose transparent
// outside-of-arc pixels shape the window (skins2 cuts transparent pixels out).
function corner(h, top, left, fill) {
  const d = top
    ? (left ? `M0 ${R}A${R} ${R} 0 0 1 ${R} 0H${R}V${h}H0Z` : `M0 0A${R} ${R} 0 0 1 ${R} ${R}V${h}H0Z`)
    : (left ? `M0 0H${R}V${h}A${R} ${R} 0 0 1 0 ${h - R}Z` : `M0 0H${R}V${h - R}A${R} ${R} 0 0 1 0 ${h}Z`);
  return svgDoc(R, h, `<path d="${d}" fill="${fill}" shape-rendering="crispEdges"/>`);
}
render(corner(TOP, true, true, C.bar), 'corner_tl.png');
render(corner(TOP, true, false, C.bar), 'corner_tr.png');
render(corner(BOT, false, true, C.bar), 'corner_bl.png');
render(corner(BOT, false, false, C.bar), 'corner_br.png');
render(corner(14, false, true, C.video), 'corner_thin_bl.png');
render(corner(14, false, false, C.video), 'corner_thin_br.png');
render(solid('#191919', 6, 6), 'hotspot.png');
render(svgDoc(1, 1, ''), 'blank.png');

// Dark slide-in menus (replacing native Windows popups, which can't be themed).
// Each row is a pre-rendered button so the text is crisp and hover works.
const ROW_H = 30, SEP_H = 11;
const esc = (t) => t.replace(/&/g, '&amp;').replace(/</g, '&lt;');
function menuRow(w, label, hint, state) {
  const bg = state === 'up' ? C.panel : state === 'over' ? '#272727' : '#2d1a08';
  const fg = state === 'down' ? C.accentHi : state === 'over' ? '#f0f0f0' : '#c8c8c8';
  return svgDoc(w, ROW_H, `<rect width="${w}" height="${ROW_H}" fill="${C.panel}"/>` +
    `<rect x="6" y="1" width="${w - 12}" height="${ROW_H - 2}" rx="8" fill="${bg}"/>` +
    `<text x="18" y="19.5" font-family="Inter" font-size="13" fill="${fg}">${esc(label)}</text>` +
    (hint ? `<text x="${w - 18}" y="19.5" font-family="Inter" font-size="12" fill="#6e6e6e" text-anchor="end">${esc(hint)}</text>` : ''));
}
function menuTitle(w, label) {
  return svgDoc(w, 44, `<rect width="${w}" height="44" fill="${C.panel}"/><text x="18" y="27" font-family="Inter" font-size="14" font-weight="600" fill="${C.title}">${esc(label)}</text>`);
}
const keyOf = (name) => KEYS[name] || '';
const MENUS = {
  menu: { w: MENU_W, title: 'Menu', items: [
    ['Open File...', 'Ctrl+O', 'dialogs.fileSimple()'],
    ['Open Folder...', 'Ctrl+F', 'dialogs.directory()'],
    ['Open Disc...', 'Ctrl+D', 'dialogs.disc()'],
    ['Network Stream...', 'Ctrl+N', 'dialogs.net()'],
    ['Advanced Open...', '', 'dialogs.file()'],
    null,
    ['Load Playlist...', '', 'playlist.load()'],
    ['Save Playlist to File...', 'Ctrl+Y', 'playlist.save()'],
    null,
    ['Convert / Stream...', 'Ctrl+S', 'dialogs.streamingWizard()'],
    ['Media Information', 'Ctrl+I', 'dialogs.fileInfo()'],
    ['Settings', 'Ctrl+P', 'dialogs.prefs()'],
    ['Change Skin...', '', 'dialogs.changeSkin()'],
    null,
    ['Quit', keyOf('quit'), 'vlc.quit()'],
  ] },
  gear: { w: GEAR_W, title: 'Player settings', items: [
    ['Hide Controls', '', 'barsOff.show();barsVid.show()'],
    null,
    ['Faster', keyOf('faster'), 'vlc.faster()'],
    ['Slower', keyOf('slower'), 'vlc.slower()'],
    null,
    ['Video: zoom, aspect, crop...', '', 'dialogs.videoPopup()'],
    ['Audio: track, stereo...', keyOf('audio-track'), 'dialogs.audioPopup()'],
    ['Subtitles &amp; more...', keyOf('subtitle-track'), 'dialogs.popup()'],
    ['Chapters &amp; bookmarks...', '', 'dialogs.miscPopup()'],
    ['Equalizer on', '', 'equalizer.enable()'],
    ['Equalizer off', '', 'equalizer.disable()'],
    null,
    ['Take Snapshot', keyOf('snapshot'), 'vlc.snapshot()'],
    ['Record', keyOf('record'), 'vlc.toggleRecord()'],
    ['Always on Top', '', 'vlc.onTop()'],
  ] },
};
for (const [name, m] of Object.entries(MENUS)) {
  render(menuTitle(m.w, m.title), `mt_${name}.png`);
  m.items.forEach((it, i) => {
    if (!it) return;
    const label = it[0].replace(/&amp;/g, '&');
    for (const st of ['up', 'over', 'down']) render(menuRow(m.w, label, it[1], st), `mr_${name}${i}${st === 'up' ? '' : '_' + st}.png`);
  });
}

fs.copyFileSync(path.join(ROOT, 'src', 'fonts', 'Inter.ttf'), path.join(OUT, 'Inter.ttf'));
fs.copyFileSync(path.join(ROOT, 'src', 'fonts', 'OFL.txt'), path.join(OUT, 'Inter-OFL.txt'));

// ---------------------------------------------------------------- theme.xml
const pngs = fs.readdirSync(OUT).filter((f) => f.endsWith('.png'));
const bitmaps = pngs.map((f) => `  <Bitmap id="${f.slice(0, -4)}" file="${f}" alphacolor="#FF00FF"/>`).join('\n');

const btn = (id, icon, x, y, action, tip, extra = '') =>
  `<Button id="${id}" x="${x}" y="${y}" up="${icon}" over="${icon}_over" down="${icon}_down" action="${action}" tooltiptext="${tip}" ${extra}/>`;

// Bottom control row. `sfx` keeps ids unique per layout, `seekW`/`points` differ for the FSC.
function controls(sfx, seekW, points, seekBg, { queueAction, gearAction, lastButton }) {
  return `
      <Panel id="left${sfx}" x="0" y="0" width="${SEEK.x}" height="${BOT}" lefttop="lefttop" rightbottom="lefttop">
        ${btn(`prev${sfx}`, 'prev', 16, 14, 'playlist.previous()', `Previous${key('prev')}`)}
        <Checkbox id="play${sfx}" x="44" y="10" state="vlc.isPlaying" up1="play" over1="play_over" down1="play_down" up2="pause" over2="pause_over" down2="pause_down" action1="vlc.play()" action2="vlc.pause()" tooltiptext1="Play${key('play-pause')}" tooltiptext2="Pause${key('play-pause')}"/>
        ${btn(`next${sfx}`, 'next', 104, 14, 'playlist.next()', `Next${key('next')}`)}
        <Text id="time${sfx}" x="132" y="16" width="76" text="$T" font="inter13" color="${C.text}" alignment="center" scrolling="none" visible="not vlc.isStopped"/>
      </Panel>
      <Slider id="seek${sfx}" x="${SEEK.x}" y="${SEEK.y}" points="${points}" thickness="14" value="time" up="seek_handle" over="seek_handle_over" down="seek_handle_down" tooltiptext="$T / $D" lefttop="lefttop" rightbottom="righttop" visible="vlc.isSeekable">
        <SliderBackground image="${seekBg}" nbvert="${SEEK_FRAMES}"/>
      </Slider>
      <Image id="seekIdle${sfx}" x="${SEEK.x}" y="${SEEK.y}" width="${seekW}" height="${SEEK.h}" image="video_bg" resize="scale" lefttop="lefttop" rightbottom="righttop" visible="not vlc.isSeekable"/>
      <Image id="capL${sfx}" x="${SEEK.x}" y="${SEEK.y}" image="seek_cap_l"/>
      <Image id="capR${sfx}" x="${SEEK.x + seekW - SEEK_R}" y="${SEEK.y}" image="seek_cap_r" lefttop="righttop" rightbottom="righttop"/>
      <Panel id="right${sfx}" x="${SEEK.x + seekW + SEEK.rightGap}" y="0" width="${RIGHT_W}" height="${BOT}" lefttop="righttop" rightbottom="righttop">
        <Text id="dur${sfx}" x="0" y="16" width="66" text="$D" font="inter13" color="${C.text}" alignment="center" scrolling="none" visible="not vlc.isStopped"/>
        <Checkbox id="mute${sfx}" x="74" y="14" state="vlc.isMute" up1="volume" over1="volume_over" down1="volume_down" up2="muted_on" over2="muted_over" down2="muted_down" action1="vlc.mute()" action2="vlc.mute()" tooltiptext1="Mute${key('vol-mute')}" tooltiptext2="Unmute${key('vol-mute')}"/>
        <Slider id="vol${sfx}" x="102" y="12" points="(1,12),(83,12)" thickness="12" value="volume" up="volume_handle" tooltiptext="Volume $V%">
          <SliderBackground image="volume_bg" nbvert="${VOL.frames}"/>
        </Slider>
        ${btn(`audio${sfx}`, 'audio', 200, 14, 'dialogs.audioPopup()', `Audio track, stereo &amp; visualizations${KEYS['audio-track'] ? ` - ${KEYS['audio-track']} cycles tracks` : ''}`)}
        ${btn(`subs${sfx}`, 'subs', 234, 14, 'dialogs.popup()', `Subtitles: Subtitle &gt; Sub Track${KEYS['subtitle-track'] ? ` - ${KEYS['subtitle-track']} cycles` : ''}${KEYS['subtitle-toggle'] ? `, ${KEYS['subtitle-toggle']} on/off` : ''}`)}
        ${btn(`gear${sfx}`, 'gear', 268, 14, gearAction, 'Player settings')}
        ${lastButton ?? btn(`queue${sfx}`, 'queue', 316, 14, queueAction, 'Queue')}
      </Panel>`;
}

function topBar(sfx, moreAction) {
  return `
      <Image id="topbg${sfx}" x="${R}" y="0" width="${W - 2 * R}" height="${TOP}" image="bar" resize="scale" action="move" lefttop="lefttop" rightbottom="righttop"/>
      <Image id="ctl${sfx}" x="0" y="0" image="corner_tl" action="move"/>
      <Image id="ctr${sfx}" x="${W - R}" y="0" image="corner_tr" action="resizeE" lefttop="righttop" rightbottom="righttop"/>
      <Image id="topmax${sfx}" x="${80}" y="0" width="${W - 240}" height="${TOP}" image="bar" resize="scale" action="move" action2="Player.maximize()" lefttop="lefttop" rightbottom="righttop" visible="not Player.isMaximized"/>
      <Image id="topunmax${sfx}" x="${80}" y="0" width="${W - 240}" height="${TOP}" image="bar" resize="scale" action="move" action2="Player.unmaximize()" lefttop="lefttop" rightbottom="righttop" visible="Player.isMaximized"/>
      <Image id="logo${sfx}" x="12" y="8" image="logo" action="move" action2="dialogs.fileInfo()" help="VLC"/>
      ${btn(`more${sfx}`, 'more', 42, 8, moreAction, 'Menu')}
      <Text id="title${sfx}" x="120" y="10" width="${W - 300}" text="$N" font="inter13" color="${C.title}" alignment="center" scrolling="none" lefttop="lefttop" rightbottom="righttop" focus="false"/>
      <Panel id="win${sfx}" x="${W - 158}" y="0" width="158" height="${TOP}" lefttop="righttop" rightbottom="righttop">
        ${btn(`min${sfx}`, 'min', 8, 8, 'vlc.minimize()', 'Minimize')}
        ${btn(`max${sfx}`, 'max', 46, 8, 'Player.maximize()', 'Maximize', 'visible="not Player.isMaximized"')}
        ${btn(`unmax${sfx}`, 'restore', 46, 8, 'Player.unmaximize()', 'Restore', 'visible="Player.isMaximized"')}
        ${btn(`fs${sfx}`, 'fullscreen', 84, 8, 'vlc.fullscreen()', `Fullscreen${key('toggle-fullscreen')}`)}
        ${btn(`close${sfx}`, 'close', 124, 8, 'vlc.quit()', 'Close')}
      </Panel>`;
}

function menuPanel(name, x, anchorRight) {
  const m = MENUS[name];
  const ph = H - TOP - BOT;
  const anchor = anchorRight ? 'lefttop="righttop" rightbottom="rightbottom"' : 'lefttop="lefttop" rightbottom="leftbottom"';
  let y = 44, rows = '';
  m.items.forEach((it, i) => {
    if (!it) {
      rows += `\n        <Image id="ms_${name}${i}" x="16" y="${y + 5}" width="${m.w - 32}" height="1" image="divider" resize="scale"/>`;
      y += SEP_H;
      return;
    }
    const img = `mr_${name}${i}`;
    rows += `\n        <Button id="mi_${name}${i}" x="0" y="${y}" up="${img}" over="${img}_over" down="${img}_down" action="Player.setLayout(main);${it[2]}"/>`;
    y += ROW_H;
  });
  return `
      <Panel id="panel_${name}" x="${x}" y="${TOP}" width="${m.w}" height="${ph}" ${anchor}>
        <Image id="pbg_${name}" x="0" y="0" width="${m.w}" height="${ph}" image="panel" resize="scale" lefttop="lefttop" rightbottom="rightbottom"/>
        <Image id="pt_${name}" x="0" y="0" image="mt_${name}"/>${rows}
      </Panel>`;
}

// Bars can't overlay the video (it's a native child window), and every layout switch
// resizes the window to that layout's own size. So hiding the bars happens *inside*
// the main layout: an invisible 1px window "barsOff" acts as a boolean, and every
// control is gated on barsOff.isVisible. The helper toggles it via two hotspots.
const SHOWN = 'not barsOff.isVisible', HIDDEN = 'barsOff.isVisible';
function gate(xml, cond) {
  return xml.replace(/<(Image|Button|Checkbox|Slider|Text|Video)\b([^>]*?)(\/?)>/g, (m, tag, attrs, close) => {
    const v = attrs.match(/ visible="([^"]*)"/);
    attrs = v ? attrs.replace(v[0], ` visible="${v[1]} and ${cond}"`) : `${attrs} visible="${cond}"`;
    return `<${tag}${attrs}${close}>`;
  });
}

// side: null (plain), 'queue' / 'gear' (right panel) or 'menu' (left panel).
function playerLayout(id, side) {
  const sfx = { queue: '_q', gear: '_g', menu: '_mm' }[side] ?? '_m';
  const pw = { queue: QUEUE_W, gear: GEAR_W, menu: MENU_W }[side] ?? 0;
  const vx = side === 'menu' ? MENU_W : 0;
  const vw = W - pw;
  const ph = H - TOP - BOT;
  const queue = side === 'queue' ? `
      <Panel id="queue${sfx}" x="${W - QUEUE_W}" y="${TOP}" width="${QUEUE_W}" height="${ph}" lefttop="righttop" rightbottom="rightbottom">
        <Image id="qbg" x="0" y="0" width="${QUEUE_W}" height="${ph}" image="panel" resize="scale" lefttop="lefttop" rightbottom="rightbottom"/>
        <Text id="qtitle" x="16" y="14" width="90" text="Queue" font="inter13b" color="${C.title}" scrolling="none"/>
        ${btn('qsort', 'sort', 128, 12, 'playlist.sort()', 'Sort')}
        <Checkbox id="qrepeat" x="160" y="12" state="playlist.isLoop" up1="repeat" over1="repeat_over" down1="repeat_down" up2="repeat_on" over2="repeat_on" down2="repeat_down" action1="playlist.setLoop(true)" action2="playlist.setLoop(false)" tooltiptext1="Repeat all" tooltiptext2="Repeat all (on)"/>
        <Checkbox id="qshuffle" x="192" y="12" state="playlist.isRandom" up1="shuffle" over1="shuffle_over" down1="shuffle_down" up2="shuffle_on" over2="shuffle_on" down2="shuffle_down" action1="playlist.setRandom(true)" action2="playlist.setRandom(false)" tooltiptext1="Shuffle" tooltiptext2="Shuffle (on)"/>
        ${btn('qdel', 'remove', 228, 12, 'playlist.del()', 'Remove selected')}
        ${btn('qadd', 'add', 262, 12, 'playlist.add()', 'Add files')}
        <Image id="qdiv" x="12" y="44" width="${QUEUE_W - 24}" height="1" image="divider" resize="scale" lefttop="lefttop" rightbottom="righttop"/>
        <Playtree id="playtree" x="8" y="52" width="${QUEUE_W - 26}" height="${ph - 60}" font="inter13" flat="true" bgcolor1="${C.panel}" bgcolor2="${C.panel}" fgcolor="#c4c4c4" playcolor="${C.accent}" selcolor="${C.panelSel}" lefttop="lefttop" rightbottom="rightbottom">
          <Slider id="qscroll" x="${QUEUE_W - 16}" y="52" points="(3,${ph - 90}),(3,30)" up="scroll" down="scroll" over="scroll" lefttop="righttop" rightbottom="rightbottom"/>
        </Playtree>
      </Panel>` : side === 'gear' ? menuPanel('gear', W - GEAR_W, true) : side === 'menu' ? menuPanel('menu', 0, false) : '';
  const vAnchor = side === 'menu' ? 'lefttop="lefttop"' : 'lefttop="lefttop"';
  return `
    <Layout id="${id}" width="${W}" height="${H}" minwidth="640" minheight="${side ? 540 : 360}" maxwidth="9999" maxheight="9999">
      <Image id="vbg${sfx}" x="${vx}" y="${TOP}" width="${vw}" height="${ph}" image="video_bg" resize="scale" action="move" action2="vlc.fullscreen()" ${vAnchor} rightbottom="rightbottom"/>
      <Panel id="idle${sfx}" x="${vx}" y="${TOP}" width="${vw}" height="${ph}" ${vAnchor} rightbottom="rightbottom">
        <Image id="cone${sfx}" x="${vw / 2 - 60}" y="${ph / 2 - 60}" image="idle_cone" xkeepratio="true" ykeepratio="true" action="move" action2="dialogs.fileSimple()" help="Double-click to open a file"/>
      </Panel>
      <Video id="video${sfx}" x="${vx + EDGE}" y="${TOP}" width="${vw - 2 * EDGE}" height="${ph}" ${vAnchor} rightbottom="rightbottom" autoresize="true"/>
      ${topBar(sfx, side === 'menu' ? 'Player.setLayout(main)' : 'Player.setLayout(main_menu)')}${side ? '' : `
      <Image id="hot_hide" x="72" y="2" image="hotspot" action2="barsOff.show();barsVid.show()" visible="not vlc.isStopped"/>`}
      <Panel id="bottom${sfx}" x="0" y="${H - BOT}" width="${W}" height="${BOT}" lefttop="leftbottom" rightbottom="rightbottom">
        <Image id="botbg${sfx}" x="${R}" y="0" width="${W - 2 * R}" height="${BOT}" image="bar" resize="scale" action="move" lefttop="lefttop" rightbottom="righttop"/>
        <Image id="cbl${sfx}" x="0" y="0" image="corner_bl" action="move"/>
        <Image id="cbr${sfx}" x="${W - R}" y="0" image="corner_br" action="resizeSE" lefttop="righttop" rightbottom="righttop"/>
        ${controls(sfx, SEEK_W, seekPoints, 'seek_bg', {
          queueAction: side === 'queue' ? 'Player.setLayout(main)' : 'Player.setLayout(main_queue)',
          gearAction: side === 'gear' ? 'Player.setLayout(main)' : 'Player.setLayout(main_gear)',
        })}
        <Image id="edgeS${sfx}" x="${R}" y="${BOT - 4}" width="${W - 2 * R}" height="4" image="bar" resize="scale" action="resizeS" lefttop="lefttop" rightbottom="righttop"/>
        <Image id="grip${sfx}" x="${W - 22}" y="${BOT - 18}" image="grip" action="resizeSE" lefttop="righttop" rightbottom="righttop"/>
      </Panel>${queue}${side === 'queue' || side === 'gear' ? '' : `
      <Image id="edgeE${sfx}" x="${W - EDGE}" y="${TOP}" width="${EDGE}" height="${ph}" image="video_bg" resize="scale" action="resizeE" lefttop="righttop" rightbottom="rightbottom"/>`}
    </Layout>`;
}

// The default layout: the normal player, plus the bars-hidden variant of itself.
function mainLayout() {
  // autoresize off: otherwise the window grows each time the video is handed back.
  const xml = playerLayout('main', null).replace(/(<Video id="video_m"[^>]*?)autoresize="true"/, '$1autoresize="false"');
  const open = xml.indexOf('>', xml.indexOf('<Layout')) + 1;
  const close = xml.lastIndexOf('</Layout>');
  // Background/idle art stays visible in both states; everything else is gated.
  const body = xml.slice(open, close).replace(
    /(<Image id="vbg_m"[^>]*?) y="\d+"([^>]*?) height="\d+"/, '$1 y="0"$2 height="' + H + '"');
  const vh = H - THIN.h;
  const hidden = `
      <Video id="video_h" x="${EDGE}" y="0" width="${W - 2 * EDGE}" height="${vh}" lefttop="lefttop" rightbottom="rightbottom" autoresize="false" visible="barsVid.isVisible"/>
      <Panel id="thin_h" x="0" y="${vh}" width="${W}" height="${THIN.h}" lefttop="leftbottom" rightbottom="rightbottom">
        <Image id="thinbg_h" x="${R}" y="0" width="${W - 2 * R}" height="${THIN.h}" image="video_bg" resize="scale" action="move" lefttop="lefttop" rightbottom="righttop"/>
        <Image id="tbl_h" x="0" y="0" image="corner_thin_bl" action="move"/>
        <Image id="tbr_h" x="${W - R}" y="0" image="corner_thin_br" action="resizeSE" lefttop="righttop" rightbottom="righttop"/>
        <Slider id="thin_seek_h" x="${R}" y="0" points="(0,${THIN.h / 2}),(${W - 32 - R},${THIN.h / 2})" thickness="8" value="time" up="thin_handle" tooltiptext="$T / $D" lefttop="lefttop" rightbottom="righttop">
          <SliderBackground image="thin_bg" nbvert="${SEEK_FRAMES}"/>
        </Slider>
        <Button id="showui_h" x="${W - 32 - R / 2}" y="0" up="showui" over="showui_over" down="showui_down" action="barsVid.hide();barsOff.hide()" tooltiptext="Show controls" lefttop="righttop" rightbottom="righttop"/>
        <Image id="hot_show" x="2" y="4" image="hotspot" action2="barsVid.hide();barsOff.hide()"/>
        <Image id="edgeS_h" x="${R}" y="${THIN.h - 3}" width="${W - 2 * R - 32}" height="3" image="video_bg" resize="scale" action="resizeS" lefttop="lefttop" rightbottom="righttop"/>
      </Panel>
      <Image id="hot_noop" x="72" y="2" width="6" height="6" image="video_bg" resize="scale" action2="none"/>
      <Image id="edgeE_h" x="${W - EDGE}" y="0" width="${EDGE}" height="${vh}" image="video_bg" resize="scale" action="resizeE" lefttop="righttop" rightbottom="rightbottom"/>`;
  const keep = /<Image id="vbg_m"[^>]*\/>|<Panel id="idle_m"[\s\S]*?<\/Panel>/g;
  const always = (body.match(keep) || []).join('\n      ');
  const rest = body.replace(keep, '');
  return xml.slice(0, open) + '\n      ' + always + gate(rest, SHOWN) + gate(hidden, HIDDEN) + '\n    ' + xml.slice(close);
}

const theme = `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE Theme PUBLIC "-//VideoLAN//DTD VLC Skins V2.0//EN" "skin.dtd">
<!--
  VLC Redesign skin, built from the VLC redesign by Juxtopposed
  (https://youtu.be/ERFeOgqk324, Figma file licensed CC BY 4.0).
  Generated by src/build.mjs; edit that file instead of this one.
-->
<Theme version="2.0" magnet="10">
  <ThemeInfo name="VLC Redesign" author="Design: Juxtopposed / Skin: Sayandeep1013" webpage="https://www.figma.com/community/file/1691465999134370980"/>

  <Font id="inter13" file="Inter.ttf" size="13"/>
  <Font id="inter13b" file="Inter.ttf" size="14"/>
${bitmaps}

  <!-- skins2 activates the last declared layout, so "main" goes last. -->
  <Window id="Player" x="120" y="80" dragdrop="true" playondrop="true">${playerLayout('main_queue', 'queue')}${playerLayout('main_gear', 'gear')}${playerLayout('main_menu', 'menu')}${mainLayout()}
  </Window>

  <!-- Invisible 1px window used purely as an on/off flag for "bars hidden". -->
  <Window id="barsOff" x="0" y="0" visible="false" dragdrop="false">
    <Layout id="barsOffL" width="1" height="1">
      <Image id="barsOffImg" x="0" y="0" image="blank"/>
    </Layout>
  </Window>

  <!-- Second flag so the video is handed over in order: the bars-area video releases
       it before the full-size one claims it (and the reverse when showing). -->
  <Window id="barsVid" x="0" y="0" visible="false" dragdrop="false">
    <Layout id="barsVidL" width="1" height="1">
      <Image id="barsVidImg" x="0" y="0" image="blank"/>
    </Layout>
  </Window>

  <!-- Floating controller: shows on mouse move in fullscreen, fades out on its own. -->
  <Window id="fullscreenController">
    <Layout id="fsc" width="${FSC.w}" height="${FSC.h}">
      <Image id="fscbg" x="0" y="0" image="fsc_bg" action="none"/>
      <Text id="fsctitle" x="20" y="14" width="${FSC.w - 40}" text="$N" font="inter13" color="${C.title}" scrolling="none"/>
      <Panel id="fscrow" x="0" y="${FSC.h - BOT - 4}" width="${FSC.w}" height="${BOT}">
        ${controls('_fsc', FSC_SEEK_W, fscSeekPoints, 'seek_bg_fsc', { gearAction: 'dialogs.videoPopup()', lastButton: btn('unfs_fsc', 'unfullscreen', 316, 14, 'vlc.fullscreen()', `Exit fullscreen${key('leave-fullscreen')}`) })}
      </Panel>
    </Layout>
  </Window>
</Theme>
`;
fs.writeFileSync(path.join(OUT, 'theme.xml'), theme);

// ---------------------------------------------------------------- package
const files = {};
for (const f of fs.readdirSync(OUT)) files[`VLC-Redesign/${f}`] = new Uint8Array(fs.readFileSync(path.join(OUT, f)));
fs.writeFileSync(path.join(DIST, 'VLC-Redesign.vlt'), zipSync(files, { level: 9 }));
console.log(`built ${Object.keys(files).length} files -> dist/VLC-Redesign.vlt (seek ${SEEK_W}px, fsc seek ${FSC_SEEK_W}px)`);

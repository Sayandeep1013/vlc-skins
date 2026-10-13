# VLC Skins

VLC skins and VLC look-and-feel experiments, including **VLC Redesign**, a standalone player built on VLC's engine from Juxtopposed's redesign.

## VLC Redesign (player)

<p align="center">
  <img src="vlc-redesign/screenshots/hero.jpg" alt="VLC Redesign playing a film, with the title bar and controls visible">
</p>

A Windows media player that brings [Juxtopposed's VLC redesign](https://youtu.be/ERFeOgqk324) to life on top of your installed VLC. It isn't a skin: it's a real player built on libVLC, so it plays everything VLC plays and uses your VLC settings and hotkeys.

- **Bars that get out of the way:** they slide in when you move the mouse and slide away while you watch; fullscreen gets a floating controller that fades out.
- **Seek previews:** hover or drag the seek bar to see the frame, time and chapter.
- **VLC's Tools:** Effects and Filters (equalizer, audio and video effects, applied live), media and codec information, statistics and messages.
- **Music view** with cover art and a waveform seek bar, plus dark menus, a player-settings sidebar, a queue and Explorer *Open with*.

**[Download the latest release](https://github.com/Sayandeep1013/vlc-skins/releases/latest)** (Windows 10/11, needs [VLC 3 64-bit](https://www.videolan.org/vlc/) installed) · **[Features, FAQ, build and credits](vlc-redesign/README.md)**

VLC Redesign is an independent project, not affiliated with VideoLAN. "VLC" and the cone are VideoLAN trademarks.

The earlier VLC Redesign *skin* (`vlc-redesign/dist/VLC-Redesign.vlt`) is still included; see the [project README](vlc-redesign/README.md#the-skin-earlier-version).

## Included Skins

### VeLoCity Dark Mist

Source project: `VeLoCity-Skin-for-VLC`

Tested skin file:

```text
VeLoCity-Skin-for-VLC/extracted-dark-mist/VeLoCity - Dark Mist/VeLoCity-Dark-Mist.vlt
```

Installed test copy:

```text
D:\vlc\skins\VeLoCity-Dark-Mist.vlt
```

### Materia

Source project: `materia-vlc`

Packaged skin file:

```text
materia-vlc/dist/Materia.vlt
```

Installed test copy:

```text
D:\vlc\skins\Materia.vlt
```

## VLC Install

1. Copy a `.vlt` file into VLC's `skins` folder.
2. Open VLC.
3. Go to `Tools -> Preferences -> Interface`.
4. Select `Use custom skin`.
5. Choose the `.vlt` file.
6. Save and restart VLC.

## Previews

Official preview images from each upstream skin project:

| VeLoCity - Dark Mist | Materia |
|---|---|
| ![VeLoCity Dark Mist preview](screenshots/velocity-dark-mist-preview.png) | ![Materia preview](screenshots/materia-preview.png) |

## Upstream Projects

- VeLoCity Skin for VLC: https://github.com/dmtiir/VeLoCity-Skin-for-VLC
- Materia VLC: https://github.com/PapirusDevelopmentTeam/materia-vlc

Respect the upstream licenses when redistributing or modifying these skins.

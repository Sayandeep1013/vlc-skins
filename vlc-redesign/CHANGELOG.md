# Changelog

All notable changes to VLC Redesign. Downloads are on the [releases page](https://github.com/Sayandeep1013/vlc-skins/releases).

## [1.2.0] - 2026-10-13

A redesigned Effects and Filters panel.

### Changed
- **Effects update live while you drag.** Sliders used to apply only after you let go, so free-angle rotate and others lagged. Settings VLC can change on the fly (rotate angle, picture adjustments, most filter settings) now update on the next frame. The rest rebuild the filter chain continuously as you drag.
- **New panel design:**
  - Each effect is a card with a one-line summary of its settings and an on/off switch.
  - Choices are chips (rotate and flip, crop, zoom, aspect ratio, stereo mode, deinterlace).
  - Color and style filters are a tile grid; settings for the tiles you turn on appear below it.
  - Sliders fill from their default and reset on double-click.
  - Overlays have a 3×3 position picker.
  - The equalizer has centered bands on a zero line.
  - Sync shows large readouts with ±50 ms nudges.
- Tabs show how many effects are active. The panel no longer jumps back to the top when you change something.
- Moving a setting on an effect that is off switches the effect on.

### Fixed
- The keyboard shortcuts list (F1) showed player settings on Ctrl+E. It now lists the VLC Tools shortcuts, with player settings on Ctrl+Shift+E.

[1.2.0]: https://github.com/Sayandeep1013/vlc-skins/releases/tag/v1.2.0

## [1.1.0] - 2026-10-13

VLC's Tools, with the same names, ranges and defaults as VLC.

### Added
- **Effects and Filters** (<kbd>Ctrl</kbd>+<kbd>E</kbd>), applied live the way VLC's own window does:
  - Audio: 10-band equalizer with presets, preamp and 2-pass; compressor; spatializer; stereo widener; pitch shift; stereo mode.
  - Video: image adjust, sharpen, banding removal, film grain, crop (ratios and pixels), zoom, aspect ratio, rotate and flip, color filters, text and logo overlay, advanced filters, deinterlace.
  - Synchronization: audio and subtitle delay.
  - Effects are remembered between sessions; crop, zoom and overlays reset each session, as in VLC.
- **Media Information** (<kbd>Ctrl</kbd>+<kbd>I</kbd>) with editable tags and *Save metadata*, **Codec Information** (<kbd>Ctrl</kbd>+<kbd>J</kbd>) and live **Statistics**.
- **Messages** (<kbd>Ctrl</kbd>+<kbd>M</kbd>) with verbosity, Copy and Clear, and a **Plugins** list.
- **Preferences** (<kbd>Ctrl</kbd>+<kbd>P</kbd>) opens classic VLC's preferences.
- **Jump to time** (<kbd>Ctrl</kbd>+<kbd>T</kbd>) and VLC's video/audio menu items: zoom, crop, deinterlace, always on top, stereo mode.

### Changed
- <kbd>Ctrl</kbd>+<kbd>E</kbd> now opens Effects and Filters, as in VLC. Player settings moved to <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>E</kbd>.
- Quicker close: the window hides at once.

### Fixed
- Text boxes no longer double their padding.

## [1.0.1] - 2026-10-13

### Added
- Release exes are now built from source by GitHub Actions.
- A friendly "VLC isn't installed" dialog with a link to videolan.org.

### Changed
- Smaller self-contained exe (English-only runtime resources).

## 1.0.0

First release.

- The redesign's look: dark menus, rounded corners, the orange play/pause pill and a cone volume control.
- Auto-hiding bars that slide in when you move the mouse and slide away while you watch.
- Fullscreen floating controller that fades out.
- YouTube-style seek-bar previews with time and chapter.
- Music view with cover art and a waveform seek bar.
- Player-settings sidebar and a queue.
- Your VLC hotkeys and settings from `vlcrc`, including VLC's arrow-key behaviour.
- Explorer *Open with* registration.

[1.1.0]: https://github.com/Sayandeep1013/vlc-skins/releases/tag/v1.1.0
[1.0.1]: https://github.com/Sayandeep1013/vlc-skins/releases/tag/v1.0.1

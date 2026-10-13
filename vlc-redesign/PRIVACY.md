# Privacy policy: VLC Redesign

_Last updated: 13 October 2026_

VLC Redesign does not collect, store or send any personal data. It has no accounts, analytics, telemetry, ads or crash reporting that leaves your PC.

**What stays on your PC**
- Window position, volume and the waveform setting are saved in `%APPDATA%\VLC-Redesign\player.json`.
- Errors are written to `%TEMP%\vlc-redesign-player.log`.
- Your VLC settings (`vlcrc`) are read, never written.
- Seek-bar preview frames and waveforms are kept in memory only. To draw a waveform, the audio is first decoded into a temporary file in `%TEMP%`, which is deleted right away.

**When it uses the network**
- When you open a network stream (a URL) yourself.
- When your own VLC settings allow VLC to look up metadata or cover art online. VLC Redesign uses your installed VLC engine and its settings; this is VLC's behaviour and is off by default in VLC.

Nothing is sent anywhere else.

Questions: open an issue at https://github.com/Sayandeep1013/vlc-skins/issues.

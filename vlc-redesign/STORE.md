# Microsoft Store submission

How to get VLC Redesign into the Microsoft Store. The Store signs the package, so Store installs have no SmartScreen warning and update automatically.

## 1. Before you start: the name

"VLC", "VLC media player" and the cone icon are registered trademarks of VideoLAN. Store review checks names and icons against trademarks, so a listing called "VLC Redesign" with the cone could be rejected, or later taken down if VideoLAN complains. Either:

- pick a name of your own and say what it is in the description, e.g. *"Cone Player: a redesigned player for VLC"*, ideally with your own icon; or
- ask VideoLAN for permission first (https://www.videolan.org/contact.html).

The display name is a parameter (`-DisplayName` in `packaging/msix/build-msix.ps1`, or the `STORE_DISPLAY_NAME` variable in GitHub Actions), so changing it doesn't need code changes. The icon is drawn in `player/tools/gen-assets.mjs`.

## 2. Partner Center

1. Create a developer account at https://partner.microsoft.com/dashboard/registration/developer (individual accounts don't need a company).
2. Apps and games → **New product** → **MSIX or PWA app**. Reserve the name.
3. Product management → **Product identity**. Copy these values:
   - `Package/Identity/Name` → `STORE_IDENTITY_NAME`
   - `Package/Identity/Publisher` → `STORE_PUBLISHER` (starts with `CN=`)
   - `Package/Properties/PublisherDisplayName` → `STORE_PUBLISHER_DISPLAY_NAME`

   Put them in GitHub → Settings → Secrets and variables → Actions → **Variables**, along with `STORE_DISPLAY_NAME`.

## 3. Build the package

Push a tag (`v1.0.1`). The release workflow attaches `VLC-Redesign-<version>.0-x64.msix`, built with your Store identity.

Or build it locally:

```powershell
./packaging/msix/build-msix.ps1 -Version 1.0.1.0 -IdentityName "<Name>" -Publisher "CN=<...>" -PublisherDisplayName "<Publisher>" -DisplayName "<Store name>"
# -> build/msix/VLC-Redesign-1.0.1.0-x64.msix
```

Test without installing anything signed (needs Windows Developer Mode): `./packaging/msix/build-msix.ps1 -Register`. Remove the test with `Get-AppxPackage *VLCRedesign* | Remove-AppxPackage`.

Store versions must increase with every submission, and the last number must be `0` (e.g. `1.0.1.0`).

## 4. Submission

- **Packages:** upload the `.msix`.
- **Properties:**
  - Category: Music & video (or Photo & video).
  - Privacy policy URL: https://github.com/Sayandeep1013/vlc-skins/blob/main/vlc-redesign/PRIVACY.md
  - Under *Product declarations*, mention that the app requires VLC media player.
- **Age ratings:** complete the questionnaire. It's a media player with no user-generated content of its own.
- **Restricted capability `runFullTrust`:** required for desktop apps. Justification: *"Desktop (WPF) media player; uses the user's installed VLC media player engine (libVLC) for playback."*
- **Store listing:** use the text below, with screenshots from `screenshots/` (at least one, 1366×768 or larger).
- **Notes for certification:** *"Requires VLC media player 3 (64-bit) to be installed: https://www.videolan.org/vlc/. Without it the app shows a message explaining that. Test with any video file."*

## Store listing text

**Short description**
A clean, modern player for your VLC: auto-hiding controls, seek previews, and a music view.

**Description**
A redesigned media player, based on Juxtopposed's VLC redesign, that runs on the VLC engine already installed on your PC. It plays everything VLC plays and uses your VLC settings and keyboard shortcuts.

• Controls that slide away while you watch and come back when you move the mouse
• Seek bar previews: see the frame before you jump
• A floating fullscreen controller that fades out
• Music view with cover art and a waveform seek bar
• Dark menus, a player-settings sidebar (speed, subtitles, audio, video), and a queue
• VLC's tools: equalizer and audio effects, video effects (color, rotate, crop, zoom, deinterlace), media and codec information, messages
• Keyboard shortcuts from your VLC settings, shown as you press them
• Right-click → Open with for your video and audio files

Requires VLC media player 3 (64-bit), free from videolan.org.

Design by Juxtopposed (CC BY 4.0). An independent project, not affiliated with VideoLAN.

**Features (bullets)**
- Uses your installed VLC engine and settings
- Auto-hiding controls with smooth animations
- YouTube-style seek previews
- Fullscreen floating controller
- Music view with waveform seek bar
- Equalizer, compressor, spatializer, pitch and video effects from VLC
- Subtitle and audio track pickers, delays, speed
- Queue with repeat and shuffle

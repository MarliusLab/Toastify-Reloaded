## Linux

Toastify Reloaded **1.4.0 for Linux** is available as a stable release.

### x64

- `ToastifyReloaded-Linux-x64.AppImage`
- `toastify-reloaded_1.4.0_amd64.deb`
- `ToastifyReloaded-Linux-x64.tar.gz`

### ARM64

- `toastify-reloaded_1.4.0_arm64.deb`
- `ToastifyReloaded-Linux-arm64.tar.gz`

The Linux release includes MPRIS Spotify controls, X11 and Wayland global
shortcut backends, system tray support, IT/EN localization, settings
import/export, Compatibility Guard, updater, toast themes including Custom,
font/color controls, animations, positioning and multi-monitor support.

Release checksums are published in `SHA256SUMS.txt`.

The development branch adds clickable toast titles/artists. Both open the
displayed song in Spotify using its validated MPRIS track URI (never a title
search), with a canonical Spotify web link if the desktop handler fails.
Ads, local files and unavailable IDs only activate Spotify. Missing desktop
handlers are handled without crashing; user settings need no migration.
These changes are not included in the existing `v1.4.0-linux` release/tag.

See `docs/LINUX.md` and `docs/LINUX_PARITY.md`.

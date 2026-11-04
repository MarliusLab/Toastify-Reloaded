# Toastify Reloaded — Linux

Published stable version: **1.4.4**

Stable tag: **`v1.4.4-linux`**

[Download Linux v1.4.4](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.4.4-linux).

The Linux port is intentionally isolated from the stable Windows/WPF application and uses Linux-native backends where Windows APIs do not exist.

## Published 1.4.4

Published on **4 November 2026**, from source **`94e5cdb45086ecc9471c3739426ac9b369019eab`**, with 15 verified assets, including genuine x64 and ARM64 delta packages. The release is public and stable; earlier releases and existing tags remain unchanged.

Clicking the toast, title or artist requests **Lyrics Plus** through the fixed `spotify:app:lyrics-plus` route. Enter/Space on either text link performs the same action after the key and held modifiers are released. If that route fails, Toastify tries to activate Spotify and reports the fallback; route acceptance does not confirm that Lyrics Plus is installed or has lyrics for the current song. The Lyrics Plus path sends no play, pause, seek or track-selection command.

The configurable **Show Toast** shortcut defaults to `Ctrl+Alt+T` when an older settings file has no assignment. The first press displays a toast; another press while any toast is visible requests Lyrics Plus. This uses toast visibility, with no double-tap deadline. After all toasts close, the shortcut shows a toast again. Existing saved shortcuts and disabled assignments are preserved. Tray/test buttons continue to show a toast.

In 1.4.4, a visible toast remains actionable during entry and exit animations. An accepted request interrupts the fade, restores that same window and holds it open until navigation settles; its earlier exit deadline cannot cancel the action. It then resumes the configured exit. Explicitly closing the toast cancels an outstanding request. No saved display durations, animations or shortcut assignments are changed.

The global shortcut uses a dedicated Lyrics Plus entry point. X11 dispatches from the existing release binding, and Wayland dispatches from the portal's `Deactivated` signal; Linux adds no separate wait for Ctrl/Alt/Shift release. The portal does not supply physical modifier state, so held-Ctrl behavior on a physical Wayland desktop remains unverified and depends on the backend's release event.

**Alt+Shift+J** remains separate: it reads the current MPRIS track identifier at invocation time and requests its validated Spotify link, with safe Spotify activation fallbacks for missing/local identifiers. Spotify owns the link's effect; Toastify sends no synthetic input or explicit playback command. X11 registers this shortcut globally only while a toast is visible and restores its own saved assignments afterwards; native conflicts are reported when the backend exposes them. Wayland supports Alt+Shift+J while the toast has keyboard focus. If the saved Show Toast shortcut itself is Alt+Shift+J, its first press shows a toast and a visible-toast press takes the current-track action. Other approved global Show Toast bindings retain the two-press Lyrics Plus behavior.

Toastify does not reserve an additional global Wayland Alt+Shift+J binding. The desktop portal controls global permissions and can remap a preferred binding externally; Toastify cannot reliably infer that remapped physical chord from its saved shortcut text.

Downloads now report bytes, percentage when known and verification/application stages. Cancel is available before the updater handoff; the application waits for an accepted handoff before exiting. Spotify/Spicetify probes run independently of repair and downloads, resolve native installation locations, and use bounded process/network operations. All themes, media controls, settings and import/export behavior remain available.

## Architecture

- UI: **Avalonia 12.1.1**, including opt-in native Wayland backend
- Spotify control/metadata: **MPRIS**, accessed through `playerctl`
- X11 global custom hotkeys: **xbindkeys**
- Wayland global custom hotkeys: **XDG Global Shortcuts Portal** through D-Bus
- Spicetify / Lyrics Plus: existing `spicetify` CLI
- Settings: `~/.config/toastify-reloaded/settings.json`
- Autostart: `~/.config/autostart/io.github.Marlius92.ToastifyReloaded.desktop`

## Preserved desktop features

- Play / Pause, Previous / Next
- Seek ±10 seconds
- Spotify volume and mute controls
- Automatic MPRIS player detection/reconnection
- Track-change toast
- Clickable, keyboard-accessible Lyrics Plus navigation, with a separate current-track shortcut
- Adaptive and fixed toast geometry
- Album artwork
- Toastify icon fallback
- Optional progress bar
- Optional current time / duration
- Fade, Slide, Fade + Slide and None animation modes
- Independent Slide In / Slide Out direction and distance
- 13 built-in Toastify Reloaded theme presets plus Custom
- Custom toast palette
- Font family and independent title/artist/time sizes
- Light / Dark / Follow system application theme
- Position presets, monitor selection and custom X/Y margins
- X11 and Wayland global shortcuts
- Linux session diagnostics
- System tray controls
- Optional close-to-tray
- English / Italian runtime localization
- JSON settings import/export
- Spicetify Lyrics Plus enable/repair helpers
- Compatibility Guard with failed-version anti-loop behavior
- Package-aware update checks/download/application
- Linux session autostart
- x64 and ARM64 release packages

See `docs/LINUX_PARITY.md` for the Windows/Linux parity matrix.

## Requirements

The Debian package declares:

```text
playerctl
xbindkeys
xdg-utils
xdg-desktop-portal
libglib2.0-bin
libx11-6
libice6
libsm6
libfontconfig1
```

For AppImage/tar users, install the required host tools and desktop libraries using the package manager for the distribution.

The X11 global Show Toast and temporary current-track shortcuts require `gdbus`. The DEB packages explicitly declare `libglib2.0-bin`, which supplies this command. AppImage/tar users should install their distribution's GLib command-line tools and check `command -v gdbus` before using these bindings.

### Ubuntu / Debian

```bash
sudo apt install playerctl xbindkeys xdg-utils xdg-desktop-portal libglib2.0-bin libx11-6 libice6 libsm6 libfontconfig1
```

### Fedora

```bash
sudo dnf install playerctl xbindkeys xdg-utils xdg-desktop-portal libX11 libICE libSM fontconfig
```

### Arch Linux

```bash
sudo pacman -S playerctl xbindkeys xdg-utils xdg-desktop-portal libx11 libice libsm fontconfig
```

## Spotify / MPRIS

`playerctl -l` should list a player whose name starts with `spotify` (or a compatible MPRIS implementation such as `spotifyd`).

Test:

```bash
playerctl --player=spotify status
playerctl --player=spotify metadata title
```

## X11 vs Wayland

Toastify Reloaded selects the hotkey backend from the active session:

- **X11:** `xbindkeys`
- **Wayland:** `org.freedesktop.portal.GlobalShortcuts`

On Wayland, the desktop portal may present a confirmation/configuration dialog the first time Toastify Reloaded binds shortcuts. The portal backend must be provided by the desktop environment.

Avalonia's native Wayland backend is enabled when `WAYLAND_DISPLAY` is available. Set:

```bash
TOASTIFY_DISABLE_NATIVE_WAYLAND=1
```

to force the normal platform-detection/XWayland path.

## System tray

Tray integration uses Avalonia's Linux StatusNotifierItem/AppIndicator support. Availability therefore depends on the desktop environment; some GNOME configurations require an AppIndicator-compatible extension. Close-to-tray is optional and disabled by default.

## Compatibility Guard

When the installed Spotify version changes, Compatibility Guard can run the Spicetify recovery flow:

```bash
spicetify backup apply
```

with fallback:

```bash
spicetify restore backup apply
```

When supported by the Spicetify installation method, Toastify Reloaded can also attempt `spicetify upgrade` before repair. Failed repairs are recorded so the same unsupported Spotify version is not retried indefinitely.

## Settings and import/export

The normal settings file is:

```text
~/.config/toastify-reloaded/settings.json
```

The application can import/export JSON settings through Avalonia's storage provider.

## Update behavior in 1.4.4

Velopack **1.2.161** supplies native AppImage update packages for x64 and ARM64. A Velopack-managed AppImage uses its architecture-specific feed, verifies the downloaded package, and hands application/restart to the native helper. Existing unmanaged AppImage and DEB installations retain the package-aware transition path; tar installations retain the manual archive path. Normal settings stay outside the application package and are preserved.

Legacy AppImage/DEB restart helpers wait for the old process to exit before launching the replacement, preserving `--minimized`. A restart from the updater suppresses another automatic installation attempt for that process, while explicit Install remains available and saved preferences stay unchanged.

After a legacy AppImage replacement or a successful DEB transaction, installation remains successful even if the normal-user restart helper cannot start. Toastify reports the restart failure separately, keeps settings intact and asks you to close and reopen the app manually; it does not retry installation or use an elevated app launch. A native SDK handoff or a downloaded manual archive is not reported as a confirmed installation.

Version 1.4.2 established the **full-package baseline**. Version 1.4.4 publishes genuine **1.4.3 → 1.4.4** deltas for both architectures. A client needs a usable cached baseline to apply a delta; missing, corrupt or inapplicable deltas use the full-package fallback. Legacy AppImage, DEB and tar archives are not treated as native full-package baselines. Native reconstruction tests do not constitute an installed-client update/restart acceptance test.

The stable updater checks GitHub Releases and only accepts later **stable Linux** tags for stable installations.

Channel policy:

- stable → later stable only;
- preview/RC → later preview, RC or stable;
- stable will not auto-install a future preview or RC.

### Historical 1.4.1 package behavior

- **AppImage:** download, verify SHA-256 digest when GitHub exposes one, replace the running AppImage when possible, restart;
- **`.deb`:** download, verify SHA-256 when available, use the privileged package path when available; otherwise leave the downloaded package for manual installation;
- **tar.gz:** download the matching portable archive without overwriting an unknown custom installation.

## Published 1.4.4 release packages

### x64

```text
ToastifyReloaded-Linux-x64.AppImage
ToastifyReloaded-Linux-x64.tar.gz
toastify-reloaded_1.4.4_amd64.deb
ToastifyReloadedLinux-1.4.4-linux-x64-full.nupkg
ToastifyReloadedLinux-1.4.4-linux-x64-delta.nupkg
releases.linux-x64.json
RELEASES-linux-x64
```

### ARM64

```text
ToastifyReloaded-Linux-arm64.tar.gz
ToastifyReloaded-Linux-arm64.AppImage
toastify-reloaded_1.4.4_arm64.deb
ToastifyReloadedLinux-1.4.4-linux-arm64-full.nupkg
ToastifyReloadedLinux-1.4.4-linux-arm64-delta.nupkg
releases.linux-arm64.json
RELEASES-linux-arm64
```

Checksums:

```text
SHA256SUMS.txt
```

## AppImage notes

The AppImage contains Toastify Reloaded and its .NET runtime. Host-side media/shortcut integration still uses Linux services such as `playerctl`, `xbindkeys` (X11) and the desktop portal (Wayland).

The historical standalone AppDir layout contains:

```text
AppRun
io.github.Marlius92.ToastifyReloaded.desktop
io.github.Marlius92.ToastifyReloaded.png
usr/bin/toastify-reloaded
usr/lib/toastify-reloaded/ToastifyReloaded.Linux
```

The `usr/bin/toastify-reloaded` wrapper matches the `Exec=toastify-reloaded` desktop entry and launches the bundled application. The new Velopack AppImage is produced by the pinned SDK and includes its native update support; do not infer that layout from the historical AppDir example.

## Build locally

Requirements:

- .NET 8 SDK
- pinned Velopack CLI 1.2.161 for the update-capable AppImage
- curl
- `dpkg-deb` for `.deb`
- common Avalonia Linux libraries

Build x64:

```bash
chmod +x scripts/build-linux.sh
./scripts/build-linux.sh
```

Build ARM64:

```bash
chmod +x scripts/build-linux-arm64.sh
./scripts/build-linux-arm64.sh
```

Build x64 packages:

```bash
chmod +x scripts/package-linux-tar.sh scripts/package-linux-deb.sh scripts/package-linux-velopack.sh
./scripts/package-linux-tar.sh
./scripts/package-linux-deb.sh
dotnet tool install vpk --version 1.2.161 --tool-path .tools/vpk
./scripts/package-linux-velopack.sh x64 1.4.4
```

Build ARM64 packages:

```bash
chmod +x scripts/package-linux-arm64-tar.sh scripts/package-linux-arm64-deb.sh scripts/package-linux-velopack.sh
./scripts/package-linux-arm64-tar.sh
./scripts/package-linux-arm64-deb.sh
./scripts/package-linux-velopack.sh arm64 1.4.4
```

## CI / stable release gate

The updated Linux CI is configured to require:

- x64 build;
- stable headless self-test (`--self-test`);
- GUI startup under virtual X11;
- x64 tar.gz, `.deb` and AppImage packaging;
- Debian content/architecture metadata;
- desktop entry validity;
- AppImage structure;
- native ARM64 build and ELF architecture on the ARM64 runner;
- native x64/ARM64 Velopack AppImage self-test and virtual-X11 startup;
- ARM64 tar.gz, `.deb` and AppImage packaging;
- full-package/feed/checksum identity and publication guards.

The tag workflow for:

```text
v1.4.4-linux
```

selects the tested source build, verifies its artifacts, generates the complete `SHA256SUMS.txt` and publishes the same private draft only after asset and immutable-source checks pass.

### Verified release evidence

[Linux main build 41871731058](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41871731058) passed on source `94e5cdb45086ecc9471c3739426ac9b369019eab`:

- **227 shared regression checks**, including the dedicated hotkey route, post-install restart-failure classification, delayed visible-toast navigation, configured Wayland Alt+Shift+J precedence and the real Linux synthetic-process restart timing check;
- **27 headless UI checks** against the actual Linux and macOS timed toast windows, covering fade interruption, pending actions beyond the previous exit deadline, normal closure after completion, explicit-close cancellation and hidden/closed owners;
- native x64 and ARM64 builds, ELF architecture checks, AppImage self-tests and 10-second graphical startup under virtual X11;
- real native x64 and ARM64 delta reconstruction from the verified published 1.4.3 baseline, comparison of every reconstructed package entry and rejection of deliberately damaged delta content;
- DEB/tar/AppImage packaging, explicit GLib dependency checks, native full/delta feeds and artifact checksum validation.

[Linux Release 41873305792](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41873305792) verified and published all **15 nonempty assets**, including both real delta packages and `SHA256SUMS.txt`. Independent public-download checks verified the bytes and SHA-256 digests of both JSON feeds, both `RELEASES` files and the checksum manifest, matching all 14 listed asset digests. The stable tag and release source both resolve to the tested commit above.

These checks establish automated startup, package and regression evidence. They do not replace live Spotify/Lyrics Plus acceptance on an X11 or Wayland desktop, or an installed-client update/restart test.

## Platform-specific notes

Functional parity does not mean identical OS implementation. Linux deliberately uses MPRIS, X11/Wayland shortcut backends and StatusNotifierItem/AppIndicator instead of Windows media/session APIs. Desktop-environment behavior can therefore differ in permission dialogs, tray availability and fullscreen detection.

## Historical click-to-track behavior (published 1.4.1)

Click the title or artist, or focus either link and press Enter/Space. Toastify uses the track identifier captured with the displayed metadata. It tries the Spotify URI through xdg-open, then the canonical Spotify web page if that handler reports failure. For missing identifiers, local files or ads, it raises Spotify through MPRIS when available, then tries the fixed spotify: application URI. An error is shown if no handler succeeds. Install xdg-utils for xdg-open; gdbus is an optional app-activation fallback. Existing settings need no migration.

Version 1.4.1 preserves settings, themes, hotkeys and media controls from 1.4.0. The existing `v1.4.0-linux` tag remains unchanged. Metadata and opener regression tests run in CI, and the headless self-test uses temporary settings instead of writing the user's configuration.

See [platform status](PLATFORM_STATUS.md) for automated evidence and remaining live desktop validation.

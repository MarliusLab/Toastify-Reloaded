# Changelog

Published releases are listed in [GitHub Releases](https://github.com/MarliusLab/Toastify-Reloaded/releases). Historical tags are preserved.

## 1.3.11 - prepared, native CI and publication pending (Windows)

- Changed toast title, artist, artwork and background clicks and Enter/Space activation to request Lyrics Plus for Spotify's current player item through the fixed `spotify:app:lyrics-plus` page. Alt+Shift+J while a toast is visible retains Go to Now Playing.
- Made the configured Show Toast shortcut show a toast when none is visible and request Lyrics Plus while any toast remains visible. After expiration/hide, the next press shows a toast again. Existing configured keys are reused; no setting is rewritten or new shortcut required. Alt+Shift+J retains visible-toast Now Playing precedence even if assigned to Show Toast or another user action.
- Kept protocol/window work off the UI thread, bounded held-input guards, shared activation serialization, close cancellation and temporary hotkey ownership. Lyrics Plus waits for held keyboard input, including a custom Show Toast combination, and mouse triggers to be released before dispatch/focus. A foreign owner of Alt+Shift+J can prevent its global registration/forwarding while click and Enter/Space still request Lyrics Plus; the Lyrics Plus action sends no synthetic keys.
- Preserved Ctrl+drag, existing settings, playback controls and maintenance choices. Clicking does not enable/apply/repair Spicetify or restart Spotify. Lyrics Plus must already be applied; providers may not return lyrics for every song. Protocol acceptance is distinct from a visually confirmed lyrics page.
- Retained the Now Playing service for the scoped shortcut and its historical tests. Windows 1.3.10 remains the published stable download.
- Local validation passed 317 Windows regression checks, including real SDK/native patch fixtures, 140 shared assertions, 19 release guards, 17 feed/publication tests, 10 recovery tests and the classic WPF layout contract. A production-service probe opened the Lyrics Plus page on real Spotify; the paused state and position were unchanged. Lyrics for the current song were unavailable. Native CI/publication, installed 1.3.11 interaction and continuity while actively playing remain pending.
- See [Windows toast behavior, requirements and history](docs/WINDOWS_TOAST.md).

## 1.3.10 - 2026-11-04 (Windows transition release)

- Added pinned Velopack 1.2.161 startup before WPF/mutex initialization and an updater facade that retains the legacy installer path for installations that have not migrated.
- Added per-machine MSI and compatibility Setup packages for x64/ARM64, retaining the original installation directory, stable launcher, enabled/disabled startup preference and `%APPDATA%` settings. Legacy NSIS registration is retired only after successful migration; installer failure restores the legacy executable/uninstaller backup.
- Added per-architecture package feeds, full baselines and automatic generation of future deltas from a verified previous release. The first transition includes its full base and has no fabricated delta from a legacy installer.
- Added cancellable package download/preparation progress, bounded HTTP/native-patch waits, verified delta/native reconstruction and full fallback. Equivalent reconstructed ZIPs are verified through the native patch engine and an in-memory receipt rather than incorrectly comparing their archive bytes with the original full ZIP.
- Added a protected seed import into the normal user's package cache, stable launcher startup paths, non-elevated restart and an application-running preflight. Existing toast, Now Playing, hotkeys, Spotify/Spicetify maintenance and settings behavior are preserved.
- Updated native build/release gates to validate package identity, architecture, feeds and hashes, exercise synthetic/native delta paths and upload a private draft before publishing only after every required asset is verified. Historical releases keep their original asset contracts and tags.
- Validation: 248 Windows regression checks, 140 shared assertions and 34 migration checks passed, including real SDK/native delta reconstruction and corrupt-delta full fallback. Final publication checks passed 19 release guards, 17 feed/publication tests and 10 recovery checks. Both architectures and all eleven release assets passed native CI and checksum verification.
- Automated installation tests use fake process/registry operations. A later read-only PC audit found 1.3.10 already installed and verified all 485 package payload files, including the two root launcher/updater relocations, against the published full package. That audit and a normal-user launch do not establish an observed elevated transition, rollback or subsequent installed-client update/uninstall acceptance.

## 1.3.9 - 2026-11-03 (Windows stable patch)

- Added Spotify's Go to Now Playing (`Alt+Shift+J`) after opening/restoring Spotify from a toast click or its Enter/Space action. This navigates to the currently playing item, without sending play/pause, skip, seek or a track URI.
- Added the same shortcut while at least one toast is visible, including without toast focus. Overlapping toasts share one temporary registration; the last close releases it. Existing global-hotkey preferences and local fallback are preserved.
- Suspend Toastify's matching registration while forwarding the shortcut, including a user-configured matching binding, to avoid recursion or an unrelated media action. Restore it after the handoff and keep other hotkeys registered.
- Check Spotify foreground ownership and held modifiers before native input, handle blocked/partial input and contain cancellation. A successful native input request is distinguished from app activation; interactive Spotify navigation acceptance remains separate.
- Wait for Enter, Space and physical mouse-button release before moving focus, preventing held activation input from repeating into Spotify's playback controls.
- Corrected the native INPUT union layout used by Windows input injection. Retained all 1.3.7 updater and 1.3.8 toast lifecycle fixes and the existing settings schema.
- Validation: 189 Windows regression checks, 140 shared assertions, 14 release guards, three native main builds and both architecture installers passed. Published checksum manifest matches both installers. Real-client navigation and playback-continuity acceptance remains separate from automated fixtures.

## 1.3.8 - 2026-11-03 (Windows stable patch)

- Connected normal clicks on the Windows toast, including title, artist and artwork, to Spotify activation. Minimized Spotify windows are restored; a closed app uses its registered protocol.
- Preserved Ctrl+drag movement, themes, geometry, animation, timeline, settings and all 1.3.7 maintenance fixes. Added Enter/Space activation for focused text, localized failure feedback, retry, click serialization and close cancellation.
- Added a validated URI/HTTPS opener path for trusted identifiers. Current Windows SMTC snapshots have no catalog URI, so they safely activate Spotify rather than claiming exact-track navigation; see [Windows toast behavior](docs/WINDOWS_TOAST.md).
- Added Windows opener fixtures and actual WPF event regression checks to the existing build and release gates.
- Validation: 109 Windows regression checks, 140 shared assertions, 14 release guards and native CI builds passed. Both installer architectures and their SHA-256 manifest were published. Interactive Spotify foreground acceptance remains unconfirmed.

## 1.5.0-macos-rc.2 - 2026-11-03 (prerelease)

- Added clickable, keyboard accessible toast titles and artists opening the displayed track through a validated Spotify catalog identifier.
- Added safe HTTPS and Spotify-activation fallbacks, bounded opener attempts and failure feedback. Missing or invalid identifiers do not become title searches or arbitrary external links.
- Fixed the stale Preview 1 self-test assertion that blocked RC1 on `main`; the self-test validates the actual Preview/RC/stable channel.
- Hardened DMG sizing, verification and cleanup after the native packaging failure; retained Intel and Apple Silicon ZIP/DMG packages and checksums.
- Added shared regression checks and isolated startup smoke tests to native build/release gates. Test configuration stays separate from user settings.
- Preserved settings, themes, hotkeys, media controls and the architecture-aware updater.
- Remains a release candidate. Real Spotify/permission/updater acceptance on Intel and Apple Silicon, and distribution signing/notarization acceptance, remain open; automated ad-hoc signature checks do not establish Gatekeeper acceptance.

## 1.4.1-linux - 2026-11-03 (stable patch)

- Added clickable, keyboard accessible title and artist links for the track displayed in each toast.
- Captured validated `xesam:url` and `mpris:trackid` identifiers with the same metadata snapshot. Conflicting identifiers are rejected, empty fields are safely framed, and transient missing identifiers do not trigger duplicate track notifications.
- Added canonical Spotify HTTPS fallback when the URI opener fails; missing/unsupported identifiers activate Spotify only. Opener/resolver timeouts propagate to helper processes and failures are reported without terminating the app.
- Added `xdg-utils` as a DEB dependency and updated x64/ARM64 package version defaults.
- Added shared regression checks, native headless tests, isolated X11 startup checks and package validation to release gates.
- Preserved configuration, themes, hotkeys, playback controls and desktop/autostart identifiers. Headless tests now use temporary settings rather than the user's configuration.

## 1.3.7 - 2026-11-03 (Windows stable patch)

- Fixed startup ordering so Spotify and Spicetify versions are discovered before automatic update downloads. Both fields complete independently; media startup/polling and the minimized-start preference are preserved during maintenance.
- Moved version discovery off the UI thread with bounded waits and cancellation. Added desktop/registered/Scoop Spotify paths and Microsoft Store package metadata; Spicetify discovery refreshes user/machine PATH and checks known installer/package-manager locations.
- Added real download byte counts and a percentage when the server supplies its size. Unknown-size downloads show received bytes with an indeterminate bar. Download, verification, Windows permission and installer handoff have separate states.
- Added cancellation before Windows elevation, a 10-minute total download limit, a 30-second idle limit and a 25-second release-metadata limit. Failed/canceled downloads remove temporary installers, show a retryable result and restore maintenance buttons.
- Hardened hidden-command timeout/cancellation and bounded output cleanup, including pipes retained by a child process. Repair reuses the resolved Spicetify executable and receives application-lifetime cancellation.
- Restored visible native NSIS installation progress for in-app updates. Only update launches skip the normal wizard pages; manual installation remains interactive. Invalid PIDs or an unsuccessful process-exit wait stop the update before installed files are overwritten.
- Preserved the settings schema, themes, hotkeys, playback controls and repair flow. A running 1.3.5/1.3.6 build must be exited from the tray and upgraded manually to 1.3.7 once to receive these updater fixes.
- Local validation: 74 Windows maintenance checks passed, including real WPF progress/error/cancel/retry behavior and synthetic updater/repair fixtures. Read-only host probes detected Spotify 1.3.6.1921 and Spicetify 2.46.4. No actual installer or Windows elevation prompt was launched during local tests.
- See [Windows maintenance and the one-time upgrade](docs/WINDOWS_MAINTENANCE.md) for behavior, limits and verification scope.

## 1.3.6 - 2026-11-03 (Windows stable patch)

- Fixed normalization of legacy Windows tags such as `v.1.3.5`, allowing Windows update checks to compare them with executable versions correctly.
- Added regression coverage for current/legacy tag spellings and version comparisons; retained filtering of Linux/macOS releases and architecture-specific installer selection.
- Preserved the settings format and installer upgrade behavior. The clickable title/artist feature is included in Linux/macOS; this Windows patch contains updater maintenance.

## 1.3.5 - 2026-08-19 (Windows; historical tag `v.1.3.5`)

- Changed update checks to select the latest stable Windows release with the matching installer, avoiding Linux/macOS releases in the shared repository.
- Prevented background update/maintenance callbacks from reopening Settings after the user hid it; explicit tray actions can still reopen it.
- Restarted automatic installer updates minimized, while allowing a manual installation to reopen Settings.
- Source provenance: commit [`3852620`](https://github.com/MarliusLab/Toastify-Reloaded/commit/3852620faf76d0421474e1d9eda35a0826b5ed31).

## 1.3.4

### Wider, shorter Settings layout
- Refined the v1.3.3 Settings shell from 1000 × 760 to **1120 × 700**.
- Reduced unnecessary vertical footprint while adding horizontal breathing room for translated labels and wider controls.
- Expanded the adaptive Toast width grid and gives its right-hand panel extra horizontal space so Italian labels such as **Larghezza min** / **Larghezza max** no longer sit against or clip into the numeric controls.
- Preserved the fixed-size, no-user-scrollbar design and the existing Light/Dark tab structure.
- Kept all v1.3.3 localization and selected-tab readability fixes.


## 1.3.3

### Localization, dark tabs and layout
- Expanded the fixed Settings window to 1000 × 760 to prevent right-side clipping in Toast General and Colors & Font at common Windows DPI scales.
- Replaced the native TabItem header rendering with a theme-aware template so selected tabs remain readable in both Light and Dark themes, including the left-side Toast sub-tabs.
- Extended Italian localization from tab names to the complete static Settings interface: labels, group headers, checkboxes, buttons, combo options, help text, tooltips and Reloaded controls.
- Added runtime localization for generated monitor labels and tray menu commands.
- Added localized hotkey action labels through a binding converter.
- Preserved English as a fully reversible runtime language option.

## 1.3.2 - Preset workflow, optional song time and independent slide in/out

### Changed
- Removed the non-functional **Apply preset** button from `Toast -> Themes`.
- Selecting a built-in Toast theme now loads that preset directly into the editable controls; the change is persisted only when the main **Save** button is pressed.
- Saving settings no longer shows a success confirmation dialog. Validation/hotkey errors can still display warnings.

### Added
- Added optional **Show song time / duration** to the Toast (`current / total`, for example `1:26 / 3:20`).
- Enabled and completed the song progress bar so it reflects the current track timeline while the Toast is visible.
- Added independent **Slide In** and **Slide Out** directions.
- Added independent **Slide In** and **Slide Out** distances.
- Existing v1.3.x animation settings remain backward-compatible through the legacy direction/distance fields.


## 1.3.1 - Dark theme rendering fix

### Fixed
- Fixed the Dark application theme leaving the main TabControl/content surface white while text switched to a light foreground.
- Applied theme palette resources at both Window and Application scope so native WPF controls resolve dark SystemColors consistently.
- Added explicit dark-aware surfaces for TabControl, GroupBox, ComboBox, TextBox and ListBox.
- Added a dark-aware ComboBox template and dropdown item styling so closed controls and popup lists remain readable in Dark mode.
- Added dynamic hover, pressed and disabled foreground brushes for both Light and Dark themes.
- Preserved Light as the default historical appearance and kept toast themes independent from the application theme.

## 1.3.0

### Added
- Added Light, Dark and Follow Windows application themes.
- Expanded the fixed Settings window to 840x700 to accommodate Reloaded features without scrollbars.
- Added 13 built-in colored toast presets plus Custom.
- Added Fade, Slide, Fade + Slide and None animation modes.
- Added position presets and multi-monitor targeting.
- Added JSON settings import/export.
- Added advanced diagnostics and compatibility report export.
- Added English/Italian localization infrastructure.

## 1.2.3

- Added optional automatic toast width based on the real track title and artist text.
- Added configurable minimum and maximum automatic toast widths (250 / 600 px defaults).
- Added album-cover artwork from the Windows Spotify media session.
- Added Toastify Reloaded icon fallback when Spotify artwork is unavailable.
- Added selectable toast image mode: Album cover, Toastify Reloaded icon, or None.
- Preserved the historical 60 x 60 artwork slot and 250 x 70 minimum classic toast geometry.
- Kept manual Toast Width behavior available when automatic width is disabled.

## 1.2.2 - 2026-08-16

### Fixed
- Reworked the Toast settings layouts to prevent labels, numeric controls and color/font controls from overlapping at Windows DPI scaling while keeping the historical 580x570 shell and native control style.
- Made the added Reloaded tab vertically scrollable so Compatibility Guard, update controls and Tools never clip each other.
- Reworked Toast corner-radius and color/font grids into explicit rows/columns instead of overlapping absolute margins.

### Added
- Added independent **Fade In** and **Fade Out** times in `Toast -> General`, each configurable from 0 to 5000 ms.
- Added real opacity animation for toast fade-in and fade-out while preserving the classic 250x70 default toast geometry.
- Decoupled global hotkeys from the visible Settings window by using a dedicated hidden Win32/WPF message sink. Hotkeys remain active while any other application is in the foreground and while the Settings window is hidden in the tray.

## 1.2.1 - 2026-08-16

### Fixed
- Replaced the earlier visual approximation with a strict Toastify 1.11.2-reference WPF interface: 580x570, non-resizable, native Windows controls and the historical General / Hotkeys / Toast / Advanced tab order.
- Restored the historical 250x70 toast notification geometry, gradient, 60x60 artwork area, title typography, border and progress-bar layout.
- Removed the custom dark dashboard visual language introduced by earlier Reloaded builds.
- Restored native WPF control styling and the historical settings geometry rather than a Reloaded reinterpretation.

### Added
- Added one extra **Reloaded** tab for Lyrics Plus, automatic updates and Compatibility Guard without modifying the original four-tab layout.
- Added persisted classic Toast size, position, colors, font and border settings.
- Added the historical Toastify-style logo resource and application icon.

### Packaging
- Keeps the v1.2 installer model: installed application under Program Files, Start-menu entry, uninstall support and installer-based automatic updates.

## 1.2.0 - 2026-08-16

### Added
- Added a real Windows installer based on NSIS, matching the installer technology used by the historical Toastify setup.
- Added x64 and ARM64 Setup packages: `ToastifyReloaded-Setup-win-x64.exe` and `ToastifyReloaded-Setup-win-arm64.exe`.
- Added installation under `C:\Program Files\Toastify Reloaded`.
- Added Start-menu shortcuts, Windows Installed Apps registration and `Uninstall.exe`.
- Added clean upgrade-in-place behavior while preserving `%APPDATA%\ToastifyReloaded` settings.
- Added automatic installer-based updates: the app downloads the correct Setup, requests UAC, closes the running version, upgrades and restarts.
- Added `scripts/build-installer.ps1` and `installer/ToastifyReloaded.nsi`.
- GitHub CI now builds an actual installer on every main-branch build.

### Changed
- Public GitHub Releases now publish Setup executables instead of portable ZIP packages.
- Maintenance PowerShell helpers remain embedded in `ToastifyReloaded.exe`; no `scripts` directory is installed or shipped to end users.
- The main GUI remains the original Toastify Reloaded interface, with only the dedicated **Aggiornamenti** tab added in 1.1.3.
- Internal application version bumped to `1.2.0`.

### Migration note
- Portable 1.1.x builds require one manual installation of the 1.2.0 Setup. After that, future updates use the installer automatically.
- The installer intentionally does not delete an old portable EXE outside Program Files.

## 1.1.3 - 2026-08-16

### Fixed
- Restored the original Toastify Reloaded main interface.
- Restored the original window dimensions, header, layout, colors and tray menu.
- Removed Compatibility Guard visual changes outside the dedicated tab.

### Added
- Added only the **Aggiornamenti** tab, matching the original interface style, for auto-update, Compatibility Guard and Spotify/Spicetify/Lyrics repair.
- Automatic functions continue to operate in the background without requiring that tab to be opened.

## 1.1.2 - Release/version alignment
- Aligned executable, GitHub tag and Release versioning.
- Kept Single-EXE packaging and embedded maintenance helpers.

## 1.1.1 - Single-EXE release packaging
- Changed public x64 and ARM64 Release ZIPs to contain only `ToastifyReloaded.exe`.
- Embedded Lyrics, recovery, removal and diagnostic PowerShell helpers inside the application assembly.

## 1.1.0 - Automatic Compatibility Guard
- Added automatic Spotify version detection and repair after Spotify updates.
- Added Lyrics Plus preservation, Spicetify recovery and GitHub self-update support.

## 1.0.3 - Full Rename
- Completed the full application identity rename to `ToastifyReloaded`.

## 1.0.2 - Toastify Reloaded naming update
- Renamed executable and release artifacts to Toastify Reloaded.

## 1.0.1 - 2026-08-16
- Fixed Windows CI compilation errors and updated GitHub Actions.

## 1.0.0 - 2026-08-16
- First public repository structure and core Spotify controls.

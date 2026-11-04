# Windows 1.3.10: transition to full/delta package updates

The transition release replaces repeated NSIS installer updates with Velopack 1.2.161 package updates. Use **ToastifyReloaded-Setup-win-x64.exe** or **ToastifyReloaded-Setup-win-arm64.exe** for the transition, including from an older app's existing update button. These compatibility Setup packages install the per-machine MSI into the existing directory and prepare the full-package base.

## First transition

1. Use the Setup matching the device architecture. If an old update is stuck or its download times out, exit Toastify from the tray and download/run the Setup from GitHub once. The bridge is larger than earlier installers because it contains both the MSI and the full base package.
2. Confirm Windows elevation. The installation remains in its original Program Files/custom folder; it is not silently moved to another user profile. Manual setup refuses to proceed while Toastify is running; in-app setup waits for the exact original PID to exit.
3. The bridge verifies its embedded payloads, backs up the legacy executable/uninstaller, installs the supported MSI layout, validates its version/architecture and saves the protected package seed. Only then does it retire the old NSIS uninstall entry. MSI manages uninstall thereafter.
4. It restarts through the interactive user's non-elevated Explorer token, retaining the minimized preference. If a normal-user restart cannot be obtained, setup reports success and asks the user to reopen the app; it does not run Toastify as administrator instead.

The MSI assets are underlying deployment packages. Existing NSIS users should use the compatibility Setup so the path, base cache and old uninstall registration are handled together.

## Preserved state and functions

Settings stay in `%APPDATA%\ToastifyReloaded\settings.json`; no settings schema migration is required. Themes, toast placement/animation/artwork, hotkey assignments, playback controls, Now Playing click/Alt+Shift+J, update preferences and Spotify/Spicetify repair options remain available. The bridge never edits another user's HKCU preferences. The normal app refreshes an already-enabled startup command to its stable root launcher and leaves disabled startup disabled.

The layout becomes `ToastifyReloaded.exe` (stable launcher), `Update.exe`, `current` (app/runtime files) and package storage. Settings are outside `current`, which Velopack replaces during updates. Files remain separate within packages so unchanged runtime DLLs can be reused by delta generation.

## Later updates

The app checks stable Windows feeds `releases.win-x64.json` or `releases.win-arm64.json` in the same GitHub repository. Linux/macOS and prerelease assets are excluded. Available deltas are downloaded and reconstructed; missing, corrupt or unsuitable deltas fall back to a full package. The first public Velopack release supplies the baseline, so it does not publish a pretend delta against the old NSIS executable.

The initial protected full base is imported into the SDK's normal-user cache after SHA-256 verification. Downloads/preparation have progress, cancellation and bounded waits. The updater waits for Toastify to exit and restarts it after applying the package. Program Files updates can still require elevation; removing the repeated installer does not remove Windows permissions.

Reconstructed ZIP archives can have different compression/metadata from the downloaded full package. The app verifies downloaded package hashes, uses the native engine's file checks and keeps a short-lived reconstruction receipt in memory. It rechecks the staged result before handoff and never treats a writable persistent marker as proof of a cached package's integrity.

## Build, publication and verification

SDK and CLI are pinned to 1.2.161. `build-windows-packages.ps1` creates per-machine MSI/full packages/feeds and compatibility Setup executables. `prepare-velopack-base.py` accepts only a verified complete prior Windows Velopack release as a delta base. `finalize-velopack-release.py` validates package identity, architecture, feed hashes and the checksum manifest without republishing old package versions. Publication uploads a private draft, verifies every uploaded size/digest and source tag, then publishes it.

Local verification includes the Windows service/WPF regression harness, synthetic migration tests with fake processes/registry on PowerShell 7 and 5.1, native package building, feed guards and real CLI/SDK patch-only reconstruction on isolated fixtures. Native patch probes do not apply an update or launch the app. No elevated installer was run on the development PC. Real installed-client migration, update/restart and uninstall acceptance remain pending. Stable upstream recovery behavior is not a guarantee of complete rollback after every filesystem failure.

References: [Velopack Windows layout/installers](https://docs.velopack.io/packaging/operating-systems/windows), [SDK integration](https://docs.velopack.io/integrating/overview), [delta packages](https://docs.velopack.io/packaging/deltas), [persistent settings](https://docs.velopack.io/integrating/preserved-files).

# Toastify Reloaded v1.1.0 - Auto Update & Compatibility Guard

## Purpose

Spotify updates can replace files modified by Spicetify. Toastify Reloaded v1.1.0 treats that as a recoverable compatibility event instead of requiring the user to manually re-run Spicetify every time.

## Spotify version detection

The app tries, in order:

1. the running `Spotify` process and its executable file version;
2. `%APPDATA%\Spotify\Spotify.exe`;
3. `%LOCALAPPDATA%\Spotify\Spotify.exe`;
4. the installed `SpotifyAB.SpotifyMusic` AppX package version.

The first version discovered becomes the baseline. Future version changes trigger the Compatibility Guard.

## Recovery flow

When automatic repair is enabled and Spotify changes version:

```text
Spotify version changed
        |
        v
Stop Spotify
        |
        +--> optional: spicetify upgrade
        |
        v
spicetify backup apply
        |
        +--> failed? --> spicetify restore backup apply
        |
        v
Ensure lyrics-plus is configured
        |
        v
spicetify apply
        |
        v
spicetify auto
        |
        v
Record new compatible Spotify version
```

A failed repair is marked for that Spotify version. The periodic checker will not keep closing Spotify and retrying the same failed repair. A manual **Ripara Spotify / Lyrics ora** action bypasses that guard.

## Toastify Reloaded self-update

The current Windows updater queries GitHub Releases, filters stable Windows releases and compares their normalized tags with the running assembly version. It selects the installer matching the process architecture:

- `ToastifyReloaded-Setup-win-x64.exe`
- `ToastifyReloaded-Setup-win-arm64.exe`

The complete installer is downloaded to a temporary file with byte/percentage progress, cancellation and bounded waits. The app checks the executable structure and starts the installer through Windows elevation. The NSIS updater waits on the exact original Toastify process before replacing files, displays native installation progress and can restart the app with its minimized preference. Downloading/verifying and installer execution have separate progress states.

Windows 1.3.9 and non-migrated installations use this installer mechanism. The 1.3.10 transition adds a Velopack facade: migrated installations use per-architecture package feeds and full/delta downloads, while older installations receive the compatibility Setup. Settings and preferences remain in the same location. See [Velopack transition and verification](WINDOWS_VELOPACK.md) and [Windows maintenance](WINDOWS_MAINTENANCE.md).

Settings are stored separately in `%APPDATA%\ToastifyReloaded` and survive application updates.

## Safety decisions

- No update is installed if the expected architecture-specific release asset is missing.
- A GitHub network error does not affect Spotify controls.
- A failed Spicetify repair is not considered successful and does not overwrite the last known compatible Spotify version.
- `spicetify upgrade` is non-fatal because package-managed Spicetify installations may not support self-upgrade.
- The user can disable each automatic behavior independently.

# Architecture

## Goals

- Avoid dependency on Spotify window titles or UI automation.
- Avoid storing Spotify credentials.
- Keep the Spotify UI customization (Lyrics) isolated from playback control.
- Keep the desktop app dependency-light: .NET 8 + Windows APIs only.

## Components

### `SpotifySessionService`

Uses `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager` to discover the Spotify media session, read metadata, control playback and seek.

### `GlobalHotkeyService`

Uses Win32 `RegisterHotKey`/`UnregisterHotKey` and a WPF `HwndSource` hook to receive `WM_HOTKEY` messages.

### `MediaKeyService`

Uses Win32 `SendInput` for volume up/down/mute keys.

### `SettingsService`

Serializes settings to `%APPDATA%\ToastifyReloaded\settings.json` with `System.Text.Json`.

### `StartupService`

Manages the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry.

### `ToastWindow`

Small borderless WPF window positioned in the bottom-right of the working area. It is intentionally separate from Windows Action Center notifications so the visual behavior remains deterministic.

### Lyrics scripts

Lyrics is deliberately not implemented in the WPF process. PowerShell calls the local Spicetify CLI and enables its built-in `lyrics-plus` Custom App. This means the project does not need to redistribute or fork that app.

## Data flow

```text
Keyboard
  -> RegisterHotKey / WM_HOTKEY
  -> GlobalHotkeyService
  -> MainWindow action dispatcher
  -> SpotifySessionService OR MediaKeyService
  -> Windows media session / Windows audio keys

Spotify media session
  -> polling
  -> TrackInfo
  -> change detection
  -> ToastWindow

Lyrics tab
  -> PowerShellService
  -> scripts/*.ps1
  -> spicetify CLI
  -> Spotify UI customization
```


## v1.1.0 - Maintenance layer

`SpotifyInstallationService` identifica la versione Spotify; `CompatibilityRepairService` gestisce il recupero Spicetify. Nell'implementazione Windows attuale, `UpdateService` consulta GitHub Releases, seleziona l'installer NSIS stabile per l'architettura corretta e gestisce download, avanzamento, annullamento e passaggio all'installer elevato. L'installer attende l'uscita del processo originale, aggiorna i file e riavvia l'app; la versione 1.3.9 scarica ancora il pacchetto completo. Le impostazioni persistenti e lo stato anti-loop restano separati dai binari in `%APPDATA%\ToastifyReloaded\settings.json`. Dettagli e limiti sono in [AUTO_UPDATE_AND_COMPATIBILITY.md](AUTO_UPDATE_AND_COMPATIBILITY.md) e [WINDOWS_MAINTENANCE.md](WINDOWS_MAINTENANCE.md).

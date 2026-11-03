# Toastify Reloaded for macOS

## Current release candidate

**Prepared RC2:** `v1.5.0-macos-rc.2` — a prerelease, not the macOS stable release.

[RC2 release page after publication](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.5.0-macos-rc.2). Until the release workflow has published it, use [GitHub Releases](https://github.com/MarliusLab/Toastify-Reloaded/releases) to inspect available packages. The existing `v1.5.0-macos-rc.1` tag remains unchanged.

The macOS port uses the same Avalonia UI and configuration model as Linux. Shared user-facing settings remain aligned, with macOS backends for Spotify, global shortcuts and login integration. Existing settings, themes, hotkeys and playback controls are preserved by RC2.

## Architectures and packages

CI builds and packages both supported 64-bit Mac architectures:

- Apple Silicon: `osx-arm64`
- Intel: `osx-x64`

RC2 release artifacts:

- `ToastifyReloaded-macOS-arm64.dmg`
- `ToastifyReloaded-macOS-arm64.zip`
- `ToastifyReloaded-macOS-x64.dmg`
- `ToastifyReloaded-macOS-x64.zip`
- `SHA256SUMS-macOS.txt`

The ZIP packages are also the assets used by the in-app architecture-aware updater.

Choose the architecture matching the Mac, verify it against `SHA256SUMS-macOS.txt`, and copy the app from the DMG or extracted ZIP into Applications. An ad-hoc signed package can require macOS approval and is not equivalent to a Developer ID signed, notarized download. Follow the normal macOS approval process only for a build you trust; RC2 does not claim Gatekeeper acceptance from CI signature verification alone.

## Feature contract

RC2 includes the desktop feature set and the new track links:

- classic Toastify-style notification;
- clickable, keyboard accessible title/artist links to the track displayed in the toast;
- album artwork with Toastify icon fallback and no-image mode;
- adaptive toast width and manual width controls;
- optional playback progress and song time / duration;
- 13 built-in toast presets plus Custom;
- full manual toast colors;
- font family and title / artist / time sizes;
- Fade, Slide, Fade + Slide and None;
- independent Slide In / Slide Out direction and distance;
- position presets, monitor selection and margins;
- Light, Dark and Follow System application themes;
- English and Italian interface;
- settings import/export;
- system tray controls;
- global configurable hotkeys;
- start at login;
- Spicetify / Lyrics Plus tools;
- Compatibility Guard after Spotify updates;
- automatic GitHub update checks and optional automatic installation.

Settings are stored at:

```text
~/Library/Application Support/ToastifyReloaded/settings.json
```

The macOS importer also accepts the Linux v1.4 settings JSON. Linux-specific X11/Wayland hotkey choices are mapped to the single macOS global-hotkey backend; all common toast/theme/hotkey values are preserved.

## Platform backends

### Spotify playback and metadata

macOS uses AppleScript through the system `osascript` executable to read Spotify metadata and invoke playback controls.

On first use, macOS can request permission for Toastify Reloaded to automate Spotify. The app bundle includes `NSAppleEventsUsageDescription`, and Developer-ID/hardened-runtime builds include the Apple Events entitlement.

Title/artist links use the validated Spotify catalog identifier captured in the same AppleScript metadata snapshot. The opener tries the Spotify URI, then the canonical HTTPS track page if the handler fails. Ads, local files and missing/invalid identifiers only activate Spotify. No title search or arbitrary metadata URL is launched. Toastify sends navigation links without issuing a play/pause or seek command; Spotify controls how navigation is handled. The toast stays visible during bounded opener attempts and reports failures.

### Global hotkeys

Global hotkeys use SharpHook/libuiohook. macOS requires Accessibility permission for the process before a global keyboard hook can operate.

If hotkeys are not active, enable Toastify Reloaded in:

```text
System Settings
→ Privacy & Security
→ Accessibility
```

Then return to Toastify Reloaded and press **Save** again.

### Spicetify and Lyrics Plus

The macOS Spicetify integration configures the normal Spotify resources path:

```text
/Applications/Spotify.app/Contents/Resources
```

Compatibility Guard retains the same high-level behavior as Linux: detect a Spotify version change, repair/reapply Spicetify, retain Lyrics Plus when enabled, and remember repair attempts to avoid loops.

## Signing and Gatekeeper

The packaging pipeline has two modes:

1. **No Apple credentials:** CI creates an ad-hoc signed `.app`, ZIP and DMG. This is sufficient for build validation, but a downloaded public build can still trigger Gatekeeper warnings. The recorded implementation CI used this mode.
2. **Developer ID configured:** CI can import a Developer ID Application certificate, use Hardened Runtime with the minimum required entitlements, and optionally notarize/staple the app when Apple notarization credentials are also configured.

The signing script explicitly signs nested Mach-O files before signing the `.app` bundle and does not use `codesign --deep`.

### Optional GitHub Actions secrets

For Developer ID signing:

```text
APPLE_CERTIFICATE_P12_BASE64
APPLE_CERTIFICATE_PASSWORD
APPLE_KEYCHAIN_PASSWORD
MACOS_SIGNING_IDENTITY    (optional if the certificate name can be discovered)
```

For notarization:

```text
APPLE_ID
APPLE_TEAM_ID
APPLE_APP_SPECIFIC_PASSWORD
```

These secrets are not required for RC2 compilation or packaging. Their optional support is not evidence that a particular release was Developer ID signed or notarized; record the actual signing/notarization result for that release before advertising it as such.

## Build from source

On macOS with the .NET 8 SDK:

```bash
dotnet run --project tests/ToastifyReloaded.TrackLink.Tests -c Release
python3 scripts/validate-macos-parity.py
./scripts/test-macos-ci.sh
./scripts/build-macos.sh osx-arm64
./scripts/package-macos-app.sh osx-arm64
./scripts/test-macos-gui.sh
./scripts/package-macos-zip.sh osx-arm64
./scripts/package-macos-dmg.sh osx-arm64
```

For Intel replace `osx-arm64` with `osx-x64`. Run the GUI smoke test on the matching native architecture; it checks 10-second startup, not Spotify interaction, permissions or visual correctness.

## RC2 acceptance and path to stable

```text
v1.5.0-macos-rc.2 (prerelease)
        ↓
native build, regression, self-test, startup and ZIP/DMG gates
        ↓
real Intel + Apple Silicon Spotify/permission/updater acceptance
        ↓
record distribution signing/notarization decision and results
        ↓
resolve confirmed blockers and approve stable promotion
        ↓
v1.5.0-macos
```

The obsolete Preview 1 version assertion that blocked RC1 has been fixed. DMG packaging now reserves explicit space and verifies the resulting image. Successful native CI for the earlier implementation is recorded in [PLATFORM_STATUS.md](PLATFORM_STATUS.md); the prepared RC2 tag still needs its own release gates.

Before stable promotion, exercise signed-in Spotify metadata, title and artist clicks, transport controls, Automation and Accessibility permissions, global shortcuts, artwork/themes/timeline, scaling/multiple monitors, Spicetify/Lyrics repair and an actual updater transition on Intel and Apple Silicon. Earlier CI executed native startup on Apple Silicon and inspected the Intel build; that does not establish interactive or Intel runtime acceptance. These checks remain open until their results are recorded.

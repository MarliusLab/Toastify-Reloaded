# Toastify Reloaded for macOS

## Current published release candidate

**Published RC3:** `v1.5.0-macos-rc.3` — a prerelease, not the macOS stable release.

[Download RC3 and its checksums](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.5.0-macos-rc.3). RC3 was published on 2026-11-04 from `4dabfe3eef58a95f921e6df39fe39d38ccdaef22` after all three platform builds passed at that source. Its tag resolves to that same commit. Earlier RC1/RC2 tags and assets remain unchanged.

The macOS port uses the same Avalonia UI and configuration model as Linux. Shared user-facing settings remain aligned, with macOS backends for Spotify, global shortcuts and login integration. Existing settings, themes, hotkeys and playback controls are preserved.

The native package version is **`1.5.0-rc.3`**. [macOS main CI](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41839459037) verified the native updater bundles and distribution packages; [release workflow](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41840242222) verified and published the complete 11-asset prerelease. This does not promote macOS to stable.

RC3 brings the Windows toast behavior and update maintenance to macOS while retaining settings and media controls. Clicking the toast, title or artist opens the fixed **Lyrics Plus** route. Enter/Space on a text link acts after key release. The configured **Show Toast** shortcut first displays a toast and, while any toast is visible, requests Lyrics Plus on the next press. Its additive default is `Ctrl+Alt+T`; existing saved assignments and deliberately empty assignments remain intact.

There is no double-tap deadline: the second press may occur later, provided a toast is still visible. Once all toasts close, the next press displays a toast again.

**Alt+Shift+J** retains Go to Now Playing while a toast is visible. Toastify waits for held input to be released, activates Spotify, checks its foreground process and sends that separate navigation shortcut through a bounded native request. This can require Accessibility/Automation approval. Synthetic hook events are ignored and forwarding temporarily suspends Toastify's matching action to prevent recursion. No playback or track-selection command is issued; accepted input alone does not prove Spotify navigated successfully.

Lyrics Plus uses `spotify:app:lyrics-plus`, followed by a Spotify-activation fallback if the route fails. Toastify reports that fallback or a failure. It does not guess a song identifier for lyrics, install a provider, or claim that a successfully requested route guarantees available lyrics.

## Architectures and packages

CI builds and packages both supported 64-bit Mac architectures:

- Apple Silicon: `osx-arm64`
- Intel: `osx-x64`

RC3 distribution artifacts:

- `ToastifyReloaded-macOS-arm64.dmg`
- `ToastifyReloaded-macOS-arm64.zip`
- `ToastifyReloaded-macOS-x64.dmg`
- `ToastifyReloaded-macOS-x64.zip`
- `SHA256SUMS-macOS.txt`

The ZIP packages retain the legacy installation/transition path. Native Velopack-managed installations use the architecture-specific update feeds and packages below.

RC3 includes six additional update assets: one full `.nupkg`, `releases.osx-ARCH.json` and `RELEASES-osx-ARCH` per architecture. Full package names are `ToastifyReloadedMac-1.5.0-rc.3-osx-x64-full.nupkg` and `ToastifyReloadedMac-1.5.0-rc.3-osx-arm64-full.nupkg`. With the common checksum manifest, this first native Velopack baseline has **11 assets** and no fabricated delta.

Choose the architecture matching the Mac, verify it against `SHA256SUMS-macOS.txt`, and copy the app from the DMG or extracted ZIP into Applications. An ad-hoc signed package can require macOS approval and is not equivalent to a Developer ID signed, notarized download. Follow the normal macOS approval process only for a build you trust; RC3 does not claim Gatekeeper acceptance from CI signature verification alone.

## Feature contract

RC3 preserves the desktop feature set and changes toast navigation as described above:

- classic Toastify-style notification;
- clickable, keyboard accessible Lyrics Plus navigation and a separate Now Playing action;
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
- automatic GitHub update checks and optional automatic installation;
- independent Spotify/Spicetify version probes, bounded maintenance operations, download progress and cancellation before updater handoff.

Settings are stored at:

```text
~/Library/Application Support/ToastifyReloaded/settings.json
```

The macOS importer also accepts the Linux v1.4 settings JSON. Linux-specific X11/Wayland hotkey choices are mapped to the single macOS global-hotkey backend; all common toast/theme/hotkey values are preserved.

## Platform backends

### Spotify playback and metadata

macOS uses AppleScript through the system `osascript` executable to read Spotify metadata and invoke playback controls.

On first use, macOS can request permission for Toastify Reloaded to automate Spotify. The app bundle includes `NSAppleEventsUsageDescription`, and Developer-ID/hardened-runtime builds include the Apple Events entitlement.

Metadata continues to validate Spotify catalog identifiers from the same AppleScript snapshot. RC3 toast clicks use the fixed Lyrics Plus route, and its separate Now Playing action uses Spotify's native navigation shortcut. The toast stays visible during bounded opener attempts and reports failures.

Historical RC2 behavior: title/artist links used the validated identifier captured in the displayed snapshot, with Spotify URI, canonical HTTPS and application-activation fallbacks. That describes the published RC2 package, not the new RC3 click action.

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

RC3 resolves Spotify bundle versions and Spicetify executables from macOS installation locations, including environments where a GUI app does not inherit a terminal's PATH. Probes start independently and do not wait behind repair or an update download.

## Updates in RC3

The pinned **Velopack 1.2.161** SDK creates native Mac bundles with `Contents/MacOS/UpdateMac` and the package manifest. Managed installations use their architecture-specific full/delta feed and native apply/restart helper. Legacy ZIP-installed bundles retain a bounded replacement-helper transition with rollback and a normal-user restart; settings remain in Application Support. Restart-minimized behavior is retained.

This first native Velopack release is a full baseline. Later releases can produce deltas from a verified published full package, and clients need a usable cached baseline; unavailable or invalid deltas fall back to a full download. Progress shows transfer bytes/percentage when available and verification/application stages. Cancellation is offered before handoff; the app waits for an accepted handoff before closing. Installed-client apply/restart still needs acceptance on a real Mac.

### RC4 maintenance in preparation

The next RC4 candidate separates a completed legacy bundle replacement from cleanup/restart problems. Failure to remove an old backup becomes a warning; if the regular-user restart request fails, the helper records `installed-manual-restart` and leaves the new bundle installed for manual launch. Replacement/rollback failures remain failures, and a backup survives a failed rollback. `apply-result.txt` and `apply-update.log` remain in the per-update cache beside the downloaded ZIP. These files do not alter saved preferences or cause an automatic reinstall. The native Velopack path continues to report helper handoff rather than claiming the app has already restarted.

## Signing and Gatekeeper

The packaging pipeline has two modes:

1. **No Apple credentials:** CI creates an ad-hoc signed `.app`, ZIP and DMG. This is sufficient for build validation, but a downloaded public build can still trigger Gatekeeper warnings. The recorded implementation CI used this mode.
2. **Developer ID configured:** CI can import a Developer ID Application certificate, use Hardened Runtime with the minimum required entitlements, and optionally notarize/staple the app when Apple notarization credentials are also configured.

The signing script explicitly signs nested Mach-O files before signing the `.app` bundle and does not use `codesign --deep`.

RC3 additionally signs the SDK-added native update helper and finalized bundle **before** generating package/feed hashes. Native CI exercises the SDK's exact ad-hoc signing flags before packaging and verifies the final helper/bundle signatures. It then creates ZIP and DMG from that same finalized bundle. These checks do not establish notarization or Gatekeeper approval.

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

These secrets are not required for RC3 compilation or ad-hoc packaging. Their optional support is not evidence that a particular release was Developer ID signed or notarized; record the actual signing/notarization result for that release before advertising it as such.

## Build from source

On macOS with the .NET 8 SDK:

```bash
dotnet run --project tests/ToastifyReloaded.TrackLink.Tests -c Release
python3 scripts/validate-macos-parity.py
python3 scripts/test-portable-release.py
./scripts/test-macos-ci.sh
dotnet tool install vpk --version 1.2.161 --tool-path .tools/vpk
./scripts/build-macos.sh osx-arm64
./scripts/package-macos-app.sh osx-arm64
./scripts/package-macos-velopack.sh osx-arm64
./scripts/test-macos-gui.sh
./scripts/package-macos-zip.sh osx-arm64
./scripts/package-macos-dmg.sh osx-arm64
```

For Intel replace `osx-arm64` with `osx-x64`. Run the GUI smoke test on the matching native architecture; it checks 10-second startup, not Spotify interaction, permissions or visual correctness.

## RC3 acceptance and path to stable

The native source build passed the self-test including osascript availability, **203 shared regression assertions**, **27 release guards** and **18 native feed/publication tests**. Both Intel and Apple Silicon bundles include verified updater/helper signatures and manifests; the packaged Apple Silicon app passed the 10-second startup test. Both DMGs passed image checksum verification, and the final 11-asset manifest passed the release pipeline. Intel was built and inspected, rather than executed on the ARM64 runner.

Post-publication checks confirmed release ID `433974899`, `draft=false`, `prerelease=true`, the exact source/tag commit and all 11 uploaded asset names/sizes/digests. The five public feed/RELEASES/checksum files were downloaded and byte-hashed; the manifest matches all ten payload/metadata asset digests, and both full-only feeds match their published package identities, sizes and SHA-256 values. No package was installed or updater restart executed by these checks.

```text
v1.5.0-macos-rc.3 (published prerelease)
        ↓
native build, regression, self-test, updater bundle, startup and ZIP/DMG/feed gates passed
        ↓
real Intel + Apple Silicon Spotify/permission/updater acceptance
        ↓
record distribution signing/notarization decision and results
        ↓
resolve confirmed blockers and approve stable promotion
        ↓
v1.5.0-macos
```

The obsolete Preview 1 version assertion that blocked RC1 has been fixed. DMG packaging reserves explicit space, retries only transient `Resource busy` creation failures with a bounded delay, and verifies the resulting image. Historical native CI and RC2 publication remain recorded in [PLATFORM_STATUS.md](PLATFORM_STATUS.md). RC3's native build and publication gates have passed; interactive acceptance remains open.

Before stable promotion, exercise signed-in Spotify metadata, Lyrics Plus toast clicks, both Show Toast presses, scoped Alt+Shift+J, uninterrupted playback, transport controls, Automation and Accessibility permissions, artwork/themes/timeline, scaling/multiple monitors, Spicetify/Lyrics repair and an actual updater transition on Intel and Apple Silicon. CI executes native startup only on the runner's architecture and inspects both builds; that does not establish interactive or other-architecture runtime acceptance. These checks remain open until their results are recorded.

# Linux and macOS implementation and validation status

Audit date: **2026-11-03**. Starting point: the live `main` commit
[`0af3713`](https://github.com/MarliusLab/Toastify-Reloaded/commit/0af3713144355cc33d5f7e41dd75f56567b44d37).
This records implementation and available evidence separately. Existing tags
and published releases are not changed by this work.

## What was actually on GitHub

| Platform | Recorded state | Evidence |
|---|---|---|
| Linux | Published stable `v1.4.0-linux`; tag resolves to `48b08c6` | [Successful release run](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/32101742397) |
| macOS | Source declares `v1.5.0-macos-rc.1`; that tag resolves to `2e98d2b`; no published macOS release found | [Existing RC1 tag](https://github.com/MarliusLab/Toastify-Reloaded/tree/v1.5.0-macos-rc.1) |
| macOS on main | Last native build failed its obsolete Preview 1 version assertion; later packaging steps were skipped | [Failed build](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/32287106204) |

Linux has the implemented Windows 1.3.4 baseline documented in
[LINUX_PARITY.md](LINUX_PARITY.md): MPRIS control, toasts, themes, timeline,
hotkeys, import/export, tray, startup integration, Spicetify repair and
package-aware updates. The published stable release is real. This does not
establish that every desktop environment, Spotify package or architecture has
passed an interactive test.

## Changes after that baseline

- Linux and macOS title and artist links open the track represented by the
  displayed metadata snapshot. Identity comes from Spotify/MPRIS metadata,
  not from a title/artist search. Only validated track IDs become links.
- If a track link cannot be handled, the opener tries the canonical HTTPS
  Spotify page. Missing or unsupported identifiers only activate Spotify;
  they never become arbitrary external URLs or shell commands. If no
  fallback succeeds, the application reports the failure.
- The macOS self-test accepts the actual parsed Preview/RC/stable channel
  instead of requiring an obsolete Preview 1 tag. The existing RC1 tag and
  version constants remain unchanged.
- Both platform build workflows run for shared link changes and regression
  tests. They continue to run on pull requests against `main`.
- Linux release packaging now requires the shared regression harness,
  headless self-test and virtual-X11 startup smoke test. Previously the
  release workflow only rebuilt and validated packages.
- macOS build and release workflows require the regression harness and an
  isolated startup smoke test of the native packaged app. This verifies
  startup, not Spotify interaction or visual correctness.
- Startup smoke tests use temporary configuration directories. Linux no
  longer treats a premature clean exit as a successful startup test and no
  longer kills unrelated `xbindkeys` processes.

## Repeatable automated checks

Shared regression harness, on any supported .NET SDK host:

```sh
dotnet run --project tests/ToastifyReloaded.TrackLink.Tests -c Release
python3 scripts/validate-macos-parity.py
```

Linux x64, with the dependencies listed in [LINUX.md](LINUX.md):

```sh
./scripts/build-linux.sh
./scripts/test-linux-rc.sh
./scripts/test-linux-ci.sh
```

macOS, after producing app bundles for both architectures:

```sh
./scripts/test-macos-ci.sh
./scripts/build-macos.sh osx-x64
./scripts/build-macos.sh osx-arm64
./scripts/package-macos-app.sh osx-x64
./scripts/package-macos-app.sh osx-arm64
./scripts/test-macos-gui.sh
```

Build workflows additionally create and inspect Linux tar/DEB/AppImage and
macOS ZIP/DMG artifacts. Linux ARM64 remains cross-built with ELF and package
checks; the workflow does not run the ARM64 app. macOS startup is exercised
only for the runner's native architecture; both architectures are published
and inspected. A Windows build or headless self-test cannot replace these
native checks.

## Remaining native acceptance work

Linux:

- Test with a signed-in Spotify client: click title and artist, change songs,
  pause, skip, and check that the displayed song opens. Test Spotify closed,
  missing URI handler, local files, and temporarily missing metadata.
- Confirm X11 shortcuts and native Wayland portal permission/shortcut
  behavior in the desktop environments being supported. Virtual X11 smoke
  testing does not exercise native Wayland.
- Verify tray visibility, monitors, scaling, themes, timeline seeking and
  artwork in a real desktop session.
- Verify Spicetify/Lyrics repair and update application on real AppImage,
  DEB and portable installations; portable updates intentionally leave the
  downloaded package for manual installation.
- Run the app natively on Linux ARM64 before claiming native ARM64 runtime
  validation.

macOS, before promotion toward stable:

- Obtain a green updated macOS build, including native self-tests, both
  architecture packages, signature verification and startup smoke test.
- On real Intel and Apple Silicon Macs, confirm Spotify Automation
  permission, metadata, transport controls and the title/artist links.
- Confirm Accessibility permission and global shortcuts; inspect toast
  appearance, artwork, progress, seeking, animation and multiple monitors.
- Exercise Spicetify/Lyrics repair and Compatibility Guard with installed
  Spotify, and the in-app updater with a controlled subsequent test build.
- Decide whether public distribution will require Developer ID signing and
  notarization. The current pipeline supports optional credentials and
  otherwise uses ad-hoc signatures; ad-hoc CI validation does not establish
  Gatekeeper acceptance of a downloaded app.

No release or tag promotion is part of this change. Native acceptance gaps
above remain open until their results are recorded.

## Checks executed on the development host

On 2026-11-03, Windows x64 with .NET SDK 9.0.315 targeting .NET 8:

- Linux Release build: PASS (five existing platform/API/deprecation warnings; no errors).
- macOS Release build: PASS (three existing platform/API/deprecation warnings; no errors).
- Shared production-parser, URI allowlist, notification identity, timeout propagation and launcher fallback harness: PASS, 132 assertions.
- macOS headless self-test on Windows: PASS, 27 checks; native osascript probe explicitly skipped. Isolated temporary configuration was used. This is not native macOS validation.
- Linux/macOS UI parity validator: PASS, 49 shared settings, 61 named controls, matching toast markup, 13 themes plus Custom.
- Modified shell scripts: syntax checks PASS. Whitespace check: PASS.

Native GitHub Actions results are recorded after the pull-request checks finish.

# Implementation, release preparation and validation status

## Releases prepared on 2026-11-03

| Platform | Prepared version/tag | Scope | Publication state |
|---|---|---|---|
| Windows x64 / ARM64 | Stable `v1.3.6` | Legacy Windows updater-tag normalization; configuration preserved | Prepared; awaiting release workflow |
| Linux x64 / ARM64 | Stable `v1.4.1-linux` | Clickable title/artist links, safe metadata/openers, regression and release gates | Prepared; awaiting release workflow |
| macOS Intel / Apple Silicon | Prerelease `v1.5.0-macos-rc.2` | Same track links, corrected RC self-test, reliable DMG sizing and native startup gate | Prepared; awaiting release workflow |

The release workflows must pass for the exact release commit before publication is considered verified. Versioned download links are targets until the corresponding release appears in [GitHub Releases](https://github.com/MarliusLab/Toastify-Reloaded/releases). The evidence recorded below belongs to the earlier implementation commit; it is not a claim that these three new tags have already passed CI or been published. Existing tags are preserved.

Windows 1.3.6 contains updater maintenance. Title/artist track links are included in Linux 1.4.1 and macOS RC2. macOS remains a release candidate while the interactive acceptance work below is open. No Developer ID or notarization acceptance is established by the recorded ad-hoc CI signatures.

## Historical audit baseline

Audit date: **2026-11-03**. Starting point: the live `main` commit
[`0af3713`](https://github.com/MarliusLab/Toastify-Reloaded/commit/0af3713144355cc33d5f7e41dd75f56567b44d37).
This records the state before the implementation and release preparation.
Existing tags and packages remain historical artifacts; the prepared releases
use new tags.

### What was actually on GitHub at the audit baseline

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
  instead of requiring an obsolete Preview 1 tag. The original implementation
  retained the RC1 version; release preparation advances the source to RC2
  while preserving the existing RC1 tag.
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
- After a repeated `hdiutil` space error in a later CI run, the DMG builder
  reserves explicit HFS+ image capacity (payload plus 25% and 64 MiB), logs
  host disk availability, verifies the completed image and removes staging
  and incomplete output on failure. This avoids relying on automatic image
  sizing; latest packaging results are linked from the pull request.

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

Publishing the prepared versions does not close these native acceptance gaps.
Their results must be recorded before claiming those behaviors verified or
promoting macOS to stable.

## Historical checks executed on the development host

On 2026-11-03, Windows x64 with .NET SDK 9.0.315 targeting .NET 8:

- Linux Release build: PASS (five existing platform/API/deprecation warnings; no errors).
- macOS Release build: PASS (three existing platform/API/deprecation warnings; no errors).
- Shared production-parser, URI allowlist, notification identity, timeout propagation and launcher fallback harness: PASS, 132 assertions.
- macOS headless self-test on Windows: PASS, 27 checks; native osascript probe explicitly skipped. Isolated temporary configuration was used. This is not native macOS validation.
- Linux/macOS UI parity validator: PASS, 49 shared settings, 61 named controls, matching toast markup, 13 themes plus Custom.
- Modified shell scripts: syntax checks PASS. Whitespace check: PASS.

Native GitHub Actions results for the implementation commit are recorded below.

## Native GitHub Actions results for the implementation

Implementation commit: `e4251bbfc52e7c03c6f79ac414c470ddd99f2c75`, [PR #1](https://github.com/MarliusLab/Toastify-Reloaded/pull/1). Both native workflows completed successfully on 2026-11-03.

| Workflow | Checks that passed |
|---|---|
| [Linux Build](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41619269739) | 132 regression assertions; x64 publish; native headless self-test; 10-second Xvfb startup; desktop entry; x64 tar/DEB/AppImage and AppImage structure; ARM64 cross-build, ELF architecture and tar/DEB validation; artifact upload |
| [macOS Build](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41619269418) | feature/parity checks; native headless self-test including osascript availability; 132 regression assertions; x64 and ARM64 publish; app bundles and ad-hoc signature verification; 10-second native ARM64 app startup; ZIP/DMG validation and SHA-256 checksums; artifact upload |

The former RC1 self-test build blocker is resolved. The native macOS startup test ran on Apple Silicon; the Intel executable was built and inspected, not executed. Linux ARM64 was cross-built, not executed. These workflow artifacts predate the prepared release versions and do not establish their publication. The remaining interactive acceptance work above is still open.

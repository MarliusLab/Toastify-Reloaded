# Implementation, releases and validation status

## Three-platform update in preparation — 2026-11-04

All user-facing Windows changes must also be implemented for Linux and macOS. Different operating-system backends and permission limits must be documented explicitly; a Windows-only build is not sufficient evidence for the other platforms.

| Platform | Candidate in source | Publication state |
|---|---|---|
| Windows x64 / ARM64 | `v1.3.12` | In preparation; existing `v1.3.11` remains the published baseline |
| Linux x64 / ARM64 | `v1.4.2-linux` | In preparation; existing `v1.4.1-linux` remains the published baseline |
| macOS Intel / Apple Silicon | `v1.5.0-macos-rc.3` | In preparation; existing RC2 remains published; stable promotion is not implied |

Linux/macOS candidates now request Lyrics Plus from the toast, title/artist and Enter/Space. The saved Show Toast shortcut displays a toast on its first press, requests Lyrics Plus while any toast is visible, and returns to showing a toast after all close. Its additive default is Ctrl+Alt+T; saved and empty assignments are retained. Shared navigation/lifetime guards handle overlapping toasts, cancellation and repeated input. Lyrics Plus uses a fixed route and safe Spotify-activation fallback without selecting or restarting a track.

Alt+Shift+J remains a separate current-track action. Linux reads fresh MPRIS identity, temporarily owns the X11 global binding while a toast is visible and offers the action locally on Wayland; the approved global Wayland Show Toast shortcut still supports both presses. macOS waits for input release, activates Spotify and forwards its native Now Playing shortcut only after checking foreground/input state, with hook recursion guards. Permission/provider availability and actual Spotify behavior still need interactive acceptance.

Both native candidates add independent Spotify/Spicetify version probes, process/network deadlines, stage/byte/percentage update progress, cancellation before handoff, and guarded exit/restart. Platform settings, media controls, themes, tray, import/export and startup features are retained.

Velopack 1.2.161 now supplies architecture-specific native AppImage/Mac update packages and feeds. Linux 1.4.2 and macOS RC3 are the first full baselines: **13 Linux assets** and **11 macOS assets**, without invented legacy-to-Velopack deltas. A subsequent release can generate deltas from verified published full packages, and clients need a usable cached base; full download remains the fallback. Compatibility installation paths are retained for legacy packages. Existing tags/assets are not overwritten.

The release pipeline stages only explicit validated files, rechecks hashes from tested artifacts, verifies the exact uploaded asset set and immutable full source SHA, then publishes the same upload's draft ID. This describes implemented guards, not a completed publication.

### Candidate checks actually executed on the development host

| Check | Environment | Result and limit |
|---|---|---|
| Windows regression suite | Windows x64, actual WPF and pinned native delta fixtures | PASS, 324 checks; no installed-client update or elevation |
| Shared track/maintenance suite | Windows x64, real SDK with isolated synthetic feeds/packages | PASS, 199 checks; two Linux process-handoff checks SKIP on Windows |
| Linux Release build | Windows x64, .NET 8 target | PASS, 0 errors / 4 existing warnings; native desktop execution still separate |
| Manual Setup parser / migration | Windows x64, native NSIS user-mode parser and synthetic migration operations | PASS, 7 parser cases and 37 migration checks; no MSI, registry writes or real installation |
| Release guard / Windows feed / recovery | Windows Python with local fixtures | PASS, 27 / 17 / 10 tests; private upload and immutable tag contracts |
| macOS Release build | Windows x64, .NET 8 target | PASS, 0 errors / 3 existing warnings; cross-platform compilation only |
| macOS isolated headless self-test | Windows x64, .NET runtime 8.0.28 | PASS, 31 checks; native osascript explicitly SKIP |
| Native feed/publication regression suite | Windows Python, local ZIP/feed fixtures and fake API | PASS, 18 tests; includes both architectures, full/delta sets, hashes, exact draft ID, provenance, bounded downloads and immutable tags |
| Python compile and macOS packaging shell syntax | Development host | PASS; syntax checking does not execute native packaging |

The self-test uses an ignored workspace temporary configuration; no installed Spotify interaction or application update was performed by these checks. The first default system-temp attempt was denied by the managed host's file-move permissions; the isolated workspace rerun passed.

The updated Linux workflow requires **native x64 and ARM64** AppImage self-tests and virtual-X11 startup, replacing the older ARM64 cross-build-only gate. The macOS workflow requires the signed SDK updater/manifest inside each bundle, native signing preflight, native-architecture startup and ZIP/DMG/feed verification. These new native gates and release publication remain pending until linked successful runs are recorded. A virtual desktop smoke test does not establish real Spotify, native Wayland, permissions or updater acceptance.

## Published baseline before this update

| Platform | Published version/tag | Scope | Publication state |
|---|---|---|---|
| Windows x64 / ARM64 | Stable `v1.3.11` | Lyrics Plus toast clicks, contextual Show Toast shortcut and Velopack full/delta updates | Published; release workflow passed |
| Linux x64 / ARM64 | Stable `v1.4.1-linux` | Clickable title/artist links, safe metadata/openers, regression and release gates | Published; release workflow passed |
| macOS Intel / Apple Silicon | Prerelease `v1.5.0-macos-rc.2` | Same track links, corrected RC self-test, reliable DMG sizing and native startup gate | Published; release workflow passed |

Windows 1.3.11 was published from `a39bb4175299a66bbaa241fcb36c1f3dcf55c38c` by [release workflow 41827896540](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41827896540). Both x64 and ARM64 package sets passed native CI and publication verification: 13 uploaded assets, including 2 deltas from the verified 1.3.10 baseline, matching feeds and SHA-256 checksums. Full-package fallback remains available. Linux 1.4.1 and macOS RC2 remain at their tested commit `fc11dfb30ebb0846ed41fea473c76adadb117804`, with their original assets unchanged. Existing tags are preserved. Earlier implementation and publication evidence, including the 1.3.10 transition, is retained below as history.

Windows 1.3.11 opens Lyrics Plus from toast clicks and Enter/Space; the saved Show Toast shortcut shows a toast when none is visible, requests Lyrics Plus while any toast is visible and returns to showing one after all expire/hide. Scoped Alt+Shift+J preserves Go to Now Playing, including when that combination has a saved user assignment. Lyrics Plus follows Spotify's current player item without a guessed track ID or playback command. The 1.3.10 Velopack layout and settings are retained. Requirements and remaining acceptance limits are described in [Windows toast behavior](WINDOWS_TOAST.md). Linux 1.4.1 and macOS RC2 include track links from validated native identifiers. macOS remains a release candidate while interactive acceptance is open; recorded ad-hoc CI signatures do not establish Developer ID signing or notarization acceptance.

## Historical audit baseline

Audit date: **2026-11-03**. Starting point: the live `main` commit
[`0af3713`](https://github.com/MarliusLab/Toastify-Reloaded/commit/0af3713144355cc33d5f7e41dd75f56567b44d37).
This records the state before the implementation and release preparation.
Existing tags and packages remain historical artifacts; the published releases
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

## Historical changes after the original audit baseline

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
python3 scripts/test-portable-release.py
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
./scripts/package-macos-velopack.sh osx-x64
./scripts/package-macos-velopack.sh osx-arm64
./scripts/test-macos-gui.sh
```

Build workflows additionally create and inspect Linux tar/DEB/AppImage and
macOS ZIP/DMG artifacts. The candidate workflow builds and executes Linux ARM64
AppImage self-test/startup on a native ARM64 runner; its result remains pending.
macOS startup is exercised
only for the runner's native architecture; both architectures are packaged
and inspected. A Windows build or headless self-test cannot replace these
native checks.

## Remaining native acceptance work

Linux:

- Test with a signed-in Spotify client: toast/title/artist clicks and both Show
  Toast presses should open Lyrics Plus without interrupting playback. Test
  Alt+Shift+J after track changes and with local/missing identifiers. Cover
  Spotify closed, absent Lyrics Plus/provider and missing URI handlers.
- Confirm X11 shortcuts and native Wayland portal permission/shortcut
  behavior in the desktop environments being supported. Virtual X11 smoke
  testing does not exercise native Wayland.
- Verify tray visibility, monitors, scaling, themes, timeline seeking and
  artwork in a real desktop session.
- Verify Spicetify/Lyrics repair and update progress/cancellation/application on
  real managed/legacy AppImage, DEB and tar installations, including full
  fallback and settings preservation. Tar transitions retain manual download.
- Record the new native ARM64 CI result separately from interactive ARM64
  desktop acceptance; neither has been established by a Windows host build.

macOS, before promotion toward stable:

- On real Intel and Apple Silicon Macs, confirm Spotify Automation
  permission, metadata, transport controls, Lyrics Plus clicks/two presses and
  scoped Alt+Shift+J during uninterrupted playback.
- Confirm Accessibility permission and global shortcuts; inspect toast
  appearance, artwork, progress, seeking, animation and multiple monitors.
- Exercise Spicetify/Lyrics repair and Compatibility Guard with installed
  Spotify, and the in-app updater with a controlled subsequent test build.
- Decide whether public distribution will require Developer ID signing and
  notarization. The current pipeline supports optional credentials and
  otherwise uses ad-hoc signatures; ad-hoc CI validation does not establish
  Gatekeeper acceptance of a downloaded app.

Publishing these versions does not close these native acceptance gaps.
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

The former RC1 self-test build blocker is resolved. The native macOS startup test ran on Apple Silicon; the Intel executable was built and inspected, not executed. Linux ARM64 was cross-built, not executed. These workflow artifacts predate these release versions and do not establish their publication. The remaining interactive acceptance work above is still open.


## Verified publication pipelines

| Platform | Release | Successful pipeline | Published assets |
|---|---|---|---|
| Windows | [1.3.6](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.3.6) | [Windows Release](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41624335433) | x64/ARM64 installers and SHA256SUMS-Windows.txt |
| Linux | [1.4.1](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.4.1-linux) | [Linux Release](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41624241687) | x64 AppImage, x64/ARM64 DEB and tar.gz, SHA256SUMS.txt |
| macOS | [1.5.0 RC2](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.5.0-macos-rc.2) | [macOS Release](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41624322519) | Intel/Apple Silicon ZIP and DMG, SHA256SUMS-macOS.txt |

Validation included 140 production version/parser/launcher assertions, 14 publication authorization/recovery tests, native platform build/self-test/startup gates and package validation. Windows 1.3.6 was the latest at that publication; Windows 1.3.7 subsequently became the global latest release and was superseded by 1.3.8 and then 1.3.9. macOS RC2 is a prerelease; interactive Spotify, permission and distribution-signing acceptance gaps remain as listed above.

## Windows 1.3.7 maintenance validation

[Published Windows 1.3.7](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.3.7) passed [release workflow 41629987353](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41629987353), including 76 Windows maintenance/UI checks, 140 shared assertions, 14 release guards and both architecture installers. A real download completed with visible byte/percentage reports and a simulated non-executing handoff. The x64/ARM64 installer digests match the published checksum manifest.

See [Windows maintenance](WINDOWS_MAINTENANCE.md) for the one-time manual upgrade from a stuck older build, configuration preservation and test limits. No real elevated installation was performed on the development host.


## Windows 1.3.8 toast validation

[Published Windows 1.3.8](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.3.8) passed [release workflow 41636985889](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41636985889) at `eb63f7f314e6e41b4b71b83bd16b7c6d3939bc23`. The 109 Windows regression checks include 33 new service/WPF assertions for canonical links, app activation fallbacks, routed clicks, keyboard, retry/cancellation, and keeping a timed toast alive while activation is pending. The 140 shared assertions, 14 release guards, main Windows/Linux/macOS builds and both Windows architecture installers passed. The public checksum manifest matches both uploaded installer digests. Existing tags and Linux/macOS assets remain unchanged.

Real-client foreground acceptance remains unconfirmed; automated service/WPF fixtures do not establish that result. See [Windows toast behavior](WINDOWS_TOAST.md).


## Windows 1.3.9 Now Playing validation

[Published Windows 1.3.9](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.3.9) passed [release workflow 41648205388](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41648205388) at `892faf3af2706303997579fe2c3a3e140dae495b`. Validation includes 189 Windows checks, 140 shared assertions, 14 release guards, the three native main builds and both Windows installer architectures. Tests cover held Enter/Space/mouse release before focus, balanced input and partial cleanup, foreign shortcut collisions, saved matching bindings, suspension/reacquisition, overlapping toast lifetime and actual WPF event routing. The public checksum manifest matches the uploaded installer digests. All older tags and Linux/macOS assets remain unchanged.

Fixtures used simulated shell/input/registration adapters and isolated settings. They did not inject input into the real Spotify client or install software. Actual Spotify navigation and uninterrupted playback still require interactive acceptance; accepted native input alone does not establish that result.


## Windows 1.3.10 Velopack transition validation

[Windows 1.3.10](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.3.10) was published from `d28e935887531d35b90c123a0e277888b4dea625`. The Windows, Linux and macOS main builds passed. The [original release run](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41792715538) validated 248 service/WPF regression checks, 140 shared assertions, 34 synthetic migration checks, 16 release guards and 12 feed/draft tests; real CLI delta chains and the production SDK/native patch-only path also passed, including corrupt-delta full fallback.

Its first publication attempt stopped safely after uploads because a draft without an existing tag could not be found through the tag endpoint. [Recovery 41796047284](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41796047284) passed 19 release guards, 17 feed/publication tests and 10 recovery checks. It verified the original successful source build, pinned release/asset IDs and all uploaded bytes before publishing the same draft by release ID, without rebuilding or replacing assets. The regular publisher now also uses the upload's exact release ID.

Both compatibility Setup executables, MSI packages, full baselines and per-architecture feeds were built and verified. All eleven uploaded assets match their recorded digests, and every non-manifest asset is listed in the public checksum manifest. Publication used a private draft until uploads and source-tag verification completed. The first public Velopack release establishes the full baseline; it intentionally has no fabricated delta from the legacy installer. Older tags and Linux/macOS release assets remain unchanged.

Settings/registry/process tests used isolated fixtures; no elevated installer, installed-client apply/restart or uninstall was executed on the development PC. MSI databases were inspected read-only. Windows Sandbox was not available for installed-client acceptance. See [migration and remaining verification](WINDOWS_VELOPACK.md).

## Windows 1.3.11 Lyrics Plus and delta validation

All three native main builds passed at the release source: [Windows](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41827160393), [Linux](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41827160296) and [macOS](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41827160347). The macOS DMG step initially encountered a transient `Resource busy` error; the same-source retry passed without code changes.

[Windows 1.3.11](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.3.11) was published from `a39bb4175299a66bbaa241fcb36c1f3dcf55c38c` by [release workflow 41827896540](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41827896540). Local and native CI checks passed 317 Windows regression checks, 140 shared assertions, 34 synthetic migration checks, 19 release guards, 17 feed/publication tests, 10 recovery tests and the classic WPF layout contract. Native delta reconstruction and corrupt-delta full fallback remained covered by isolated production SDK/patch fixtures. Both x64 and ARM64 package sets passed native CI and publication verification: 13 uploaded assets, including 2 deltas from the verified 1.3.10 baseline, matching feeds and SHA-256 checksums. Full-package fallback remains available.

Post-publication verification downloaded and hashed both architecture feeds, RELEASES files and the checksum manifest, which matches all twelve package/metadata asset digests. The actual public x64 delta (259,561 bytes) was reconstructed from the verified public 1.3.10 baseline with the pinned native patch-only helper. All 489 ZIP entry names and uncompressed SHA-256 values matched the public 1.3.11 full package (82,303,129 bytes). The installed baseline remained unchanged; no update was applied. ARM64's published delta is 259,266 bytes versus its 77,619,706-byte full package; this does not establish native ARM64 execution on the development PC.

Installed 1.3.11 toast clicks, the configured Show Toast two-press action and scoped Alt+Shift+J during active playback still require direct acceptance testing. A separate production-service probe opened the Lyrics Plus page on real Spotify and preserved the paused state and position; that narrower result does not establish those installed interactions. Provider availability is separate from route navigation. No new elevated installer, installed-client update/restart or uninstall acceptance is claimed. See [Windows toast verification](WINDOWS_TOAST.md) and [Velopack migration](WINDOWS_VELOPACK.md).

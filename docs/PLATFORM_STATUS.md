# Implementation, releases and validation status

## Current published restart-recovery update — 2026-11-04

| Platform | Published release | Verified public state |
|---|---|---|
| Windows x64 / ARM64 | [Stable `v1.3.13`](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.3.13) | Release ID `434093050`; 13 assets, including both architecture deltas from 1.3.12; not draft or prerelease |
| Linux x64 / ARM64 | [Stable `v1.4.3-linux`](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.4.3-linux) | Release ID `434092268`; 15 assets, including both architecture deltas from 1.4.2; not draft or prerelease |
| macOS Intel / Apple Silicon | [Prerelease `v1.5.0-macos-rc.4`](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.5.0-macos-rc.4) | Release ID `434092339`; 13 assets, including both architecture deltas from RC3; not draft, remains a prerelease |

All three releases and their new tags resolve to `269c51f92ec4deac190a8a2b322ea92faeb591c3`. An independent comparison of all **31 previous tag refs** found every target unchanged; there are now 34 refs. The release publishers validated their exact private-draft asset sets against tested artifact sizes, SHA-256 values and native feed/package identities before publication. Independent public API reads confirmed 13/15/13 nonempty uploaded assets with SHA-256 digests, the exact source SHA and the flags above.

The Windows correction addresses the observed installer error after 1.3.12 had already been applied: NSIS's native `System.dll` in the working directory was mistaken for a managed reference during `Add-Type`. The desktop-user launcher now compiles using the loaded CLR directory and explicit framework references, then restores both working directories. Installation and restart outcomes are separate: a post-install helper/import/compile/launch failure reports successful installation with manual-start instructions instead of an installation failure. The application never falls back to an elevated launch.

Linux and macOS apply the same outcome distinction to their native legacy replacement/restart paths. Linux DEB dependencies include the GLib command tools needed by the shortcuts. Existing Lyrics Plus clicks, the saved Show Toast action, settings and the separate current-track shortcut remain supported. The next Show Toast press does **not** require a rapid double tap: it requests Lyrics Plus after any delay while at least one toast remains visible, within the existing configured toast lifetime.

### Main CI and release evidence for the current source

| Platform | Successful main build | Successful publication | Checks actually passed |
|---|---|---|---|
| Windows | [41853524659](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41853524659) | [41856043801](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41856043801) | 324 Windows/WPF regressions; 217 shared assertions; 94 desktop-restart assertions; 7 native NSIS parser cases; 37 synthetic migration checks; both architecture package sets |
| Linux | [41853524394](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41853524394) | [41856043711](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41856043711) | 226 shared assertions on x64; native x64/ARM64 AppImage self-tests and 10-second Xvfb startup; native patch-only reconstruction and corrupt-delta rejection on both architectures; DEB/tar/AppImage/feed verification |
| macOS | [41853524407](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41853524407) | [41856043940](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41856043940) | Native self-test; 225 shared assertions; native ARM64 patch-only reconstruction and corrupt-delta rejection; 10-second ARM64 app startup; both architecture bundle/signature/ZIP/DMG/feed checks |

All three main builds passed the parity verifier, **27 release guard** and **18 native feed/publication** tests. Windows additionally passed **17 Windows feed/publication** and **10 recovery** tests. Shared assertion totals differ because native-only process and replacement cases execute on their corresponding host; a cross-platform build does not substitute for those cases.

The 94 desktop-restart assertions comprise 50 checks in eight isolated compilation/restart cases and 44 host/provenance/entrypoint checks. Actual Windows PowerShell 5.1 x64 and x86 reproduced the original `CS0009` collision and passed the corrected real C# compilation and directory-restoration checks; PowerShell 7 also passed. Six subprocess cases invoked the real migration entrypoint with fake migration modules: a missing restart module or invalid launcher source returned **10** after simulated committed installation, while failed migration returned **1** without importing the restart helper. No MSI, real updater, desktop-user launch, registry or application settings were used by these fixtures.

The first successful Linux completion triggered all three release checks, which correctly reported `waiting_for_successful_main_builds:windows,macos` and skipped download/upload/publication. Only after all three main builds succeeded at this SHA did the final runs above publish their tested artifacts. macOS initially waited for a hosted runner; that queue delay did not weaken the gate or change the source.

The public delta sizes are Windows x64/ARM64 **203,361 / 203,058 bytes**, Linux x64/ARM64 **231,445 / 232,479 bytes**, and macOS x64/ARM64 **556,308 / 180,165 bytes**. Unix native patch-only probes reconstructed the actual newly built packages from the verified published previous full baselines and compared every ZIP entry's content hash. Deliberately corrupt deltas were rejected. Linux x64/ARM64 compared four package entries each; macOS ARM64 compared 14. The macOS Intel package and delta were built and inspected, not natively executed. No update or application was installed/launched by the patch probes; full fallback remains covered separately by the SDK fixtures.

### Actual installed PC and remaining acceptance limits

A read-only post-publication check still finds installed **Windows 1.3.12**, with source identity `4dabfe3eef58a95f921e6df39fe39d38ccdaef22`; its configuration contains the same **76 properties** and its JSON SHA-256 remains unchanged. The user-observed 1.3.12 package application had succeeded before the launcher-compilation error. **1.3.13 has not been installed on that PC by these checks**, so a successful real desktop restart after the corrected Setup is not yet claimed.

Native Linux/macOS Spotify interactions, global shortcut permissions/compositor behavior and installed update/restart flows remain **SKIP / interactive acceptance pending**. Virtual-X11 startup, macOS ARM64 startup, synthetic token/launcher tests and patch-only probes establish their narrower results only. macOS remains RC4; Developer ID/notarization and downloaded-app Gatekeeper acceptance remain separate from ad-hoc CI signature verification. Earlier release and audit evidence is preserved below as history.

## Previous three-platform publication — source `4dabfe3` (2026-11-04)

All user-facing Windows changes must also be implemented for Linux and macOS. Different operating-system backends and permission limits must be documented explicitly; a Windows-only build is not sufficient evidence for the other platforms.

| Platform | Published version | Publication state |
|---|---|---|
| Windows x64 / ARM64 | [Stable `v1.3.12`](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.3.12) | Published; 13 verified assets, including 2 deltas from 1.3.11 |
| Linux x64 / ARM64 | [Stable `v1.4.2-linux`](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.4.2-linux) | Published; 13 verified assets, first full native Velopack baseline |
| macOS Intel / Apple Silicon | [Prerelease `v1.5.0-macos-rc.3`](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.5.0-macos-rc.3) | Published; 11 verified assets, first full native Velopack baseline; stable promotion is not implied |

Linux/macOS now request Lyrics Plus from the toast, title/artist and Enter/Space. The saved Show Toast shortcut displays a toast on its first press, requests Lyrics Plus while any toast is visible, and returns to showing a toast after all close. Its additive default is Ctrl+Alt+T; saved and empty assignments are retained. Shared navigation/lifetime guards handle overlapping toasts, cancellation and repeated input. Lyrics Plus uses a fixed route and safe Spotify-activation fallback without selecting or restarting a track.

Alt+Shift+J remains a separate current-track action. Linux reads fresh MPRIS identity, temporarily owns the X11 global binding while a toast is visible and offers the action locally on Wayland; the approved global Wayland Show Toast shortcut still supports both presses. macOS waits for input release, activates Spotify and forwards its native Now Playing shortcut only after checking foreground/input state, with hook recursion guards. Permission/provider availability and actual Spotify behavior still need interactive acceptance.

Both native ports add independent Spotify/Spicetify version probes, process/network deadlines, stage/byte/percentage update progress, cancellation before handoff, and guarded exit/restart. Platform settings, media controls, themes, tray, import/export and startup features are retained.

Velopack 1.2.161 now supplies architecture-specific native AppImage/Mac update packages and feeds. Linux 1.4.2 and macOS RC3 are the first full baselines: **13 Linux assets** and **11 macOS assets**, without invented legacy-to-Velopack deltas. A subsequent release can generate deltas from verified published full packages, and clients need a usable cached base; full download remains the fallback. Compatibility installation paths are retained for legacy packages. Existing tags/assets are not overwritten.

All three versions were published from the same tested source commit, `4dabfe3eef58a95f921e6df39fe39d38ccdaef22`. The release pipelines staged only explicit validated files, rechecked hashes from tested artifacts, verified the exact uploaded asset sets and immutable full source SHA, then published the same uploads' draft IDs. Independent public API verification confirmed 13/13/11 nonempty uploaded assets, recorded SHA-256 digests and the expected stable/stable/prerelease flags. Existing release tags and assets remain unchanged.

### Checks actually executed on the development host before publication

| Check | Environment | Result and limit |
|---|---|---|
| Windows regression suite | Windows x64, actual WPF and pinned native delta fixtures | PASS, 324 checks; no installed-client update or elevation |
| Shared track/maintenance suite | Windows x64, real SDK with isolated synthetic feeds/packages | PASS, 203 checks; two Linux process-handoff checks SKIP on Windows |
| Linux Release build | Windows x64, .NET 8 target | PASS, 0 errors / 4 existing warnings; native desktop execution still separate |
| Manual Setup parser / migration | Windows x64, native NSIS user-mode parser and synthetic migration operations | PASS, 7 parser cases and 37 migration checks; no MSI, registry writes or real installation |
| Release guard / Windows feed / recovery | Windows Python with local fixtures | PASS, 27 / 17 / 10 tests; private upload and immutable tag contracts |
| macOS Release build | Windows x64, .NET 8 target | PASS, 0 errors / 3 existing warnings; cross-platform compilation only |
| macOS isolated headless self-test | Windows x64, .NET runtime 8.0.28 | PASS, 31 checks; native osascript explicitly SKIP |
| Native feed/publication regression suite | Windows Python, local ZIP/feed fixtures and fake API | PASS, 18 tests; includes both architectures, full/delta sets, hashes, exact draft ID, provenance, bounded downloads and immutable tags |
| Python compile and macOS packaging shell syntax | Development host | PASS; syntax checking does not execute native packaging |

The self-test uses an ignored workspace temporary configuration; no installed Spotify interaction or application update was performed by these checks. The first default system-temp attempt was denied by the managed host's file-move permissions; the isolated workspace rerun passed.

### Native main builds and coordinated publication that passed

| Platform | Successful native main build | Successful publication | Actual validation |
|---|---|---|---|
| Windows | [41839459585](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41839459585) | [41840242086](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41840242086) | 324 Windows checks; 203 shared checks; real SDK/native patch-only fallback; 7 NSIS parser cases; 37 synthetic migration checks; x64/ARM64 full/delta feeds and compatibility installers |
| Linux | [41839458627](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41839458627) | [41840242116](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41840242116) | 205 shared checks, including native Linux synthetic restart timing; native x64 and ARM64 AppImage self-tests and 10-second Xvfb startup; ELF architecture, DEB/tar/AppImage, full feeds and checksums |
| macOS | [41839459037](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41839459037) | [41840242222](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41840242222) | Native self-test; 203 shared checks; x64/ARM64 bundle and updater signing/identity checks; 10-second native ARM64 startup; ZIP/DMG/full feeds and checksums |

All three main builds passed the permanent parity verifier, 27 publication guard tests and 18 native feed/publication tests; Windows additionally passed 17 Windows feed/publication and 10 recovery tests. The two Linux-specific process-handoff checks were explicitly skipped on Windows/macOS, accounting for 203 versus 205 shared checks.

The first Linux/macOS completions triggered publication checks that correctly reported `waiting_for_successful_main_builds:windows` and `publish=false`; download, upload and publication steps were skipped. Only after the Windows main build also succeeded did all three final guards report `all_three_main_builds_passed`, the exact run IDs above and the same source SHA. Releases reused those tested build artifacts without rebuilding binaries.

Windows 1.3.12 contains two real deltas from the verified public 1.3.11 baseline: x64 279,184 bytes and ARM64 278,888 bytes. Linux/macOS contain no delta packages in their first native baselines. The published private-draft verification passed for all 37 assets across the three releases. This does not constitute an atomic transaction across GitHub releases.

The Linux native ARM64 gate now supplements the earlier cross-build-only evidence. The macOS native startup result covers the runner's ARM64 architecture; Intel was built, signed and inspected, not launched. Virtual-X11/native startup smoke tests do not establish real Spotify, native Wayland, permissions or installed-updater acceptance. Interactive acceptance remains **SKIP / not yet performed** for the newly published Linux/macOS behavior; macOS remains RC3.

Post-publication checks downloaded all 15 public feed/RELEASES/checksum files across the three platforms, verified their bytes against GitHub asset sizes and SHA-256 values, and matched each manifest and full/delta entry to its exact package architecture/version. All 28 prior tag targets remained unchanged; the three new tags resolve to the released source above. Windows 1.3.12 is the repository Latest release. A read-only PC check still finds installed 1.3.11 and the same 76-setting JSON checksum; the new installer was not interactively installed or launched by these tests.

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
macOS ZIP/DMG artifacts. The current workflow built and executed Linux ARM64
AppImage self-test/startup on a native ARM64 runner; the successful run is linked above.
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
- Keep the passing native ARM64 CI result separate from interactive ARM64
  desktop acceptance, which remains open; a Windows host build establishes neither.

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

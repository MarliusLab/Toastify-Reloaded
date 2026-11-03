# Windows 1.3.7 maintenance update

Release status: **published stable patch**, tag **[`v1.3.7`](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.3.7)**. Both installers and their checksum manifest are available.

## One-time upgrade from a running 1.3.5 or 1.3.6 build

The fixes below run inside 1.3.7. An older running program continues using its older downloader and cannot acquire the new progress/timeout behavior until it is replaced.

1. Right-click the Toastify Reloaded system-tray icon and choose **Exit**. Closing Settings with its X button only hides it. If the old application does not respond, end only ToastifyReloaded.exe in Task Manager before running the installer.
2. Open the [Windows v1.3.7 release](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.3.7) and download the installer matching the device: `ToastifyReloaded-Setup-win-x64.exe` or `ToastifyReloaded-Setup-win-arm64.exe`.
3. Run that installer manually and confirm the normal Windows permission request. Let its installation progress finish, then start Toastify Reloaded 1.3.7.
4. Open **Reloaded** and check the displayed application, Spotify and Spicetify versions. Subsequent update downloads use the new progress and cancellation behavior.

The existing configuration remains in `%APPDATA%\ToastifyReloaded\settings.json`. No settings migration or removal is required. Themes, hotkeys, playback controls, automatic maintenance choices and startup preferences are preserved.

## Version discovery

Previously, startup could wait for an automatic update download before checking Spotify and Spicetify. This left both version fields showing ellipses while the download ran without visible transfer details.

Version probes now start before automatic downloads and publish each result independently. One slow or missing application cannot hide the other application's completed result. Process, file, registry and CLI probes run off the UI thread; overall discovery waits are bounded. A failed or timed-out probe completes with an explicit result instead of leaving the field in its initial loading state.

Spotify discovery checks a running process, normal desktop installation paths, registered application paths, Scoop and Microsoft Store package metadata. Store output is parsed as structured version/location data; warning text is not accepted as a version.

Spicetify discovery reads current user and machine PATH values on each probe, as well as the running process's inherited PATH. It also checks the official Local AppData installation, the older installer location, WinGet, Scoop and Chocolatey locations. It invokes the resolved executable directly with literal arguments; repair reuses that same resolved executable. Discovery has a 10-second overall limit, and each Spicetify version command has a three-second execution limit plus bounded cleanup. The official installation locations are documented by the [Spicetify installer](https://raw.githubusercontent.com/spicetify/cli/main/install.ps1).

Minimized startup is honored before maintenance awaits. Media-session initialization and normal track polling are not held behind a potentially long automatic repair. Closing the application cancels its version probes and active repair commands. Exit is deferred while Windows elevation is pending; after confirmed installer launch all toast windows close so the old process can exit promptly.

## Download and installation progress

| Stage | What the user sees |
|---|---|
| Download, total size known | Received/total bytes, a real percentage and a progress bar |
| Download, total size unknown | Received bytes and an indeterminate bar; no invented percentage |
| Installer verification | A separate verification message and indeterminate progress |
| Windows permission request | An instruction to confirm Windows elevation; download cancellation is no longer offered |
| Installer started | An instruction to follow the native installer's progress bar |
| Canceled, timed out or failed | A specific result, stopped spinner and restored maintenance buttons for retry |

The download can be canceled while waiting for response headers, transferring bytes or verifying the completed installer. Cancellation ends at the Windows elevation handoff. Canceling the Windows permission request is reported separately from canceling the download.

The default limits are:

- **25 seconds** for a release-metadata request, including its response body.
- **10 minutes** for the complete installer download.
- **30 seconds** without incoming download data.

Failed and canceled downloads remove their temporary installer files. The completed download is closed and checked as a Windows executable before it is offered to the installer. This structural check is not an Authenticode-signature verification.

In-app updates now show the native NSIS installation progress instead of running the installer silently. Update launches skip the normal wizard pages and proceed to progress; a manually opened installer retains its normal interactive pages. The application does not invent a percentage for installation work performed by another process.

For an in-app update, the installer validates the supplied process ID and waits for the old process to exit before changing installed files. If that wait times out or the process state cannot be verified, installation stops before overwrite. It does not force an overwrite of a possibly running executable. The update wait is at most 60 seconds.

## Verification recorded on 2026-11-03

| Check | Recorded result |
|---|---|
| Windows maintenance regression harness | **76 checks passed**: detection, synthetic repair, downloader progress/timeouts/cancellation, permission-result handling, installer guards and real WPF UI behavior |
| Shared core regression harness | **140 checks passed**; existing URI/tag and cross-platform behavior retained |
| Release-preparation guard tests | **14 checks passed** |
| Read-only probes on the user's Windows host | Spotify Desktop **1.3.6.1921** and Spicetify **2.46.4** detected |
| WPF rendering and responsiveness | Progress panel rendered and inspected; byte/percentage display, unknown-size state and cancellation/error button recovery exercised |

Updater tests use synthetic HTTP responses and a simulated installer launcher. Repair-flow tests use injected executables, stop and restart callbacks; they do not repair, stop or restart the user's Spotify. Subprocess tests execute only synthetic output/sleep helpers. The rendered download preview uses simulated transfer data and is not evidence of an actual installation in progress.

No actual installer or Windows elevation prompt was launched during local tests. A successful build, installer compilation and these regression checks do not establish an end-to-end elevated upgrade on a real installed x64/ARM64 system. Release workflow results and published asset checks must be recorded separately for the exact release commit.

## Verified publication

[Windows release workflow 41629987353](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41629987353) passed for commit `689f4f7e6c79e3d3b42fbd940dc7049d98f94762`. Both x64 and ARM64 installers and SHA256SUMS-Windows.txt are published; their uploaded digests match the public checksum manifest.

A real 51,746,692-byte download of the published Windows 1.3.6 installer also completed through the new downloader in about 232 seconds, with 1,270 progress reports and a simulated handoff. No installer or elevation prompt was launched. The user's settings file remained identical to its distinct pre-test backup.

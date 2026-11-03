# Windows toast: go to Now Playing

Windows 1.3.9 adds Spotify's **Go to Now Playing** command after opening/restoring its window. Click the toast title, artist, artwork or background, or focus either text line and press Enter/Space. Ctrl+drag retains the existing toast movement behavior.

**Alt+Shift+J works while a toast is visible**, including without toast focus. One temporary registration is shared by overlapping toasts; the last close/hide releases it. The existing global-hotkeys preference is respected. If global hotkeys are disabled, the same combination works locally with a toast text line focused, provided another app has not registered it. If another app owns the combination, click or Enter/Space still activates Spotify but forwarding the occupied shortcut is skipped. The tooltip reports the fallback.

Spotify documents [Alt+Shift+J as Go to Now Playing](https://support.spotify.com/us/article/keyboard-shortcuts/). The target is the item currently playing when the action runs, which may have changed since the toast appeared. Toastify sends no play/pause, skip, seek or track-URI navigation command for this action. The song continues from its current position; the Spotify client controls the resulting view.

## Focus and shortcut handling

The opener waits a bounded interval for held shortcut/activation keys and mouse buttons to be released before moving focus. This includes Alt/Shift/Ctrl/Win/J, Enter, Space and both main mouse buttons: a held Space must not auto-repeat into Spotify's play/pause control. It restores a minimized Spotify window or uses the fixed `spotify:` protocol to launch it, then checks that a live Spotify window owns the foreground. It checks held input and focus again immediately before injection and submits one balanced Alt+Shift+J sequence. A failed focus check sends no new key-down events.

Toastify suspends only its matching shortcut while forwarding input. This includes a saved user binding with the same combination, preventing recursive capture or accidental execution of that binding. Other hotkeys remain registered. Before releasing ownership it checks that the combination is available, including when global hotkeys are disabled; a known foreign owner blocks forwarding. Availability can change after this probe, so an application registering the same combination during the handoff remains a Windows-level race. The registration is restored after the handoff if a toast remains visible; settings are not rewritten.

Windows can reject foreground activation or input, including when Spotify runs at a higher integrity level. The result distinguishes app activation from a requested Now Playing navigation. Partial injection triggers best-effort release of only the synthetic keys already inserted. Foreground checks and input insertion are separate Windows operations, so there is a small unavoidable focus-change race. See Microsoft's [SendInput contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).

## Track identity

The Windows SMTC [media properties](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmediaproperties) do not supply a Spotify catalog URI. This feature uses the desktop navigation shortcut and does not guess identifiers from title/artist. The older validated URI opener remains available internally and tested; the toast uses the separate Now Playing action.

## Verification

Automated checks use fake shell/input/registration adapters and real WPF routed events without controlling the user's Spotify client. They cover focus gating, held keys, input layout/sequence/cleanup, shortcut suspension and restoration, overlapping toast lifetime, local keyboard routing, retry/cancellation and the existing timeline/lifetime behavior.

Local Windows validation passed 189 Windows regression checks, 140 shared version/track-link assertions, 14 release guards, the classic WPF layout contract and Linux/macOS structural parity. Existing maintenance regression checks remain in the same harness.

Real-client acceptance remains separate: play a song, navigate elsewhere in Spotify, minimize it, then click the toast or press Alt+Shift+J while it is visible. Confirm Spotify shows the current item and playback continues without a restart or pause. Repeat with two toasts, Ctrl+drag, disabled global hotkeys, and a closed client. Successful input insertion alone does not prove that Spotify processed the command.

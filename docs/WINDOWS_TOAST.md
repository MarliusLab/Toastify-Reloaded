# Windows toast: Lyrics Plus clicks and Now Playing shortcut

**[Windows 1.3.11 is published](https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.3.11).** Native CI and [release workflow 41827896540](https://github.com/MarliusLab/Toastify-Reloaded/actions/runs/41827896540) passed for source `a39bb4175299a66bbaa241fcb36c1f3dcf55c38c`. A real-client Lyrics Plus service probe also passed as described below. Earlier 1.3.9/1.3.10 behavior remains recorded in the history section.

## Windows 1.3.11 behavior

Click a toast's title, artist, artwork or background to request **Lyrics Plus** in Spotify. Enter/Space on either focused text line uses the same action. **Alt+Shift+J while a toast is visible keeps Go to Now Playing**; it does not open Lyrics Plus. Ctrl+drag still moves the toast; themes, positioning, playback controls and existing settings are preserved.

The click/Enter/Space action opens the single fixed page `spotify:app:lyrics-plus`, corresponding to Spotify's internal `/lyrics-plus` route. Lyrics Plus reads Spotify's current player item and follows queue changes. No title search or track identifier from a stale toast is sent. This Lyrics Plus path does not inject Alt+Shift+J, send play/pause/skip/seek, or run a Spotify restart. The separate shortcut retains its existing guarded Now Playing forwarding path.

Lyrics Plus must already be enabled and applied through Spicetify. Clicking does not install, enable, apply or repair it; the existing Reloaded controls remain the place for those operations. Opening its page cannot guarantee that a provider has lyrics for every song. If the protocol request fails, Toastify tries to activate Spotify, reports the fallback and permits a retry. If Spotify accepts the URI but cannot render the custom app, Toastify cannot detect that from protocol acceptance alone.

The route and current-track behavior are supported by Spicetify 2.46.4's [Lyrics Plus page initialization](https://github.com/spicetify/cli/blob/v2.46.4/CustomApps/lyrics-plus/index.js#L950), [playbar navigation](https://github.com/spicetify/cli/blob/v2.46.4/CustomApps/lyrics-plus/PlaybarButton.js#L13) and [custom-app route registration](https://github.com/spicetify/cli/blob/v2.46.4/src/apply/apply.go#L434). A read-only local audit also found the applied route, script, CSS and manifest on the Windows PC.

## Configured Show Toast shortcut

The existing **Show Toast** assignment (default Ctrl+Alt+T) changes its action according to visibility:

1. With no visible toast, it shows the current toast.
2. While any toast is still visible, pressing the same configured shortcut requests Lyrics Plus.
3. Once all toasts expire or hide, the next press shows a toast again.

This uses the saved combination, including custom keys, without rewriting preferences or requiring a new assignment. The global-hotkeys preference still controls registration. **Exception: Alt+Shift+J is reserved for Now Playing while a toast is visible, even when saved as Show Toast or another user action.** Other Show Toast combinations follow the contextual Lyrics Plus behavior above.

## Input, focus and concurrent toasts

URI dispatch, window activation and live held-input checks run off the WPF UI thread. The Lyrics Plus path waits a bounded interval for held keyboard input, including arbitrary keys in the configured Show Toast combination, and both main mouse buttons to be released before dispatch or focus. The separate Now Playing path retains its own shortcut/Enter/Space/mouse guards. This protects against held activation input repeating into Spotify after focus changes. Failure to release input stops the attempt; Toastify does not release the user's physical keys itself.

Overlapping toasts share one temporary Alt+Shift+J registration and one execution gate for both actions. Repeated or concurrent activation is suppressed while a request is in progress; the latest visible toast receives the global Now Playing action. The final close/hide releases the temporary registration. Closing a toast cancels its pending work, while an already accepted external request cannot be undone.

The global-hotkeys setting is respected. Alt+Shift+J is also handled locally when the toast receives it. If another application owns that chord, global registration or Now Playing forwarding can be unavailable; click and Enter/Space still request Lyrics Plus. The URI action does not depend on forwarding the occupied combination into Spotify. Any saved user assignment is preserved, with temporary toast ownership released/restored through the existing coordinator; settings are not rewritten.

Windows may refuse foreground activation even after accepting a URI. The result distinguishes a Lyrics Plus protocol request, Spotify-only activation and failure. A successful request confirms handoff to the registered handler; it does not confirm the displayed view, provider response or playback continuity.

## Windows 1.3.11 verification

Local and native CI validation passed **317 Windows regression checks**, **140 shared assertions**, **19 release guards**, **17 feed/publication tests**, **10 recovery tests** and the classic WPF layout contract. The Windows harness covers Lyrics Plus URI/fallback behavior, guarded Now Playing forwarding, configured shortcut keys, visible/expired toast transitions, WPF event routes, held triggers, responsiveness, cancellation, shared gating and foreign shortcut ownership. It also retains maintenance and real SDK/native patch fixture checks; the total is not 317 new Lyrics Plus tests. These automated results remain separate from real-client acceptance.

A separate probe through the production opener on real Spotify successfully requested the route. A read-only screenshot confirmed the Lyrics Plus page and its Karaoke/Synced/Unsynced modes. The current song had no available lyrics, illustrating the provider limit. Media-session state confirmed that the paused state and position were unchanged. This verifies the service route and observed page; it does not establish a real click or shortcut from an installed 1.3.11 toast, or continuity while actively playing.

Both x64 and ARM64 package sets passed native CI and publication verification: 13 uploaded assets, including 2 deltas from the verified 1.3.10 baseline, matching feeds and SHA-256 checksums. Full-package fallback remains available. Installed-client acceptance remains pending. After installation, confirm an actual toast click opens Lyrics Plus while scoped Alt+Shift+J goes to Now Playing during playback. Check that the configured Show Toast shortcut shows with no toast, requests Lyrics Plus while any toast is visible, and shows again after all expire/hide, respecting the Alt+Shift+J exception above. Repeat with a paused song, a track change, overlapping toasts, Ctrl+drag, disabled global hotkeys and an unavailable Lyrics Plus page. Confirm the displayed page and playback state directly. Protocol acceptance alone is not that acceptance result.

## History: Windows 1.3.9 and 1.3.10

Windows 1.3.9 introduced Spotify's [Go to Now Playing shortcut](https://support.spotify.com/us/article/keyboard-shortcuts/) after restoring/activating its window; 1.3.10 retained it. Toast clicks, Enter/Space and the scoped Alt+Shift+J action requested the currently playing item through one balanced native input sequence. Foreground checks, held-key guards and temporary release of matching Toastify registrations protected that forwarding path. A foreign owner could block forwarding; successful input insertion was not proof that Spotify processed it.

The historical 1.3.9 validation passed **189 Windows regression checks**, **140 shared assertions** and **14 release guards**, plus native builds and installers. The 1.3.10 harness later passed **248 Windows checks** alongside package-update tests. Those totals belong to their respective versions and are not a count or acceptance result for 1.3.11. Real-client Now Playing navigation remained separate from those automated fixtures.

The validated track-link API remains internally available and tested. In 1.3.11, click/Enter/Space calls the separate Lyrics Plus action and scoped Alt+Shift+J retains the native Now Playing API. Windows SMTC [media properties](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmediaproperties) do not provide a Spotify catalog track URI; the Lyrics Plus page obtains its current item inside Spotify instead.

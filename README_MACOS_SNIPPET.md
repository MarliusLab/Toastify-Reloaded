### macOS — RC1 source, stable validation pending

The version configured in source is `v1.5.0-macos-rc.1`. The repository already
contains this tag; the changes in this branch do not move tags or publish a
release. Package names below describe the packaging output, not a claim that
RC1 downloads have passed all validation.

**Apple Silicon (arm64)**

- `ToastifyReloaded-macOS-arm64.dmg`
- `ToastifyReloaded-macOS-arm64.zip`

**Intel (x64)**

- `ToastifyReloaded-macOS-x64.dmg`
- `ToastifyReloaded-macOS-x64.zip`

The macOS port retains its existing settings, themes, tray actions, playback
controls, global hotkeys, autostart, Spicetify repair and updater.

Click the title or artist in a toast, or focus either with Tab and press
Enter/Space, to open the displayed track in Spotify. Both labels open the same
track, using the ID obtained with its AppleScript metadata. A track change
during metadata collection discards that snapshot, and a later click uses the
immutable ID attached to the toast rather than resolving a newer song by title.

Only a validated Spotify catalog track ID becomes a link. Opening first uses
`/usr/bin/open -a Spotify spotify:track:…`, then the canonical
`https://open.spotify.com/track/…` URL if the native launcher reports failure.
Missing IDs, local files, unsupported media and invalid URLs only activate
Spotify; Toastify never searches by title, executes metadata, or calls a
play-track command. If all exact-track launchers fail, activating the application
is the final safe fallback. The toast reports app-only fallback or failure and
stays visible while opening and briefly after the result.

`open` accepting a URL confirms dispatch, not that Spotify navigated correctly.
Live checks on both Intel and Apple Silicon Macs remain necessary: exact-track
navigation, app-only fallback, unchanged playback/queue, long title layout,
keyboard focus, permission prompts, tray/hotkeys, update installation, and
ZIP/DMG launch after download. The automated self-test covers metadata parsing,
strict identifiers, native/web/app fallback order, launcher failures, settings
roundtrips and version ordering. It now validates the current macOS tag against
the assembly version instead of requiring obsolete Preview 1.

macOS requires Automation permission for Spotify control and Accessibility
permission for global hotkeys. Stable promotion, Developer ID signing and
notarization remain separate steps and require an explicit release decision.

# Windows toast activation

Windows 1.3.8 adds the missing normal-click action to the WPF toast. Click its title, artist, artwork or background to open Spotify. Focus either text line and press Enter or Space for the same action. Ctrl+drag retains the existing toast movement behavior.

The opener restores minimized Spotify windows and requests foreground activation. If no window is available, it uses the registered `spotify:` handler and retries activation briefly. Windows may restrict foreground activation; acceptance still requires an interactive check with the installed Spotify client.

## Track identity and safe fallbacks

The current Windows backend uses `GlobalSystemMediaTransportControlsSessionMediaProperties`. Its [documented properties](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmediaproperties) contain title, artist, album and thumbnail, but no catalog URI. Production Windows snapshots therefore leave `TrackUri` unset and activate Spotify. This does not establish exact-track navigation for Windows.

The opener accepts an optional trusted track URI, validates it with the same strict shared parser used on Linux/macOS, and tries the canonical Spotify URI followed by its canonical HTTPS URL. Text such as title/artist is never used to guess an identifier or launch an arbitrary URL. Each toast retains its own immutable snapshot.

## Verification

Automated checks cover both text lines and artwork through real WPF routed events, keyboard activation, snapshot forwarding, repeated clicks, error/retry and cancellation on close. Service fixtures test safe fallback order and malformed identifiers without launching the user's Spotify client.

Local Windows validation passed: Release build (zero warnings/errors), 109 Windows regression checks, 140 shared track/version assertions, 14 release guard tests, classic WPF UI invariants and Linux/macOS structural parity. The timed-toast regression keeps a 500ms toast alive during a pending activation while its timeline advances, then verifies normal closure afterward. Native Spotify foreground behavior has not been interactively accepted on this host.

Interactive acceptance: play a song in Spotify, minimize its window, then click the title or artist of a newly displayed toast. Confirm Spotify is restored. Also check Ctrl+drag, a closed Spotify client, and absence of an installed protocol handler. Automated checks do not replace this real-client acceptance.

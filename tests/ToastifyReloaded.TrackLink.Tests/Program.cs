using ToastifyReloaded.Shared;
using Linux = ToastifyReloaded.Linux.Services;
using Mac = ToastifyReloaded.Mac.Services;

if (await CrossMaintenanceTests.TryRunChildAsync(args)) return;

const string id = "4uLU6hMCjMI75M1A2tKUQC";
const string uri = "spotify:track:" + id;
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS: " + name);
}

Check(WindowsReleaseVersion.Normalize("v.1.3.5") == "1.3.5", "legacy dotted-v Windows release tag is recognized");
Check(WindowsReleaseVersion.Normalize("v1.3.6") == "1.3.6", "current Windows release tag is recognized");
Check(WindowsReleaseVersion.Normalize(" V.1.3.5 ") == "1.3.5", "Windows version prefix is case-insensitive and trimmed");
Check(WindowsReleaseVersion.Normalize("1.3.6.0") == "1.3.6", "Windows assembly revision normalizes to release version");
Check(WindowsReleaseVersion.Normalize("v1.3.6-preview.1") == "1.3.6", "Windows version normalization retains prior suffix behavior");
Check(Version.Parse(WindowsReleaseVersion.Normalize("v.1.3.5")) <
    Version.Parse(WindowsReleaseVersion.Normalize("v1.3.6")), "Windows update comparison works across legacy and current tags");
Check(!Version.TryParse(WindowsReleaseVersion.Normalize("v..1.3.5"), out _), "malformed repeated Windows tag prefix remains invalid");
Check(!Version.TryParse(WindowsReleaseVersion.Normalize("invalid-tag"), out _), "non-version Windows tags remain invalid");

var valid = new[]
{
    uri, " SPOTIFY:TRACK:" + id + " ",
    "https://open.spotify.com/track/" + id,
    "https://open.spotify.com/track/" + id + "?si=share&utm_source=toast#ignored",
    "https://open.spotify.com/intl-it/track/" + id,
    "/com/spotify/track/" + id
};
foreach (var value in valid)
    Check(SpotifyTrackLink.TryGetTrackUri(value) == uri, "canonical identifier: " + value);

string?[] invalid =
{
    null, "", "  ", id, "spotify:", "spotify:track:", "spotify:track:short",
    "spotify:track:" + id + "x", "spotify:track:" + id + ":extra",
    "spotify:album:" + id, "spotify:episode:" + id, "spotify:local:artist:album:title:123",
    "/org/mpris/MediaPlayer2/TrackList/NoTrack", "/com/spotify/track/short",
    "https://open.spotify.com.evil.test/track/" + id,
    "https://open.spotify.com@evil.test/track/" + id,
    "https://evil.test@open.spotify.com/track/" + id,
    "http://open.spotify.com/track/" + id, "https://open.spotify.com:8443/track/" + id,
    "https://open.spotify.com/album/" + id, "https://open.spotify.com/track/" + id + "/extra",
    "https://open.spotify.com/track/%34uLU6hMCjMI75M1A2tKUQC",
    "https://open.spotify.com/track/../track/" + id,
    "file:///tmp/music", "javascript:alert(1)", "--help", "$(touch /tmp/nope)",
    uri + "\n", uri + "\0", uri + "\u001fhttps://evil.test",
    "https://open.spotify.com/track/" + id + "?si=bad\narg",
    new string('x', 4097)
};
foreach (var value in invalid)
{
    Check(SpotifyTrackLink.TryGetTrackUri(value) is null, "reject unsafe or unsupported identifier #" + passed);
    Check(SpotifyTrackLink.GetOpenTargets(value).Count == 0, "no launch target for invalid identifier #" + passed);
}

var targets = SpotifyTrackLink.GetOpenTargets("https://open.spotify.com/track/" + id + "?si=anything");
Check(targets.SequenceEqual(new[] { uri, "https://open.spotify.com/track/" + id }),
    "native Spotify first, canonical HTTPS fallback with metadata query removed");
// Exercise the actual production parsers and open services, using fake process
// results. These tests run on every OS without Spotify or a graphical session.
const string otherId = "0VjIjW4GlUZAMYd2vXMi3b";
string Snapshot(string url, string path) => string.Join('\u001f',
    "Same song", "Same artist", "Album", "180000000", "", url, path);
var linuxTrack = Linux.PlayerctlService.ParseTrackSnapshot(Snapshot(uri, "/com/spotify/track/" + id), 12, true);
Check(linuxTrack?.TrackUri == uri && linuxTrack.DurationSeconds == 180 && linuxTrack.PositionSeconds == 12,
    "Linux title and ID from one metadata snapshot");
Check(Linux.PlayerctlService.ParseTrackSnapshot(Snapshot("", "/com/spotify/track/" + id), 0, false)?.TrackUri == uri,
    "Linux uses recognized Spotify MPRIS path if URL is absent");
Check(Linux.PlayerctlService.ParseTrackSnapshot(Snapshot("https://evil.test", "/com/spotify/track/" + id), 0, false)?.TrackUri == uri,
    "Linux ignores untrusted URL and uses verified Spotify path");
var missing = Linux.PlayerctlService.ParseTrackSnapshot(Snapshot("", ""), 0, false);
Check(missing is not null && missing.TrackUri is null, "Linux clears old URI when next snapshot omits ID");
var conflict = Linux.PlayerctlService.ParseTrackSnapshot(Snapshot(uri, "/com/spotify/track/" + otherId), 0, true);
Check(conflict is not null && conflict.TrackUri is null, "Linux refuses conflicting IDs");
Check(Linux.PlayerctlService.ParseTrackSnapshot(Snapshot(uri, "") + "\u001finjection", 0, true) is null,
    "Linux rejects delimiter injection");
Check(Linux.PlayerctlService.ParseTrackSnapshot((Snapshot(uri, "") + "\u001ftoastify-mpris-v1").Trim(), 0, true)?.TrackUri == uri,
    "Linux framed metadata retains valid URL when final trackid is empty");
Check(linuxTrack!.Identity != (linuxTrack with { TrackUri = "spotify:track:" + otherId }).Identity,
    "Linux distinguishes recordings with identical labels");

string MacPayload(string trackUri) => string.Join('\u001f',
    "Same song", "Same artist", "Album", "180000", "12", "playing", "", trackUri, "toastify-track-v1");
var macTrack = Mac.SpotifyAppleScriptService.ParseTrackPayload(MacPayload(uri));
Check(macTrack?.TrackUri == uri && macTrack.DurationSeconds == 180 && macTrack.PositionSeconds == 12,
    "Mac title and ID from one AppleScript payload");
Check(Mac.SpotifyAppleScriptService.ParseTrackPayload(MacPayload(""))?.TrackUri == "",
    "Mac missing ID cannot reuse a previous ID");
Check(Mac.SpotifyAppleScriptService.ParseTrackPayload(MacPayload(uri + "\n"))?.TrackUri == "",
    "Mac preserves and rejects control characters in ID");
Check(Mac.SpotifyAppleScriptService.ParseTrackPayload(MacPayload(uri) + "\u001finjection") is null,
    "Mac rejects delimiter injection");
Check(macTrack!.Identity != (macTrack with { TrackUri = "spotify:track:" + otherId }).Identity,
    "Mac distinguishes recordings with identical labels");

var changes = new SpotifyTrackChangeTracker();
Check(changes.Update(uri, "Song", "Artist", "Album"), "first snapshot triggers toast");
Check(!changes.Update(null, "Song", "Artist", "Album"), "temporary missing ID does not duplicate toast");
Check(!changes.Update(uri, "Song", "Artist", "Album"), "restored ID does not duplicate toast");
Check(!changes.Update(null, "Song", "Artist", "Album"), "another missing ID keeps notification state only");
Check(changes.Update("spotify:track:" + otherId, "Song", "Artist", "Album"),
    "new recording with identical labels is detected after missing ID");
Check(changes.Update(null, "Other song", "Artist", "Album"), "label change without ID triggers toast");
Check(!changes.Update(uri, "Other song", "Artist", "Album"), "late ID supplements notification state without extra toast");

var calls = new List<(string File, string[] Args)>();
var responses = new Queue<int>();
Task<(int ExitCode, string StdOut, string StdErr)> Run(string file, IEnumerable<string> args, CancellationToken token)
{
    Check(token.CanBeCanceled, "launcher receives timeout token");
    calls.Add((file, args.ToArray()));
    return Task.FromResult((responses.Count > 0 ? responses.Dequeue() : 1, "", ""));
}
void Reset(params int[] codes) { calls.Clear(); responses.Clear(); foreach (var code in codes) responses.Enqueue(code); }

var linuxOpener = new Linux.SpotifyTrackOpenService(Run, token =>
{
    Check(token.CanBeCanceled, "Linux player resolution receives timeout token");
    return Task.FromResult<string?>("spotify");
});
Reset(0);
Check(await linuxOpener.OpenAsync(uri) == Linux.SpotifyTrackOpenResult.TrackOpened && calls.Count == 1 &&
    calls[0].File == "xdg-open" && calls[0].Args.SequenceEqual(new[] { uri }), "Linux native URI in single argument");
Reset(1, 0);
Check(await linuxOpener.OpenAsync(uri) == Linux.SpotifyTrackOpenResult.TrackOpened && calls.Count == 2 &&
    calls[1].Args.SequenceEqual(new[] { targets[1] }), "Linux falls back to canonical HTTPS");
Reset(1, 1, 0);
Check(await linuxOpener.OpenAsync(uri) == Linux.SpotifyTrackOpenResult.AppActivated && calls[2].File == "gdbus" &&
    calls[2].Args.Contains("org.mpris.MediaPlayer2.Raise"), "Linux raises Spotify after track handlers fail");
Reset(0);
Check(await linuxOpener.OpenAsync("file:///untrusted") == Linux.SpotifyTrackOpenResult.AppActivated &&
    calls.Count == 1 && calls[0].File == "gdbus", "Linux missing/invalid ID only raises Spotify");
Reset(1, 0);
Check(await linuxOpener.OpenAsync(null) == Linux.SpotifyTrackOpenResult.AppActivated &&
    calls[1].Args.SequenceEqual(new[] { "spotify:" }), "Linux app URI after MPRIS raise failure");
Reset(1, 1, 1, 1);
Check(await linuxOpener.OpenAsync(uri) == Linux.SpotifyTrackOpenResult.Failed && calls.Count == 4,
    "Linux reports exhausted fallback chain");
var linuxException = new Linux.SpotifyTrackOpenService((_, _, _) => throw new IOException("missing launcher"),
    _ => throw new IOException("player vanished"));
Check(await linuxException.OpenAsync(uri) == Linux.SpotifyTrackOpenResult.Failed, "Linux launcher exceptions do not escape");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try
    {
        await new Linux.PlayerctlService(new Linux.ProcessService()).ResolveSpotifyPlayerAsync(cancellationToken: cancelled.Token);
        Check(false, "pre-cancelled player resolution must not start a helper");
    }
    catch (OperationCanceledException)
    {
        Check(true, "pre-cancelled player resolution does not start a helper");
    }
}

var macOpener = new Mac.SpotifyTrackOpenService(Run);
Reset(0);
var macOpened = await macOpener.OpenAsync(uri);
Check(macOpened.Success && macOpened.OpenedTrack && calls.Count == 1 && calls[0].File == "/usr/bin/open" &&
    calls[0].Args.SequenceEqual(new[] { "-a", "Spotify", uri }), "Mac explicit Spotify app and one URI argument");
Reset(1, 0);
macOpened = await macOpener.OpenAsync(uri);
Check(macOpened.Success && macOpened.OpenedTrack && calls.Count == 2 && calls[1].Args.SequenceEqual(new[] { targets[1] }),
    "Mac canonical HTTPS fallback");
Reset(1, 1, 0);
macOpened = await macOpener.OpenAsync(uri);
Check(macOpened.Success && !macOpened.OpenedTrack && calls[2].Args.SequenceEqual(new[] { "-a", "Spotify" }),
    "Mac app activation when track handlers fail");
Reset(0);
macOpened = await macOpener.OpenAsync("javascript:alert(1)");
Check(macOpened.Success && !macOpened.OpenedTrack && calls.Count == 1 &&
    calls[0].Args.SequenceEqual(new[] { "-a", "Spotify" }), "Mac unsafe metadata never becomes a launch target");
Reset(1, 1, 1);
Check(!(await macOpener.OpenAsync(uri)).Success && calls.Count == 3, "Mac reports exhausted fallback chain");
var macException = new Mac.SpotifyTrackOpenService((_, _, _) => throw new OperationCanceledException());
Check(!(await macException.OpenAsync(uri)).Success, "Mac canceled launcher is crash-safe");
await LinuxLyricsTests.RunAsync(Check);
await MacLyricsTests.RunAsync(Check);
await MacUpdateHandoffTests.RunAsync(Check);
await CrossMaintenanceTests.RunAsync(Check);
Console.WriteLine($"TRACK LINK TEST RESULT: PASS ({passed})");

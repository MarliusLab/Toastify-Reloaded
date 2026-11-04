using ToastifyReloaded.Mac.Services;

internal static class MacLyricsTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var calls = new List<(string File, string[] Arguments)>();
        var responses = new Queue<int>();
        Task<(int ExitCode, string StdOut, string StdErr)> Run(string file, IEnumerable<string> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls.Add((file, arguments.ToArray()));
            return Task.FromResult((responses.Count > 0 ? responses.Dequeue() : 0, file.EndsWith("osascript") ? "requested" : "", ""));
        }
        var guardCalls = 0;
        Task<bool> Released(CancellationToken token)
        { token.ThrowIfCancellationRequested(); guardCalls++; return Task.FromResult(true); }
        var opener = new SpotifyTrackOpenService(Run, Released);
        var result = await opener.OpenLyricsPlusAsync();
        check(result.Success && !result.OpenedTrack && result.Message == "LyricsPlusRequested" && calls.Count == 1 &&
              calls[0].File == "/usr/bin/open" && calls[0].Arguments.SequenceEqual(new[] { "-a", "Spotify", "spotify:app:lyrics-plus" }),
            "macOS Lyrics Plus dispatches only its fixed URI in literal open arguments without a track or playback command");
        check(guardCalls == 1, "macOS Lyrics Plus waits for physical trigger release before protocol/focus handoff");

        calls.Clear(); responses.Enqueue(1); responses.Enqueue(0);
        result = await opener.OpenLyricsPlusAsync();
        check(result.Message == "LyricsPlusActivatedFallback" && calls.Count == 2 &&
              calls[1].Arguments.SequenceEqual(new[] { "-a", "Spotify" }),
            "macOS rejected Lyrics Plus route falls back to Spotify activation with a distinct result");
        calls.Clear(); responses.Enqueue(1); responses.Enqueue(1);
        check(!(await opener.OpenLyricsPlusAsync()).Success && calls.Count == 2,
            "macOS reports exhausted Lyrics Plus activation fallbacks without a browser or metadata search");

        calls.Clear();
        var held = new SpotifyTrackOpenService(Run, _ => Task.FromResult(false));
        check(!(await held.OpenLyricsPlusAsync()).Success && calls.Count == 0,
            "macOS held input blocks Lyrics Plus before any launcher or focus request");

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            check(await CaptureAsync(() => opener.OpenLyricsPlusAsync(canceled.Token)) is OperationCanceledException && calls.Count == 0,
                "macOS canceled Lyrics Plus does not launch any helper");
        }
        var waiting = new SpotifyTrackOpenService(Run, async token => { await Task.Delay(Timeout.Infinite, token); return true; });
        using (var canceled = new CancellationTokenSource(30))
            check(await CaptureAsync(() => waiting.OpenLyricsPlusAsync(canceled.Token)) is OperationCanceledException && calls.Count == 0,
                "macOS caller cancellation bounds a pending input-release guard");

        var launcherSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new SpotifyTrackOpenService(async (_, _, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); return (0, "", ""); }
            finally { launcherSettled.TrySetResult(); }
        }, Released);
        using (var canceled = new CancellationTokenSource(30))
        {
            var error = await CaptureAsync(() => blocked.OpenLyricsPlusAsync(canceled.Token));
            await launcherSettled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(error is OperationCanceledException,
                "macOS canceled native Lyrics request settles its helper without starting a fallback");
        }

        calls.Clear(); guardCalls = 0;
        result = await opener.OpenNowPlayingAsync();
        check(result.Message == "NowPlayingRequested" && calls.Count == 2 && calls[0].Arguments.SequenceEqual(new[] { "-a", "Spotify" }) &&
              calls[1].File == "/usr/bin/osascript" && calls[1].Arguments.Length == 2 && calls[1].Arguments[0] == "-e" &&
              calls[1].Arguments[1].Contains("if frontmost then") && calls[1].Arguments[1].Contains("keystroke \"j\" using {option down, shift down}"),
            "macOS Now Playing remains separate and forwards only its documented shortcut after Spotify foreground verification");
        check(guardCalls == 2, "macOS Now Playing rechecks physical release after activation and before forwarding");
        calls.Clear(); guardCalls = 0;
        var changedInput = new SpotifyTrackOpenService(Run, _ => Task.FromResult(++guardCalls == 1));
        result = await changedInput.OpenNowPlayingAsync();
        check(result.Message == "SpotifyActivated" && calls.Count == 1,
            "macOS input pressed during activation prevents Now Playing shortcut forwarding");
        calls.Clear();
        var denied = new SpotifyTrackOpenService((_, _, _) => Task.FromResult((1, "", "permission denied")), Released);
        check(!(await denied.OpenNowPlayingAsync()).Success,
            "macOS permission/activation refusal cannot be reported as confirmed Now Playing");
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }
}

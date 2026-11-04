using ToastifyReloaded.Linux.Services;
using ToastifyReloaded.Shared;

internal static class LinuxLyricsTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var calls = new List<(string File, string[] Args)>();
        var responses = new Queue<int>();
        Task<(int ExitCode, string StdOut, string StdErr)> Run(string file, IEnumerable<string> args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); calls.Add((file, args.ToArray()));
            return Task.FromResult((responses.Count == 0 ? 0 : responses.Dequeue(), "", ""));
        }
        var opener = new SpotifyTrackOpenService(Run, _ => Task.FromResult<string?>("spotify"));
        check(await opener.OpenLyricsPlusAsync() == SpotifyTrackOpenResult.LyricsPlusRequested && calls.Count == 1 &&
              calls[0].File == "xdg-open" && calls[0].Args.SequenceEqual(new[] { "spotify:app:lyrics-plus" }),
            "Linux Lyrics Plus uses one fixed URI argument without track metadata or playback commands");
        calls.Clear(); responses.Enqueue(1); responses.Enqueue(0);
        check(await opener.OpenLyricsPlusAsync() == SpotifyTrackOpenResult.AppActivated && calls.Count == 2 &&
              calls[1].File == "gdbus" && calls[1].Args.Last() == "org.mpris.MediaPlayer2.Raise",
            "Linux unavailable Lyrics route falls back to MPRIS activation with a distinct result");
        calls.Clear(); responses.Enqueue(1); responses.Enqueue(1); responses.Enqueue(1);
        check(await opener.OpenLyricsPlusAsync() == SpotifyTrackOpenResult.Failed && calls[^1].Args.SequenceEqual(new[] { "spotify:" }),
            "Linux failed Lyrics handoff uses only a fixed Spotify app fallback");
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); calls.Clear();
        try { await opener.OpenLyricsPlusAsync(canceled.Token); check(false, "Linux canceled request must stop"); }
        catch (OperationCanceledException) { check(calls.Count == 0, "Linux canceled Lyrics request starts no helper"); }
        var stalled = new SpotifyTrackOpenService(async (_, _, token) =>
        { await Task.Delay(Timeout.Infinite, token); return (0, "", ""); }, _ => Task.FromResult<string?>(null));
        using var during = new CancellationTokenSource(30);
        try { await stalled.OpenLyricsPlusAsync(during.Token); check(false, "Linux cancellation must propagate"); }
        catch (OperationCanceledException) { check(true, "Linux helper cancellation propagates without starting fallback"); }
        const string currentUri = "spotify:track:0123456789ABCDEFGHIJKL";
        var current = new SpotifyTrackOpenService(Run, _ => Task.FromResult<string?>("spotify"),
            _ => Task.FromResult<string?>(currentUri));
        calls.Clear();
        check(await current.OpenCurrentTrackAsync() == SpotifyTrackOpenResult.TrackOpened && calls.Count == 1 &&
              calls[0].Args.SequenceEqual(new[] { currentUri }),
            "Linux Now Playing equivalent opens fresh validated MPRIS identity without playback or synthetic keys");
        calls.Clear();
        check(await opener.OpenCurrentTrackAsync() == SpotifyTrackOpenResult.AppActivated && calls.Count == 1 &&
              calls[0].File == "gdbus" && calls[0].Args.Last() == "org.mpris.MediaPlayer2.Raise",
            "Linux missing current identity safely raises Spotify instead of reusing a displayed stale track");
        var keyboard = new ToastKeyboardNavigation();
        check(keyboard.KeyDown("Enter", false) && keyboard.KeyDown("Enter", false) &&
              keyboard.KeyUp("Enter", false) == ToastKeyboardAction.LyricsPlus &&
              keyboard.KeyUp("Enter", false) == ToastKeyboardAction.None,
            "Linux Enter down and repeats are consumed; one Lyrics request occurs only on release");
        keyboard.KeyDown("Space", false);
        check(keyboard.KeyUp("Space", true) == ToastKeyboardAction.None &&
              keyboard.KeyUp("LeftShift", false) == ToastKeyboardAction.LyricsPlus,
            "Linux keyboard navigation waits for held modifiers after the activation key releases");
        keyboard.KeyDown("J", true);
        check(keyboard.KeyUp("J", true) == ToastKeyboardAction.None &&
              keyboard.KeyUp("LeftAlt", false) == ToastKeyboardAction.CurrentTrack,
            "Linux local Alt Shift J remains a separate current-track action after modifier release");
        keyboard.KeyDown("Enter", false); keyboard.Reset();
        check(keyboard.KeyUp("Enter", false) == ToastKeyboardAction.None && !keyboard.KeyDown("J", false),
            "Linux focus loss clears pending activation and ordinary J remains untouched");
        check(ToastKeyboardNavigation.IsCurrentTrackChord("Shift+ALT+j") &&
              !ToastKeyboardNavigation.IsCurrentTrackChord("Ctrl+Alt+Shift+J") &&
              !ToastKeyboardNavigation.IsCurrentTrackChord("Alt+Shift+K"),
            "Linux temporary shortcut matching masks only the exact chord, preserving other saved bindings");
        using var controller = new ToastShowHotkeyController();
        using var gate = new ToastNavigationGate();
        var shown = 0; var lyrics = 0; var visible = false;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lease = controller.Attach(() => visible, () => gate.RunAsync(async () => { lyrics++; await pending.Task; }));
        await controller.ExecuteAsync(() => { shown++; visible = true; return Task.CompletedTask; });
        var request = controller.ExecuteAsync(() => { shown++; return Task.CompletedTask; });
        await controller.ExecuteAsync(() => { shown++; return Task.CompletedTask; });
        check(shown == 1 && lyrics == 1 && !request.IsCompleted, "Linux two-press Show Toast keeps one visible owner and consumes repeats");
        check(!await gate.RunAsync(() => Task.CompletedTask), "Linux overlapping toasts share one navigation gate");
        visible = false; pending.SetResult(); await request;
        await controller.ExecuteAsync(() => { shown++; return Task.CompletedTask; });
        check(shown == 2, "Linux hidden or closed owners release the next Show Toast request");
        await TestLegacyRestartAsync(check);
    }

    private static async Task TestLegacyRestartAsync(Action<bool, string> check)
    {
        var allowed = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(Path.Combine(allowed, "linux-restart-fixture-" + Guid.NewGuid().ToString("N")));
        if (!root.StartsWith(allowed, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid fixture path.");
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "fake '$(never-execute)' app");
            var start = LinuxRestartHandoff.CreateStartInfo(executable, 123, true, root);
            check(start.FileName == "/bin/sh" && !start.UseShellExecute && start.Arguments == "" &&
                  start.ArgumentList.Count == 4 && start.ArgumentList[1] == "123" && start.ArgumentList[2] == executable && start.ArgumentList[3] == "1",
                "Linux legacy restart passes exact executable, PID and minimized flag as literal arguments");
            check(File.ReadAllText(start.ArgumentList[0]) == LinuxRestartHandoff.Script.Replace("\r\n", "\n") + "\n" &&
                  !File.ReadAllText(start.ArgumentList[0]).Contains(executable),
                "Linux restart helper contains no interpolated paths and waits boundedly before exec");
            try { LinuxRestartHandoff.CreateStartInfo(executable, 0, false, root); check(false, "invalid old PID must fail"); }
            catch (ArgumentException) { check(true, "Linux restart rejects an invalid PID before creating a helper"); }
            try { LinuxRestartHandoff.CreateStartInfo("relative-app", 123, false, root); check(false, "relative app must fail"); }
            catch (ArgumentException) { check(true, "Linux restart rejects a relative executable"); }
            if (!OperatingSystem.IsLinux())
            {
                Console.WriteLine("SKIP: native Linux restart timing fixture requires Linux; no app or installer started on this host.");
                return;
            }
            File.WriteAllText(executable, "#!/bin/sh\nprintf '%s' \"$1\" > \"$0.receipt\"\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var oldProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = "/bin/sleep", UseShellExecute = false, ArgumentList = { "10" } })!;
            using var restart = System.Diagnostics.Process.Start(LinuxRestartHandoff.CreateStartInfo(executable, oldProcess.Id, true, root))!;
            try
            {
                await Task.Delay(200);
                check(!File.Exists(executable + ".receipt") && !restart.HasExited,
                    "Linux native restart helper never launches while the old synthetic process is alive");
                oldProcess.Kill();
                await oldProcess.WaitForExitAsync();
                await restart.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                check(restart.ExitCode == 0 && File.ReadAllText(executable + ".receipt") == "--minimized",
                    "Linux native helper starts the isolated fake app only after old-process exit and preserves minimized mode");
            }
            finally
            {
                if (!oldProcess.HasExited) { oldProcess.Kill(); await oldProcess.WaitForExitAsync(); }
                if (!restart.HasExited) { restart.Kill(); await restart.WaitForExitAsync(); }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

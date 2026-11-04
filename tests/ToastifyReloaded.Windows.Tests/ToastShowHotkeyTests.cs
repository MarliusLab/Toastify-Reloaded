using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ToastifyReloaded.Models;
using ToastifyReloaded.Services;

namespace ToastifyReloaded.Windows.Tests;

internal static class ToastShowHotkeyTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var controller = new ToastShowHotkeyController();
        var shows = 0;
        Task Show() { shows++; return Task.CompletedTask; }
        var fetching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = controller.ExecuteAsync(async () => { shows++; await fetching.Task; });
        await controller.ExecuteAsync(Show);
        check(shows == 1 && !initial.IsCompleted,
            "Show Toast repeats cannot overlap the initial metadata fetch before a toast becomes visible");
        fetching.SetResult();
        await initial;

        var firstLyrics = 0;
        var secondLyrics = 0;
        var nowPlaying = 0;
        var first = Create(_ => { firstLyrics++; return Task.FromResult(SpotifyTrackOpenResult.LyricsPlusRequested); },
            _ => { nowPlaying++; return Task.FromResult(SpotifyTrackOpenResult.NowPlayingRequested); });
        var second = Create(_ => { secondLyrics++; return Task.FromResult(SpotifyTrackOpenResult.LyricsPlusRequested); },
            _ => { nowPlaying++; return Task.FromResult(SpotifyTrackOpenResult.NowPlayingRequested); });
        Attach(controller, first);
        Attach(controller, second);
        first.Show(); second.Show();
        try
        {
            await controller.ExecuteAsync(Show);
            check(firstLyrics == 0 && secondLyrics == 1 && nowPlaying == 0 && shows == 1,
                "the second Show Toast press requests Lyrics Plus on the newest visible toast without making another");
            second.Hide();
            await controller.ExecuteAsync(Show);
            check(firstLyrics == 1 && secondLyrics == 1 && shows == 1,
                "a hidden newer toast yields the Show Toast second action to the remaining visible toast");
            second.Show();
            await controller.ExecuteAsync(Show);
            check(secondLyrics == 2 && firstLyrics == 1,
                "showing the newest toast again restores its second-press ownership");
            second.Close();
            await controller.ExecuteAsync(Show);
            check(firstLyrics == 2 && secondLyrics == 2 && shows == 1,
                "closing the newest toast cleans its lease and leaves the older visible owner usable");
            first.Hide();
            await controller.ExecuteAsync(Show);
            check(shows == 2 && firstLyrics == 2 && secondLyrics == 2,
                "when every toast is hidden the configured shortcut returns to showing a toast");
        }
        finally { first.Close(); second.Close(); }
        await controller.ExecuteAsync(Show);
        check(shows == 3, "after every toast closes the next shortcut press shows a fresh toast");

        var raceLyrics = 0;
        var race = Create(_ => { raceLyrics++; return Task.FromResult(SpotifyTrackOpenResult.LyricsPlusRequested); });
        using var raceLease = controller.Attach(() => race.CanHandleLyricsPlusRequest, () =>
        {
            race.Close();
            return race.RequestLyricsPlusAsync();
        });
        race.Show();
        await controller.ExecuteAsync(Show);
        check(raceLyrics == 0 && shows == 3,
            "a toast closing between owner selection and the Lyrics request consumes the press without a duplicate popup");

        var expired = Create(_ => Task.FromResult(SpotifyTrackOpenResult.LyricsPlusRequested), settings: new AppSettings
        {
            ToastDurationMs = 500, ToastAnimationStyle = "None", ToastFadeInMs = 0, ToastFadeOutMs = 0
        });
        Attach(controller, expired);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        expired.Closed += (_, _) => closed.TrySetResult();
        expired.ContentRendered += (_, _) => expired.Opacity = 0;
        expired.ShowTimed();
        try
        {
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await controller.ExecuteAsync(Show);
            check(shows == 4 && !expired.IsVisible,
                "an expired timed toast releases second-press ownership so the shortcut shows another");
        }
        finally { if (!closed.Task.IsCompleted) expired.Close(); }

        await VerifySharedGateAsync(check);
        controller.Dispose();
        await controller.ExecuteAsync(Show);
        check(shows == 4, "disposing the Show Toast controller prevents later hotkey work");
    }

    private static async Task VerifySharedGateAsync(Action<bool, string> check)
    {
        long clock = 0;
        var registered = false;
        using var hotkeys = new ToastNowPlayingHotkeyCoordinator(_ => { registered = true; return true; },
            () => registered = false, () => new Scope(),
            duration => { clock += (long)duration.TotalMilliseconds; return Task.CompletedTask; }, () => clock);
        using var controller = new ToastShowHotkeyController();
        var shows = 0;
        Task Show() { shows++; return Task.CompletedTask; }
        var firstLyrics = 0;
        var secondLyrics = 0;
        var nowPlaying = 0;
        var pendingNowPlaying = new TaskCompletionSource<SpotifyTrackOpenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingLyrics = new TaskCompletionSource<SpotifyTrackOpenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Create(_ => { firstLyrics++; return Task.FromResult(SpotifyTrackOpenResult.LyricsPlusRequested); },
            token => { nowPlaying++; return pendingNowPlaying.Task.WaitAsync(token); }, hotkeys);
        var second = Create(token => { secondLyrics++; return pendingLyrics.Task.WaitAsync(token); },
            _ => Task.FromResult(SpotifyTrackOpenResult.NowPlayingRequested), hotkeys);
        Attach(controller, first); Attach(controller, second);
        first.Show(); second.Show();
        try
        {
            RaiseNowPlaying(first);
            await controller.ExecuteAsync(Show);
            check(nowPlaying == 1 && secondLyrics == 0 && shows == 0,
                "a visible toast consumes repeated Show Toast during pending Now Playing without overlapping it or adding a popup");
            pendingNowPlaying.SetResult(SpotifyTrackOpenResult.NowPlayingRequested);
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!registered && DateTime.UtcNow < deadline) await Task.Delay(20);
            var lyricsRequest = controller.ExecuteAsync(Show);
            check(secondLyrics == 1 && !lyricsRequest.IsCompleted && shows == 0,
                "after Now Playing finishes the same Show Toast shortcut can request Lyrics Plus through the shared gate");
            RaiseNowPlaying(first);
            await controller.ExecuteAsync(Show);
            check(nowPlaying == 1 && secondLyrics == 1 && shows == 0,
                "pending second-press Lyrics Plus blocks concurrent Now Playing and further Show Toast dispatches");
            second.Close();
            await lyricsRequest.WaitAsync(TimeSpan.FromSeconds(3));
            await controller.ExecuteAsync(Show);
            check(firstLyrics == 1 && secondLyrics == 1 && shows == 0,
                "closing a toast with pending Lyrics cancels its request and restores the remaining visible owner's shortcut");
        }
        finally
        {
            pendingNowPlaying.TrySetResult(SpotifyTrackOpenResult.NowPlayingRequested);
            pendingLyrics.TrySetResult(SpotifyTrackOpenResult.LyricsPlusRequested);
            first.Close(); second.Close();
        }
    }

    private static ToastWindow Create(Func<CancellationToken, Task<SpotifyTrackOpenResult>> lyrics,
        Func<CancellationToken, Task<SpotifyTrackOpenResult>>? nowPlaying = null,
        ToastNowPlayingHotkeyCoordinator? hotkeys = null, AppSettings? settings = null)
    {
        var toast = new ToastWindow(TrackInfo.Empty, settings ?? new AppSettings(), lyrics,
            () => ModifierKeys.Alt | ModifierKeys.Shift, hotkeys,
            openNowPlaying: nowPlaying ?? (_ => Task.FromResult(SpotifyTrackOpenResult.NowPlayingRequested)))
        { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, Topmost = false };
        toast.Loaded += (_, _) => { toast.Left = -10000; toast.Top = -10000; toast.Opacity = 0; };
        return toast;
    }

    private static void Attach(ToastShowHotkeyController controller, ToastWindow toast)
    {
        var lease = controller.Attach(() => toast.CanHandleLyricsPlusRequest, toast.RequestLyricsPlusAsync);
        toast.Closed += (_, _) => lease.Dispose();
    }

    private static void RaiseNowPlaying(ToastWindow toast)
        => toast.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(toast)!,
            Environment.TickCount, Key.J) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    private sealed class Scope : IDisposable { public void Dispose() { } }
}

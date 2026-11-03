using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ToastifyReloaded.Models;
using ToastifyReloaded.Services;

namespace ToastifyReloaded.Windows.Tests;

internal static class ToastTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const string uri = "spotify:track:4uLU6hMCjMI75M1A2tKUQC";
        var settings = new AppSettings { ApplicationLanguage = "Italian", ToastTitlesOrder = "ArtistTrack" };
        var calls = new List<string?>();
        var pending = new TaskCompletionSource<SpotifyTrackOpenResult>();
        var window = new ToastWindow(new TrackInfo("Song", "Artist", "Album", TrackUri: uri), settings,
            (target, _) => { calls.Add(target); return pending.Task; }) { Opacity = 0, ShowActivated = false };
        window.Loaded += (_, _) => { window.Left = -10000; window.Top = -10000; };
        window.Show();
        try
        {
            var title = (TextBlock)window.FindName("Title1");
            var artist = (TextBlock)window.FindName("Title2");
            check(title.Text == "Artist" && artist.Text == "Song", "toast preserves configured artist/title order");
            Click(title);
            check(calls.SequenceEqual(new[] { uri }), "clicking the first toast text opens its captured track snapshot");
            Click(artist);
            check(calls.Count == 1, "repeated toast clicks do not start concurrent Spotify launches");
            pending.SetResult(SpotifyTrackOpenResult.AppActivated);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(artist);
            check(calls.Count == 2, "clicking the second toast text works after the first launch completes");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click((UIElement)window.FindName("AlbumArt"));
            check(calls.Count == 3, "artwork clicks also open Spotify through the toast handler");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var source = PresentationSource.FromVisual(window)!;
            title.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            artist.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
                { RoutedEvent = Keyboard.KeyDownEvent });
            check(calls.Count == 5, "Enter and Space activate both focusable toast text links");
            check(title.Focusable && artist.Focusable && !window.ShowActivated && !window.ShowInTaskbar,
                "keyboard links preserve non-activating tray toast behavior");
        }
        finally { window.Close(); }

        var attempts = 0;
        var retry = new ToastWindow(TrackInfo.Empty, settings, (_, _) =>
        {
            attempts++;
            return attempts == 1 ? Task.FromException<SpotifyTrackOpenResult>(new InvalidOperationException("fake failure"))
                : Task.FromResult(SpotifyTrackOpenResult.AppActivated);
        });
        Click(retry);
        check(((FrameworkElement)retry.FindName("ToastBorder")).ToolTip.ToString()!.Contains("Impossibile"),
            "opener failures are contained and provide localized retry feedback");
        Click(retry);
        check(attempts == 2 && ((FrameworkElement)retry.FindName("ToastBorder")).ToolTip.ToString()!.StartsWith("Apri"),
            "failed opening can be retried, including when track URI is unavailable");
        retry.Close();

        CancellationToken token = default;
        var canceled = new ToastWindow(TrackInfo.Empty, settings, (_, cancellation) =>
        {
            token = cancellation;
            return Task.Delay(Timeout.Infinite, cancellation).ContinueWith<SpotifyTrackOpenResult>(
                task => { task.GetAwaiter().GetResult(); return SpotifyTrackOpenResult.Failed; }, TaskScheduler.Default);
        });
        Click(canceled);
        canceled.Close();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(token.IsCancellationRequested, "closing a toast cancels its pending Spotify activation");

        var activation = new TaskCompletionSource<SpotifyTrackOpenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken timedToken = default;
        var timedSettings = new AppSettings
        {
            ToastDurationMs = 500,
            ToastAnimationStyle = "None",
            ToastFadeInMs = 0,
            ToastFadeOutMs = 0,
            ShowSongDuration = true,
            ShowSongProgressBar = true
        };
        var timed = new ToastWindow(new TrackInfo("Timed song", "Artist", "Album",
            Position: TimeSpan.FromSeconds(10), Duration: TimeSpan.FromMinutes(2), IsPlaying: true),
            timedSettings, (_, cancellation) =>
            {
                timedToken = cancellation;
                activationRequested.TrySetResult();
                return activation.Task;
            })
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = false,
            Left = -10000,
            Top = -10000,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        // ShowTimed resets opacity for the None animation. Keep the first render
        // off screen, then invisible even when the toast's second positioning
        // pass runs. This test never presents or launches Spotify.
        timed.Loaded += (_, _) => { timed.Left = -10000; timed.Top = -10000; timed.Opacity = 0; };
        timed.ContentRendered += (_, _) => timed.Opacity = 0;
        timed.Closed += (_, _) => timedClosed.TrySetResult();
        try
        {
            timed.ShowTimed();
            var timeline = (TextBlock)timed.FindName("SongDurationText");
            var initialTimeline = timeline.Text;
            Click((UIElement)timed.FindName("Title1"));
            await activationRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(1100);
            check(timed.IsVisible && !timedToken.IsCancellationRequested && !timedClosed.Task.IsCompleted,
                "ShowTimed toast survives its 500ms timeout while Spotify activation remains pending");
            check(timeline.Text != initialTimeline,
                "song timeline continues while the toast lifetime timer is paused for activation");
            activation.SetResult(SpotifyTrackOpenResult.AppActivated);
            await timedClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(!timed.IsVisible,
                "ShowTimed toast resumes its lifetime timer and closes after activation completes");
        }
        finally
        {
            activation.TrySetResult(SpotifyTrackOpenResult.AppActivated);
            if (!timedClosed.Task.IsCompleted) timed.Close();
        }
    }

    private static void Click(UIElement element)
        => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.MouseDownEvent });
}

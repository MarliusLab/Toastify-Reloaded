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
        var calls = 0;
        var nowPlayingCalls = 0;
        var modifiers = ModifierKeys.None;
        var pending = new TaskCompletionSource<SpotifyTrackOpenResult>();
        var window = new ToastWindow(new TrackInfo("Song", "Artist", "Album", TrackUri: uri), settings,
            _ => { calls++; return pending.Task; }, () => modifiers,
            openNowPlaying: _ =>
            {
                nowPlayingCalls++;
                return Task.FromResult(SpotifyTrackOpenResult.NowPlayingRequested);
            }) { Opacity = 0, ShowActivated = false };
        window.Loaded += (_, _) => { window.Left = -10000; window.Top = -10000; };
        window.Show();
        try
        {
            var title = (TextBlock)window.FindName("Title1");
            var artist = (TextBlock)window.FindName("Title2");
            check(title.Text == "Artist" && artist.Text == "Song", "toast preserves configured artist/title order");
            Click(title);
            check(calls == 1 && nowPlayingCalls == 0, "clicking the first toast text requests Lyrics Plus without opening its old track URI");
            Click(artist);
            check(calls == 1, "repeated toast clicks do not start concurrent Spotify launches");
            var source = PresentationSource.FromVisual(window)!;
            modifiers = ModifierKeys.Alt | ModifierKeys.Shift;
            RaiseShortcut(title, source);
            check(nowPlayingCalls == 0 && calls == 1, "local Now Playing cannot overlap a pending Lyrics Plus request in the same toast");
            modifiers = ModifierKeys.None;
            pending.SetResult(SpotifyTrackOpenResult.LyricsPlusRequested);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(artist);
            check(calls == 2, "clicking the second toast text works after the first launch completes");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click((UIElement)window.FindName("AlbumArt"));
            check(calls == 3, "artwork clicks also open Lyrics Plus through the toast handler");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click((UIElement)window.FindName("ToastBorder"));
            check(calls == 4 && nowPlayingCalls == 0, "toast background clicks request Lyrics Plus without running Now Playing");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            title.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            artist.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
                { RoutedEvent = Keyboard.KeyDownEvent });
            check(calls == 6 && nowPlayingCalls == 0, "Enter and Space request Lyrics Plus from both focusable toast text links");
            modifiers = ModifierKeys.Alt | ModifierKeys.Shift;
            RaiseShortcut(title, source);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(calls == 6 && nowPlayingCalls == 1, "local Alt+Shift+J retains Now Playing and does not request Lyrics Plus");
            modifiers |= ModifierKeys.Control;
            title.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.J)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            modifiers = ModifierKeys.Alt | ModifierKeys.Shift;
            title.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.R)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            check(calls == 6 && nowPlayingCalls == 1, "toast shortcut ignores extra modifiers and unrelated Spotify shortcuts");
            modifiers = ModifierKeys.Control;
            title.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            check(calls == 6 && nowPlayingCalls == 1, "modified Enter does not trigger either toast navigation action");
            modifiers = ModifierKeys.None;
            Click(title);
            check(calls == 7 && nowPlayingCalls == 1 &&
                  ((FrameworkElement)window.FindName("ToastBorder")).ToolTip.ToString()!.Contains("Lyrics Plus"),
                "clicking after Now Playing returns to the Lyrics Plus action and its target-aware hint");
            check(title.Focusable && artist.Focusable && !window.ShowActivated && !window.ShowInTaskbar,
                "keyboard links preserve non-activating tray toast behavior");
        }
        finally { window.Close(); }

        var attempts = 0;
        var retry = new ToastWindow(TrackInfo.Empty, settings, _ =>
        {
            attempts++;
            return attempts == 1 ? Task.FromException<SpotifyTrackOpenResult>(new InvalidOperationException("fake failure"))
                : Task.FromResult(SpotifyTrackOpenResult.LyricsPlusRequested);
        });
        Click(retry);
        check(((FrameworkElement)retry.FindName("ToastBorder")).ToolTip.ToString()!.Contains("Impossibile"),
            "opener failures are contained and provide localized retry feedback");
        Click(retry);
        check(attempts == 2 && ((FrameworkElement)retry.FindName("ToastBorder")).ToolTip.ToString()!.StartsWith("Apri"),
            "failed opening can be retried, including when track URI is unavailable");
        retry.Close();

        var activationOnly = new ToastWindow(TrackInfo.Empty, settings,
            _ => Task.FromResult(SpotifyTrackOpenResult.AppActivated));
        Click(activationOnly);
        check(((FrameworkElement)activationOnly.FindName("ToastBorder")).ToolTip.ToString()!.Contains("Lyrics Plus non confermato"),
            "toast distinguishes Spotify activation from an unconfirmed Lyrics Plus request");
        activationOnly.Close();

        CancellationToken token = default;
        var canceled = new ToastWindow(TrackInfo.Empty, settings, cancellation =>
        {
            token = cancellation;
            return Task.Delay(Timeout.Infinite, cancellation).ContinueWith<SpotifyTrackOpenResult>(
                task => { task.GetAwaiter().GetResult(); return SpotifyTrackOpenResult.Failed; }, TaskScheduler.Default);
        });
        Click(canceled);
        canceled.Close();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        check(token.IsCancellationRequested, "closing a toast cancels its pending Lyrics Plus request");

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
            timedSettings, cancellation =>
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
                "ShowTimed toast survives its 500ms timeout while Lyrics Plus remains pending");
            // CI may schedule a 250ms dispatcher tick just before the next
            // displayed whole second. Observe a real subsequent tick with a
            // deadline instead of assuming it happened by exactly 1100ms.
            var timelineDeadline = System.Diagnostics.Stopwatch.StartNew();
            while (timeline.Text == initialTimeline && timelineDeadline.Elapsed < TimeSpan.FromSeconds(4))
            {
                await Task.Delay(25);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            check(timeline.Text != initialTimeline,
                "song timeline continues while the toast lifetime timer is paused for activation");
            activation.SetResult(SpotifyTrackOpenResult.LyricsPlusRequested);
            await timedClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(!timed.IsVisible,
                "ShowTimed toast resumes its lifetime timer and closes after Lyrics Plus completes");
        }
        finally
        {
            activation.TrySetResult(SpotifyTrackOpenResult.LyricsPlusRequested);
            if (!timedClosed.Task.IsCompleted) timed.Close();
        }

        await VerifyVisibleToastShortcutAsync(check);
    }

    private static async Task VerifyVisibleToastShortcutAsync(Action<bool, string> check)
    {
        Action? globalPress = null;
        var registered = false;
        var suspended = false;
        long clock = 0;
        using var hotkeys = new ToastNowPlayingHotkeyCoordinator(
            callback => { globalPress = callback; registered = true; return true; },
            () => registered = false,
            () => { suspended = true; return new EndScope(() => suspended = false); },
            delay: duration => { clock += (long)duration.TotalMilliseconds; return Task.CompletedTask; },
            clock: () => clock);
        var firstLyrics = 0;
        var firstNowPlaying = 0;
        var secondLyrics = 0;
        var secondNowPlaying = 0;
        var navigationWasSuspended = true;
        var pending = new TaskCompletionSource<SpotifyTrackOpenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingLyrics = new TaskCompletionSource<SpotifyTrackOpenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new ToastWindow(TrackInfo.Empty, new AppSettings(), _ =>
        {
            firstLyrics++;
            return Task.FromResult(SpotifyTrackOpenResult.LyricsPlusRequested);
        }, () => ModifierKeys.Alt | ModifierKeys.Shift, hotkeys, openNowPlaying: _ =>
        {
            firstNowPlaying++;
            return Task.FromResult(SpotifyTrackOpenResult.NowPlayingRequested);
        }) { Opacity = 0, ShowActivated = false };
        var second = new ToastWindow(TrackInfo.Empty, new AppSettings { ApplicationLanguage = "Italian" }, _ =>
        {
            secondLyrics++;
            navigationWasSuspended &= suspended && !registered;
            return pendingLyrics.Task;
        }, () => ModifierKeys.Alt | ModifierKeys.Shift, hotkeys, openNowPlaying: _ =>
        {
            secondNowPlaying++;
            navigationWasSuspended &= suspended && !registered;
            return pending.Task;
        }) { Opacity = 0, ShowActivated = false };
        first.Loaded += (_, _) => { first.Left = -10000; first.Top = -10000; };
        second.Loaded += (_, _) => { second.Left = -10000; second.Top = -10000; };
        first.Show(); second.Show();
        try
        {
            check(registered && hotkeys.IsGlobalRegistered, "visible overlapping WPF toasts attach the shared shortcut host");
            globalPress!();
            check(firstNowPlaying == 0 && secondNowPlaying == 1 && firstLyrics == 0 && secondLyrics == 0,
                "global Alt+Shift+J retains Now Playing through the latest visible toast and never requests Lyrics Plus");
            check(navigationWasSuspended, "Now Playing suspends the shared shortcut before invoking its distinct navigation callback");
            Click(first);
            check(firstLyrics == 0 && secondNowPlaying == 1,
                "a Lyrics Plus click in another toast cannot overlap pending global Now Playing");
            pending.SetResult(SpotifyTrackOpenResult.NowPlayingRequested);
            var restoreDeadline = DateTime.UtcNow.AddSeconds(3);
            while (!registered && DateTime.UtcNow < restoreDeadline) await Task.Delay(20);
            check(registered && hotkeys.IsGlobalRegistered,
                "the shared shortcut is restored after pending Now Playing completes");
            var secondSource = PresentationSource.FromVisual(second)!;
            Click(second);
            check(secondLyrics == 1 && secondNowPlaying == 1 && suspended && !registered,
                "Lyrics Plus uses the same shared gate while keeping its callback distinct from Now Playing");
            RaiseShortcut((UIElement)first.FindName("Title1"), PresentationSource.FromVisual(first)!);
            globalPress!();
            check(firstNowPlaying == 0 && secondNowPlaying == 1 && secondLyrics == 1,
                "local or queued global Now Playing cannot overlap Lyrics Plus pending in another toast");
            first.Close();
            pendingLyrics.SetResult(SpotifyTrackOpenResult.LyricsPlusRequested);
            restoreDeadline = DateTime.UtcNow.AddSeconds(3);
            while (!registered && DateTime.UtcNow < restoreDeadline) await Task.Delay(20);
            check(registered && hotkeys.IsGlobalRegistered && navigationWasSuspended,
                "closing the older toast preserves the newer shortcut after Lyrics Plus releases the shared gate");
            second.Hide();
            check(!registered && !hotkeys.IsGlobalRegistered, "hiding the last WPF toast releases the temporary shortcut");
            second.Show();
            check(registered, "showing a toast again reacquires its shortcut lease");
            hotkeys.Enabled = false;
            check(!registered && ((FrameworkElement)second.FindName("ToastBorder")).ToolTip.ToString()!.Contains("disattivata"),
                "disabling global hotkeys updates the visible toast's local-fallback hint");
            RaiseShortcut((UIElement)second.FindName("Title1"), secondSource);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(secondNowPlaying == 2 && secondLyrics == 1 && !registered,
                "local Alt+Shift+J still requests Now Playing, not Lyrics Plus, while global hotkeys are disabled");
            check(((FrameworkElement)second.FindName("ToastBorder")).ToolTip.ToString()!
                    .Contains("riproduzione", StringComparison.OrdinalIgnoreCase),
                "the Now Playing result keeps a target-aware hint after a preceding Lyrics Plus click");
            Click(second);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(secondLyrics == 2 && secondNowPlaying == 2 && !registered,
                "disabling global hotkeys does not disable an ordinary Lyrics Plus click");
        }
        finally
        {
            pending.TrySetResult(SpotifyTrackOpenResult.NowPlayingRequested);
            pendingLyrics.TrySetResult(SpotifyTrackOpenResult.LyricsPlusRequested);
            if (first.IsVisible) first.Close();
            second.Close();
        }
        check(!registered, "closing all WPF toasts leaves no temporary shortcut registered");

        long occupiedClock = 0;
        using var occupied = new ToastNowPlayingHotkeyCoordinator(_ => false, () => { },
            () => new DeniedScope(),
            duration => { occupiedClock += (long)duration.TotalMilliseconds; return Task.CompletedTask; }, () => occupiedClock);
        var lyricsRequests = 0;
        var nowPlayingRequests = 0;
        var activationRequests = 0;
        var fallback = new ToastWindow(TrackInfo.Empty, new AppSettings { ApplicationLanguage = "Italian" }, _ =>
        {
            lyricsRequests++;
            return Task.FromResult(SpotifyTrackOpenResult.LyricsPlusRequested);
        }, () => ModifierKeys.Alt | ModifierKeys.Shift, occupied, openNowPlaying: _ =>
        {
            nowPlayingRequests++;
            return Task.FromResult(SpotifyTrackOpenResult.NowPlayingRequested);
        }, activateSpotify: _ =>
        {
            activationRequests++;
            return Task.FromResult(SpotifyTrackOpenResult.AppActivated);
        }) { Opacity = 0, ShowActivated = false };
        fallback.Loaded += (_, _) => { fallback.Left = -10000; fallback.Top = -10000; };
        fallback.Show();
        try
        {
            Click(fallback);
            check(lyricsRequests == 1 && nowPlayingRequests == 0 && activationRequests == 0,
                "occupied foreign shortcut does not block or downgrade a URI-based Lyrics Plus click");
            check(((FrameworkElement)fallback.FindName("ToastBorder")).ToolTip.ToString()!.Contains("Lyrics Plus"),
                "the toast advertises Lyrics Plus even when its global shortcut is occupied");
            RaiseShortcut((UIElement)fallback.FindName("Title1"), PresentationSource.FromVisual(fallback)!);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(lyricsRequests == 1 && nowPlayingRequests == 0 && activationRequests == 1,
                "occupied foreign Alt+Shift+J uses activation-only fallback without requesting Lyrics Plus or forwarding input");
            check(((FrameworkElement)fallback.FindName("ToastBorder")).ToolTip.ToString()!
                    .Contains("riproduzione", StringComparison.OrdinalIgnoreCase) &&
                  ((FrameworkElement)fallback.FindName("ToastBorder")).ToolTip.ToString()!.Contains("non confermato"),
                "activation-only fallback reports the unconfirmed Now Playing target rather than a Lyrics Plus failure");
            Click(fallback);
            check(lyricsRequests == 2 && nowPlayingRequests == 0 && activationRequests == 1,
                "a later Lyrics Plus click still works after a blocked Now Playing shortcut");
        }
        finally { fallback.Close(); }
    }

    private sealed class DeniedScope : IToastShortcutSuspension
    {
        public bool CanForwardShortcut => false;
        public void Dispose() { }
    }

    private sealed class EndScope(Action end) : IDisposable
    {
        public void Dispose() => end();
    }

    private static void Click(UIElement element)
        => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.MouseDownEvent });

    private static void RaiseShortcut(UIElement element, PresentationSource source)
        => element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.J)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
}

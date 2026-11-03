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
        var modifiers = ModifierKeys.None;
        var pending = new TaskCompletionSource<SpotifyTrackOpenResult>();
        var window = new ToastWindow(new TrackInfo("Song", "Artist", "Album", TrackUri: uri), settings,
            _ => { calls++; return pending.Task; }, () => modifiers) { Opacity = 0, ShowActivated = false };
        window.Loaded += (_, _) => { window.Left = -10000; window.Top = -10000; };
        window.Show();
        try
        {
            var title = (TextBlock)window.FindName("Title1");
            var artist = (TextBlock)window.FindName("Title2");
            check(title.Text == "Artist" && artist.Text == "Song", "toast preserves configured artist/title order");
            Click(title);
            check(calls == 1, "clicking the first toast text requests Now Playing without opening its old track URI");
            Click(artist);
            check(calls == 1, "repeated toast clicks do not start concurrent Spotify launches");
            pending.SetResult(SpotifyTrackOpenResult.NowPlayingRequested);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(artist);
            check(calls == 2, "clicking the second toast text works after the first launch completes");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click((UIElement)window.FindName("AlbumArt"));
            check(calls == 3, "artwork clicks also open Spotify through the toast handler");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var source = PresentationSource.FromVisual(window)!;
            title.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            artist.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Space)
                { RoutedEvent = Keyboard.KeyDownEvent });
            check(calls == 5, "Enter and Space activate both focusable toast text links");
            modifiers = ModifierKeys.Alt | ModifierKeys.Shift;
            title.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.J)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(calls == 6, "Alt+Shift+J in the toast uses the same Now Playing action as a click");
            modifiers |= ModifierKeys.Control;
            title.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.J)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            modifiers = ModifierKeys.Alt | ModifierKeys.Shift;
            title.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.R)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            check(calls == 6, "toast shortcut ignores extra modifiers and unrelated Spotify shortcuts");
            modifiers = ModifierKeys.None;
            check(title.Focusable && artist.Focusable && !window.ShowActivated && !window.ShowInTaskbar,
                "keyboard links preserve non-activating tray toast behavior");
        }
        finally { window.Close(); }

        var attempts = 0;
        var retry = new ToastWindow(TrackInfo.Empty, settings, _ =>
        {
            attempts++;
            return attempts == 1 ? Task.FromException<SpotifyTrackOpenResult>(new InvalidOperationException("fake failure"))
                : Task.FromResult(SpotifyTrackOpenResult.NowPlayingRequested);
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
        check(((FrameworkElement)activationOnly.FindName("ToastBorder")).ToolTip.ToString()!.Contains("comando In riproduzione non confermato"),
            "toast distinguishes Spotify activation from a blocked Now Playing navigation");
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
                "ShowTimed toast survives its 500ms timeout while Spotify activation remains pending");
            check(timeline.Text != initialTimeline,
                "song timeline continues while the toast lifetime timer is paused for activation");
            activation.SetResult(SpotifyTrackOpenResult.NowPlayingRequested);
            await timedClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(!timed.IsVisible,
                "ShowTimed toast resumes its lifetime timer and closes after activation completes");
        }
        finally
        {
            activation.TrySetResult(SpotifyTrackOpenResult.NowPlayingRequested);
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
        var firstCalls = 0;
        var secondCalls = 0;
        var pending = new TaskCompletionSource<SpotifyTrackOpenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new ToastWindow(TrackInfo.Empty, new AppSettings(), _ =>
        {
            firstCalls++;
            return Task.FromResult(SpotifyTrackOpenResult.NowPlayingRequested);
        }, hotkeys: hotkeys) { Opacity = 0, ShowActivated = false };
        var second = new ToastWindow(TrackInfo.Empty, new AppSettings { ApplicationLanguage = "Italian" }, _ =>
        {
            secondCalls++;
            check(suspended && !registered, "toast suspends its global shortcut before the navigation callback");
            return pending.Task;
        }, () => ModifierKeys.Alt | ModifierKeys.Shift, hotkeys) { Opacity = 0, ShowActivated = false };
        first.Loaded += (_, _) => { first.Left = -10000; first.Top = -10000; };
        second.Loaded += (_, _) => { second.Left = -10000; second.Top = -10000; };
        first.Show(); second.Show();
        try
        {
            check(registered && hotkeys.IsGlobalRegistered, "visible overlapping WPF toasts attach the shared shortcut host");
            globalPress!();
            check(firstCalls == 0 && secondCalls == 1, "global toast shortcut navigates once through the latest visible toast");
            Click(first);
            check(firstCalls == 0 && secondCalls == 1, "clicking another toast cannot overlap an ongoing global navigation");
            first.Close();
            pending.SetResult(SpotifyTrackOpenResult.NowPlayingRequested);
            var restoreDeadline = DateTime.UtcNow.AddSeconds(3);
            while (!registered && DateTime.UtcNow < restoreDeadline) await Task.Delay(20);
            check(registered && hotkeys.IsGlobalRegistered, "closing the older toast preserves the newer toast shortcut after handoff");
            second.Hide();
            check(!registered && !hotkeys.IsGlobalRegistered, "hiding the last WPF toast releases the temporary shortcut");
            second.Show();
            check(registered, "showing a toast again reacquires its shortcut lease");
            hotkeys.Enabled = false;
            check(!registered && ((FrameworkElement)second.FindName("ToastBorder")).ToolTip.ToString()!.Contains("disattivata"),
                "disabling global hotkeys updates the visible toast's local-fallback hint");
            var source = PresentationSource.FromVisual(second)!;
            ((UIElement)second.FindName("Title1")).RaiseEvent(
                new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.J)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(secondCalls == 2 && !registered, "local Alt+Shift+J still navigates while global hotkeys are disabled");
        }
        finally
        {
            pending.TrySetResult(SpotifyTrackOpenResult.NowPlayingRequested);
            if (first.IsVisible) first.Close();
            second.Close();
        }
        check(!registered, "closing all WPF toasts leaves no temporary shortcut registered");

        using var occupied = new ToastNowPlayingHotkeyCoordinator(_ => false, () => { },
            () => new DeniedScope(), _ => Task.CompletedTask);
        var activated = 0;
        var forwarded = 0;
        var fallback = new ToastWindow(TrackInfo.Empty, new AppSettings(), _ =>
        {
            forwarded++;
            return Task.FromResult(SpotifyTrackOpenResult.NowPlayingRequested);
        }, hotkeys: occupied, activateSpotify: _ =>
        {
            activated++;
            return Task.FromResult(SpotifyTrackOpenResult.AppActivated);
        });
        try
        {
            Click(fallback);
            check(activated == 1 && forwarded == 0,
                "occupied foreign shortcut opens Spotify without forwarding navigation input to its owner");
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
}

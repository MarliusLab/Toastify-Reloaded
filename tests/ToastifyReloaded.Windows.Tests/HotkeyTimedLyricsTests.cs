using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using ToastifyReloaded.Models;
using ToastifyReloaded.Services;
using ToastifyReloaded.Shared;

namespace ToastifyReloaded.Windows.Tests;

internal static class HotkeyTimedLyricsTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await VerifyDelayedHeldControlAsync(check);
        await VerifyFadingOwnerAsync(check);
        await VerifyMultipleTimedToastsAsync(check);
    }

    private static async Task VerifyDelayedHeldControlAsync(Action<bool, string> check)
    {
        using var path = new TimedPath(new AppSettings
        {
            ToastDurationMs = 3500, ToastFadeInMs = 250, ToastFadeOutMs = 250,
            ToastAnimationStyle = "FadeSlide"
        });
        path.Input.Hold(0x11, true); // Ctrl remains held across both actual dispatches.
        path.Input.Hold(0xA2, true); // Windows also reports its physical left-hand variant.
        path.Input.Hold(0x26, true); // Up is independently released/repressed.
        await path.PressAsync();
        path.Input.Hold(0x26, false);
        check(path.Shows == 1 && path.LastToast!.IsVisible && path.UriCalls == 0,
            "real Ctrl+Up dispatch shows a timed toast without opening lyrics on its first press");
        await Task.Delay(2000);
        path.Input.Hold(0x26, true);
        var second = path.PressAsync();
        await Task.Delay(100);
        check(path.UriCalls == 0 && path.Input.IsKeyDown(0x26),
            "timed hotkey lyrics waits for the primary Up key without requiring Ctrl release");
        path.Input.Hold(0x26, false);
        await UntilAsync(() => path.UriCalls != 0 || second.IsCompleted, TimeSpan.FromSeconds(3));
        await second.WaitAsync(TimeSpan.FromSeconds(3));
        check(path.UriCalls == 1 && path.RoutedWithControlHeld && path.Input.IsKeyDown(0x11) && path.Shows == 1,
            "a second Ctrl+Up after two seconds opens lyrics while Ctrl is still physically held on the real ShowTimed path");
        check(path.Targets.SequenceEqual(new[] { SpotifyTrackOpenService.LyricsPlusUri }) && path.Input.Sends == 0 && path.Input.Captures == 0,
            "timed hotkey navigation sends only the fixed lyrics route and never injects keys or selects a track");
        await Task.Delay(1600);
        check(path.Input.IsKeyDown(0x11) && path.UriCalls == 1 && path.LastToast!.IsVisible,
            "holding Ctrl for more than four seconds across both presses does not defer lyrics or repeat the request");
    }

    private static async Task VerifyFadingOwnerAsync(Action<bool, string> check)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var path = new TimedPath(new AppSettings
        {
            ToastDurationMs = 500, ToastFadeInMs = 0, ToastFadeOutMs = 3000,
            ToastAnimationStyle = "Fade"
        }, pending.Task);
        await path.PressAsync();
        var toast = path.LastToast!;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        toast.Closed += (_, _) => closed.TrySetResult();
        await UntilAsync(() => IsExiting(toast), TimeSpan.FromSeconds(4));
        check(toast.IsVisible && !closed.Task.IsCompleted && IsExiting(toast),
            "real timed fixture reaches a still-visible long fade-out before the delayed shortcut");
        var request = path.PressAsync();
        try
        {
            await UntilAsync(() => path.UriCalls != 0 || request.IsCompleted, TimeSpan.FromSeconds(2));
            check(path.UriCalls == 1 && path.Shows == 1 && !request.IsCompleted,
                "Show Toast requests lyrics from its visible fading owner rather than consuming the press or creating another toast");
            await Task.Delay(3100);
            check(toast.IsVisible && !closed.Task.IsCompleted && !path.OpenerToken.IsCancellationRequested,
                "pending lyrics survives the original fade-close deadline on an actual timed window");
            pending.TrySetResult();
            await request.WaitAsync(TimeSpan.FromSeconds(2));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(6));
            check(!toast.IsVisible, "the interrupted fade eventually closes after lyrics navigation settles");
            await path.PressAsync();
            check(path.Shows == 2 && path.LastToast!.IsVisible,
                "after the resumed timed toast closes the next configured shortcut shows a new toast");
        }
        finally { pending.TrySetResult(); }
    }

    private static async Task VerifyMultipleTimedToastsAsync(Action<bool, string> check)
    {
        using var path = new TimedPath(new AppSettings
        {
            ToastDurationMs = 5000, ToastFadeInMs = 0, ToastFadeOutMs = 0,
            ToastAnimationStyle = "None"
        });
        await path.PressAsync();
        var older = path.LastToast!;
        path.ShowAutomatic(new AppSettings
        {
            ToastDurationMs = 500, ToastFadeInMs = 0, ToastFadeOutMs = 3000,
            ToastAnimationStyle = "Fade"
        });
        var newer = path.LastToast!;
        await UntilAsync(() => IsExiting(newer), TimeSpan.FromSeconds(4));
        check(older.IsVisible && newer.IsVisible && IsExiting(newer),
            "overlapping real timed toasts include an older visible owner and a newer fading owner");
        await path.PressAsync().WaitAsync(TimeSpan.FromSeconds(3));
        check(path.UriCalls == 1 && path.Shows == 2,
            "one shortcut across overlapping timed toasts produces exactly one lyrics request and no new popup");
        newer.Hide();
        await Task.Delay(250); // Allow the shared native shortcut gate to rearm.
        await path.PressAsync().WaitAsync(TimeSpan.FromSeconds(3));
        check(path.UriCalls == 2 && path.Shows == 2 && older.IsVisible,
            "hiding the newest real timed toast restores the remaining visible owner's second action");
    }

    private static async Task UntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate() && elapsed.Elapsed < timeout) await Task.Delay(20);
    }

    // Observe the real timer's phase; never invoke animation methods or force
    // a phase. Slow native runners need not hit an assumed millisecond boundary.
    private static bool IsExiting(ToastWindow toast)
        => (bool)(typeof(ToastWindow).GetField("_fadeOutStarted", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Timed toast exit phase is unavailable")).GetValue(toast)!;

    private sealed class TimedPath : IDisposable
    {
        private readonly ToastShowHotkeyController _controller = new();
        private readonly GlobalHotkeyService _hotkeys = new((_, _, _) => true, _ => { });
        private readonly List<ToastWindow> _toasts = new();
        private readonly AppSettings _settings;
        private readonly SpotifyTrackOpenService _service;
        private Task _dispatch = Task.CompletedTask;
        private int _uriCalls;
        public readonly HeldInput Input = new();
        public readonly ConcurrentQueue<string> Targets = new();
        public int UriCalls => Volatile.Read(ref _uriCalls);
        public int Shows { get; private set; }
        public bool RoutedWithControlHeld { get; private set; }
        public CancellationToken OpenerToken { get; private set; }
        public ToastWindow? LastToast { get; private set; }

        public TimedPath(AppSettings settings, Task? pending = null)
        {
            _settings = settings;
            _service = new SpotifyTrackOpenService(async (uri, token) =>
            {
                OpenerToken = token;
                Targets.Enqueue(uri);
                RoutedWithControlHeld = Input.IsKeyDown(0x11);
                Interlocked.Increment(ref _uriCalls);
                if (pending is not null) await pending.WaitAsync(token);
                return true;
            }, _ => Task.FromResult(true), attemptTimeout: TimeSpan.FromSeconds(7), nowPlayingInput: Input);
            _hotkeys.RegisterAll(new[] { new HotkeyBinding { Action = HotkeyAction.ShowToast, Shortcut = "Ctrl+Up", Enabled = true } });
            _hotkeys.HotkeyPressed += (_, action) =>
            {
                if (action == HotkeyAction.ShowToast)
                    _dispatch = _controller.ExecuteAsync(() => { ShowAutomatic(_settings); return Task.CompletedTask; });
            };
        }

        public Task PressAsync()
        {
            if (!_hotkeys.DispatchHotkey(1000)) throw new InvalidOperationException("Show Toast binding was not registered");
            return _dispatch;
        }

        public void ShowAutomatic(AppSettings settings)
        {
            var toast = new ToastWindow(TrackInfo.Empty, settings, _service.OpenLyricsPlusAsync,
                () => ModifierKeys.None, _hotkeys.ToastNowPlaying,
                openNowPlaying: _ => Task.FromResult(SpotifyTrackOpenResult.AppActivated),
                activateSpotify: _ => Task.FromResult(SpotifyTrackOpenResult.AppActivated),
                openLyricsPlusFromHotkey: _service.OpenLyricsPlusFromHotkeyAsync);
            toast.ShowActivated = false; toast.ShowInTaskbar = false; toast.Topmost = false;
            toast.WindowStartupLocation = WindowStartupLocation.Manual;
            // Production positioning and animation still run. Keep every HWND
            // outside the desktop even when the second positioning pass fires.
            toast.LocationChanged += (_, _) => { if (toast.Left != -30000) toast.Left = -30000; if (toast.Top != -30000) toast.Top = -30000; };
            var lease = _controller.Attach(() => toast.CanHandleLyricsPlusRequest, toast.RequestLyricsPlusFromHotkeyAsync);
            toast.Closed += (_, _) => lease.Dispose();
            _toasts.Add(toast); LastToast = toast; Shows++;
            toast.ShowTimed();
        }

        public void Dispose()
        {
            foreach (var toast in _toasts) toast.Close();
            _controller.Dispose(); _hotkeys.Dispose();
        }
    }

    private sealed class HeldInput : ISpotifyNowPlayingInput
    {
        private readonly ConcurrentDictionary<ushort, byte> _held = new();
        public int Sends, Captures;
        public void Hold(ushort key, bool held) { if (held) _held[key] = 1; else _held.TryRemove(key, out _); }
        public bool IsKeyDown(ushort key) => _held.ContainsKey(key);
        public IntPtr CaptureForegroundSpotifyWindow() { Interlocked.Increment(ref Captures); return IntPtr.Zero; }
        public bool IsSameSpotifyForegroundWindow(IntPtr window) => false;
        public uint Send(IReadOnlyList<SpotifyShortcutInput> inputs) { Interlocked.Increment(ref Sends); return 0; }
    }
}

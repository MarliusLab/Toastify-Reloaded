extern alias LinuxApp;

using System.Diagnostics;
using LinuxToast = LinuxApp::ToastifyReloaded.Linux.ToastWindow;
using Settings = LinuxApp::ToastifyReloaded.Linux.Models.LinuxSettings;
using Track = LinuxApp::ToastifyReloaded.Linux.Models.LinuxTrackInfo;
using Artwork = LinuxApp::ToastifyReloaded.Linux.Services.ArtworkService;
using Opener = LinuxApp::ToastifyReloaded.Linux.Services.SpotifyTrackOpenService;
using Gate = LinuxApp::ToastifyReloaded.Shared.ToastNavigationGate;
using ShowController = LinuxApp::ToastifyReloaded.Shared.ToastShowHotkeyController;

internal static class LinuxTimedToastTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await DelayedHotkeyDuringFadeAsync(check);
        await CloseCancelsAcceptedActionAsync(check);
        await EnteringAndHiddenToastAsync(check);
    }

    private static Settings ToastSettings(int display = 500, int fadeIn = 0, int fadeOut = 1100) => new()
    {
        ImageMode = "None", ShowProgress = false, ShowSongTime = false,
        ToastDisplayMs = display, FadeInMs = fadeIn, FadeOutMs = fadeOut,
        AnimationStyle = "Fade", AutoWidth = false, MinWidth = 300
    };

    private static LinuxToast Create(Settings settings, Opener opener, Gate gate)
        => new(new Track("Isolated timed toast", "Artist", "Album", "", 10, 180, true), settings,
            new Artwork(), opener, gate);

    private static async Task UntilAsync(Func<bool> condition, string name)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(name);
            await Task.Delay(15);
        }
    }

    private static async Task DelayedHotkeyDuringFadeAsync(Action<bool, string> check)
    {
        using var gate = new Gate();
        using var controller = new ShowController();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken actionToken = default;
        var opener = new Opener(async (file, arguments, token) =>
        {
            calls++; actionToken = token;
            check(file == "xdg-open" && arguments.SequenceEqual(new[] { "spotify:app:lyrics-plus" }),
                "Linux timed hotkey reaches only the fixed Lyrics Plus URI");
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return (0, "", "");
        }, _ => Task.FromResult<string?>(null));
        var toast = Create(ToastSettings(), opener, gate);
        using var lease = controller.Attach(() => toast.CanHandleLyricsPlusRequest, toast.RequestLyricsPlusFromHotkeyAsync);
        Task lifetime = Task.CompletedTask;
        var shown = 0;
        Task Show() { shown++; lifetime = toast.ShowToastAsync(); return Task.CompletedTask; }
        try
        {
            await controller.ExecuteAsync(Show);
            await UntilAsync(() => toast.IsVisible && toast.Opacity < .65, "Linux toast never entered its real fade-out");
            check(!lifetime.IsCompleted && toast.CanHandleLyricsPlusRequest,
                "Linux actual timed toast remains eligible during visible fade-out after its display timeout");
            var request = controller.ExecuteAsync(Show);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(toast.Opacity == 1 && toast.IsVisible,
                "Linux delayed Show Toast interrupts the actual fade and restores the same window");
            await controller.ExecuteAsync(Show);
            var otherCalls = 0;
            var other = Create(ToastSettings(), new Opener((_, _, _) =>
            { otherCalls++; return Task.FromResult((0, "", "")); }, _ => Task.FromResult<string?>(null)), gate);
            var otherLifetime = other.ShowToastAsync();
            try
            {
                await UntilAsync(() => other.IsVisible, "Linux second toast was not shown");
                await other.RequestLyricsPlusFromHotkeyAsync();
                check(shown == 1 && calls == 1 && otherCalls == 0,
                    "Linux real visible toasts and repeated hotkeys share one pending navigation gate");
            }
            finally { other.Close(); await otherLifetime.WaitAsync(TimeSpan.FromSeconds(2)); }
            await Task.Delay(1400); // Beyond this window's original display + fade deadline.
            check(toast.IsVisible && !lifetime.IsCompleted && !actionToken.IsCancellationRequested,
                "Linux interrupted exit cannot close or cancel an accepted delayed action at the old deadline");
            release.TrySetResult();
            await request.WaitAsync(TimeSpan.FromSeconds(2));
            await lifetime.WaitAsync(TimeSpan.FromSeconds(4));
            check(!toast.IsVisible && !toast.CanHandleLyricsPlusRequest,
                "Linux actual toast resumes its configured exit after the accepted action settles");
            var next = Create(ToastSettings(fadeOut: 50), new Opener((_, _, _) => Task.FromResult((0, "", "")),
                _ => Task.FromResult<string?>(null)), gate);
            Task nextLifetime = Task.CompletedTask;
            try
            {
                await controller.ExecuteAsync(() => { shown++; nextLifetime = next.ShowToastAsync(); return Task.CompletedTask; });
                check(shown == 2 && next.IsVisible, "Linux next Show Toast press creates a new real toast after the previous one closes");
            }
            finally { next.Close(); await nextLifetime.WaitAsync(TimeSpan.FromSeconds(2)); }
        }
        finally { release.TrySetResult(); toast.Close(); await lifetime.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private static async Task CloseCancelsAcceptedActionAsync(Action<bool, string> check)
    {
        using var gate = new Gate();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken token = default;
        var toast = Create(ToastSettings(fadeOut: 1500), new Opener(async (_, _, cancellation) =>
        {
            calls++; token = cancellation; started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
            return (0, "", "");
        }, _ => Task.FromResult<string?>(null)), gate);
        var lifetime = toast.ShowToastAsync();
        Task request = Task.CompletedTask;
        try
        {
            await UntilAsync(() => toast.IsVisible && toast.Opacity < .7, "Linux close test did not reach fade-out");
            request = toast.RequestLyricsPlusFromHotkeyAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            toast.Close();
            await Task.WhenAll(request, lifetime).WaitAsync(TimeSpan.FromSeconds(2));
            await toast.RequestLyricsPlusFromHotkeyAsync();
            check(token.IsCancellationRequested && calls == 1 && !toast.IsVisible,
                "Linux explicit close cancels a fade-interrupting action without a late fallback or second launch");
        }
        finally { toast.Close(); await Task.WhenAll(request, lifetime).WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private static async Task EnteringAndHiddenToastAsync(Action<bool, string> check)
    {
        using var gate = new Gate();
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toast = Create(ToastSettings(fadeIn: 1200, fadeOut: 50), new Opener(async (_, _, token) =>
        { calls++; await release.Task.WaitAsync(token); return (0, "", ""); }, _ => Task.FromResult<string?>(null)), gate);
        var lifetime = toast.ShowToastAsync();
        Task request = Task.CompletedTask;
        try
        {
            await UntilAsync(() => toast.IsVisible, "Linux entering toast was not visible");
            request = toast.RequestLyricsPlusFromHotkeyAsync();
            await Task.Delay(800);
            check(toast.IsVisible && toast.Opacity == 1 && calls == 1 && !lifetime.IsCompleted,
                "Linux accepted navigation interrupts entry animation and holds the actual window beyond its display duration");
            release.TrySetResult(); await request;
            toast.Hide();
            await toast.RequestLyricsPlusFromHotkeyAsync();
            check(calls == 1, "Linux hidden timed toast cannot accept a hotkey navigation request");
        }
        finally { release.TrySetResult(); toast.Close(); await Task.WhenAll(request, lifetime).WaitAsync(TimeSpan.FromSeconds(3)); }
    }
}

extern alias MacApp;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using SharpHook.Data;
using Toast = MacApp::ToastifyReloaded.Mac.ToastWindow;
using Settings = MacApp::ToastifyReloaded.Mac.Models.MacSettings;
using Track = MacApp::ToastifyReloaded.Mac.Models.MacTrackInfo;
using Artwork = MacApp::ToastifyReloaded.Mac.Services.ArtworkService;
using Result = MacApp::ToastifyReloaded.Mac.Services.SpotifyTrackOpenResult;
using Opener = MacApp::ToastifyReloaded.Mac.Services.SpotifyTrackOpenService;
using Hotkeys = MacApp::ToastifyReloaded.Mac.Services.MacGlobalHotkeyService;
using Gate = MacApp::ToastifyReloaded.Shared.ToastNavigationGate;
using Controller = MacApp::ToastifyReloaded.Shared.ToastShowHotkeyController;

internal static class MacTimedToastTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(Hotkeys.CanDispatchAfterRelease(new[] { KeyCode.VcLeftControl }, true) &&
              !Hotkeys.CanDispatchAfterRelease(new[] { KeyCode.VcLeftControl, KeyCode.VcUp }, true) &&
              !Hotkeys.CanDispatchAfterRelease(new[] { KeyCode.VcLeftControl }, false),
            "macOS saved Show Toast waits for Up release while allowing continuously held Ctrl; strict actions retain full release");
        await HeldModifiersServiceAsync(check);
        await DelayedFadeAsync(check);
        await StrictKeyboardAndCloseAsync(check);
        await EntryAndHiddenAsync(check);
    }

    private static Settings Profile(int fadeIn = 0, int fadeOut = 1100) => new()
    {
        ImageMode = "None", ShowProgress = false, ShowSongTime = false,
        ToastDisplayMs = 500, FadeInMs = fadeIn, FadeOutMs = fadeOut,
        AnimationStyle = "Fade + Slide", AutoWidth = false, MinWidth = 300
    };

    private static Toast Create(Settings settings, Func<bool, CancellationToken, Task<Result>> open, Gate? gate = null)
        => new(new Track("Isolated timed toast", "Artist", "Album", "", 10, 180, true), settings, new Artwork(), open, gate);

    private static async Task HeldModifiersServiceAsync(Action<bool, string> check)
    {
        var keys = new HashSet<ushort> { 0x3b, 0x7e }; // Control + Up, physical macOS virtual-key codes.
        var mouse = false;
        var calls = 0;
        var strictCalls = 0;
        var hotkeyCalls = 0;
        async Task<bool> Wait(bool allow, CancellationToken token)
        {
            if (allow) hotkeyCalls++; else strictCalls++;
            for (var i = 0; i < 50; i++)
            {
                token.ThrowIfCancellationRequested();
                if (!Opener.HasBlockingInput(keys.Contains, _ => mouse, allow)) return true;
                await Task.Delay(5, token);
            }
            return false;
        }
        var opener = new Opener((_, arguments, _) =>
        {
            calls++;
            check(arguments.SequenceEqual(new[] { "-a", "Spotify", "spotify:app:lyrics-plus" }),
                "macOS held-modifier hotkey launches only the literal Lyrics Plus URI without synthetic key or playback commands");
            return Task.FromResult((0, "", ""));
        }, token => Wait(false, token), token => Wait(true, token));
        var request = opener.OpenLyricsPlusFromHotkeyAsync();
        await Task.Delay(40);
        check(calls == 0 && !request.IsCompleted, "macOS hotkey URI waits while the primary Up key is still physically held");
        keys.Remove(0x7e);
        check((await request).Success && calls == 1 && keys.Contains(0x3b) && hotkeyCalls == 1 && strictCalls == 0,
            "macOS hotkey URI succeeds after Up release while Ctrl stays held continuously");
        check(!(await opener.OpenLyricsPlusAsync()).Success && calls == 1 && strictCalls == 1,
            "macOS click/Enter entry point still refuses held modifiers");
        mouse = true;
        check(!(await opener.OpenLyricsPlusFromHotkeyAsync()).Success && calls == 1,
            "macOS hotkey context never permits a held mouse button");
    }

    private static async Task DelayedFadeAsync(Action<bool, string> check)
    {
        using var controller = new Controller();
        using var gate = new Gate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var hotkeyContext = false;
        CancellationToken actionToken = default;
        var settings = Profile(fadeOut: 5000);
        var toast = Create(settings, async (hotkey, token) =>
        { calls++; hotkeyContext = hotkey; actionToken = token; await release.Task.WaitAsync(token); return new(true, false, "LyricsPlusRequested"); }, gate);
        using var lease = controller.Attach(() => toast.CanHandleLyricsPlusRequest, toast.RequestLyricsPlusFromHotkeyAsync);
        var shown = 0;
        Task lifetime = Task.CompletedTask;
        Task Show() { shown++; lifetime = toast.ShowToastAsync(); return Task.CompletedTask; }
        Task navigation = Task.CompletedTask;
        try
        {
            await controller.ExecuteAsync(Show);
            await TimedToastWait.UntilAsync(() => toast.IsExitAnimationActive && toast.Opacity < .95);
            check(toast.IsVisible && toast.CanHandleLyricsPlusRequest,
                "macOS actual timed Show Toast remains eligible after display timeout during visible fade-out");
            navigation = controller.ExecuteAsync(Show);
            await TimedToastWait.UntilAsync(() => calls == 1);
            check(hotkeyContext && shown == 1 && toast.IsVisible && toast.Opacity == 1 && !toast.IsExitAnimationActive,
                "macOS delayed saved shortcut interrupts actual fade and selects the modifier-aware context on the same toast");
            await controller.ExecuteAsync(Show);
            var otherCalls = 0;
            var other = Create(Profile(fadeOut: 50), (_, _) => { otherCalls++; return Task.FromResult(new Result(true, false, "LyricsPlusRequested")); }, gate);
            var otherLifetime = other.ShowToastAsync();
            try { await other.RequestLyricsPlusFromHotkeyAsync(); check(calls == 1 && otherCalls == 0 && shown == 1,
                "macOS pending navigation serializes repeated shortcuts and overlapping actual toasts"); }
            finally { other.Close(); await otherLifetime.WaitAsync(TimeSpan.FromSeconds(2)); }
            await Task.Delay(5200);
            check(toast.IsVisible && !lifetime.IsCompleted && !actionToken.IsCancellationRequested && settings.FadeOutMs == 5000,
                "macOS accepted action survives its original timed close without changing saved fade duration");
            release.TrySetResult(); await navigation.WaitAsync(TimeSpan.FromSeconds(2));
            // Loaded headless runners can quantize/throttle 16 ms frame delays.
            // Input, old-deadline survival and unchanged preferences were asserted
            // above; this final wait checks eventual close, not frame cadence.
            try { await lifetime.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch
            {
                Console.Error.WriteLine($"macOS eventual close: visible={toast.IsVisible}, opacity={toast.Opacity}, " +
                    $"fading={toast.IsExitAnimationActive}, lifetime={lifetime.Status}, navigation={navigation.Status}, " +
                    $"calls={calls}, display={settings.ToastDisplayMs}, fadeOut={settings.FadeOutMs}");
                throw;
            }
            check(!toast.IsVisible && !toast.CanHandleLyricsPlusRequest,
                "macOS timed toast resumes its configured exit after navigation settles");
            await controller.ExecuteAsync(() => { shown++; return Task.CompletedTask; });
            check(shown == 2, "macOS next saved shortcut shows a new toast after actual timed close");
        }
        finally { release.TrySetResult(); toast.Close(); await Task.WhenAll(lifetime, navigation).WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private static async Task StrictKeyboardAndCloseAsync(Action<bool, string> check)
    {
        var calls = 0;
        var strict = false;
        CancellationToken actionToken = default;
        var toast = Create(Profile(), async (hotkey, token) =>
        { calls++; strict = !hotkey; actionToken = token; await Task.Delay(Timeout.Infinite, token); return new(true, false, "LyricsPlusRequested"); });
        var lifetime = toast.ShowToastAsync();
        try
        {
            await TimedToastWait.UntilAsync(() => toast.IsExitAnimationActive && toast.Opacity < .8);
            toast.FindControl<Button>("TitleLink")!.Focus();
            toast.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            check(calls == 0, "macOS held Enter cannot launch Lyrics Plus on key-down");
            toast.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await TimedToastWait.UntilAsync(() => calls == 1);
            check(strict && toast.Opacity == 1 && toast.IsVisible,
                "macOS Enter release during actual fade interrupts exit using the strict click/keyboard context");
            toast.Close(); await lifetime.WaitAsync(TimeSpan.FromSeconds(2));
            await toast.RequestLyricsPlusFromHotkeyAsync();
            check(actionToken.IsCancellationRequested && calls == 1,
                "macOS explicit close cancels a pending accepted request and prevents a late fallback");
        }
        finally { toast.Close(); await lifetime.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private static async Task EntryAndHiddenAsync(Action<bool, string> check)
    {
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toast = Create(Profile(fadeIn: 1200, fadeOut: 50), async (_, token) =>
        { calls++; await release.Task.WaitAsync(token); return new(true, false, "LyricsPlusRequested"); });
        var lifetime = toast.ShowToastAsync();
        Task request = Task.CompletedTask;
        try
        {
            request = toast.RequestLyricsPlusFromHotkeyAsync();
            await Task.Delay(800);
            check(calls == 1 && toast.IsVisible && toast.Opacity == 1 && !lifetime.IsCompleted,
                "macOS accepted shortcut interrupts entry animation and holds the real timed toast");
            release.TrySetResult(); await request;
            toast.Hide(); await toast.RequestLyricsPlusFromHotkeyAsync();
            check(calls == 1, "macOS hidden toast cannot own another saved shortcut action");
        }
        finally { release.TrySetResult(); toast.Close(); await Task.WhenAll(lifetime, request).WaitAsync(TimeSpan.FromSeconds(3)); }
    }
}

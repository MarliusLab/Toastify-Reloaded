using ToastifyReloaded.Shared;

internal static class ToastVisibilityShortcutTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var controller = new ToastShowHotkeyController();
        var visible = false;
        var shown = 0;
        var lyrics = 0;
        Task Show() { shown++; visible = true; return Task.CompletedTask; }
        using var lease = controller.Attach(() => visible, () => { lyrics++; return Task.CompletedTask; });
        await controller.ExecuteAsync(Show);
        // This exceeds a normal double-click interval. All three applications
        // compile this same controller: only current visibility owns the press.
        await Task.Delay(1200);
        await controller.ExecuteAsync(Show);
        check(visible && shown == 1 && lyrics == 1,
            "all-platform Show Toast opens Lyrics Plus after a delayed second press while the toast remains visible");
        await controller.ExecuteAsync(Show);
        check(shown == 1 && lyrics == 2,
            "a visible toast remains eligible after earlier Lyrics requests without a double-tap expiry");
        visible = false;
        await controller.ExecuteAsync(Show);
        check(shown == 2 && lyrics == 2,
            "hidden or expired toast visibility, rather than elapsed time between presses, returns the shortcut to Show Toast");
    }
}

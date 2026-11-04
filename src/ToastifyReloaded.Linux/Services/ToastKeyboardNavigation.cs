namespace ToastifyReloaded.Linux.Services;

internal enum ToastKeyboardAction { None, LyricsPlus, CurrentTrack }

/// <summary>Consumes button key-downs; navigation begins only after key/modifier release.</summary>
internal sealed class ToastKeyboardNavigation
{
    private string? _key;
    private ToastKeyboardAction _action;
    private bool _released;

    public bool KeyDown(string key, bool altShiftOnly)
    {
        var action = key is "Enter" or "Space" ? ToastKeyboardAction.LyricsPlus :
            key == "J" && altShiftOnly ? ToastKeyboardAction.CurrentTrack : ToastKeyboardAction.None;
        if (action == ToastKeyboardAction.None) return false;
        if (_key is null) { _key = key; _action = action; _released = false; }
        return true; // Repeats are consumed, without starting another action.
    }

    public ToastKeyboardAction KeyUp(string key, bool modifiersHeld)
    {
        if (_key == key) _released = true;
        if (!_released || modifiersHeld) return ToastKeyboardAction.None;
        var action = _action;
        Reset();
        return action;
    }

    public void Reset() { _key = null; _action = ToastKeyboardAction.None; _released = false; }

    public static Task RouteShowToastAsync(string configuredShortcut, bool wayland, bool visibleToast,
        Func<Task> showToast, Func<Task> currentTrack)
        => wayland && visibleToast && IsCurrentTrackChord(configuredShortcut)
            ? currentTrack() : showToast();

    public static bool IsCurrentTrackChord(string shortcut)
    {
        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts.Select(p => p.ToLowerInvariant()).ToHashSet()
            .SetEquals(["alt", "shift", "j"]);
    }
}

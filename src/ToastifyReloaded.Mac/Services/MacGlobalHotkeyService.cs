using SharpHook;
using SharpHook.Data;
using SharpHook.Native;
using SharpHook.Providers;
using ToastifyReloaded.Mac.Models;

namespace ToastifyReloaded.Mac.Services;

public sealed class MacGlobalHotkeyService : IDisposable
{
    private readonly SpotifyAppleScriptService _spotify;
    private readonly Func<Task>? _showToast;
    private readonly Func<bool>? _hasVisibleToast;
    private readonly Func<Task>? _nowPlaying;
    private readonly object _sync = new();
    private readonly HashSet<KeyCode> _pressed = new();
    private readonly Dictionary<Shortcut, Binding> _bindings = new();

    private SimpleGlobalHook? _hook;
    private Task? _hookTask;
    private int _forwardingNowPlaying;
    private int _generation;
    private readonly HashSet<Shortcut> _pending = new();

    public string Status { get; private set; } = "Hotkey globali non inizializzate.";
    public bool PermissionGranted { get; private set; }
    public int BindingCount { get { lock (_sync) return _bindings.Count; } }

    public MacGlobalHotkeyService(SpotifyAppleScriptService spotify, Func<Task>? showToast = null,
        Func<bool>? hasVisibleToast = null, Func<Task>? nowPlaying = null)
    {
        _spotify = spotify;
        _showToast = showToast;
        _hasVisibleToast = hasVisibleToast;
        _nowPlaying = nowPlaying;
    }

    public Task<(bool Success, string Message)> ApplyAsync(MacSettings settings)
    {
        DisposeHook();
        lock (_sync) _bindings.Clear();

        if (!settings.EnableGlobalHotkeys)
        {
            Status = "Hotkey globali disattivate.";
            return Task.FromResult((true, Status));
        }

        if (!OperatingSystem.IsMacOS())
        {
            Status = "Backend hotkey macOS disponibile solo su macOS.";
            return Task.FromResult((false, Status));
        }

        PermissionGranted = UioHook.IsAxApiEnabled(promptUserIfDisabled: true);
        if (!PermissionGranted)
        {
            Status = "Concedi a Toastify Reloaded l'accesso in Impostazioni di Sistema → Privacy e Sicurezza → Accessibilità, poi premi Salva.";
            return Task.FromResult((false, Status));
        }

        AddBinding(settings.HotkeyPlayPause, _spotify.PlayPauseAsync);
        AddBinding(settings.HotkeyNext, _spotify.NextAsync);
        AddBinding(settings.HotkeyPrevious, _spotify.PreviousAsync);
        AddBinding(settings.HotkeyVolumeUp, _spotify.VolumeUpAsync);
        AddBinding(settings.HotkeyVolumeDown, _spotify.VolumeDownAsync);
        AddBinding(settings.HotkeyMute, _spotify.ToggleMuteAsync);
        AddBinding(settings.HotkeySeekForward, _spotify.SeekForwardAsync);
        AddBinding(settings.HotkeySeekBackward, _spotify.SeekBackwardAsync);
        if (_showToast is not null) AddBinding(settings.HotkeyShowToast, _showToast, allowHeldModifiers: true);

        UioHookProvider.Instance.KeyTypedEnabled = false;

        _hook = new SimpleGlobalHook(
            GlobalHookType.Keyboard,
            runAsyncOnBackgroundThread: true);

        _hook.KeyPressed += Hook_KeyPressed;
        _hook.KeyReleased += Hook_KeyReleased;

        try
        {
            _hookTask = _hook.RunAsync();
            Status = $"Hotkey globali macOS attive ({_bindings.Count}).";
            return Task.FromResult((true, Status));
        }
        catch (Exception ex)
        {
            DisposeHook();
            Status = $"Impossibile avviare le hotkey globali: {ex.Message}";
            return Task.FromResult((false, Status));
        }
    }

    internal static Shortcut? ParseShortcut(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parts = text
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        EventMask modifiers = EventMask.None;
        string? keyText = null;

        foreach (var raw in parts)
        {
            var token = raw.Trim();

            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= EventMask.Ctrl;
            }
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("Option", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= EventMask.Alt;
            }
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= EventMask.Shift;
            }
            else if (token.Equals("Meta", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("Cmd", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("Command", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("Super", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= EventMask.Meta;
            }
            else
            {
                if (keyText is not null)
                    return null;

                keyText = token;
            }
        }

        if (string.IsNullOrWhiteSpace(keyText) || !TryParseKeyCode(keyText, out var keyCode))
            return null;

        return new Shortcut(keyCode, modifiers);
    }

    private static bool TryParseKeyCode(string token, out KeyCode keyCode)
    {
        var normalized = token.Trim();

        var aliases = new Dictionary<string, KeyCode>(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = KeyCode.VcSpace,
            ["Left"] = KeyCode.VcLeft,
            ["Right"] = KeyCode.VcRight,
            ["Up"] = KeyCode.VcUp,
            ["Down"] = KeyCode.VcDown,
            ["Enter"] = KeyCode.VcEnter,
            ["Return"] = KeyCode.VcEnter,
            ["Escape"] = KeyCode.VcEscape,
            ["Esc"] = KeyCode.VcEscape,
            ["Tab"] = KeyCode.VcTab,
            ["Home"] = KeyCode.VcHome,
            ["End"] = KeyCode.VcEnd,
            ["PageUp"] = KeyCode.VcPageUp,
            ["PageDown"] = KeyCode.VcPageDown,
            ["MediaPlay"] = KeyCode.VcMediaPlay,
            ["MediaNext"] = KeyCode.VcMediaNext,
            ["MediaPrevious"] = KeyCode.VcMediaPrevious,
            ["VolumeUp"] = KeyCode.VcVolumeUp,
            ["VolumeDown"] = KeyCode.VcVolumeDown,
            ["VolumeMute"] = KeyCode.VcVolumeMute
        };

        if (aliases.TryGetValue(normalized, out keyCode))
            return true;

        if (normalized.Length == 1 && char.IsLetterOrDigit(normalized[0]))
        {
            var enumName = "Vc" + char.ToUpperInvariant(normalized[0]);
            return Enum.TryParse(enumName, ignoreCase: true, out keyCode);
        }

        if (normalized.Length is 2 or 3 &&
            normalized.StartsWith('F') &&
            int.TryParse(normalized[1..], out var f) &&
            f is >= 1 and <= 20)
        {
            return Enum.TryParse("VcF" + f, ignoreCase: true, out keyCode);
        }

        return Enum.TryParse(
            normalized.StartsWith("Vc", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : "Vc" + normalized,
            ignoreCase: true,
            out keyCode);
    }

    private sealed record Binding(Func<Task> Action, bool AllowHeldModifiers);

    private void AddBinding(string text, Func<Task> action, bool allowHeldModifiers = false)
    {
        var shortcut = ParseShortcut(text);
        if (shortcut is not null)
            lock (_sync) _bindings.TryAdd(shortcut.Value, new(action, allowHeldModifiers));
    }

    private void Hook_KeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (e.IsEventSimulated) return;
        var key = e.Data.KeyCode;

        lock (_sync)
        {
            if (!_pressed.Add(key))
                return;
        }

        var modifiers = NormalizeModifiers(e.RawEvent.Mask);
        var shortcut = new Shortcut(key, modifiers);
        var isNowPlaying = key == KeyCode.VcJ && modifiers == (EventMask.Alt | EventMask.Shift);
        if (isNowPlaying && _hasVisibleToast?.Invoke() == true)
        {
            if (Volatile.Read(ref _forwardingNowPlaying) != 0) return;
            e.SuppressEvent = true;
            if (_nowPlaying is not null) QueueAfterRelease(shortcut, _nowPlaying);
        }
        else
        {
            Binding? binding;
            lock (_sync) _bindings.TryGetValue(shortcut, out binding);
            if (binding is not null) QueueAfterRelease(shortcut, binding.Action, binding.AllowHeldModifiers);
        }
    }

    private void QueueAfterRelease(Shortcut shortcut, Func<Task> action, bool allowHeldModifiers = false)
    {
        var generation = Volatile.Read(ref _generation);
        lock (_sync) if (!_pending.Add(shortcut)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    if (generation != Volatile.Read(ref _generation)) return;
                    bool released;
                    lock (_sync) released = CanDispatchAfterRelease(_pressed, allowHeldModifiers);
                    if (released) { await action(); return; }
                    await Task.Delay(25);
                }
            }
            catch { /* A permission or native shortcut failure must not escape the hook. */ }
            finally { lock (_sync) _pending.Remove(shortcut); }
        });
    }

    internal static bool CanDispatchAfterRelease(IEnumerable<KeyCode> pressed, bool allowHeldModifiers)
        => !pressed.Any(key => !allowHeldModifiers || key is not (KeyCode.VcLeftControl or KeyCode.VcRightControl or
            KeyCode.VcLeftAlt or KeyCode.VcRightAlt or KeyCode.VcLeftShift or KeyCode.VcRightShift or
            KeyCode.VcLeftMeta or KeyCode.VcRightMeta or KeyCode.VcCapsLock));

    public async Task<T> RunNowPlayingSuspendedAsync<T>(Func<Task<T>> action)
    {
        Interlocked.Increment(ref _forwardingNowPlaying);
        try { return await action(); }
        finally
        {
            // Native events can arrive just after osascript exits.
            await Task.Delay(200);
            Interlocked.Decrement(ref _forwardingNowPlaying);
        }
    }

    private void Hook_KeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        if (e.IsEventSimulated) return;
        lock (_sync)
            _pressed.Remove(e.Data.KeyCode);
    }

    private void DisposeHook()
    {
        Interlocked.Increment(ref _generation);
        if (_hook is null)
            return;

        try
        {
            _hook.KeyPressed -= Hook_KeyPressed;
            _hook.KeyReleased -= Hook_KeyReleased;
            _hook.Stop();
            _hook.Dispose();
        }
        catch
        {
        }
        finally
        {
            _hook = null;
            _hookTask = null;
            lock (_sync)
                _pressed.Clear();
        }
    }

    public void Dispose() => DisposeHook();

    internal static EventMask NormalizeModifiers(EventMask raw)
    {
        EventMask normalized = EventMask.None;

        if ((raw & EventMask.Ctrl) != 0)
            normalized |= EventMask.Ctrl;
        if ((raw & EventMask.Alt) != 0)
            normalized |= EventMask.Alt;
        if ((raw & EventMask.Shift) != 0)
            normalized |= EventMask.Shift;
        if ((raw & EventMask.Meta) != 0)
            normalized |= EventMask.Meta;

        return normalized;
    }

    public readonly record struct Shortcut(KeyCode KeyCode, EventMask Modifiers);
}

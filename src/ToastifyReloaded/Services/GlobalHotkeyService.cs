using System.Windows.Input;
using System.Windows.Interop;
using ToastifyReloaded.Models;
using ToastifyReloaded.Native;

namespace ToastifyReloaded.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private readonly IntPtr _windowHandle;
    private readonly HwndSource? _source;
    private readonly Func<int, uint, uint, bool> _registerHotkey;
    private readonly Action<int> _unregisterHotkey;
    private readonly Dictionary<int, HotkeyAction> _registrations = new();
    private HotkeyAction? _nowPlayingUserAction;
    private Action? _toastNowPlaying;
    private bool _nowPlayingRegistered;
    private bool _registeredForToast;
    private int _nowPlayingSuspensions;
    private int _nowPlayingId = 0x5000;
    private bool _disposed;

    public ToastNowPlayingHotkeyCoordinator ToastNowPlaying { get; }

    public event EventHandler<HotkeyAction>? HotkeyPressed;

    public GlobalHotkeyService()
    {
        // Use a dedicated hidden native window as the hotkey message sink.
        // This keeps RegisterHotKey independent from whichever application or
        // Toastify settings window currently has focus or is visible.
        var parameters = new HwndSourceParameters("ToastifyReloaded.GlobalHotkeySink")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000) // WS_POPUP, intentionally not WS_VISIBLE
        };

        _source = new HwndSource(parameters);
        _windowHandle = _source.Handle;
        if (_windowHandle == IntPtr.Zero)
        {
            _source.Dispose();
            throw new InvalidOperationException("Impossibile creare il ricevitore globale delle hotkey.");
        }

        _source.AddHook(WndProc);
        _registerHotkey = (id, modifiers, key) => NativeMethods.RegisterHotKey(_windowHandle, id, modifiers, key);
        _unregisterHotkey = id => { NativeMethods.UnregisterHotKey(_windowHandle, id); };
        ToastNowPlaying = new ToastNowPlayingHotkeyCoordinator(
            SetToastNowPlaying, ClearToastNowPlaying, SuspendNowPlaying);
    }

    internal GlobalHotkeyService(Func<int, uint, uint, bool> registerHotkey, Action<int> unregisterHotkey,
        Func<TimeSpan, Task>? delay = null, Func<long>? clock = null)
    {
        _registerHotkey = registerHotkey;
        _unregisterHotkey = unregisterHotkey;
        ToastNowPlaying = new ToastNowPlayingHotkeyCoordinator(
            SetToastNowPlaying, ClearToastNowPlaying, SuspendNowPlaying, delay, clock);
    }

    public IReadOnlyList<string> RegisterAll(IEnumerable<HotkeyBinding> bindings)
    {
        UnregisterAll();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var id = 1000;

        foreach (var binding in bindings)
        {
            if (!binding.Enabled)
                continue;

            if (!seen.Add(binding.Shortcut.Trim()))
            {
                errors.Add($"{binding.ActionLabel}: scorciatoia duplicata ({binding.Shortcut}).");
                continue;
            }

            if (!TryParse(binding.Shortcut, out var modifiers, out var key, out var error))
            {
                errors.Add($"{binding.ActionLabel}: {error}");
                continue;
            }

            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (modifiers == (NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT) && vk == 0x4A)
            {
                if (_nowPlayingUserAction.HasValue)
                {
                    errors.Add($"{binding.ActionLabel}: scorciatoia duplicata ({binding.Shortcut}).");
                    continue;
                }
                // Keep the user's assignment intact. Visible toasts temporarily
                // take precedence; suspension releases only this exact chord.
                _nowPlayingUserAction = binding.Action;
                continue;
            }
            if (!_registerHotkey(id, modifiers | NativeMethods.MOD_NOREPEAT, vk))
            {
                errors.Add($"{binding.ActionLabel}: {binding.Shortcut} è già usata da Windows o da un altro programma.");
                continue;
            }

            _registrations[id] = binding.Action;
            id++;
        }

        var nowPlayingAvailable = RefreshNowPlayingRegistration();
        if (_nowPlayingUserAction.HasValue && !nowPlayingAvailable && _nowPlayingSuspensions == 0)
            errors.Add("Alt+Shift+J è già usata da Windows o da un altro programma.");
        ToastNowPlaying.RefreshRegistration();

        return errors;
    }

    private static bool TryParse(string shortcut, out uint modifiers, out Key key, out string error)
    {
        modifiers = 0;
        key = Key.None;
        error = string.Empty;

        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            error = "usa il formato Ctrl+Alt+T.";
            return false;
        }

        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= NativeMethods.MOD_ALT;
                    break;
                case "shift":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    modifiers |= NativeMethods.MOD_WIN;
                    break;
                default:
                    error = $"modificatore non riconosciuto: {part}.";
                    return false;
            }
        }

        if (modifiers == 0)
        {
            error = "serve almeno un modificatore (Ctrl, Alt, Shift o Win).";
            return false;
        }

        var keyName = parts[^1];
        if (!Enum.TryParse<Key>(keyName, ignoreCase: true, out key) || key == Key.None)
        {
            error = $"tasto non riconosciuto: {keyName}.";
            return false;
        }

        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY) handled = DispatchHotkey(wParam.ToInt32());
        return IntPtr.Zero;
    }

    internal bool DispatchHotkey(int id)
    {
        if (id == _nowPlayingId && _nowPlayingRegistered)
        {
            if (_toastNowPlaying is not null) _toastNowPlaying();
            else if (_nowPlayingUserAction is { } nowPlayingAction) HotkeyPressed?.Invoke(this, nowPlayingAction);
            return true;
        }
        if (_registrations.TryGetValue(id, out var action))
        {
            HotkeyPressed?.Invoke(this, action);
            return true;
        }
        return false;
    }

    private void UnregisterAll()
    {
        foreach (var id in _registrations.Keys)
            _unregisterHotkey(id);
        _registrations.Clear();
        _nowPlayingUserAction = null;
        RefreshNowPlayingRegistration();
    }

    private bool SetToastNowPlaying(Action callback)
    {
        _toastNowPlaying = callback;
        return RefreshNowPlayingRegistration();
    }

    private void ClearToastNowPlaying()
    {
        _toastNowPlaying = null;
        RefreshNowPlayingRegistration();
    }

    private bool RefreshNowPlayingRegistration()
    {
        var wanted = !_disposed && _nowPlayingSuspensions == 0 &&
            (_toastNowPlaying is not null || _nowPlayingUserAction.HasValue);
        var forToast = _toastNowPlaying is not null;
        if (_nowPlayingRegistered && (!wanted || _registeredForToast != forToast))
        {
            _unregisterHotkey(_nowPlayingId);
            _nowPlayingRegistered = false;
        }
        if (!wanted || _nowPlayingRegistered) return _nowPlayingRegistered;
        // Change IDs on every acquisition so queued messages from an old toast
        // cannot turn into the user's playback action after ownership changes.
        NextNowPlayingId();
        _nowPlayingRegistered = _registerHotkey(_nowPlayingId,
            NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, 0x4A);
        _registeredForToast = forToast;
        return _nowPlayingRegistered;
    }

    private IDisposable SuspendNowPlaying()
    {
        var canForward = _nowPlayingRegistered;
        _nowPlayingSuspensions++;
        RefreshNowPlayingRegistration();
        if (!canForward && !_disposed)
        {
            // Even with global hotkeys disabled, forwarding this chord would
            // trigger another application's registration. Probe availability
            // first, and release only a probe which this host actually owns.
            var probe = NextNowPlayingId();
            canForward = _registerHotkey(probe,
                NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, 0x4A);
            if (canForward) _unregisterHotkey(probe);
        }
        return new ResumeNowPlaying(this, canForward);
    }

    private int NextNowPlayingId()
        => _nowPlayingId = _nowPlayingId >= 0x7fff ? 0x5000 : _nowPlayingId + 1;

    private sealed class ResumeNowPlaying(GlobalHotkeyService owner, bool canForward) : IToastShortcutSuspension
    {
        private bool _disposed;
        public bool CanForwardShortcut { get; } = canForward;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._nowPlayingSuspensions--;
            owner.RefreshNowPlayingRegistration();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ToastNowPlaying.Dispose();
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
    }
}

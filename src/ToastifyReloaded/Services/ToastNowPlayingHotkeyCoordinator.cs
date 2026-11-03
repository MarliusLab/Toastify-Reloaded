namespace ToastifyReloaded.Services;

public interface IToastShortcutSuspension : IDisposable
{
    bool CanForwardShortcut { get; }
}

/// <summary>Temporary Alt+Shift+J ownership shared by the currently visible toasts.</summary>
public sealed class ToastNowPlayingHotkeyCoordinator : IDisposable
{
    private readonly Func<Action, bool> _register;
    private readonly Action _unregister;
    private readonly Func<IDisposable> _suspendChord;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Func<long> _clock;
    private readonly List<Lease> _leases = new();
    private bool _enabled = true;
    private bool _disposed;
    private bool _busy;
    private bool _dispatching;
    private bool _canForwardShortcut;
    private long? _lastRun;
    private long? _lastGlobal;
    private const int QuiescenceMs = 200;

    public ToastNowPlayingHotkeyCoordinator(
        Func<Action, bool> register,
        Action unregister,
        Func<IDisposable> suspendChord,
        Func<TimeSpan, Task>? delay = null,
        Func<long>? clock = null)
    {
        _register = register;
        _unregister = unregister;
        _suspendChord = suspendChord;
        _delay = delay ?? Task.Delay;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled == value) return; _enabled = value; RefreshRegistration(); }
    }

    public bool IsGlobalRegistered { get; private set; }
    public bool CanForwardShortcut => _busy && _canForwardShortcut;

    public IDisposable Attach(Func<Task> callback, Action<bool>? availabilityChanged = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lease = new Lease(this, callback, availabilityChanged);
        _leases.Add(lease);
        RefreshRegistration();
        return lease;
    }

    /// <summary>
    /// Every click/local/global activation uses this gate. It releases this
    /// chord's user and toast registrations while Spotify receives its own
    /// shortcut, then waits for queued input before restoring ownership.
    /// </summary>
    public async Task<bool> RunSuspendedAsync(Func<Task> action)
    {
        if (_disposed || _busy || Recently(_lastRun)) return false;
        _busy = true;
        _canForwardShortcut = false;
        _lastRun = _clock();
        IDisposable? suspension = null;
        try
        {
            suspension = _suspendChord();
            _canForwardShortcut = suspension is not IToastShortcutSuspension guarded || guarded.CanForwardShortcut;
            RefreshRegistration();
            await action();
            return true;
        }
        finally
        {
            // Do not re-register immediately after SendInput: the injected
            // Alt+Shift+J must reach Spotify instead of recursively reaching us.
            try { await _delay(TimeSpan.FromMilliseconds(QuiescenceMs)); }
            finally
            {
                try { suspension?.Dispose(); }
                finally { _canForwardShortcut = false; _busy = false; RefreshRegistration(); }
            }
        }
    }

    internal void RefreshRegistration()
    {
        var available = false;
        try
        {
            if (!_disposed && _enabled && !_busy && _leases.Count > 0)
                available = _register(OnGlobalPressed);
            else
                _unregister();
        }
        catch { available = false; }
        IsGlobalRegistered = available;
        foreach (var lease in _leases.ToArray()) lease.Notify(available);
    }

    private bool Recently(long? timestamp) => timestamp.HasValue && _clock() - timestamp.Value < QuiescenceMs;

    private void OnGlobalPressed() => _ = DispatchGlobalAsync();

    private async Task DispatchGlobalAsync()
    {
        if (_disposed || !_enabled || _busy || _dispatching || _leases.Count == 0 ||
            Recently(_lastRun) || Recently(_lastGlobal)) return;
        _lastGlobal = _clock();
        _dispatching = true;
        var owner = _leases[^1];
        try
        {
            // The toast's callback calls RunSuspendedAsync itself. Taking the
            // execution gate here would suppress that same callback.
            await owner.Callback();
        }
        catch { /* The toast reports errors; never escape a native hotkey hook. */ }
        finally { _dispatching = false; }
    }

    private void Detach(Lease lease)
    {
        if (_leases.Remove(lease)) RefreshRegistration();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var lease in _leases.ToArray()) lease.Notify(false);
        _leases.Clear();
        RefreshRegistration();
    }

    private sealed class Lease(ToastNowPlayingHotkeyCoordinator owner,
        Func<Task> callback, Action<bool>? availabilityChanged) : IDisposable
    {
        private bool _disposed;
        public Func<Task> Callback { get; } = callback;
        public void Notify(bool available)
        {
            if (_disposed) return;
            try { availabilityChanged?.Invoke(available); }
            catch { /* A closing UI observer must not affect hotkey ownership. */ }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.Detach(this);
        }
    }
}

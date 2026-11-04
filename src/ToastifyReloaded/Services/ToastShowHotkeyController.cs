namespace ToastifyReloaded.Services;

/// <summary>Only the configured Show Toast hotkey gains the visible-toast second action.</summary>
internal sealed class ToastShowHotkeyController : IDisposable
{
    private readonly List<Lease> _toasts = new();
    private bool _executing;
    private bool _disposed;

    public IDisposable Attach(Func<bool> canHandle, Func<Task> requestLyrics)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lease = new Lease(this, canHandle, requestLyrics);
        _toasts.Add(lease);
        return lease;
    }

    public async Task ExecuteAsync(Func<Task> showToast)
    {
        if (_disposed || _executing) return;
        _executing = true;
        try
        {
            var owner = _toasts.ToArray().LastOrDefault(t => !t.Disposed && t.CanHandle());
            if (owner is not null)
            {
                // Once a visible toast owns this press, closing/fading/busy races
                // consume it. Its own request guard must never create a new toast.
                await owner.RequestLyrics();
            }
            else
                await showToast();
        }
        finally { _executing = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var toast in _toasts.ToArray()) toast.Dispose();
    }

    private sealed class Lease(ToastShowHotkeyController owner, Func<bool> canHandle, Func<Task> requestLyrics) : IDisposable
    {
        public bool Disposed { get; private set; }
        public Func<bool> CanHandle { get; } = canHandle;
        public Func<Task> RequestLyrics { get; } = requestLyrics;
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            owner._toasts.Remove(this);
        }
    }
}

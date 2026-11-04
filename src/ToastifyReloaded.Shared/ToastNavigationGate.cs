namespace ToastifyReloaded.Shared;

/// <summary>UI-thread gate shared by all visible platform toasts.</summary>
public sealed class ToastNavigationGate : IDisposable
{
    private bool _busy;
    private bool _disposed;

    public async Task<bool> RunAsync(Func<Task> action)
    {
        if (_disposed || _busy) return false;
        _busy = true;
        try { await action(); return true; }
        finally { _busy = false; }
    }

    public void Dispose() => _disposed = true;
}

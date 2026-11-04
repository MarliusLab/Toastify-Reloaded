using System.Threading;

namespace ToastifyReloaded.Shared;

/// <summary>One application instance per platform and login session; updater hooks run before this lease.</summary>
public sealed class SingleInstanceLease : IDisposable
{
    private readonly Mutex _mutex;
    private SingleInstanceLease(Mutex mutex) => _mutex = mutex;

    public static SingleInstanceLease? TryAcquire(string platform)
    {
        if (platform is not ("linux" or "macos")) throw new ArgumentOutOfRangeException(nameof(platform));
        var mutex = new Mutex(true, "ToastifyReloaded.SingleInstance." + platform, out var created);
        if (created) return new SingleInstanceLease(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
}

using Tmds.DBus;

namespace ToastifyReloaded.Linux.Services;

[DBusInterface("io.github.MarliusLab.ToastifyReloaded.Control")]
public interface IToastControl : IDBusObject
{
    Task ShowToastAsync();
    Task OpenCurrentTrackAsync();
}

/// <summary>Session-bus entry point for xbindkeys; contains no commands or track metadata.</summary>
public sealed class ToastControlService(Func<Task> showToast, Func<Task>? openCurrentTrack = null) : IToastControl, IDisposable
{
    public const string BusName = "io.github.MarliusLab.ToastifyReloaded";
    public ObjectPath ObjectPath => new("/io/github/MarliusLab/ToastifyReloaded");
    private Connection? _connection;
    public async Task StartAsync()
    {
        _connection = new Connection(Address.Session);
        await _connection.ConnectAsync();
        await _connection.RegisterObjectAsync(this);
        await _connection.RegisterServiceAsync(BusName);
    }
    public Task ShowToastAsync() => showToast();
    public Task OpenCurrentTrackAsync() => openCurrentTrack?.Invoke() ?? Task.CompletedTask;
    public void Dispose() { _connection?.Dispose(); _connection = null; }
}

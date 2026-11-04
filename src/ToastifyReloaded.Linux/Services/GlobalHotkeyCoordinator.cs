using ToastifyReloaded.Linux.Models;

namespace ToastifyReloaded.Linux.Services;

public sealed class GlobalHotkeyCoordinator : IDisposable
{
    private readonly XbindkeysService _x11;
    private readonly XdgGlobalShortcutsService _wayland;

    public GlobalHotkeyCoordinator(
        XbindkeysService x11,
        XdgGlobalShortcutsService wayland)
    {
        _x11 = x11;
        _wayland = wayland;
    }

    public string SessionType
    {
        get
        {
            if (_wayland.IsWayland)
                return "wayland";

            return _x11.SessionType;
        }
    }

    public bool IsWayland => _wayland.IsWayland;

    public Task<(bool Success, string Message)> SetToastNavigationScopeAsync(bool active)
        => IsWayland
            ? Task.FromResult((true, active ? "Wayland: Alt+Shift+J disponibile nel toast con focus; la scorciatoia globale Show Toast conserva l'azione Lyrics Plus alla seconda pressione." : string.Empty))
            : _x11.SetToastNavigationScopeAsync(active);

    public async Task<(bool Success, string Message)> ApplyAsync(
        LinuxSettings settings)
    {
        if (IsWayland)
        {
            _x11.Stop();
            return await _wayland.ApplyAsync(settings);
        }

        await _wayland.StopAsync();
        return await _x11.ApplyAsync(settings);
    }

    public void Dispose()
    {
        _x11.Dispose();
        _wayland.Dispose();
    }
}

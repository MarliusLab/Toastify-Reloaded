using System.Diagnostics;
using System.Text;
using ToastifyReloaded.Linux.Models;

namespace ToastifyReloaded.Linux.Services;

public sealed class XbindkeysService : IDisposable
{
    private readonly ProcessService _processService;
    private readonly LinuxSettingsService _settingsService;
    private Process? _process;
    private readonly SemaphoreSlim _configuration = new(1, 1);
    private LinuxSettings? _settings;
    private bool _toastNavigationActive, _disposed;

    public XbindkeysService(
        ProcessService processService,
        LinuxSettingsService settingsService)
    {
        _processService = processService;
        _settingsService = settingsService;
    }

    public string SessionType =>
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")?.Trim().ToLowerInvariant()
        ?? "unknown";

    public bool IsWayland => SessionType == "wayland";

    public async Task<(bool Success, string Message)> ApplyAsync(LinuxSettings settings)
    {
        _settings = settings;
        return await ApplyCurrentAsync();
    }

    public Task<(bool Success, string Message)> SetToastNavigationScopeAsync(bool active)
    {
        if (_toastNavigationActive == active || _settings is null)
        { _toastNavigationActive = active; return Task.FromResult((true, string.Empty)); }
        _toastNavigationActive = active;
        return ApplyCurrentAsync();
    }

    private async Task<(bool Success, string Message)> ApplyCurrentAsync()
    {
        await _configuration.WaitAsync();
        try
        {
        if (_disposed || _settings is not { } settings) return (false, "Hotkey chiuse.");
        await Task.Run(Stop);

        if (!settings.EnableX11GlobalHotkeys)
            return (true, "Hotkey globali disattivate.");

        if (IsWayland)
            return (
                false,
                "Sessione Wayland rilevata: il backend xbindkeys è riservato a X11/XWayland. Su Wayland viene usato XDG Global Shortcuts Portal.");

        if (!await _processService.ExistsAsync("xbindkeys"))
            return (
                false,
                "xbindkeys non trovato. Installa xbindkeys oppure usa il pacchetto .deb, che lo dichiara come dipendenza.");

        Directory.CreateDirectory(_settingsService.ConfigDirectory);
        var configPath = Path.Combine(
            _settingsService.ConfigDirectory,
            "xbindkeysrc");

        await File.WriteAllTextAsync(
            configPath,
            BuildConfig(settings, _toastNavigationActive),
            Encoding.UTF8);

        if (_disposed) return (false, "Hotkey chiuse.");

        var startInfo = new ProcessStartInfo
        {
            FileName = "xbindkeys",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(configPath);

        _process = Process.Start(startInfo);
        var nativeError = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_process is not null)
        {
            // Drain redirected streams without allowing native diagnostic output
            // to block the owned xbindkeys process.
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, e) =>
            { if (!string.IsNullOrWhiteSpace(e.Data)) nativeError.TrySetResult(e.Data[..Math.Min(e.Data.Length, 512)]); };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            await Task.WhenAny(nativeError.Task, Task.Delay(150));
            if (_disposed) { Stop(); return (false, "Hotkey chiuse."); }
            if (nativeError.Task.IsCompletedSuccessfully)
                return (false, "X11: impossibile confermare tutte le scorciatoie (possibile conflitto): " + nativeError.Task.Result);
            if (_process is null || _process.HasExited)
                return (false, "xbindkeys si è chiuso; verifica il server X11 e le scorciatoie già occupate.");
        }

        return _process is not null
            ? (true, _toastNavigationActive
                ? "Hotkey X11 attive; Alt+Shift+J temporaneo richiesto al server X11 mentre il toast è visibile."
                : "Hotkey globali X11 attive.")
            : (false, "Impossibile avviare xbindkeys.");
        }
        finally { _configuration.Release(); }
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(1500);
            }
        }
        catch
        {
            // Best effort.
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    internal static string BuildConfig(LinuxSettings s, bool toastNavigationActive = false)
    {
        var entries = new[]
        {
            (s.HotkeyPlayPause, "playerctl --player=spotify play-pause"),
            (s.HotkeyNext, "playerctl --player=spotify next"),
            (s.HotkeyPrevious, "playerctl --player=spotify previous"),
            (s.HotkeyVolumeUp, "playerctl --player=spotify volume 0.05+"),
            (s.HotkeyVolumeDown, "playerctl --player=spotify volume 0.05-"),
            (s.HotkeyMute, "playerctl --player=spotify volume 0"),
            (s.HotkeySeekForward, "playerctl --player=spotify position 10+"),
            (s.HotkeySeekBackward, "playerctl --player=spotify position 10-"),
            (s.HotkeyShowToast, "gdbus call --session --dest io.github.MarliusLab.ToastifyReloaded --object-path /io/github/MarliusLab/ToastifyReloaded --method io.github.MarliusLab.ToastifyReloaded.Control.ShowToast")
        };

        var builder = new StringBuilder();
        builder.AppendLine("# Generated by Toastify Reloaded Linux");
        builder.AppendLine();

        foreach (var (shortcut, command) in entries)
        {
            // The overlay never edits saved settings. Rebuilding after the last
            // visible lease restores any original matching assignment verbatim.
            if (toastNavigationActive && ToastKeyboardNavigation.IsCurrentTrackChord(shortcut)) continue;
            if (string.IsNullOrWhiteSpace(shortcut) || shortcut.Any(char.IsControl) || shortcut.Contains('"') || shortcut.Contains('\\'))
                continue;

            builder.Append('"').Append(command).AppendLine("\"");
            builder.Append("  ");
            if (command.StartsWith("gdbus call", StringComparison.Ordinal)) builder.Append("Release + ");
            builder.AppendLine(ToXbindkeys(shortcut));
            builder.AppendLine();
        }

        if (toastNavigationActive)
        {
            builder.AppendLine("\"gdbus call --session --dest io.github.MarliusLab.ToastifyReloaded --object-path /io/github/MarliusLab/ToastifyReloaded --method io.github.MarliusLab.ToastifyReloaded.Control.OpenCurrentTrack\"");
            builder.AppendLine("  Release + Alt+Shift + j");
        }

        return builder.ToString();
    }

    private static string ToXbindkeys(string shortcut)
    {
        var parts = shortcut
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
            return shortcut;

        var modifiers = new List<string>();
        var key = parts[^1];

        foreach (var part in parts[..^1])
        {
            modifiers.Add(part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => "Control",
                "alt" => "Alt",
                "shift" => "Shift",
                "super" or "win" or "meta" => "Mod4",
                _ => part
            });
        }

        var mappedKey = key.ToLowerInvariant() switch
        {
            "space" => "space",
            "right" => "Right",
            "left" => "Left",
            "up" => "Up",
            "down" => "Down",
            _ => key.Length == 1 ? key.ToLowerInvariant() : key
        };

        return modifiers.Count == 0
            ? mappedKey
            : string.Join('+', modifiers) + " + " + mappedKey;
    }

    public void Dispose() { _disposed = true; Stop(); }
}

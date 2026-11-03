using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ToastifyReloaded.Models;

namespace ToastifyReloaded.Services;

public sealed partial class SpotifyInstallationService
{
    private readonly Func<SpotifyInstallationInfo> _desktopProbe;
    private readonly Func<CancellationToken, Task<SpotifyInstallationInfo?>> _storeProbe;
    private readonly TimeSpan _timeout;

    public SpotifyInstallationService()
        : this(FindDesktopSpotify, TryGetStoreSpotifyAsync, TimeSpan.FromSeconds(10))
    {
    }

    public SpotifyInstallationService(
        Func<SpotifyInstallationInfo> desktopProbe,
        Func<CancellationToken, Task<SpotifyInstallationInfo?>> storeProbe,
        TimeSpan? timeout = null)
    {
        _desktopProbe = desktopProbe;
        _storeProbe = storeProbe;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<SpotifyInstallationInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            // File version/registry/process probes can block. Keep all of them
            // off the UI thread and bound the overall detection wait as well.
            return await Task.Run(async () =>
            {
                var desktop = _desktopProbe();
                if (desktop.IsDetected) return desktop;
                linked.Token.ThrowIfCancellationRequested();
                return await _storeProbe(linked.Token).ConfigureAwait(false) ?? SpotifyInstallationInfo.NotFound;
            }, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SpotifyInstallationInfo.NotFound;
        }
    }

    private static SpotifyInstallationInfo FindDesktopSpotify()
    {
        var running = TryGetRunningSpotify();
        if (running.IsDetected)
            return running;

        foreach (var path in GetClassicSpotifyPaths())
        {
            if (!File.Exists(path))
                continue;

            var version = TryGetFileVersion(path);
            if (!string.IsNullOrWhiteSpace(version))
                return new SpotifyInstallationInfo(version, "Desktop", path);
        }

        return SpotifyInstallationInfo.NotFound;
    }

    private static SpotifyInstallationInfo TryGetRunningSpotify()
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName("Spotify"); }
        catch { return SpotifyInstallationInfo.NotFound; }
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    var version = TryGetFileVersion(path);
                    if (!string.IsNullOrWhiteSpace(version))
                        return new SpotifyInstallationInfo(version,
                            path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ? "Microsoft Store" : "Processo attivo", path);
                }
                catch
                {
                    // Microsoft Store processes can deny MainModule access.
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        return SpotifyInstallationInfo.NotFound;
    }

    private static IEnumerable<string> GetClassicSpotifyPaths()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(roaming, "Spotify", "Spotify.exe");
        yield return Path.Combine(local, "Spotify", "Spotify.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Spotify", "Spotify.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Spotify", "Spotify.exe");
        var scoop = Environment.GetEnvironmentVariable("SCOOP");
        yield return Path.Combine(string.IsNullOrWhiteSpace(scoop)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop") : scoop,
            "apps", "spotify", "current", "Spotify.exe");
        foreach (var path in GetRegisteredSpotifyPaths()) yield return path;
    }

    private static IEnumerable<string> GetRegisteredSpotifyPaths()
    {
        var paths = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Spotify.exe");
                if (key?.GetValue(null) is string path && !string.IsNullOrWhiteSpace(path))
                    paths.Add(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            }
            catch { }
        }
        return paths;
    }

    private static string TryGetFileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var version = NormalizeVersion(info.FileVersion ?? string.Empty);
            return string.IsNullOrWhiteSpace(version)
                ? NormalizeVersion(info.ProductVersion ?? string.Empty)
                : version;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<SpotifyInstallationInfo?> TryGetStoreSpotifyAsync(CancellationToken cancellationToken)
    {
        const string command = "$ErrorActionPreference='Stop'; $p = Get-AppxPackage -Name SpotifyAB.SpotifyMusic -ErrorAction SilentlyContinue | Sort-Object Version -Descending | Select-Object -First 1; if ($p) { @{Version=$p.Version.ToString(); InstallLocation=$p.InstallLocation} | ConvertTo-Json -Compress }";
        try
        {
            var result = await PowerShellService.RunHiddenCommandAsync(command, TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
            return result.Success ? ParseStorePackageOutput(result.StandardOutput) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    public static SpotifyInstallationInfo? ParseStorePackageOutput(string output)
    {
        try
        {
            using var json = JsonDocument.Parse(output);
            var version = NormalizeVersion(json.RootElement.GetProperty("Version").GetString() ?? "");
            if (string.IsNullOrWhiteSpace(version)) return null;
            string? executable = null;
            if (json.RootElement.TryGetProperty("InstallLocation", out var locationNode) && locationNode.ValueKind == JsonValueKind.String)
            {
                var location = locationNode.GetString();
                if (!string.IsNullOrWhiteSpace(location) && Path.IsPathFullyQualified(location))
                    executable = Path.Combine(location, "Spotify.exe");
            }
            return new SpotifyInstallationInfo(version, "Microsoft Store", executable);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static string NormalizeVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var match = VersionRegex().Match(text);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    [GeneratedRegex(@"\A\s*(\d+(?:\.\d+){1,4})(?:\.[A-Za-z][A-Za-z0-9]*)?\s*\z")]
    private static partial Regex VersionRegex();
}

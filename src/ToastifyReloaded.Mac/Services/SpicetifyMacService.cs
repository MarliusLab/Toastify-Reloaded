using System.Text.RegularExpressions;

namespace ToastifyReloaded.Mac.Services;

public sealed class SpicetifyMacService
{
    private readonly ProcessService _process;
    private readonly SemaphoreSlim _maintenance = new(1, 1);
    public SpicetifyMacService(ProcessService process) => _process = process;

    private static string? ResolveExecutable()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p, "spicetify"))
            .Concat(new[] { Path.Combine(home, ".spicetify", "spicetify"),
                Path.Combine(home, ".local", "bin", "spicetify"), "/opt/homebrew/bin/spicetify",
                "/usr/local/bin/spicetify", "/usr/bin/spicetify" });
        return candidates.FirstOrDefault(File.Exists);
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ResolveExecutable() is not null);
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var executable = ResolveExecutable();
        if (executable is null) return "not available";
        try
        {
            var result = await _process.RunAsync(executable, new[] { "-v" }, cancellationToken, TimeSpan.FromSeconds(3));
            if (result.ExitCode != 0) return "not available";
            var match = Regex.Match(result.StdOut, @"(?m)^\s*v?(\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?)\s*$");
            return match.Success ? match.Groups[1].Value : "not available";
        }
        catch (OperationCanceledException) { throw; }
        catch { return "not available"; }
    }

    public Task<(bool Success, string Message)> RepairAsync(CancellationToken cancellationToken = default)
        => RepairAfterSpotifyUpdateAsync(false, cancellationToken);

    public Task<(bool Success, string Message)> EnableLyricsAsync(CancellationToken cancellationToken = default)
        => RunMaintenanceAsync(keepLyrics: true, repair: false, cancellationToken);

    public Task<(bool Success, string Message)> RepairAfterSpotifyUpdateAsync(bool keepLyricsPlus,
        CancellationToken cancellationToken = default)
        => RunMaintenanceAsync(keepLyricsPlus, repair: true, cancellationToken);

    private async Task<(bool Success, string Message)> RunMaintenanceAsync(bool keepLyrics, bool repair, CancellationToken token)
    {
        await _maintenance.WaitAsync(token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var executable = ResolveExecutable();
            if (executable is null) return (false, "Spicetify non trovato.");
            await EnsureSpotifyPathAsync(executable, operation.Token);
            if (repair)
            {
                // Package-manager installations may not permit self-upgrade.
                try { _ = await RunAsync(executable, new[] { "upgrade" }, operation.Token, 45); }
                catch (OperationCanceledException) { throw; }
                catch { }
                var primary = await RunAsync(executable, new[] { "backup", "apply" }, operation.Token, 75);
                if (primary.ExitCode != 0)
                {
                    var fallback = await RunAsync(executable, new[] { "restore", "backup", "apply" }, operation.Token, 75);
                    if (fallback.ExitCode != 0) return (false, Error(fallback));
                }
            }
            if (keepLyrics)
            {
                var config = await RunAsync(executable, new[] { "config", "custom_apps", "lyrics-plus" }, operation.Token, 15);
                if (config.ExitCode != 0) return (false, Error(config));
                var apply = await RunAsync(executable, new[] { "apply" }, operation.Token, 75);
                if (apply.ExitCode != 0) return (false, Error(apply));
            }
            return (true, repair ? "Riparazione Spicetify completata." : "Lyrics Plus applicato.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return (false, "La manutenzione Spicetify ha superato il tempo massimo."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return (false, error.Message); }
        finally { _maintenance.Release(); }
    }

    private Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string executable, string[] args,
        CancellationToken token, int seconds) => _process.RunAsync(executable, args, token, TimeSpan.FromSeconds(seconds));
    private static string Error((int ExitCode, string StdOut, string StdErr) result)
        => !string.IsNullOrWhiteSpace(result.StdErr) ? result.StdErr : result.StdOut;

    private async Task EnsureSpotifyPathAsync(string executable, CancellationToken token)
    {
        // Preserve an existing valid custom Spotify path before supplying a
        // conventional location for an unconfigured installation.
        var configResult = await RunAsync(executable, new[] { "-c" }, token, 3);
        var configPath = configResult.StdOut.Trim();
        if (File.Exists(configPath))
        {
            foreach (var line in await File.ReadAllLinesAsync(configPath, token))
            {
                var match = Regex.Match(line, @"^\s*spotify_path\s*=\s*(.+)$");
                if (match.Success && Directory.Exists(match.Groups[1].Value.Trim())) return;
            }
        }
        else return; // Unknown configuration: never overwrite it speculatively.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var app in new[] { "/Applications/Spotify.app", Path.Combine(home, "Applications", "Spotify.app") })
        {
            var resources = Path.Combine(app, "Contents", "Resources");
            if (!Directory.Exists(resources)) continue;
            var result = await RunAsync(executable, new[] { "config", "spotify_path", resources }, token, 10);
            if (result.ExitCode != 0) throw new IOException(Error(result));
            return;
        }
    }

}

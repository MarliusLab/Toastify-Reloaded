using System.Diagnostics;
using System.Text.RegularExpressions;
using ToastifyReloaded.Models;

namespace ToastifyReloaded.Services;

public sealed class CompatibilityRepairService
{
    private readonly WindowsExecutableLocator _executables;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<PowerShellCommandResult>> _run;
    private readonly Func<CancellationToken, Task> _stopSpotify;
    private readonly Action _launchSpotify;

    public CompatibilityRepairService()
        : this(new WindowsExecutableLocator(), PowerShellService.RunHiddenExecutableAsync, StopSpotifyAsync, LaunchSpotify)
    {
    }

    public CompatibilityRepairService(
        WindowsExecutableLocator executables,
        Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<PowerShellCommandResult>> run,
        Func<CancellationToken, Task>? stopSpotify = null,
        Action? launchSpotify = null)
    {
        _executables = executables;
        _run = run;
        _stopSpotify = stopSpotify ?? StopSpotifyAsync;
        _launchSpotify = launchSpotify ?? LaunchSpotify;
    }

    public async Task<string> GetSpicetifyVersionAsync(CancellationToken cancellationToken = default)
        => (await ProbeSpicetifyAsync(cancellationToken).ConfigureAwait(false)).Version;

    private async Task<(string Version, string? Path)> ProbeSpicetifyAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var candidates = await Task.Run(_executables.FindSpicetifyCandidates, linked.Token)
                .WaitAsync(linked.Token).ConfigureAwait(false);
            foreach (var candidate in candidates)
            {
                try
                {
                    var result = await _run(candidate, new[] { "--version" }, TimeSpan.FromSeconds(3), linked.Token)
                        .WaitAsync(linked.Token).ConfigureAwait(false);
                    if (!result.Success) continue;
                    var match = Regex.Match(result.StandardOutput,
                        @"(?im)^\s*(?:spicetify\s+)?v?(\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?)\s*$",
                        RegexOptions.CultureInvariant);
                    if (!match.Success) continue;
                    return (match.Groups[1].Value, candidate);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // A removed/broken shim must not hide a valid installation.
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) { throw; }
        catch
        {
        }
        return (string.Empty, null);
    }

    public async Task<CompatibilityRepairResult> RepairAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var installation = await ProbeSpicetifyAsync(cancellationToken).ConfigureAwait(false);
        var spicetifyVersion = installation.Version;
        if (string.IsNullOrWhiteSpace(spicetifyVersion))
        {
            return new CompatibilityRepairResult(
                false,
                "Spicetify non è stato rilevato nei percorsi di installazione. Usa prima 'Installa / abilita Lyrics Plus'.",
                string.Empty,
                false);
        }

        var executablePath = installation.Path!;

        cancellationToken.ThrowIfCancellationRequested();
        await _stopSpotify(cancellationToken).ConfigureAwait(false);

        if (settings.AutoUpgradeSpicetify)
        {
            // 'upgrade' is the official CLI self-update command for script-based installations.
            // Package-managed installations may reject it; that must not abort recovery.
            await RunSpicetifyAsync(executablePath, new[] { "upgrade" }, cancellationToken, allowFailure: true).ConfigureAwait(false);
        }

        var primary = await RunSpicetifyAsync(executablePath, new[] { "backup", "apply" }, cancellationToken, allowFailure: true).ConfigureAwait(false);
        var usedFallback = false;
        if (!primary.Success)
        {
            usedFallback = true;
            var fallback = await RunSpicetifyAsync(executablePath, new[] { "restore", "backup", "apply" }, cancellationToken, allowFailure: true).ConfigureAwait(false);
            if (!fallback.Success)
            {
                var error = CleanError(fallback.StandardError, fallback.StandardOutput);
                return new CompatibilityRepairResult(
                    false,
                    $"Spicetify non è riuscito a riapplicarsi alla nuova versione di Spotify. {error}".Trim(),
                    spicetifyVersion,
                    true);
            }
        }

        if (settings.KeepLyricsPlusEnabled)
        {
            var configuredApps = await RunSpicetifyAsync(executablePath, new[] { "config", "custom_apps" }, cancellationToken, allowFailure: true).ConfigureAwait(false);
            if (!configuredApps.StandardOutput.Contains("lyrics-plus", StringComparison.OrdinalIgnoreCase))
            {
                var addLyrics = await RunSpicetifyAsync(executablePath, new[] { "config", "custom_apps", "lyrics-plus" }, cancellationToken, allowFailure: true).ConfigureAwait(false);
                if (!addLyrics.Success)
                {
                    return new CompatibilityRepairResult(
                        false,
                        "Spotify è stato riparato, ma non sono riuscito a riabilitare Lyrics Plus.",
                        spicetifyVersion,
                        usedFallback);
                }
            }

            var applyLyrics = await RunSpicetifyAsync(executablePath, new[] { "apply" }, cancellationToken, allowFailure: true).ConfigureAwait(false);
            if (!applyLyrics.Success)
            {
                return new CompatibilityRepairResult(
                    false,
                    "Spotify è stato riparato, ma l'applicazione finale di Lyrics Plus non è riuscita.",
                    spicetifyVersion,
                    usedFallback);
            }
        }

        if (settings.RestartSpotifyAfterRepair)
        {
            // Spicetify documents 'auto' as: backup if needed, apply, then launch Spotify.
            var launch = await RunSpicetifyAsync(executablePath, new[] { "auto" }, cancellationToken, allowFailure: true, timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            if (!launch.Success)
            {
                try
                {
                    _launchSpotify();
                }
                catch
                {
                    // Repair is still considered successful; the user can open Spotify manually.
                }
            }
        }

        return new CompatibilityRepairResult(
            true,
            usedFallback
                ? "Riparazione completata con ricostruzione completa del backup Spicetify."
                : "Riparazione automatica completata correttamente.",
            spicetifyVersion,
            usedFallback);
    }

    private static void LaunchSpotify()
        => Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });

    private static async Task StopSpotifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processes = Process.GetProcessesByName("Spotify");
        foreach (var process in processes)
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.CloseMainWindow())
                        await Task.Run(() => process.WaitForExit(1800), cancellationToken).ConfigureAwait(false);

                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Continue with other Spotify processes. Spicetify will report if files stay locked.
                }
            }
        }

        if (processes.Length > 0)
            await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PowerShellCommandResult> RunSpicetifyAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowFailure,
        TimeSpan? timeout = null)
    {
        var result = await _run(
            executablePath,
            arguments,
            timeout ?? TimeSpan.FromMinutes(4),
            cancellationToken).ConfigureAwait(false);

        if (!allowFailure && !result.Success)
            throw new InvalidOperationException(CleanError(result.StandardError, result.StandardOutput));

        return result;
    }

    private static string FirstUsefulLine(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? string.Empty;

    private static string CleanError(string error, string output)
    {
        var text = string.IsNullOrWhiteSpace(error) ? output : error;
        return FirstUsefulLine(text);
    }
}

using System.Text.RegularExpressions;
using ToastifyReloaded.Shared;

namespace ToastifyReloaded.Linux.Services;

public enum SpotifyTrackOpenResult
{
    TrackOpened,
    AppActivated,
    Failed
}

/// <summary>Opens a validated track without constructing shell commands.</summary>
public sealed class SpotifyTrackOpenService
{
    private readonly Func<string, IEnumerable<string>, CancellationToken,
        Task<(int ExitCode, string StdOut, string StdErr)>> _run;
    private readonly Func<CancellationToken, Task<string?>> _resolvePlayer;

    public SpotifyTrackOpenService(ProcessService process, PlayerctlService playerctl)
        : this(process.RunAsync, token => playerctl.ResolveSpotifyPlayerAsync(
            forceRefresh: true, cancellationToken: token))
    {
    }

    internal SpotifyTrackOpenService(
        Func<string, IEnumerable<string>, CancellationToken,
            Task<(int ExitCode, string StdOut, string StdErr)>> run,
        Func<CancellationToken, Task<string?>> resolvePlayer)
    {
        _run = run;
        _resolvePlayer = resolvePlayer;
    }

    public async Task<SpotifyTrackOpenResult> OpenAsync(string? trackUri)
    {
        foreach (var target in SpotifyTrackLink.GetOpenTargets(trackUri))
        {
            if (await TryRunAsync("xdg-open", target))
                return SpotifyTrackOpenResult.TrackOpened;
        }

        // Ads, local files and missing IDs must never become title searches or
        // arbitrary URLs. Raise the existing Spotify instance when possible.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var player = await _resolvePlayer(timeout.Token).WaitAsync(timeout.Token);
            if (player is not null && Regex.IsMatch(player, @"\A[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*\z") &&
                await TryRunAsync("gdbus", "call", "--session", "--dest",
                    "org.mpris.MediaPlayer2." + player, "--object-path",
                    "/org/mpris/MediaPlayer2", "--method", "org.mpris.MediaPlayer2.Raise"))
                return SpotifyTrackOpenResult.AppActivated;
        }
        catch
        {
            // Player disappearance or a missing D-Bus service is recoverable.
        }

        return await TryRunAsync("xdg-open", "spotify:")
            ? SpotifyTrackOpenResult.AppActivated
            : SpotifyTrackOpenResult.Failed;
    }

    private async Task<bool> TryRunAsync(string fileName, params string[] arguments)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await _run(fileName, arguments, timeout.Token);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

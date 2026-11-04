using System.Text.RegularExpressions;
using ToastifyReloaded.Shared;

namespace ToastifyReloaded.Linux.Services;

public enum SpotifyTrackOpenResult
{
    TrackOpened,
    AppActivated,
    Failed,
    LyricsPlusRequested
}

/// <summary>Opens a validated track without constructing shell commands.</summary>
public sealed class SpotifyTrackOpenService
{
    public const string LyricsPlusUri = "spotify:app:lyrics-plus";
    private readonly Func<string, IEnumerable<string>, CancellationToken,
        Task<(int ExitCode, string StdOut, string StdErr)>> _run;
    private readonly Func<CancellationToken, Task<string?>> _resolvePlayer;
    private readonly Func<CancellationToken, Task<string?>> _currentTrackUri;

    public SpotifyTrackOpenService(ProcessService process, PlayerctlService playerctl)
        : this(process.RunAsync, token => playerctl.ResolveSpotifyPlayerAsync(
            forceRefresh: true, cancellationToken: token),
            async token => (await playerctl.GetTrackAsync(token))?.TrackUri)
    {
    }

    internal SpotifyTrackOpenService(
        Func<string, IEnumerable<string>, CancellationToken,
            Task<(int ExitCode, string StdOut, string StdErr)>> run,
        Func<CancellationToken, Task<string?>> resolvePlayer,
        Func<CancellationToken, Task<string?>>? currentTrackUri = null)
    {
        _run = run;
        _resolvePlayer = resolvePlayer;
        _currentTrackUri = currentTrackUri ?? (_ => Task.FromResult<string?>(null));
    }

    // Native Linux equivalent: navigate via a fresh, validated MPRIS identity.
    // Spotify owns the link's behavior; no playback or synthetic input is sent.
    public async Task<SpotifyTrackOpenResult> OpenCurrentTrackAsync(CancellationToken cancellationToken = default)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            string? uri = null;
            try { uri = await _currentTrackUri(linked.Token).WaitAsync(linked.Token); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch { /* Missing/transient metadata falls back to app activation. */ }
            return await OpenAsync(uri, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return SpotifyTrackOpenResult.Failed; }
    }

    public async Task<SpotifyTrackOpenResult> OpenLyricsPlusAsync(CancellationToken cancellationToken = default)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryRunAsync(linked.Token, "xdg-open", LyricsPlusUri))
                return SpotifyTrackOpenResult.LyricsPlusRequested;
            return await ActivateAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return SpotifyTrackOpenResult.Failed; }
    }

    public async Task<SpotifyTrackOpenResult> OpenAsync(string? trackUri, CancellationToken cancellationToken = default)
    {
        foreach (var target in SpotifyTrackLink.GetOpenTargets(trackUri))
        {
            if (await TryRunAsync(cancellationToken, "xdg-open", target))
                return SpotifyTrackOpenResult.TrackOpened;
        }
        return await ActivateAsync(cancellationToken);
    }

    private async Task<SpotifyTrackOpenResult> ActivateAsync(CancellationToken cancellationToken)
    {

        // Ads, local files and missing IDs must never become title searches or
        // arbitrary URLs. Raise the existing Spotify instance when possible.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            var player = await _resolvePlayer(linked.Token).WaitAsync(linked.Token);
            if (player is not null && Regex.IsMatch(player, @"\A[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*\z") &&
                await TryRunAsync(cancellationToken, "gdbus", "call", "--session", "--dest",
                    "org.mpris.MediaPlayer2." + player, "--object-path",
                    "/org/mpris/MediaPlayer2", "--method", "org.mpris.MediaPlayer2.Raise"))
                return SpotifyTrackOpenResult.AppActivated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // Player disappearance or a missing D-Bus service is recoverable.
        }

        return await TryRunAsync(cancellationToken, "xdg-open", "spotify:")
            ? SpotifyTrackOpenResult.AppActivated
            : SpotifyTrackOpenResult.Failed;
    }

    private async Task<bool> TryRunAsync(CancellationToken cancellationToken, string fileName, params string[] arguments)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            var result = await _run(fileName, arguments, linked.Token).WaitAsync(linked.Token);
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return false;
        }
    }
}

using ToastifyReloaded.Shared;

namespace ToastifyReloaded.Mac.Services;

public sealed record SpotifyTrackOpenResult(bool Success, bool OpenedTrack, string Message);

/// <summary>Opens only canonical Spotify track targets; metadata is never executable text.</summary>
public sealed class SpotifyTrackOpenService
{
    private readonly Func<string, IEnumerable<string>, CancellationToken,
        Task<(int ExitCode, string StdOut, string StdErr)>> _run;

    public SpotifyTrackOpenService(ProcessService process)
        : this(process.RunAsync)
    {
    }

    internal SpotifyTrackOpenService(Func<string, IEnumerable<string>, CancellationToken,
        Task<(int ExitCode, string StdOut, string StdErr)>> run)
        => _run = run;

    public async Task<SpotifyTrackOpenResult> OpenAsync(string? trackUri)
    {
        var targets = SpotifyTrackLink.GetOpenTargets(trackUri);
        if (targets.Count > 0)
        {
            if (await TryOpenAsync("-a", "Spotify", targets[0]) ||
                await TryOpenAsync(targets[1]))
                return new(true, true, "TrackOpened");
        }

        // Missing, local or unsupported media never becomes a title search or an
        // arbitrary URL. Activating Spotify also avoids changing playback.
        if (await TryOpenAsync("-a", "Spotify"))
            return new(true, false, "SpotifyActivated");

        return new(false, false, "TrackOpenFailed");
    }

    private async Task<bool> TryOpenAsync(params string[] arguments)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await _run("/usr/bin/open", arguments, timeout.Token);
            return result.ExitCode == 0;
        }
        catch (Exception)
        {
            // Missing Spotify, a rejected handler and launcher errors all take
            // the same bounded fallback path without crashing the toast.
            return false;
        }
    }
}

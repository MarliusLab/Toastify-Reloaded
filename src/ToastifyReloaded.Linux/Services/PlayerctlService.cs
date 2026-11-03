using System.Globalization;
using ToastifyReloaded.Linux.Models;
using ToastifyReloaded.Shared;

namespace ToastifyReloaded.Linux.Services;

public sealed class PlayerctlService
{
    internal const string MetadataSeparator = "\u001f";
    internal const string MetadataEndMarker = "toastify-mpris-v1";
    private readonly ProcessService _process;
    private double _lastNonZeroVolume = 0.5;
    private string? _cachedPlayer;

    public PlayerctlService(ProcessService process)
        => _process = process;

    public Task<bool> IsAvailableAsync()
        => _process.ExistsAsync("playerctl");

    public async Task<string?> ResolveSpotifyPlayerAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!forceRefresh && !string.IsNullOrWhiteSpace(_cachedPlayer))
        {
            var status = await RunRawAsync(
                cancellationToken,
                "--player=" + _cachedPlayer,
                "status");

            if (status.ExitCode == 0)
                return _cachedPlayer;
        }

        var result = await RunRawAsync(cancellationToken, "--list-all");
        if (result.ExitCode != 0)
        {
            _cachedPlayer = null;
            return null;
        }

        var players = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _cachedPlayer =
            players.FirstOrDefault(p => p.Equals("spotify", StringComparison.OrdinalIgnoreCase))
            ?? players.FirstOrDefault(p => p.StartsWith("spotify.instance", StringComparison.OrdinalIgnoreCase))
            ?? players.FirstOrDefault(p => p.StartsWith("spotify", StringComparison.OrdinalIgnoreCase))
            ?? players.FirstOrDefault(p => p.Equals("spotifyd", StringComparison.OrdinalIgnoreCase))
            ?? players.FirstOrDefault(p => p.StartsWith("spotifyd", StringComparison.OrdinalIgnoreCase));

        return _cachedPlayer;
    }

    public async Task<bool> IsSpotifyAvailableAsync()
        => await ResolveSpotifyPlayerAsync(forceRefresh: true) is not null;

    public async Task<LinuxTrackInfo?> GetTrackAsync(CancellationToken cancellationToken = default)
    {
        var player = await ResolveSpotifyPlayerAsync(cancellationToken: cancellationToken);
        if (player is null)
            return null;

        var format =
            "{{title}}" + MetadataSeparator +
            "{{artist}}" + MetadataSeparator +
            "{{album}}" + MetadataSeparator +
            "{{mpris:length}}" + MetadataSeparator +
            "{{mpris:artUrl}}" + MetadataSeparator +
            "{{xesam:url}}" + MetadataSeparator +
            "{{mpris:trackid}}" + MetadataSeparator + MetadataEndMarker;

        var metadata = await RunPlayerAsync(player, cancellationToken, "--format", format, "metadata");

        if (metadata.ExitCode != 0 || string.IsNullOrWhiteSpace(metadata.StdOut))
        {
            // Spotify may have restarted and received a new MPRIS name.
            player = await ResolveSpotifyPlayerAsync(forceRefresh: true, cancellationToken: cancellationToken);
            if (player is null)
                return null;

            metadata = await RunPlayerAsync(player, cancellationToken, "--format", format, "metadata");
        }

        if (metadata.ExitCode != 0 || string.IsNullOrWhiteSpace(metadata.StdOut))
            return null;

        var positionResult = await RunPlayerAsync(player, cancellationToken, "position");
        var statusResult = await RunPlayerAsync(player, cancellationToken, "status");

        double position = 0;
        _ = double.TryParse(
            positionResult.StdOut,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out position);

        return ParseTrackSnapshot(
            metadata.StdOut,
            position,
            statusResult.StdOut.Equals("Playing", StringComparison.OrdinalIgnoreCase));
    }

    // All identity fields come from one metadata read. Never carry a previous
    // track URI forward when Spotify omits it during a transition/ad/local file.
    internal static LinuxTrackInfo? ParseTrackSnapshot(
        string metadata,
        double position,
        bool isPlaying)
    {
        var parts = metadata.Split(MetadataSeparator);
        // A final non-whitespace marker protects empty trailing identity fields
        // from ProcessService's output trimming. Keep seven-field snapshots
        // readable for existing fixtures, while rejecting ambiguous framing.
        if ((parts.Length != 7 &&
             (parts.Length != 8 || parts[7] != MetadataEndMarker)) ||
            string.IsNullOrWhiteSpace(parts[0]))
            return null;

        var urlTrackUri = SpotifyTrackLink.TryGetTrackUri(parts[5]);
        var mprisTrackUri = SpotifyTrackLink.TryGetTrackUri(parts[6]);
        var trackUri = urlTrackUri is not null && mprisTrackUri is not null &&
            !string.Equals(urlTrackUri, mprisTrackUri, StringComparison.Ordinal)
                ? null
                : urlTrackUri ?? mprisTrackUri;

        double duration = 0;
        if (double.TryParse(
                parts[3],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var durationMicros))
        {
            duration = durationMicros / 1_000_000d;
        }

        return new LinuxTrackInfo(
            parts[0].Trim(),
            parts[1].Trim(),
            parts[2].Trim(),
            NormalizeArtwork(parts[4]),
            double.IsFinite(position) ? Math.Max(0, position) : 0,
            double.IsFinite(duration) ? Math.Max(0, duration) : 0,
            isPlaying,
            trackUri);
    }

    public Task PlayPauseAsync() => CommandAsync("play-pause");
    public Task NextAsync() => CommandAsync("next");
    public Task PreviousAsync() => CommandAsync("previous");
    public Task SeekForwardAsync() => CommandAsync("position", "10+");
    public Task SeekBackwardAsync() => CommandAsync("position", "10-");
    public Task VolumeUpAsync() => CommandAsync("volume", "0.05+");
    public Task VolumeDownAsync() => CommandAsync("volume", "0.05-");

    public async Task ToggleMuteAsync()
    {
        var player = await ResolveSpotifyPlayerAsync();
        if (player is null)
            return;

        var current = await RunPlayerAsync(player, "volume");
        if (current.ExitCode != 0)
            return;

        if (!double.TryParse(
                current.StdOut,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var volume))
            return;

        if (volume > 0.001)
        {
            _lastNonZeroVolume = volume;
            await RunPlayerAsync(player, "volume", "0");
        }
        else
        {
            await RunPlayerAsync(
                player,
                "volume",
                Math.Clamp(_lastNonZeroVolume, 0.05, 1.0)
                    .ToString("0.00", CultureInfo.InvariantCulture));
        }
    }

    private async Task CommandAsync(params string[] command)
    {
        var player = await ResolveSpotifyPlayerAsync();
        if (player is null)
            return;

        var result = await RunPlayerAsync(player, command);
        if (result.ExitCode != 0)
        {
            _cachedPlayer = null;
            player = await ResolveSpotifyPlayerAsync(forceRefresh: true);
            if (player is not null)
                _ = await RunPlayerAsync(player, command);
        }
    }

    private Task<(int ExitCode, string StdOut, string StdErr)> RunPlayerAsync(
        string player,
        params string[] args)
        => RunPlayerAsync(player, CancellationToken.None, args);

    private Task<(int ExitCode, string StdOut, string StdErr)> RunPlayerAsync(
        string player,
        CancellationToken cancellationToken,
        params string[] args)
        => RunRawAsync(cancellationToken, new[] { "--player=" + player }.Concat(args).ToArray());

    private Task<(int ExitCode, string StdOut, string StdErr)> RunRawAsync(
        CancellationToken cancellationToken,
        params string[] args)
        => _process.RunAsync("playerctl", args, cancellationToken);

    private static string NormalizeArtwork(string value)
    {
        var art = value.Trim();

        if (art.StartsWith("https://open.spotify.com/image/", StringComparison.OrdinalIgnoreCase))
            return art.Replace(
                "https://open.spotify.com/image/",
                "https://i.scdn.co/image/",
                StringComparison.OrdinalIgnoreCase);

        return art;
    }
}

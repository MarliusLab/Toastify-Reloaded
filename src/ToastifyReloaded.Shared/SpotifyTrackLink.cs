using System.Text.RegularExpressions;

namespace ToastifyReloaded.Shared;

/// <summary>Converts only Spotify catalog track identifiers into trusted launch targets.</summary>
public static class SpotifyTrackLink
{
    private static readonly Regex TrackUri = new(
        @"\Aspotify:track:([A-Za-z0-9]{22})\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex TrackUrl = new(
        @"\Ahttps://open\.spotify\.com/(?:intl-[a-z]{2}/)?track/([A-Za-z0-9]{22})(?:\?[^\s#\\]*)?(?:#[^\s\\]*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex MprisTrackPath = new(
        @"\A/com/spotify/track/([A-Za-z0-9]{22})\z", RegexOptions.CultureInvariant);

    public static string? TryGetTrackUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl))
            return null;

        value = value.Trim();
        foreach (var pattern in new[] { TrackUri, TrackUrl, MprisTrackPath })
        {
            var match = pattern.Match(value);
            if (match.Success)
                return "spotify:track:" + match.Groups[1].Value;
        }
        return null;
    }

    public static IReadOnlyList<string> GetOpenTargets(string? trackUri)
    {
        var canonical = TryGetTrackUri(trackUri);
        return canonical is null
            ? Array.Empty<string>()
            : new[] { canonical, "https://open.spotify.com/track/" + canonical["spotify:track:".Length..] };
    }
}

/// <summary>Tracks notifications without copying old IDs into a new track snapshot.</summary>
public sealed class SpotifyTrackChangeTracker
{
    private string? _lastUri;
    private string? _lastLabels;

    public bool Update(string? trackUri, string title, string artist, string album)
    {
        var currentUri = SpotifyTrackLink.TryGetTrackUri(trackUri);
        var labels = $"{title}\u001f{artist}\u001f{album}";
        var same = _lastLabels is not null &&
            (_lastUri is not null && currentUri is not null
                ? string.Equals(_lastUri, currentUri, StringComparison.Ordinal)
                : string.Equals(_lastLabels, labels, StringComparison.Ordinal));

        // Retain known identity only in notification comparison state. The
        // displayed snapshot and its clickable URI are never filled from here.
        _lastUri = currentUri ?? (same ? _lastUri : null);
        _lastLabels = labels;
        return !same;
    }
}

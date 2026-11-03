using ToastifyReloaded.Shared;

namespace ToastifyReloaded.Mac.Models;

public sealed record MacTrackInfo(
    string Title,
    string Artist,
    string Album,
    string ArtworkUrl,
    double PositionSeconds,
    double DurationSeconds,
    bool IsPlaying,
    string TrackUri = "")
{
    public string Identity => SpotifyTrackLink.TryGetTrackUri(TrackUri)
        ?? $"{Title}\u001f{Artist}\u001f{Album}";
}

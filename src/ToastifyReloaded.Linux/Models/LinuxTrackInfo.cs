namespace ToastifyReloaded.Linux.Models;

public sealed record LinuxTrackInfo(
    string Title,
    string Artist,
    string Album,
    string ArtworkUrl,
    double PositionSeconds,
    double DurationSeconds,
    bool IsPlaying,
    string? TrackUri = null)
{
    public string Identity => ToastifyReloaded.Shared.SpotifyTrackLink.TryGetTrackUri(TrackUri)
        ?? $"{Title}\u001f{Artist}\u001f{Album}";
}

using System.Text.Json;
using System.Text.Json.Serialization;
using ToastifyReloaded.Shared;
using Velopack.Sources;

namespace ToastifyReloaded.Services;

/// <summary>Searches a bounded 100 releases instead of the SDK's first ten across every platform.</summary>
internal sealed class ToastifyGithubSource(string channel, IFileDownloader downloader)
    : GithubSource(VelopackUpdateService.RepositoryUrl, null, prerelease: false, downloader)
{
    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        var json = await Downloader.DownloadString(
            "https://api.github.com/repos/MarliusLab/Toastify-Reloaded/releases?per_page=100&page=1",
            GetRequestHeaders("application/vnd.github+json")).ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize<WindowsGithubRelease[]>(json) ?? [];
        var feedName = $"releases.{channel}.json";
        return releases.Where(r => !r.Draft && !r.Prerelease && IsWindowsTag(r.TagName) &&
                r.Assets.Any(a => string.Equals(a.Name, feedName, StringComparison.Ordinal)))
            .OrderByDescending(r => r.PublishedAt).Cast<GithubRelease>().ToArray();
    }

    private static bool IsWindowsTag(string? tag) => !string.IsNullOrWhiteSpace(tag) &&
        !tag.Contains('-') && Version.TryParse(WindowsReleaseVersion.Normalize(tag), out _);

    public sealed class WindowsGithubRelease : GithubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }
        [JsonPropertyName("draft")]
        public bool Draft { get; set; }
    }
}

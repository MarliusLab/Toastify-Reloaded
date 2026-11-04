using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Velopack.Sources;
using Velopack.Locators;

namespace ToastifyReloaded.Shared;

internal sealed class PortableGithubSource(string platform, string channel, IFileDownloader downloader)
    : GithubSource(PortablePackageUpdateService.RepositoryUrl, null,
        prerelease: platform == "macos" && (VelopackLocator.Current.CurrentlyInstalledVersion?.IsPrerelease ?? false), downloader)
{
    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        var json = await Downloader.DownloadString(
            "https://api.github.com/repos/MarliusLab/Toastify-Reloaded/releases?per_page=100&page=1",
            GetRequestHeaders("application/vnd.github+json")).ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize<PlatformRelease[]>(json) ?? [];
        var pattern = platform == "linux" ? @"^v\d+\.\d+\.\d+-linux$"
            : @"^v\d+\.\d+\.\d+-macos(?:-(?:preview|rc)\.[1-9][0-9]*)?$";
        return releases.Where(r => !r.Draft && (!r.Prerelease || includePrereleases) &&
                Regex.IsMatch(r.TagName ?? "", pattern, RegexOptions.CultureInvariant) &&
                r.Assets.Any(a => a.Name == $"releases.{channel}.json"))
            .OrderByDescending(r => r.PublishedAt).Cast<GithubRelease>().ToArray();
    }

    public sealed class PlatformRelease : GithubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
    }
}

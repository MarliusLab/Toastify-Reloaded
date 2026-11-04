using System.IO;
using System.Security.Cryptography;

namespace ToastifyReloaded.Services;

/// <summary>Imports the protected MSI bridge base into the SDK's per-user cache.</summary>
internal static class VelopackPackageSeed
{
    public static async Task<bool> TryImportAsync(string rootDirectory, string cacheDirectory,
        string installedVersion, string channel, CancellationToken cancellationToken = default,
        Func<Stream, Stream, CancellationToken, Task>? copyAsync = null)
    {
        if (channel is not ("win-x64" or "win-arm64") ||
            !Version.TryParse(installedVersion, out _) || installedVersion.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        var name = $"{VelopackUpdateService.PackageId}-{installedVersion}-{channel}-full.nupkg";
        var source = Path.Combine(rootDirectory, "packages", name);
        var destination = Path.Combine(cacheDirectory, name);
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return false;
        var sidecar = source + ".sha256";
        if (!File.Exists(source) || !File.Exists(sidecar)) return false;
        if (new FileInfo(sidecar).Length > 128) return false;
        var expected = (await File.ReadAllTextAsync(sidecar, cancellationToken).ConfigureAwait(false)).Trim();
        if (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c))) return false;
        if (File.Exists(destination) && await MatchesAsync(destination, expected, cancellationToken).ConfigureAwait(false))
            return true;

        Directory.CreateDirectory(cacheDirectory);
        var temporary = destination + ".seed-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                             64 * 1024, useAsync: true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, useAsync: true))
            {
                if (copyAsync is null)
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                else
                    await copyAsync(input, output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (!await MatchesAsync(temporary, expected, cancellationToken).ConfigureAwait(false))
                return false; // Leave the cache untouched; SDK will use the full package.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return true;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}

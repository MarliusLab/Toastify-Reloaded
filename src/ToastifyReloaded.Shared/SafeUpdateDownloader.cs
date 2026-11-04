using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace ToastifyReloaded.Shared;

public static class SafeUpdateDownloader
{
    public static bool IsTrustedAssetUri(Uri uri, string tag, string name) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com" &&
        uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        string.IsNullOrEmpty(uri.Query) && IsSafeFileName(name) &&
        uri.AbsolutePath == "/MarliusLab/Toastify-Reloaded/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name);

    public static bool IsSafeFileName(string name) => !string.IsNullOrWhiteSpace(name) &&
        name == Path.GetFileName(name) && !name.Contains('/') && !name.Contains('\\') &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..");

    public static async Task DownloadAsync(HttpClient http, Uri uri, string tag, string name, string destination,
        long expectedSize, string? digest, IProgress<UpdateOperationProgress>? progress = null,
        CancellationToken cancellationToken = default, TimeSpan? totalTimeout = null, TimeSpan? idleTimeout = null)
    {
        if (!IsTrustedAssetUri(uri, tag, name)) throw new InvalidDataException("Untrusted update asset URL.");
        var expectedHash = ParseDigest(digest);
        if (expectedSize <= 0) throw new InvalidDataException("The update has no valid declared size.");
        using var deadline = new CancellationTokenSource(totalTimeout ?? TimeSpan.FromMinutes(10));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var idleBudget = idleTimeout ?? TimeSpan.FromSeconds(30);
        var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            progress?.Report(new(UpdateOperationStage.Downloading));
            using var headersDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var headersToken = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, headersDeadline.Token);
            HttpResponseMessage responseValue;
            try
            {
                responseValue = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, headersToken.Token)
                    .WaitAsync(headersToken.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (headersDeadline.IsCancellationRequested && !operation.IsCancellationRequested)
            { throw new TimeoutException("The update server did not return response headers in time."); }
            using var response = responseValue;
            response.EnsureSuccessStatusCode();
            var declared = response.Content.Headers.ContentLength;
            if (declared.HasValue && declared.Value != expectedSize)
                throw new InvalidDataException("Update Content-Length differs from release metadata.");
            var total = declared is > 0 ? declared : null;
            long received = 0;
            var reportClock = Stopwatch.StartNew();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            await using (var input = await response.Content.ReadAsStreamAsync(operation.Token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    using var idle = new CancellationTokenSource(idleBudget);
                    using var read = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, idle.Token);
                    int count;
                    try { count = await input.ReadAsync(buffer, read.Token).AsTask().WaitAsync(read.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (idle.IsCancellationRequested && !operation.IsCancellationRequested)
                    { throw new TimeoutException("The update download stopped receiving data."); }
                    if (count == 0) break;
                    received += count;
                    if (received > expectedSize) throw new InvalidDataException("Update exceeds its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), operation.Token).ConfigureAwait(false);
                    if (reportClock.ElapsedMilliseconds >= 100 || received == expectedSize)
                    {
                        progress?.Report(new(UpdateOperationStage.Downloading, received, total));
                        reportClock.Restart();
                    }
                }
            }
            if (received != expectedSize) throw new InvalidDataException("Update download is incomplete.");
            progress?.Report(new(UpdateOperationStage.Verifying, received, total));
            await VerifyFileAsync(partial, expectedSize, expectedHash, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: true);
            progress?.Report(new(UpdateOperationStage.Ready, received, total));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            progress?.Report(new(UpdateOperationStage.Failed, Message: "Download deadline exceeded."));
            throw new TimeoutException("The update download exceeded its total deadline.");
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new(UpdateOperationStage.Cancelled));
            throw;
        }
        catch
        {
            progress?.Report(new(UpdateOperationStage.Failed));
            throw;
        }
        finally
        {
            try { File.Delete(partial); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static string ParseDigest(string? digest)
    {
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A published SHA-256 digest is required for updates.");
        var value = digest[7..];
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Invalid published SHA-256 digest.");
        return value.ToLowerInvariant();
    }

    public static async Task VerifyFileAsync(string path, long expectedSize, string expectedHash,
        CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous);
        if (stream.Length != expectedSize) throw new InvalidDataException("Update file size changed.");
        var actual = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        if (!Convert.ToHexString(actual).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update SHA-256 verification failed.");
    }
}

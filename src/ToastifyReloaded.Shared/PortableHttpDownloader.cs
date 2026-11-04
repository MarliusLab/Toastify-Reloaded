using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Velopack.Sources;

namespace ToastifyReloaded.Shared;

internal readonly record struct PortableTransferProgress(long BytesReceived, long? TotalBytes);

/// <summary>Bounds the SDK's metadata calls too, which have no cancellation parameter.</summary>
internal sealed class PortableHttpDownloader : IFileDownloader
{
    private readonly HttpClient _client;
    private readonly TimeSpan _metadataTimeout;
    private readonly TimeSpan _idleTimeout;
    private readonly AsyncLocal<Operation?> _operation = new();

    public PortableHttpDownloader(HttpClient client, TimeSpan? metadataTimeout = null, TimeSpan? idleTimeout = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _metadataTimeout = metadataTimeout ?? TimeSpan.FromSeconds(25);
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
        if (_metadataTimeout <= TimeSpan.Zero || _idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(metadataTimeout));
    }

    public IDisposable BeginOperation(CancellationToken token, Action<PortableTransferProgress>? progress = null)
    {
        var previous = _operation.Value;
        _operation.Value = new Operation(token, progress);
        return new Scope(() => _operation.Value = previous);
    }

    public async Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancelToken, _operation.Value?.Token ?? default);
        cancellation.CancelAfter(TimeSpan.FromMinutes(Math.Min(timeout, 10)));
        using var request = CreateRequest(url, headers);
        using var response = await SendHeadersAsync(request, cancellation.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false);
        await using var output = new FileStream(targetFile, FileMode.Create, FileAccess.Write,
            FileShare.None, 64 * 1024, useAsync: true);
        await CopyAsync(input, output, response.Content.Headers.ContentLength, cancellation.Token,
            (received, total) =>
            {
                _operation.Value?.Progress?.Invoke(new(received, total));
                if (total is > 0)
                    progress((int)Math.Clamp(received * 100d / total.Value, 0, 100));
            }).ConfigureAwait(false);
        await output.FlushAsync(cancellation.Token).ConfigureAwait(false);
        progress(100);
    }

    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_operation.Value?.Token ?? default);
        cancellation.CancelAfter(_metadataTimeout);
        using var request = CreateRequest(url, headers);
        using var response = await SendHeadersAsync(request, cancellation.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 4 * 1024 * 1024)
            throw new InvalidDataException("Metadati aggiornamento troppo grandi.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        await CopyAsync(input, output, response.Content.Headers.ContentLength, cancellation.Token,
            maxBytes: 4 * 1024 * 1024).ConfigureAwait(false);
        return output.ToArray();
    }

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        => Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout).ConfigureAwait(false));

    private async Task CopyAsync(Stream input, Stream output, long? total, CancellationToken token,
        Action<long, long?>? report = null, long maxBytes = long.MaxValue)
    {
        long received = 0;
        report?.Invoke(0, total);
        var buffer = new byte[64 * 1024];
        var clock = Stopwatch.StartNew();
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(_idleTimeout);
            int count;
            try { count = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Download interrotto: il server non ha inviato dati entro il tempo previsto.", ex);
            }
            if (count == 0) break;
            received = checked(received + count);
            if (received > maxBytes || (total.HasValue && received > total.Value))
                throw new InvalidDataException("Dimensione del pacchetto aggiornamento non valida.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (clock.ElapsedMilliseconds >= 100)
            {
                report?.Invoke(received, total);
                clock.Restart();
            }
        }
        if (total.HasValue && total.Value != received)
            throw new InvalidDataException("Download aggiornamento incompleto.");
        report?.Invoke(received, total);
    }

    private static HttpRequestMessage CreateRequest(string url, IDictionary<string, string>? headers)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("L'aggiornamento richiede una connessione HTTPS.");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("ToastifyReloaded/Velopack");
        foreach (var header in headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return request;
    }

    private async Task<HttpResponseMessage> SendHeadersAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(_idleTimeout);
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Download interrotto: il server non ha risposto entro il tempo previsto.", ex);
        }
    }

    private sealed record Operation(CancellationToken Token, Action<PortableTransferProgress>? Progress);
    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

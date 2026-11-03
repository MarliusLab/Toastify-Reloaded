using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ToastifyReloaded.Models;

namespace ToastifyReloaded.Services;

public sealed class UpdateService
{
    private const string Owner = "MarliusLab";
    private const string Repository = "Toastify-Reloaded";
    private static readonly HttpClient Client = CreateClient();
    private readonly HttpClient _client;
    private readonly Func<ProcessStartInfo, Task<bool>> _installerLauncher;
    private readonly string _tempDirectory;
    private readonly string _currentVersion;
    private readonly TimeSpan _totalTimeout;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _checkTimeout;

    public UpdateService() : this(Client)
    {
    }

    public UpdateService(
        HttpClient client,
        Func<ProcessStartInfo, Task<bool>>? installerLauncher = null,
        string? tempDirectory = null,
        string? currentVersion = null,
        TimeSpan? totalTimeout = null,
        TimeSpan? idleTimeout = null,
        TimeSpan? checkTimeout = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _installerLauncher = installerLauncher ?? (info =>
        {
            using var installer = Process.Start(info);
            return Task.FromResult(installer is not null);
        });
        _tempDirectory = tempDirectory ?? Path.Combine(Path.GetTempPath(), "ToastifyReloaded");
        _currentVersion = NormalizeVersion(currentVersion ??
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");
        _totalTimeout = totalTimeout ?? TimeSpan.FromMinutes(10);
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(25);
        if (_totalTimeout <= TimeSpan.Zero || _totalTimeout > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(totalTimeout));
        if (_idleTimeout <= TimeSpan.Zero || _idleTimeout > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        if (_checkTimeout <= TimeSpan.Zero || _checkTimeout > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(checkTimeout));
    }

    public string CurrentVersion => _currentVersion;

    public async Task<UpdateCheckResult> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        // HttpClient's ResponseHeadersRead timeout stops at the headers. Keep a
        // separate deadline alive through deserialization of the response body.
        using var checkTimeout = new CancellationTokenSource(_checkTimeout);
        using var checkCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, checkTimeout.Token);
        var token = checkCancellation.Token;
        try
        {
            // Do not use /releases/latest in a multi-platform repository. A Linux or
            // macOS release can legitimately have a numerically newer tag than the
            // Windows line and must never be treated as a Windows update.
            using var response = await _client.GetAsync(
                $"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=30",
                HttpCompletionOption.ResponseHeadersRead,
                token);

            if (!response.IsSuccessStatusCode)
                return Failure($"GitHub ha risposto {(int)response.StatusCode} ({response.ReasonPhrase}).");

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, cancellationToken: token);
            if (releases is null || releases.Count == 0)
                return Failure("GitHub non ha restituito release utilizzabili.");

            var assetName = GetExpectedAssetName();
            GitHubRelease? release = null;
            Version? latest = null;
            string? latestText = null;

            foreach (var candidate in releases)
            {
                if (candidate.Draft || candidate.Prerelease || !IsWindowsReleaseTag(candidate.TagName))
                    continue;

                var candidateAsset = candidate.Assets.FirstOrDefault(a =>
                    a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase));
                if (candidateAsset is null)
                    continue;

                var candidateText = NormalizeVersion(candidate.TagName);
                if (!Version.TryParse(candidateText, out var candidateVersion))
                    continue;

                if (latest is null || candidateVersion > latest)
                {
                    release = candidate;
                    latest = candidateVersion;
                    latestText = candidateText;
                }
            }

            if (release is null || latest is null || latestText is null)
                return Failure($"Nessuna release Windows stabile con l'installer {assetName} è disponibile.");

            if (!Version.TryParse(CurrentVersion, out var current))
                return Failure($"Impossibile confrontare la versione corrente {CurrentVersion}.");

            var asset = release.Assets.First(a =>
                a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase));

            var available = latest > current;
            var message = available
                ? $"È disponibile Toastify Reloaded {latestText}."
                : $"Toastify Reloaded è aggiornato ({CurrentVersion}).";

            return new UpdateCheckResult(
                true,
                available,
                CurrentVersion,
                latestText,
                release.TagName,
                asset.Name,
                asset.BrowserDownloadUrl,
                release.HtmlUrl,
                message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("Controllo aggiornamenti scaduto per timeout.");
        }
        catch (Exception ex)
        {
            return Failure($"Controllo aggiornamenti non riuscito: {ex.Message}");
        }
    }

    public async Task<bool> PrepareAndLaunchUpdateAsync(
        UpdateCheckResult update,
        int currentProcessId,
        bool restartMinimized = false,
        CancellationToken cancellationToken = default,
        IProgress<UpdateProgressInfo>? progress = null)
    {
        if (!update.UpdateAvailable || string.IsNullOrWhiteSpace(update.DownloadUrl))
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        var updateRoot = Path.Combine(_tempDirectory, "installer-update-" + Guid.NewGuid().ToString("N"));
        var installerName = string.IsNullOrWhiteSpace(update.AssetName)
            ? GetExpectedAssetName()
            : Path.GetFileName(update.AssetName);
        var installerPath = Path.Combine(updateRoot, installerName);
        var partialPath = installerPath + ".partial";
        var launched = false;
        try
        {
            Directory.CreateDirectory(updateRoot);
            var (received, total) = await DownloadInstallerAsync(
                update.DownloadUrl, partialPath, progress, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(UpdatePhase.Verifying, received, total));
            ValidatePortableExecutable(partialPath);
            // All file handles are closed before rename and the shell handoff.
            File.Move(partialPath, installerPath);
            cancellationToken.ThrowIfCancellationRequested();

            // /UPDATEPID lets NSIS wait for this process to close before replacing
            // files. Do not pass /S: native installation progress remains visible.
            var installerArguments = $"/UPDATEPID={currentProcessId}";
            if (restartMinimized)
                installerArguments += " /RESTARTMINIMIZED=1";
            var startInfo = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = installerArguments,
                WorkingDirectory = updateRoot,
                UseShellExecute = true,
                Verb = "runas"
            };

            // ShellExecute can wait for UAC. Keep it off the UI thread and await
            // its actual outcome. Once this handoff starts, cancellation is
            // disabled: abandoning the await could launch an installer later.
            progress?.Report(new(UpdatePhase.WaitingForPermission, received, total));
            launched = await Task.Run(() => _installerLauncher(startInfo)).ConfigureAwait(false);
            if (launched)
            {
                // A closing/disposed UI observer cannot undo the shell handoff.
                // Return success so the app can close and NSIS can replace files.
                try { progress?.Report(new(UpdatePhase.LaunchingInstaller, received, total)); }
                catch (Exception) { }
            }
            return launched;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new UpdatePermissionCanceledException(ex);
        }
        finally
        {
            if (!launched)
            {
                TryDeleteFile(partialPath);
                TryDeleteFile(installerPath);
                try { if (Directory.Exists(updateRoot)) Directory.Delete(updateRoot); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task<(long Received, long? Total)> DownloadInstallerAsync(
        string url, string destinationPath, IProgress<UpdateProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        using var totalTimeout = new CancellationTokenSource(_totalTimeout);
        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, totalTimeout.Token);
        var token = downloadCancellation.Token;
        try
        {
            using var response = await _client.GetAsync(url,
                HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            progress?.Report(new(UpdatePhase.Downloading, 0, total));
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var destination = new FileStream(destinationPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            var buffer = new byte[64 * 1024];
            long received = 0;
            var lastReport = Stopwatch.StartNew();
            while (true)
            {
                using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                idleCancellation.CancelAfter(_idleTimeout);
                int read;
                try
                {
                    read = await source.ReadAsync(buffer.AsMemory(), idleCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested &&
                                                          idleCancellation.IsCancellationRequested)
                {
                    throw new TimeoutException("Download interrotto: il server non ha inviato dati entro il tempo previsto.", ex);
                }
                if (read == 0)
                    break;
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received = checked(received + read);
                if (total.HasValue && received > total.Value)
                    throw new InvalidDataException("Dimensione dell'installer diversa da quella dichiarata dal server.");
                if (lastReport.ElapsedMilliseconds >= 100)
                {
                    progress?.Report(new(UpdatePhase.Downloading, received, total));
                    lastReport.Restart();
                }
            }
            await destination.FlushAsync(token).ConfigureAwait(false);
            if (total.HasValue && received != total.Value)
                throw new InvalidDataException("Download incompleto: dimensione dell'installer inferiore a quella prevista.");
            progress?.Report(new(UpdatePhase.Downloading, received, total));
            return (received, total);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (totalTimeout.IsCancellationRequested)
        {
            throw new TimeoutException("Tempo massimo del download superato. Riprovare o scaricare l'installer dalla pagina della release.", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException("Connessione per il download scaduta per timeout.", ex);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private UpdateCheckResult Failure(string message) =>
        new(false, false, CurrentVersion, string.Empty, null, null, null,
            $"https://github.com/{Owner}/{Repository}/releases/latest", message);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ToastifyReloaded", "1.2"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        return client;
    }

    private static bool IsWindowsReleaseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        return !tag.Contains("-linux", StringComparison.OrdinalIgnoreCase)
               && !tag.Contains("-macos", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExpectedAssetName()
    {
        var runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        return $"ToastifyReloaded-Setup-{runtime}.exe";
    }

    private static void ValidatePortableExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 64 || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
            throw new InvalidDataException("L'asset scaricato non è un installer Windows valido.");
        stream.Position = 0x3c;
        using var reader = new BinaryReader(stream);
        var peOffset = reader.ReadInt32();
        if (peOffset < 64 || peOffset > stream.Length - 4)
            throw new InvalidDataException("L'installer Windows scaricato ha un'intestazione PE non valida.");
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550)
            throw new InvalidDataException("L'installer Windows scaricato ha un'intestazione PE non valida.");
    }

    private static string NormalizeVersion(string raw)
        => ToastifyReloaded.Shared.WindowsReleaseVersion.Normalize(raw);

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}

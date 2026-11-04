using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ToastifyReloaded.Models;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace ToastifyReloaded.Services;

/// <summary>Opaque checked target. Only its originating backend may download/apply it.</summary>
public sealed class VelopackUpdatePackage
{
    public string Version { get; }
    public string AssetName { get; }
    public bool IsDelta { get; }
    public long DownloadSize { get; }
    internal object NativeUpdate { get; }

    internal VelopackUpdatePackage(string version, string assetName, bool isDelta, long downloadSize, object nativeUpdate)
    {
        Version = version;
        AssetName = assetName;
        IsDelta = isDelta;
        DownloadSize = downloadSize;
        NativeUpdate = nativeUpdate;
    }
}

public interface IVelopackUpdateBackend
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    string Channel { get; }
    Task<VelopackUpdatePackage?> CheckAsync(CancellationToken cancellationToken = default);
    Task DownloadAsync(VelopackUpdatePackage update, IProgress<UpdateProgressInfo>? progress,
        CancellationToken cancellationToken = default);
    // Success means helper handoff only. The caller must promptly shut down WPF.
    Task<bool> ApplyAndRestartAsync(VelopackUpdatePackage update, bool restartMinimized,
        CancellationToken cancellationToken = default);
}

/// <summary>Uses the pinned SDK to reconstruct deltas, verify checksums and launch its native updater.</summary>
public sealed class VelopackUpdateService : IVelopackUpdateBackend
{
    public const string PackageId = "ToastifyReloaded";
    public const string RepositoryUrl = "https://github.com/MarliusLab/Toastify-Reloaded";
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly UpdateManager _manager;
    private readonly VelopackHttpDownloader _downloader;
    private readonly IVelopackLocator _locator;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly TimeSpan _checkTimeout;
    private readonly TimeSpan _downloadTimeout;
    private VelopackUpdatePackage? _checked;
    private VelopackUpdatePackage? _downloaded;
    private ReconstructedPackageReceipt? _verifiedPackage;

    public VelopackUpdateService() : this(new VelopackHttpDownloader(Client),
        GetChannel(RuntimeInformation.ProcessArchitecture)) { }

    private VelopackUpdateService(VelopackHttpDownloader downloader, string channel)
        : this(new ToastifyUpdateManager(new ToastifyGithubSource(channel, downloader),
            new UpdateOptions { ExplicitChannel = channel, AllowVersionDowngrade = false }), downloader, channel) { }

    internal VelopackUpdateService(UpdateManager manager, VelopackHttpDownloader downloader, string channel,
        TimeSpan? checkTimeout = null, TimeSpan? downloadTimeout = null, IVelopackLocator? locator = null)
    {
        _manager = manager;
        _downloader = downloader;
        _locator = locator ?? VelopackLocator.Current;
        Channel = channel;
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(25);
        _downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(10);
    }

    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToFullString() ?? string.Empty;
    public string Channel { get; }
    public static string GetChannel(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => throw new PlatformNotSupportedException("Toastify Reloaded supporta Windows x64 e ARM64.")
    };

    public async Task<VelopackUpdatePackage?> CheckAsync(CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(_checkTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            _checked = null;
            _downloaded = null;
            _verifiedPackage = null;
            return await Task.Run(async () =>
            {
                using var scope = _downloader.BeginOperation(linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                await ImportSeedAsync(linked.Token).ConfigureAwait(false);
                var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (update is null) return null;
                ValidateTarget(update);
                var deltas = update.DeltasToTarget;
                var deltaSize = deltas.Sum(d => d.Size);
                var useDeltas = update.BaseRelease is not null && deltas.Length is > 0 and <= 10 &&
                    deltaSize <= update.TargetFullRelease.Size;
                var size = useDeltas ? deltaSize : update.TargetFullRelease.Size;
                return _checked = new VelopackUpdatePackage(update.TargetFullRelease.Version.ToFullString(),
                    update.TargetFullRelease.FileName, useDeltas, size, update);
            }, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Controllo aggiornamenti scaduto per timeout.", ex); }
        finally { _operation.Release(); }
    }

    public async Task DownloadAsync(VelopackUpdatePackage update, IProgress<UpdateProgressInfo>? progress,
        CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(_downloadTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            if (!ReferenceEquals(update, _checked)) throw new InvalidOperationException("Controllare di nuovo l'aggiornamento prima del download.");
            _downloaded = null;
            _verifiedPackage = null;
            await Task.Run(async () =>
            {
                var percent = 0;
                var transfer = new VelopackTransferProgress(0, null);
                void Report() => progress?.Report(new(UpdatePhase.Downloading, transfer.BytesReceived,
                    transfer.TotalBytes, PercentOverride: percent, IsPackageUpdate: true));
                using var scope = _downloader.BeginOperation(linked.Token, p => { transfer = p; Report(); });
                await ImportSeedAsync(linked.Token).ConfigureAwait(false);
                var stagedPath = Path.Combine(_locator.PackagesDir!, update.AssetName);
                var expected = ((UpdateInfo)update.NativeUpdate).TargetFullRelease;
                // A cold cache has no in-memory evidence of native reconstruction.
                // Keep only byte-exact published full packages; otherwise rebuild.
                if (File.Exists(stagedPath) && !await MatchesAsync(stagedPath, expected.SHA256, expected.Size,
                        linked.Token).ConfigureAwait(false))
                    File.Delete(stagedPath);
                Report();
                await _manager.DownloadUpdatesAsync((UpdateInfo)update.NativeUpdate,
                    value => { percent = Math.Clamp(value, 0, 100); Report(); }, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                // The SDK skips its checksum check if a completed file already
                // exists. Always verify it here too before any native handoff.
                try
                {
                    string hash;
                    long size;
                    await using (var staged = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                     64 * 1024, useAsync: true))
                    {
                        hash = Convert.ToHexString(await SHA256.HashDataAsync(staged, linked.Token).ConfigureAwait(false));
                        size = staged.Length;
                    }
                    var publishedFull = size == expected.Size && hash.Equals(expected.SHA256, StringComparison.OrdinalIgnoreCase);
                    var receipt = (_manager as ToastifyUpdateManager)?.ReconstructedPackage;
                    var freshlyReconstructed = receipt is not null && receipt.AssetName == update.AssetName &&
                        receipt.Size == size && receipt.SHA256.Equals(hash, StringComparison.OrdinalIgnoreCase);
                    if (!publishedFull && !freshlyReconstructed)
                            throw new InvalidDataException("Il checksum del pacchetto aggiornamento non è valido.");
                    var actual = VelopackAsset.FromNupkgNoChecksum(stagedPath);
                    if (actual.PackageId != PackageId || actual.Version.ToFullString() != update.Version)
                        throw new InvalidDataException("Il pacchetto scaricato non appartiene a questa applicazione.");
                    _verifiedPackage = new(update.AssetName, hash, size);
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or FormatException)
                {
                    try { File.Delete(stagedPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    throw new InvalidDataException("Il pacchetto aggiornamento non è valido; ripetere il download.", ex);
                }
                progress?.Report(new(UpdatePhase.Verifying, transfer.BytesReceived, transfer.TotalBytes,
                    IsPackageUpdate: true));
                _downloaded = update;
            }, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Tempo massimo del download aggiornamento superato.", ex); }
        finally { _operation.Release(); }
    }

    public async Task<bool> ApplyAndRestartAsync(VelopackUpdatePackage update, bool restartMinimized,
        CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(update, _downloaded)) throw new InvalidOperationException("Il pacchetto non è stato scaricato e verificato.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(_locator.PackagesDir!, update.AssetName)))
                throw new FileNotFoundException("Il pacchetto verificato non è più disponibile; ripetere il download.");
            if (_verifiedPackage is not { } verified ||
                !await MatchesAsync(Path.Combine(_locator.PackagesDir!, update.AssetName), verified.SHA256,
                    verified.Size, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Il pacchetto verificato è stato modificato; ripetere il download.");
            // Do not abandon this noncancelable process handoff. Success must reach
            // the caller so it can release the tray, hooks and single-instance mutex.
            await Task.Run(() => _manager.WaitExitThenApplyUpdates(((UpdateInfo)update.NativeUpdate).TargetFullRelease,
                silent: false, restart: true, restartArgs: restartMinimized ? ["--minimized"] : [])).ConfigureAwait(false);
            _checked = null;
            _downloaded = null;
            _verifiedPackage = null;
            return true;
        }
        finally { _operation.Release(); }
    }

    private void ValidateTarget(UpdateInfo update)
    {
        var all = update.DeltasToTarget.Append(update.TargetFullRelease);
        foreach (var asset in all)
        {
            if (asset.PackageId != PackageId || asset.Version is null || asset.Version.IsPrerelease ||
                asset.Size <= 0 || string.IsNullOrWhiteSpace(asset.FileName) ||
                string.IsNullOrWhiteSpace(asset.SHA256) || asset.SHA256.Length != 64 || asset.SHA256.Any(c => !Uri.IsHexDigit(c)) ||
                asset.FileName != Path.GetFileName(asset.FileName) || asset.FileName.Contains('\\') ||
                !asset.FileName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) ||
                !asset.FileName.Contains("-" + Channel + "-", StringComparison.Ordinal) ||
                asset.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Il feed non contiene un pacchetto Windows valido per questa architettura.");
        }
        if (update.IsDowngrade || update.TargetFullRelease.Type != VelopackAssetType.Full ||
            update.DeltasToTarget.Any(d => d.Type != VelopackAssetType.Delta))
            throw new InvalidDataException("Il feed contiene un aggiornamento non consentito.");
    }

    private async Task ImportSeedAsync(CancellationToken token)
    {
        try
        {
            if (_locator.RootAppDir is { } root && _locator.PackagesDir is { } cache && _manager.CurrentVersion is { } version)
                await VelopackPackageSeed.TryImportAsync(root, cache, version.ToFullString(), Channel, token).ConfigureAwait(false);
        }
        catch (IOException) { /* Optional base unavailable: SDK falls back to a full download. */ }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<bool> MatchesAsync(string path, string expectedHash, long expectedSize, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, useAsync: true);
        if (stream.Length != expectedSize) return false;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        return hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using ToastifyReloaded.Mac.Models;
using ToastifyReloaded.Shared;

namespace ToastifyReloaded.Mac.Services;

public sealed record DownloadedMacUpdate(MacUpdateInfo Update, MacUpdateAsset? Asset, string? FilePath,
    PortableUpdatePackage? Package = null);

public sealed class MacUpdateInstallerService
{
    private readonly ProcessService _process;
    private readonly HttpClient _http;
    private readonly IPortableUpdateBackend? _packages;

    public MacUpdateInstallerService(ProcessService process, IPortableUpdateBackend? packages = null, HttpClient? http = null)
    {
        _process = process;
        _packages = packages ?? (OperatingSystem.IsMacOS() ? new PortablePackageUpdateService("macos") : null);
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ToastifyReloaded/1.5.0");
    }

    public Task<MacUpdateApplyResult> DownloadAndApplyAsync(MacUpdateInfo update, CancellationToken cancellationToken = default)
        => DownloadAndApplyAsync(update, null, cancellationToken);

    public async Task<MacUpdateApplyResult> DownloadAndApplyAsync(MacUpdateInfo update,
        IProgress<UpdateOperationProgress>? progress, CancellationToken cancellationToken = default)
        => await InstallAsync(await DownloadAsync(update, progress, cancellationToken), progress, cancellationToken);

    public async Task<DownloadedMacUpdate> DownloadAsync(MacUpdateInfo update,
        IProgress<UpdateOperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_packages?.IsInstalled == true)
        {
            progress?.Report(new(UpdateOperationStage.Checking));
            var package = await _packages.CheckAsync(cancellationToken);
            var expected = update.Version.Major + "." + update.Version.Minor + "." + update.Version.Patch;
            if (update.Version.Stage != MacReleaseStage.Stable)
                expected += update.Version.Stage == MacReleaseStage.Preview ? "-preview." + update.Version.StageNumber : "-rc." + update.Version.StageNumber;
            if (package is null || package.Version != expected)
                throw new InvalidDataException("The checked macOS package feed does not match the requested release.");
            await _packages.DownloadAsync(package, progress, cancellationToken);
            return new(update, null, null, package);
        }
        var asset = SelectAsset(update, RuntimeInformation.ProcessArchitecture)
            ?? throw new InvalidDataException("Nessun pacchetto macOS compatibile trovato nella release.");
        var destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Caches", "ToastifyReloaded", "updates", update.Tag, asset.Name);
        await SafeUpdateDownloader.DownloadAsync(_http, asset.DownloadUri, update.Tag, asset.Name,
            destination, asset.Size, asset.Digest, progress, cancellationToken);
        return new(update, asset, destination);
    }

    public async Task<MacUpdateApplyResult> InstallAsync(DownloadedMacUpdate downloaded,
        IProgress<UpdateOperationProgress>? progress = null, CancellationToken cancellationToken = default,
        bool restartMinimized = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (downloaded.Package is { } package)
        {
            if (_packages?.IsInstalled != true) throw new InvalidOperationException("Package backend changed; check again.");
            progress?.Report(new(UpdateOperationStage.WaitingForPermission));
            var started = await _packages.ApplyAndRestartAsync(package, restartMinimized, cancellationToken);
            return new(started, started, started ? "Avvio aggiornamento macOS; segui l'eventuale richiesta di autorizzazione." :
                "Impossibile avviare l'aggiornamento macOS.", null);
        }
        var asset = downloaded.Asset ?? throw new InvalidDataException("Missing checked update asset.");
        var downloadedPath = downloaded.FilePath ?? throw new InvalidDataException("Missing checked update path.");
        if (!SafeUpdateDownloader.IsTrustedAssetUri(asset.DownloadUri, downloaded.Update.Tag, asset.Name) ||
            Path.GetFileName(downloadedPath) != asset.Name) throw new InvalidDataException("Untrusted checked update.");
        progress?.Report(new(UpdateOperationStage.Verifying));
        await SafeUpdateDownloader.VerifyFileAsync(downloadedPath, asset.Size,
            SafeUpdateDownloader.ParseDigest(asset.Digest), cancellationToken);
        using (var zip = ZipFile.OpenRead(downloadedPath))
        {
            if (zip.Entries.Any(entry => entry.FullName.StartsWith('/') || entry.FullName.Contains('\\') ||
                entry.FullName.Split('/').Contains("..")))
                throw new InvalidDataException("Il pacchetto contiene percorsi non sicuri.");
        }
        var staging = Path.Combine(Path.GetDirectoryName(downloadedPath)!, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        progress?.Report(new(UpdateOperationStage.Preparing));
        var extract = await _process.RunAsync("/usr/bin/ditto", new[] { "-x", "-k", downloadedPath, staging },
            cancellationToken, TimeSpan.FromMinutes(2));
        if (extract.ExitCode != 0) return new(false, false, extract.StdErr, downloadedPath);
        var apps = Directory.GetDirectories(staging, "*.app", SearchOption.TopDirectoryOnly);
        if (apps.Length != 1) return new(false, false, "Il pacchetto deve contenere un solo bundle .app.", downloadedPath);
        var current = FindCurrentAppBundle();
        if (current is null)
        {
            // An unpackaged development process cannot replace a real install.
            return new(true, false, "Aggiornamento verificato ed estratto; installa il bundle dalla cartella scaricata.", apps[0]);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var helper = MacUpdateHandoff.Create(current, apps[0], Environment.ProcessId, restartMinimized,
            resultDirectory: Path.GetDirectoryName(downloadedPath));
        progress?.Report(new(UpdateOperationStage.WaitingForPermission,
            Message: "Chiudi Toastify e segui l'eventuale autorizzazione macOS; il riavvio avviene come utente normale."));
        cancellationToken.ThrowIfCancellationRequested();
        if (Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh", UseShellExecute = false, CreateNoWindow = true, ArgumentList = { helper.Helper }
        }) is null) return new(false, false, "Impossibile avviare l'assistente aggiornamento.", downloadedPath);
        return new(true, true, "Assistente aggiornamento avviato; sostituirà il bundle dopo la chiusura di Toastify. " +
            "Esito e diagnostica: " + helper.Receipt + " / " + helper.Log, downloadedPath);
    }

    internal static MacUpdateAsset? SelectAsset(MacUpdateInfo update, Architecture architecture)
    {
        if (architecture is not (Architecture.X64 or Architecture.Arm64)) return null;
        var expected = "ToastifyReloaded-macOS-" + (architecture == Architecture.Arm64 ? "arm64" : "x64") + ".zip";
        return update.Assets.SingleOrDefault(asset => asset.Name == expected);
    }

    internal static string? FindCurrentAppBundle()
    {
        var process = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(process)) return null;
        var directory = new DirectoryInfo(Path.GetDirectoryName(process)!);
        while (directory is not null)
        {
            if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    internal static string CreateReplacementHelper(string currentApp, string newApp, int pid, bool minimized = false)
        => MacUpdateHandoff.Create(currentApp, newApp, pid, minimized).Helper;
}

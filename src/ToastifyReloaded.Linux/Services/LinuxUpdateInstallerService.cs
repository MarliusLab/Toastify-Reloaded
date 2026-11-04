using System.Diagnostics;
using System.Runtime.InteropServices;
using ToastifyReloaded.Linux.Models;
using ToastifyReloaded.Shared;

namespace ToastifyReloaded.Linux.Services;

public enum LinuxInstallKind { Portable, AppImage, Deb }
public sealed record DownloadedLinuxUpdate(LinuxUpdateInfo Update, LinuxUpdateAsset? Asset, string? FilePath,
    LinuxInstallKind Kind, PortableUpdatePackage? Package = null);

public sealed class LinuxUpdateInstallerService
{
    private readonly ProcessService _process;
    private readonly HttpClient _http;
    private readonly IPortableUpdateBackend? _packages;

    public LinuxUpdateInstallerService(ProcessService process, IPortableUpdateBackend? packages = null, HttpClient? http = null)
    {
        _process = process;
        _packages = packages ?? (OperatingSystem.IsLinux() ? new PortablePackageUpdateService("linux") : null);
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ToastifyReloaded/1.4.2");
    }

    public Task<LinuxUpdateApplyResult> DownloadAndApplyAsync(LinuxUpdateInfo update, CancellationToken cancellationToken = default)
        => DownloadAndApplyAsync(update, null, cancellationToken);

    public async Task<LinuxUpdateApplyResult> DownloadAndApplyAsync(LinuxUpdateInfo update,
        IProgress<UpdateOperationProgress>? progress, CancellationToken cancellationToken = default)
        => await InstallAsync(await DownloadAsync(update, progress, cancellationToken), progress, cancellationToken);

    public async Task<DownloadedLinuxUpdate> DownloadAsync(LinuxUpdateInfo update,
        IProgress<UpdateOperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var kind = DetectInstallKind();
        if (_packages?.IsInstalled == true)
        {
            progress?.Report(new(UpdateOperationStage.Checking));
            var package = await _packages.CheckAsync(cancellationToken);
            var expected = update.Version.Major + "." + update.Version.Minor + "." + update.Version.Patch;
            if (update.Version.Stage != LinuxReleaseStage.Stable)
                expected += update.Version.Stage == LinuxReleaseStage.Preview ? "-preview." + update.Version.StageNumber : "-rc." + update.Version.StageNumber;
            if (package is null || package.Version != expected)
                throw new InvalidDataException("The checked Linux package feed does not match the requested release.");
            await _packages.DownloadAsync(package, progress, cancellationToken);
            return new(update, null, null, kind, package);
        }
        var asset = SelectAsset(update, kind, RuntimeInformation.ProcessArchitecture)
            ?? throw new InvalidDataException("Nessun pacchetto Linux compatibile trovato nella release.");
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var cache = !string.IsNullOrWhiteSpace(xdg) ? xdg : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        var destination = Path.Combine(cache, "toastify-reloaded", "updates", update.Tag, asset.Name);
        await SafeUpdateDownloader.DownloadAsync(_http, asset.DownloadUri, update.Tag, asset.Name,
            destination, asset.Size, asset.Digest, progress, cancellationToken);
        return new(update, asset, destination, kind);
    }

    public async Task<LinuxUpdateApplyResult> InstallAsync(DownloadedLinuxUpdate downloaded,
        IProgress<UpdateOperationProgress>? progress = null, CancellationToken cancellationToken = default,
        bool restartMinimized = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (downloaded.Package is { } package)
        {
            if (_packages?.IsInstalled != true) throw new InvalidOperationException("Package backend changed; check again.");
            progress?.Report(new(UpdateOperationStage.WaitingForPermission));
            var started = await _packages.ApplyAndRestartAsync(package, restartMinimized, cancellationToken);
            return new(started, started, started ? "Avvio aggiornamento Linux; segui l'eventuale richiesta di autorizzazione." :
                "Impossibile avviare l'aggiornamento Linux.", null);
        }
        var asset = downloaded.Asset ?? throw new InvalidDataException("Missing checked update asset.");
        var path = downloaded.FilePath ?? throw new InvalidDataException("Missing checked update path.");
        if (!SafeUpdateDownloader.IsTrustedAssetUri(asset.DownloadUri, downloaded.Update.Tag, asset.Name) ||
            Path.GetFileName(path) != asset.Name) throw new InvalidDataException("Untrusted checked update.");
        progress?.Report(new(UpdateOperationStage.Verifying));
        await SafeUpdateDownloader.VerifyFileAsync(path, asset.Size, SafeUpdateDownloader.ParseDigest(asset.Digest), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (downloaded.Kind == LinuxInstallKind.Portable)
        {
            progress?.Report(new(UpdateOperationStage.Ready, Message: "Pacchetto pronto per l'installazione manuale."));
            return new(true, false, "Aggiornamento verificato e scaricato: " + path, path);
        }
        var installed = false;
        try
        {
            if (downloaded.Kind == LinuxInstallKind.AppImage)
            {
                progress?.Report(new(UpdateOperationStage.Preparing));
                var current = Environment.GetEnvironmentVariable("APPIMAGE");
                if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
                    throw new IOException("AppImage corrente non disponibile.");
                var staged = current + ".new-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(path, staged);
                    await SafeUpdateDownloader.VerifyFileAsync(staged, asset.Size, SafeUpdateDownloader.ParseDigest(asset.Digest), cancellationToken);
                    File.SetUnixFileMode(staged, File.GetUnixFileMode(current));
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new(UpdateOperationStage.Installing));
                    // Same-directory rename keeps the active AppImage in place
                    // until the complete verified replacement is available.
                    File.Copy(current, current + ".previous", overwrite: true);
                    File.Move(staged, current, overwrite: true);
                    installed = true;
                    var restart = LinuxRestartHandoff.RequestAfterInstall("AppImage aggiornata", current, () =>
                        Process.Start(LinuxRestartHandoff.CreateStartInfo(current, Environment.ProcessId, restartMinimized)) is not null);
                    ReportCompleted(progress, restart.Message);
                    return restart;
                }
                finally { if (File.Exists(staged)) File.Delete(staged); }
            }
            if (!await _process.ExistsAsync("pkexec", cancellationToken) ||
                !await _process.ExistsAsync("apt-get", cancellationToken))
                return new(true, false, "Pacchetto .deb verificato: " + path, path);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(UpdateOperationStage.WaitingForPermission, Message: "Conferma l'installazione nel gestore pacchetti."));
            // Authorization is the handoff boundary. Do not cancel a package
            // manager transaction merely because the old app is closing.
            var result = await _process.RunAsync("pkexec", new[] { "apt-get", "install", "-y", path },
                CancellationToken.None, TimeSpan.FromMinutes(5));
            if (result.ExitCode != 0)
                return new(false, false, string.IsNullOrWhiteSpace(result.StdErr) ? "Installazione .deb non riuscita." : result.StdErr, path);
            installed = true;
            const string launcher = "/usr/bin/toastify-reloaded";
            var debRestart = LinuxRestartHandoff.RequestAfterInstall("Pacchetto .deb aggiornato", path, () =>
                File.Exists(launcher) && Process.Start(LinuxRestartHandoff.CreateStartInfo(launcher, Environment.ProcessId, restartMinimized)) is not null);
            ReportCompleted(progress, debRestart.Message);
            return debRestart;
        }
        catch (OperationCanceledException) when (!installed) { throw; }
        catch (Exception error)
        {
            if (installed)
            {
                var message = LinuxRestartHandoff.ManualRestartMessage("Aggiornamento Linux installato") + " Dettagli: " + error.Message;
                ReportCompleted(progress, message);
                return new(true, false, message, path, Installed: true);
            }
            progress?.Report(new(UpdateOperationStage.Failed, Message: error.Message));
            return new(false, false, error.Message, path);
        }
    }

    private static void ReportCompleted(IProgress<UpdateOperationProgress>? progress, string message)
    {
        try { progress?.Report(new(UpdateOperationStage.Completed, Message: message)); }
        catch (Exception) { /* Observers cannot turn a committed install into a failure. */ }
    }

    private static LinuxInstallKind DetectInstallKind()
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage)) return LinuxInstallKind.AppImage;
        return (Environment.ProcessPath ?? "").StartsWith("/opt/toastify-reloaded/", StringComparison.Ordinal)
            ? LinuxInstallKind.Deb : LinuxInstallKind.Portable;
    }

    internal static LinuxUpdateAsset? SelectAsset(LinuxUpdateInfo update, LinuxInstallKind kind, Architecture architecture)
    {
        if (architecture is not (Architecture.X64 or Architecture.Arm64)) return null;
        var arm = architecture == Architecture.Arm64;
        var version = update.Version.Major + "." + update.Version.Minor + "." + update.Version.Patch;
        var expected = kind switch
        {
            LinuxInstallKind.AppImage => "ToastifyReloaded-Linux-" + (arm ? "arm64" : "x64") + ".AppImage",
            LinuxInstallKind.Deb => "toastify-reloaded_" + version + "_" + (arm ? "arm64" : "amd64") + ".deb",
            _ => "ToastifyReloaded-Linux-" + (arm ? "arm64" : "x64") + ".tar.gz"
        };
        return update.Assets.SingleOrDefault(asset => asset.Name == expected);
    }
}

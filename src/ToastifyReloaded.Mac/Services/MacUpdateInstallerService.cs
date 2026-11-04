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
        var helper = CreateReplacementHelper(current, apps[0], Environment.ProcessId, restartMinimized);
        progress?.Report(new(UpdateOperationStage.WaitingForPermission,
            Message: "Chiudi Toastify e segui l'eventuale autorizzazione macOS; il riavvio avviene come utente normale."));
        cancellationToken.ThrowIfCancellationRequested();
        if (Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh", UseShellExecute = false, CreateNoWindow = true, ArgumentList = { helper }
        }) is null) return new(false, false, "Impossibile avviare l'assistente aggiornamento.", downloadedPath);
        return new(true, true, "Assistente aggiornamento avviato; sostituirà il bundle dopo la chiusura di Toastify.", downloadedPath);
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
    {
        if (pid <= 0 || !Path.IsPathRooted(currentApp) || !currentApp.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathRooted(newApp) || !newApp.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid macOS update handoff.");
        var helperRoot = Path.Combine(Path.GetTempPath(), "ToastifyReloaded-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperRoot);
        var worker = Path.Combine(helperRoot, "replace.sh");
        var appleScript = Path.Combine(helperRoot, "elevate.applescript");
        var helper = Path.Combine(helperRoot, "apply-update.sh");
        File.WriteAllText(worker, """
#!/bin/sh
set -eu
current=$1
staged=$2
nonce=$3
candidate="$current.update-$nonce"
backup="$current.previous-$nonce"
[ ! -e "$candidate" ] && [ ! -e "$backup" ] || exit 73
trap '/bin/rm -rf -- "$candidate"' EXIT
/usr/bin/ditto "$staged" "$candidate"
/bin/mv "$current" "$backup"
if /bin/mv "$candidate" "$current"; then
  /bin/rm -rf -- "$backup"
else
  /bin/mv "$backup" "$current"
  exit 1
fi
""");
        File.WriteAllText(appleScript, """
on run argv
    set commandText to "/bin/sh " & quoted form of (item 1 of argv) & " " & quoted form of (item 2 of argv) & " " & quoted form of (item 3 of argv) & " " & quoted form of (item 4 of argv)
    do shell script commandText with administrator privileges
end run
""");
        // Paths are literal shell arguments. The AppleScript lives in its own
        // file, so quotes/spaces/apostrophes never cross two quoting layers.
        var lines = new[]
        {
            "#!/bin/sh", "set -eu",
            "current=" + ShellQuote(currentApp), "staged=" + ShellQuote(newApp),
            "worker=" + ShellQuote(worker), "apple=" + ShellQuote(appleScript),
            "nonce=" + ShellQuote(Guid.NewGuid().ToString("N")),
            "count=0", "while /bin/kill -0 " + pid + " 2>/dev/null; do",
            "  count=$((count + 1))", "  [ \"$count\" -lt 240 ] || exit 1", "  /bin/sleep 0.25", "done",
            "if [ -w \"$(/usr/bin/dirname \"$current\")\" ] && [ -w \"$current\" ]; then",
            "  /bin/sh \"$worker\" \"$current\" \"$staged\" \"$nonce\"",
            "else", "  /usr/bin/osascript \"$apple\" \"$worker\" \"$current\" \"$staged\" \"$nonce\"", "fi",
            // This outer helper is not elevated. Never open the app from the
            // privileged replacement worker or inherit its administrator token.
            "/usr/bin/open \"$current\"" + (minimized ? " --args --minimized" : ""),
            "/bin/rm -rf -- " + ShellQuote(helperRoot), ""
        };
        File.WriteAllText(helper, string.Join("\n", lines));
        foreach (var file in new[] { helper, worker })
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return helper;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

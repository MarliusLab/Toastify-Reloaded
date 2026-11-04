using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ToastifyReloaded.Models;
using ToastifyReloaded.Services;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace ToastifyReloaded.Windows.Tests;

public static class VelopackUpdateTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(VelopackUpdateService.GetChannel(Architecture.X64) == "win-x64" &&
              VelopackUpdateService.GetChannel(Architecture.Arm64) == "win-arm64",
            "Velopack channels distinguish x64 and ARM64 in the multi-platform repository");
        check(StartupService.ResolveExecutablePath(@"C:\App\current\ToastifyReloaded.exe", @"C:\App", true, _ => true) ==
              @"C:\App\ToastifyReloaded.exe" &&
              StartupService.ResolveExecutablePath(@"C:\Old\ToastifyReloaded.exe", null, false, _ => false) ==
              @"C:\Old\ToastifyReloaded.exe",
            "startup keeps the stable Velopack stub and preserves legacy executable paths");

        var backend = new FakeBackend();
        var service = new WindowsUpdateService(backend: backend);
        var result = await service.CheckLatestAsync();
        check(result.UpdateAvailable && result.AssetName!.EndsWith(".nupkg") && result.DownloadUrl is null &&
              service.IsVelopackInstalled && service.CurrentVersion == "1.3.10",
            "packaged facade returns its checked SDK target without requiring an installer URL");
        check(!await service.PrepareAndLaunchUpdateAsync(result with { LatestVersion = "99.0.0" }, 111) && backend.Downloads == 0,
            "packaged facade refuses a modified or foreign checked target");
        var reports = new List<UpdateProgressInfo>();
        check(await service.PrepareAndLaunchUpdateAsync(result, 111, restartMinimized: true,
                  progress: new InlineProgress(reports.Add)) && backend.Downloads == 1 && backend.Applies == 1 && backend.Minimized,
            "packaged facade downloads then hands off the exact target and minimized restart");
        check(reports.Select(p => p.Phase).SequenceEqual(new[] { UpdatePhase.Downloading, UpdatePhase.Verifying,
                  UpdatePhase.WaitingForPermission, UpdatePhase.LaunchingUpdater }) && reports.All(p => p.IsPackageUpdate),
            "packaged progress distinguishes preparation, uncancelable helper handoff, and restart");
        check(!await service.PrepareAndLaunchUpdateAsync(result, 111) && backend.Applies == 1,
            "a successfully handed-off package cannot be applied twice");
        backend.NoUpdate = true;
        check(!(await service.CheckLatestAsync()).UpdateAvailable, "packaged facade reports no update without an installer fallback");
        backend.NoUpdate = false;
        backend.FailCheck = true;
        check(!(await service.CheckLatestAsync()).Success, "SDK metadata failure is surfaced without switching to an unrelated installer");
        backend.FailCheck = false;
        result = await service.CheckLatestAsync();
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            check(await CaptureAsync(() => service.PrepareAndLaunchUpdateAsync(result, 111,
                      cancellationToken: canceled.Token)) is OperationCanceledException && backend.Applies == 1,
                "caller cancellation prevents a packaged helper handoff");
        }
        backend.CancelDownload = true;
        check(await CaptureAsync(() => service.PrepareAndLaunchUpdateAsync(result, 111)) is OperationCanceledException &&
              backend.Applies == 1, "download cancellation never starts the packaged updater");

        using (var legacyClient = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
               { Content = new StringContent("[]") })))
        {
            var legacyBackend = new FakeBackend { IsInstalled = false };
            var legacy = new UpdateService(legacyClient, currentVersion: "1.3.9");
            var facade = new WindowsUpdateService(legacy: legacy, backend: legacyBackend);
            var response = await facade.CheckLatestAsync();
            check(!response.Success && response.CurrentVersion == "1.3.9" && legacyBackend.Checks == 0,
                "non-Velopack installation keeps the existing legacy updater and version");
        }

        var folder = Path.Combine(AppContext.BaseDirectory, "velopack-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await RunSeedTestsAsync(folder, check);
            await RunDownloaderTestsAsync(folder, check);
            await RunGithubSourceTestsAsync(check);
            await RunSdkTestsAsync(folder, check);
            await RunSdkDeltaTestsAsync(folder, check);
            await RunNativeDeltaTestsAsync(folder, check);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static async Task RunSeedTestsAsync(string folder, Action<bool, string> check)
    {
        var root = Path.Combine(folder, "installed");
        var packages = Path.Combine(root, "packages");
        var cache = Path.Combine(folder, "user-cache");
        Directory.CreateDirectory(packages);
        var name = "ToastifyReloaded-1.3.10-win-x64-full.nupkg";
        var path = Path.Combine(packages, name);
        var payload = Encoding.UTF8.GetBytes("protected bridge base");
        await File.WriteAllBytesAsync(path, payload);
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        await File.WriteAllTextAsync(path + ".sha256", hash + "\n");
        check(await VelopackPackageSeed.TryImportAsync(root, cache, "1.3.10", "win-x64") &&
              File.ReadAllBytes(Path.Combine(cache, name)).SequenceEqual(payload),
            "MSI bridge base is copied into the user SDK cache only after exact SHA256 validation");
        var timestamp = File.GetLastWriteTimeUtc(Path.Combine(cache, name));
        check(await VelopackPackageSeed.TryImportAsync(root, cache, "1.3.10", "win-x64") &&
              timestamp == File.GetLastWriteTimeUtc(Path.Combine(cache, name)),
            "a valid existing user cache base is retained without overwrite");
        await File.WriteAllTextAsync(Path.Combine(cache, name), "corrupted cache");
        check(await VelopackPackageSeed.TryImportAsync(root, cache, "1.3.10", "win-x64") &&
              File.ReadAllBytes(Path.Combine(cache, name)).SequenceEqual(payload),
            "a corrupt cached base is replaced through a verified temporary file");
        check(!await VelopackPackageSeed.TryImportAsync(root, cache, "1.3.11", "win-x64") &&
              !await VelopackPackageSeed.TryImportAsync(root, cache, "1.3.10", "win-arm64"),
            "the seed never crosses installed versions or CPU channels");
        await File.WriteAllTextAsync(path, "corrupted source");
        var other = Path.Combine(folder, "corrupt-seed-cache");
        check(!await VelopackPackageSeed.TryImportAsync(root, other, "1.3.10", "win-x64") &&
              !Directory.EnumerateFiles(other).Any(),
            "a corrupt protected seed is not imported and temporary files are removed");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        check(await CaptureAsync(() => VelopackPackageSeed.TryImportAsync(root, other, "1.3.10", "win-x64", canceled.Token))
              is OperationCanceledException && !Directory.EnumerateFiles(other).Any(),
            "seed import honors cancellation and leaves no partial cache file");
        using var duringCopy = new CancellationTokenSource();
        check(await CaptureAsync(() => VelopackPackageSeed.TryImportAsync(root, other, "1.3.10", "win-x64", duringCopy.Token,
                  async (_, output, token) =>
                  {
                      await output.WriteAsync(new byte[] { 1, 2, 3 }, token);
                      duringCopy.Cancel();
                      token.ThrowIfCancellationRequested();
                  })) is OperationCanceledException && !Directory.EnumerateFiles(other).Any(),
            "cancellation during seed copying removes the already-created partial temporary file");
    }

    private static async Task RunDownloaderTestsAsync(string folder, Action<bool, string> check)
    {
        var payload = Encoding.UTF8.GetBytes("bounded package content");
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var downloader = new VelopackHttpDownloader(client);
        var reports = new List<VelopackTransferProgress>();
        using (downloader.BeginOperation(default, reports.Add))
            await downloader.DownloadFile("https://github.com/test/package", Path.Combine(folder, "download.bin"), _ => { });
        check(reports.First().BytesReceived == 0 && reports.Last().BytesReceived == payload.Length &&
              reports.Last().TotalBytes == payload.Length,
            "Velopack transport reports actual received bytes and known lengths");
        check(await CaptureAsync(() => downloader.DownloadBytes("http://github.com/insecure")) is InvalidDataException,
            "Velopack transport refuses unencrypted update metadata");
        using var stalledClient = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
            { Content = new StreamContent(new StalledStream()) }));
        var stalled = new VelopackHttpDownloader(stalledClient, metadataTimeout: TimeSpan.FromMilliseconds(50),
            idleTimeout: TimeSpan.FromMilliseconds(30));
        check(await CaptureAsync(() => stalled.DownloadBytes("https://github.com/test/feed")) is TimeoutException,
            "Velopack metadata body has a bounded idle timeout after response headers");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using (stalled.BeginOperation(cancel.Token))
            check(await CaptureAsync(() => stalled.DownloadBytes("https://github.com/test/feed")) is OperationCanceledException,
                "SDK metadata without a token parameter receives caller cancellation through the transport scope");
        using var headersClient = new HttpClient(new StalledHandler());
        var headers = new VelopackHttpDownloader(headersClient, idleTimeout: TimeSpan.FromMilliseconds(30));
        check(await CaptureAsync(() => headers.DownloadFile("https://github.com/test/stalled", Path.Combine(folder, "never-created"),
                  _ => { })) is TimeoutException && !File.Exists(Path.Combine(folder, "never-created")),
            "Velopack package response headers have an idle deadline before any file is created");
    }

    private static async Task RunSdkTestsAsync(string folder, Action<bool, string> check)
    {
        var root = Path.Combine(folder, "sdk-installed");
        var cache = Path.Combine(folder, "sdk-cache");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(cache);
        var helper = Path.Combine(root, "Update.exe");
        await File.WriteAllTextAsync(helper, "never executed");
        var process = new CapturingProcess();
        var source = new Source();
        var baseAsset = Asset("1.3.10", VelopackAssetType.Full);
        await File.WriteAllBytesAsync(Path.Combine(cache, baseAsset.FileName), Package("1.3.10"));
        var target = Asset("1.3.11", VelopackAssetType.Full);
        var bytes = Package("1.3.11");
        target.Size = bytes.Length;
        target.SHA256 = Convert.ToHexString(SHA256.HashData(bytes));
        target.SHA1 = Convert.ToHexString(SHA1.HashData(bytes));
        source.Payload = bytes;
        var locator = new Locator(root, cache, helper, baseAsset, process);
        var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = "win-x64" }, locator);
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("network forbidden")));
        var adapter = new VelopackUpdateService(manager, new VelopackHttpDownloader(http), "win-x64", locator: locator);

        source.Assets = [baseAsset, Asset("1.3.11", VelopackAssetType.Delta), target];
        var delta = await adapter.CheckAsync();
        check(delta is { IsDelta: true } && source.RequestedChannel == "win-x64",
            "real Velopack SDK selects a delta chain using the cached installed base and exact channel");
        var oversized = Asset("1.3.11", VelopackAssetType.Delta) with { Size = target.Size + 1 };
        source.Assets = [baseAsset, oversized, target];
        check(await adapter.CheckAsync() is { IsDelta: false } large && large.DownloadSize == target.Size,
            "descriptor follows SDK full fallback when deltas cost more than the full package");

        var corruptCache = Path.Combine(folder, "sdk-corrupt-cache");
        Directory.CreateDirectory(corruptCache);
        await File.WriteAllTextAsync(Path.Combine(corruptCache, baseAsset.FileName), "not a zip");
        var corruptLocator = new Locator(root, corruptCache, helper, null, process);
        var corruptManager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = "win-x64" }, corruptLocator);
        var corruptAdapter = new VelopackUpdateService(corruptManager, new VelopackHttpDownloader(http),
            "win-x64", locator: corruptLocator);
        source.Assets = [baseAsset, Asset("1.3.11", VelopackAssetType.Delta), target];
        check(await corruptAdapter.CheckAsync() is { IsDelta: false },
            "real SDK ignores a malformed cached base and chooses a full package without a valid seed");
        source.Assets = [target];
        var update = await adapter.CheckAsync();
        check(update is { IsDelta: false }, "real SDK selects the full package when a delta is unavailable");
        check(await CaptureAsync(() => adapter.ApplyAndRestartAsync(update!, true)) is InvalidOperationException &&
              process.Starts == 0, "SDK adapter cannot hand off an unchecked or undownloaded package");
        await adapter.DownloadAsync(update!, null);
        check(source.Downloads == 1 && File.Exists(Path.Combine(cache, target.FileName)),
            "real SDK verifies and stages a full package without applying or launching anything");
        check(await adapter.ApplyAndRestartAsync(update!, true) && process.Starts == 1 && process.Path == helper &&
              process.Args.Contains("apply") && process.Args.Contains("--waitPid") &&
              process.Args.Contains("4321") && process.Args.TakeLast(2).SequenceEqual(new[] { "--", "--minimized" }) &&
              !process.Args.Contains("--silent") && !process.Exited,
            "real SDK helper handoff preserves PID wait, visible apply progress and minimized restart without exiting WPF");

        var retry = await adapter.CheckAsync();
        await File.WriteAllTextAsync(Path.Combine(cache, target.FileName), "corrupt completed cache file");
        await adapter.DownloadAsync(retry!, null);
        check(source.Downloads == 2 && File.ReadAllBytes(Path.Combine(cache, target.FileName)).SequenceEqual(bytes),
            "a corrupt cold target is discarded and redownloaded before SDK can skip its checksum check");
        await File.WriteAllTextAsync(Path.Combine(cache, target.FileName), "tampered after successful preparation");
        check(await CaptureAsync(() => adapter.ApplyAndRestartAsync(retry!, true)) is InvalidDataException && process.Starts == 1,
            "package changes after preparation invalidate the in-memory receipt before native handoff");

        source.Assets = [target with { FileName = "ToastifyReloaded-1.3.11-win-arm64-full.nupkg" }];
        check(await CaptureAsync(() => adapter.CheckAsync()) is InvalidDataException,
            "SDK adapter rejects a package for the wrong CPU even if metadata names the same application");
        source.Assets = [target with { PackageId = "OtherApplication" }];
        check(await CaptureAsync(() => adapter.CheckAsync()) is InvalidDataException,
            "SDK adapter rejects a foreign application package before download");
        source.Assets = [target with { FileName = "../ToastifyReloaded-1.3.11-win-x64-full.nupkg" }];
        check(await CaptureAsync(() => adapter.CheckAsync()) is InvalidDataException,
            "SDK adapter rejects package paths that escape its cache");
        source.Assets = [target with { Version = SemanticVersion.Parse("1.3.12-beta.1") }];
        check(await CaptureAsync(() => adapter.CheckAsync()) is InvalidDataException,
            "stable SDK adapter rejects a prerelease target");
    }

    private static async Task RunGithubSourceTestsAsync(Action<bool, string> check)
    {
        var requestPaths = new List<string>();
        var feed = "{\"Assets\":[{\"PackageId\":\"ToastifyReloaded\",\"Version\":\"1.3.11\",\"Type\":\"Full\",\"FileName\":\"ToastifyReloaded-1.3.11-win-x64-full.nupkg\",\"Size\":123,\"SHA1\":\"" + new string('A', 40) + "\",\"SHA256\":\"" + new string('A', 64) + "\"}]}";
        string Release(string tag, bool prerelease = false, bool draft = false) =>
            "{\"tag_name\":\"" + tag + "\",\"name\":\"" + tag + "\",\"draft\":" + draft.ToString().ToLowerInvariant() +
            ",\"prerelease\":" + prerelease.ToString().ToLowerInvariant() +
            ",\"assets\":[{\"name\":\"releases.win-x64.json\",\"browser_download_url\":\"https://github.com/test/" + tag + "/feed\"}]}";
        var releases = Enumerable.Range(1, 12).Select(i => Release($"v9.0.{i}-linux"))
            .Append(Release("v9.0.0-macos-rc.2", prerelease: true)).Append(Release("v9.0.0", draft: true))
            .Append(Release("v1.3.11"));
        using var client = new HttpClient(new Handler(request =>
        {
            requestPaths.Add(request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.Host == "api.github.com"
                ? "[" + string.Join(",", releases) + "]" : feed) };
        }));
        var downloader = new VelopackHttpDownloader(client);
        var source = new ToastifyGithubSource("win-x64", downloader);
        var response = await source.GetReleaseFeed(new NullVelopackLogger(), "ToastifyReloaded", "win-x64");
        check(response.Assets.Length == 1 && response.Assets[0].Version.ToFullString() == "1.3.11" &&
              requestPaths.Count == 2 && requestPaths[0].Contains("per_page=100") &&
              requestPaths[1].Contains("v1.3.11/feed"),
            "GitHub source searches 100 releases and ignores newer Linux, macOS, and draft entries before reading the Windows feed");
    }

    private static async Task RunSdkDeltaTestsAsync(string folder, Action<bool, string> check)
    {
        foreach (var mode in new[] { "success", "corrupt", "cancel" })
        {
            var root = Path.Combine(folder, "delta-" + mode);
            var cache = Path.Combine(root, "packages");
            Directory.CreateDirectory(cache);
            var helper = Path.Combine(root, "Update.exe");
            await File.WriteAllTextAsync(helper, "injected patch runner; never executed");
            var baseAsset = Asset("1.3.10", VelopackAssetType.Full);
            var target = Asset("1.3.11", VelopackAssetType.Full);
            var delta = Asset("1.3.11", VelopackAssetType.Delta);
            var fullPayload = Package("1.3.11");
            var deltaPayload = Encoding.UTF8.GetBytes("synthetic patch data");
            target = target with { Size = fullPayload.Length, SHA256 = Convert.ToHexString(SHA256.HashData(fullPayload)),
                SHA1 = Convert.ToHexString(SHA1.HashData(fullPayload)) };
            delta = delta with { Size = deltaPayload.Length, SHA256 = Convert.ToHexString(SHA256.HashData(deltaPayload)),
                SHA1 = Convert.ToHexString(SHA1.HashData(deltaPayload)) };
            await File.WriteAllBytesAsync(Path.Combine(cache, baseAsset.FileName), Package("1.3.10"));
            var source = new Source { Assets = [baseAsset, delta, target], Payload = fullPayload };
            source.Payloads[delta.FileName] = deltaPayload;
            var process = new CapturingProcess();
            var locator = new Locator(root, cache, helper, baseAsset, process);
            using var canceled = new CancellationTokenSource();
            var patchCalls = 0;
            string[] patchArguments = [];
            var manager = new ToastifyUpdateManager(source, new UpdateOptions { ExplicitChannel = "win-x64" }, locator,
                async (file, arguments, token) =>
                {
                    patchCalls++;
                    patchArguments = arguments.ToArray();
                    if (mode == "cancel")
                    {
                        canceled.Cancel();
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    if (mode == "corrupt")
                        return new PowerShellCommandResult(1, "", "synthetic native per-file checksum rejection");
                    var output = arguments[arguments.ToList().IndexOf("--output") + 1];
                    await File.WriteAllBytesAsync(output, fullPayload, token);
                    return new PowerShellCommandResult(0, "", "");
                });
            using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("network forbidden")));
            var adapter = new VelopackUpdateService(manager, new VelopackHttpDownloader(http), "win-x64", locator: locator);
            var update = await adapter.CheckAsync();
            var error = await CaptureAsync(() => adapter.DownloadAsync(update!, null, canceled.Token));
            if (mode == "cancel")
                check(error is OperationCanceledException && patchCalls == 1 && process.Starts == 0 &&
                      !Directory.EnumerateFiles(cache, "*.partial").Any(),
                    "cancellation reaches the native delta runner and cleans partial output without an updater handoff");
            else
                check(error is null && patchCalls == 1 && source.Downloads == (mode == "corrupt" ? 2 : 1) &&
                      File.ReadAllBytes(Path.Combine(cache, target.FileName)).SequenceEqual(fullPayload) &&
                      patchArguments[0] == "patch" && patchArguments.Contains("--old") && patchArguments.Contains("--delta") &&
                      !patchArguments.Contains("apply") && process.Starts == 0,
                    mode == "corrupt" ? "native per-file delta verification failure makes SDK fall back to a verified full package" :
                        "SDK delta preparation uses only the patch command and validates the final package before marking it ready");
        }
    }

    private static async Task RunNativeDeltaTestsAsync(string folder, Action<bool, string> check)
    {
        var fixtures = Environment.GetEnvironmentVariable("TOASTIFY_VELOPACK_FIXTURES");
        var helper = Environment.GetEnvironmentVariable("TOASTIFY_VELOPACK_PATCH_HELPER");
        if (string.IsNullOrEmpty(fixtures) || string.IsNullOrEmpty(helper))
        {
            Console.WriteLine("SKIP: native Velopack patch probe (provide isolated fixtures and pinned helper paths)");
            return;
        }
        if (!Directory.Exists(fixtures) || !File.Exists(helper))
            throw new InvalidOperationException("Native patch fixture/helper path is missing.");
        foreach (var corrupt in new[] { false, true })
        {
            var root = Path.Combine(folder, corrupt ? "native-corrupt-delta" : "native-good-delta");
            var cache = Path.Combine(root, "packages");
            Directory.CreateDirectory(cache);
            var basePath = Path.Combine(fixtures, "ToastifyReloaded-1.3.10-win-x64-full.nupkg");
            var deltaPath = Path.Combine(fixtures, "ToastifyReloaded-1.3.11-win-x64-delta.nupkg");
            var targetPath = Path.Combine(fixtures, "ToastifyReloaded-1.3.11-win-x64-full.nupkg");
            var baseAsset = await VelopackAsset.FromNupkgGenerateChecksumAsync(basePath);
            var delta = await VelopackAsset.FromNupkgGenerateChecksumAsync(deltaPath);
            var target = await VelopackAsset.FromNupkgGenerateChecksumAsync(targetPath);
            File.Copy(basePath, Path.Combine(cache, baseAsset.FileName));
            var deltaBytes = await File.ReadAllBytesAsync(deltaPath);
            if (corrupt)
            {
                deltaBytes = deltaBytes[..100];
                // The envelope checksum is valid, but native patch must reject
                // the damaged ZIP content and the SDK must fall back to full.
                delta = delta with { Size = deltaBytes.Length,
                    SHA256 = Convert.ToHexString(SHA256.HashData(deltaBytes)),
                    SHA1 = Convert.ToHexString(SHA1.HashData(deltaBytes)) };
            }
            var source = new Source { Assets = [baseAsset, delta, target], Payload = await File.ReadAllBytesAsync(targetPath) };
            source.Payloads[delta.FileName] = deltaBytes;
            var process = new CapturingProcess();
            var locator = new Locator(root, cache, helper, baseAsset, process);
            var manager = new ToastifyUpdateManager(source, new UpdateOptions { ExplicitChannel = "win-x64" }, locator);
            using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("network forbidden")));
            var adapter = new VelopackUpdateService(manager, new VelopackHttpDownloader(http), "win-x64", locator: locator);
            var update = await adapter.CheckAsync();
            check(update is { IsDelta: true }, "native patch fixture selects a real delta strategy");
            await adapter.DownloadAsync(update!, null);
            var staged = Path.Combine(cache, target.FileName);
            check(PackageEntries(staged).OrderBy(x => x.Key).SequenceEqual(PackageEntries(targetPath).OrderBy(x => x.Key)) &&
                  source.Downloads == (corrupt ? 2 : 1) && process.Starts == 0 && !process.Exited &&
                  !Directory.EnumerateFiles(cache, "*.reconstructed.json").Any(),
                corrupt ? "real Update.exe patch rejects corrupt delta content and SDK downloads the full package without applying it" :
                    "real Update.exe reconstructs a matching package through the production SDK adapter with no installer/app launch");
            if (!corrupt)
                check(manager.ReconstructedPackage is { } receipt && receipt.AssetName == target.FileName &&
                      receipt.SHA256 != target.SHA256.ToUpperInvariant(),
                    "native reconstructed ZIP uses an in-memory receipt because equivalent contents have different archive bytes");
        }
    }

    private static Dictionary<string, string> PackageEntries(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            return Convert.ToHexString(SHA256.HashData(stream));
        });
    }

    private static VelopackAsset Asset(string version, VelopackAssetType type) => new()
    {
        PackageId = "ToastifyReloaded", Version = SemanticVersion.Parse(version), Type = type, Size = 10,
        SHA256 = new string('A', 64), SHA1 = new string('A', 40),
        FileName = $"ToastifyReloaded-{version}-win-x64-{(type == VelopackAssetType.Delta ? "delta" : "full")}.nupkg"
    };

    private static byte[] Package(string version)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("ToastifyReloaded.nuspec").Open());
            writer.Write($"<package><metadata><id>ToastifyReloaded</id><version>{version}</version><authors>Tests</authors><description>Isolated fixture</description></metadata></package>");
        }
        return output.ToArray();
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private sealed class InlineProgress(Action<UpdateProgressInfo> report) : IProgress<UpdateProgressInfo>
    { public void Report(UpdateProgressInfo value) => report(value); }

    private sealed class FakeBackend : IVelopackUpdateBackend
    {
        public bool IsInstalled { get; set; } = true;
        public string CurrentVersion => "1.3.10";
        public string Channel => "win-x64";
        public int Checks, Downloads, Applies;
        public bool Minimized, NoUpdate, FailCheck, CancelDownload;
        public Task<VelopackUpdatePackage?> CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Checks++;
            if (FailCheck) throw new HttpRequestException("synthetic metadata failure");
            return Task.FromResult(NoUpdate ? null : new VelopackUpdatePackage("1.3.11",
                "ToastifyReloaded-1.3.11-win-x64-full.nupkg", true, 50, new object()));
        }
        public Task DownloadAsync(VelopackUpdatePackage update, IProgress<UpdateProgressInfo>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Downloads++;
            if (CancelDownload) throw new OperationCanceledException();
            progress?.Report(new(UpdatePhase.Downloading, 50, 100, PercentOverride: 20, IsPackageUpdate: true));
            progress?.Report(new(UpdatePhase.Verifying, IsPackageUpdate: true));
            return Task.CompletedTask;
        }
        public Task<bool> ApplyAndRestartAsync(VelopackUpdatePackage update, bool restartMinimized, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Applies++; Minimized = restartMinimized; return Task.FromResult(true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class StalledHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException("unreachable"); }
    }
    private sealed class Source : IUpdateSource
    {
        public VelopackAsset[] Assets = [];
        public byte[] Payload = [];
        public Dictionary<string, byte[]> Payloads { get; } = new();
        public string? RequestedChannel;
        public int Downloads;
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        { RequestedChannel = channel; return Task.FromResult(new VelopackAssetFeed { Assets = Assets }); }
        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
            Action<int> progress, CancellationToken cancelToken)
        {
            cancelToken.ThrowIfCancellationRequested();
            Downloads++;
            await File.WriteAllBytesAsync(localFile, Payloads.GetValueOrDefault(releaseEntry.FileName, Payload), cancelToken);
            progress(100);
        }
    }
    private sealed class Locator(string root, string packages, string helper, VelopackAsset? localPackage, IProcessImpl process)
        : TestVelopackLocator("ToastifyReloaded", "1.3.10", packages, Path.Combine(root, "current"), root, helper,
            "win-x64", localPackage: localPackage)
    { public override IProcessImpl Process => process; }
    private sealed class CapturingProcess : IProcessImpl
    {
        public int Starts;
        public string? Path;
        public string[] Args = [];
        public bool Exited;
        public string GetCurrentProcessPath() => "isolated-test.exe";
        public uint GetCurrentProcessId() => 4321;
        public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
        { Starts++; Path = exePath; Args = args.ToArray(); }
        public void Exit(int exitCode) => Exited = true;
    }
}

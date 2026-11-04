using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ToastifyReloaded.Shared;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

internal static class CrossMaintenanceTests
{
    public static async Task<bool> TryRunChildAsync(string[] args)
    {
        if (args.Length != 1 || !args[0].StartsWith("--maintenance-fixture-", StringComparison.Ordinal)) return false;
        switch (args[0])
        {
            case "--maintenance-fixture-hang": await Task.Delay(Timeout.Infinite); break;
            case "--maintenance-fixture-output": Console.Write(new string('x', 200_000)); break;
            case "--maintenance-fixture-hold-pipe": await Task.Delay(1800); break;
            case "--maintenance-fixture-spawn-holder":
                var command = ChildCommand("--maintenance-fixture-hold-pipe");
                var start = new ProcessStartInfo { FileName = command.File, UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in command.Args) start.ArgumentList.Add(argument);
                using (Process.Start(start)) { }
                break;
            default: throw new ArgumentException("Unknown isolated maintenance fixture.");
        }
        return true;
    }

    private static (string File, string[] Args) ChildCommand(string mode)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing harness executable.");
        return Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? (executable, new[] { typeof(CrossMaintenanceTests).Assembly.Location, mode })
            : (executable, new[] { mode });
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "cross-maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await TestProcessesAsync(check);
            await TestDownloadsAsync(root, check);
            await TestPortableSdkAsync(root, check);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task TestProcessesAsync(Action<bool, string> check)
    {
        var output = ChildCommand("--maintenance-fixture-output");
        var result = await BoundedProcessRunner.RunAsync(output.File, output.Args, timeout: TimeSpan.FromSeconds(5));
        check(result.ExitCode == 0 && result.StdOut.Length == 64 * 1024,
            "cross-platform process output is drained while captured diagnostics stay bounded");
        var hang = ChildCommand("--maintenance-fixture-hang");
        var clock = Stopwatch.StartNew();
        check(await Capture(() => BoundedProcessRunner.RunAsync(hang.File, hang.Args,
                  timeout: TimeSpan.FromMilliseconds(150))) is TimeoutException && clock.Elapsed < TimeSpan.FromSeconds(4),
            "cross-platform hung helper has a real execution deadline");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        check(await Capture(() => BoundedProcessRunner.RunAsync(hang.File, hang.Args, cancel.Token)) is OperationCanceledException,
            "caller cancellation reaches the isolated cross-platform helper");
        var held = ChildCommand("--maintenance-fixture-spawn-holder");
        clock.Restart();
        check(await Capture(() => BoundedProcessRunner.RunAsync(held.File, held.Args,
                  timeout: TimeSpan.FromSeconds(5))) is TimeoutException && clock.Elapsed < TimeSpan.FromSeconds(4),
            "an exited helper whose descendant retains stdout cannot hang the application");
        await Task.Delay(900); // Let only the short-lived synthetic pipe holder finish before another build.
    }

    private static async Task TestDownloadsAsync(string root, Action<bool, string> check)
    {
        const string tag = "v1.4.2-linux";
        const string name = "ToastifyReloaded-Linux-x64.AppImage";
        var uri = new Uri("https://github.com/MarliusLab/Toastify-Reloaded/releases/download/" + tag + "/" + name);
        var bytes = Encoding.UTF8.GetBytes("isolated verified native package");
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var destination = Path.Combine(root, name);
        var reports = new List<UpdateOperationProgress>();
        using var client = new HttpClient(new Handler(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        await SafeUpdateDownloader.DownloadAsync(client, uri, tag, name, destination, bytes.Length, digest, new Inline(reports.Add));
        check(File.ReadAllBytes(destination).SequenceEqual(bytes) &&
              reports.Any(p => p.Stage == UpdateOperationStage.Downloading && p.ReceivedBytes == bytes.Length && p.Percent == 100) &&
              reports.Any(p => p.Stage == UpdateOperationStage.Verifying) && reports.Last().Stage == UpdateOperationStage.Ready,
            "native package download reports real bytes and separate verification before becoming ready");
        File.Delete(destination);
        using var unknown = new HttpClient(new Handler(() =>
        {
            var content = new StreamContent(new UnknownLengthStream(bytes));
            content.Headers.ContentLength = null;
            return new(HttpStatusCode.OK) { Content = content };
        }));
        reports.Clear();
        await SafeUpdateDownloader.DownloadAsync(unknown, uri, tag, name, destination, bytes.Length, digest, new Inline(reports.Add));
        check(reports.Where(p => p.Stage == UpdateOperationStage.Downloading).All(p => p.TotalBytes is null && p.Percent is null),
            "unknown HTTP length preserves byte progress without inventing a percentage");
        File.Delete(destination);
        check(await Capture(() => SafeUpdateDownloader.DownloadAsync(client, uri, tag, name, destination, bytes.Length, null))
                  is InvalidDataException && !File.Exists(destination), "native updates require a published SHA256 digest");
        check(await Capture(() => SafeUpdateDownloader.DownloadAsync(client, new Uri("https://evil.invalid/" + name),
                  tag, name, destination, bytes.Length, digest)) is InvalidDataException,
            "native updates reject a foreign download host before contacting it");
        check(!SafeUpdateDownloader.IsTrustedAssetUri(uri, "v9.9.9-linux", name) &&
              !SafeUpdateDownloader.IsSafeFileName("../other") && !SafeUpdateDownloader.IsSafeFileName("..\\other"),
            "native package identity binds exact release path and safe basename");
        check(await Capture(() => SafeUpdateDownloader.DownloadAsync(client, uri, tag, name, destination,
                  bytes.Length + 1, digest)) is InvalidDataException && Clean(root, destination),
            "declared-size mismatch rejects a native package and removes partial output");
        check(await Capture(() => SafeUpdateDownloader.DownloadAsync(client, uri, tag, name, destination,
                  bytes.Length, "sha256:" + new string('0', 64))) is InvalidDataException && Clean(root, destination),
            "native package checksum corruption never leaves an installable result");
        using var stalled = new HttpClient(new Handler(() => new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }));
        check(await Capture(() => SafeUpdateDownloader.DownloadAsync(stalled, uri, tag, name, destination, bytes.Length, digest,
                  totalTimeout: TimeSpan.FromSeconds(2), idleTimeout: TimeSpan.FromMilliseconds(60))) is TimeoutException && Clean(root, destination),
            "native download body has an independent idle deadline and partial cleanup");
        check(await Capture(() => SafeUpdateDownloader.DownloadAsync(stalled, uri, tag, name, destination, bytes.Length, digest,
                  totalTimeout: TimeSpan.FromMilliseconds(60), idleTimeout: TimeSpan.FromSeconds(2))) is TimeoutException && Clean(root, destination),
            "native download total deadline still covers a stalled response body");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        check(await Capture(() => SafeUpdateDownloader.DownloadAsync(stalled, uri, tag, name, destination, bytes.Length, digest,
                  cancellationToken: cancellation.Token)) is OperationCanceledException && Clean(root, destination),
            "native body cancellation removes partial files and never begins installation");
    }

    private static bool Clean(string root, string destination) => !File.Exists(destination) &&
        !Directory.EnumerateFiles(root, "*.partial-*").Any();

    private static async Task TestPortableSdkAsync(string folder, Action<bool, string> check)
    {
        foreach (var platform in new[] { "linux", "macos" })
        {
            var channel = PortablePackageUpdateService.GetChannel(platform, System.Runtime.InteropServices.Architecture.X64);
            var id = PortablePackageUpdateService.GetPackageId(platform);
            var current = platform == "linux" ? "1.4.2" : "1.5.0-rc.3";
            var next = platform == "linux" ? "1.4.3" : "1.5.0-rc.4";
            var root = Path.Combine(folder, platform);
            var cache = Path.Combine(root, "packages");
            Directory.CreateDirectory(cache);
            var helper = Path.Combine(root, "never-executed-helper");
            await File.WriteAllTextAsync(helper, "fixture only");
            var source = new Source();
            var process = new CapturingProcess();
            var oldPayload = Package(id, current);
            var old = Asset(id, current, channel, oldPayload);
            await File.WriteAllBytesAsync(Path.Combine(cache, old.FileName), oldPayload);
            var payload = Package(id, next);
            var target = Asset(id, next, channel, payload);
            source.Payload = payload;
            source.Assets = [old, target, target with { Type = VelopackAssetType.Delta,
                FileName = target.FileName.Replace("-full", "-delta"), Size = 10 }];
            var locator = new Locator(id, current, root, cache, helper, channel, old, process);
            var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = channel }, locator);
            using var http = new HttpClient(new Handler(() => throw new InvalidOperationException("Network forbidden")));
            var adapter = new PortablePackageUpdateService(manager, new PortableHttpDownloader(http), channel, id,
                platform == "macos", locator);
            check(await adapter.CheckAsync() is { IsDelta: true } && source.Channel == channel,
                platform + " SDK chooses a delta only from its exact OS/CPU channel and valid cached base");
            source.Assets = [target];
            var update = await adapter.CheckAsync();
            check(update is { IsDelta: false } && update.Version == next,
                platform + " SDK keeps exact semantic versions including macOS release candidates");
            check(await Capture(() => adapter.DownloadAsync(new PortableUpdatePackage(next, target.FileName, false,
                      target.Size, new object()), null)) is InvalidOperationException && source.Downloads == 0,
                platform + " SDK rejects a foreign unchecked target before download");
            await adapter.DownloadAsync(update!, null);
            check(source.Downloads == 1 && process.Starts == 0 && File.ReadAllBytes(Path.Combine(cache, target.FileName)).SequenceEqual(payload),
                platform + " SDK stages and verifies a full package without starting any app or helper");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            check(await Capture(() => adapter.ApplyAndRestartAsync(update!, true, cancelled.Token)) is OperationCanceledException && process.Starts == 0,
                platform + " SDK cancellation prevents a helper handoff");
            await File.WriteAllTextAsync(Path.Combine(cache, target.FileName), "tampered after preparation");
            check(await Capture(() => adapter.ApplyAndRestartAsync(update!, false)) is InvalidDataException && process.Starts == 0,
                platform + " SDK rechecks its in-memory preparation receipt before apply");
            source.Assets = [target with { FileName = target.FileName.Replace("-x64-", "-arm64-") }];
            check(await Capture(() => adapter.CheckAsync()) is InvalidDataException,
                platform + " SDK rejects a package labeled for another processor");
        }
    }

    private static VelopackAsset Asset(string id, string version, string channel, byte[] payload) => new()
    {
        PackageId = id, Version = SemanticVersion.Parse(version), Type = VelopackAssetType.Full,
        FileName = id + "-" + version + "-" + channel + "-full.nupkg", Size = payload.Length,
        SHA1 = Convert.ToHexString(SHA1.HashData(payload)), SHA256 = Convert.ToHexString(SHA256.HashData(payload))
    };

    private static byte[] Package(string id, string version)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry(id + ".nuspec").Open()))
            writer.Write($"<package><metadata><id>{id}</id><version>{version}</version><authors>Tests</authors><description>Isolated fixture</description></metadata></package>");
        return output.ToArray();
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }
    private sealed class Inline(Action<UpdateOperationProgress> report) : IProgress<UpdateOperationProgress>
    { public void Report(UpdateOperationProgress value) => report(value); }
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(response()); }
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.Infinite, token); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class UnknownLengthStream(byte[] payload) : MemoryStream(payload)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
    private sealed class Source : IUpdateSource
    {
        public VelopackAsset[] Assets = []; public byte[] Payload = []; public string? Channel; public int Downloads;
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        { Channel = channel; return Task.FromResult(new VelopackAssetFeed { Assets = Assets }); }
        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset asset, string destination,
            Action<int> progress, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Downloads++; await File.WriteAllBytesAsync(destination, Payload, token); progress(100); }
    }
    private sealed class Locator(string id, string version, string root, string cache, string helper, string channel,
        VelopackAsset local, IProcessImpl process)
        : TestVelopackLocator(id, version, cache, Path.Combine(root, "current"), root, helper, channel, localPackage: local)
    { public override IProcessImpl Process => process; }
    private sealed class CapturingProcess : IProcessImpl
    {
        public int Starts;
        public string GetCurrentProcessPath() => "isolated-maintenance-fixture";
        public uint GetCurrentProcessId() => 4321;
        public void StartProcess(string executable, IEnumerable<string> args, string workDir, bool showWindow) => Starts++;
        public void Exit(int exitCode) => throw new InvalidOperationException("Tests must not exit their host.");
    }
}

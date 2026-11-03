using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using ToastifyReloaded.Models;
using ToastifyReloaded.Services;

namespace ToastifyReloaded.Windows.Tests;

public static class UpdateTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var expectedAsset = "ToastifyReloaded-Setup-" +
            (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64") + ".exe";
        var update = new UpdateCheckResult(true, true, "1.3.5", "1.3.6", "v1.3.6", expectedAsset,
            "https://example.invalid/setup.exe", "https://example.invalid/release", "update available");
        var payload = ExecutableFixture();

        async Task RunCase(string name, Func<string, Task> body)
        {
            var folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            try { await body(folder); }
            catch (Exception ex) { check(false, name + ": " + ex); }
        }

        try
        {
            await RunCase("release-selection", async folder =>
            {
                var json = "[" +
                    ReleaseJson("v9.0.0-linux", expectedAsset) + "," +
                    ReleaseJson("v9.0.0-macos", expectedAsset) + "," +
                    ReleaseJson("v9.0.0", expectedAsset, prerelease: true) + "," +
                    ReleaseJson("v8.0.0", expectedAsset, draft: true) + "," +
                    ReleaseJson("v7.0.0", "wrong-architecture.exe") + "," +
                    ReleaseJson("v.1.3.5", expectedAsset) + "," +
                    ReleaseJson("v1.3.6", expectedAsset) + "]";
                using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
                    { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
                var service = new UpdateService(client, tempDirectory: folder, currentVersion: "1.3.5.0");
                var result = await service.CheckLatestAsync();
                check(result.UpdateAvailable && result.LatestVersion == "1.3.6" &&
                      result.CurrentVersion == "1.3.5" && result.AssetName == expectedAsset,
                    "updater selects newer stable Windows asset and preserves architecture/tag normalization");
            });

            await RunCase("known-length", async folder =>
            {
                var reports = new List<UpdateProgressInfo>();
                ProcessStartInfo? launch = null;
                var closedFile = false;
                var offThread = false;
                using var client = DownloadClient(payload, knownLength: true, delay: 45, chunk: 1024);
                var service = new UpdateService(client, info =>
                {
                    launch = info;
                    offThread = Thread.CurrentThread.IsThreadPoolThread;
                    using var exclusive = File.Open(info.FileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    closedFile = exclusive.Length == payload.Length;
                    return Task.FromResult(true);
                }, folder, "1.3.5");
                var launched = await service.PrepareAndLaunchUpdateAsync(update, 123,
                    restartMinimized: true, progress: new InlineProgress(reports.Add));
                var download = reports.Where(x => x.Phase == UpdatePhase.Downloading).ToArray();
                check(launched && closedFile && offThread && launch is not null && launch.UseShellExecute &&
                      launch.Verb == "runas" && launch.Arguments == "/UPDATEPID=123 /RESTARTMINIMIZED=1",
                    "installer handoff is off UI thread, file closed, with visible NSIS progress and restart flag");
                check(download.Length >= 3 && download[0].BytesReceived == 0 &&
                      download.Any(x => x.BytesReceived > 0 && x.BytesReceived < payload.Length) &&
                      download[^1].BytesReceived == payload.Length && download[^1].Percent == 100 &&
                      download.All(x => x.TotalBytes == payload.Length),
                    "download progress uses actual intermediate bytes and known Content-Length");
                check(reports.Select(x => x.Phase).Distinct().SequenceEqual(new[]
                      { UpdatePhase.Downloading, UpdatePhase.Verifying, UpdatePhase.WaitingForPermission,
                        UpdatePhase.LaunchingInstaller }) &&
                      reports.Where(x => x.Phase != UpdatePhase.Downloading).All(x => x.Percent is null) &&
                      reports.Where(x => !x.CanCancel).All(x => x.Phase is UpdatePhase.WaitingForPermission or UpdatePhase.LaunchingInstaller),
                    "handoff phases never pretend to measure installation percent and disable cancellation at UAC");
            });

            await RunCase("metadata-body-timeout", async folder =>
            {
                using var client = DownloadClient(Encoding.UTF8.GetBytes("[]"), knownLength: true, delay: 10000);
                var service = new UpdateService(client, tempDirectory: folder,
                    checkTimeout: TimeSpan.FromMilliseconds(80));
                var result = await service.CheckLatestAsync();
                check(!result.Success && result.Message.Contains("timeout"),
                    "release metadata body is bounded after response headers arrive");
            });

            await RunCase("unknown-length", async folder =>
            {
                var reports = new List<UpdateProgressInfo>();
                using var client = DownloadClient(payload, knownLength: false);
                var service = new UpdateService(client, _ => Task.FromResult(true), folder);
                check(await service.PrepareAndLaunchUpdateAsync(update, 123,
                          progress: new InlineProgress(reports.Add)) &&
                      reports.Where(x => x.Phase == UpdatePhase.Downloading).All(x => x.Percent is null && x.TotalBytes is null) &&
                      reports.Any(x => x.BytesReceived == payload.Length),
                    "unknown Content-Length reports bytes with indeterminate percentage");
            });

            await RunCase("idle-timeout", async folder =>
            {
                var launched = false;
                using var client = DownloadClient(payload, knownLength: true, delay: 10000);
                var service = new UpdateService(client, _ => { launched = true; return Task.FromResult(true); },
                    folder, totalTimeout: TimeSpan.FromSeconds(2), idleTimeout: TimeSpan.FromMilliseconds(70));
                var error = await CaptureAsync(() => service.PrepareAndLaunchUpdateAsync(update, 123));
                check(error is TimeoutException && error.Message.Contains("server") && !launched && Empty(folder),
                    "stalled response body hits idle timeout and removes partial installer");
            });

            await RunCase("total-timeout", async folder =>
            {
                var launched = false;
                using var client = DownloadClient(payload, knownLength: false, delay: 35, chunk: 16);
                var service = new UpdateService(client, _ => { launched = true; return Task.FromResult(true); },
                    folder, totalTimeout: TimeSpan.FromMilliseconds(130), idleTimeout: TimeSpan.FromSeconds(1));
                var error = await CaptureAsync(() => service.PrepareAndLaunchUpdateAsync(update, 123));
                check(error is TimeoutException && error.Message.Contains("massimo") && !launched && Empty(folder),
                    "continuous slow data cannot bypass the total download timeout");
            });

            await RunCase("download-cancel", async folder =>
            {
                var launched = false;
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
                using var client = DownloadClient(payload, knownLength: true, delay: 10000);
                var service = new UpdateService(client, _ => { launched = true; return Task.FromResult(true); }, folder);
                var error = await CaptureAsync(() => service.PrepareAndLaunchUpdateAsync(update, 123,
                    cancellationToken: cancel.Token));
                check(error is OperationCanceledException && error is not UpdatePermissionCanceledException &&
                      !launched && Empty(folder),
                    "user may cancel a pending download without launching or leaving partial files");
            });

            await RunCase("verify-cancel", async folder =>
            {
                var launched = false;
                using var cancel = new CancellationTokenSource();
                using var client = DownloadClient(payload, knownLength: true);
                var service = new UpdateService(client, _ => { launched = true; return Task.FromResult(true); }, folder);
                var error = await CaptureAsync(() => service.PrepareAndLaunchUpdateAsync(update, 123,
                    cancellationToken: cancel.Token, progress: new InlineProgress(p =>
                    { if (p.Phase == UpdatePhase.Verifying) cancel.Cancel(); })));
                check(error is OperationCanceledException && !launched && Empty(folder),
                    "cancellation during verification stops before the external handoff");
            });

            await RunCase("truncated-body", async folder =>
            {
                var launched = false;
                using var client = DownloadClient(payload, knownLength: true, declaredLength: payload.Length + 10);
                var service = new UpdateService(client, _ => { launched = true; return Task.FromResult(true); }, folder);
                var error = await CaptureAsync(() => service.PrepareAndLaunchUpdateAsync(update, 123));
                check(error is InvalidDataException && !launched && Empty(folder),
                    "truncated installer is rejected and cleaned instead of reaching UAC");
            });

            await RunCase("invalid-executable", async folder =>
            {
                var launched = false;
                using var client = DownloadClient(Encoding.UTF8.GetBytes("MZ<html>not an executable</html>"), knownLength: true);
                var service = new UpdateService(client, _ => { launched = true; return Task.FromResult(true); }, folder);
                var error = await CaptureAsync(() => service.PrepareAndLaunchUpdateAsync(update, 123));
                check(error is InvalidDataException && !launched && Empty(folder),
                    "MZ prefix alone is insufficient executable validation");
            });

            await RunCase("http-error", async folder =>
            {
                using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound)));
                var service = new UpdateService(client, _ => throw new Exception("must not launch"), folder);
                var error = await CaptureAsync(() => service.PrepareAndLaunchUpdateAsync(update, 123));
                check(error is HttpRequestException && Empty(folder), "HTTP failure cleans the download directory");
            });

            await RunCase("uac-rejected", async folder =>
            {
                using var client = DownloadClient(payload, knownLength: true);
                var service = new UpdateService(client, _ => throw new Win32Exception(1223), folder);
                var error = await CaptureAsync(() => service.PrepareAndLaunchUpdateAsync(update, 123));
                check(error is UpdatePermissionCanceledException && Empty(folder),
                    "UAC refusal is distinguishable from download cancellation and leaves no installer");
            });

            await RunCase("launch-failed", async folder =>
            {
                using var client = DownloadClient(payload, knownLength: true);
                var service = new UpdateService(client, _ => Task.FromResult(false), folder);
                check(!await service.PrepareAndLaunchUpdateAsync(update, 123) && Empty(folder),
                    "a failed installer launch never reports success and removes its files");
            });

            await RunCase("pending-handoff", async folder =>
            {
                using var client = DownloadClient(payload, knownLength: true);
                using var cancel = new CancellationTokenSource();
                var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var service = new UpdateService(client, _ => decision.Task, folder);
                var task = service.PrepareAndLaunchUpdateAsync(update, 123, cancellationToken: cancel.Token,
                    progress: new InlineProgress(p =>
                    {
                        if (p.Phase == UpdatePhase.WaitingForPermission)
                        {
                            cancel.Cancel();
                            waiting.TrySetResult(true);
                        }
                    }));
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
                check(!task.IsCompleted, "pending UAC handoff is not abandoned when cancellation arrives too late");
                decision.SetResult(true);
                check(await task, "installer handoff returns only after the launcher outcome is known");
            });

            await RunCase("completed-handoff-observer-error", async folder =>
            {
                using var client = DownloadClient(payload, knownLength: true);
                string? installer = null;
                var service = new UpdateService(client, info =>
                { installer = info.FileName; return Task.FromResult(true); }, folder);
                var result = await service.PrepareAndLaunchUpdateAsync(update, 123,
                    progress: new InlineProgress(p =>
                    { if (p.Phase == UpdatePhase.LaunchingInstaller) throw new InvalidOperationException("UI closed"); }));
                check(result && File.Exists(installer),
                    "a progress observer failure cannot undo a completed installer handoff or delete its executable");
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static bool Empty(string folder) => !Directory.EnumerateFileSystemEntries(folder).Any();

    private static async Task<Exception?> CaptureAsync(Func<Task<bool>> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static string ReleaseJson(string tag, string asset, bool draft = false, bool prerelease = false)
        => $$"""{"tag_name":"{{tag}}","draft":{{draft.ToString().ToLowerInvariant()}},"prerelease":{{prerelease.ToString().ToLowerInvariant()}},"assets":[{"name":"{{asset}}","browser_download_url":"https://example.invalid/setup.exe"}]}""";

    private static byte[] ExecutableFixture()
    {
        var bytes = new byte[4096];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        bytes[0x3c] = 0x80;
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
        return bytes;
    }

    private static HttpClient DownloadClient(byte[] bytes, bool knownLength, int delay = 0,
        int chunk = 65536, long? declaredLength = null)
        => new(new Handler(_ =>
        {
            var content = new StreamContent(new ChunkStream(bytes, delay, chunk));
            if (knownLength) content.Headers.ContentLength = declaredLength ?? bytes.Length;
            return new(HttpStatusCode.OK) { Content = content };
        }));

    private sealed class InlineProgress(Action<UpdateProgressInfo> action) : IProgress<UpdateProgressInfo>
    {
        public void Report(UpdateProgressInfo value) => action(value);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }

    private sealed class ChunkStream(byte[] bytes, int delay, int chunk) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (delay > 0) await Task.Delay(delay, cancellationToken);
            var read = Math.Min(Math.Min(buffer.Length, chunk), bytes.Length - _position);
            bytes.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            return read;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

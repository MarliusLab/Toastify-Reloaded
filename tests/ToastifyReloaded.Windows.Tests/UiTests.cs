using System.IO;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToastifyReloaded.Models;
using ToastifyReloaded.Services;

namespace ToastifyReloaded.Windows.Tests;

public static class UiTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var window = new MainWindow(maintenanceTestMode: true);
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = -10000;
        window.ConfigureMaintenancePreview("Italian", "Dark");
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        try
        {
            await window.ProbeMaintenancePreviewAsync();
            var spotifyVersion = (TextBlock)window.FindName("SpotifyVersionText");
            var spicetifyVersion = (TextBlock)window.FindName("SpicetifyVersionText");
            check(spotifyVersion.Text != "…" && spicetifyVersion.Text != "…" &&
                  !spotifyVersion.Text.StartsWith("Rilevamento") && !spicetifyVersion.Text.StartsWith("Rilevamento"),
                "read-only native detection completes both visible version fields with isolated settings");
            var bar = (ProgressBar)window.FindName("UpdateProgressBar");
            var panel = (FrameworkElement)window.FindName("UpdateProgressPanel");
            var cancel = (Button)window.FindName("CancelUpdateButton");
            var status = (TextBlock)window.FindName("UpdateStatusText");
            var group = (FrameworkElement)window.FindName("CompatibilityGroupBox");
            window.ReportMaintenanceProgress(new(UpdatePhase.Downloading, 24 * 1024 * 1024, 40 * 1024 * 1024));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            check(panel.IsVisible && !bar.IsIndeterminate && bar.Value == 60 && cancel.IsEnabled,
                "real WPF maintenance panel displays determinate download progress and cancel action");
            check(status.Text.Contains("60%") && status.Text.Contains("MB") && window.CanCancelUpdate,
                "Italian progress includes received bytes, total size and actual percentage");
            check(bar.ActualHeight >= 10 && bar.ActualWidth > 150 && bar.TransformToAncestor(window).Transform(new Point(0, bar.ActualHeight)).Y < window.ActualHeight,
                "progress bar fits within the fixed settings window");

            var folder = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (folder.Parent is not null && !Directory.Exists(Path.Combine(folder.FullName, "installer"))) folder = folder.Parent;
            var output = Path.Combine(folder.FullName, "artifacts", "windows-maintenance-progress.png");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var render = new RenderTargetBitmap((int)Math.Ceiling(group.ActualWidth), (int)Math.Ceiling(group.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
                drawing.DrawRectangle(new VisualBrush(group) { Stretch = Stretch.Fill }, null,
                    new Rect(0, 0, group.ActualWidth, group.ActualHeight));
            render.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(render));
            using (var stream = File.Create(output)) encoder.Save(stream);
            Console.WriteLine("UI preview (simulated download): " + output);

            window.ReportMaintenanceProgress(new(UpdatePhase.Downloading, 8192));
            check(bar.IsIndeterminate && !status.Text.Contains('%'),
                "unknown download size uses indeterminate progress without a fabricated percentage");
            var ticks = 0;
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Normal, (_, _) => ticks++, Dispatcher.CurrentDispatcher);
            timer.Start();
            await Task.Delay(80);
            timer.Stop();
            check(ticks > 1, "WPF dispatcher remains responsive while maintenance progress is displayed");

            window.ReportMaintenanceProgress(new(UpdatePhase.Verifying));
            check(bar.IsIndeterminate && status.Text.Contains("Verifica") && cancel.IsEnabled,
                "verification has its own cancellable, indeterminate stage");
            window.ReportMaintenanceProgress(new(UpdatePhase.WaitingForPermission));
            check(!window.CanCancelUpdate && cancel.Visibility == Visibility.Collapsed && status.Text.Contains("Conferma") && bar.IsIndeterminate,
                "Windows permission handoff explains the required action and cannot be canceled as a download");
            window.ReportMaintenanceProgress(new(UpdatePhase.LaunchingInstaller));
            check(status.Text.Contains("finestra dell'installer") && !window.CanCancelUpdate,
                "installer progress is delegated truthfully to the visible native installation window");
        }
        finally { window.Close(); }

        foreach (var scenario in new[] { "cancel", "timeout", "uac" })
        {
            using var handler = new UiDownloadHandler(scenario);
            using var client = new HttpClient(handler);
            var directory = Path.Combine(Path.GetTempPath(), "Toastify-ui-updater-" + Guid.NewGuid().ToString("N"));
            var launched = false;
            var updater = new UpdateService(client, _ =>
            {
                launched = true;
                throw new Win32Exception(1223);
            }, directory, "1.3.7", totalTimeout: TimeSpan.FromMilliseconds(scenario == "timeout" ? 100 : 5000));
            var test = new MainWindow(true, updater) { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            test.ConfigureMaintenancePreview("Italian", "Dark");
            test.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            try
            {
                var task = test.RunUpdatePreviewAsync();
                await handler.DownloadRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (scenario == "cancel")
                {
                    var cancel = (Button)test.FindName("CancelUpdateButton");
                    check(cancel.IsVisible && cancel.IsEnabled && ((ProgressBar)test.FindName("UpdateProgressBar")).IsIndeterminate,
                        "download can be canceled even while waiting for response headers");
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                check(!await task.WaitAsync(TimeSpan.FromSeconds(5)), "UI update failure/cancellation returns without closing the application: " + scenario);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var result = ((TextBlock)test.FindName("UpdateStatusText")).Text;
                check(((Button)test.FindName("InstallUpdateButton")).IsEnabled &&
                      ((Button)test.FindName("CheckCompatibilityButton")).IsEnabled &&
                      !((ProgressBar)test.FindName("UpdateProgressBar")).IsIndeterminate && !test.CanCancelUpdate,
                    "maintenance buttons recover and spinner stops after " + scenario);
                check(result.Contains(scenario == "uac" ? "richiesta di Windows" : scenario == "timeout" ? "tempo massimo" : "annullato"),
                    $"UI explains the actual completed error/cancellation stage: {scenario}; launcher reached={launched}; actual status=[{result}]");
                check(launched == (scenario == "uac") && (!Directory.Exists(directory) || !Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any()),
                    "failed UI update removes temporary installers and never launches after download cancellation: " + scenario);
            }
            finally
            {
                test.Close();
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }

        using (var handler = new UiDownloadHandler("uac"))
        using (var client = new HttpClient(handler))
        {
            var permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var directory = Path.Combine(Path.GetTempPath(), "Toastify-ui-pending-uac-" + Guid.NewGuid().ToString("N"));
            var updater = new UpdateService(client, _ => { requested.TrySetResult(); return permission.Task; }, directory, "1.3.7");
            var pending = new MainWindow(true, updater) { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            pending.ConfigureMaintenancePreview("Italian", "Dark");
            pending.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            try
            {
                var task = pending.RunUpdatePreviewAsync();
                await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                pending.RequestMaintenancePreviewExit();
                check(pending.IsVisible && !task.IsCompleted,
                    "tray exit is deferred while the Windows elevation handoff is pending");
                permission.SetException(new Win32Exception(1223));
                check(!await task.WaitAsync(TimeSpan.FromSeconds(5)) && !pending.IsVisible,
                    "deferred exit completes only after the Windows permission outcome is known");
            }
            finally
            {
                permission.TrySetException(new Win32Exception(1223));
                if (pending.IsVisible) pending.Close();
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class UiDownloadHandler(string scenario) : HttpMessageHandler
    {
        public TaskCompletionSource DownloadRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
                var json = "[{\"tag_name\":\"v1.3.8\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"ToastifyReloaded-Setup-win-" + arch + ".exe\",\"browser_download_url\":\"https://example.invalid/setup.exe\"}]}]";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            DownloadRequested.TrySetResult();
            if (scenario != "uac") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var bytes = new byte[256]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; bytes[0x3c] = 0x80; bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}

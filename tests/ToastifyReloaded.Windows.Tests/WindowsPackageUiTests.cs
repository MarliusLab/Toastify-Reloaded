using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ToastifyReloaded.Models;
using ToastifyReloaded.Services;

namespace ToastifyReloaded.Windows.Tests;

internal static class WindowsPackageUiTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var cancel in new[] { false, true })
        {
            var backend = new PackageBackend();
            var window = new MainWindow(true, windowsUpdateService: new WindowsUpdateService(backend: backend))
            { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, Opacity = 0 };
            window.ConfigureMaintenancePreview("Italian", "Dark");
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            try
            {
                var run = window.RunUpdatePreviewAsync();
                await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var bar = (ProgressBar)window.FindName("UpdateProgressBar");
                var status = (TextBlock)window.FindName("UpdateStatusText");
                check(backend.Checks == 1 && backend.Downloads == 1 && bar.Value == 37 && !bar.IsIndeterminate,
                    "WPF package update accepts an SDK target without a legacy installer URL and shows SDK progress");
                check(status.Text.Contains("preparazione") && status.Text.Contains("37%") && !status.Text.Contains("MB"),
                    "package progress labels download/preparation without inventing byte totals");
                check(window.CanCancelUpdate, "Velopack package preparation remains cancellable in the real WPF panel");
                if (cancel)
                    ((Button)window.FindName("CancelUpdateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else
                    backend.Release.TrySetResult();
                check(!await run.WaitAsync(TimeSpan.FromSeconds(3)) && window.IsVisible,
                    "failed or canceled package handoff leaves Toastify open for retry");
                check(backend.Applies == (cancel ? 0 : 1), "canceled package preparation never launches the update helper");
                check(((Button)window.FindName("InstallUpdateButton")).IsEnabled && !window.CanCancelUpdate,
                    "package update completion restores maintenance buttons and cancellation state");
            }
            finally { backend.Release.TrySetResult(); window.Close(); }
        }
        check(new UpdateProgressInfo(UpdatePhase.Downloading, PercentOverride: double.NaN, IsPackageUpdate: true).Percent is null &&
              new UpdateProgressInfo(UpdatePhase.Downloading, PercentOverride: 200, IsPackageUpdate: true).Percent == 100,
            "SDK progress rejects non-finite values and bounds percentages");
    }

    private sealed class PackageBackend : IVelopackUpdateBackend
    {
        public bool IsInstalled => true;
        public string CurrentVersion => "1.3.10";
        public string Channel => "win-x64";
        public int Checks, Downloads, Applies;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<VelopackUpdatePackage?> CheckAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            return Task.FromResult<VelopackUpdatePackage?>(new("1.3.11", "ToastifyReloaded-1.3.11-win-x64-full.nupkg",
                true, 4096, new object()));
        }
        public async Task DownloadAsync(VelopackUpdatePackage update, IProgress<UpdateProgressInfo>? progress,
            CancellationToken cancellationToken = default)
        {
            Downloads++;
            progress?.Report(new(UpdatePhase.Downloading, PercentOverride: 37, IsPackageUpdate: true));
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            progress?.Report(new(UpdatePhase.Verifying, IsPackageUpdate: true));
        }
        public Task<bool> ApplyAndRestartAsync(VelopackUpdatePackage update, bool restartMinimized,
            CancellationToken cancellationToken = default)
        {
            Applies++;
            return Task.FromResult(false);
        }
    }
}

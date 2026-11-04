using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using ToastifyReloaded.Models;
using ToastifyReloaded.Services;
using ToastifyReloaded.Shared;

namespace ToastifyReloaded.Windows.Tests;

internal static class UpdateSessionPolicyTests
{
    // Keep this last: the process-only restart marker deliberately cannot be
    // cleared by product code, settings toggles, or a periodic update check.
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(!UpdateSessionPolicy.SuppressAutomaticInstall,
            "ordinary process startup preserves automatic installation");
        UpdateSessionPolicy.MarkUpdaterRestart();
        check(UpdateSessionPolicy.SuppressAutomaticInstall,
            "an updater restart suppresses automatic retry for this process");

        var backend = new Backend();
        var window = new MainWindow(true, windowsUpdateService: new WindowsUpdateService(backend: backend))
        { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, Opacity = 0 };
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var automatic = typeof(MainWindow).GetMethod("CheckForToastifyUpdatesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            var run = (Task<bool>)automatic.Invoke(window, [true, false])!;
            check(!await run.WaitAsync(TimeSpan.FromSeconds(3)) && backend.Checks == 1 && backend.Downloads == 0,
                "WPF still checks for updates after updater restart without another automatic download or UAC handoff");
            check(((TextBlock)window.FindName("UpdateStatusText")).Text.Contains("1.3.11"),
                "a suppressed automatic retry leaves the available version visible");
            check(!await window.RunUpdatePreviewAsync().WaitAsync(TimeSpan.FromSeconds(3)) &&
                  backend.Downloads == 1 && backend.Applies == 1,
                "the explicit Install action can retry after an updater restart");
            var settings = (AppSettings)typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            check(settings.AutoInstallToastifyUpdates && settings.AutoCheckToastifyUpdates,
                "restart suppression preserves both saved update preferences");
            run = (Task<bool>)automatic.Invoke(window, [true, false])!;
            check(!await run.WaitAsync(TimeSpan.FromSeconds(3)) && backend.Downloads == 1,
                "later periodic checks do not restore the canceled automatic update loop");
        }
        finally { window.Close(); }
    }

    private sealed class Backend : IVelopackUpdateBackend
    {
        public bool IsInstalled => true;
        public string CurrentVersion => "1.3.10";
        public string Channel => "win-x64";
        public int Checks, Downloads, Applies;
        public Task<VelopackUpdatePackage?> CheckAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            return Task.FromResult<VelopackUpdatePackage?>(new("1.3.11", "ToastifyReloaded-1.3.11-win-x64-full.nupkg",
                false, 4096, new object()));
        }
        public Task DownloadAsync(VelopackUpdatePackage update, IProgress<UpdateProgressInfo>? progress,
            CancellationToken cancellationToken = default)
        { Downloads++; return Task.CompletedTask; }
        public Task<bool> ApplyAndRestartAsync(VelopackUpdatePackage update, bool restartMinimized,
            CancellationToken cancellationToken = default)
        { Applies++; return Task.FromResult(false); }
    }
}

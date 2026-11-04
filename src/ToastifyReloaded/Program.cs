using Velopack;
using ToastifyReloaded.Shared;
using ToastifyReloaded.Services;

namespace ToastifyReloaded;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Lifecycle hooks must exit before WPF, the single-instance mutex, tray,
        // Spotify probes, or normal application settings are initialized.
        VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false)
            .OnRestarted(_ => UpdateSessionPolicy.MarkUpdaterRestart()).Run();

        // Hook invocations have already exited. Keep existing enabled startup
        // entries pointing at the stable stub after the one-time migration.
        StartupService.RefreshEnabledCommand();

        var app = new App();
        // App.xaml intentionally has no resources or StartupUri; WPF does not
        // generate InitializeComponent for this empty application definition.
        app.Run();
    }
}

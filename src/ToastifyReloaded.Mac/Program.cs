using Avalonia;
using Avalonia.Controls;
using Avalonia.Fonts.Inter;
using ToastifyReloaded.Mac.Services;
using Velopack;

namespace ToastifyReloaded.Mac;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false)
            .OnRestarted(_ => ToastifyReloaded.Shared.UpdateSessionPolicy.MarkUpdaterRestart()).Run();
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = MacSelfTestService.RunAsync().GetAwaiter().GetResult();
            return;
        }

        using var instance = ToastifyReloaded.Shared.SingleInstanceLease.TryAcquire("macos");
        if (instance is null) { Console.Error.WriteLine("Toastify Reloaded è già in esecuzione."); return; }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(
            args,
            ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

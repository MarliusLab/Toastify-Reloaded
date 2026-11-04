using Avalonia;
using Avalonia.Controls;
using Avalonia.Fonts.Inter;
using ToastifyReloaded.Linux.Services;
using Velopack;

namespace ToastifyReloaded.Linux;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false)
            .OnRestarted(_ => ToastifyReloaded.Shared.UpdateSessionPolicy.MarkUpdaterRestart()).Run();
        if (args.Contains(
                "--self-test",
                StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode =
                LinuxSelfTestService
                    .RunAsync()
                    .GetAwaiter()
                    .GetResult();

            return;
        }

        using var instance = ToastifyReloaded.Shared.SingleInstanceLease.TryAcquire("linux");
        if (instance is null) { Console.Error.WriteLine("Toastify Reloaded è già in esecuzione."); return; }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(
            args,
            ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        var waylandDisplay =
            Environment.GetEnvironmentVariable(
                "WAYLAND_DISPLAY");

        var disableNativeWayland =
            string.Equals(
                Environment.GetEnvironmentVariable(
                    "TOASTIFY_DISABLE_NATIVE_WAYLAND"),
                "1",
                StringComparison.Ordinal);

        if (OperatingSystem.IsLinux() &&
            !disableNativeWayland &&
            !string.IsNullOrWhiteSpace(waylandDisplay))
        {
            builder = builder.UseWayland();
        }

        return builder;
    }
}

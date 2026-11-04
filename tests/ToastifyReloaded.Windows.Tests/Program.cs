using System.Windows.Threading;

namespace ToastifyReloaded.Windows.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Velopack.VelopackApp.Build().SetArgs([]).SetAutoApplyOnStartup(false).Run();
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var task = RunAsync();
        _ = task.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.Default);
        Dispatcher.Run();
        return task.GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            count++;
            Console.WriteLine("PASS: " + name);
        }
        try
        {
            await DetectionTests.RunAsync(Check);
            await UpdateTests.RunAsync(Check);
            await VelopackUpdateTests.RunAsync(Check);
            await InstallerTests.RunAsync(Check);
            await UiTests.RunAsync(Check);
            await WindowsPackageUiTests.RunAsync(Check);
            await SpotifyTrackOpenTests.RunAsync(Check);
            await LyricsPlusOpenTests.RunAsync(Check);
            await ToastTests.RunAsync(Check);
            await ToastShowHotkeyTests.RunAsync(Check);
            await ToastHotkeyTests.RunAsync(Check);
            Console.WriteLine($"WINDOWS REGRESSION TEST RESULT: PASS ({count})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}

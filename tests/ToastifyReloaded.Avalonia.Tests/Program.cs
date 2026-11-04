using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            // One application, dispatcher and UI thread for both production views.
            // Neither native app startup nor any user's settings/backend is initialized.
            AppBuilder.Configure<TestApplication>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
            using var stop = new CancellationTokenSource();
            var tests = RunAsync();
            _ = tests.ContinueWith(_ => stop.Cancel(), TaskScheduler.Default);
            Dispatcher.UIThread.MainLoop(stop.Token);
            tests.GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("AVALONIA TIMED TOAST RESULT: FAIL");
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        var passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
            Console.WriteLine("PASS: " + message);
        }
        await MacTimedToastTests.RunAsync(Check);
        await LinuxTimedToastTests.RunAsync(Check);
        Console.WriteLine($"AVALONIA TIMED TOAST RESULT: PASS ({passed})");
    }
}

internal sealed class TestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

internal static class TimedToastWait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("Timed toast UI condition did not settle");
            await Task.Delay(15);
        }
    }
}

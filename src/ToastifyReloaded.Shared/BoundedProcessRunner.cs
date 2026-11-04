using System.Diagnostics;
using System.IO;
using System.Text;

namespace ToastifyReloaded.Shared;

public static class BoundedProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(1);
    private const int OutputLimit = 64 * 1024;

    public static Task<ProcessCommandResult> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
        => Task.Run(() => RunCoreAsync(executable, arguments, cancellationToken, timeout), cancellationToken);

    private static async Task<ProcessCommandResult> RunCoreAsync(string executable, IEnumerable<string> arguments,
        CancellationToken cancellationToken, TimeSpan? timeout)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budget = timeout ?? DefaultTimeout;
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using var drains = new CancellationTokenSource();
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = executable, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        }};
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException("Could not start the requested helper.");
        var stdout = ReadBoundedAsync(process.StandardOutput, drains.Token);
        var stderr = ReadBoundedAsync(process.StandardError, drains.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            // A descendant may retain inherited pipe handles after this helper
            // exits. Reading those streams must never create an unlimited wait.
            try
            {
                await Task.WhenAll(stdout, stderr).WaitAsync(DrainTimeout, linked.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("The helper exited but its output pipes did not close.");
            }
            return new ProcessCommandResult(process.ExitCode, (await stdout).Trim(), (await stderr).Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The helper exceeded its execution deadline.");
        }
        finally
        {
            drains.Cancel();
            try
            {
                // Only the helper belongs to this operation. A desktop player
                // it opened must never be killed as an incidental descendant.
                if (!process.HasExited) process.Kill(entireProcessTree: false);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            Observe(stdout);
            Observe(stderr);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            var remaining = OutputLimit - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(remaining, count));
            // Continue draining excess data so the helper cannot block on a
            // full pipe, while keeping memory use bounded.
        }
        return output.ToString();
    }

    private static void Observe(Task task) => _ = task.ContinueWith(t => _ = t.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}

using ToastifyReloaded.Shared;

namespace ToastifyReloaded.Linux.Services;

public sealed class ProcessService
{
    // Keep the three-argument signature for existing opener delegates.
    public Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string fileName,
        IEnumerable<string> arguments, CancellationToken cancellationToken = default)
        => RunCoreAsync(fileName, arguments, cancellationToken, null);

    public Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string fileName,
        IEnumerable<string> arguments, CancellationToken cancellationToken, TimeSpan timeout)
        => RunCoreAsync(fileName, arguments, cancellationToken, timeout);

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCoreAsync(
        string fileName, IEnumerable<string> arguments, CancellationToken token, TimeSpan? timeout)
    {
        var result = await BoundedProcessRunner.RunAsync(fileName, arguments, token, timeout);
        return (result.ExitCode, result.StdOut, result.StdErr);
    }

    public async Task<bool> ExistsAsync(string command, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunAsync("which", new[] { command }, cancellationToken, TimeSpan.FromSeconds(3));
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }
}

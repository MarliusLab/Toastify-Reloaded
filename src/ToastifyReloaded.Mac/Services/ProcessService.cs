using System.Diagnostics;

namespace ToastifyReloaded.Mac.Services;

public sealed class ProcessService
{
    public async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stop the hung helper only; a desktop application it launched
            // must remain independent and must never be terminated here.
            try { process.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) { }
            throw;
        }

        return (
            process.ExitCode,
            (await stdoutTask).Trim(),
            (await stderrTask).Trim());
    }

    public async Task<bool> ExistsAsync(string command)
    {
        try
        {
            var result = await RunAsync("which", new[] { command });
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
        }
        catch
        {
            return false;
        }
    }
}

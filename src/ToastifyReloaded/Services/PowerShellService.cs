using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace ToastifyReloaded.Services;

public sealed record PowerShellCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

public static class PowerShellService
{
    private const string EmbeddedScriptPrefix = "ToastifyReloaded.Scripts.";

    /// <summary>
    /// Estrae in una cartella temporanea uno script PowerShell incorporato
    /// nell'EXE e lo avvia. In questo modo la Release può contenere un solo EXE
    /// pur mantenendo gli helper Lyrics/diagnostica disponibili nell'app.
    /// </summary>
    public static void RunScript(string scriptName, params string[] arguments)
    {
        if (string.IsNullOrWhiteSpace(scriptName) ||
            !string.Equals(scriptName, Path.GetFileName(scriptName), StringComparison.Ordinal))
        {
            throw new ArgumentException("Nome script non valido.", nameof(scriptName));
        }

        var resourceName = EmbeddedScriptPrefix + scriptName;
        var assembly = Assembly.GetExecutingAssembly();
        using var resourceStream = assembly.GetManifestResourceStream(resourceName);
        if (resourceStream is null)
        {
            throw new FileNotFoundException(
                $"Script incorporato non trovato: {scriptName}. La build di Toastify Reloaded potrebbe essere incompleta.");
        }

        var scriptDirectory = Path.Combine(
            Path.GetTempPath(),
            "ToastifyReloaded",
            "embedded-scripts",
            GetAssemblyVersion());
        Directory.CreateDirectory(scriptDirectory);

        var scriptPath = Path.Combine(scriptDirectory, scriptName);
        using (var destination = new FileStream(scriptPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            resourceStream.CopyTo(destination);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("Impossibile avviare PowerShell.");
    }

    public static Task<PowerShellCommandResult> RunHiddenCommandAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        return RunHiddenExecutableAsync("powershell.exe", new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded
        }, timeout ?? TimeSpan.FromMinutes(4), cancellationToken);
    }

    public static Task<PowerShellCommandResult> RunHiddenExecutableAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        // Process.Start and filesystem/pipe setup must not run on the WPF UI thread.
        => Task.Run(() => RunProcessAsync(fileName, arguments, timeout, cancellationToken), cancellationToken);

    private static async Task<PowerShellCommandResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Impossibile avviare {Path.GetFileName(fileName)}.");

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
        var output = new OutputCapture();
        var error = new OutputCapture();
        var outputTask = output.ReadAsync(process.StandardOutput, readCts.Token);
        var errorTask = error.ReadAsync(process.StandardError, readCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            await StopProcessBoundedAsync(process).ConfigureAwait(false);
        }
        finally
        {
            // A descendant can retain a redirected pipe after its parent exits
            // (or tree termination can fail). Never wait indefinitely for EOF.
            try { await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch { }
            readCts.Cancel();
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return timedOut
            ? new PowerShellCommandResult(-1, output.Text, "Comando scaduto per timeout. " + error.Text)
            : new PowerShellCommandResult(process.ExitCode, output.Text, error.Text);
    }

    private static async Task StopProcessBoundedAsync(Process process)
    {
        try
        {
            await Task.Run(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch
                {
                    // Tree enumeration can be denied even when terminating our
                    // own direct child is allowed. Still stop that helper.
                    try { if (!process.HasExited) process.Kill(); }
                    catch { }
                }
            }).WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        catch { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
        catch { }
    }

    private sealed class OutputCapture
    {
        private readonly StringBuilder _text = new();
        public string Text { get { lock (_text) return _text.ToString(); } }

        public async Task ReadAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    lock (_text)
                    {
                        // Keep diagnostics bounded even if a broken CLI is noisy.
                        var available = 1024 * 1024 - _text.Length;
                        if (available > 0) _text.Append(buffer, 0, Math.Min(available, count));
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The caller controls process lifetime and bounded pipe cleanup.
            }
        }
    }

    private static string GetAssemblyVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }
}

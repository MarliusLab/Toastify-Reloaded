using System.Diagnostics;
using System.IO;
using ToastifyReloaded.Models;
using ToastifyReloaded.Services;

namespace ToastifyReloaded.Windows.Tests;

public static class DetectionTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var callerThread = Environment.CurrentManagedThreadId;
        var probeThread = callerThread;
        var storeCalls = 0;
        var desktop = new SpotifyInstallationInfo("1.2.56.502", "Desktop", @"C:\Spotify\Spotify.exe");
        var detector = new SpotifyInstallationService(() =>
        {
            probeThread = Environment.CurrentManagedThreadId;
            return desktop;
        }, _ => { storeCalls++; return Task.FromResult<SpotifyInstallationInfo?>(null); });
        check(await detector.GetInfoAsync() == desktop && probeThread != callerThread && storeCalls == 0,
            "Spotify desktop probe runs off caller/UI thread and skips Store lookup when detected");

        var store = SpotifyInstallationService.ParseStorePackageOutput(
            "{\"Version\":\"1.250.273.0\",\"InstallLocation\":\"C:\\\\Program Files\\\\WindowsApps\\\\SpotifyAB.SpotifyMusic_fixture\"}");
        check(store is { Version: "1.250.273.0", InstallKind: "Microsoft Store" } &&
            store.ExecutablePath?.EndsWith("Spotify.exe", StringComparison.Ordinal) == true,
            "Microsoft Store JSON preserves package version and executable location");
        var storeDetector = new SpotifyInstallationService(() => SpotifyInstallationInfo.NotFound,
            _ => Task.FromResult(store));
        check(await storeDetector.GetInfoAsync() == store, "Store package is detected when desktop installation is absent");
        check(SpotifyInstallationService.ParseStorePackageOutput("WARNING: package tool 1.2.3 failed") is null &&
            SpotifyInstallationService.ParseStorePackageOutput("{\"Version\":\"WARNING 1.2.3\"}") is null &&
            SpotifyInstallationService.ParseStorePackageOutput("{}") is null,
            "malformed Store output and numeric warnings are not reported as installed Spotify");

        var timedDetector = new SpotifyInstallationService(() => { Thread.Sleep(250); return SpotifyInstallationInfo.NotFound; },
            _ => Task.FromResult<SpotifyInstallationInfo?>(null), TimeSpan.FromMilliseconds(40));
        var elapsed = Stopwatch.StartNew();
        check(!(await timedDetector.GetInfoAsync()).IsDetected && elapsed.Elapsed < TimeSpan.FromSeconds(2),
            "overall Spotify detection wait is bounded even when a synchronous probe stalls");
        var cancelDetector = new SpotifyInstallationService(() => SpotifyInstallationInfo.NotFound, async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return null;
        });
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            var cancelled = false;
            try { await cancelDetector.GetInfoAsync(cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Spotify Store probe propagates caller cancellation");
        }

        var firstDirectory = Path.Combine(Path.GetTempPath(), "Toastify old shim fixture");
        var secondDirectory = Path.Combine(Path.GetTempPath(), "Toastify installed CLI fixture");
        var paths = new[] { firstDirectory, secondDirectory }.Select(d => Path.Combine(d, "spicetify.exe")).ToArray();
        var locator = new WindowsExecutableLocator(
            () => new[] { "relative-untrusted", '"' + firstDirectory + '"', firstDirectory.ToUpperInvariant(), secondDirectory },
            path => paths.Contains(path, StringComparer.OrdinalIgnoreCase));
        check(locator.FindSpicetifyCandidates().SequenceEqual(paths, StringComparer.OrdinalIgnoreCase),
            "Spicetify lookup accepts quoted install paths, rejects relative paths and deduplicates stale PATH entries");
        var freshDirectory = firstDirectory;
        var freshLocator = new WindowsExecutableLocator(() => new[] { freshDirectory }, _ => true);
        var before = freshLocator.FindSpicetifyCandidates().Single();
        freshDirectory = secondDirectory;
        check(freshLocator.FindSpicetifyCandidates().Single() != before,
            "Spicetify directories are refreshed on each probe instead of cached inherited PATH");

        var runs = new List<(string Path, string[] Arguments)>();
        var compatibility = new CompatibilityRepairService(locator, (path, arguments, timeout, token) =>
        {
            runs.Add((path, arguments.ToArray()));
            check(timeout <= TimeSpan.FromSeconds(3) && token.CanBeCanceled,
                "Spicetify version process has timeout and cancellation");
            return Task.FromResult(path.Equals(paths[0], StringComparison.OrdinalIgnoreCase)
                ? new PowerShellCommandResult(1, "", "stale shim")
                : new PowerShellCommandResult(0, "v2.43.0\r\n", ""));
        });
        check(await compatibility.GetSpicetifyVersionAsync() == "2.43.0" && runs.Count == 2 &&
            runs.All(r => r.Arguments.SequenceEqual(new[] { "--version" })),
            "version lookup skips broken shim and invokes resolved Spicetify path with literal arguments");
        var warningCli = new CompatibilityRepairService(locator,
            (_, _, _, _) => Task.FromResult(new PowerShellCommandResult(0, "Download failed for 2.43.0", "")));
        check(await warningCli.GetSpicetifyVersionAsync() == "", "Spicetify warning text is not mistaken for a version");
        var missingCli = new CompatibilityRepairService(new WindowsExecutableLocator(() => Array.Empty<string>(), _ => false),
            (_, _, _, _) => throw new InvalidOperationException("No executable should be launched"));
        check(await missingCli.GetSpicetifyVersionAsync() == "", "missing Spicetify returns a completed not-detected result");
        var cancellableCli = new CompatibilityRepairService(locator, async (_, _, _, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new PowerShellCommandResult(0, "2.43.0", "");
        });
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            var cancelled = false;
            try { await cancellableCli.GetSpicetifyVersionAsync(cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Spicetify version lookup propagates caller cancellation");
        }

        var stopped = 0;
        var launched = 0;
        var repairCommands = new List<string>();
        var repair = new CompatibilityRepairService(new WindowsExecutableLocator(() => new[] { secondDirectory }, _ => true),
            (path, arguments, _, _) =>
            {
                check(path == paths[1], "repair reuses the resolved Spicetify executable outside inherited PATH");
                var command = string.Join(" ", arguments);
                repairCommands.Add(command);
                return Task.FromResult(command switch
                {
                    "--version" => new PowerShellCommandResult(0, "2.43.0", ""),
                    "upgrade" or "backup apply" or "auto" => new PowerShellCommandResult(1, "", "synthetic failure"),
                    "config custom_apps" => new PowerShellCommandResult(0, "another-app", ""),
                    _ => new PowerShellCommandResult(0, "", "")
                });
            }, _ => { stopped++; return Task.CompletedTask; }, () => launched++);
        var repairResult = await repair.RepairAsync(new AppSettings
        {
            AutoUpgradeSpicetify = true,
            KeepLyricsPlusEnabled = true,
            RestartSpotifyAfterRepair = true
        });
        check(repairResult.Success && repairResult.UsedFallback && stopped == 1 && launched == 1 &&
            repairCommands.SequenceEqual(new[] { "--version", "upgrade", "backup apply", "restore backup apply",
                "config custom_apps", "config custom_apps lyrics-plus", "apply", "auto" }),
            "synthetic repair preserves upgrade, fallback, Lyrics and restart flow without touching host Spotify");

        // Real subprocess tests execute only output/sleep commands. They never
        // invoke installed Spotify, installed Spicetify, repair or user settings.
        var output = await PowerShellService.RunHiddenCommandAsync(
            "Write-Output 'stdout-sentinel'; [Console]::Error.WriteLine('stderr-sentinel'); exit 7", TimeSpan.FromSeconds(8));
        check(output.ExitCode == 7 && output.StandardOutput.Contains("stdout-sentinel") && output.StandardError.Contains("stderr-sentinel"),
            "hidden process captures stdout, stderr and exit code");

        // The short-lived parent exits while a synthetic child retains its
        // inherited pipe. This reproduced the old unbounded ReadToEnd await.
        // Clean up only the exact child PID created by this fixture.
        elapsed.Restart();
        var heldPipe = await PowerShellService.RunHiddenCommandAsync(
            "$s = New-Object System.Diagnostics.ProcessStartInfo; $s.FileName='powershell.exe'; " +
            "$s.UseShellExecute=$false; $s.CreateNoWindow=$true; " +
            "$s.Arguments='-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 6\"'; " +
            "$p=[System.Diagnostics.Process]::Start($s); Write-Output $p.Id", TimeSpan.FromSeconds(8));
        var heldPipeDuration = elapsed.Elapsed;
        try
        {
            check(heldPipe.Success && int.TryParse(heldPipe.StandardOutput.Trim(), out _) && heldPipeDuration < TimeSpan.FromSeconds(4),
                "parent exit with child-held stdout does not cause an unbounded drain wait");
        }
        finally
        {
            if (int.TryParse(heldPipe.StandardOutput.Trim(), out var childPid))
            {
                try
                {
                    using var child = Process.GetProcessById(childPid);
                    if (!child.HasExited && child.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase))
                    {
                        try { child.Kill(entireProcessTree: true); }
                        catch (System.ComponentModel.Win32Exception) { child.Kill(); }
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    }
                }
                catch (ArgumentException) { } // Child already finished its six-second timer.
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { } // Restricted host; synthetic child exits its own short timer.
            }
        }
        elapsed.Restart();
        var timeoutResult = await PowerShellService.RunHiddenCommandAsync("Start-Sleep -Seconds 20", TimeSpan.FromMilliseconds(250));
        check(timeoutResult.ExitCode == -1 && elapsed.Elapsed < TimeSpan.FromSeconds(4),
            "hidden process timeout terminates and drains output within a bounded interval");
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            elapsed.Restart();
            var cancelled = false;
            try { await PowerShellService.RunHiddenCommandAsync("Start-Sleep -Seconds 20", TimeSpan.FromSeconds(30), cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && elapsed.Elapsed < TimeSpan.FromSeconds(4),
                "hidden process cancellation returns promptly after bounded cleanup");
        }
    }
}

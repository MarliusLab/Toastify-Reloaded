using System.Diagnostics;
using ToastifyReloaded.Mac.Services;

internal static class MacUpdateHandoffTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Console.WriteLine("SKIP: generated macOS update shell execution fixtures (requires a Unix host)");
            return;
        }
        foreach (var mode in new[] { "success", "cleanup-fails", "restart-fails", "replace-fails", "rollback-fails", "wait-release", "wait-timeout" })
        {
            var root = Path.Combine(Path.GetTempPath(), "mac-update-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var current = Path.Combine(root, "current quote ' $(touch unexpected).app");
                var staged = Path.Combine(root, "new quote ' $(touch unexpected).app");
                Directory.CreateDirectory(current); Directory.CreateDirectory(staged);
                File.WriteAllText(Path.Combine(current, "payload"), "old");
                File.WriteAllText(Path.Combine(staged, "payload"), "new");
                var settings = Path.Combine(root, "settings.json"); File.WriteAllText(settings, "{\"preserve\":true}");
                var settingsBefore = File.ReadAllBytes(settings);
                string Command(string name, string content)
                {
                    var path = Path.Combine(root, name + ".sh");
                    File.WriteAllText(path, "#!/bin/sh\nset -eu\n" + content + "\n");
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    return path;
                }
                var ditto = Command("ditto", "exec /bin/cp -R \"$1\" \"$2\"");
                var move = Command("move", """
case "$1" in
  *.update-*) case "$MAC_FIXTURE_MODE" in replace-fails|rollback-fails) exit 23 ;; esac ;;
  *.previous-*) [ "$MAC_FIXTURE_MODE" != rollback-fails ] || exit 24 ;;
esac
exec /bin/mv "$@"
""");
                var remove = Command("remove", """
if [ "$MAC_FIXTURE_MODE" = cleanup-fails ]; then
  for argument in "$@"; do case "$argument" in *.previous-*) exit 13 ;; esac; done
fi
exec /bin/rm "$@"
""");
                var open = Command("open", """
printf '%s\n' "$@" > "$MAC_FIXTURE_OPEN"
[ "$(/bin/cat "$1/payload")" = new ] || exit 18
[ "$MAC_FIXTURE_MODE" != restart-fails ]
""");
                var kill = Command("kill", """
[ "$(/bin/cat "$MAC_FIXTURE_CURRENT/payload")" = old ] || exit 29
count=0
[ ! -f "$MAC_FIXTURE_STATE" ] || count=$(/bin/cat "$MAC_FIXTURE_STATE")
count=$((count + 1))
printf '%s\n' "$count" > "$MAC_FIXTURE_STATE"
[ "$MAC_FIXTURE_MODE" != wait-timeout ] || exit 0
if [ "$MAC_FIXTURE_MODE" = wait-release ] && [ "$count" -lt 3 ]; then exit 0; fi
exit 1
""");
                var sleep = Command("sleep", "exit 0");
                var noElevation = Command("no-elevation", "exit 91");
                var files = MacUpdateHandoff.Create(current, staged, 12345, true, root, Path.Combine(root, "results"),
                    new(ditto, move, remove, open, kill, sleep, AppleScript: noElevation));
                if (OperatingSystem.IsMacOS() && mode == "success")
                {
                    var compiler = new ProcessStartInfo("/usr/bin/osacompile") { UseShellExecute = false };
                    foreach (var argument in new[] { "-o", Path.Combine(root, "compiled.scpt"), files.AppleScript })
                        compiler.ArgumentList.Add(argument);
                    using var compilation = Process.Start(compiler)!;
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    try { await compilation.WaitForExitAsync(deadline.Token); }
                    catch { compilation.Kill(entireProcessTree: true); throw; }
                    check(compilation.ExitCode == 0, "macOS elevation script compiles without requesting permission or running an installer");
                }
                var start = new ProcessStartInfo("/bin/sh")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
                start.ArgumentList.Add(files.Helper);
                start.Environment["MAC_FIXTURE_MODE"] = mode;
                start.Environment["MAC_FIXTURE_CURRENT"] = current;
                start.Environment["MAC_FIXTURE_STATE"] = Path.Combine(root, "kill-count");
                start.Environment["MAC_FIXTURE_OPEN"] = Path.Combine(root, "open-arguments");
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch { process.Kill(entireProcessTree: true); throw; }
                await Task.WhenAll(stdout, stderr);
                var receipt = File.ReadAllText(files.Receipt).Trim();
                var log = File.ReadAllText(files.Log);
                var currentPayload = File.Exists(Path.Combine(current, "payload")) ? File.ReadAllText(Path.Combine(current, "payload")) : "missing";
                var backups = Directory.GetDirectories(root, "*.previous-*");
                var started = File.Exists(Path.Combine(root, "open-arguments"));
                var passed = mode switch
                {
                    "replace-fails" => process.ExitCode != 0 && receipt == "not-installed-replacement-failed" && currentPayload == "old" && !started,
                    "rollback-fails" => process.ExitCode != 0 && currentPayload == "missing" && backups.Length == 1 && File.ReadAllText(Path.Combine(backups[0], "payload")) == "old" && !started,
                    "wait-timeout" => process.ExitCode != 0 && receipt == "not-installed-process-still-running" && currentPayload == "old" && !started,
                    "restart-fails" => process.ExitCode == 0 && receipt == "installed-manual-restart" && currentPayload == "new" && log.Contains("INSTALLED:") && started,
                    "cleanup-fails" => process.ExitCode == 0 && receipt == "installed-restart-requested" && currentPayload == "new" && log.Contains("WARNING:") && backups.Length == 1 && started,
                    _ => process.ExitCode == 0 && receipt == "installed-restart-requested" && currentPayload == "new" && started
                };
                if (mode == "wait-release") passed &= File.ReadAllText(Path.Combine(root, "kill-count")).Trim() == "3";
                if (started) passed &= File.ReadAllLines(Path.Combine(root, "open-arguments")).SequenceEqual(new[] { current, "--args", "--minimized" });
                passed &= settingsBefore.SequenceEqual(File.ReadAllBytes(settings)) && !File.Exists(Path.Combine(root, "unexpected"));
                if (!passed) Console.WriteLine($"macOS fixture {mode}: exit={process.ExitCode},receipt={receipt},payload={currentPayload},log={log}");
                check(passed, "macOS actual generated helper preserves install/restart phase, settings and literal paths: " + mode);
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }
}

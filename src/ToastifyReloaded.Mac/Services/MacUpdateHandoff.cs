namespace ToastifyReloaded.Mac.Services;

internal sealed record MacUpdateHandoffCommands(
    string Ditto = "/usr/bin/ditto", string Move = "/bin/mv", string Remove = "/bin/rm",
    string Open = "/usr/bin/open", string Kill = "/bin/kill", string Sleep = "/bin/sleep",
    string Shell = "/bin/sh", string AppleScript = "/usr/bin/osascript", string Dirname = "/usr/bin/dirname");

internal sealed record MacUpdateHandoffFiles(string Helper, string Worker, string AppleScript, string Receipt, string Log);

/// <summary>Separates a committed bundle replacement from its optional restart and cleanup.</summary>
internal static class MacUpdateHandoff
{
    internal static MacUpdateHandoffFiles Create(string currentApp, string newApp, int pid, bool minimized = false,
        string? temporaryRoot = null, string? resultDirectory = null, MacUpdateHandoffCommands? commands = null)
    {
        if (pid <= 0 || !Path.IsPathRooted(currentApp) || !currentApp.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathRooted(newApp) || !newApp.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ||
            currentApp.Contains('\0') || newApp.Contains('\0'))
            throw new ArgumentException("Invalid macOS update handoff.");
        commands ??= new();
        foreach (var command in new[] { commands.Ditto, commands.Move, commands.Remove, commands.Open, commands.Kill,
                     commands.Sleep, commands.Shell, commands.AppleScript, commands.Dirname })
            if (!Path.IsPathRooted(command) || command.Contains('\0')) throw new ArgumentException("Invalid update helper command.");
        var helperRoot = Path.Combine(temporaryRoot ?? Path.GetTempPath(), "ToastifyReloaded-update-" + Guid.NewGuid().ToString("N"));
        resultDirectory ??= Path.Combine(Path.GetDirectoryName(newApp)!, "update-status");
        if (!Path.IsPathRooted(resultDirectory)) throw new ArgumentException("Invalid update result path.");
        Directory.CreateDirectory(helperRoot);
        Directory.CreateDirectory(resultDirectory);
        var worker = Path.Combine(helperRoot, "replace.sh");
        var apple = Path.Combine(helperRoot, "elevate.applescript");
        var helper = Path.Combine(helperRoot, "apply-update.sh");
        var receipt = Path.Combine(resultDirectory, "apply-result.txt");
        var log = Path.Combine(resultDirectory, "apply-update.log");
        // Receipts/logs are created as the regular user, outside transient helpers.
        File.WriteAllText(receipt, "pending\n");
        File.WriteAllText(log, "");
        File.WriteAllText(worker, Lines(
            "#!/bin/sh", "set -eu", "current=$1", "staged=$2", "nonce=$3",
            "candidate=\"$current.update-$nonce\"", "backup=\"$current.previous-$nonce\"",
            "move=" + Quote(commands.Move), "remove=" + Quote(commands.Remove),
            "[ ! -e \"$candidate\" ] && [ ! -e \"$backup\" ] || exit 73",
            "trap '\"$remove\" -rf -- \"$candidate\" || :' EXIT",
            Quote(commands.Ditto) + " \"$staged\" \"$candidate\"",
            "\"$move\" \"$current\" \"$backup\"",
            "if \"$move\" \"$candidate\" \"$current\"; then",
            "  if ! \"$remove\" -rf -- \"$backup\"; then",
            "    printf '%s\\n' 'WARNING: update installed; previous bundle cleanup failed.' >&2 || :", "  fi",
            "  exit 0", "else",
            "  if ! \"$move\" \"$backup\" \"$current\"; then",
            "    printf '%s\\n' \"ERROR: replacement failed; recover the previous bundle at $backup\" >&2 || :", "  fi",
            "  exit 1", "fi"));
        File.WriteAllText(apple, """
on run argv
    set commandText to quoted form of (item 1 of argv) & " " & quoted form of (item 2 of argv) & " " & quoted form of (item 3 of argv) & " " & quoted form of (item 4 of argv) & " " & quoted form of (item 5 of argv)
    do shell script commandText with administrator privileges
end run
""");
        File.WriteAllText(helper, Lines(
            "#!/bin/sh", "set -u",
            "current=" + Quote(currentApp), "staged=" + Quote(newApp), "worker=" + Quote(worker),
            "apple=" + Quote(apple), "receipt=" + Quote(receipt), "log=" + Quote(log),
            "remove=" + Quote(commands.Remove), "helperRoot=" + Quote(helperRoot), "nonce=" + Quote(Guid.NewGuid().ToString("N")),
            "write_state() { printf '%s\\n' \"$1\" >\"$receipt\" || :; }",
            "trap '\"$remove\" -rf -- \"$helperRoot\" || :' EXIT",
            "if : >>\"$log\"; then exec >>\"$log\" 2>&1; fi",
            "write_state waiting-for-exit", "count=0",
            "while " + Quote(commands.Kill) + " -0 " + pid + " 2>/dev/null; do",
            "  count=$((count + 1))",
            "  if [ \"$count\" -ge 240 ]; then write_state not-installed-process-still-running; exit 1; fi",
            "  " + Quote(commands.Sleep) + " 0.25 || { write_state not-installed-wait-failed; exit 1; }", "done",
            "write_state installing",
            "if [ -w \"$(" + Quote(commands.Dirname) + " \"$current\")\" ] && [ -w \"$current\" ]; then",
            "  " + Quote(commands.Shell) + " \"$worker\" \"$current\" \"$staged\" \"$nonce\"", "else",
            "  " + Quote(commands.AppleScript) + " \"$apple\" " + Quote(commands.Shell) + " \"$worker\" \"$current\" \"$staged\" \"$nonce\"", "fi",
            "status=$?", "if [ \"$status\" -ne 0 ]; then write_state not-installed-replacement-failed; exit \"$status\"; fi",
            "write_state installed",
            // This helper is never elevated; the privileged worker cannot launch the app.
            "if " + Quote(commands.Open) + " \"$current\"" + (minimized ? " --args --minimized" : "") + "; then",
            "  write_state installed-restart-requested", "else",
            "  write_state installed-manual-restart",
            "  printf '%s\\n' 'INSTALLED: update completed; automatic restart failed. Open Toastify Reloaded manually.' >&2 || :", "fi",
            "exit 0"));
        return new(helper, worker, apple, receipt, log);
    }

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

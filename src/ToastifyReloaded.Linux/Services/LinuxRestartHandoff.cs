using System.Diagnostics;

namespace ToastifyReloaded.Linux.Services;

/// <summary>Defers legacy restart until the old process has released its instance lease.</summary>
internal static class LinuxRestartHandoff
{
    internal const string Script = """
#!/bin/sh
set -eu
pid=$1
executable=$2
minimized=$3
cleanup() {
  /bin/rm -f -- "$0"
  /bin/rmdir -- "$(/usr/bin/dirname "$0")" 2>/dev/null || :
}
trap cleanup EXIT
count=0
while /bin/kill -0 "$pid" 2>/dev/null || [ -d "/proc/$pid" ]; do
  count=$((count + 1))
  [ "$count" -lt 240 ] || exit 1
  /bin/sleep 0.25
done
cleanup
trap - EXIT
if [ "$minimized" = "1" ]; then
  exec "$executable" --minimized
else
  exec "$executable"
fi
""";

    internal static ProcessStartInfo CreateStartInfo(string executable, int oldProcessId,
        bool minimized, string? temporaryRoot = null)
    {
        if (oldProcessId <= 0 || string.IsNullOrWhiteSpace(executable) || !Path.IsPathRooted(executable) ||
            executable.Contains('\0')) throw new ArgumentException("Invalid Linux restart handoff.");
        var directory = Path.Combine(temporaryRoot ?? Path.GetTempPath(), "toastify-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "restart.sh");
        File.WriteAllText(script, Script.Replace("\r\n", "\n") + "\n");
        var start = new ProcessStartInfo { FileName = "/bin/sh", UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { script, oldProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), executable, minimized ? "1" : "0" })
            start.ArgumentList.Add(argument);
        return start;
    }
}

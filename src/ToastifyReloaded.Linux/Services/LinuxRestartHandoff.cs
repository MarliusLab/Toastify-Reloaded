using System.Diagnostics;
using ToastifyReloaded.Linux.Models;

namespace ToastifyReloaded.Linux.Services;

/// <summary>Defers legacy restart until the old process has released its instance lease.</summary>
internal static class LinuxRestartHandoff
{
    internal static LinuxUpdateApplyResult RequestAfterInstall(string installedMessage, string path, Func<bool> startRestart)
    {
        try
        {
            return startRestart()
                ? new(true, true, installedMessage + "; riavvio richiesto dopo la chiusura della versione attuale.", path, Installed: true)
                : new(true, false, ManualRestartMessage(installedMessage), path, Installed: true);
        }
        catch (Exception error)
        {
            // The files/package are already committed. A failed restart (even
            // late cancellation) cannot undo the successful installation.
            return new(true, false, ManualRestartMessage(installedMessage) + " Dettagli: " + error.Message, path, Installed: true);
        }
    }

    internal static string ManualRestartMessage(string installedMessage)
        => installedMessage + ". L'avvio automatico non è riuscito: chiudi Toastify Reloaded e riaprilo manualmente. Le impostazioni sono state conservate.";

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

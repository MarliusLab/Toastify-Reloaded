using System.Text.RegularExpressions;
using System.IO;

namespace ToastifyReloaded.Windows.Tests;

public static class InstallerTests
{
    public static Task RunAsync(Action<bool, string> check)
    {
        var folder = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "installer", "ToastifyReloaded.nsi")))
            folder = folder.Parent;
        if (folder is null)
            throw new InvalidOperationException("Cannot locate the installer source for handshake regression checks.");

        var script = File.ReadAllText(Path.Combine(folder.FullName, "installer", "ToastifyReloaded.nsi"));
        string FunctionBody(string name) => Regex.Match(script,
            @"(?ms)^Function " + Regex.Escape(name) + @"\r?\n(.*?)^FunctionEnd\s*$").Groups[1].Value;
        var initializer = FunctionBody(".onInit");
        var wait = FunctionBody("WaitForUpdatingProcess");
        var skipPage = FunctionBody("SkipUpdateWizardPage");

        check(!initializer.Contains("WaitForSingleObject") && !initializer.Contains("Wait-Process"),
            "installer initialization cannot wait on the application during UAC handoff");
        check(wait.Contains("OpenProcess") && wait.Contains("WaitForSingleObject") && wait.Contains("CloseHandle") &&
              wait.Contains("SetErrorLevel 1460") && wait.Contains("Abort"),
            "installer waits on the exact process handle and aborts before replacement on timeout");
        check(!script.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase),
            "installer PID arguments are never interpolated into elevated shell code");
        check(wait.Contains("IntFmt $0 \"%u\" $UpdatePid") && wait.Contains("$0 != $UpdatePid") &&
              wait.Contains("$0 > 10") && wait.Contains("$0 == \"0\""),
            "installer accepts only a positive canonical decimal process ID within a DWORD");

        var section = script.IndexOf("Section \"${APP_NAME}\" SecMain", StringComparison.Ordinal);
        var waitCall = script.IndexOf("Call WaitForUpdatingProcess", section, StringComparison.Ordinal);
        var firstFile = script.IndexOf("File /oname", section, StringComparison.Ordinal);
        check(section >= 0 && waitCall > section && firstFile > waitCall,
            "installer waits before writing the running application executable");
        check(skipPage.Contains("$IsUpdate") && skipPage.Contains("Abort") &&
              script.Contains("!insertmacro MUI_PAGE_INSTFILES") && initializer.Contains("SetAutoClose true") &&
              !initializer.Contains("SetSilent silent", StringComparison.OrdinalIgnoreCase),
            "updates retain visible native installation progress and close after completion");
        check(!Regex.IsMatch(script, @"(?im)^\s*(?:Delete|RMDir)\s+[^\r\n]*\$(?:APPDATA|LOCALAPPDATA)"),
            "installer and uninstaller preserve per-user settings directories");
        return Task.CompletedTask;
    }
}

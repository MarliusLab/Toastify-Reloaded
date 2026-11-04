using Microsoft.Win32;
using System.IO;
using System.Security;
using Velopack.Locators;

namespace ToastifyReloaded.Services;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ToastifyReloaded";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);

        if (enabled)
        {
            var executable = GetExecutablePath();
            key.SetValue(ValueName, $"\"{executable}\" --minimized");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    // Refresh only an already-enabled entry after NSIS -> MSI migration; never
    // enable startup for a user who disabled it or create another preference.
    public static void RefreshEnabledCommand()
    {
        try { if (IsEnabled()) SetEnabled(true); }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
        catch (IOException) { }
    }

    internal static string ResolveExecutablePath(string currentExecutable, string? installedRoot,
        bool isVelopackInstalled, Func<string, bool> fileExists)
    {
        if (isVelopackInstalled && !string.IsNullOrWhiteSpace(installedRoot))
        {
            var stub = Path.Combine(installedRoot, "ToastifyReloaded.exe");
            if (fileExists(stub)) return stub;
        }
        return currentExecutable;
    }

    private static string GetExecutablePath()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Percorso dell'eseguibile non disponibile.");
        var locator = VelopackLocator.Current;
        return ResolveExecutablePath(executable, locator.RootAppDir,
            locator.CurrentlyInstalledVersion is not null, File.Exists);
    }
}

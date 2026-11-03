using System.IO;

namespace ToastifyReloaded.Services;

/// <summary>Finds installed CLIs without depending on the app's inherited PATH.</summary>
public sealed class WindowsExecutableLocator
{
    private readonly Func<IEnumerable<string>> _directories;
    private readonly Func<string, bool> _exists;

    public WindowsExecutableLocator()
        : this(GetSpicetifyDirectories, File.Exists)
    {
    }

    public WindowsExecutableLocator(Func<IEnumerable<string>> directories, Func<string, bool> exists)
    {
        _directories = directories;
        _exists = exists;
    }

    public IReadOnlyList<string> FindSpicetifyCandidates()
    {
        var results = new List<string>();
        foreach (var directory in _directories())
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
                if (!Path.IsPathFullyQualified(expanded))
                    continue;
                var candidate = Path.GetFullPath(Path.Combine(expanded, "spicetify.exe"));
                if (_exists(candidate) && !results.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    results.Add(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // One stale/unreadable PATH entry must not hide another install.
            }
        }
        return results;
    }

    private static IEnumerable<string> GetSpicetifyDirectories()
    {
        // Read persistent PATH values on every probe: an installer can update
        // them while Toastify is already running with an older environment.
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? path;
            try { path = Environment.GetEnvironmentVariable("PATH", target); }
            catch { continue; }
            foreach (var directory in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                yield return directory;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(local, "spicetify"); // Official install.ps1 location.
        yield return Path.Combine(roaming, "spicetify");
        yield return Path.Combine(home, "spicetify-cli"); // Legacy official installer.
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links");
        yield return Path.Combine(programFiles, "WinGet", "Links");

        foreach (var root in new[] { Path.Combine(local, "Microsoft", "WinGet", "Packages"), Path.Combine(programFiles, "WinGet", "Packages") })
        {
            foreach (var package in EnumerateDirectories(root, "Spicetify.Spicetify_*"))
            {
                yield return package;
                foreach (var child in EnumerateDirectories(package, "*"))
                    yield return child;
            }
        }

        foreach (var scoop in new[] { Environment.GetEnvironmentVariable("SCOOP"), Path.Combine(home, "scoop"), Environment.GetEnvironmentVariable("SCOOP_GLOBAL"), Path.Combine(programData, "scoop") })
        {
            if (string.IsNullOrWhiteSpace(scoop)) continue;
            yield return Path.Combine(scoop, "shims");
            yield return Path.Combine(scoop, "apps", "spicetify-cli", "current");
        }
        var chocolatey = Environment.GetEnvironmentVariable("ChocolateyInstall");
        yield return Path.Combine(string.IsNullOrWhiteSpace(chocolatey) ? Path.Combine(programData, "chocolatey") : chocolatey, "bin");
    }

    private static IEnumerable<string> EnumerateDirectories(string root, string pattern)
    {
        try { return Directory.EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly).Take(16).ToArray(); }
        catch { return Array.Empty<string>(); }
    }
}

namespace ToastifyReloaded.Shared;

/// <summary>Normalizes the Windows line's current and legacy GitHub tag spelling.</summary>
public static class WindowsReleaseVersion
{
    public static string Normalize(string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith("v.", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        else if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];

        var dash = value.IndexOf('-');
        if (dash >= 0)
            value = value[..dash];

        if (Version.TryParse(value, out var parsed))
            return $"{parsed.Major}.{parsed.Minor}.{Math.Max(parsed.Build, 0)}";

        return value;
    }
}

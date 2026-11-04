namespace ToastifyReloaded.Shared;

/// <summary>Preserves saved preferences while preventing an automatic retry loop after updater restarts.</summary>
public static class UpdateSessionPolicy
{
    public static bool SuppressAutomaticInstall { get; private set; }
    public static void MarkUpdaterRestart() => SuppressAutomaticInstall = true;
}

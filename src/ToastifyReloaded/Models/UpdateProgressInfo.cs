namespace ToastifyReloaded.Models;

public enum UpdatePhase
{
    Downloading,
    Verifying,
    WaitingForPermission,
    LaunchingInstaller,
    LaunchingUpdater
}

/// <summary>Download/preparation progress and the external update handoff state.</summary>
public sealed record UpdateProgressInfo(
    UpdatePhase Phase,
    long BytesReceived = 0,
    long? TotalBytes = null,
    double? PercentOverride = null,
    bool IsPackageUpdate = false)
{
    public double? Percent => Phase != UpdatePhase.Downloading ? null
        : PercentOverride is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 100)
        : TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value * 100, 0, 100)
        : null;

    public bool CanCancel => Phase is UpdatePhase.Downloading or UpdatePhase.Verifying;
}

/// <summary>Windows rejected the elevation request because the user canceled UAC.</summary>
public sealed class UpdatePermissionCanceledException : OperationCanceledException
{
    public UpdatePermissionCanceledException(Exception innerException)
        : base("Aggiornamento annullato nella richiesta di autorizzazione di Windows (UAC).", innerException)
    {
    }
}

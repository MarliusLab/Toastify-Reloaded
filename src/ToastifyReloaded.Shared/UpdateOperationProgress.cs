namespace ToastifyReloaded.Shared;

public enum UpdateOperationStage
{
    Checking, Downloading, Verifying, Preparing, WaitingForExit,
    WaitingForPermission, Installing, Ready, Completed, Cancelled, Failed
}

public sealed record UpdateOperationProgress(
    UpdateOperationStage Stage,
    long ReceivedBytes = 0,
    long? TotalBytes = null,
    string? Message = null,
    double? PercentOverride = null)
{
    public double? Percent => PercentOverride ?? (TotalBytes is > 0
        ? Math.Clamp(100d * Math.Max(0, ReceivedBytes) / TotalBytes.Value, 0, 100)
        : null);
}

public sealed record ProcessCommandResult(int ExitCode, string StdOut, string StdErr);

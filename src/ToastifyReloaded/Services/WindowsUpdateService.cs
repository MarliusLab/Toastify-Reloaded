using ToastifyReloaded.Models;

namespace ToastifyReloaded.Services;

/// <summary>Keeps legacy NSIS installations working while packaged installations use Velopack.</summary>
public sealed class WindowsUpdateService
{
    private readonly IVelopackUpdateBackend _backend;
    private readonly UpdateService _legacy;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private UpdateCheckResult? _checkedResult;
    private VelopackUpdatePackage? _checkedPackage;

    public WindowsUpdateService(UpdateService? legacy = null, IVelopackUpdateBackend? backend = null)
    {
        _legacy = legacy ?? new UpdateService();
        _backend = backend ?? new VelopackUpdateService();
    }

    public bool IsVelopackInstalled => _backend.IsInstalled;
    public string CurrentVersion => IsVelopackInstalled ? _backend.CurrentVersion : _legacy.CurrentVersion;
    public string Channel => _backend.Channel;

    public async Task<UpdateCheckResult> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        if (!IsVelopackInstalled) return await _legacy.CheckLatestAsync(cancellationToken).ConfigureAwait(false);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _checkedPackage = null;
            _checkedResult = null;
            var update = await _backend.CheckAsync(cancellationToken).ConfigureAwait(false);
            _checkedPackage = update;
            return _checkedResult = new(true, update is not null, CurrentVersion, update?.Version ?? CurrentVersion,
                update is null ? null : "v" + update.Version, update?.AssetName, null,
                update is null ? VelopackUpdateService.RepositoryUrl + "/releases" :
                    VelopackUpdateService.RepositoryUrl + "/releases/tag/v" + update.Version,
                update is null ? $"Toastify Reloaded è aggiornato ({CurrentVersion})." :
                    $"È disponibile Toastify Reloaded {update.Version}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(false, false, CurrentVersion, string.Empty, null, null, null,
                VelopackUpdateService.RepositoryUrl + "/releases", $"Controllo aggiornamenti non riuscito: {ex.Message}");
        }
        finally { _operation.Release(); }
    }

    public async Task<bool> PrepareAndLaunchUpdateAsync(UpdateCheckResult update, int currentProcessId,
        bool restartMinimized = false, CancellationToken cancellationToken = default,
        IProgress<UpdateProgressInfo>? progress = null)
    {
        if (!IsVelopackInstalled)
            return await _legacy.PrepareAndLaunchUpdateAsync(update, currentProcessId, restartMinimized,
                cancellationToken, progress).ConfigureAwait(false);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(update, _checkedResult) || !update.Success || !update.UpdateAvailable || _checkedPackage is null)
                return false;
            await _backend.DownloadAsync(_checkedPackage, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(UpdatePhase.WaitingForPermission, IsPackageUpdate: true));
            var launched = await _backend.ApplyAndRestartAsync(_checkedPackage, restartMinimized,
                cancellationToken).ConfigureAwait(false);
            if (launched)
            {
                _checkedResult = null;
                _checkedPackage = null;
                try { progress?.Report(new(UpdatePhase.LaunchingUpdater, IsPackageUpdate: true)); }
                catch (Exception) { /* The observer cannot undo a completed handoff. */ }
            }
            return launched;
        }
        finally { _operation.Release(); }
    }
}

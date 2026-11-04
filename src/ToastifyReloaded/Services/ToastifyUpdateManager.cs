using System.IO;
using System.Security.Cryptography;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace ToastifyReloaded.Services;

internal sealed record ReconstructedPackageReceipt(string AssetName, string SHA256, long Size);

/// <summary>Keeps SDK delta selection/fallback, adding cancellable native patching and final verification.</summary>
internal sealed class ToastifyUpdateManager : UpdateManager
{
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<PowerShellCommandResult>> _patch;
    public ReconstructedPackageReceipt? ReconstructedPackage { get; private set; }

    public ToastifyUpdateManager(IUpdateSource source, UpdateOptions options, IVelopackLocator? locator = null,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<PowerShellCommandResult>>? patch = null)
        : base(source, options, locator)
    {
        // This is the existing generic hidden-process runner: ArgumentList,
        // cancellation, bounded tree-kill/pipe drain, and capped diagnostic text.
        _patch = patch ?? ((file, args, token) => PowerShellService.RunHiddenExecutableAsync(
            file, args, TimeSpan.FromMinutes(5), token));
    }

    public override async Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress = null,
        CancellationToken cancelToken = default)
    {
        ReconstructedPackage = null;
        await base.DownloadUpdatesAsync(updates, progress, cancelToken).ConfigureAwait(false);
    }

    protected override async Task DownloadAndApplyDeltaUpdates(UpdateInfo updates, string targetFile,
        Action<int> progress, CancellationToken cancelToken)
    {
        var deltas = updates.DeltasToTarget.OrderBy(d => d.Version).ToArray();
        var helper = Locator.UpdateExePath;
        if (string.IsNullOrEmpty(helper) || !File.Exists(helper))
            throw new FileNotFoundException("Update.exe non disponibile per preparare il pacchetto delta.", helper);
        if (updates.BaseRelease is null || deltas.Length == 0)
            throw new InvalidDataException("Base del pacchetto delta non disponibile.");

        for (var index = 0; index < deltas.Length; index++)
        {
            cancelToken.ThrowIfCancellationRequested();
            var delta = deltas[index];
            var localDelta = GetLocalPackagePath(delta);
            var completed = index;
            await Source.DownloadReleaseEntry(Log, delta, localDelta,
                value => progress((int)((completed + Math.Clamp(value, 0, 100) / 100d) / deltas.Length * 70)),
                cancelToken).ConfigureAwait(false);
            await VerifyPackageChecksumAsync(delta, localDelta).ConfigureAwait(false);
        }

        cancelToken.ThrowIfCancellationRequested();
        var arguments = new List<string>
        {
            "patch", "--old", GetLocalPackagePath(updates.BaseRelease), "--output", targetFile
        };
        if (Locator.RootAppDir is { Length: > 0 } root)
        {
            arguments.Add("--rootDir");
            arguments.Add(root);
        }
        foreach (var delta in deltas)
        {
            arguments.Add("--delta");
            arguments.Add(GetLocalPackagePath(delta));
        }

        var result = await _patch(helper, arguments, cancelToken).ConfigureAwait(false);
        cancelToken.ThrowIfCancellationRequested();
        if (result.ExitCode == -1)
            throw new TimeoutException("Preparazione del pacchetto delta scaduta per timeout.");
        if (!result.Success)
            throw new InvalidDataException($"Preparazione del pacchetto delta non riuscita ({result.ExitCode}).");

        // Native patch checks each reconstructed file against the checksums in
        // the verified delta. Repacked ZIP bytes intentionally differ from the
        // published full ZIP, so its download hash cannot verify this result.
        cancelToken.ThrowIfCancellationRequested();
        await using (var stream = new FileStream(targetFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                         64 * 1024, useAsync: true))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancelToken).ConfigureAwait(false));
            ReconstructedPackage = new(updates.TargetFullRelease.FileName, hash, stream.Length);
        }
        progress(100);
    }

    private string GetLocalPackagePath(VelopackAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.FileName) || asset.FileName != Path.GetFileName(asset.FileName) ||
            asset.FileName.Contains('\\') || asset.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Percorso del pacchetto delta non valido.");
        return Path.Combine(Locator.PackagesDir ?? throw new InvalidOperationException("Cache aggiornamenti non disponibile."),
            asset.FileName);
    }
}

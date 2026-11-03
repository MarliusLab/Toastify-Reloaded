using System.Diagnostics;
using ToastifyReloaded.Native;
using ToastifyReloaded.Shared;

namespace ToastifyReloaded.Services;

public enum SpotifyTrackOpenResult
{
    TrackOpened,
    AppActivated,
    Failed
}

/// <summary>Opens a trusted track link or activates Spotify without changing playback.</summary>
public sealed class SpotifyTrackOpenService
{
    private readonly Func<string, CancellationToken, Task<bool>> _openUri;
    private readonly Func<CancellationToken, Task<bool>> _activateExisting;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _attemptTimeout;

    public SpotifyTrackOpenService()
        : this(OpenProtocolAsync, ActivateExistingSpotifyAsync)
    {
    }

    public SpotifyTrackOpenService(
        Func<string, CancellationToken, Task<bool>> openUri,
        Func<CancellationToken, Task<bool>> activateExisting,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? attemptTimeout = null)
    {
        _openUri = openUri;
        _activateExisting = activateExisting;
        _delay = delay ?? Task.Delay;
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(3);
        if (_attemptTimeout <= TimeSpan.Zero || _attemptTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
    }

    public async Task<SpotifyTrackOpenResult> OpenAsync(
        string? trackUri = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            return await OpenCoreAsync(trackUri, linked.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SpotifyTrackOpenResult.Failed;
        }
    }

    private async Task<SpotifyTrackOpenResult> OpenCoreAsync(
        string? trackUri,
        CancellationToken operationToken,
        CancellationToken userCancellationToken)
    {
        foreach (var target in SpotifyTrackLink.GetOpenTargets(trackUri))
        {
            if (!await TryAsync(token => _openUri(target, token), operationToken).ConfigureAwait(false))
                continue;
            // An HTTPS fallback belongs to the browser/web player; focusing a
            // different desktop window would hide the track just opened there.
            if (target.StartsWith("spotify:", StringComparison.Ordinal))
                await FocusAfterLaunchAsync(operationToken, userCancellationToken).ConfigureAwait(false);
            return SpotifyTrackOpenResult.TrackOpened;
        }

        // SMTC normally supplies labels without a trustworthy catalog ID.
        // Never manufacture an ID or turn labels/metadata into a search URL.
        if (await TryAsync(_activateExisting, operationToken).ConfigureAwait(false))
            return SpotifyTrackOpenResult.AppActivated;

        if (!await TryAsync(token => _openUri("spotify:", token), operationToken).ConfigureAwait(false))
            return SpotifyTrackOpenResult.Failed;

        await FocusAfterLaunchAsync(operationToken, userCancellationToken).ConfigureAwait(false);
        // ShellExecute can hand off to an existing app without returning a new
        // Process object. Successful protocol handoff is still an activation.
        return SpotifyTrackOpenResult.AppActivated;
    }

    private async Task FocusAfterLaunchAsync(CancellationToken cancellationToken, CancellationToken userCancellationToken)
    {
        try
        {
            // Allow the protocol handler time to create or restore its window.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0)
                    await _delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken).ConfigureAwait(false);
                if (await TryAsync(_activateExisting, cancellationToken).ConfigureAwait(false))
                    return;
            }
        }
        catch (OperationCanceledException) when (!userCancellationToken.IsCancellationRequested)
        {
            // A focus-budget timeout does not undo an accepted shell handoff.
        }
    }

    private async Task<bool> TryAsync(
        Func<CancellationToken, Task<bool>> operation,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_attemptTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var operationToken = linked.Token;
        try
        {
            // Native shell/window APIs must not block the toast's UI thread.
            // Cancellation bounds our wait; Windows ShellExecute itself cannot
            // retract a navigation request after it has accepted that request.
            return await Task.Run(() => operation(operationToken), operationToken)
                .WaitAsync(operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return false;
        }
    }

    private static Task<bool> OpenProtocolAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Accept exactly the canonical targets generated by the shared helper.
        if (target != "spotify:" && !SpotifyTrackLink.GetOpenTargets(target).Contains(target, StringComparer.Ordinal))
            return Task.FromResult(false);
        using var launched = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return Task.FromResult(true);
    }

    private static async Task<bool> ActivateExistingSpotifyAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return false;
        cancellationToken.ThrowIfCancellationRequested();
        var processes = Process.GetProcessesByName("Spotify");
        try
        {
            var processIds = processes.Select(p => (uint)p.Id).ToHashSet();
            if (processIds.Count == 0) return false;
            var handles = new List<IntPtr>();
            foreach (var process in processes)
            {
                try
                {
                    var handle = process.MainWindowHandle;
                    if (handle != IntPtr.Zero && !handles.Contains(handle)) handles.Add(handle);
                }
                catch { }
            }
            // Store/Desktop Spotify can have several helper processes, and
            // MainWindowHandle can be zero or stale. Find their top-level windows.
            NativeMethods.EnumWindows((handle, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(handle, out var processId);
                if (processIds.Contains(processId) && NativeMethods.GetWindow(handle, NativeMethods.GW_OWNER) == IntPtr.Zero &&
                    (NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle)) && !handles.Contains(handle))
                    handles.Add(handle);
                return true;
            }, IntPtr.Zero);

            foreach (var handle in handles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!NativeMethods.IsWindow(handle)) continue;
                NativeMethods.ShowWindowAsync(handle,
                    NativeMethods.IsIconic(handle) ? NativeMethods.SW_RESTORE : NativeMethods.SW_SHOW);
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var foreground = NativeMethods.SetForegroundWindow(handle) || NativeMethods.GetForegroundWindow() == handle;
                    if (foreground && !NativeMethods.IsIconic(handle)) return true;
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
            return false;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }
}

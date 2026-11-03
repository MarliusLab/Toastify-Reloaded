using System.Diagnostics;
using System.Runtime.InteropServices;
using ToastifyReloaded.Native;
using ToastifyReloaded.Shared;

namespace ToastifyReloaded.Services;

public enum SpotifyTrackOpenResult
{
    TrackOpened,
    AppActivated,
    Failed,
    NowPlayingRequested
}

public readonly record struct SpotifyShortcutInput(ushort VirtualKey, bool KeyUp);

/// <summary>Keyboard and live HWND checks, separated for tests without real input.</summary>
public interface ISpotifyNowPlayingInput
{
    IntPtr CaptureForegroundSpotifyWindow();
    bool IsSameSpotifyForegroundWindow(IntPtr window);
    bool IsKeyDown(ushort virtualKey);
    uint Send(IReadOnlyList<SpotifyShortcutInput> inputs);
}

/// <summary>Opens a trusted track link or activates Spotify without changing playback.</summary>
public sealed class SpotifyTrackOpenService
{
    private readonly Func<string, CancellationToken, Task<bool>> _openUri;
    private readonly Func<CancellationToken, Task<bool>> _activateExisting;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _attemptTimeout;
    private readonly ISpotifyNowPlayingInput _nowPlayingInput;
    private static readonly ushort[] ShortcutGuardKeys =
    {
        NativeMethods.VK_MENU, NativeMethods.VK_SHIFT, NativeMethods.VK_CONTROL,
        NativeMethods.VK_LWIN, NativeMethods.VK_RWIN, NativeMethods.VK_J,
        NativeMethods.VK_RETURN, NativeMethods.VK_SPACE, NativeMethods.VK_LBUTTON, NativeMethods.VK_RBUTTON
    };
    private static readonly SpotifyShortcutInput[] NowPlayingShortcut =
    {
        new(NativeMethods.VK_MENU, false), new(NativeMethods.VK_SHIFT, false), new(NativeMethods.VK_J, false),
        new(NativeMethods.VK_J, true), new(NativeMethods.VK_SHIFT, true), new(NativeMethods.VK_MENU, true)
    };

    public SpotifyTrackOpenService()
        : this(OpenProtocolAsync, ActivateExistingSpotifyAsync)
    {
    }

    public SpotifyTrackOpenService(
        Func<string, CancellationToken, Task<bool>> openUri,
        Func<CancellationToken, Task<bool>> activateExisting,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? attemptTimeout = null,
        ISpotifyNowPlayingInput? nowPlayingInput = null)
    {
        _openUri = openUri;
        _activateExisting = activateExisting;
        _delay = delay ?? Task.Delay;
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(3);
        _nowPlayingInput = nowPlayingInput ?? new NativeNowPlayingInput();
        if (_attemptTimeout <= TimeSpan.Zero || _attemptTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
    }

    /// <summary>
    /// Requests Spotify's native Now Playing command. The caller must suspend
    /// Toastify registrations of Alt+Shift+J for the complete call to avoid
    /// recapturing our synthetic shortcut. This does not navigate to a track URI.
    /// </summary>
    public async Task<SpotifyTrackOpenResult> OpenNowPlayingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            // Keyboard/window operations never execute on the WPF UI thread.
            // Cancellation is observed by the core only after key-up cleanup;
            // it must not cause an early return between a partial send and cleanup.
            return await Task.Run(() => OpenNowPlayingCoreAsync(linked.Token, cancellationToken), linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SpotifyTrackOpenResult.Failed;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return SpotifyTrackOpenResult.Failed;
        }
    }

    private async Task<SpotifyTrackOpenResult> OpenNowPlayingCoreAsync(
        CancellationToken operationToken,
        CancellationToken userCancellationToken)
    {
        operationToken.ThrowIfCancellationRequested();
        // A shortcut, Enter/Space key-down or mouse-down callback can run before
        // its physical trigger is released. Moving focus sooner could send a
        // repeated Space/Enter or the mouse-up into Spotify. Guard both physical
        // mouse buttons because Windows can swap their logical mapping.
        // Never release keys/buttons that were already held by the user.
        if (!await WaitForShortcutReleaseAsync(operationToken).ConfigureAwait(false))
            return SpotifyTrackOpenResult.Failed;

        var launched = false;
        if (!await TryAsync(_activateExisting, operationToken).ConfigureAwait(false))
        {
            if (!await TryAsync(token => _openUri("spotify:", token), operationToken).ConfigureAwait(false))
                return SpotifyTrackOpenResult.Failed;
            launched = true;
        }

        try
        {
            if (launched)
                await FocusAfterLaunchAsync(operationToken, userCancellationToken).ConfigureAwait(false);
            if (!await WaitForShortcutReleaseAsync(operationToken).ConfigureAwait(false))
                return SpotifyTrackOpenResult.AppActivated;

            var window = _nowPlayingInput.CaptureForegroundSpotifyWindow();
            // Check live modifier state and the same verified HWND immediately
            // before the single main SendInput batch. Windows exposes no atomic
            // foreground-check-plus-SendInput primitive: the remaining TOCTOU race
            // is checked again after the send and never treated as confirmed success.
            if (window == IntPtr.Zero || !ShortcutKeysReleased() ||
                !_nowPlayingInput.IsSameSpotifyForegroundWindow(window))
                return SpotifyTrackOpenResult.AppActivated;
            operationToken.ThrowIfCancellationRequested();

            uint inserted = 0;
            try
            {
                inserted = _nowPlayingInput.Send(NowPlayingShortcut);
            }
            finally
            {
                if (inserted > 0 && inserted < NowPlayingShortcut.Length)
                    ReleasePartialShortcut(inserted);
            }

            operationToken.ThrowIfCancellationRequested();
            return inserted == NowPlayingShortcut.Length && _nowPlayingInput.IsSameSpotifyForegroundWindow(window)
                ? SpotifyTrackOpenResult.NowPlayingRequested
                : SpotifyTrackOpenResult.AppActivated;
        }
        catch (OperationCanceledException) when (!userCancellationToken.IsCancellationRequested)
        {
            return SpotifyTrackOpenResult.AppActivated;
        }
        catch (OperationCanceledException) { throw; }
        catch { return SpotifyTrackOpenResult.AppActivated; }
    }

    private bool ShortcutKeysReleased() => ShortcutGuardKeys.All(key => !_nowPlayingInput.IsKeyDown(key));

    private async Task<bool> WaitForShortcutReleaseAsync(CancellationToken cancellationToken)
    {
        using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, releaseTimeout.Token);
        try
        {
            // The iteration cap also makes injected test clocks deterministic.
            for (var poll = 0; poll < 60; poll++)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (ShortcutKeysReleased()) return true;
                await _delay(TimeSpan.FromMilliseconds(25), linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            }
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void ReleasePartialShortcut(uint inserted)
    {
        var pressed = new List<ushort>();
        foreach (var input in NowPlayingShortcut.Take((int)inserted))
        {
            if (input.KeyUp) pressed.Remove(input.VirtualKey);
            else if (!pressed.Contains(input.VirtualKey)) pressed.Add(input.VirtualKey);
        }
        var releases = pressed.AsEnumerable().Reverse().Select(key => new SpotifyShortcutInput(key, true)).ToArray();
        // No cancellation check and no refocus here. A partial send owns these
        // synthetic key-downs; key-up-only cleanup must run even if focus moved
        // or the caller canceled. Never send another command/key-down to that app.
        for (var attempt = 0; attempt < 3 && releases.Length > 0; attempt++)
        {
            try
            {
                var released = _nowPlayingInput.Send(releases);
                if (released > releases.Length) return;
                releases = releases.Skip((int)released).ToArray();
            }
            catch { return; }
        }
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

    private sealed class NativeNowPlayingInput : ISpotifyNowPlayingInput
    {
        public IntPtr CaptureForegroundSpotifyWindow()
        {
            if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
            var window = NativeMethods.GetForegroundWindow();
            if (window == IntPtr.Zero || !NativeMethods.IsWindow(window) || NativeMethods.IsIconic(window))
                return IntPtr.Zero;
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                return !process.HasExited && process.ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase)
                    ? window : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        public bool IsSameSpotifyForegroundWindow(IntPtr window)
            => window != IntPtr.Zero && CaptureForegroundSpotifyWindow() == window;

        public bool IsKeyDown(ushort virtualKey)
            => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        public uint Send(IReadOnlyList<SpotifyShortcutInput> inputs)
        {
            var native = inputs.Select(input => new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = input.VirtualKey,
                        dwFlags = input.KeyUp ? NativeMethods.KEYEVENTF_KEYUP : 0
                    }
                }
            }).ToArray();
            return NativeMethods.SendInput((uint)native.Length, native, Marshal.SizeOf<NativeMethods.INPUT>());
        }
    }
}

using ToastifyReloaded.Shared;
using System.Runtime.InteropServices;

namespace ToastifyReloaded.Mac.Services;

public sealed record SpotifyTrackOpenResult(bool Success, bool OpenedTrack, string Message);

/// <summary>Opens only canonical Spotify track targets; metadata is never executable text.</summary>
public sealed class SpotifyTrackOpenService
{
    public const string LyricsPlusUri = "spotify:app:lyrics-plus";
    private readonly Func<string, IEnumerable<string>, CancellationToken,
        Task<(int ExitCode, string StdOut, string StdErr)>> _run;
    private readonly Func<CancellationToken, Task<bool>> _waitForInputRelease;
    private readonly Func<CancellationToken, Task<bool>> _waitForHotkeyInputRelease;

    public SpotifyTrackOpenService(ProcessService process)
        : this(process.RunAsync)
    {
    }

    internal SpotifyTrackOpenService(Func<string, IEnumerable<string>, CancellationToken,
        Task<(int ExitCode, string StdOut, string StdErr)>> run,
        Func<CancellationToken, Task<bool>>? waitForInputRelease = null,
        Func<CancellationToken, Task<bool>>? waitForHotkeyInputRelease = null)
    {
        _run = run;
        _waitForInputRelease = waitForInputRelease ?? WaitForInputReleaseAsync;
        _waitForHotkeyInputRelease = waitForHotkeyInputRelease ?? WaitForHotkeyInputReleaseAsync;
    }

    public Task<SpotifyTrackOpenResult> OpenLyricsPlusAsync(CancellationToken cancellationToken = default)
        => OpenLyricsPlusCoreAsync(_waitForInputRelease, cancellationToken);

    public Task<SpotifyTrackOpenResult> OpenLyricsPlusFromHotkeyAsync(CancellationToken cancellationToken = default)
        => OpenLyricsPlusCoreAsync(_waitForHotkeyInputRelease, cancellationToken);

    private async Task<SpotifyTrackOpenResult> OpenLyricsPlusCoreAsync(
        Func<CancellationToken, Task<bool>> waitForRelease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            if (!await waitForRelease(linked.Token).WaitAsync(linked.Token))
                return new(false, false, "LyricsPlusOpenFailed");
            if (await TryOpenAsync(linked.Token, "-a", "Spotify", LyricsPlusUri))
                return new(true, false, "LyricsPlusRequested");
            if (!await waitForRelease(linked.Token).WaitAsync(linked.Token))
                return new(false, false, "LyricsPlusOpenFailed");
            return await TryOpenAsync(linked.Token, "-a", "Spotify")
                ? new(true, false, "LyricsPlusActivatedFallback")
                : new(false, false, "LyricsPlusOpenFailed");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, false, "LyricsPlusOpenFailed"); }
        catch (OperationCanceledException) { throw; }
        catch { return new(false, false, "LyricsPlusOpenFailed"); }
    }

    public async Task<SpotifyTrackOpenResult> OpenNowPlayingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            if (!await _waitForInputRelease(linked.Token).WaitAsync(linked.Token))
                return new(false, false, "NowPlayingFailed");
            if (!await TryOpenAsync(linked.Token, "-a", "Spotify"))
                return new(false, false, "NowPlayingFailed");
            if (!await _waitForInputRelease(linked.Token).WaitAsync(linked.Token))
                return new(true, false, "SpotifyActivated");
            const string script = "tell application \"System Events\"\nif exists process \"Spotify\" then\ntell process \"Spotify\"\nif frontmost then\nkeystroke \"j\" using {option down, shift down}\nreturn \"requested\"\nend if\nend tell\nend if\nend tell\nreturn \"not-foreground\"";
            var result = await RunBoundedAsync("/usr/bin/osascript", new[] { "-e", script }, linked.Token);
            return result.ExitCode == 0 && result.StdOut.Trim() == "requested"
                ? new(true, false, "NowPlayingRequested")
                : new(false, false, "NowPlayingFailed");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, false, "NowPlayingFailed"); }
        catch (OperationCanceledException) { throw; }
        catch { return new(false, false, "NowPlayingFailed"); }
    }

    public async Task<SpotifyTrackOpenResult> OpenAsync(string? trackUri)
    {
        var targets = SpotifyTrackLink.GetOpenTargets(trackUri);
        if (targets.Count > 0)
        {
            if (await TryOpenAsync("-a", "Spotify", targets[0]) ||
                await TryOpenAsync(targets[1]))
                return new(true, true, "TrackOpened");
        }

        // Missing, local or unsupported media never becomes a title search or an
        // arbitrary URL. Activating Spotify also avoids changing playback.
        if (await TryOpenAsync("-a", "Spotify"))
            return new(true, false, "SpotifyActivated");

        return new(false, false, "TrackOpenFailed");
    }

    private async Task<bool> TryOpenAsync(params string[] arguments)
        => await TryOpenAsync(CancellationToken.None, arguments);

    private async Task<bool> TryOpenAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        try
        {
            var result = await RunBoundedAsync("/usr/bin/open", arguments, cancellationToken);
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Missing Spotify, a rejected handler and launcher errors all take
            // the same bounded fallback path without crashing the toast.
            return false;
        }
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunBoundedAsync(
        string file, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        return await Task.Run(() => _run(file, arguments, linked.Token), linked.Token)
            .WaitAsync(linked.Token).ConfigureAwait(false);
    }

    private static Task<bool> WaitForInputReleaseAsync(CancellationToken cancellationToken)
        => WaitForInputReleaseAsync(cancellationToken, allowHeldModifiers: false);

    private static Task<bool> WaitForHotkeyInputReleaseAsync(CancellationToken cancellationToken)
        => WaitForInputReleaseAsync(cancellationToken, allowHeldModifiers: true);

    private static Task<bool> WaitForInputReleaseAsync(CancellationToken cancellationToken, bool allowHeldModifiers)
        => Task.Run(async () =>
        {
            if (!OperatingSystem.IsMacOS()) return true;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var held = HasBlockingInput(key => CGEventSourceKeyState(0, key),
                    button => CGEventSourceButtonState(0, button), allowHeldModifiers);
                if (!held) return true;
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            return false;
        }, cancellationToken);

    internal static bool HasBlockingInput(Func<ushort, bool> keyDown, Func<uint, bool> mouseDown, bool allowHeldModifiers)
    {
        // macOS virtual-key codes 0x36..0x3f are Command, Shift, Caps Lock,
        // Option, Control (including right variants) and Fn. Never release or synthesize them.
        return Enumerable.Range(0, 128).Any(key => !(allowHeldModifiers && key is >= 0x36 and <= 0x3f) && keyDown((ushort)key)) ||
            Enumerable.Range(0, 5).Any(button => mouseDown((uint)button));
    }

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGEventSourceKeyState(int state, ushort key);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGEventSourceButtonState(int state, uint button);
}

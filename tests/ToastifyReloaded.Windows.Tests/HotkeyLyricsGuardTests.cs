using System.Collections.Concurrent;
using ToastifyReloaded.Services;

namespace ToastifyReloaded.Windows.Tests;

internal static class HotkeyLyricsGuardTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var modifier in new ushort[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
        {
            var input = new Input(); input.Hold(modifier);
            var uriCalls = 0; var focuses = 0;
            var service = Create(input, (_, _) => { uriCalls++; return Task.FromResult(true); },
                _ => { focuses++; return Task.FromResult(true); });
            check(await service.OpenLyricsPlusFromHotkeyAsync() == SpotifyTrackOpenResult.LyricsPlusRequested &&
                  input.IsKeyDown(modifier) && uriCalls == 1 && focuses == 1 && input.Sends == 0,
                "Show Toast Lyrics allows a held modifier without releasing/injecting it: " + modifier);
        }
        foreach (var trigger in new ushort[] { 0x26, 0x54, 0x0D, 0x20, 0x01, 0x02, 0x05 })
        {
            var input = new Input(); input.Hold(0x11); input.Hold(trigger);
            var calls = 0;
            var service = Create(input, (_, _) => { calls++; return Task.FromResult(true); },
                _ => { calls++; return Task.FromResult(true); });
            check(await service.OpenLyricsPlusFromHotkeyAsync() == SpotifyTrackOpenResult.Failed && calls == 0 && input.Sends == 0,
                "hotkey Lyrics still refuses a held primary key or mouse button before URI/focus: " + trigger);
        }
        {
            var input = new Input(); input.Hold(0x11); input.Hold(0x26);
            var calls = 0; var polls = 0;
            var service = Create(input, (_, _) => { calls++; return Task.FromResult(true); },
                _ => { calls++; return Task.FromResult(true); }, (_, token) =>
                { token.ThrowIfCancellationRequested(); polls++; input.Release(0x26); return Task.CompletedTask; });
            check(await service.OpenLyricsPlusFromHotkeyAsync() == SpotifyTrackOpenResult.LyricsPlusRequested &&
                  polls > 0 && calls == 2 && input.IsKeyDown(0x11) && !input.IsKeyDown(0x26),
                "hotkey Lyrics waits for the primary key then proceeds while Ctrl remains held");
        }
        {
            var input = new Input(); input.Hold(0x11);
            var calls = 0;
            var service = Create(input, (_, _) => { calls++; return Task.FromResult(true); },
                _ => { calls++; return Task.FromResult(true); });
            check(await service.OpenLyricsPlusAsync() == SpotifyTrackOpenResult.Failed && calls == 0,
                "ordinary click/Enter Lyrics retains its strict modifier-release guard");
        }
        {
            var input = new Input(); input.Hold(0x11);
            var focuses = 0;
            var service = Create(input, (_, _) => { input.Hold(0x20); return Task.FromResult(true); },
                _ => { focuses++; return Task.FromResult(true); });
            check(await service.OpenLyricsPlusFromHotkeyAsync() == SpotifyTrackOpenResult.LyricsPlusRequested && focuses == 0,
                "an accepted hotkey URI never forces focus while a newly pressed primary key remains held");
        }
        {
            var input = new Input(); input.Hold(0x11);
            var calls = 0;
            var service = Create(input, (_, _) => { calls++; return Task.FromResult(true); },
                _ => { calls++; return Task.FromResult(true); });
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await service.OpenLyricsPlusFromHotkeyAsync(cancelled.Token); check(false, "canceled hotkey request must throw"); }
            catch (OperationCanceledException)
            { check(calls == 0 && input.Reads == 0, "pre-canceled hotkey Lyrics performs no input reads, URI or focus operation"); }
        }
    }

    private static SpotifyTrackOpenService Create(Input input, Func<string, CancellationToken, Task<bool>> uri,
        Func<CancellationToken, Task<bool>> focus, Func<TimeSpan, CancellationToken, Task>? delay = null)
        => new(uri, focus, delay ?? ((_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }),
            nowPlayingInput: input);

    private sealed class Input : ISpotifyNowPlayingInput
    {
        private readonly ConcurrentDictionary<ushort, bool> _held = new();
        public int Sends, Reads;
        public void Hold(ushort key) => _held[key] = true;
        public void Release(ushort key) => _held.TryRemove(key, out _);
        public bool IsKeyDown(ushort key) { Interlocked.Increment(ref Reads); return _held.ContainsKey(key); }
        public IntPtr CaptureForegroundSpotifyWindow() => throw new InvalidOperationException("Lyrics must not use native shortcut input");
        public bool IsSameSpotifyForegroundWindow(IntPtr window) => throw new InvalidOperationException("Lyrics must not use native shortcut input");
        public uint Send(IReadOnlyList<SpotifyShortcutInput> inputs) { Sends++; throw new InvalidOperationException("No synthetic input permitted"); }
    }
}

using ToastifyReloaded.Models;
using ToastifyReloaded.Native;
using ToastifyReloaded.Services;

namespace ToastifyReloaded.Windows.Tests;

internal static class ToastHotkeyTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        long now = 1000;
        var native = new FakeHotkeys();
        using (var host = new GlobalHotkeyService(native.Register, native.Unregister,
                   _ => Task.CompletedTask, () => now))
        {
            var errors = host.RegisterAll(new[]
            {
                Binding(HotkeyAction.PlayPause, "Alt+Shift+J"),
                Binding(HotkeyAction.NextTrack, "Shift+Alt+J"),
                Binding(HotkeyAction.VolumeUp, "Ctrl+Alt+K")
            });
            var userActions = new List<HotkeyAction>();
            host.HotkeyPressed += (_, action) => userActions.Add(action);
            check(errors.Count == 1 && errors[0].Contains("duplicata"),
                "semantic Alt+Shift+J duplicates preserve the first binding and report an error");
            host.DispatchHotkey(native.NowPlayingId);
            check(userActions.SequenceEqual(new[] { HotkeyAction.PlayPause }),
                "the original user Alt+Shift+J binding works when no toast is visible");
            check(native.Active.Values.All(x => (x.Modifiers & NativeMethods.MOD_NOREPEAT) != 0),
                "user and temporary registrations suppress native keyboard auto-repeat");

            var oldUserId = native.NowPlayingId;
            var ordinaryId = native.Active.Single(x => x.Value.Key == 0x4B).Key;
            var olderCalls = 0;
            var newerCalls = 0;
            var coordinator = host.ToastNowPlaying;
            Task<bool>? invocation = null;
            using var older = coordinator.Attach(() => invocation = coordinator.RunSuspendedAsync(() =>
            { olderCalls++; return Task.CompletedTask; }));
            var newer = coordinator.Attach(() => invocation = coordinator.RunSuspendedAsync(() =>
            { newerCalls++; return Task.CompletedTask; }));
            var oldToastId = native.NowPlayingId;
            check(native.Active.Count == 2 && coordinator.IsGlobalRegistered && native.NowPlayingCount == 1,
                "multiple visible toasts share exactly one registration without disturbing other shortcuts");
            check(!host.DispatchHotkey(oldUserId) && userActions.Count == 1,
                "queued user shortcut messages cannot change playback after toast ownership starts");
            host.DispatchHotkey(oldToastId);
            await invocation!;
            check(newerCalls == 1 && olderCalls == 0 && userActions.Count == 1,
                "the newest visible toast overrides the user shortcut without also firing its playback action");
            check(native.Active.ContainsKey(ordinaryId),
                "Spotify shortcut forwarding suspends only Alt+Shift+J, not unrelated user shortcuts");
            check(!host.DispatchHotkey(oldToastId),
                "queued hotkey messages from before suspension cannot recursively reactivate a toast");

            newer.Dispose();
            now += 1000;
            host.DispatchHotkey(native.NowPlayingId);
            await invocation!;
            check(olderCalls == 1 && newerCalls == 1,
                "closing the newest toast gives the shortcut back to the remaining visible toast");

            older.Dispose();
            host.DispatchHotkey(native.NowPlayingId);
            check(userActions.Count == 2 && userActions[^1] == HotkeyAction.PlayPause,
                "the last toast restores the user's original chord assignment");

            now += 1000;
            await coordinator.RunSuspendedAsync(() =>
            {
                var refreshed = host.RegisterAll(new[]
                {
                    Binding(HotkeyAction.PlayPause, "Alt+Shift+J"),
                    Binding(HotkeyAction.VolumeUp, "Ctrl+Alt+K")
                });
                check(refreshed.Count == 0 && native.NowPlayingCount == 0,
                    "saving bindings during intentional suspension produces no false collision error");
                return Task.CompletedTask;
            });
            check(native.NowPlayingCount == 1,
                "the user chord is restored after forwarding even when no toast remains");
        }
        check(native.Active.Count == 0, "disposing the shared hotkey host releases every owned registration");

        now = 5000;
        var temporary = new FakeHotkeys();
        using (var host = new GlobalHotkeyService(temporary.Register, temporary.Unregister,
                   _ => Task.CompletedTask, () => now))
        {
            var coordinator = host.ToastNowPlaying;
            var first = coordinator.Attach(() => Task.CompletedTask);
            var second = coordinator.Attach(() => Task.CompletedTask);
            first.Dispose();
            check(temporary.NowPlayingCount == 1, "closing one of several toast leases keeps the temporary shortcut alive");
            coordinator.Enabled = false;
            check(!coordinator.IsGlobalRegistered && temporary.Active.Count == 0,
                "disabling global hotkeys releases the temporary registration while retaining toast leases");
            var fallbackCalls = 0;
            var safeToForward = false;
            check(await coordinator.RunSuspendedAsync(() =>
                  { fallbackCalls++; safeToForward = coordinator.CanForwardShortcut; return Task.CompletedTask; }) && fallbackCalls == 1,
                "local/click activation remains available when global hotkeys are disabled");
            check(safeToForward && !coordinator.CanForwardShortcut && temporary.Active.Count == 0,
                "disabled-global activation probes and releases its own free shortcut before forwarding");
            coordinator.Enabled = true;
            check(coordinator.IsGlobalRegistered && temporary.NowPlayingCount == 1,
                "enabling global hotkeys restores the shortcut for an already visible toast");
            second.Dispose();
            check(temporary.Active.Count == 0 && !coordinator.IsGlobalRegistered,
                "the final toast unregisters Alt+Shift+J when there is no saved user assignment");
        }

        now = 10000;
        var conflict = new FakeHotkeys { ForeignNowPlaying = true };
        using (var host = new GlobalHotkeyService(conflict.Register, conflict.Unregister,
                   _ => Task.CompletedTask, () => now))
        {
            host.RegisterAll(new[] { Binding(HotkeyAction.VolumeUp, "Ctrl+Alt+K") });
            var availability = new List<bool>();
            using var lease = host.ToastNowPlaying.Attach(() => Task.CompletedTask, availability.Add);
            check(!host.ToastNowPlaying.IsGlobalRegistered && availability[^1] == false && conflict.Active.Count == 1,
                "a foreign shortcut collision reports local fallback without removing other applications' ownership");
            var forwardingAllowed = true;
            check(await host.ToastNowPlaying.RunSuspendedAsync(() =>
                  { forwardingAllowed = host.ToastNowPlaying.CanForwardShortcut; return Task.CompletedTask; }) && conflict.ForeignNowPlaying,
                "fallback activation never unregisters the foreign application's shortcut");
            check(!forwardingAllowed,
                "a known foreign shortcut collision forbids forwarding input to another application's hotkey");
        }

        now = 20000;
        var pendingNative = new FakeHotkeys();
        var actionFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quiescence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quiescenceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var host = new GlobalHotkeyService(pendingNative.Register, pendingNative.Unregister,
                   interval =>
                   {
                       check(interval == TimeSpan.FromMilliseconds(200), "forwarded input has a 200ms quiescence window before re-registration");
                       quiescenceStarted.TrySetResult();
                       return quiescence.Task;
                   }, () => now))
        {
            Task<bool>? invocation = null;
            var calls = 0;
            var lease = host.ToastNowPlaying.Attach(() => invocation = host.ToastNowPlaying.RunSuspendedAsync(async () =>
            { calls++; await actionFinished.Task; }));
            var oldId = pendingNative.NowPlayingId;
            host.DispatchHotkey(oldId);
            check(calls == 1 && pendingNative.Active.Count == 0,
                "the global callback can enter the shared suspension gate without a nested-busy deadlock");
            check(!await host.ToastNowPlaying.RunSuspendedAsync(() => { calls++; return Task.CompletedTask; }),
                "a simultaneous local event cannot duplicate an in-flight global activation");
            actionFinished.SetResult();
            await quiescenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(!invocation!.IsCompleted && pendingNative.Active.Count == 0,
                "the shortcut remains unregistered until forwarded input has settled");
            lease.Dispose();
            quiescence.SetResult();
            await invocation;
            check(pendingNative.Active.Count == 0 && !host.ToastNowPlaying.IsGlobalRegistered,
                "closing the final toast during an activation prevents its temporary registration from returning");
        }

        now = 30000;
        var repeatNative = new FakeHotkeys();
        using (var host = new GlobalHotkeyService(repeatNative.Register, repeatNative.Unregister,
                   _ => Task.CompletedTask, () => now))
        {
            var count = 0;
            using var lease = host.ToastNowPlaying.Attach(() => host.ToastNowPlaying.RunSuspendedAsync(() =>
            { count++; return Task.CompletedTask; }));
            host.DispatchHotkey(repeatNative.NowPlayingId);
            host.DispatchHotkey(repeatNative.NowPlayingId);
            check(count == 1, "immediate repeated global messages cannot execute the shortcut twice");
            now += 1000;
            host.DispatchHotkey(repeatNative.NowPlayingId);
            check(count == 2, "a later deliberate shortcut press is accepted after quiescence");
        }
    }

    private static HotkeyBinding Binding(HotkeyAction action, string shortcut)
        => new() { Action = action, Shortcut = shortcut, Enabled = true };

    private sealed class FakeHotkeys
    {
        public Dictionary<int, (uint Modifiers, uint Key)> Active { get; } = new();
        public bool ForeignNowPlaying { get; init; }
        public int NowPlayingCount => Active.Count(x => x.Value.Key == 0x4A);
        public int NowPlayingId => Active.Single(x => x.Value.Key == 0x4A).Key;
        public bool Register(int id, uint modifiers, uint key)
        {
            if (key == 0x4A && ForeignNowPlaying) return false;
            if (Active.Values.Any(x => x.Key == key && x.Modifiers == modifiers)) return false;
            Active.Add(id, (modifiers, key));
            return true;
        }
        public void Unregister(int id) => Active.Remove(id);
    }
}

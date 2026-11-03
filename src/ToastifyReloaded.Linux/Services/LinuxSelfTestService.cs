using ToastifyReloaded.Linux.Models;

namespace ToastifyReloaded.Linux.Services;

public static class LinuxSelfTestService
{
    public static async Task<int> RunAsync()
    {
        var failures = new List<string>();
        var testConfig = Path.Combine(Path.GetTempPath(), "toastify-linux-selftest-" + Guid.NewGuid().ToString("N"));

        void Check(
            bool condition,
            string name)
        {
            if (condition)
            {
                Console.WriteLine($"PASS: {name}");
            }
            else
            {
                Console.Error.WriteLine($"FAIL: {name}");
                failures.Add(name);
            }
        }

        try
        {
            Console.WriteLine(
                "Toastify Reloaded Linux 1.4.0 self-test");

            Console.WriteLine(
                $"Runtime: {Environment.Version}");

            Console.WriteLine(
                $"OS: {Environment.OSVersion}");

            Check(
                LinuxUpdateService.CurrentTag ==
                "v1.4.0-linux",
                "stable version constant");

            var preview4 =
                LinuxUpdateService.ParseTag(
                    "v1.4.0-linux-preview.4");

            var rc1 =
                LinuxUpdateService.ParseTag(
                    "v1.4.0-linux-rc.1");

            var stable =
                LinuxUpdateService.ParseTag(
                    "v1.4.0-linux");

            var nextPreview =
                LinuxUpdateService.ParseTag(
                    "v1.4.1-linux-preview.1");

            var nextRc =
                LinuxUpdateService.ParseTag(
                    "v1.4.1-linux-rc.1");

            var nextStable =
                LinuxUpdateService.ParseTag(
                    "v1.4.1-linux");

            Check(
                preview4 is not null &&
                rc1 is not null &&
                preview4.CompareTo(rc1) < 0,
                "preview sorts before RC");

            Check(
                rc1 is not null &&
                stable is not null &&
                rc1.CompareTo(stable) < 0,
                "RC sorts before stable");

            Check(
                stable is not null &&
                nextStable is not null &&
                stable.CompareTo(nextStable) < 0,
                "semantic stable versions sort correctly");

            Check(
                stable is not null &&
                nextPreview is not null &&
                !LinuxUpdateService.IsEligibleUpdateCandidate(
                    stable,
                    nextPreview),
                "stable channel rejects future preview");

            Check(
                stable is not null &&
                nextRc is not null &&
                !LinuxUpdateService.IsEligibleUpdateCandidate(
                    stable,
                    nextRc),
                "stable channel rejects future RC");

            Check(
                stable is not null &&
                nextStable is not null &&
                LinuxUpdateService.IsEligibleUpdateCandidate(
                    stable,
                    nextStable),
                "stable channel accepts future stable");

            Check(
                rc1 is not null &&
                stable is not null &&
                LinuxUpdateService.IsEligibleUpdateCandidate(
                    rc1,
                    stable),
                "RC channel can promote to stable");

            Check(
                LinuxUpdateService.ParseTag(
                    "v1.4.0") is null,
                "non-Linux tag rejected");

            var customSettings =
                new LinuxSettings
                {
                    ApplicationTheme = "Dark",
                    Language = "English",
                    ToastTheme = "Custom",
                    ToastFontFamily = "Monospace",
                    TitleFontSize = 18,
                    ArtistFontSize = 13,
                    TimeFontSize = 11,
                    CustomTopColor = "#123456",
                    CustomBottomColor = "#010203",
                    CustomBorderColor = "#333333",
                    CustomTitleColor = "#FFFFFF",
                    CustomSecondaryColor = "#CCCCCC",
                    CustomProgressBackgroundColor = "#222222",
                    CustomProgressForegroundColor = "#00FF88",
                    MonitorIndex = 2,
                    ToastPosition = "TopLeft",
                    ToastMarginX = 31,
                    ToastMarginY = 42,
                    AutoInstallLinuxUpdates = true
                };

            var palette =
                ToastThemePalette.FromSettings(
                    customSettings);

            Check(
                palette.Top == "#123456" &&
                palette.ProgressForeground == "#00FF88",
                "custom theme palette");

            var localization =
                new LocalizationService();

            Check(
                !string.IsNullOrWhiteSpace(
                    localization.Get(
                        "UpdateAvailable",
                        "English")),
                "English localization");

            Check(
                !string.IsNullOrWhiteSpace(
                    localization.Get(
                        "UpdateAvailable",
                        "Italiano")),
                "Italian localization");

            var settingsService =
                new LinuxSettingsService(testConfig);

            await settingsService.SaveAsync(
                customSettings);

            var reloaded =
                await settingsService.LoadAsync();

            Check(
                reloaded.ToastTheme == "Custom" &&
                reloaded.MonitorIndex == 2 &&
                reloaded.ToastPosition == "TopLeft" &&
                reloaded.AutoInstallLinuxUpdates,
                "settings save/load roundtrip");

            await using var exportStream =
                new MemoryStream();

            await settingsService.ExportAsync(
                exportStream,
                reloaded);

            exportStream.Position = 0;

            var imported =
                await settingsService.ImportAsync(
                    exportStream);

            Check(
                imported.CustomTopColor == "#123456" &&
                imported.ToastFontFamily == "Monospace",
                "settings export/import roundtrip");

            var process =
                new ProcessService();

            var separator = PlayerctlService.MetadataSeparator;
            var validId = "4uLU6hMCjMI75M1A2tKUQC";
            var metadata = string.Join(separator, "Song", "Artist", "Album", "180000000", "", "spotify:track:" + validId, "");
            var snapshot = PlayerctlService.ParseTrackSnapshot(metadata, 12, true);
            Check(snapshot?.TrackUri == "spotify:track:" + validId && snapshot.DurationSeconds == 180,
                "track URI captured in metadata snapshot");
            var framedMetadata = metadata + separator + PlayerctlService.MetadataEndMarker;
            Check(PlayerctlService.ParseTrackSnapshot(framedMetadata.Trim(), 12, true)?.TrackUri == snapshot?.TrackUri,
                "metadata framing preserves missing final track ID after output trimming");
            Check(PlayerctlService.ParseTrackSnapshot(framedMetadata + separator + "unexpected", 0, true) is null &&
                PlayerctlService.ParseTrackSnapshot(metadata + separator + "wrong-marker", 0, true) is null,
                "invalid metadata framing is rejected");
            var missing = PlayerctlService.ParseTrackSnapshot(
                string.Join(separator, "Local song", "Artist", "Album", "0", "", "file:///tmp/song.mp3", ""), 0, true);
            Check(missing is not null && missing.TrackUri is null,
                "missing URI never reuses previous track identity");
            var pathSnapshot = PlayerctlService.ParseTrackSnapshot(
                string.Join(separator, "Song", "Artist", "Album", "0", "", "", "/com/spotify/track/" + validId), 0, false);
            Check(pathSnapshot?.TrackUri == snapshot?.TrackUri,
                "MPRIS Spotify track path is a validated URI fallback");
            var otherId = "0VjIjW4GlUZAMYd2vXMi3b";
            var conflictSnapshot = PlayerctlService.ParseTrackSnapshot(
                string.Join(separator, "Song", "Artist", "Album", "0", "", "spotify:track:" + validId, "/com/spotify/track/" + otherId), 0, true);
            Check(conflictSnapshot is not null && conflictSnapshot.TrackUri is null,
                "conflicting metadata track IDs are not opened");
            Check(snapshot is not null && snapshot.Identity != (snapshot with { TrackUri = "spotify:track:" + otherId }).Identity,
                "different recordings with identical labels retain different identities");
            Check(PlayerctlService.ParseTrackSnapshot(metadata + separator + "injected", 0, true) is null,
                "ambiguous metadata fields rejected");
            var calls = new List<string>();
            var opener = new SpotifyTrackOpenService((file, arguments, _) =>
            {
                calls.Add(file + " " + string.Join(" ", arguments));
                return Task.FromResult((calls.Count == 2 ? 0 : 1, "", ""));
            }, _ => Task.FromResult<string?>(null));
            Check(await opener.OpenAsync(snapshot?.TrackUri) == SpotifyTrackOpenResult.TrackOpened &&
                calls.Count == 2 && calls[0].Contains("spotify:track:" + validId) &&
                calls[1].Contains("https://open.spotify.com/track/" + validId),
                "desktop track URI falls back to canonical HTTPS");
            calls.Clear();
            var resolverCanCancel = false;
            var activation = new SpotifyTrackOpenService((file, arguments, _) =>
            {
                calls.Add(file + " " + string.Join(" ", arguments));
                return Task.FromResult((0, "", ""));
            }, token =>
            {
                resolverCanCancel = token.CanBeCanceled;
                return Task.FromResult<string?>(null);
            });
            Check(await activation.OpenAsync("javascript:alert(1)") == SpotifyTrackOpenResult.AppActivated &&
                resolverCanCancel && calls.SequenceEqual(new[] { "xdg-open spotify:" }),
                "invalid metadata only activates Spotify");
            var failedOpener = new SpotifyTrackOpenService((_, _, _) =>
                throw new InvalidOperationException("Simulated missing desktop opener"),
                _ => throw new InvalidOperationException("Simulated lost player"));
            Check(await failedOpener.OpenAsync(snapshot?.TrackUri) == SpotifyTrackOpenResult.Failed,
                "all opener failures are crash-safe");

            var playerctl =
                new PlayerctlService(process);

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try
                {
                    await playerctl.ResolveSpotifyPlayerAsync(cancellationToken: cancellation.Token);
                    Check(false, "cancelled player resolver does not start playerctl");
                }
                catch (OperationCanceledException)
                {
                    Check(true, "cancelled player resolver does not start playerctl");
                }
            }

            Check(
                await playerctl.IsAvailableAsync(),
                "playerctl available in CI/runtime");

            var spicetify =
                new SpicetifyLinuxService(process);

            var spicetifyVersion =
                await spicetify.GetVersionAsync();

            Check(
                !string.IsNullOrWhiteSpace(
                    spicetifyVersion),
                "Spicetify probe is crash-safe");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            failures.Add(
                "unhandled self-test exception");
        }
        finally
        {
            if (Directory.Exists(testConfig))
                Directory.Delete(testConfig, recursive: true);
        }

        Console.WriteLine();
        Console.WriteLine(
            failures.Count == 0
                ? "SELF-TEST RESULT: PASS"
                : $"SELF-TEST RESULT: FAIL ({failures.Count})");

        return failures.Count == 0
            ? 0
            : 1;
    }
}

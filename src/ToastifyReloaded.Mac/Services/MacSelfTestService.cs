using System.Runtime.InteropServices;
using System.Text;
using ToastifyReloaded.Mac.Models;

namespace ToastifyReloaded.Mac.Services;

public static class MacSelfTestService
{
    public static async Task<int> RunAsync()
    {
        var failures = new List<string>();

        void Check(bool condition, string name)
        {
            if (condition)
                Console.WriteLine($"PASS: {name}");
            else
            {
                Console.Error.WriteLine($"FAIL: {name}");
                failures.Add(name);
            }
        }

        var oldConfigOverride = Environment.GetEnvironmentVariable("TOASTIFY_RELOADED_CONFIG_DIR");
        var tempRoot = Path.Combine(Path.GetTempPath(), "toastify-macos-selftest-" + Guid.NewGuid().ToString("N"));

        try
        {
            Environment.SetEnvironmentVariable("TOASTIFY_RELOADED_CONFIG_DIR", tempRoot);

            Console.WriteLine($"Toastify Reloaded macOS {MacUpdateService.CurrentTag} self-test");
            Console.WriteLine($"Runtime: {Environment.Version}");
            Console.WriteLine($"OS: {Environment.OSVersion}");
            Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");

            var assemblyVersion = typeof(MacSelfTestService).Assembly.GetName().Version!;
            var currentVersion = MacUpdateService.ParseTag(MacUpdateService.CurrentTag);
            Check(currentVersion is not null &&
                  currentVersion == MacUpdateService.CurrentVersion &&
                  currentVersion.Major == assemblyVersion.Major &&
                  currentVersion.Minor == assemblyVersion.Minor &&
                  currentVersion.Patch == assemblyVersion.Build,
                "current macOS tag is valid and matches assembly version");

            var preview1 = MacUpdateService.ParseTag("v1.5.0-macos-preview.1");
            var preview2 = MacUpdateService.ParseTag("v1.5.0-macos-preview.2");
            var rc1 = MacUpdateService.ParseTag("v1.5.0-macos-rc.1");
            var rc2 = MacUpdateService.ParseTag("v1.5.0-macos-rc.2");
            var stable = MacUpdateService.ParseTag("v1.5.0-macos");
            var nextPreview = MacUpdateService.ParseTag("v1.5.1-macos-preview.1");
            var nextStable = MacUpdateService.ParseTag("v1.5.1-macos");

            Check(preview1 is not null && preview2 is not null && preview1.CompareTo(preview2) < 0,
                "preview versions sort correctly");
            Check(preview2 is not null && rc1 is not null && preview2.CompareTo(rc1) < 0,
                "preview sorts before RC");
            Check(rc1 is not null && rc2 is not null && rc1.CompareTo(rc2) < 0 &&
                  MacUpdateService.IsEligibleUpdateCandidate(rc1, rc2),
                "RC1 advances to RC2 without changing the numeric version");
            Check(rc1 is not null && stable is not null && rc1.CompareTo(stable) < 0,
                "RC sorts before stable");
            Check(rc2 is not null && stable is not null && rc2.CompareTo(stable) < 0 &&
                  MacUpdateService.IsEligibleUpdateCandidate(rc2, stable) &&
                  !MacUpdateService.IsEligibleUpdateCandidate(stable, rc2),
                "RC2 may promote to stable while stable rejects RC2");
            Check(rc2 is not null && preview1 is not null && stable is not null &&
                  MacUpdateService.FormatDisplayVersion(rc2) == "macOS 1.5.0 RC2" &&
                  MacUpdateService.FormatDisplayVersion(preview1) == "macOS 1.5.0 Preview 1" &&
                  MacUpdateService.FormatDisplayVersion(stable) == "macOS 1.5.0",
                "display version follows Preview, RC and stable channels");
            Check(stable is not null && nextStable is not null && stable.CompareTo(nextStable) < 0,
                "semantic stable versions sort correctly");
            Check(stable is not null && nextPreview is not null &&
                  !MacUpdateService.IsEligibleUpdateCandidate(stable, nextPreview),
                "stable channel rejects future preview");
            Check(stable is not null && nextStable is not null &&
                  MacUpdateService.IsEligibleUpdateCandidate(stable, nextStable),
                "stable channel accepts future stable");
            Check(rc1 is not null && stable is not null &&
                  MacUpdateService.IsEligibleUpdateCandidate(rc1, stable),
                "RC can promote to stable");
            Check(MacUpdateService.ParseTag("v1.5.0-linux") is null &&
                  MacUpdateService.ParseTag("v1.5.0") is null,
                "non-macOS tags rejected");

            var customSettings = new MacSettings
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
                EnableGlobalHotkeys = true,
                AutoInstallMacUpdates = true
            };

            var palette = ToastThemePalette.FromSettings(customSettings);
            Check(palette.Top == "#123456" && palette.ProgressForeground == "#00FF88",
                "custom theme palette");

            var normalized = MacSettingsService.Normalize(new MacSettings
            {
                MinWidth = 700,
                MaxWidth = 300,
                FadeInMs = -20,
                ToastMarginX = 800,
                CustomTopColor = "not-a-color"
            });
            Check(normalized.MaxWidth >= normalized.MinWidth &&
                  normalized.FadeInMs == 0 &&
                  normalized.ToastMarginX == 500 &&
                  normalized.CustomTopColor == "#555555",
                "settings normalization");

            var settingsService = new MacSettingsService();
            await settingsService.SaveAsync(customSettings);
            var reloaded = await settingsService.LoadAsync();
            Check(reloaded.ToastTheme == "Custom" &&
                  reloaded.MonitorIndex == 2 &&
                  reloaded.ToastPosition == "TopLeft" &&
                  reloaded.AutoInstallMacUpdates,
                "settings save/load roundtrip");

            await using var exportStream = new MemoryStream();
            await settingsService.ExportAsync(exportStream, reloaded);
            exportStream.Position = 0;
            var imported = await settingsService.ImportAsync(exportStream);
            Check(imported.CustomTopColor == "#123456" && imported.ToastFontFamily == "Monospace",
                "settings export/import roundtrip");

            const string linuxSettingsJson = """
            {
              "ApplicationTheme": "Dark",
              "Language": "English",
              "EnableX11GlobalHotkeys": true,
              "EnableWaylandPortalHotkeys": false,
              "AutoCheckLinuxUpdates": false,
              "AutoInstallLinuxUpdates": true,
              "ToastTheme": "Sakura",
              "HotkeyPlayPause": "Ctrl+Alt+Space"
            }
            """;
            await using var linuxSettingsStream = new MemoryStream(Encoding.UTF8.GetBytes(linuxSettingsJson));
            var importedLinux = await settingsService.ImportAsync(linuxSettingsStream);
            Check(importedLinux.EnableGlobalHotkeys &&
                  !importedLinux.AutoCheckMacUpdates &&
                  importedLinux.AutoInstallMacUpdates &&
                  importedLinux.ToastTheme == "Sakura",
                "Linux settings import compatibility");

            var payload = string.Join('\u001f', new[]
            {
                "Get Lucky",
                "Daft Punk",
                "Random Access Memories",
                "360000",
                "76.5",
                "playing",
                "spotify:image:abc123",
                "spotify:track:4uLU6hMCjMI75M1A2tKUQC",
                "toastify-track-v1"
            });
            var track = SpotifyAppleScriptService.ParseTrackPayload(payload);
            Check(track is not null &&
                  track.Title == "Get Lucky" &&
                  Math.Abs(track.DurationSeconds - 360) < 0.01 &&
                  Math.Abs(track.PositionSeconds - 76.5) < 0.01 &&
                  track.IsPlaying &&
                  track.ArtworkUrl == "https://i.scdn.co/image/abc123" &&
                  track.TrackUri == "spotify:track:4uLU6hMCjMI75M1A2tKUQC" &&
                  track.Identity == track.TrackUri,
                "Spotify AppleScript metadata parser");

            var legacyPayload = string.Join('\u001f', payload.Split('\u001f').Take(7));
            Check(SpotifyAppleScriptService.ParseTrackPayload(legacyPayload)?.TrackUri == "" &&
                  SpotifyAppleScriptService.ParseTrackPayload(legacyPayload + "\u001f\u001ftoastify-track-v1")?.TrackUri == "",
                "missing track ID stays absent without reusing an older ID");
            Check(SpotifyAppleScriptService.ParseTrackPayload(legacyPayload + "\u001fhttps://evil.invalid/track\u001ftoastify-track-v1")?.TrackUri == "" &&
                  SpotifyAppleScriptService.ParseTrackPayload(legacyPayload + "\u001fspotify:track:4uLU6hMCjMI75M1A2tKUQC\n\u001ftoastify-track-v1")?.TrackUri == "" &&
                  SpotifyAppleScriptService.ParseTrackPayload(payload + "\u001fextra") is null,
                "malformed IDs and ambiguous metadata payloads rejected");
            Check(track is not null &&
                  (track with { TrackUri = "spotify:track:0VjIjW4GlUZAMYd2vXMi3b" }).Identity != track.Identity,
                "identical track labels with different Spotify IDs remain distinct");

            var directOpen = await ProbeOpenAsync(track?.TrackUri, 0);
            Check(directOpen.Result.Success && directOpen.Result.OpenedTrack &&
                  directOpen.Calls.Count == 1 && directOpen.Calls[0].File == "/usr/bin/open" &&
                  directOpen.Calls[0].Arguments.SequenceEqual(new[] { "-a", "Spotify", track!.TrackUri }),
                "track opener passes exact snapshot URI as a separate argument");

            var webOpen = await ProbeOpenAsync(track?.TrackUri, 1, 0);
            Check(webOpen.Result.OpenedTrack && webOpen.Calls.Count == 2 &&
                  webOpen.Calls[1].Arguments.SequenceEqual(new[] { "https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC" }),
                "native handler failure falls back to canonical Spotify HTTPS");

            var missingOpen = await ProbeOpenAsync(null, 0);
            var unsafeOpen = await ProbeOpenAsync("spotify:local:artist:$(touch /tmp/unsafe)", 0);
            Check(missingOpen.Result.Success && !missingOpen.Result.OpenedTrack &&
                  missingOpen.Calls.Count == 1 && unsafeOpen.Calls.Count == 1 &&
                  unsafeOpen.Calls[0].Arguments.SequenceEqual(new[] { "-a", "Spotify" }),
                "missing and local track IDs only activate Spotify without search or arbitrary URLs");

            var failedOpen = await ProbeOpenAsync(track?.TrackUri, 1, 1, 1);
            var thrownOpen = await ProbeOpenAsync(track?.TrackUri, -1, -1, -1);
            Check(!failedOpen.Result.Success && failedOpen.Calls.Count == 3 &&
                  !thrownOpen.Result.Success && thrownOpen.Calls.Count == 3,
                "all launcher failures and exceptions produce a safe failure result");

            Check(MacGlobalHotkeyService.ParseShortcut("Ctrl+Alt+Space") is not null &&
                  MacGlobalHotkeyService.ParseShortcut("Cmd+Shift+Right") is not null &&
                  MacGlobalHotkeyService.ParseShortcut("Ctrl+Alt+Shift+Left") is not null &&
                  MacGlobalHotkeyService.ParseShortcut("Ctrl+Bogus+Space") is null,
                "global hotkey parser");

            var ctrlAlt = MacGlobalHotkeyService.ParseShortcut("Ctrl+Alt+Space");
            var normalizedCtrlAlt = MacGlobalHotkeyService.NormalizeModifiers(
                SharpHook.Data.EventMask.LeftCtrl | SharpHook.Data.EventMask.LeftAlt);
            Check(ctrlAlt is not null && ctrlAlt.Value.Modifiers == normalizedCtrlAlt,
                "global hotkey left/right modifier normalization");

            var release = new MacUpdateInfo(
                stable ?? throw new InvalidOperationException("stable test tag parse failed"),
                new Uri("https://github.com/MarliusLab/Toastify-Reloaded/releases/tag/v1.5.0-macos"),
                new MacUpdateAsset[]
                {
                    new("ToastifyReloaded-macOS-arm64.zip", new Uri("https://example.invalid/arm64.zip"), 1, null),
                    new("ToastifyReloaded-macOS-x64.zip", new Uri("https://example.invalid/x64.zip"), 1, null)
                });

            Check(MacUpdateInstallerService.SelectAsset(release, Architecture.Arm64)?.Name.EndsWith("arm64.zip", StringComparison.Ordinal) == true &&
                  MacUpdateInstallerService.SelectAsset(release, Architecture.X64)?.Name.EndsWith("x64.zip", StringComparison.Ordinal) == true,
                "architecture-specific updater assets");

            Check(MacSpotifyVersionService.ExtractVersion("Spotify 1.2.99.1234") == "1.2.99.1234",
                "Spotify bundle version parser");

            var localization = new LocalizationService();
            Check(localization.Get("Updates", "English") == "macOS updates" &&
                  localization.Get("Updates", "Italiano") == "Aggiornamenti macOS",
                "English/Italian localization");

            if (OperatingSystem.IsMacOS())
            {
                var process = new ProcessService();
                var spotify = new SpotifyAppleScriptService(process);
                Check(await spotify.IsAvailableAsync(), "osascript available on macOS runner");
            }
            else
            {
                Console.WriteLine("SKIP: osascript runtime probe (not running on macOS)");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            failures.Add("unhandled self-test exception");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOASTIFY_RELOADED_CONFIG_DIR", oldConfigOverride);
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures.Count == 0
            ? "SELF-TEST RESULT: PASS"
            : $"SELF-TEST RESULT: FAIL ({failures.Count})");

        return failures.Count == 0 ? 0 : 1;
    }

    private static async Task<(SpotifyTrackOpenResult Result,
        List<(string File, string[] Arguments)> Calls)> ProbeOpenAsync(string? uri, params int[] outcomes)
    {
        var calls = new List<(string File, string[] Arguments)>();
        var opener = new SpotifyTrackOpenService((file, arguments, _) =>
        {
            calls.Add((file, arguments.ToArray()));
            var code = outcomes[Math.Min(calls.Count - 1, outcomes.Length - 1)];
            if (code < 0)
                throw new IOException("simulated launcher failure");
            return Task.FromResult((code, "", ""));
        });
        return (await opener.OpenAsync(uri), calls);
    }
}

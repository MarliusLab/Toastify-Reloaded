using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using ToastifyReloaded.Linux.Models;
using ToastifyReloaded.Linux.Services;
using ToastifyReloaded.Shared;

namespace ToastifyReloaded.Linux;

public partial class MainWindow : Window
{
    private readonly ProcessService _processService = new();
    private readonly LinuxSettingsService _settingsService = new();
    private readonly ArtworkService _artworkService = new();
    private readonly LocalizationService _localization = new();
    private readonly LinuxUpdateService _updates = new();
    private readonly LinuxUpdateInstallerService _updateInstaller;

    private readonly PlayerctlService _playerctl;
    private readonly GlobalHotkeyCoordinator _hotkeys;
    private readonly SpicetifyLinuxService _spicetify;
    private readonly SpotifyVersionService _spotifyVersion;
    private readonly CompatibilityGuardService _compatibilityGuard;
    private readonly LinuxAutostartService _autostart = new();
    private readonly ToastifyReloaded.Shared.ToastShowHotkeyController _showToastHotkey = new();
    private readonly ToastifyReloaded.Shared.ToastShowHotkeyController _currentTrackHotkey = new();
    private readonly List<ToastWindow> _toastWindows = new();
    private readonly ToastifyReloaded.Shared.ToastNavigationGate _toastNavigation = new();
    private readonly ToastControlService _toastControl;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _updateCancellation;
    private bool _checkingUpdate, _installingUpdate;
    private volatile bool _canCancelUpdate, _updateHandoffPending;
    private bool _exitAfterUpdate;

    private LinuxSettings _settings = new();
    private DispatcherTimer? _pollTimer;
    private readonly ToastifyReloaded.Shared.SpotifyTrackChangeTracker _trackChanges = new();
    private bool _polling;
    private bool _loadingUi;
    private bool _allowExit;
    private LinuxUpdateInfo? _availableUpdate;

    public MainWindow()
    {
        InitializeComponent();

        _playerctl = new PlayerctlService(_processService);
        _updateInstaller = new LinuxUpdateInstallerService(_processService);

        var x11Hotkeys = new XbindkeysService(
            _processService,
            _settingsService);

        _toastControl = new ToastControlService(DispatchShowToastAsync, DispatchCurrentTrackAsync);
        var waylandHotkeys = new XdgGlobalShortcutsService(_playerctl, DispatchShowToastAsync);

        _hotkeys = new GlobalHotkeyCoordinator(
            x11Hotkeys,
            waylandHotkeys);

        _spicetify = new SpicetifyLinuxService(_processService);
        _spotifyVersion = new SpotifyVersionService(_processService);
        _compatibilityGuard = new CompatibilityGuardService(
            _spotifyVersion,
            _spicetify);

        Opened += async (_, _) =>
        {
            try { await InitializeAsync(); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { Find<TextBlock>("StatusText").Text = ex.Message; }
        };
    }

    private async Task InitializeAsync()
    {
        _settings = await _settingsService.LoadAsync();

        PopulateMonitorCombo();
        LoadSettingsToUi();
        ApplyApplicationTheme();
        ApplyLocalization();

        var statusTask = RefreshStatusAsync();
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += async (_, _) => await PollSpotifyAsync();
        _pollTimer.Start();

        try { await _toastControl.StartAsync(); }
        catch (Exception ex) { Find<TextBlock>("SpotifyStatusText").Text = "Show Toast D-Bus: " + ex.Message; }
        await ApplyHotkeysAsync();

        await statusTask;
        if (_settings.EnableCompatibilityGuard)
            await RunCompatibilityGuardAsync(silent: true);

        if (_settings.AutoCheckLinuxUpdates)
        {
            _ = CheckUpdatesAsync(
                silent: true,
                installIfAvailable: _settings.AutoInstallLinuxUpdates && !UpdateSessionPolicy.SuppressAutomaticInstall);
        }

    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_updateHandoffPending && (_allowExit || !_settings.CloseToTray))
        { e.Cancel = true; _exitAfterUpdate = true; return; }
        if (!_allowExit && _settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _pollTimer?.Stop();
        _lifetime.Cancel();
        _updateCancellation?.Cancel();
        _hotkeys.Dispose();
        _toastControl.Dispose();
        _showToastHotkey.Dispose();
        _currentTrackHotkey.Dispose();
        _toastNavigation.Dispose();

        base.OnClosing(e);
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;

        if (!IsVisible)
            Show();

        WindowState = WindowState.Normal;
        Activate();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    public Task TrayPlayPauseAsync() => _playerctl.PlayPauseAsync();
    public Task TrayNextAsync() => _playerctl.NextAsync();
    public Task TrayPreviousAsync() => _playerctl.PreviousAsync();

    public void RequestExit()
    {
        if (_updateHandoffPending) { _exitAfterUpdate = true; return; }
        _allowExit = true;
        _lifetime.Cancel();
        _updateCancellation?.Cancel();
        _toastControl.Dispose();
        _showToastHotkey.Dispose();
        _currentTrackHotkey.Dispose();
        _toastNavigation.Dispose();
        _pollTimer?.Stop();
        _hotkeys.Dispose();

        if (Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private async Task PollSpotifyAsync()
    {
        if (_polling)
            return;

        _polling = true;

        try
        {
            var track = await _playerctl.GetTrackAsync();
            if (track is null || string.IsNullOrWhiteSpace(track.Title))
                return;

            if (_settings.ShowToastOnTrackChange &&
                _trackChanges.Update(track.TrackUri, track.Title, track.Artist, track.Album))
            {
                _ = ShowToastAsync(track);
            }
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task ShowToastAsync(LinuxTrackInfo track, bool untilVisible = false)
    {
        try
        {
            var toast = new ToastWindow(
                track,
                _settings,
                _artworkService,
                new SpotifyTrackOpenService(_processService, _playerctl), _toastNavigation);

            var lease = _showToastHotkey.Attach(() => toast.CanHandleLyricsPlusRequest, toast.RequestLyricsPlusAsync);
            var trackLease = _currentTrackHotkey.Attach(() => toast.CanHandleLyricsPlusRequest, toast.RequestCurrentTrackAsync);
            _toastWindows.Add(toast);
            toast.PropertyChanged += (_, change) =>
            { if (change.Property == IsVisibleProperty) _ = RefreshToastNavigationScopeAsync(); };
            toast.Closed += (_, _) =>
            { lease.Dispose(); trackLease.Dispose(); _toastWindows.Remove(toast); _ = RefreshToastNavigationScopeAsync(); };

            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            toast.Opened += (_, _) => opened.TrySetResult();
            async Task ObserveLifetimeAsync()
            {
                try { await toast.ShowToastAsync(); }
                catch { lease.Dispose(); trackLease.Dispose(); _toastWindows.Remove(toast); await RefreshToastNavigationScopeAsync(); }
            }
            var lifetime = ObserveLifetimeAsync();
            if (untilVisible) await Task.WhenAny(opened.Task, lifetime);
            else await lifetime;
        }
        catch
        {
            // A toast failure must never terminate the background application.
        }
    }

    private Task ShowToastHotkeyAsync() => ToastKeyboardNavigation.RouteShowToastAsync(
        _settings.HotkeyShowToast, _hotkeys.IsWayland, _toastWindows.Any(t => t.CanHandleLyricsPlusRequest),
        () => _showToastHotkey.ExecuteAsync(async () =>
        {
            var track = await _playerctl.GetTrackAsync() ?? new LinuxTrackInfo("Spotify", "", "", "", 0, 0, false);
            await ShowToastAsync(track, untilVisible: true);
        }),
        // Consume this press even if the last owner closes or is busy before
        // dispatch. The current-track controller must never create a new toast.
        () => _currentTrackHotkey.ExecuteAsync(() => Task.CompletedTask));

    private Task DispatchShowToastAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { await ShowToastHotkeyAsync(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    private Task DispatchCurrentTrackAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { await _currentTrackHotkey.ExecuteAsync(() => Task.CompletedTask); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    private async Task RefreshToastNavigationScopeAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        try
        {
            var result = await _hotkeys.SetToastNavigationScopeAsync(_toastWindows.Any(t => t.CanHandleLyricsPlusRequest));
            if (!string.IsNullOrEmpty(result.Message)) Find<TextBlock>("StatusText").Text = result.Message;
        }
        catch (Exception ex)
        { if (!_lifetime.IsCancellationRequested) Find<TextBlock>("StatusText").Text = "Alt+Shift+J: " + ex.Message; }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        _settings = ReadSettingsFromUi();

        await _settingsService.SaveAsync(_settings);
        await _autostart.SetEnabledAsync(_settings.StartWithSession);

        ApplyApplicationTheme();
        ApplyLocalization();

        await ApplyHotkeysAsync();
        await RefreshStatusAsync();
    }

    private async void TestToast_Click(object? sender, RoutedEventArgs e)
    {
        _settings = ReadSettingsFromUi();

        var track = await _playerctl.GetTrackAsync()
            ?? new LinuxTrackInfo(
                "Toastify Reloaded Linux",
                $"Linux {LinuxUpdateService.CurrentVersion.Major}.{LinuxUpdateService.CurrentVersion.Minor}.{LinuxUpdateService.CurrentVersion.Patch}",
                "Classic Toastify experience",
                string.Empty,
                76,
                200,
                true);

        await ShowToastAsync(track);
    }

    private async void RefreshStatus_Click(object? sender, RoutedEventArgs e)
        => await RefreshStatusAsync();

    private async void EnableLyrics_Click(object? sender, RoutedEventArgs e)
    {
        var result = await _spicetify.EnableLyricsAsync();

        Find<TextBlock>("SpicetifyStatusText").Text =
            $"Spicetify: {result.Message}";
    }

    private async void RepairSpicetify_Click(object? sender, RoutedEventArgs e)
    {
        var result = await _spicetify.RepairAfterSpotifyUpdateAsync(
            _settings.KeepLyricsPlus);

        Find<TextBlock>("SpicetifyStatusText").Text =
            $"Spicetify: {result.Message}";
    }

    private void Exit_Click(object? sender, RoutedEventArgs e)
        => RequestExit();

    private void LanguageCombo_SelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingUi)
            return;

        _settings.Language = ComboValue(
            "LanguageCombo",
            "Italiano");

        var selectedMonitor = ComboValue("MonitorCombo", "-1");
        ApplyLocalization();
        PopulateMonitorCombo();
        SelectCombo("MonitorCombo", selectedMonitor);
    }

    private async void ExportSettings_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _settings = ReadSettingsFromUi();

            var file = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = _settings.Language == "English"
                        ? "Export Toastify Reloaded settings"
                        : "Esporta impostazioni Toastify Reloaded",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("JSON")
                        {
                            Patterns = new[] { "*.json" }
                        }
                    }
                });

            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);

            await _settingsService.ExportAsync(
                stream,
                _settings);

            Find<TextBlock>("SettingsIoStatusText").Text =
                T("ExportOk");
        }
        catch (Exception ex)
        {
            Find<TextBlock>("SettingsIoStatusText").Text =
                ex.Message;
        }
    }

    private async void ImportSettings_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = _settings.Language == "English"
                        ? "Import Toastify Reloaded settings"
                        : "Importa impostazioni Toastify Reloaded",
                    AllowMultiple = false
                });

            if (files.Count == 0)
                return;

            await using var stream = await files[0].OpenReadAsync();
            _settings = await _settingsService.ImportAsync(stream);

            LoadSettingsToUi();
            ApplyApplicationTheme();
            ApplyLocalization();

            await _settingsService.SaveAsync(_settings);
            await _autostart.SetEnabledAsync(_settings.StartWithSession);
            await ApplyHotkeysAsync();
            await RefreshStatusAsync();

            Find<TextBlock>("SettingsIoStatusText").Text =
                T("ImportOk");
        }
        catch (Exception ex)
        {
            Find<TextBlock>("SettingsIoStatusText").Text =
                ex.Message;
        }
    }

    private async void CheckUpdates_Click(object? sender, RoutedEventArgs e)
        => await CheckUpdatesAsync(
            silent: false,
            installIfAvailable: false);

    private async Task CheckUpdatesAsync(bool silent, bool installIfAvailable)
    {
        if (_checkingUpdate || _installingUpdate) return;
        _checkingUpdate = true;
        try
        {
            _availableUpdate = await _updates.CheckAsync(_lifetime.Token);

            if (_availableUpdate is null)
            {
                Find<Button>("OpenReleaseButton").IsEnabled = false;
                Find<Button>("InstallUpdateButton").IsEnabled = false;

                if (!silent)
                    Find<TextBlock>("UpdateStatusText").Text = T("NoUpdate");

                return;
            }

            Find<Button>("OpenReleaseButton").IsEnabled = true;
            Find<Button>("InstallUpdateButton").IsEnabled = true;

            Find<TextBlock>("UpdateStatusText").Text =
                $"{T("UpdateAvailable")}: {_availableUpdate.Tag}";

            if (installIfAvailable) { _checkingUpdate = false; await InstallAvailableUpdateAsync(); }
        }
        catch (Exception ex)
        {
            if (!silent)
                Find<TextBlock>("UpdateStatusText").Text = ex.Message;
        }
        finally { _checkingUpdate = false; }
    }


    private async void InstallUpdate_Click(object? sender, RoutedEventArgs e)
        => await InstallAvailableUpdateAsync();

    private async Task InstallAvailableUpdateAsync()
    {
        if (_availableUpdate is null || _installingUpdate || _checkingUpdate)
            return;

        _installingUpdate = true;
        _canCancelUpdate = true;
        _updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Find<Button>("InstallUpdateButton").IsEnabled = false;

        try
        {
            Find<TextBlock>("UpdateStatusText").Text =
                _settings.Language == "English"
                    ? "Downloading update..."
                    : "Download aggiornamento...";

            var result =
                await _updateInstaller.DownloadAndApplyAsync(
                    _availableUpdate, new UpdateProgressCallback(ReportUpdateProgress), _updateCancellation.Token);

            Find<TextBlock>("UpdateStatusText").Text =
                result.Message;

            if (result.Installed)
            {
                _availableUpdate = null;
                Find<Button>("InstallUpdateButton").IsEnabled = false;
            }

            if (result.Success &&
                result.RestartStarted)
            {
                RequestExit();
            }
        }
        catch (OperationCanceledException)
        { Find<TextBlock>("UpdateStatusText").Text = T("UpdateCanceled"); }
        catch (Exception ex)
        {
            Find<TextBlock>("UpdateStatusText").Text =
                ex.Message;
        }
        finally
        {
            _installingUpdate = false;
            _canCancelUpdate = false;
            _updateHandoffPending = false;
            _updateCancellation?.Dispose();
            _updateCancellation = null;
            Find<Button>("CancelUpdateButton").IsVisible = false;
            if (_availableUpdate is not null)
                Find<Button>("InstallUpdateButton").IsEnabled = true;
            if (_exitAfterUpdate) RequestExit();
        }
    }

    private void CancelUpdate_Click(object? sender, RoutedEventArgs e)
    { if (_canCancelUpdate) _updateCancellation?.Cancel(); }

    private void ReportUpdateProgress(UpdateOperationProgress progress)
    {
        _canCancelUpdate = progress.Stage is UpdateOperationStage.Downloading or UpdateOperationStage.Verifying or UpdateOperationStage.Preparing;
        _updateHandoffPending = progress.Stage is UpdateOperationStage.WaitingForPermission or UpdateOperationStage.Installing or UpdateOperationStage.WaitingForExit;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => ApplyUpdateProgress(progress)); return; }
        ApplyUpdateProgress(progress);
    }

    private void ApplyUpdateProgress(UpdateOperationProgress progress)
    {
        if (!_installingUpdate) return;
        var bar = Find<ProgressBar>("UpdateProgressBar");
        var canCancel = progress.Stage is UpdateOperationStage.Downloading or UpdateOperationStage.Verifying or UpdateOperationStage.Preparing;
        Find<Button>("CancelUpdateButton").IsVisible = canCancel;
        bar.IsVisible = true;
        bar.IsIndeterminate = progress.Stage != UpdateOperationStage.Downloading || progress.Percent is null;
        if (progress.Percent is { } value) bar.Value = value;
        Find<TextBlock>("UpdateStatusText").Text = progress.Stage == UpdateOperationStage.Downloading
            ? progress.TotalBytes is { } total ? $"{T("UpdateDownloading")}: {progress.Percent:0}% ({progress.ReceivedBytes:N0} / {total:N0} B)"
                : $"{T("UpdateDownloading")}: {progress.ReceivedBytes:N0} B"
            : progress.Message ?? T("Update" + progress.Stage);
    }

    private sealed class UpdateProgressCallback(Action<UpdateOperationProgress> report) : IProgress<UpdateOperationProgress>
    { public void Report(UpdateOperationProgress value) => report(value); }

    private async void OpenLatestRelease_Click(object? sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
            return;

        await Launcher.LaunchUriAsync(
            _availableUpdate.ReleaseUri);
    }

    private async void RunCompatibilityGuard_Click(
        object? sender,
        RoutedEventArgs e)
        => await RunCompatibilityGuardAsync(silent: false);

    private async Task RunCompatibilityGuardAsync(bool silent)
    {
        try
        {
            _settings = ReadSettingsFromUi();

            var result = await _compatibilityGuard.CheckAsync(_settings, _lifetime.Token);

            await _settingsService.SaveAsync(_settings);

            var detail = result.Spotify is null
                ? ""
                : $" ({result.Spotify.Source}: {result.Spotify.Version})";

            Find<TextBlock>("CompatibilityGuardStatusText").Text =
                T(result.Message) + detail;
        }
        catch (Exception ex)
        {
            if (!silent)
                Find<TextBlock>("CompatibilityGuardStatusText").Text =
                    ex.Message;
        }
    }

    private async Task RefreshStatusAsync()
    {
        var playerTask = _playerctl.IsAvailableAsync();
        var spiceTask = _spicetify.GetVersionAsync(_lifetime.Token);
        var installTask = _spotifyVersion.GetVersionAsync(_lifetime.Token);
        async Task PublishSpiceAsync()
        {
            var version = await spiceTask;
            Find<TextBlock>("SpicetifyStatusText").Text = $"Spicetify: {version}";
        }
        async Task PublishSpotifyAsync()
        {
            var install = await installTask;
            Find<TextBlock>("SpotifyVersionText").Text = install is null ? "Spotify: " + T("NotDetected")
                : $"Spotify: {install.Version} ({install.Source})";
        }
        var publish = Task.WhenAll(PublishSpiceAsync(), PublishSpotifyAsync());
        var playerctlAvailable = await playerTask;
        var spotifyAvailable = playerctlAvailable &&
                               await _playerctl.IsSpotifyAvailableAsync();

        await publish;
        var spicetifyVersion = await spiceTask;
        var spotifyInstall = await installTask;

        Find<TextBlock>("PlayerctlStatusText").Text =
            $"playerctl: {(playerctlAvailable ? "OK" : T("Missing"))}";

        Find<TextBlock>("SpotifyStatusText").Text =
            $"Spotify MPRIS: {(spotifyAvailable ? T("Detected") : T("NotDetected"))}";

        Find<TextBlock>("SessionStatusText").Text =
            $"Session: {_hotkeys.SessionType}";

        Find<TextBlock>("SpicetifyStatusText").Text =
            $"Spicetify: {spicetifyVersion}";

        var hotkeyText = _hotkeys.IsWayland
            ? "Wayland: XDG Global Shortcuts Portal"
            : "X11: xbindkeys";

        Find<TextBlock>("StatusText").Text =
            spotifyAvailable
                ? $"{T("SpotifyReady")} • {hotkeyText}"
                : $"{T("SpotifyMissing")} • {hotkeyText}";

        Find<TextBlock>("DiagnosticsText").Text =
            string.Join(
                Environment.NewLine,
                new[]
                {
                    $"Toastify Reloaded Linux: {LinuxUpdateService.CurrentVersion.Major}.{LinuxUpdateService.CurrentVersion.Minor}.{LinuxUpdateService.CurrentVersion.Patch}",
                    $"OS: {Environment.OSVersion}",
                    $"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
                    $"Session: {_hotkeys.SessionType}",
                    $"DISPLAY: {Environment.GetEnvironmentVariable("DISPLAY") ?? "(none)"}",
                    $"WAYLAND_DISPLAY: {Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "(none)"}",
                    $"playerctl: {(playerctlAvailable ? "available" : "missing")}",
                    $"Spotify MPRIS: {(spotifyAvailable ? "available" : "missing")}",
                    $"Spotify install: {(spotifyInstall is null ? "unknown" : spotifyInstall.Source + " " + spotifyInstall.Version)}",
                    $"Spicetify: {spicetifyVersion}",
                    $"Config: {_settingsService.SettingsPath}",
                    $"Updater: {LinuxUpdateService.CurrentTag}"
                });
    }

    private async Task ApplyHotkeysAsync()
    {
        var result = await _hotkeys.ApplyAsync(_settings);
        Find<TextBlock>("StatusText").Text = result.Message;
    }

    private void ApplyApplicationTheme()
    {
        if (Application.Current is null)
            return;

        Application.Current.RequestedThemeVariant =
            _settings.ApplicationTheme switch
            {
                "Dark" => ThemeVariant.Dark,
                "Light" => ThemeVariant.Light,
                _ => ThemeVariant.Default
            };
    }

    private void ApplyLocalization()
    {
        _localization.Apply(this, _settings.Language);
        Title = "Toastify Reloaded — Linux";
    }

    private string T(string key)
        => _localization.Get(key, _settings.Language);

    private void LoadSettingsToUi()
    {
        _loadingUi = true;

        try
        {
            SelectCombo("AppThemeCombo", _settings.ApplicationTheme);
            SelectCombo("LanguageCombo", _settings.Language);

            SetCheck("AutostartCheck", _settings.StartWithSession);
            SetCheck("CloseToTrayCheck", _settings.CloseToTray);
            SetCheck("EnableHotkeysCheck", _settings.EnableX11GlobalHotkeys);

            SetText("PlayPauseHotkeyBox", _settings.HotkeyPlayPause);
            SetText("NextHotkeyBox", _settings.HotkeyNext);
            SetText("PreviousHotkeyBox", _settings.HotkeyPrevious);
            SetText("VolumeUpHotkeyBox", _settings.HotkeyVolumeUp);
            SetText("VolumeDownHotkeyBox", _settings.HotkeyVolumeDown);
            SetText("MuteHotkeyBox", _settings.HotkeyMute);
            SetText("SeekForwardHotkeyBox", _settings.HotkeySeekForward);
            SetText("SeekBackwardHotkeyBox", _settings.HotkeySeekBackward);
            SetText("ShowToastHotkeyBox", _settings.HotkeyShowToast);

            SetCheck("ShowToastCheck", _settings.ShowToastOnTrackChange);
            SetNumeric("ToastDisplayNumeric", _settings.ToastDisplayMs);
            SetNumeric("FadeInNumeric", _settings.FadeInMs);
            SetNumeric("FadeOutNumeric", _settings.FadeOutMs);
            SetCheck("ShowProgressCheck", _settings.ShowProgress);
            SetCheck("ShowTimeCheck", _settings.ShowSongTime);
            SetCheck("AutoWidthCheck", _settings.AutoWidth);
            SetNumeric("MinWidthNumeric", _settings.MinWidth);
            SetNumeric("MaxWidthNumeric", _settings.MaxWidth);
            SelectCombo("ImageModeCombo", _settings.ImageMode);
            SetCheck("IconFallbackCheck", _settings.IconFallback);
            SelectCombo("ToastThemeCombo", _settings.ToastTheme);

            SelectCombo("ToastFontCombo", _settings.ToastFontFamily);
            SetNumeric("TitleFontSizeNumeric", (decimal)_settings.TitleFontSize);
            SetNumeric("ArtistFontSizeNumeric", (decimal)_settings.ArtistFontSize);
            SetNumeric("TimeFontSizeNumeric", (decimal)_settings.TimeFontSize);

            SetText("CustomTopColorBox", _settings.CustomTopColor);
            SetText("CustomBottomColorBox", _settings.CustomBottomColor);
            SetText("CustomBorderColorBox", _settings.CustomBorderColor);
            SetText("CustomTitleColorBox", _settings.CustomTitleColor);
            SetText("CustomSecondaryColorBox", _settings.CustomSecondaryColor);
            SetText("CustomProgressBackgroundColorBox", _settings.CustomProgressBackgroundColor);
            SetText("CustomProgressForegroundColorBox", _settings.CustomProgressForegroundColor);

            SelectCombo("MonitorCombo", _settings.MonitorIndex.ToString());
            SelectCombo("ToastPositionCombo", _settings.ToastPosition);
            SetNumeric("ToastMarginXNumeric", _settings.ToastMarginX);
            SetNumeric("ToastMarginYNumeric", _settings.ToastMarginY);

            SelectCombo("AnimationStyleCombo", _settings.AnimationStyle);
            SelectCombo("SlideInCombo", _settings.SlideInDirection);
            SelectCombo("SlideOutCombo", _settings.SlideOutDirection);
            SetNumeric("SlideInDistanceNumeric", _settings.SlideInDistance);
            SetNumeric("SlideOutDistanceNumeric", _settings.SlideOutDistance);

            SetCheck("EnableCompatibilityGuardCheck", _settings.EnableCompatibilityGuard);
            SetCheck("AutoRepairSpicetifyCheck", _settings.AutoRepairSpicetify);
            SetCheck("AutoCheckUpdatesCheck", _settings.AutoCheckLinuxUpdates);
            SetCheck("AutoInstallUpdatesCheck", _settings.AutoInstallLinuxUpdates);
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private LinuxSettings ReadSettingsFromUi()
    {
        return new LinuxSettings
        {
            ApplicationTheme = ComboValue("AppThemeCombo", "System"),
            Language = ComboValue("LanguageCombo", "Italiano"),
            StartWithSession = CheckValue("AutostartCheck"),
            CloseToTray = CheckValue("CloseToTrayCheck"),

            EnableX11GlobalHotkeys = CheckValue("EnableHotkeysCheck"),
            EnableWaylandPortalHotkeys = CheckValue("EnableHotkeysCheck"),

            HotkeyPlayPause = TextValue("PlayPauseHotkeyBox"),
            HotkeyNext = TextValue("NextHotkeyBox"),
            HotkeyPrevious = TextValue("PreviousHotkeyBox"),
            HotkeyVolumeUp = TextValue("VolumeUpHotkeyBox"),
            HotkeyVolumeDown = TextValue("VolumeDownHotkeyBox"),
            HotkeyMute = TextValue("MuteHotkeyBox"),
            HotkeySeekForward = TextValue("SeekForwardHotkeyBox"),
            HotkeySeekBackward = TextValue("SeekBackwardHotkeyBox"),
            HotkeyShowToast = TextValue("ShowToastHotkeyBox"),

            ShowToastOnTrackChange = CheckValue("ShowToastCheck"),
            ToastDisplayMs = NumericValue("ToastDisplayNumeric", 3500),
            FadeInMs = NumericValue("FadeInNumeric", 250),
            FadeOutMs = NumericValue("FadeOutNumeric", 250),
            ShowProgress = CheckValue("ShowProgressCheck"),
            ShowSongTime = CheckValue("ShowTimeCheck"),
            AutoWidth = CheckValue("AutoWidthCheck"),
            MinWidth = NumericValue("MinWidthNumeric", 250),
            MaxWidth = NumericValue("MaxWidthNumeric", 600),
            ImageMode = ComboValue("ImageModeCombo", "Album cover"),
            IconFallback = CheckValue("IconFallbackCheck"),
            ToastTheme = ComboValue("ToastThemeCombo", "Classic Toastify"),

            ToastFontFamily = ComboValue("ToastFontCombo", "Inter"),
            TitleFontSize = NumericDoubleValue("TitleFontSizeNumeric", 15),
            ArtistFontSize = NumericDoubleValue("ArtistFontSizeNumeric", 12),
            TimeFontSize = NumericDoubleValue("TimeFontSizeNumeric", 10),

            CustomTopColor = TextValue("CustomTopColorBox"),
            CustomBottomColor = TextValue("CustomBottomColorBox"),
            CustomBorderColor = TextValue("CustomBorderColorBox"),
            CustomTitleColor = TextValue("CustomTitleColorBox"),
            CustomSecondaryColor = TextValue("CustomSecondaryColorBox"),
            CustomProgressBackgroundColor = TextValue("CustomProgressBackgroundColorBox"),
            CustomProgressForegroundColor = TextValue("CustomProgressForegroundColorBox"),

            AnimationStyle = ComboValue("AnimationStyleCombo", "Fade + Slide"),
            SlideInDirection = ComboValue("SlideInCombo", "Up"),
            SlideOutDirection = ComboValue("SlideOutCombo", "Right"),
            SlideInDistance = NumericValue("SlideInDistanceNumeric", 28),
            SlideOutDistance = NumericValue("SlideOutDistanceNumeric", 50),

            MonitorIndex = int.TryParse(
                ComboValue("MonitorCombo", "-1"),
                out var monitorIndex)
                ? monitorIndex
                : -1,
            ToastPosition = ComboValue("ToastPositionCombo", "BottomRight"),
            ToastMarginX = NumericValue("ToastMarginXNumeric", 18),
            ToastMarginY = NumericValue("ToastMarginYNumeric", 18),

            KeepLyricsPlus = _settings.KeepLyricsPlus,

            EnableCompatibilityGuard = CheckValue("EnableCompatibilityGuardCheck"),
            AutoRepairSpicetify = CheckValue("AutoRepairSpicetifyCheck"),
            LastSpotifyVersion = _settings.LastSpotifyVersion,
            LastRepairAttemptVersion = _settings.LastRepairAttemptVersion,

            AutoCheckLinuxUpdates = CheckValue("AutoCheckUpdatesCheck"),
            AutoInstallLinuxUpdates = CheckValue("AutoInstallUpdatesCheck")
        };
    }

    private T Find<T>(string name) where T : Control
        => this.FindControl<T>(name)
           ?? throw new InvalidOperationException($"Control not found: {name}");


    private void PopulateMonitorCombo()
    {
        var combo = Find<ComboBox>("MonitorCombo");
        combo.Items.Clear();

        combo.Items.Add(
            new ComboBoxItem
            {
                Content = _settings.Language == "English"
                    ? "Primary monitor"
                    : "Monitor principale",
                Tag = "-1"
            });

        for (var i = 0; i < Screens.All.Count; i++)
        {
            var screen = Screens.All[i];
            var displayName = string.IsNullOrWhiteSpace(screen.DisplayName)
                ? $"Monitor {i + 1}"
                : screen.DisplayName;

            var primary = screen.IsPrimary
                ? (_settings.Language == "English" ? " · primary" : " · principale")
                : "";

            combo.Items.Add(
                new ComboBoxItem
                {
                    Content =
                        $"{i + 1}: {displayName} — " +
                        $"{screen.Bounds.Width}×{screen.Bounds.Height}" +
                        primary,
                    Tag = i.ToString()
                });
        }
    }

    private void SelectCombo(string name, string value)
    {
        var combo = Find<ComboBox>(name);

        foreach (var item in combo.Items)
        {
            if (item is not ComboBoxItem comboItem)
                continue;

            var canonical =
                comboItem.Tag?.ToString()
                ?? comboItem.Content?.ToString();

            if (string.Equals(
                    canonical,
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private string ComboValue(string name, string fallback)
    {
        var combo = Find<ComboBox>(name);

        if (combo.SelectedItem is not ComboBoxItem item)
            return fallback;

        return item.Tag?.ToString()
               ?? item.Content?.ToString()
               ?? fallback;
    }

    private void SetCheck(string name, bool value)
        => Find<CheckBox>(name).IsChecked = value;

    private bool CheckValue(string name)
        => Find<CheckBox>(name).IsChecked == true;

    private void SetText(string name, string value)
        => Find<TextBox>(name).Text = value;

    private string TextValue(string name)
        => Find<TextBox>(name).Text?.Trim() ?? string.Empty;

    private void SetNumeric(string name, decimal value)
        => Find<NumericUpDown>(name).Value = value;


    private double NumericDoubleValue(string name, double fallback)
    {
        var value = Find<NumericUpDown>(name).Value;

        return value.HasValue
            ? (double)value.Value
            : fallback;
    }

    private int NumericValue(string name, int fallback)
    {
        var value = Find<NumericUpDown>(name).Value;

        return value.HasValue
            ? (int)Math.Round(value.Value)
            : fallback;
    }
}

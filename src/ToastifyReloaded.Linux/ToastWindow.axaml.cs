using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using ToastifyReloaded.Linux.Models;
using ToastifyReloaded.Linux.Services;

namespace ToastifyReloaded.Linux;

public partial class ToastWindow : Window
{
    private readonly LinuxTrackInfo _track;
    private readonly LinuxSettings _settings;
    private readonly ArtworkService _artworkService;
    private readonly SpotifyTrackOpenService _trackOpener;
    private bool _openingTrack;
    private bool _showOpenStatus;
    private DateTime _statusUntil;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ToastifyReloaded.Shared.ToastNavigationGate _actions;
    private bool _closed, _exiting;
    private int _navigationGeneration;
    private readonly ToastKeyboardNavigation _keyboardNavigation = new();
    internal bool CanHandleLyricsPlusRequest => IsVisible && !_closed;

    public ToastWindow(
        LinuxTrackInfo track,
        LinuxSettings settings,
        ArtworkService artworkService,
        SpotifyTrackOpenService trackOpener)
        : this(track, settings, artworkService, trackOpener, new ToastifyReloaded.Shared.ToastNavigationGate()) { }

    internal ToastWindow(LinuxTrackInfo track, LinuxSettings settings, ArtworkService artworkService,
        SpotifyTrackOpenService trackOpener, ToastifyReloaded.Shared.ToastNavigationGate actions)
    {
        InitializeComponent();
        _track = track;
        _settings = settings;
        _artworkService = artworkService;
        _trackOpener = trackOpener;
        _actions = actions;
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); };
        RootBorder.AddHandler(PointerReleasedEvent, OnToastPointerReleased, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnNavigationKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnNavigationKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _keyboardNavigation.Reset();
        var trackItem = new MenuItem { Header = _settings.Language == "English" ? "Open displayed track" : "Apri il brano visualizzato" };
        trackItem.Click += async (_, _) => await RequestNavigationAsync(lyrics: false);
        RootBorder.ContextMenu = new ContextMenu { ItemsSource = new[] { trackItem } };
    }

    public async Task ShowToastAsync()
    {
        try
        {
        ApplyThemeAndTypography();
        ApplyContent();
        ApplyWidth();

        var wantsArtwork = _settings.ImageMode != "None";
        ArtworkImage.IsVisible = wantsArtwork;

        if (wantsArtwork)
        {
            var useAlbum = _settings.ImageMode == "Album cover";
            ArtworkImage.Source = await _artworkService.LoadAsync(
                useAlbum ? _track.ArtworkUrl : string.Empty,
                _settings.IconFallback || !useAlbum);
        }

        ProgressBar.IsVisible =
            _settings.ShowProgress &&
            _track.DurationSeconds > 0;

        TimeText.IsVisible =
            _settings.ShowSongTime &&
            _track.DurationSeconds > 0;

        if (_closed) return;
        var screen = ResolveTargetScreen();

        PositionAtEnd(screen, entering: true);

        Opacity = 0;
        Show();

        await AnimateInAsync(screen);

        var started = DateTime.UtcNow;
        var initialPosition = _track.PositionSeconds;

        while (!_closed)
        {
            while (!_closed && ((DateTime.UtcNow - started).TotalMilliseconds <
                   _settings.ToastDisplayMs || _openingTrack || DateTime.UtcNow < _statusUntil))
            {
                var elapsed = _track.IsPlaying
                    ? (DateTime.UtcNow - started).TotalSeconds
                    : 0;

                var current = Math.Min(
                    _track.DurationSeconds,
                    Math.Max(0, initialPosition + elapsed));

                UpdateTimeline(current);
                await Task.Delay(250, _lifetime.Token);
            }

            if (_closed) return;
            _exiting = true;
            var exitGeneration = _navigationGeneration;
            if (await AnimateOutAsync(screen, exitGeneration) && _exiting && !_openingTrack &&
                exitGeneration == _navigationGeneration && !_closed)
            {
                Close();
                return;
            }
        }
        }
        catch (OperationCanceledException) when (_closed) { }
    }

    private void ApplyContent()
    {
        TitleText.Text = string.IsNullOrWhiteSpace(_track.Title)
            ? "Spotify"
            : _track.Title;

        ArtistText.Text = string.IsNullOrWhiteSpace(_track.Artist)
            ? _track.Album
            : _track.Artist;

        var description = new LocalizationService().Get("OpenLyricsPlus", _settings.Language);
        ToolTip.SetTip(TitleLink, description);
        ToolTip.SetTip(ArtistLink, description);
        AutomationProperties.SetName(TitleLink, description + ": " + TitleText.Text);
        AutomationProperties.SetName(ArtistLink, description + ": " + ArtistText.Text);
    }

    private async void OnTrackLinkClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await RequestLyricsPlusAsync();
    }

    private async void OnToastPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || e.Handled) return;
        e.Handled = true;
        await RequestLyricsPlusAsync();
    }

    internal Task RequestLyricsPlusAsync() => !CanHandleLyricsPlusRequest
        ? Task.CompletedTask : RequestNavigationAsync(lyrics: true);

    internal Task RequestLyricsPlusFromHotkeyAsync() => !CanHandleLyricsPlusRequest
        ? Task.CompletedTask : RequestNavigationAsync(lyrics: true, fromHotkey: true);

    internal Task RequestCurrentTrackAsync() => !CanHandleLyricsPlusRequest
        ? Task.CompletedTask : RequestNavigationAsync(lyrics: false, currentTrack: true);

    private void OnNavigationKeyDown(object? sender, KeyEventArgs e)
    {
        if (_keyboardNavigation.KeyDown(e.Key.ToString(), e.KeyModifiers == (KeyModifiers.Alt | KeyModifiers.Shift)))
            e.Handled = true;
    }

    private async void OnNavigationKeyUp(object? sender, KeyEventArgs e)
    {
        var action = _keyboardNavigation.KeyUp(e.Key.ToString(), e.KeyModifiers != KeyModifiers.None);
        if (e.Key is Key.Enter or Key.Space or Key.J || action != ToastKeyboardAction.None) e.Handled = true;
        if (action == ToastKeyboardAction.LyricsPlus) await RequestLyricsPlusAsync();
        else if (action == ToastKeyboardAction.CurrentTrack) await RequestCurrentTrackAsync();
    }

    private Task RequestNavigationAsync(bool lyrics, bool currentTrack = false, bool fromHotkey = false) => _actions.RunAsync(async () =>
    {
        if (_openingTrack || _closed || !IsVisible)
            return;

        _openingTrack = true;
        _navigationGeneration++;
        // A visible fade-out is still a live toast. Stop that exit cycle before
        // yielding to the opener so its old deadline cannot cancel this action.
        _exiting = false;
        Opacity = 1;
        try
        {
            Position = GetFinalPosition(ResolveTargetScreen());
            // Use the same immutable snapshot as the displayed title/artist,
            // even if Spotify changes tracks while this toast is still open.
            var result = lyrics ? fromHotkey
                ? await _trackOpener.OpenLyricsPlusFromHotkeyAsync(_lifetime.Token)
                : await _trackOpener.OpenLyricsPlusAsync(_lifetime.Token)
                : currentTrack ? await _trackOpener.OpenCurrentTrackAsync(_lifetime.Token)
                : await _trackOpener.OpenAsync(_track.TrackUri, _lifetime.Token);
            if (_closed) return;
            if (result is not (SpotifyTrackOpenResult.TrackOpened or SpotifyTrackOpenResult.LyricsPlusRequested))
            {
                _showOpenStatus = true;
                var key = result == SpotifyTrackOpenResult.AppActivated
                    ? (lyrics ? "LyricsPlusActivatedFallback" : "TrackUnavailableOpenedSpotify")
                    : (lyrics ? "LyricsPlusOpenFailed" : "TrackOpenFailed");
                TimeText.Text = new LocalizationService().Get(key, _settings.Language);
                TimeText.IsVisible = true;
                _statusUntil = DateTime.UtcNow.AddSeconds(2);
                ToolTip.SetTip(TitleLink, TimeText.Text);
                ToolTip.SetTip(ArtistLink, TimeText.Text);
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch
        {
            if (_closed) return;
            _showOpenStatus = true;
            TimeText.Text = new LocalizationService().Get(lyrics ? "LyricsPlusOpenFailed" : "TrackOpenFailed", _settings.Language);
            TimeText.IsVisible = true;
            _statusUntil = DateTime.UtcNow.AddSeconds(2);
        }
        finally
        {
            _openingTrack = false;
        }
    });

    private void ApplyWidth()
    {
        if (!_settings.AutoWidth)
        {
            Width = _settings.MinWidth;
            return;
        }

        var longest = Math.Max(
            _track.Title?.Length ?? 0,
            _track.Artist?.Length ?? 0);

        var artworkSpace = _settings.ImageMode == "None"
            ? 40
            : 108;

        var estimate =
            artworkSpace +
            90 +
            (longest * Math.Max(5.5, _settings.TitleFontSize * 0.48));

        Width = Math.Clamp(
            estimate,
            Math.Max(220, _settings.MinWidth),
            Math.Max(_settings.MinWidth, _settings.MaxWidth));
    }

    private void ApplyThemeAndTypography()
    {
        var palette = ToastThemePalette.FromSettings(_settings);

        RootBorder.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse(palette.Top), 0),
                new GradientStop(Color.Parse(palette.Bottom), 1)
            }
        };

        RootBorder.BorderBrush =
            new SolidColorBrush(Color.Parse(palette.Border));

        TitleText.Foreground =
            new SolidColorBrush(Color.Parse(palette.Title));

        ArtistText.Foreground =
            new SolidColorBrush(Color.Parse(palette.Secondary));

        TimeText.Foreground =
            new SolidColorBrush(Color.Parse(palette.Secondary));

        ProgressBar.Background =
            new SolidColorBrush(Color.Parse(palette.ProgressBackground));

        ProgressBar.Foreground =
            new SolidColorBrush(Color.Parse(palette.ProgressForeground));

        try
        {
            var font = new FontFamily(_settings.ToastFontFamily);
            TitleText.FontFamily = font;
            ArtistText.FontFamily = font;
            TimeText.FontFamily = font;
        }
        catch
        {
            // Keep Avalonia/platform fallback font if the requested family
            // is unavailable.
        }

        TitleText.FontSize = _settings.TitleFontSize;
        ArtistText.FontSize = _settings.ArtistFontSize;
        TimeText.FontSize = _settings.TimeFontSize;
    }

    private void UpdateTimeline(double current)
    {
        if (_track.DurationSeconds <= 0)
            return;

        ProgressBar.Value = Math.Clamp(
            current / _track.DurationSeconds,
            0,
            1);

        if (!_showOpenStatus)
            TimeText.Text =
                $"{FormatTime(current)} / {FormatTime(_track.DurationSeconds)}";
    }

    private static string FormatTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));

        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");
    }

    private Screen ResolveTargetScreen()
    {
        var all = Screens.All;

        if (_settings.MonitorIndex >= 0 &&
            _settings.MonitorIndex < all.Count)
        {
            return all[_settings.MonitorIndex];
        }

        return Screens.Primary ?? all.First();
    }

    private async Task AnimateInAsync(Screen screen)
    {
        var duration = Math.Max(0, _settings.FadeInMs);
        var steps = Math.Max(1, duration / 16);

        var finalPosition = GetFinalPosition(screen);
        var startPosition = OffsetPosition(
            finalPosition,
            _settings.SlideInDirection,
            _settings.AnimationStyle.Contains(
                "Slide",
                StringComparison.OrdinalIgnoreCase)
                ? _settings.SlideInDistance
                : 0);

        for (var i = 0; i <= steps; i++)
        {
            if (_openingTrack || _navigationGeneration > 0) break;
            var t = (double)i / steps;

            Opacity = _settings.AnimationStyle == "None"
                ? 1
                : t;

            Position = Lerp(
                startPosition,
                finalPosition,
                t);

            if (duration > 0)
                await Task.Delay(16, _lifetime.Token);
        }

        Opacity = 1;
        Position = finalPosition;
    }

    private async Task<bool> AnimateOutAsync(Screen screen, int generation)
    {
        var duration = Math.Max(0, _settings.FadeOutMs);
        var steps = Math.Max(1, duration / 16);

        var startPosition = GetFinalPosition(screen);
        var endPosition = OffsetPosition(
            startPosition,
            _settings.SlideOutDirection,
            _settings.AnimationStyle.Contains(
                "Slide",
                StringComparison.OrdinalIgnoreCase)
                ? _settings.SlideOutDistance
                : 0);

        for (var i = 0; i <= steps; i++)
        {
            if (!_exiting || _openingTrack || generation != _navigationGeneration || DateTime.UtcNow < _statusUntil)
                return false;
            var t = (double)i / steps;

            Opacity = _settings.AnimationStyle == "None"
                ? 1
                : 1 - t;

            Position = Lerp(
                startPosition,
                endPosition,
                t);

            if (duration > 0)
                await Task.Delay(16, _lifetime.Token);
        }
        return true;
    }

    private void PositionAtEnd(Screen screen, bool entering)
        => Position = entering
            ? OffsetPosition(
                GetFinalPosition(screen),
                _settings.SlideInDirection,
                _settings.SlideInDistance)
            : GetFinalPosition(screen);

    private PixelPoint GetFinalPosition(Screen screen)
    {
        var area = screen.WorkingArea;
        var scale = Math.Max(1, screen.Scaling);

        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);

        var marginX = Math.Max(0, _settings.ToastMarginX);
        var marginY = Math.Max(0, _settings.ToastMarginY);

        return _settings.ToastPosition switch
        {
            "TopLeft" => new PixelPoint(
                area.X + marginX,
                area.Y + marginY),

            "TopRight" => new PixelPoint(
                area.Right - width - marginX,
                area.Y + marginY),

            "BottomLeft" => new PixelPoint(
                area.X + marginX,
                area.Bottom - height - marginY),

            _ => new PixelPoint(
                area.Right - width - marginX,
                area.Bottom - height - marginY)
        };
    }

    private static PixelPoint OffsetPosition(
        PixelPoint point,
        string direction,
        int distance)
        => direction switch
        {
            "Down" => new PixelPoint(
                point.X,
                point.Y + distance),

            "Left" => new PixelPoint(
                point.X - distance,
                point.Y),

            "Right" => new PixelPoint(
                point.X + distance,
                point.Y),

            _ => new PixelPoint(
                point.X,
                point.Y - distance)
        };

    private static PixelPoint Lerp(
        PixelPoint a,
        PixelPoint b,
        double t)
        => new(
            (int)Math.Round(
                a.X + ((b.X - a.X) * t)),
            (int)Math.Round(
                a.Y + ((b.Y - a.Y) * t)));
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Interactivity;
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

    public ToastWindow(
        LinuxTrackInfo track,
        LinuxSettings settings,
        ArtworkService artworkService,
        SpotifyTrackOpenService trackOpener)
    {
        InitializeComponent();
        _track = track;
        _settings = settings;
        _artworkService = artworkService;
        _trackOpener = trackOpener;
    }

    public async Task ShowToastAsync()
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

        var screen = ResolveTargetScreen();

        PositionAtEnd(screen, entering: true);

        Opacity = 0;
        Show();

        await AnimateInAsync(screen);

        var started = DateTime.UtcNow;
        var initialPosition = _track.PositionSeconds;

        while ((DateTime.UtcNow - started).TotalMilliseconds <
               _settings.ToastDisplayMs || _openingTrack || DateTime.UtcNow < _statusUntil)
        {
            var elapsed = _track.IsPlaying
                ? (DateTime.UtcNow - started).TotalSeconds
                : 0;

            var current = Math.Min(
                _track.DurationSeconds,
                Math.Max(0, initialPosition + elapsed));

            UpdateTimeline(current);
            await Task.Delay(250);
        }

        await AnimateOutAsync(screen);
        Close();
    }

    private void ApplyContent()
    {
        TitleText.Text = string.IsNullOrWhiteSpace(_track.Title)
            ? "Spotify"
            : _track.Title;

        ArtistText.Text = string.IsNullOrWhiteSpace(_track.Artist)
            ? _track.Album
            : _track.Artist;

        var description = new LocalizationService().Get("OpenTrack", _settings.Language);
        ToolTip.SetTip(TitleLink, description);
        ToolTip.SetTip(ArtistLink, description);
        AutomationProperties.SetName(TitleLink, description + ": " + TitleText.Text);
        AutomationProperties.SetName(ArtistLink, description + ": " + ArtistText.Text);
    }

    private async void OnTrackLinkClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_openingTrack)
            return;

        _openingTrack = true;
        try
        {
            // Use the same immutable snapshot as the displayed title/artist,
            // even if Spotify changes tracks while this toast is still open.
            var result = await _trackOpener.OpenAsync(_track.TrackUri);
            if (result != SpotifyTrackOpenResult.TrackOpened)
            {
                _showOpenStatus = true;
                var key = result == SpotifyTrackOpenResult.AppActivated
                    ? "TrackUnavailableOpenedSpotify"
                    : "TrackOpenFailed";
                TimeText.Text = new LocalizationService().Get(key, _settings.Language);
                TimeText.IsVisible = true;
                _statusUntil = DateTime.UtcNow.AddSeconds(2);
                ToolTip.SetTip(TitleLink, TimeText.Text);
                ToolTip.SetTip(ArtistLink, TimeText.Text);
            }
        }
        catch
        {
            _showOpenStatus = true;
            TimeText.Text = new LocalizationService().Get("TrackOpenFailed", _settings.Language);
            TimeText.IsVisible = true;
            _statusUntil = DateTime.UtcNow.AddSeconds(2);
        }
        finally
        {
            _openingTrack = false;
        }
    }

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
            var t = (double)i / steps;

            Opacity = _settings.AnimationStyle == "None"
                ? 1
                : t;

            Position = Lerp(
                startPosition,
                finalPosition,
                t);

            if (duration > 0)
                await Task.Delay(16);
        }

        Opacity = 1;
        Position = finalPosition;
    }

    private async Task AnimateOutAsync(Screen screen)
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
            var t = (double)i / steps;

            Opacity = _settings.AnimationStyle == "None"
                ? 1
                : 1 - t;

            Position = Lerp(
                startPosition,
                endPosition,
                t);

            if (duration > 0)
                await Task.Delay(16);
        }
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

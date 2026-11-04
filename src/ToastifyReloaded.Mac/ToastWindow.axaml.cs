using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Input;
using ToastifyReloaded.Shared;
using ToastifyReloaded.Mac.Models;
using ToastifyReloaded.Mac.Services;

namespace ToastifyReloaded.Mac;

public partial class ToastWindow : Window
{
    private readonly MacTrackInfo _track;
    private readonly MacSettings _settings;
    private readonly ArtworkService _artworkService;
    private readonly SpotifyTrackOpenService _trackOpenService = new(new ProcessService());
    private readonly ToastNavigationGate _navigation;
    private readonly Func<CancellationToken, Task<SpotifyTrackOpenResult>> _nowPlaying;
    private readonly CancellationTokenSource _lifetime;
    private bool _closed;
    private bool _fading;
    private bool _dragging;
    private bool _openingTrack;
    private Key? _pendingLyricsKey;
    private bool _pendingNowPlayingKey;
    private string? _openStatus;
    private DateTime _statusUntil;

    public ToastWindow(
        MacTrackInfo track,
        MacSettings settings,
        ArtworkService artworkService,
        ToastNavigationGate? navigation = null,
        Func<CancellationToken, Task<SpotifyTrackOpenResult>>? nowPlaying = null,
        CancellationToken lifetimeCancellationToken = default)
    {
        InitializeComponent();
        _track = track;
        _settings = settings;
        _artworkService = artworkService;
        _navigation = navigation ?? new ToastNavigationGate();
        _nowPlaying = nowPlaying ?? _trackOpenService.OpenNowPlayingAsync;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellationToken);
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); };
        RootBorder.AddHandler(PointerReleasedEvent, OnSurfaceReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        RootBorder.AddHandler(PointerPressedEvent, OnSurfacePressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnToastKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnToastKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => { _pendingLyricsKey = null; _pendingNowPlayingKey = false; };
    }

    internal bool CanHandleLyricsPlusRequest => IsVisible && !_closed;
    internal Task RequestLyricsPlusAsync()
        => !CanHandleLyricsPlusRequest || _fading ? Task.CompletedTask : NavigateAsync(lyrics: true);
    internal Task RequestNowPlayingAsync()
        => !CanHandleLyricsPlusRequest || _fading ? Task.CompletedTask : NavigateAsync(lyrics: false);

    public async Task ShowToastAsync()
    {
        try
        {
        _lifetime.Token.ThrowIfCancellationRequested();
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

        _lifetime.Token.ThrowIfCancellationRequested();

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

        _fading = true;
        await AnimateOutAsync(screen);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            if (!_closed) Close();
            _lifetime.Dispose();
        }
    }

    private void ApplyContent()
    {
        TitleText.Text = string.IsNullOrWhiteSpace(_track.Title)
            ? "Spotify"
            : _track.Title;

        ArtistText.Text = string.IsNullOrWhiteSpace(_track.Artist)
            ? _track.Album
            : _track.Artist;

        var label = new LocalizationService().Get("OpenLyricsPlus", _settings.Language);
        AutomationProperties.SetName(TitleLink, $"{label}: {TitleText.Text}");
        AutomationProperties.SetName(ArtistLink, $"{label}: {ArtistText.Text}");
        ToolTip.SetTip(TitleLink, label);
        ToolTip.SetTip(ArtistLink, label);
    }

    private async void OnTrackLinkClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_dragging) return;
        await NavigateAsync(lyrics: true);
    }

    private async Task NavigateAsync(bool lyrics)
    {
        if (_openingTrack || _closed || _fading) return;
        _openingTrack = true;
        TitleLink.IsEnabled = ArtistLink.IsEnabled = false;
        try
        {
            await _navigation.RunAsync(async () =>
            {
            var result = lyrics
                ? await _trackOpenService.OpenLyricsPlusAsync(_lifetime.Token)
                : await _nowPlaying(_lifetime.Token);
            if (_closed) return;
            _openStatus = new LocalizationService().Get(result.Message, _settings.Language);
            ToolTip.SetTip(TitleLink, _openStatus);
            ToolTip.SetTip(ArtistLink, _openStatus);
            TimeText.IsVisible = true;
            TimeText.Text = _openStatus;
            _statusUntil = DateTime.UtcNow.AddSeconds(2);
            });
        }
        catch (OperationCanceledException) when (_closed || _lifetime.IsCancellationRequested) { }
        catch
        {
            if (!_closed)
            {
                _openStatus = new LocalizationService().Get(lyrics ? "LyricsPlusOpenFailed" : "NowPlayingFailed", _settings.Language);
                ToolTip.SetTip(TitleLink, _openStatus);
                ToolTip.SetTip(ArtistLink, _openStatus);
                TimeText.IsVisible = true;
                TimeText.Text = _openStatus;
                _statusUntil = DateTime.UtcNow.AddSeconds(2);
            }
        }
        finally
        {
            _openingTrack = false;
            if (!_closed) TitleLink.IsEnabled = ArtistLink.IsEnabled = true;
        }
    }

    private void OnSurfacePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            _dragging = true;
            BeginMoveDrag(e);
        }
    }

    private async void OnSurfaceReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging) { _dragging = false; e.Handled = true; return; }
        if (e.Handled || e.InitialPressMouseButton != MouseButton.Left || e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        // Buttons own their Click routing; the remaining surface opens lyrics.
        if (e.Source is Control control && IsTextLink(control)) return;
        e.Handled = true;
        await NavigateAsync(lyrics: true);
    }

    private bool IsTextLink(Control control)
    {
        for (Control? current = control; current is not null; current = current.Parent as Control)
            if (current == TitleLink || current == ArtistLink) return true;
        return false;
    }

    private void OnToastKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None &&
            e.Source is Control control && IsTextLink(control))
        {
            e.Handled = true;
            _pendingLyricsKey ??= e.Key;
        }
        else if (e.Key == Key.J && e.KeyModifiers == (KeyModifiers.Alt | KeyModifiers.Shift))
        {
            e.Handled = true;
            _pendingNowPlayingKey = true;
        }
    }

    private async void OnToastKeyUp(object? sender, KeyEventArgs e)
    {
        if (_pendingLyricsKey == e.Key)
        {
            _pendingLyricsKey = null;
            e.Handled = true;
            await RequestLyricsPlusAsync();
        }
        else if (_pendingNowPlayingKey && e.Key == Key.J)
        {
            _pendingNowPlayingKey = false;
            e.Handled = true;
            await RequestNowPlayingAsync();
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

        if (_openStatus is null)
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
            if (_closed || _lifetime.IsCancellationRequested) return;
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

        var startPosition = Position;
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
            if (_closed || _lifetime.IsCancellationRequested) return;
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

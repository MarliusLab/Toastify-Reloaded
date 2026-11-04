using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using ToastifyReloaded.Models;
using ToastifyReloaded.Services;
using Forms = System.Windows.Forms;

namespace ToastifyReloaded;

public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _timelineTimer;
    private readonly AppSettings _settings;
    private readonly TimeSpan _timelineStartPosition;
    private readonly TimeSpan _trackDuration;
    private readonly bool _timelineIsPlaying;
    private readonly DateTimeOffset _timelineSnapshotAt;
    private bool _fadeOutStarted;
    private DispatcherTimer? _enterCompletionTimer, _exitCompletionTimer;
    private int _animationGeneration;
    private bool _displayTimerDeferred;
    private bool _secondPositionPassScheduled;
    private readonly Func<CancellationToken, Task<SpotifyTrackOpenResult>> _openLyricsPlus;
    private readonly Func<CancellationToken, Task<SpotifyTrackOpenResult>> _openLyricsPlusFromHotkey;
    private readonly Func<CancellationToken, Task<SpotifyTrackOpenResult>> _openNowPlaying;
    private readonly Func<CancellationToken, Task<SpotifyTrackOpenResult>> _activateSpotify;
    private readonly Func<ModifierKeys> _getModifiers;
    private readonly ToastNowPlayingHotkeyCoordinator? _toastHotkeys;
    private IDisposable? _toastHotkeyLease;
    private bool _globalShortcutAvailable;
    private SpotifyTrackOpenResult _lastOpenResult = SpotifyTrackOpenResult.LyricsPlusRequested;
    private bool _lastWasLyricsPlus = true;
    private readonly CancellationTokenSource _openCancellation = new();
    private bool _openingSpotify;
    private bool _closed;

    public ToastWindow(TrackInfo track, AppSettings settings)
        : this(track, settings, new SpotifyTrackOpenService(), hotkeys: null)
    {
    }

    internal ToastWindow(TrackInfo track, AppSettings settings, ToastNowPlayingHotkeyCoordinator? hotkeys)
        : this(track, settings, new SpotifyTrackOpenService(), hotkeys)
    {
    }

    private ToastWindow(TrackInfo track, AppSettings settings, SpotifyTrackOpenService service,
        ToastNowPlayingHotkeyCoordinator? hotkeys)
        : this(track, settings, service.OpenLyricsPlusAsync, hotkeys: hotkeys,
            openNowPlaying: service.OpenNowPlayingAsync,
            activateSpotify: token => service.OpenAsync(cancellationToken: token),
            openLyricsPlusFromHotkey: service.OpenLyricsPlusFromHotkeyAsync)
    {
    }

    internal ToastWindow(TrackInfo track, AppSettings settings,
        Func<CancellationToken, Task<SpotifyTrackOpenResult>> openLyricsPlus,
        Func<ModifierKeys>? getModifiers = null,
        ToastNowPlayingHotkeyCoordinator? hotkeys = null,
        Func<CancellationToken, Task<SpotifyTrackOpenResult>>? openNowPlaying = null,
        Func<CancellationToken, Task<SpotifyTrackOpenResult>>? activateSpotify = null,
        Func<CancellationToken, Task<SpotifyTrackOpenResult>>? openLyricsPlusFromHotkey = null)
    {
        _openLyricsPlus = openLyricsPlus;
        _openLyricsPlusFromHotkey = openLyricsPlusFromHotkey ?? openLyricsPlus;
        _openNowPlaying = openNowPlaying ?? openLyricsPlus;
        _activateSpotify = activateSpotify ?? _openNowPlaying;
        _getModifiers = getModifiers ?? (() => Keyboard.Modifiers);
        _toastHotkeys = hotkeys;
        _settings = settings;
        _timelineStartPosition = track.Position < TimeSpan.Zero ? TimeSpan.Zero : track.Position;
        _trackDuration = track.Duration < TimeSpan.Zero ? TimeSpan.Zero : track.Duration;
        _timelineIsPlaying = track.IsPlaying;
        _timelineSnapshotAt = DateTimeOffset.UtcNow;
        InitializeComponent();

        Height = Math.Max(50, settings.ToastHeight);

        if (string.Equals(settings.ToastTitlesOrder, "ArtistTrack", StringComparison.OrdinalIgnoreCase))
        {
            Title1.Text = track.Artist;
            Title2.Text = track.Title;
        }
        else
        {
            Title1.Text = track.Title;
            Title2.Text = track.Artist;
        }
        SetOpenSpotifyHint(SpotifyTrackOpenResult.LyricsPlusRequested);

        ToastBorder.BorderThickness = new Thickness(Math.Max(0, settings.ToastBorderThickness));
        ToastBorder.CornerRadius = new CornerRadius(
            Math.Max(0, settings.ToastCornerTopLeft),
            Math.Max(0, settings.ToastCornerTopRight),
            Math.Max(0, settings.ToastCornerBottomRight),
            Math.Max(0, settings.ToastCornerBottomLeft));
        ToastBorder.BorderBrush = new SolidColorBrush(ParseColor(settings.ToastBorderColor, MediaColor.FromArgb(255, 41, 41, 41)));
        TopGradientStop.Color = ParseColor(settings.ToastColorTop, MediaColor.FromArgb(255, 85, 85, 85));
        BottomGradientStop.Color = ParseColor(settings.ToastColorBottom, MediaColor.FromArgb(255, 21, 21, 21));
        TopGradientStop.Offset = Math.Clamp(settings.ToastColorTopOffset, 0, 1);
        BottomGradientStop.Offset = Math.Clamp(settings.ToastColorBottomOffset, TopGradientStop.Offset, 1);
        Title1.Foreground = new SolidColorBrush(ParseColor(settings.ToastTitle1Color, MediaColors.White));
        Title2.Foreground = new SolidColorBrush(ParseColor(settings.ToastTitle2Color, MediaColor.FromArgb(255, 240, 240, 240)));
        Title1.FontSize = Math.Clamp(settings.ToastTitle1FontSize, 6, 40);
        Title2.FontSize = Math.Clamp(settings.ToastTitle2FontSize, 6, 40);

        if (settings.ToastTitle1DropShadow)
            Title1.Effect = new DropShadowEffect { ShadowDepth = Math.Clamp(settings.ToastTitle1ShadowDepth, 0, 8), BlurRadius = Math.Clamp(settings.ToastTitle1ShadowBlur, 0, 24), Opacity = 0.8 };
        if (settings.ToastTitle2DropShadow)
            Title2.Effect = new DropShadowEffect { ShadowDepth = Math.Clamp(settings.ToastTitle2ShadowDepth, 0, 8), BlurRadius = Math.Clamp(settings.ToastTitle2ShadowBlur, 0, 24), Opacity = 0.8 };

        SongProgressBarContainer.Background = new SolidColorBrush(ParseColor(settings.SongProgressBarBackgroundColor, MediaColor.FromArgb(255, 51, 51, 51)));
        var progressBrush = new SolidColorBrush(ParseColor(settings.SongProgressBarForegroundColor, MediaColor.FromArgb(255, 160, 160, 160)));
        SongProgressBarLine.Background = progressBrush;
        SongProgressBarLineEllipse.Fill = progressBrush;
        SongProgressBar.Visibility = settings.ShowSongProgressBar ? Visibility.Visible : Visibility.Collapsed;
        SongDurationText.Visibility = settings.ShowSongDuration ? Visibility.Visible : Visibility.Collapsed;
        SongDurationText.Foreground = Title2.Foreground;
        SongTimelineGrid.Visibility = settings.ShowSongProgressBar || settings.ShowSongDuration
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateTimelineDisplay();

        ApplyArtwork(track);
        ApplyWidth();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.ToastDurationMs, 500, 30000)) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            StartExitAnimation();
        };

        _timelineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timelineTimer.Tick += (_, _) => UpdateTimelineDisplay();
        SongProgressBarContainer.SizeChanged += (_, _) => UpdateTimelineDisplay();

        Loaded += (_, _) =>
        {
            UpdateToastHotkeyLease();
            UpdateTimelineDisplay();
            if (settings.ShowSongProgressBar || settings.ShowSongDuration)
                _timelineTimer.Start();
            PositionToast();
            ScheduleSecondPositionPass();
        };
        IsVisibleChanged += (_, _) => UpdateToastHotkeyLease();
        Closed += (_, _) =>
        {
            _closed = true;
            _toastHotkeyLease?.Dispose();
            _toastHotkeyLease = null;
            _openCancellation.Cancel();
            _openCancellation.Dispose();
            _timer.Stop();
            _timelineTimer.Stop();
            _animationGeneration++;
            _enterCompletionTimer?.Stop();
            _exitCompletionTimer?.Stop();
        };
    }

    private void UpdateToastHotkeyLease()
    {
        if (IsVisible && !_closed && _toastHotkeys is not null)
        {
            _toastHotkeyLease ??= _toastHotkeys.Attach(OpenNowPlayingAsync, available =>
            {
                _globalShortcutAvailable = available;
                if (!_closed) SetOpenSpotifyHint(_lastOpenResult);
            });
        }
        else
        {
            _toastHotkeyLease?.Dispose();
            _toastHotkeyLease = null;
            _globalShortcutAvailable = false;
        }
    }

    public void ShowTimed()
    {
        PrepareEnterState();
        Show();
        StartEnterAnimation();
    }

    private void PrepareEnterState()
    {
        _animationGeneration++;
        var style = NormalizeAnimationStyle(_settings.ToastAnimationStyle);
        var fade = style is "Fade" or "FadeSlide";
        var slide = style is "Slide" or "FadeSlide";

        Opacity = fade ? 0 : 1;
        var (x, y) = slide ? GetEnterOffset() : (0d, 0d);
        ToastTranslate.X = x;
        ToastTranslate.Y = y;
    }

    private void StartEnterAnimation()
    {
        var style = NormalizeAnimationStyle(_settings.ToastAnimationStyle);
        if (style == "None")
        {
            Opacity = 1;
            ToastTranslate.X = 0;
            ToastTranslate.Y = 0;
            StartDisplayTimer();
            return;
        }

        var durationMs = Math.Clamp(_settings.ToastFadeInMs, 0, 5000);
        if (durationMs == 0)
        {
            Opacity = 1;
            ToastTranslate.X = 0;
            ToastTranslate.Y = 0;
            StartDisplayTimer();
            return;
        }

        var fade = style is "Fade" or "FadeSlide";
        var slide = style is "Slide" or "FadeSlide";
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (fade)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration)
            {
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = easing
            });
        }

        if (slide)
        {
            var (startX, startY) = GetEnterOffset();
            if (Math.Abs(startX) > 0.001)
            {
                ToastTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(startX, 0, duration)
                {
                    FillBehavior = FillBehavior.HoldEnd,
                    EasingFunction = easing
                });
            }
            if (Math.Abs(startY) > 0.001)
            {
                ToastTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(startY, 0, duration)
                {
                    FillBehavior = FillBehavior.HoldEnd,
                    EasingFunction = easing
                });
            }
        }

        _enterCompletionTimer = ScheduleAfter(durationMs, () =>
        {
            _enterCompletionTimer = null;
            BeginAnimation(OpacityProperty, null);
            ToastTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            ToastTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            Opacity = 1;
            ToastTranslate.X = 0;
            ToastTranslate.Y = 0;
            StartDisplayTimer();
        });
    }

    private void StartDisplayTimer()
    {
        if (_closed) return;
        if (_openingSpotify) { _displayTimerDeferred = true; return; }
        _displayTimerDeferred = false;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.ToastDurationMs, 500, 30000));
        _timer.Start();
    }

    private void StartExitAnimation()
    {
        if (_closed || _fadeOutStarted)
            return;
        if (_openingSpotify) { _displayTimerDeferred = true; return; }

        _fadeOutStarted = true;
        _timelineTimer.Stop();
        var style = NormalizeAnimationStyle(_settings.ToastAnimationStyle);
        if (style == "None")
        {
            Close();
            return;
        }

        var durationMs = Math.Clamp(_settings.ToastFadeOutMs, 0, 5000);
        if (durationMs == 0)
        {
            Close();
            return;
        }

        var fade = style is "Fade" or "FadeSlide";
        var slide = style is "Slide" or "FadeSlide";
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };

        if (fade)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 0, duration)
            {
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = easing
            });
        }

        if (slide)
        {
            var (exitX, exitY) = GetExitOffset();
            if (Math.Abs(exitX) > 0.001)
            {
                ToastTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, exitX, duration)
                {
                    FillBehavior = FillBehavior.HoldEnd,
                    EasingFunction = easing
                });
            }
            if (Math.Abs(exitY) > 0.001)
            {
                ToastTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, exitY, duration)
                {
                    FillBehavior = FillBehavior.HoldEnd,
                    EasingFunction = easing
                });
            }
        }

        _exitCompletionTimer = ScheduleAfter(durationMs, Close);
    }

    private (double X, double Y) GetEnterOffset()
    {
        var distance = Math.Clamp(_settings.ToastSlideInDistance ?? _settings.ToastSlideDistance, 0, 300);
        var direction = _settings.ToastSlideInDirection ?? _settings.ToastAnimationDirection;
        return NormalizeAnimationDirection(direction) switch
        {
            "Down" => (0, -distance),
            "Left" => (distance, 0),
            "Right" => (-distance, 0),
            _ => (0, distance) // Up: enter from below and travel upward.
        };
    }

    private (double X, double Y) GetExitOffset()
    {
        var distance = Math.Clamp(_settings.ToastSlideOutDistance ?? _settings.ToastSlideDistance, 0, 300);
        var direction = _settings.ToastSlideOutDirection ?? _settings.ToastAnimationDirection;
        return NormalizeAnimationDirection(direction) switch
        {
            "Down" => (0, distance),
            "Left" => (-distance, 0),
            "Right" => (distance, 0),
            _ => (0, -distance)
        };
    }

    private static string NormalizeAnimationStyle(string? value) => value switch
    {
        "Slide" => "Slide",
        "FadeSlide" => "FadeSlide",
        "None" => "None",
        _ => "Fade"
    };

    private static string NormalizeAnimationDirection(string? value) => value switch
    {
        "Down" => "Down",
        "Left" => "Left",
        "Right" => "Right",
        _ => "Up"
    };

    private DispatcherTimer ScheduleAfter(int milliseconds, Action action)
    {
        var generation = _animationGeneration;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (IsLoaded && !_closed && generation == _animationGeneration)
                action();
        };
        timer.Start();
        return timer;
    }

    private void UpdateTimelineDisplay()
    {
        var position = GetEstimatedPosition();

        if (_settings.ShowSongDuration)
        {
            SongDurationText.Text = _trackDuration > TimeSpan.Zero
                ? $"{FormatTimeline(position)} / {FormatTimeline(_trackDuration)}"
                : $"{FormatTimeline(position)} / --:--";
        }

        if (_settings.ShowSongProgressBar)
        {
            var available = Math.Max(0, SongProgressBarContainer.ActualWidth);
            var ratio = _trackDuration > TimeSpan.Zero
                ? Math.Clamp(position.TotalMilliseconds / _trackDuration.TotalMilliseconds, 0, 1)
                : 0;
            SongProgressBarFill.Width = available * ratio;
        }
    }

    private TimeSpan GetEstimatedPosition()
    {
        var position = _timelineStartPosition;
        if (_timelineIsPlaying)
            position += DateTimeOffset.UtcNow - _timelineSnapshotAt;

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        if (_trackDuration > TimeSpan.Zero && position > _trackDuration)
            position = _trackDuration;
        return position;
    }

    private static string FormatTimeline(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    private void ApplyArtwork(TrackInfo track)
    {
        var mode = _settings.ToastImageMode?.Trim() ?? "AlbumCover";

        if (mode.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            HideArtwork();
            return;
        }

        if (mode.Equals("ToastifyIcon", StringComparison.OrdinalIgnoreCase))
        {
            // The historical Toastify logo is already loaded by the XAML resource.
            return;
        }

        // AlbumCover is the default. Use the Windows media-session thumbnail supplied by Spotify.
        if (track.ArtworkBytes is { Length: > 0 } && TryLoadArtwork(track.ArtworkBytes))
            return;

        if (!_settings.ToastImageFallbackToIcon)
            HideArtwork();
    }

    private bool TryLoadArtwork(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            AlbumArt.Source = bitmap;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void HideArtwork()
    {
        AlbumArt.Visibility = Visibility.Collapsed;
        ArtworkColumn.Width = new GridLength(0);
        ToastContentGrid.Margin = new Thickness(10, 15, 8, 4);
    }

    private void ApplyWidth()
    {
        if (!_settings.ToastAutoWidth)
        {
            Width = Math.Max(150, _settings.ToastWidth);
            return;
        }

        var minimum = Math.Max(150, _settings.ToastMinWidth);
        var maximum = Math.Max(minimum, _settings.ToastMaxWidth);

        var textWidth = Math.Max(
            MeasureTextWidth(Title1),
            MeasureTextWidth(Title2));

        if (_settings.ShowSongDuration && SongDurationText.Visibility == Visibility.Visible)
        {
            var timelineWidth = MeasureTextWidth(SongDurationText) + (_settings.ShowSongProgressBar ? 90 : 0);
            textWidth = Math.Max(textWidth, timelineWidth);
        }

        var artworkSpace = AlbumArt.Visibility == Visibility.Visible
            ? ArtworkColumn.Width.Value
            : 0;

        var horizontalContentMargin = ToastContentGrid.Margin.Left + ToastContentGrid.Margin.Right;
        var borderSpace = Math.Max(0, ToastBorder.BorderThickness.Left) * 2;
        const double trailingSafetyPadding = 14;

        var desired = artworkSpace + horizontalContentMargin + textWidth + trailingSafetyPadding + borderSpace;
        Width = Math.Clamp(Math.Ceiling(desired), minimum, maximum);
    }

    private double MeasureTextWidth(TextBlock textBlock)
    {
        if (string.IsNullOrEmpty(textBlock.Text))
            return 0;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(
            textBlock.FontFamily,
            textBlock.FontStyle,
            textBlock.FontWeight,
            textBlock.FontStretch);

        var formatted = new FormattedText(
            textBlock.Text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            textBlock.FontSize,
            System.Windows.Media.Brushes.White,
            dpi);

        return formatted.WidthIncludingTrailingWhitespace;
    }

    private void PositionToast()
    {
        var preset = string.IsNullOrWhiteSpace(_settings.ToastPositionPreset)
            ? "BottomRight"
            : _settings.ToastPositionPreset;

        if (preset.Equals("Custom", StringComparison.OrdinalIgnoreCase) && _settings.ToastMonitorIndex < 0)
        {
            // Preserve historical absolute-coordinate behavior when Custom is used without a monitor selection.
            var area = SystemParameters.WorkArea;
            var left = _settings.PositionLeft;
            var top = _settings.PositionTop;

            if (left < area.Left || left + ActualWidth > area.Right)
                left = area.Right - ActualWidth;
            if (top < area.Top || top + ActualHeight > area.Bottom)
                top = area.Bottom - ActualHeight;

            Left = left;
            Top = top;
            return;
        }

        var screen = ResolveTargetScreen();
        var areaPx = screen.WorkingArea;
        var helper = new WindowInteropHelper(this);
        var hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero)
            return;

        GetWindowRect(hwnd, out var rect);
        var widthPx = Math.Max(1, rect.Right - rect.Left);
        var heightPx = Math.Max(1, rect.Bottom - rect.Top);
        var margin = (int)Math.Round(Math.Clamp(_settings.ToastScreenMargin, 0, 200));

        int x;
        int y;

        if (preset.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            x = areaPx.Left + (int)Math.Round(Math.Max(0, _settings.PositionLeft));
            y = areaPx.Top + (int)Math.Round(Math.Max(0, _settings.PositionTop));
        }
        else
        {
            var left = areaPx.Left + margin;
            var centerX = areaPx.Left + (areaPx.Width - widthPx) / 2;
            var right = areaPx.Right - widthPx - margin;
            var top = areaPx.Top + margin;
            var centerY = areaPx.Top + (areaPx.Height - heightPx) / 2;
            var bottom = areaPx.Bottom - heightPx - margin;

            (x, y) = preset switch
            {
                "TopLeft" => (left, top),
                "TopCenter" => (centerX, top),
                "TopRight" => (right, top),
                "MiddleLeft" => (left, centerY),
                "Center" => (centerX, centerY),
                "MiddleRight" => (right, centerY),
                "BottomLeft" => (left, bottom),
                "BottomCenter" => (centerX, bottom),
                _ => (right, bottom)
            };
        }

        x = Math.Clamp(x, areaPx.Left, Math.Max(areaPx.Left, areaPx.Right - widthPx));
        y = Math.Clamp(y, areaPx.Top, Math.Max(areaPx.Top, areaPx.Bottom - heightPx));

        _ = SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private Forms.Screen ResolveTargetScreen()
    {
        var screens = Forms.Screen.AllScreens;
        if (_settings.ToastMonitorIndex >= 0 && _settings.ToastMonitorIndex < screens.Length)
            return screens[_settings.ToastMonitorIndex];
        return Forms.Screen.PrimaryScreen ?? screens.First();
    }

    private void ScheduleSecondPositionPass()
    {
        if (_secondPositionPassScheduled)
            return;

        _secondPositionPassScheduled = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            PositionToast();
        };
        timer.Start();
    }

    private async void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch { }
            }
            e.Handled = true;
            return;
        }
        e.Handled = true;
        await OpenLyricsPlusAsync();
    }

    private async void Title_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || _getModifiers() != ModifierKeys.None)
            return;
        e.Handled = true;
        if (!e.IsRepeat) await OpenLyricsPlusAsync();
    }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.J || _getModifiers() != (ModifierKeys.Alt | ModifierKeys.Shift)) return;
        e.Handled = true;
        if (!e.IsRepeat) await OpenNowPlayingAsync();
    }

    internal bool CanHandleLyricsPlusRequest => IsVisible && !_closed;

    internal Task RequestLyricsPlusAsync()
        => !CanHandleLyricsPlusRequest ? Task.CompletedTask : OpenLyricsPlusAsync();

    internal Task RequestLyricsPlusFromHotkeyAsync()
        => !CanHandleLyricsPlusRequest ? Task.CompletedTask : OpenSpotifyAsync(lyricsPlus: true, fromHotkey: true);

    private Task OpenLyricsPlusAsync() => OpenSpotifyAsync(lyricsPlus: true);

    private Task OpenNowPlayingAsync() => OpenSpotifyAsync(lyricsPlus: false);

    private async Task OpenSpotifyAsync(bool lyricsPlus, bool fromHotkey = false)
    {
        if (_openingSpotify || _closed) return;
        _openingSpotify = true;
        var resumeTimer = _timer.IsEnabled || _enterCompletionTimer is not null || _displayTimerDeferred;
        var resumeExit = _fadeOutStarted;
        _displayTimerDeferred = false;
        _timer.Stop();
        if (_enterCompletionTimer is not null || resumeExit)
        {
            // A still-visible toast remains actionable during either animation.
            // Invalidate its old completion before yielding to URI/guard work.
            _animationGeneration++;
            _enterCompletionTimer?.Stop();
            _exitCompletionTimer?.Stop();
            _enterCompletionTimer = _exitCompletionTimer = null;
            BeginAnimation(OpacityProperty, null);
            ToastTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            ToastTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            Opacity = 1;
            ToastTranslate.X = ToastTranslate.Y = 0;
            _fadeOutStarted = false;
            if (_settings.ShowSongProgressBar || _settings.ShowSongDuration) _timelineTimer.Start();
        }
        try
        {
            async Task NavigateAsync()
            {
                _lastWasLyricsPlus = lyricsPlus;
                // Lyrics Plus uses one fixed page URI, not an injected Spotify
                // shortcut. A foreign Alt+Shift+J owner cannot block this click.
                var action = lyricsPlus ? (fromHotkey ? _openLyricsPlusFromHotkey : _openLyricsPlus)
                    : _toastHotkeys?.CanForwardShortcut == false ? _activateSpotify : _openNowPlaying;
                var result = await action(_openCancellation.Token);
                if (!_closed) SetOpenSpotifyHint(result);
            }
            if (_toastHotkeys is null) await NavigateAsync();
            else await _toastHotkeys.RunSuspendedAsync(NavigateAsync);
        }
        catch (OperationCanceledException) when (_closed || _openCancellation.IsCancellationRequested) { }
        catch
        {
            if (!_closed) SetOpenSpotifyHint(SpotifyTrackOpenResult.Failed);
        }
        finally
        {
            _openingSpotify = false;
            if (!_closed)
            {
                if (resumeExit) StartExitAnimation();
                else if (resumeTimer || _displayTimerDeferred) StartDisplayTimer();
            }
        }
    }

    private void SetOpenSpotifyHint(SpotifyTrackOpenResult result)
    {
        _lastOpenResult = result;
        var italian = string.Equals(_settings.ApplicationLanguage, "Italian", StringComparison.OrdinalIgnoreCase);
        var hint = result switch
        {
            SpotifyTrackOpenResult.Failed => italian
                ? (_lastWasLyricsPlus ? "Impossibile aprire Lyrics Plus." : "Impossibile completare il comando In riproduzione.") + " Verifica Spotify, rilascia i tasti e riprova."
                : (_lastWasLyricsPlus ? "Could not open Lyrics Plus." : "Could not complete the Now Playing command.") + " Check Spotify, release the keys and try again.",
            SpotifyTrackOpenResult.AppActivated => italian
                ? (_lastWasLyricsPlus ? "Spotify aperto; Lyrics Plus non confermato. Verifica che sia abilitato nella scheda Reloaded." : "Spotify aperto; comando In riproduzione non confermato. Rilascia i tasti e riprova.")
                : (_lastWasLyricsPlus ? "Spotify opened; Lyrics Plus was not confirmed. Check that it is enabled in the Reloaded tab." : "Spotify opened; Now Playing command was not confirmed. Release the keys and try again."),
            _ => italian
                ? "Apri il testo in Lyrics Plus con clic o Invio/Spazio; Alt+Maiusc+J va al brano In riproduzione. Ctrl+trascina per spostare il toast."
                : "Open Lyrics Plus with a click or Enter/Space; Alt+Shift+J goes to Now Playing. Ctrl+drag to move the toast."
        };
        if (!_globalShortcutAvailable)
            hint += italian
                ? " Scorciatoia globale occupata o disattivata; clic e Invio/Spazio restano disponibili per Lyrics Plus."
                : " Global shortcut occupied or disabled; click and Enter/Space remain available for Lyrics Plus.";
        ToastBorder.ToolTip = hint;
        System.Windows.Automation.AutomationProperties.SetName(Title1, $"{Title1.Text}. {hint}");
        System.Windows.Automation.AutomationProperties.SetName(Title2, $"{Title2.Text}. {hint}");
    }

    private static MediaColor ParseColor(string value, MediaColor fallback)
    {
        try { return (MediaColor)MediaColorConverter.ConvertFromString(value)!; }
        catch { return fallback; }
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);
}

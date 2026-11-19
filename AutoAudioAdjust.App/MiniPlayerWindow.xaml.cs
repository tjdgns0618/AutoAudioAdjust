using System.Windows;
using System.Windows.Media.Imaging;
using AutoAudioAdjust.App.Services;

namespace AutoAudioAdjust.App;

/// <summary>
/// Small player that opens above the tray icon. Shows the current song and artwork, transport
/// buttons, a seek bar, the YouTube Music volume, the measured level and the auto adjust switch.
/// Can be dragged from any empty spot. Stays open until the X button (or Esc, or the tray icon) closes it.
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private static readonly string VolumeIcon = char.ConvertFromUtf32(0xE767);
    private static readonly string MutedIcon = char.ConvertFromUtf32(0xE74F);

    private string? _artworkUrl;
    private bool _syncing;
    private bool _seeking;
    private DateTime _seekHoldUntil;
    private Point? _movedTo; // where the user dragged the flyout; null = next to the tray

    public MiniPlayerWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                Hide();
            }
        };

        // Drag from any empty spot. Buttons and sliders handle their own clicks, so they never get here.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != System.Windows.Input.MouseButtonState.Pressed)
            {
                return;
            }

            DragMove();
            _movedTo = new Point(Left, Top);
        };

        // The slider marks clicks on its track as handled (IsMoveToPointEnabled), so listen to handled events too.
        SeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent,
            new System.Windows.Input.MouseButtonEventHandler(SeekSlider_PreviewMouseLeftButtonDown), handledEventsToo: true);
        SeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent,
            new System.Windows.Input.MouseButtonEventHandler(SeekSlider_PreviewMouseLeftButtonUp), handledEventsToo: true);
    }

    public event Action? OpenRequested;
    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action<bool>? AutoAdjustChanged;
    public event Action<double>? SeekRequested;
    public event Action<int>? VolumeChanged;
    public event Action? MuteToggled;

    /// <summary>
    /// Shows the flyout at the bottom-right of the work area, just above the taskbar, or where the
    /// user last dragged it during this run.
    /// </summary>
    public void ShowNearTray()
    {
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        if (_movedTo is { } moved)
        {
            // Keep the spot the user chose, but pull it back inside the screen if needed.
            Left = Math.Clamp(moved.X, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
            Top = Math.Clamp(moved.Y, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
        }
        else
        {
            Left = area.Right - ActualWidth - 2;
            Top = area.Bottom - ActualHeight - 2;
        }

        Activate();
    }

    public void Update(NowPlaying nowPlaying, double levelDb, float? volume, bool autoAdjustOn)
    {
        TitleText.Text = nowPlaying.HasTrack ? nowPlaying.Title : "재생 중인 곡 없음";
        ArtistText.Text = nowPlaying.HasTrack ? nowPlaying.Artist : "유튜브 뮤직에서 곡을 재생해 주세요";
        PlayPauseButton.Content = nowPlaying.IsPlaying ? "" : "";
        PlayPauseButton.ToolTip = nowPlaying.IsPlaying ? "일시정지" : "재생";
        PreviousButton.IsEnabled = NextButton.IsEnabled = PlayPauseButton.IsEnabled = nowPlaying.HasPlayer;
        LevelRun.Text = Audio.LevelNormalizer.IsSilent(levelDb) ? "—" : $"{Audio.Loudness.ToScale(levelDb):0}";
        VolumeRun.Text = volume is { } v ? $"{v * 100:0}%" : "—%";
        AutoToggle.IsChecked = autoAdjustOn;
        SetArtwork(nowPlaying.ArtworkUrl);
        UpdateSeekBar(nowPlaying);
        UpdateVolume(nowPlaying);
    }

    private void UpdateSeekBar(NowPlaying nowPlaying)
    {
        var duration = nowPlaying.HasTrack ? nowPlaying.Duration : null;
        SeekSlider.IsEnabled = duration is not null;
        DurationText.Text = duration is { } d ? FormatTime(d) : "0:00";
        if (_seeking || DateTime.UtcNow < _seekHoldUntil)
        {
            return; // the user is dragging, or the page has not caught up with the last seek yet
        }

        _syncing = true;
        SeekSlider.Maximum = duration ?? 1;
        SeekSlider.Value = duration is null ? 0 : Math.Clamp(nowPlaying.Position ?? 0, 0, duration.Value);
        _syncing = false;
        PositionText.Text = duration is null ? "0:00" : FormatTime(SeekSlider.Value);
    }

    private void UpdateVolume(NowPlaying nowPlaying)
    {
        var available = nowPlaying.Volume is not null;
        VolumeSlider.IsEnabled = MuteButton.IsEnabled = available;
        MuteButton.Content = nowPlaying.Muted ? MutedIcon : VolumeIcon;
        if (nowPlaying.Volume is not { } volume)
        {
            VolumeText.Text = "—%";
            return;
        }

        if (!VolumeSlider.IsMouseCaptureWithin)
        {
            _syncing = true;
            VolumeSlider.Value = volume;
            _syncing = false;
            VolumeText.Text = nowPlaying.Muted ? "음소거" : $"{volume:0}%";
        }
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private void SetArtwork(string? url)
    {
        if (url == _artworkUrl)
        {
            return;
        }

        _artworkUrl = url;
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            ArtworkBrush.ImageSource = null;
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = 144;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            ArtworkBrush.ImageSource = image;
        }
        catch (Exception)
        {
            ArtworkBrush.ImageSource = null;
        }
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        OpenRequested?.Invoke();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => _seeking = true;

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_seeking)
        {
            return;
        }

        _seeking = false;
        _seekHoldUntil = DateTime.UtcNow.AddSeconds(1.2);
        SeekRequested?.Invoke(SeekSlider.Value);
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing && SeekSlider.IsEnabled)
        {
            PositionText.Text = FormatTime(e.NewValue); // live preview while dragging
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !IsLoaded)
        {
            return;
        }

        var volume = (int)Math.Round(e.NewValue);
        VolumeText.Text = $"{volume}%";
        VolumeChanged?.Invoke(volume);
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        MuteButton.Content = (MuteButton.Content as string) == MutedIcon ? VolumeIcon : MutedIcon;
        MuteToggled?.Invoke();
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke();

    private void NextButton_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke();

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke();

    private void AutoToggle_Click(object sender, RoutedEventArgs e) => AutoAdjustChanged?.Invoke(AutoToggle.IsChecked == true);
}

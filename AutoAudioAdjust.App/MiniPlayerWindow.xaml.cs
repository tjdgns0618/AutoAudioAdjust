using System.Windows;
using System.Windows.Media.Imaging;
using AutoAudioAdjust.App.Services;

namespace AutoAudioAdjust.App;

/// <summary>
/// Small player that opens above the tray icon. Shows the current song and artwork, transport
/// buttons, the measured level and the auto adjust switch. Hides itself when it loses focus.
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private string? _artworkUrl;

    public MiniPlayerWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => Hide();
    }

    public event Action? OpenRequested;
    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action<bool>? AutoAdjustChanged;

    /// <summary>Shows the flyout at the bottom-right of the work area, just above the taskbar.</summary>
    public void ShowNearTray()
    {
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 2;
        Top = area.Bottom - ActualHeight - 2;
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

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke();

    private void NextButton_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke();

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke();

    private void AutoToggle_Click(object sender, RoutedEventArgs e) => AutoAdjustChanged?.Invoke(AutoToggle.IsChecked == true);
}

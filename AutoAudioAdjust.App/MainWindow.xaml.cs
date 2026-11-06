using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AutoAudioAdjust.App.Audio;
using AutoAudioAdjust.App.Models;
using AutoAudioAdjust.App.Services;

namespace AutoAudioAdjust.App;

public partial class MainWindow : Window
{
    private readonly AudioSessionService _audioSessions = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly DispatcherTimer _meterTimer;
    private readonly LevelNormalizer _normalizer = new();
    private AppSettings _settings = new();
    private AudioSessionItem? _selectedSession;
    private bool _isRunning;
    private bool _isLoading = true;

    public MainWindow()
    {
        InitializeComponent();

        _meterTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _meterTimer.Tick += MeterTimer_Tick;

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsStore.Load();
        TargetSlider.Value = _settings.TargetLevelDb;
        MinVolumeSlider.Value = _settings.MinimumVolumePercent;
        MaxVolumeSlider.Value = _settings.MaximumVolumePercent;
        _isLoading = false;
        UpdateSettingLabels();
        RefreshSessions(_settings.PreferredProcessName);
        _meterTimer.Start();
    }

    private void RefreshSessions(string? preferredProcessName = null)
    {
        StopNormalization("오디오 세션 목록을 갱신했습니다.");
        var sessions = _audioSessions.Refresh();
        SessionComboBox.ItemsSource = sessions;

        AudioSessionItem? preferred = null;
        if (!string.IsNullOrWhiteSpace(preferredProcessName))
        {
            preferred = sessions.FirstOrDefault(x =>
                string.Equals(x.ProcessName, preferredProcessName, StringComparison.OrdinalIgnoreCase));
        }

        preferred ??= sessions.FirstOrDefault(x => x.IsLikelyYouTubeMusic);
        SessionComboBox.SelectedItem = preferred;

        if (sessions.Count == 0)
        {
            FooterStatusText.Text = "재생 중인 앱을 찾지 못했습니다";
            FooterDetailText.Text = "유튜브 뮤직에서 노래를 재생한 뒤 새로고침해 주세요.";
        }
    }

    private void MeterTimer_Tick(object? sender, EventArgs e)
    {
        if (_selectedSession is null)
        {
            UpdateMeter(-60, 0);
            return;
        }

        try
        {
            var peak = _selectedSession.PeakValue;
            var volume = _selectedSession.Volume;
            var instantaneousDb = LevelNormalizer.LinearToDb(peak);

            if (_isRunning)
            {
                var result = _normalizer.Process(peak, volume, _settings, _meterTimer.Interval.TotalSeconds);
                if (result.NewVolume is { } newVolume)
                {
                    _selectedSession.Volume = newVolume;
                    volume = newVolume;
                }

                UpdateMeter(result.DisplayLevelDb, volume);
                FooterStatusText.Text = result.StatusText;
                FooterDetailText.Text = result.DetailText;
            }
            else
            {
                UpdateMeter(instantaneousDb, volume);
            }

            CalibrateButton.IsEnabled = peak > 0.002f;
        }
        catch (Exception)
        {
            StopNormalization("선택한 오디오 세션이 종료되었습니다.");
            FooterDetailText.Text = "노래를 재생한 뒤 새로고침해 주세요.";
            CalibrateButton.IsEnabled = false;
        }
    }

    private void UpdateMeter(double db, float volume)
    {
        var clampedDb = Math.Clamp(db, -60, 0);
        var availableWidth = Math.Max(0, LevelMeter.Parent is FrameworkElement parent ? parent.ActualWidth : 0);
        LevelMeter.Width = availableWidth * ((clampedDb + 60) / 60.0);
        CurrentLevelText.Text = db <= -59.9 ? "— dB" : $"{db:0.0} dB";
        CurrentVolumeText.Text = $"{volume * 100:0}%";
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            StopNormalization("자동 보정을 멈췄습니다.");
            return;
        }

        if (_selectedSession is null)
        {
            return;
        }

        _normalizer.Reset();
        _isRunning = true;
        SessionComboBox.IsEnabled = false;
        ToggleButton.Content = "자동 보정 중지";
        ToggleButton.Background = new SolidColorBrush(Color.FromRgb(235, 108, 108));
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(53, 214, 177));
        StatusPill.Background = new SolidColorBrush(Color.FromRgb(18, 55, 49));
        HeaderStatusText.Text = "보정 중";
        FooterStatusText.Text = "소리를 분석하고 있습니다";

        SaveSettings();
    }

    private void StopNormalization(string message)
    {
        _isRunning = false;
        _normalizer.Reset();
        SessionComboBox.IsEnabled = true;
        ToggleButton.Content = "자동 보정 시작";
        ToggleButton.Background = (Brush)FindResource("Accent");
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(113, 128, 150));
        StatusPill.Background = new SolidColorBrush(Color.FromRgb(25, 40, 58));
        HeaderStatusText.Text = "대기 중";
        FooterStatusText.Text = message;
    }

    private void SessionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedSession = SessionComboBox.SelectedItem as AudioSessionItem;
        ToggleButton.IsEnabled = _selectedSession is not null;
        CalibrateButton.IsEnabled = false;

        if (_selectedSession is not null)
        {
            FooterStatusText.Text = $"{_selectedSession.DisplayName} 준비 완료";
            FooterDetailText.Text = "자동 보정을 시작하면 이 앱의 볼륨만 조절합니다.";
            _settings.PreferredProcessName = _selectedSession.ProcessName;
            SaveSettings();
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshSessions(_settings.PreferredProcessName);

    private void CalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSession is null)
        {
            return;
        }

        var currentDb = LevelNormalizer.LinearToDb(_selectedSession.PeakValue);
        if (currentDb <= -50)
        {
            FooterStatusText.Text = "소리가 충분히 감지되지 않았습니다";
            return;
        }

        TargetSlider.Value = Math.Clamp(Math.Round(currentDb), TargetSlider.Minimum, TargetSlider.Maximum);
        FooterStatusText.Text = "현재 음량을 목표로 설정했습니다";
        FooterDetailText.Text = "곡의 큰 부분에서 기준을 잡으면 더 자연스럽게 동작합니다.";
    }

    private void SettingsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoading)
        {
            return;
        }

        if (ReferenceEquals(sender, MinVolumeSlider) && MinVolumeSlider.Value > MaxVolumeSlider.Value - 5)
        {
            MaxVolumeSlider.Value = Math.Min(100, MinVolumeSlider.Value + 5);
        }
        else if (ReferenceEquals(sender, MaxVolumeSlider) && MaxVolumeSlider.Value < MinVolumeSlider.Value + 5)
        {
            MinVolumeSlider.Value = Math.Max(0, MaxVolumeSlider.Value - 5);
        }

        _settings.TargetLevelDb = TargetSlider.Value;
        _settings.MinimumVolumePercent = (int)Math.Round(MinVolumeSlider.Value);
        _settings.MaximumVolumePercent = (int)Math.Round(MaxVolumeSlider.Value);
        UpdateSettingLabels();
        SaveSettings();
    }

    private void UpdateSettingLabels()
    {
        TargetLevelText.Text = $"{TargetSlider.Value:0} dB";
        MinVolumeText.Text = $"{MinVolumeSlider.Value:0}%";
        MaxVolumeText.Text = $"{MaxVolumeSlider.Value:0}%";
    }

    private void SaveSettings()
    {
        if (!_isLoading)
        {
            _settingsStore.Save(_settings);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _meterTimer.Stop();
        SaveSettings();
        _audioSessions.Dispose();
    }
}

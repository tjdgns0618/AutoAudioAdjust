using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AutoAudioAdjust.App.Audio;
using AutoAudioAdjust.App.Models;
using AutoAudioAdjust.App.Services;
using Microsoft.Web.WebView2.Core;

namespace AutoAudioAdjust.App;

public partial class MainWindow : Window
{
    private const string HomeUrl = "https://music.youtube.com/";

    private static readonly string WebViewDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoAudioAdjust",
        "WebView2");

    private readonly SettingsStore _settingsStore = new();
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _ytmTimer;
    private readonly LevelNormalizer _normalizer = new();
    private readonly EmbeddedPlayerAudio _player = new();
    private readonly Queue<double> _recentSquaredLevels = new();
    private ProcessLoopbackMeter? _meter;
    private int? _meterProcessId;
    private AppSettings _settings = new();
    private bool _isLoading = true;
    private bool _syncingUi;
    private bool _ytmBusy;
    private bool _ytmMuted;

    // YouTube Music's own volume as a linear gain (player volume / 100, 0 when muted). Auto adjust
    // measures the app's sound as if this were 100%, so lowering YouTube Music makes it quieter
    // instead of being compensated by a higher app volume.
    private double _ytmGain = 1.0;
    private double YtmGainDb => 20 * Math.Log10(Math.Max(1e-4, _ytmGain));
    private YouTubeMusicBridge? _bridge;
    private NowPlaying _nowPlaying = NowPlaying.Empty;
    private TrayIcon? _tray;
    private MediaControls? _mediaControls;
    private MiniPlayerWindow? _miniPlayer;
    private bool _exiting;
    private double _lastDisplayDb = LevelNormalizer.FloorDb;
    private float? _lastVolume;

    // Restoring saved volumes happens once, when the player / its sound first appears.
    private bool _ytmVolumeRestored;
    private bool _appVolumeRestored;
    private readonly DispatcherTimer _saveTimer;

    public MainWindow()
    {
        InitializeComponent();

        // Loaded before the window is shown so its saved size and position apply immediately.
        _settings = _settingsStore.Load();
        ApplyWindowPlacement();

        // Volume changes happen often (sliders, auto adjust); save them at most once a second.
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings();
        };

        _meterTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _meterTimer.Tick += MeterTimer_Tick;

        _ytmTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _ytmTimer.Tick += YtmTimer_Tick;

        SourceInitialized += (_, _) =>
        {
            UseDarkTitleBar();
            InitializeMediaControls();
        };
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private void UseDarkTitleBar()
    {
        const int DwmwaUseImmersiveDarkMode = 20;
        var enabled = 1;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private bool IsAutoAdjustOn => AutoToggle.IsChecked == true;

    private void ApplyWindowPlacement()
    {
        if (_settings.Window is not { } placement)
        {
            return;
        }

        // Only restore when the saved area is still on a connected screen.
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var saved = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        var visible = Rect.Intersect(screen, saved);
        if (visible.IsEmpty || visible.Width < 200 || visible.Height < 100)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = Math.Max(MinWidth, placement.Width);
        Height = Math.Max(MinHeight, placement.Height);
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void CaptureWindowPlacement()
    {
        if (WindowState == WindowState.Minimized && RestoreBounds.IsEmpty)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width < 200)
        {
            return;
        }

        _settings.Window = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized
        };
    }

    private void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        TargetSlider.Value = Math.Round(Loudness.ToScale(_settings.TargetLevelDb));
        MinVolumeSlider.Value = _settings.MinimumVolumePercent;
        MaxVolumeSlider.Value = _settings.MaximumVolumePercent;
        AutoToggle.IsChecked = _settings.AutoAdjustEnabled;
        _isLoading = false;
        UpdateSettingLabels();
        UpdateAutoState();

        InitializeTray();

        // Measurement starts once the player's audio session appears (see EnsureMeter).
        _meterTimer.Start();

        await InitializeBrowserAsync();
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            Directory.CreateDirectory(WebViewDataFolder);
            // WebView2 would register with the Windows media controls itself as "msedgewebview2.exe"
            // (shown as an unknown app); the app registers under its own name instead (MediaControls).
            var options = new CoreWebView2EnvironmentOptions("--disable-features=HardwareMediaKeyHandling");
            var environment = await CoreWebView2Environment.CreateAsync(null, WebViewDataFolder, options);
            await Browser.EnsureCoreWebView2Async(environment);

            var core = Browser.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += Browser_NewWindowRequested;
            core.HistoryChanged += (_, _) => UpdateNavigationButtons();
            core.NavigationCompleted += (_, _) => UpdateNavigationButtons();
            _bridge = new YouTubeMusicBridge(core);
            core.Navigate(HomeUrl);
            BrowserPlaceholder.Visibility = Visibility.Collapsed;
            _ytmTimer.Start();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            BrowserPlaceholderText.Text = "Microsoft Edge WebView2 런타임이 필요합니다.\nhttps://developer.microsoft.com/microsoft-edge/webview2 에서 설치해 주세요.";
        }
    }

    private void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Keep YouTube / Google pages (e.g. sign-in) inside the app; open anything else in the
        // default browser so the player keeps playing.
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && IsInAppHost(uri.Host))
        {
            e.Handled = true;
            Browser.CoreWebView2.Navigate(e.Uri);
            return;
        }

        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing to open with; ignore.
        }
    }

    private static bool IsInAppHost(string host) =>
        host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase);

    private void UpdateNavigationButtons()
    {
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        BackButton.IsEnabled = Browser.CoreWebView2.CanGoBack;
        ForwardButton.IsEnabled = Browser.CoreWebView2.CanGoForward;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.GoBack();

    private void ForwardButton_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.GoForward();

    private void ReloadButton_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();

    private void HomeButton_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Navigate(HomeUrl);

    /// <summary>
    /// Captures the process that owns the player's audio session. WebView2 may restart its audio
    /// process, so capture follows the session's process.
    /// </summary>
    private void EnsureMeter()
    {
        var pid = _player.SessionProcessId;
        if (pid == _meterProcessId)
        {
            return;
        }

        _meter?.Dispose();
        _meter = pid is { } id ? ProcessLoopbackMeter.Start(id) : null;
        _meterProcessId = pid;
        _normalizer.Reset();
        _recentSquaredLevels.Clear();
    }

    private void MeterTimer_Tick(object? sender, EventArgs e)
    {
        EnsureMeter();
        if (_meter?.Error is { } error)
        {
            UpdateMeter(LevelNormalizer.FloorDb, null);
            AutoStatusText.Text = "소리를 측정할 수 없습니다";
            AutoDetailText.Text = $"Windows 11 또는 Windows 10 (20348 이상)이 필요합니다 · {error}";
            return;
        }

        var level = _meter?.ReadRms() ?? 0;
        // Level before YouTube Music's own volume; 0 (= silence, no change) when it is muted or at 0.
        var compensated = _ytmGain >= 0.01 ? level / _ytmGain : 0;
        RememberLevel(compensated);
        CalibrateButton.IsEnabled = level > 0.0001; // above about -80 dB

        if (!_player.HasSession)
        {
            UpdateMeter(LevelNormalizer.FloorDb, null);
            SetAppVolumeControlsAvailable(false);
            if (IsAutoAdjustOn)
            {
                AutoStatusText.Text = "재생을 기다리는 중";
                AutoDetailText.Text = "유튜브 뮤직에서 곡을 재생하면 자동으로 보정합니다.";
            }
            return;
        }

        try
        {
            if (!_appVolumeRestored)
            {
                // First sound since launch: start from the last app volume instead of 100%, so the
                // first song is not suddenly loud and manual settings carry over.
                _appVolumeRestored = true;
                if (_settings.AppVolumePercent is { } savedVolume)
                {
                    _player.Volume = savedVolume / 100f;
                }
                _player.Muted = _settings.AppMuted;
                _normalizer.Reset();
            }

            var volume = _player.Volume;
            if (IsAutoAdjustOn)
            {
                var result = _normalizer.Process(compensated, volume, _settings, _meterTimer.Interval.TotalSeconds);
                if (result.NewVolume is { } newVolume)
                {
                    _player.Volume = newVolume;
                    volume = newVolume;
                }

                RememberAppVolume(volume);

                // Show what is actually heard (YouTube Music volume included).
                var heardDb = LevelNormalizer.IsSilent(result.DisplayLevelDb)
                    ? LevelNormalizer.LinearToDb(level)
                    : result.DisplayLevelDb + YtmGainDb;
                UpdateMeter(heardDb, volume);
                AutoStatusText.Text = result.StatusText;
                AutoDetailText.Text = result.DetailText;
            }
            else
            {
                UpdateMeter(LevelNormalizer.LinearToDb(level), volume);
            }
        }
        catch (COMException)
        {
            // The player's audio session went away (device change, renderer restart).
            _player.Invalidate();
        }
    }

    private void RememberAppVolume(float volume)
    {
        var percent = (int)Math.Round(volume * 100);
        if (_settings.AppVolumePercent != percent)
        {
            _settings.AppVolumePercent = percent;
            RequestSave();
        }
    }

    private void RememberLevel(double level)
    {
        _recentSquaredLevels.Enqueue(level * level);
        while (_recentSquaredLevels.Count > 10)
        {
            _recentSquaredLevels.Dequeue();
        }
    }

    private void UpdateMeter(double db, float? volume)
    {
        _lastDisplayDb = db;
        _lastVolume = volume;
        // The bar and the big number use the same 0-100 "듣는 크기" scale as the target slider.
        var silent = LevelNormalizer.IsSilent(db);
        var scale = silent ? 0 : Loudness.ToScale(db);
        var width = LevelTrack.ActualWidth;
        LevelMeter.Width = width * scale / 100;
        // Where the sound settles: the target lowered by YouTube Music's own volume.
        TargetMarker.Margin = new Thickness(width * Loudness.ToScale(_settings.TargetLevelDb + YtmGainDb) / 100 - 1, 0, 0, 0);
        CurrentLevelText.Text = silent ? "—" : $"{scale:0}";
        CurrentDbText.Text = silent ? "— dB RMS" : $"{db:0.0} dB RMS";
        CurrentVolumeText.Text = volume is { } v ? $"{v * 100:0}%" : "—%";
        if (volume is { } current)
        {
            SetAppVolumeControlsAvailable(true);
            if (!AppVolumeSlider.IsMouseCaptureWithin)
            {
                SyncSlider(AppVolumeSlider, current * 100);
            }
            AppMuteButton.Content = _player.Muted ? MutedIcon : VolumeIcon;
        }
    }

    // ---------- 볼륨 조절 카드 ----------

    private static readonly string VolumeIcon = char.ConvertFromUtf32(0xE767); // Segoe Fluent "Volume"
    private static readonly string MutedIcon = char.ConvertFromUtf32(0xE74F);  // Segoe Fluent "Mute"

    private async void YtmTimer_Tick(object? sender, EventArgs e)
    {
        if (_ytmBusy || _bridge is null)
        {
            return;
        }

        _ytmBusy = true;
        try
        {
            _nowPlaying = await _bridge.GetStateAsync();
        }
        finally
        {
            _ytmBusy = false;
        }

        var np = _nowPlaying;
        if (np.Volume is { } current && !_ytmVolumeRestored)
        {
            // The player just loaded: put back the YouTube Music volume and mute from last time.
            _ytmVolumeRestored = true;
            if (_settings.YouTubeMusicVolume is { } savedVolume && Math.Abs(savedVolume - current) >= 1)
            {
                await _bridge.SetVolumeAsync(savedVolume);
            }
            if (_settings.YouTubeMusicVolume is not null && _settings.YouTubeMusicMuted != np.Muted)
            {
                await _bridge.SetMutedAsync(_settings.YouTubeMusicMuted);
            }
            return; // the next poll shows the restored values
        }

        if (np.Volume is { } changed && _ytmVolumeRestored)
        {
            // Remember changes made here or with YouTube Music's own volume control.
            var percent = (int)Math.Round(changed);
            if (_settings.YouTubeMusicVolume != percent || _settings.YouTubeMusicMuted != np.Muted)
            {
                _settings.YouTubeMusicVolume = percent;
                _settings.YouTubeMusicMuted = np.Muted;
                RequestSave();
            }
        }

        YtmVolumeSlider.IsEnabled = np.Volume is not null;
        YtmMuteButton.IsEnabled = np.Volume is not null;
        if (np.Volume is { } volume)
        {
            _ytmMuted = np.Muted;
            if (!YtmVolumeSlider.IsMouseCaptureWithin)
            {
                _ytmGain = np.Muted ? 0 : volume / 100.0;
            }
            YtmMuteButton.Content = _ytmMuted ? MutedIcon : VolumeIcon;
            if (!YtmVolumeSlider.IsMouseCaptureWithin)
            {
                YtmVolumeText.Text = _ytmMuted ? "음소거" : $"{volume:0}%";
                SyncSlider(YtmVolumeSlider, volume);
            }
        }
        else
        {
            YtmVolumeText.Text = "—%";
            _ytmGain = 1.0;
        }

        _tray?.Update(np, IsAutoAdjustOn, _settings.CloseAction == CloseAction.Ask);
        _mediaControls?.Update(np);
        UpdateMiniPlayer();
    }

    private void InitializeMediaControls()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _mediaControls = MediaControls.TryCreate(handle);
        if (_mediaControls is null)
        {
            return;
        }

        _mediaControls.ButtonPressed += button => Dispatcher.BeginInvoke(() =>
        {
            switch (button)
            {
                case Windows.Media.SystemMediaTransportControlsButton.Play:
                case Windows.Media.SystemMediaTransportControlsButton.Pause:
                    // Only toggle when the requested state differs, so Play never pauses.
                    var wantPlaying = button == Windows.Media.SystemMediaTransportControlsButton.Play;
                    if (_nowPlaying.IsPlaying != wantPlaying)
                    {
                        _ = _bridge?.PlayPauseAsync();
                    }
                    break;
                case Windows.Media.SystemMediaTransportControlsButton.Next:
                    _ = _bridge?.NextAsync();
                    break;
                case Windows.Media.SystemMediaTransportControlsButton.Previous:
                    _ = _bridge?.PreviousAsync();
                    break;
            }
        });
    }

    private async void YtmVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingUi || _bridge is null)
        {
            return;
        }

        var volume = (int)Math.Round(e.NewValue);
        YtmVolumeText.Text = $"{volume}%";
        _ytmGain = _ytmMuted ? 0 : volume / 100.0; // apply at once, before the next page poll
        await _bridge.SetVolumeAsync(volume);
    }

    private async void YtmMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bridge is null)
        {
            return;
        }

        _ytmMuted = !_ytmMuted;
        _ytmGain = _ytmMuted ? 0 : YtmVolumeSlider.Value / 100.0;
        YtmMuteButton.Content = _ytmMuted ? MutedIcon : VolumeIcon;
        await _bridge.SetMutedAsync(_ytmMuted);
    }

    private void AppVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // While auto adjust is on, the slider only displays the value the normalizer sets.
        if (_syncingUi || _isLoading || IsAutoAdjustOn || !_player.HasSession)
        {
            return;
        }

        _player.Volume = (float)(e.NewValue / 100.0);
        CurrentVolumeText.Text = $"{e.NewValue:0}%";
        RememberAppVolume(_player.Volume);
    }

    private void AppMuteButton_Click(object sender, RoutedEventArgs e)
    {
        var muted = !_player.Muted;
        _player.Muted = muted;
        AppMuteButton.Content = muted ? MutedIcon : VolumeIcon;
        _settings.AppMuted = muted;
        RequestSave();
    }

    private void SetAppVolumeControlsAvailable(bool hasSession)
    {
        AppMuteButton.IsEnabled = hasSession;
        AppVolumeSlider.IsEnabled = hasSession && !IsAutoAdjustOn;
    }

    private void SyncSlider(System.Windows.Controls.Slider slider, double value)
    {
        if (Math.Abs(slider.Value - value) < 0.5)
        {
            return;
        }

        _syncingUi = true;
        slider.Value = value;
        _syncingUi = false;
    }

    private void AutoToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _normalizer.Reset();
        _settings.AutoAdjustEnabled = IsAutoAdjustOn;
        SaveSettings();
        UpdateAutoState();
    }

    private void UpdateAutoState()
    {
        var on = IsAutoAdjustOn;
        StatusDot.Fill = new SolidColorBrush(on ? Color.FromRgb(53, 214, 177) : Color.FromRgb(113, 128, 150));
        StatusPill.Background = new SolidColorBrush(on ? Color.FromRgb(18, 55, 49) : Color.FromRgb(25, 40, 58));
        HeaderStatusText.Text = on ? "보정 중" : "대기 중";
        AppVolumeSlider.IsEnabled = !on && _player.HasSession;
        AppVolumeHint.Text = on
            ? "자동 보정이 켜져 있으면 앱 볼륨은 자동으로 조절됩니다. 유튜브 뮤직 볼륨은 목표 크기 위에 그대로 적용됩니다."
            : "자동 보정이 꺼져 있어 앱 볼륨을 직접 조절할 수 있습니다.";
        if (!on)
        {
            AutoStatusText.Text = "자동 보정이 꺼져 있습니다";
            AutoDetailText.Text = "켜면 곡이 바뀌어도 같은 크기로 맞춰 줍니다.";
        }
    }

    private void CalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        // Average of the last second of true RMS, so one quiet or loud moment does not set the target.
        var currentDb = _recentSquaredLevels.Count == 0
            ? LevelNormalizer.FloorDb
            : LevelNormalizer.LinearToDb(Math.Sqrt(_recentSquaredLevels.Average()));
        if (currentDb <= -80)
        {
            AutoStatusText.Text = "소리가 충분히 감지되지 않았습니다";
            return;
        }

        TargetSlider.Value = Math.Round(Loudness.ToScale(currentDb));
        AutoStatusText.Text = "현재 음량을 목표로 설정했습니다";
        AutoDetailText.Text = "곡의 큰 부분에서 기준을 잡으면 더 자연스럽게 동작합니다.";
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

        _settings.TargetLevelDb = Loudness.ToDb(TargetSlider.Value);
        _settings.MinimumVolumePercent = (int)Math.Round(MinVolumeSlider.Value);
        _settings.MaximumVolumePercent = (int)Math.Round(MaxVolumeSlider.Value);
        UpdateSettingLabels();
        SaveSettings();
    }

    private void UpdateSettingLabels()
    {
        TargetLevelText.Text = $"{TargetSlider.Value:0}";
        TargetDbText.Text = $"{Loudness.ToDb(TargetSlider.Value):0.#} dB";
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

    // ---------- 트레이 / 미니 플레이어 / 닫기 ----------

    private void InitializeTray()
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico")).Stream;
        var size = System.Windows.Forms.SystemInformation.SmallIconSize;
        _tray = new TrayIcon(new System.Drawing.Icon(iconStream, size));
        _tray.OpenRequested += ShowFromTray;
        _tray.ExitRequested += ExitApp;
        _tray.PlayPauseRequested += () => _ = _bridge?.PlayPauseAsync();
        _tray.NextRequested += () => _ = _bridge?.NextAsync();
        _tray.PreviousRequested += () => _ = _bridge?.PreviousAsync();
        _tray.AutoAdjustToggled += () => AutoToggle.IsChecked = !IsAutoAdjustOn;
        _tray.AskOnCloseToggled += () =>
        {
            // Turning "ask" off keeps the X button sending the app to the tray.
            _settings.CloseAction = _settings.CloseAction == CloseAction.Ask ? CloseAction.Tray : CloseAction.Ask;
            SaveSettings();
            _tray.Update(_nowPlaying, IsAutoAdjustOn, _settings.CloseAction == CloseAction.Ask);
        };
        _tray.MiniPlayerRequested += _ => ToggleMiniPlayer();
        _tray.Update(_nowPlaying, IsAutoAdjustOn, _settings.CloseAction == CloseAction.Ask);

        _miniPlayer = new MiniPlayerWindow();
        _miniPlayer.OpenRequested += ShowFromTray;
        _miniPlayer.PlayPauseRequested += () => _ = _bridge?.PlayPauseAsync();
        _miniPlayer.NextRequested += () => _ = _bridge?.NextAsync();
        _miniPlayer.PreviousRequested += () => _ = _bridge?.PreviousAsync();
        _miniPlayer.AutoAdjustChanged += on => AutoToggle.IsChecked = on;
        _miniPlayer.SeekRequested += seconds => _ = _bridge?.SeekAsync(seconds);
        // Route through the main window's YouTube Music controls so gain, saving and UI stay in sync.
        _miniPlayer.VolumeChanged += volume => YtmVolumeSlider.Value = volume;
        _miniPlayer.MuteToggled += () => YtmMuteButton_Click(YtmMuteButton, new RoutedEventArgs());
    }

    private void ToggleMiniPlayer()
    {
        if (_miniPlayer is null)
        {
            return;
        }

        if (_miniPlayer.IsVisible)
        {
            _miniPlayer.Hide();
            return;
        }

        UpdateMiniPlayer(force: true);
        _miniPlayer.ShowNearTray();
    }

    private void UpdateMiniPlayer(bool force = false)
    {
        if (_miniPlayer is { } mini && (force || mini.IsVisible))
        {
            mini.Update(_nowPlaying, _lastDisplayDb, _lastVolume, IsAutoAdjustOn);
        }
    }

    private void ShowFromTray()
    {
        _miniPlayer?.Hide();
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void HideToTray()
    {
        CaptureWindowPlacement();
        Hide();
        SaveSettings();
        if (!_settings.TrayHintShown)
        {
            _settings.TrayHintShown = true;
            SaveSettings();
            _tray?.ShowBalloon("트레이에서 계속 재생 중", "아이콘을 클릭하면 미니 플레이어, 더블클릭하면 창이 열립니다. 오른쪽 클릭으로 종료할 수 있습니다.");
        }
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CaptureWindowPlacement();
        if (_exiting)
        {
            return;
        }

        var action = _settings.CloseAction;
        if (action == CloseAction.Ask)
        {
            var dialog = new CloseChoiceWindow { Owner = this };
            dialog.ShowDialog();
            if (dialog.Choice is not { } choice)
            {
                e.Cancel = true; // Esc: keep the window open.
                return;
            }

            action = choice;
            if (dialog.Remember)
            {
                _settings.CloseAction = choice;
                SaveSettings();
            }
        }

        if (action == CloseAction.Tray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _exiting = true;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _meterTimer.Stop();
        _ytmTimer.Stop();
        _saveTimer.Stop();
        _miniPlayer?.Close();
        _tray?.Dispose();
        SaveSettings();
        _meter?.Dispose();
        _player.Dispose();
    }
}

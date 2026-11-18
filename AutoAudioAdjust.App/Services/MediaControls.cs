using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Media;
using Windows.Storage.Streams;

namespace AutoAudioAdjust.App.Services;

/// <summary>
/// Registers the app with the Windows media controls (the flyout shown by volume/media keys and
/// in quick settings) under the app's own name and icon, and routes hardware media keys to the
/// YouTube Music page. WebView2's own registration is turned off (see MainWindow) because it shows
/// up as "msedgewebview2.exe" / unknown app.
/// </summary>
public sealed class MediaControls
{
    public const string AppUserModelId = "AAA.AutoAudioAdjust";
    public const string DisplayName = "AAA · Auto Audio Adjust";

    private readonly SystemMediaTransportControls _controls;
    private NowPlaying? _shown;

    private MediaControls(SystemMediaTransportControls controls)
    {
        _controls = controls;
        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsNextEnabled = true;
        _controls.IsPreviousEnabled = true;
        _controls.ButtonPressed += (_, e) => ButtonPressed?.Invoke(e.Button);
    }

    /// <summary>Raised on a background thread when a media key or flyout button is pressed.</summary>
    public event Action<SystemMediaTransportControlsButton>? ButtonPressed;

    /// <summary>
    /// Gives this process an explicit app identity and registers its display name and icon, so
    /// Windows shows "AAA · Auto Audio Adjust" instead of an unknown app. Call before any window opens.
    /// </summary>
    public static void RegisterAppIdentity(Stream iconPng)
    {
        _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoAudioAdjust");
            Directory.CreateDirectory(folder);
            var iconPath = Path.Combine(folder, "AppIcon.png");
            using (var file = File.Create(iconPath))
            {
                iconPng.CopyTo(file);
            }

            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
            key.SetValue("DisplayName", DisplayName);
            key.SetValue("IconUri", iconPath);

            // The media flyout looks up the app's name and icon through a Start menu shortcut that
            // carries the same AppUserModelID (this is how Chrome shows up as "Chrome").
            if (Environment.ProcessPath is { } exePath)
            {
                var shortcut = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AAA · Auto Audio Adjust.lnk");
                StartMenuShortcut.CreateOrUpdate(shortcut, exePath, AppUserModelId, DisplayName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            // Without the registration Windows falls back to a generic name; playback still works.
        }
    }

    public static MediaControls? TryCreate(IntPtr windowHandle)
    {
        try
        {
            return new MediaControls(SystemMediaTransportControlsInterop.GetForWindow(windowHandle));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Update(NowPlaying nowPlaying)
    {
        _controls.PlaybackStatus = !nowPlaying.HasTrack
            ? MediaPlaybackStatus.Closed
            : nowPlaying.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

        if (_shown is { } shown && shown.Title == nowPlaying.Title && shown.Artist == nowPlaying.Artist
            && shown.ArtworkUrl == nowPlaying.ArtworkUrl)
        {
            return;
        }

        _shown = nowPlaying;
        var updater = _controls.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = nowPlaying.Title;
        updater.MusicProperties.Artist = nowPlaying.Artist;
        updater.Thumbnail = nowPlaying.ArtworkUrl is { } art && Uri.TryCreate(art, UriKind.Absolute, out var uri)
            ? RandomAccessStreamReference.CreateFromUri(uri)
            : null;
        updater.Update();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}

using System.Windows;
using AutoAudioAdjust.App.Services;

namespace AutoAudioAdjust.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Must happen before the first window so Windows shows the app's own name and icon in the
        // taskbar and the media controls flyout.
        var icon = GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon-256.png")).Stream;
        MediaControls.RegisterAppIdentity(icon);
        base.OnStartup(e);
    }
}

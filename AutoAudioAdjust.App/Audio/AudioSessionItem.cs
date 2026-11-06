using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AutoAudioAdjust.App.Audio;

public sealed class AudioSessionItem : IDisposable
{
    private readonly AudioSessionControl _control;

    internal AudioSessionItem(AudioSessionControl control, int processId, string processName, string displayName)
    {
        _control = control;
        ProcessId = processId;
        ProcessName = processName;
        DisplayName = displayName;
    }

    public int ProcessId { get; }
    public string ProcessName { get; }
    public string DisplayName { get; }
    public bool IsLikelyYouTubeMusic => ProcessName is "chrome" or "msedge" or "YouTube Music";
    public bool IsActive => _control.State == AudioSessionState.AudioSessionStateActive;
    public float PeakValue => _control.AudioMeterInformation.MasterPeakValue;

    public float Volume
    {
        get => _control.SimpleAudioVolume.Volume;
        set => _control.SimpleAudioVolume.Volume = Math.Clamp(value, 0f, 1f);
    }

    public void Dispose() => _control.Dispose();
}

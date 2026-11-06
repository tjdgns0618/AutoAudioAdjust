using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AutoAudioAdjust.App.Audio;

public sealed class AudioSessionService : IDisposable
{
    private readonly List<AudioSessionItem> _sessions = [];
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;

    public IReadOnlyList<AudioSessionItem> Refresh()
    {
        DisposeCurrent();
        _enumerator = new MMDeviceEnumerator();
        _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var collection = _device.AudioSessionManager.Sessions;

        for (var i = 0; i < collection.Count; i++)
        {
            AudioSessionControl? control = null;
            try
            {
                control = collection[i];
                if (control.State == AudioSessionState.AudioSessionStateExpired)
                {
                    control.Dispose();
                    continue;
                }

                var processId = unchecked((int)control.GetProcessID);
                if (processId <= 0)
                {
                    control.Dispose();
                    continue;
                }

                var processName = GetProcessName(processId);
                var sessionName = control.DisplayName;
                var displayName = string.IsNullOrWhiteSpace(sessionName)
                    ? $"{FriendlyName(processName)}  ·  PID {processId}"
                    : $"{sessionName}  ·  {FriendlyName(processName)}";

                _sessions.Add(new AudioSessionItem(control, processId, processName, displayName));
                control = null;
            }
            catch
            {
                control?.Dispose();
            }
        }

        return _sessions
            .OrderByDescending(x => x.IsActive)
            .ThenByDescending(x => x.IsLikelyYouTubeMusic)
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return $"process-{processId}";
        }
    }

    private static string FriendlyName(string processName) => processName.ToLowerInvariant() switch
    {
        "chrome" => "Google Chrome",
        "msedge" => "Microsoft Edge",
        "firefox" => "Mozilla Firefox",
        "youtube music" => "YouTube Music",
        _ => processName
    };

    private void DisposeCurrent()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }

        _sessions.Clear();
        _device?.Dispose();
        _device = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    public void Dispose() => DisposeCurrent();
}

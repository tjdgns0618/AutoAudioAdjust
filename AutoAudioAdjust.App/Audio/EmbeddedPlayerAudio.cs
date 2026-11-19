using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AutoAudioAdjust.App.Audio;

/// <summary>
/// Audio of the YouTube Music page shown inside this app. WebView2 plays sound from its own
/// child processes (msedgewebview2.exe), so this finds the Windows audio sessions that belong to
/// any descendant of this process and controls their volume together. Because these sessions
/// only ever contain this app's player, nothing else on the PC is measured or changed.
/// <para>
/// This app's own process is skipped on purpose: the level meter's loopback capture shows up as an
/// audio session of this process on the same device. Picking that session as the "player" made the
/// meter capture this app itself (silence), so auto adjust stopped following the music.
/// </para>
/// </summary>
public sealed class EmbeddedPlayerAudio : IDisposable
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(2);

    private readonly int _rootProcessId = Environment.ProcessId;
    private readonly List<AudioSessionControl> _sessions = [];
    private readonly List<MMDevice> _devices = [];
    private MMDeviceEnumerator? _enumerator;
    private int? _primaryProcessId;
    private DateTime _lastScan = DateTime.MinValue;
    private float _volume = 1f;
    private bool _volumeWasSet;

    /// <summary>True when at least one player audio session has been found.</summary>
    public bool HasSession
    {
        get
        {
            RescanIfDue();
            return _sessions.Count > 0;
        }
    }

    /// <summary>
    /// Player volume (0..1). Setting applies to every player session, including ones created later
    /// (for example after the default output device changes).
    /// </summary>
    public float Volume
    {
        get
        {
            RescanIfDue();
            return _sessions.Count > 0 ? _sessions[0].SimpleAudioVolume.Volume : _volume;
        }
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            _volumeWasSet = true;
            RescanIfDue();
            foreach (var session in _sessions)
            {
                session.SimpleAudioVolume.Volume = _volume;
            }
        }
    }

    /// <summary>Mute state of the player's Windows audio session(s).</summary>
    public bool Muted
    {
        get
        {
            RescanIfDue();
            return _sessions.Count > 0 && _sessions[0].SimpleAudioVolume.Mute;
        }
        set
        {
            RescanIfDue();
            foreach (var session in _sessions)
            {
                session.SimpleAudioVolume.Mute = value;
            }
        }
    }

    /// <summary>
    /// Process that owns the player's audio session (WebView2's audio process). Capture must target
    /// this process directly: process loopback on this app's own PID does not pick up WebView2
    /// processes that were started after capture began.
    /// </summary>
    public int? SessionProcessId
    {
        get
        {
            RescanIfDue();
            return _sessions.Count > 0 ? (int)_sessions[0].GetProcessID : null;
        }
    }

    /// <summary>Forces the next access to look for sessions again.</summary>
    public void Invalidate() => _lastScan = DateTime.MinValue;

    private void RescanIfDue()
    {
        if (DateTime.UtcNow - _lastScan < RescanInterval)
        {
            return;
        }

        _lastScan = DateTime.UtcNow;
        DisposeSessions();

        try
        {
            _enumerator = new MMDeviceEnumerator();
            var descendants = ProcessTree.GetDescendants(_rootProcessId);

            // Every active output device, not only the default one: the player may be routed to
            // another device in Windows' per-app sound settings, or keep playing on the old device
            // for a moment after the default changes.
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                _devices.Add(device);
                var collection = device.AudioSessionManager.Sessions;
                for (var i = 0; i < collection.Count; i++)
                {
                    var control = collection[i];
                    var pid = (int)control.GetProcessID;
                    if (!descendants.Contains(pid) || control.State == AudioSessionState.AudioSessionStateExpired)
                    {
                        control.Dispose();
                        continue;
                    }

                    _sessions.Add(control);
                    // A new session (new device or restarted renderer) starts at 100%; keep it in step.
                    if (_volumeWasSet && Math.Abs(control.SimpleAudioVolume.Volume - _volume) > 0.001f)
                    {
                        control.SimpleAudioVolume.Volume = _volume;
                    }
                }
            }

            PutPrimaryFirst();
            if (_sessions.Count > 0 && !_volumeWasSet)
            {
                _volume = _sessions[0].SimpleAudioVolume.Volume;
            }
        }
        catch (COMException)
        {
            // Audio device changed or unavailable; the next scan will try again.
            DisposeSessions();
        }
    }

    /// <summary>
    /// Moves the session the meter should follow to index 0. Keeps the current one while it is
    /// still there, so the meter is not restarted just because Windows listed sessions in another
    /// order; otherwise prefers a session that is playing right now.
    /// </summary>
    private void PutPrimaryFirst()
    {
        if (_sessions.Count == 0)
        {
            return; // keep _primaryProcessId: a short gap between scans should not lose it
        }

        var index = _primaryProcessId is { } current
            ? _sessions.FindIndex(s => (int)s.GetProcessID == current && s.State == AudioSessionState.AudioSessionStateActive)
            : -1;
        if (index < 0)
        {
            index = _sessions.FindIndex(s => s.State == AudioSessionState.AudioSessionStateActive);
        }
        if (index < 0 && _primaryProcessId is { } previous)
        {
            index = _sessions.FindIndex(s => (int)s.GetProcessID == previous);
        }
        if (index < 0)
        {
            index = 0;
        }

        if (index > 0)
        {
            (_sessions[0], _sessions[index]) = (_sessions[index], _sessions[0]);
        }

        _primaryProcessId = (int)_sessions[0].GetProcessID;
    }

    private void DisposeSessions()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }

        _sessions.Clear();
        foreach (var device in _devices)
        {
            device.Dispose();
        }

        _devices.Clear();
        _enumerator?.Dispose();
        _enumerator = null;
    }

    public void Dispose() => DisposeSessions();

    private static class ProcessTree
    {
        private const uint Th32csSnapProcess = 0x00000002;

        public static HashSet<int> GetDescendants(int rootProcessId)
        {
            var children = new Dictionary<int, List<int>>();
            var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                return [];
            }

            try
            {
                var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
                if (Process32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        var parent = (int)entry.th32ParentProcessID;
                        if (!children.TryGetValue(parent, out var list))
                        {
                            children[parent] = list = [];
                        }
                        list.Add((int)entry.th32ProcessID);
                    } while (Process32NextW(snapshot, ref entry));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            var result = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(rootProcessId);
            while (stack.Count > 0)
            {
                if (!children.TryGetValue(stack.Pop(), out var list))
                {
                    continue;
                }

                foreach (var child in list)
                {
                    if (result.Add(child))
                    {
                        stack.Push(child);
                    }
                }
            }

            return result;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

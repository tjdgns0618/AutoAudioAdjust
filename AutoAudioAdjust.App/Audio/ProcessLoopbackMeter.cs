using System.Runtime.InteropServices;

namespace AutoAudioAdjust.App.Audio;

/// <summary>
/// Captures the actual audio samples of one process (and its child processes) through
/// Windows process loopback capture and computes a true RMS level from every sample.
/// Requires Windows 10 build 20348 or Windows 11.
/// </summary>
public sealed class ProcessLoopbackMeter : IDisposable
{
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private const int CaptureSampleRate = 44100;
    private const int CaptureChannels = 2;
    private const int CaptureBitsPerSample = 16;

    private readonly int _processId;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stopRequested = new(false);
    private readonly object _gate = new();

    private short[] _pcm = new short[4096];
    private double _sumOfSquares;
    private long _sampleCount;
    private long _totalSamples;
    private volatile bool _isCapturing;
    private volatile string? _error;

    private ProcessLoopbackMeter(int processId)
    {
        _processId = processId;
        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = $"Process loopback meter ({processId})"
        };
    }

    /// <summary>True once audio is flowing from the target process.</summary>
    public bool IsCapturing => _isCapturing;

    /// <summary>Total number of samples received so far (diagnostics).</summary>
    public long TotalSamples => Interlocked.Read(ref _totalSamples);

    /// <summary>Set when capture could not be started (e.g. unsupported Windows version).</summary>
    public string? Error => _error;

    public static ProcessLoopbackMeter Start(int processId)
    {
        var meter = new ProcessLoopbackMeter(processId);
        meter._thread.Start();
        return meter;
    }

    /// <summary>
    /// Returns the true RMS (0..1, full scale) of all samples captured since the previous call.
    /// When the process produced no audio, the result is 0.
    /// </summary>
    public double ReadRms()
    {
        lock (_gate)
        {
            var rms = _sampleCount == 0 ? 0 : Math.Sqrt(_sumOfSquares / _sampleCount);
            _sumOfSquares = 0;
            _sampleCount = 0;
            return rms;
        }
    }

    private void CaptureLoop()
    {
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        using var bufferReady = new AutoResetEvent(false);

        var stage = "activate";
        try
        {
            (audioClient, captureClient) = Activate(_processId, bufferReady.SafeWaitHandle.DangerousGetHandle());

            stage = "start";
            audioClient.Start();
            _isCapturing = true;
            stage = "capture";

            var handles = new[] { _stopRequested.WaitHandle, bufferReady };
            while (WaitHandle.WaitAny(handles, 100) != 0)
            {
                DrainPackets(captureClient);
            }

            audioClient.Stop();
        }
        catch (Exception ex)
        {
            _error = ex is InvalidOperationException ? ex.Message : $"{stage}: {ex.Message}";
        }
        finally
        {
            _isCapturing = false;
            if (captureClient is not null)
            {
                Marshal.ReleaseComObject(captureClient);
            }
            if (audioClient is not null)
            {
                Marshal.ReleaseComObject(audioClient);
            }
        }
    }

    private void DrainPackets(IAudioCaptureClient captureClient)
    {
        captureClient.GetNextPacketSize(out var packetFrames);
        while (packetFrames > 0)
        {
            captureClient.GetBuffer(out var data, out var frames, out var flags, out _, out _);

            var samples = (int)frames * CaptureChannels;
            double sum = 0;
            if ((flags & AudClntBufferFlags.Silent) == 0 && data != IntPtr.Zero)
            {
                if (_pcm.Length < samples)
                {
                    _pcm = new short[samples];
                }

                Marshal.Copy(data, _pcm, 0, samples);
                for (var i = 0; i < samples; i++)
                {
                    var s = _pcm[i] / 32768.0;
                    sum += s * s;
                }
            }

            lock (_gate)
            {
                _sumOfSquares += sum;
                _sampleCount += samples;
            }

            Interlocked.Add(ref _totalSamples, samples);

            captureClient.ReleaseBuffer(frames);
            captureClient.GetNextPacketSize(out packetFrames);
        }
    }

    /// <summary>
    /// Activates process loopback and prepares the stream. Following Microsoft's ApplicationLoopback
    /// sample, Initialize/GetService/SetEventHandle run inside the activation callback; doing them on
    /// another thread made GetService fail with E_HANDLE.
    /// </summary>
    private static (IAudioClient, IAudioCaptureClient) Activate(int processId, IntPtr bufferReadyEvent)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = 0 // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };

        var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        var paramsPtr = Marshal.AllocHGlobal(paramsSize);
        // PROPVARIANT holding a VT_BLOB: vt (2 bytes) + 6 reserved bytes, then BLOB { ULONG cbSize; BYTE* pBlobData }.
        var propVariantPtr = Marshal.AllocHGlobal(8 + 2 * IntPtr.Size);
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            for (var i = 0; i < 8 + 2 * IntPtr.Size; i++)
            {
                Marshal.WriteByte(propVariantPtr, i, 0);
            }
            Marshal.WriteInt16(propVariantPtr, 0, 65); // VT_BLOB
            Marshal.WriteInt32(propVariantPtr, 8, paramsSize);
            Marshal.WriteIntPtr(propVariantPtr, 8 + IntPtr.Size, paramsPtr);

            var handler = new ActivationCompletionHandler(bufferReadyEvent);
            NativeMethods.ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                typeof(IAudioClient).GUID,
                propVariantPtr,
                handler,
                out _);

            if (!((IAsyncResult)handler.Completed).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("오디오 캡처 활성화가 시간 안에 끝나지 않았습니다.");
            }

            return handler.Completed.GetAwaiter().GetResult();
        }
        finally
        {
            Marshal.FreeHGlobal(propVariantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    public void Dispose()
    {
        _stopRequested.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _stopRequested.Dispose();
    }

    private sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly IntPtr _bufferReadyEvent;
        private readonly TaskCompletionSource<(IAudioClient, IAudioCaptureClient)> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ActivationCompletionHandler(IntPtr bufferReadyEvent) => _bufferReadyEvent = bufferReadyEvent;

        public Task<(IAudioClient, IAudioCaptureClient)> Completed => _completion.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            var stage = "activate";
            try
            {
                activateOperation.GetActivateResult(out var hr, out var activated);
                Marshal.ThrowExceptionForHR(hr);
                var audioClient = (IAudioClient)activated;

                var format = new WaveFormatEx
                {
                    FormatTag = 1, // WAVE_FORMAT_PCM
                    Channels = CaptureChannels,
                    SamplesPerSec = CaptureSampleRate,
                    BitsPerSample = CaptureBitsPerSample,
                    BlockAlign = CaptureChannels * CaptureBitsPerSample / 8,
                    AvgBytesPerSec = CaptureSampleRate * CaptureChannels * CaptureBitsPerSample / 8,
                    Size = 0
                };

                const uint streamFlags = AudClntStreamFlags.Loopback
                                         | AudClntStreamFlags.EventCallback
                                         | AudClntStreamFlags.AutoConvertPcm;

                stage = "initialize";
                audioClient.Initialize(0 /* shared */, streamFlags, 0, 0, ref format, IntPtr.Zero);

                stage = "get capture client";
                var captureClientId = typeof(IAudioCaptureClient).GUID;
                audioClient.GetService(ref captureClientId, out var service);

                stage = "set event";
                audioClient.SetEventHandle(_bufferReadyEvent);

                _completion.SetResult((audioClient, (IAudioCaptureClient)service));
            }
            catch (Exception ex)
            {
                _completion.TrySetException(new InvalidOperationException($"{stage}: {ex.Message}", ex));
            }
        }
    }

    private static class AudClntStreamFlags
    {
        public const uint Loopback = 0x00020000;
        public const uint EventCallback = 0x00040000;
        public const uint AutoConvertPcm = 0x80000000;
    }

    private static class AudClntBufferFlags
    {
        public const uint Silent = 0x2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    private static class NativeMethods
    {
        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        public static extern void ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity,
            ref WaveFormatEx format, IntPtr audioSessionGuid);
        void GetBufferSize(out uint bufferFrames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint paddingFrames);
        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
        void GetMixFormat(out IntPtr deviceFormat);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint packetFrames);
    }
}

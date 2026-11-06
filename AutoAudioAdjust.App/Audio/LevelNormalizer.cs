using AutoAudioAdjust.App.Models;

namespace AutoAudioAdjust.App.Audio;

public sealed class LevelNormalizer
{
    private const double SilenceGateDb = -52;
    private const double DeadBandDb = 1.25;
    private const double RaiseRateDbPerSecond = 2.0;
    private const int WindowSize = 20;
    private const int FastWindowSize = 2;
    private const int MinimumSamples = 8;

    private readonly Queue<double> _squaredPeaks = new();
    private readonly Queue<double> _fastSquaredPeaks = new();
    private int _silentTicks;

    public NormalizationResult Process(float peak, float currentVolume, AppSettings settings, double elapsedSeconds)
    {
        var peakDb = LinearToDb(peak);
        if (peakDb <= SilenceGateDb)
        {
            _silentTicks++;
            if (_silentTicks >= 10)
            {
                _squaredPeaks.Clear();
                _fastSquaredPeaks.Clear();
            }

            return new NormalizationResult(null, peakDb, "소리를 기다리는 중", "무음 구간에서는 볼륨을 변경하지 않습니다.");
        }

        _silentTicks = 0;
        _squaredPeaks.Enqueue(peak * peak);
        _fastSquaredPeaks.Enqueue(peak * peak);
        while (_squaredPeaks.Count > WindowSize)
        {
            _squaredPeaks.Dequeue();
        }
        while (_fastSquaredPeaks.Count > FastWindowSize)
        {
            _fastSquaredPeaks.Dequeue();
        }

        var rmsOfPeaks = Math.Sqrt(_squaredPeaks.Average());
        var measuredDb = LinearToDb(rmsOfPeaks);
        var fastRmsOfPeaks = Math.Sqrt(_fastSquaredPeaks.Average());
        var fastMeasuredDb = LinearToDb(fastRmsOfPeaks);
        var fastErrorDb = settings.TargetLevelDb - fastMeasuredDb;
        var canUseFastProtection = _fastSquaredPeaks.Count >= FastWindowSize && fastErrorDb < -DeadBandDb;

        if (_squaredPeaks.Count < MinimumSamples && !canUseFastProtection)
        {
            return new NormalizationResult(null, measuredDb, "소리를 분석하고 있습니다", "안정적인 기준을 잡기 위해 잠시 듣고 있습니다.");
        }

        // A short attack window reacts to a suddenly loud next track. Raising volume
        // still uses the long window, so quiet intros cannot cause a dangerous jump.
        var controlLevelDb = canUseFastProtection ? Math.Max(measuredDb, fastMeasuredDb) : measuredDb;
        var errorDb = settings.TargetLevelDb - controlLevelDb;
        if (Math.Abs(errorDb) <= DeadBandDb)
        {
            return new NormalizationResult(null, measuredDb, "목표 음량을 유지하는 중", $"오차 {Math.Abs(errorDb):0.0} dB · 보정 불필요");
        }

        var rateDbPerSecond = errorDb < 0
            ? GetLowerRateDbPerSecond(-errorDb)
            : RaiseRateDbPerSecond;
        var maxStep = rateDbPerSecond * Math.Max(0.01, elapsedSeconds);
        var appliedDb = Math.Clamp(errorDb, -maxStep, maxStep);
        var minVolume = settings.MinimumVolumePercent / 100f;
        var maxVolume = settings.MaximumVolumePercent / 100f;
        var proposedVolume = currentVolume * (float)Math.Pow(10, appliedDb / 20.0);
        var newVolume = Math.Clamp(proposedVolume, minVolume, maxVolume);

        var direction = errorDb < 0 ? "낮추는" : "높이는";
        var atLimit = Math.Abs(newVolume - currentVolume) < 0.0001f;
        var detail = atLimit
            ? $"설정한 안전 범위에 도달했습니다 · 현재 {currentVolume * 100:0}%"
            : $"목표까지 {Math.Abs(errorDb):0.0} dB · 앱 볼륨 {newVolume * 100:0}%";

        return new NormalizationResult(atLimit ? null : newVolume, controlLevelDb, $"음량을 {direction} 중", detail);
    }

    private static double GetLowerRateDbPerSecond(double excessDb) => excessDb switch
    {
        >= 10 => 28,
        >= 6 => 20,
        >= 3 => 12,
        _ => 8
    };

    public void Reset()
    {
        _squaredPeaks.Clear();
        _fastSquaredPeaks.Clear();
        _silentTicks = 0;
    }

    public static double LinearToDb(double linear) => linear <= 0.000001 ? -60 : Math.Max(-60, 20 * Math.Log10(linear));
}

public sealed record NormalizationResult(
    float? NewVolume,
    double DisplayLevelDb,
    string StatusText,
    string DetailText);

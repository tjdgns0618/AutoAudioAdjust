using AutoAudioAdjust.App.Models;

namespace AutoAudioAdjust.App.Audio;

/// <summary>
/// Keeps the heard level of one app near the target. The input is the true RMS of the
/// app's audio for each tick, measured after the app volume (see <see cref="ProcessLoopbackMeter"/>).
/// The windows store the source level (heard level divided by the volume that was applied), so
/// changing the volume does not leave stale loud values in the history and cause overshoot.
/// The heard level at the current volume is then predicted from that source level.
/// </summary>
public sealed class LevelNormalizer
{
    /// <summary>Lowest level the app shows or measures; values at or below it mean "no sound".</summary>
    public const double FloorDb = -90;

    // Silence is judged on the song itself (before the app volume), so a very low target or a
    // low app volume never makes quiet music look like silence.
    private const double SourceSilenceGateDb = -60;
    private const double DeadBandDb = 1.25;
    private const double RaiseRateDbPerSecond = 2.0;
    private const int WindowSize = 20;
    private const int FastWindowSize = 2;
    private const int MinimumSamples = 8;

    private readonly Queue<double> _squaredLevels = new();
    private readonly Queue<double> _fastSquaredLevels = new();
    private int _silentTicks;
    private float? _previousVolume;

    public NormalizationResult Process(double rms, float currentVolume, AppSettings settings, double elapsedSeconds)
    {
        // The audio measured in this tick was played at the volume set on the previous tick.
        var playedVolume = _previousVolume ?? currentVolume;
        _previousVolume = currentVolume;

        var tickDb = LinearToDb(rms);
        var sourceDb = playedVolume < 0.001f ? FloorDb : LinearToDb(rms / playedVolume);
        if (sourceDb <= SourceSilenceGateDb)
        {
            _silentTicks++;
            if (_silentTicks >= 10)
            {
                _squaredLevels.Clear();
                _fastSquaredLevels.Clear();
            }

            return new NormalizationResult(null, tickDb, "소리를 기다리는 중", "무음 구간에서는 볼륨을 변경하지 않습니다.");
        }

        _silentTicks = 0;
        var source = rms / playedVolume;
        _squaredLevels.Enqueue(source * source);
        _fastSquaredLevels.Enqueue(source * source);
        while (_squaredLevels.Count > WindowSize)
        {
            _squaredLevels.Dequeue();
        }
        while (_fastSquaredLevels.Count > FastWindowSize)
        {
            _fastSquaredLevels.Dequeue();
        }

        // Predicted heard level at the current volume.
        var volumeDb = 20 * Math.Log10(Math.Max(0.001, currentVolume));
        var measuredDb = SquaredToDb(_squaredLevels.Average()) + volumeDb;
        var fastMeasuredDb = SquaredToDb(_fastSquaredLevels.Average()) + volumeDb;
        var fastErrorDb = settings.TargetLevelDb - fastMeasuredDb;
        var canUseFastProtection = _fastSquaredLevels.Count >= FastWindowSize && fastErrorDb < -DeadBandDb;

        if (_squaredLevels.Count < MinimumSamples && !canUseFastProtection)
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
        _squaredLevels.Clear();
        _fastSquaredLevels.Clear();
        _silentTicks = 0;
        _previousVolume = null;
    }

    // Source levels can exceed full scale when the volume is low, so no floor/clamp here.
    private static double SquaredToDb(double squared) => 10 * Math.Log10(Math.Max(1e-12, squared));

    public static double LinearToDb(double linear) =>
        linear <= 1e-9 ? FloorDb : Math.Max(FloorDb, 20 * Math.Log10(linear));

    /// <summary>True when a displayed level means "no sound".</summary>
    public static bool IsSilent(double db) => db <= FloorDb + 0.1;
}

public sealed record NormalizationResult(
    float? NewVolume,
    double DisplayLevelDb,
    string StatusText,
    string DetailText);

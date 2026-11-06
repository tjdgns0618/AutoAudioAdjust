using AutoAudioAdjust.App.Audio;
using AutoAudioAdjust.App.Models;

namespace AutoAudioAdjust.Tests;

public sealed class LevelNormalizerTests
{
    [Fact]
    public void SilenceNeverChangesVolume()
    {
        var normalizer = new LevelNormalizer();
        var settings = new AppSettings();

        var result = normalizer.Process(0, 0.5f, settings, 0.1);

        Assert.Null(result.NewVolume);
        Assert.Contains("기다리는", result.StatusText);
    }

    [Fact]
    public void LoudSignalIsLoweredAfterAnalysisWindow()
    {
        var normalizer = new LevelNormalizer();
        var settings = new AppSettings { TargetLevelDb = -18 };

        var result = ProcessRepeatedly(normalizer, settings, peak: 0.5f, volume: 0.5f);

        Assert.NotNull(result.NewVolume);
        Assert.True(result.NewVolume < 0.5f);
    }

    [Fact]
    public void SuddenlyLoudTrackUsesFastProtectionWindow()
    {
        var normalizer = new LevelNormalizer();
        var settings = new AppSettings { TargetLevelDb = -18 };

        for (var i = 0; i < 20; i++)
        {
            normalizer.Process(0.08f, 0.8f, settings, 0.1);
        }

        normalizer.Process(0.9f, 0.8f, settings, 0.1);
        var result = normalizer.Process(0.9f, 0.8f, settings, 0.1);

        Assert.NotNull(result.NewVolume);
        Assert.True(result.NewVolume < 0.65f);
        Assert.Contains("낮추는", result.StatusText);
    }

    [Fact]
    public void QuietTrackDoesNotUseFastVolumeRaise()
    {
        var normalizer = new LevelNormalizer();
        var settings = new AppSettings { TargetLevelDb = -12 };

        normalizer.Process(0.08f, 0.4f, settings, 0.1);
        var result = normalizer.Process(0.08f, 0.4f, settings, 0.1);

        Assert.Null(result.NewVolume);
        Assert.Contains("분석", result.StatusText);
    }

    [Fact]
    public void QuietSignalIsRaisedAfterAnalysisWindow()
    {
        var normalizer = new LevelNormalizer();
        var settings = new AppSettings { TargetLevelDb = -12 };

        var result = ProcessRepeatedly(normalizer, settings, peak: 0.08f, volume: 0.4f);

        Assert.NotNull(result.NewVolume);
        Assert.True(result.NewVolume > 0.4f);
    }

    [Fact]
    public void SignalInsideDeadBandIsNotChanged()
    {
        var normalizer = new LevelNormalizer();
        var settings = new AppSettings { TargetLevelDb = -18 };
        var matchingPeak = (float)Math.Pow(10, -18 / 20.0);

        var result = ProcessRepeatedly(normalizer, settings, matchingPeak, 0.5f);

        Assert.Null(result.NewVolume);
        Assert.Contains("유지", result.StatusText);
    }

    [Fact]
    public void MinimumSafetyLimitIsRespected()
    {
        var normalizer = new LevelNormalizer();
        var settings = new AppSettings
        {
            TargetLevelDb = -30,
            MinimumVolumePercent = 20,
            MaximumVolumePercent = 80
        };

        var result = ProcessRepeatedly(normalizer, settings, peak: 0.9f, volume: 0.2f);

        Assert.Null(result.NewVolume);
        Assert.Contains("안전 범위", result.DetailText);
    }

    [Fact]
    public void SettingsValidationMaintainsSafeGap()
    {
        var settings = new AppSettings
        {
            TargetLevelDb = 4,
            MinimumVolumePercent = 78,
            MaximumVolumePercent = 30
        };

        settings.Validate();

        Assert.Equal(-8, settings.TargetLevelDb);
        Assert.True(settings.MaximumVolumePercent >= settings.MinimumVolumePercent + 5);
        Assert.InRange(settings.MaximumVolumePercent, 20, 100);
    }

    private static NormalizationResult ProcessRepeatedly(
        LevelNormalizer normalizer,
        AppSettings settings,
        float peak,
        float volume)
    {
        NormalizationResult? result = null;
        for (var i = 0; i < 8; i++)
        {
            result = normalizer.Process(peak, volume, settings, 0.1);
        }

        return result!;
    }
}

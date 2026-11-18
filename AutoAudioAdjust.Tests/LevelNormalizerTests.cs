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

        var result = ProcessRepeatedly(normalizer, settings, rms: 0.5f, volume: 0.5f);

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

        var result = ProcessRepeatedly(normalizer, settings, rms: 0.08f, volume: 0.4f);

        Assert.NotNull(result.NewVolume);
        Assert.True(result.NewVolume > 0.4f);
    }

    [Fact]
    public void SignalInsideDeadBandIsNotChanged()
    {
        var normalizer = new LevelNormalizer();
        var settings = new AppSettings { TargetLevelDb = -18 };
        var matchingRms = (float)Math.Pow(10, -18 / 20.0);

        var result = ProcessRepeatedly(normalizer, settings, matchingRms, 0.5f);

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

        var result = ProcessRepeatedly(normalizer, settings, rms: 0.9f, volume: 0.2f);

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

        Assert.Equal(-18, settings.TargetLevelDb);
        Assert.True(settings.MaximumVolumePercent >= settings.MinimumVolumePercent + 5);
        Assert.InRange(settings.MaximumVolumePercent, 20, 100);
    }

    private static NormalizationResult ProcessRepeatedly(
        LevelNormalizer normalizer,
        AppSettings settings,
        double rms,
        float volume)
    {
        NormalizationResult? result = null;
        for (var i = 0; i < 8; i++)
        {
            result = normalizer.Process(rms, volume, settings, 0.1);
        }

        return result!;
    }

    [Fact]
    public void OldPeakScaleTargetIsMovedOntoTrueRmsScale()
    {
        var settings = new AppSettings { TargetLevelDb = -18, LevelScaleVersion = 1 };

        settings.MigrateLevelScale();
        settings.MigrateLevelScale();

        Assert.Equal(-28, settings.TargetLevelDb);
        Assert.Equal(AppSettings.CurrentLevelScaleVersion, settings.LevelScaleVersion);
    }

    [Fact]
    public void NewSettingsAreAlreadyOnTrueRmsScale()
    {
        var settings = new AppSettings();

        settings.MigrateLevelScale();

        Assert.Equal(-28, settings.TargetLevelDb);
    }

    [Fact]
    public void LoweringDoesNotOvershootWhenMeasurementFollowsVolume()
    {
        // Simulates the real app: the heard level is source x volume and lags one tick.
        var normalizer = new LevelNormalizer();
        var settings = new AppSettings { TargetLevelDb = -40, MinimumVolumePercent = 1, MaximumVolumePercent = 100 };
        const double sourceRms = 0.065; // about -23.7 dB, as measured on YouTube Music
        var idealVolume = Math.Pow(10, -40 / 20.0) / sourceRms; // about 0.154
        var volume = 0.5f;
        var playedVolume = volume;
        var lowestVolume = volume;

        for (var tick = 0; tick < 100; tick++)
        {
            var result = normalizer.Process(sourceRms * playedVolume, volume, settings, 0.1);
            playedVolume = volume;
            if (result.NewVolume is { } newVolume)
            {
                volume = newVolume;
            }
            lowestVolume = Math.Min(lowestVolume, volume);
        }

        var overshootDb = 20 * Math.Log10(idealVolume / lowestVolume);
        var finalErrorDb = 20 * Math.Log10(volume / idealVolume);
        Assert.True(overshootDb < 2.5, $"overshoot {overshootDb:0.0} dB");
        Assert.InRange(finalErrorDb, -1.5, 1.5);
    }

    [Fact]
    public void QuietHeardLevelIsNotMistakenForSilenceWithLowTarget()
    {
        // Music at -23.7 dB played at 2% is heard at about -57.7 dB. With a -50 dB target the
        // app must keep adjusting instead of treating it as silence.
        var normalizer = new LevelNormalizer();
        var settings = new AppSettings { TargetLevelDb = -50, MinimumVolumePercent = 1, MaximumVolumePercent = 100 };
        const double sourceRms = 0.065;
        var volume = 0.02f;
        var playedVolume = volume;
        NormalizationResult? result = null;

        for (var tick = 0; tick < 40; tick++)
        {
            result = normalizer.Process(sourceRms * playedVolume, volume, settings, 0.1);
            playedVolume = volume;
            if (result.NewVolume is { } newVolume)
            {
                volume = newVolume;
            }
        }

        Assert.DoesNotContain("기다리는", result!.StatusText);
        Assert.True(volume > 0.02f, $"volume stayed at {volume}");
    }

    [Fact]
    public void TargetCanGoDownToMinus75()
    {
        var settings = new AppSettings { TargetLevelDb = -90 };

        settings.Validate();

        Assert.Equal(-75, settings.TargetLevelDb);
    }

    [Theory]
    [InlineData(-75, 0)]
    [InlineData(-18, 100)]
    [InlineData(-46.5, 50)]
    [InlineData(-90, 0)]
    [InlineData(-5, 100)]
    public void LoudnessScaleMapsTargetRange(double db, double expectedScale)
    {
        Assert.Equal(expectedScale, Loudness.ToScale(db), 3);
    }

    [Fact]
    public void LoudnessScaleRoundTrips()
    {
        for (var scale = 0; scale <= 100; scale += 10)
        {
            Assert.Equal(scale, Loudness.ToScale(Loudness.ToDb(scale)), 6);
        }
    }
    [Fact]
    public void SavedVolumesAndWindowSurviveJsonRoundTrip()
    {
        var settings = new AppSettings
        {
            YouTubeMusicVolume = 47,
            YouTubeMusicMuted = true,
            AppVolumePercent = 13,
            AppMuted = false,
            Window = new WindowPlacement { Left = 100, Top = 80, Width = 1280, Height = 800, Maximized = true }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        loaded.Validate();

        Assert.Equal(47, loaded.YouTubeMusicVolume);
        Assert.True(loaded.YouTubeMusicMuted);
        Assert.Equal(13, loaded.AppVolumePercent);
        Assert.NotNull(loaded.Window);
        Assert.Equal(1280, loaded.Window!.Width);
        Assert.True(loaded.Window.Maximized);
    }

    [Fact]
    public void InvalidSavedVolumesAndWindowAreCorrected()
    {
        var settings = new AppSettings
        {
            YouTubeMusicVolume = 140,
            AppVolumePercent = -5,
            Window = new WindowPlacement { Left = double.NaN, Top = 0, Width = 1280, Height = 800 }
        };

        settings.Validate();

        Assert.Equal(100, settings.YouTubeMusicVolume);
        Assert.Equal(0, settings.AppVolumePercent);
        Assert.Null(settings.Window);
    }
}

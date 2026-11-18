namespace AutoAudioAdjust.App.Models;

public sealed class AppSettings
{
    /// <summary>
    /// Version 1 measured Windows session peak readings, which ignore the app volume.
    /// Version 2 measures the true RMS of the audio the app actually plays. The old target is
    /// shifted so the old default (-18) lands on the new default (-28).
    /// </summary>
    public const int CurrentLevelScaleVersion = 2;
    private const double PeakToTrueRmsOffsetDb = 10;

    public double TargetLevelDb { get; set; } = -28;
    public int MinimumVolumePercent { get; set; } = 10;
    public int MaximumVolumePercent { get; set; } = 85;
    public bool AutoAdjustEnabled { get; set; } = true;

    /// <summary>What the window's X button does: ask each time, hide to the tray, or exit.</summary>
    public CloseAction CloseAction { get; set; } = CloseAction.Ask;

    /// <summary>The "still playing in the tray" balloon is shown only once.</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>0 for files written before this property existed (version 1 scale).</summary>
    public int LevelScaleVersion { get; set; } = CurrentLevelScaleVersion;

    public void MigrateLevelScale()
    {
        if (LevelScaleVersion < CurrentLevelScaleVersion)
        {
            TargetLevelDb -= PeakToTrueRmsOffsetDb;
            LevelScaleVersion = CurrentLevelScaleVersion;
        }
    }

    public void Validate()
    {
        TargetLevelDb = Math.Clamp(TargetLevelDb, Audio.Loudness.MinDb, Audio.Loudness.MaxDb);
        MinimumVolumePercent = Math.Clamp(MinimumVolumePercent, 0, 80);
        MaximumVolumePercent = Math.Clamp(MaximumVolumePercent, 20, 100);
        if (MaximumVolumePercent < MinimumVolumePercent + 5)
        {
            MaximumVolumePercent = Math.Min(100, MinimumVolumePercent + 5);
        }
    }
}

public enum CloseAction
{
    Ask,
    Tray,
    Exit
}

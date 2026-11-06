namespace AutoAudioAdjust.App.Models;

public sealed class AppSettings
{
    public double TargetLevelDb { get; set; } = -18;
    public int MinimumVolumePercent { get; set; } = 10;
    public int MaximumVolumePercent { get; set; } = 85;
    public string? PreferredProcessName { get; set; }

    public void Validate()
    {
        TargetLevelDb = Math.Clamp(TargetLevelDb, -30, -8);
        MinimumVolumePercent = Math.Clamp(MinimumVolumePercent, 0, 80);
        MaximumVolumePercent = Math.Clamp(MaximumVolumePercent, 20, 100);
        if (MaximumVolumePercent < MinimumVolumePercent + 5)
        {
            MaximumVolumePercent = Math.Min(100, MinimumVolumePercent + 5);
        }
    }
}

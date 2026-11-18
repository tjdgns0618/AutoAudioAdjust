namespace AutoAudioAdjust.App.Audio;

/// <summary>
/// "듣는 크기" scale shown to the user: 0 (quietest target) to 100 (loudest target), linear in dB.
/// Settings keep storing the target in dB; this only converts for display and the slider.
/// </summary>
public static class Loudness
{
    public const double MinDb = -75;
    public const double MaxDb = -18;

    /// <summary>dB → 0..100 (clamped).</summary>
    public static double ToScale(double db) => Math.Clamp((db - MinDb) / (MaxDb - MinDb) * 100, 0, 100);

    /// <summary>0..100 → dB.</summary>
    public static double ToDb(double scale) => MinDb + Math.Clamp(scale, 0, 100) / 100 * (MaxDb - MinDb);
}

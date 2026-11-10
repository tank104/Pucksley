namespace Pucksley.Plugins.Windows.Audio;

/// <summary>Volume arithmetic shared by the master and per-app tools.</summary>
internal static class VolumeMath
{
    /// <summary>
    /// The level to set: <paramref name="volume"/> when given (it wins over a change), otherwise
    /// <paramref name="current"/> + <paramref name="change"/>; clamped to 0–100. Null when neither
    /// was given, meaning "just report it".
    /// </summary>
    public static int? Target(int current, int? volume, int? change)
    {
        if (volume is int absolute) return Math.Clamp(absolute, 0, 100);
        if (change is int delta) return Math.Clamp(current + delta, 0, 100);
        return null;
    }

    /// <summary>Core Audio's 0.0–1.0 scalar → 0–100.</summary>
    public static int ToPercent(float scalar) => (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100, MidpointRounding.AwayFromZero);

    /// <summary>0–100 → Core Audio's 0.0–1.0 scalar.</summary>
    public static float ToScalar(int percent) => Math.Clamp(percent, 0, 100) / 100f;
}

namespace UnifiedRgb.Core;

/// <summary>Global master brightness (0.05–1.0) applied at the last moment
/// before colors reach hardware — the engine scales every animated frame and
/// the app scales every static push, so saved profiles/frames stay unscaled
/// and brightness is non-destructive.</summary>
public static class Master
{
    static double _brightness = 1.0;

    public static double Brightness
    {
        get => Volatile.Read(ref _brightness);
        set => Volatile.Write(ref _brightness, Math.Clamp(value, 0.05, 1.0));
    }

    /// <summary>Scale a buffer in place (call only on frames/clones about to
    /// be written to hardware, never on stored state).</summary>
    public static void Scale(Rgb[] buf)
    {
        double b = Brightness;
        if (b >= 0.999) return;
        for (int i = 0; i < buf.Length; i++)
        {
            var c = buf[i];
            buf[i] = new Rgb((byte)(c.R * b + 0.5), (byte)(c.G * b + 0.5), (byte)(c.B * b + 0.5));
        }
    }
}

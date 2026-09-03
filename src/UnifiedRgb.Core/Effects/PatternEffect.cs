namespace UnifiedRgb.Core.Effects;

public enum PatternColor { Rainbow, Gradient, Solid, Wallpaper, Temp }
public enum PatternMotion { Static, Rotate, Chase, Breathe, Wave, AudioPulse, AudioLevel }

/// <summary>A user-authored pattern: a color source (rainbow, a custom gradient,
/// or a solid color) combined with a motion. Ring geometry is used so rotation
/// and chase sweep naturally around a fan.</summary>
public sealed class PatternEffect : IEffect
{
    public string Name => "Custom Pattern";
    // Solid mode is "this one color, with motion" - the color IS the user's
    // wheel pick (it rendered Palette[0], a default pink, and the wheel color
    // silently went nowhere).
    public bool UsesBaseColor => Color == PatternColor.Solid;
    public bool Bakeable => Color != PatternColor.Wallpaper && Color != PatternColor.Temp
        && Motion != PatternMotion.AudioPulse && Motion != PatternMotion.AudioLevel;
    public bool LiveInput => Motion is PatternMotion.AudioPulse or PatternMotion.AudioLevel;

    public PatternColor Color { get; set; } = PatternColor.Rainbow;
    public PatternMotion Motion { get; set; } = PatternMotion.Rotate;

    public void Render(IRgbDevice device, int offset, Rgb[] buf, LedPos[] pos,
                       double seconds, double speed, Rgb baseColor)
    {
        // -1 = ring geometry (single fan) · 1 = flow along Y (a stack of fans:
        // gradients run continuously down the whole column) · 0 = flow along X
        // (wide strips). Passed through as a PARAMETER: the old mutable field
        // was written per frame from every render thread, so one target
        // spanning two devices raced and frames could use the other device's
        // axis.
        bool wholeDevice = buf.Length == device.LedCount;
        float aspect = device.PreviewAspect ?? 1.0f;
        int axis = device is Devices.LianLiWireless && buf.Length >= 88 ? 1   // spans 2+ fans (group / all)
                 : wholeDevice && aspect <= 0.6f ? 1
                 : wholeDevice && aspect >= 1.8f ? 0
                 : -1;
        RenderCore(buf, pos, seconds, speed, baseColor, axis);
    }
    public bool Reverse { get; set; }
    public double Density { get; set; } = 1.0;         // color repeats around the ring
    public double TailLength { get; set; } = 0.35;     // comet tail (fraction of ring)
    public IReadOnlyList<Rgb> Palette { get; set; } = new[] { new Rgb(255, 0, 96), new Rgb(0, 160, 255) };

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb userColor)
        => RenderCore(buf, pos, t, speed, userColor, -1);   // no device context: ring default

    void RenderCore(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb userColor, int axis)
    {
        double dir = Reverse ? -1.0 : 1.0;
        double move = t * speed * 0.25 * dir;                 // ring turns per unit time
        double head = Frac(move);                             // comet head position
        bool moves = Motion is PatternMotion.Rotate or PatternMotion.Chase;
        double breathe = 0.2 + 0.8 * (0.5 + 0.5 * Math.Sin(t * speed * 2.0));

        // Screen Sync colors: sample the live screen at each LED's position;
        // moving motions spin the sampling coordinates so the wallpaper's
        // colors swirl around the ring.
        double ca = 1, sa = 0;
        if (Color == PatternColor.Wallpaper)
        {
            WallpaperCapture.Touch();
            if (moves) { double a = move * Math.PI * 2.0; ca = Math.Cos(a); sa = Math.Sin(a); }
        }

        // Temp color: green when cool through amber to red when hot (hottest
        // of CPU/GPU, same scale as Temp Glow). Dim blue = no sensor.
        Rgb tempColor = default;
        if (Color == PatternColor.Temp)
        {
            Sensors.SensorHub.TouchTemps();   // temp only: don't arm the Cooling-pane sweep
            tempColor = Sensors.SensorHub.HottestC is double tc
                ? ColorUtil.HsvToRgb(120.0 * (1.0 - Math.Clamp((tc - 35.0) / 50.0, 0, 1)), 1.0, 1.0)
                : ColorUtil.HsvToRgb(220, 0.9, 0.5);
        }

        // Audio motions: brightness rides the live level (with bass kick).
        double level = 0;
        if (Motion is PatternMotion.AudioPulse or PatternMotion.AudioLevel)
        {
            Audio.AudioAnalyzer.Touch();
            double punch = Math.Clamp(Math.Abs(speed), 0.25, 4.0);   // punch, not direction
            level = Math.Pow(0.6 * Audio.AudioAnalyzer.Level + 0.4 * Audio.AudioAnalyzer.Bass, 1.0 / punch);
        }

        for (int i = 0; i < buf.Length; i++)
        {
            double u = Coord(pos[i], axis);                   // 0..1 around the ring / along the span
            double p = u * Density + (moves ? move : 0.0);

            Rgb baseColor = Color switch
            {
                PatternColor.Rainbow => ColorUtil.HsvToRgb(Frac(p) * 360.0, 1.0, 1.0),
                // Rings need the gradient to wrap (seamless circle); a linear
                // stack wants it end-to-end: first color at the top, last at
                // the bottom - wrapped, both ends matched and the middle was
                // the "other" color, which read as inverted.
                PatternColor.Gradient => axis >= 0 ? SamplePaletteLinear(Frac(p)) : SamplePalette(Frac(p)),
                PatternColor.Wallpaper => SampleScreen(pos[i], ca, sa),
                PatternColor.Temp => tempColor,
                _ => userColor,   // Solid = the wheel color, live
            };

            double bright = Motion switch
            {
                PatternMotion.Breathe => breathe,
                PatternMotion.Wave => 0.12 + 0.88 * (0.5 + 0.5 * Math.Sin((u * Density - move) * Math.PI * 2.0)),
                PatternMotion.Chase => Comet(u, head),
                PatternMotion.AudioPulse => 0.05 + 0.95 * level,
                // Circular VU meter: the ring fills with the music level.
                PatternMotion.AudioLevel => Math.Clamp((level - (Reverse ? 1.0 - u : u)) * 10.0 + 0.5, 0.04, 1.0),
                _ => 1.0,
            };

            buf[i] = ColorUtil.Scale(baseColor, bright);
        }
    }

    double Comet(double u, double head)
    {
        double d = Math.Abs(u - head);
        d = Math.Min(d, 1.0 - d);                             // wrap around the ring
        double tail = Math.Max(0.05, TailLength);
        double v = 1.0 - d / tail;
        return v > 0 ? v * v : 0.0;
    }

    static Rgb SampleScreen(LedPos p, double ca, double sa)
    {
        double x = p.X - 0.5, y = p.Y - 0.5;
        return WallpaperCapture.Sample((float)(x * ca - y * sa + 0.5), (float)(x * sa + y * ca + 0.5));
    }

    /// <summary>Non-cyclic: f 0..1 maps first color -> last color exactly once.</summary>
    Rgb SamplePaletteLinear(double f)
    {
        var pal = Palette;
        int n = pal.Count;
        if (n == 0) return new Rgb(255, 255, 255);
        if (n == 1) return pal[0];
        double x = Math.Clamp(f, 0, 1) * (n - 1);
        int a = Math.Min((int)x, n - 2);
        return Lerp(pal[a], pal[a + 1], x - a);
    }

    /// <summary>Cyclic: wraps the last colour back to the first (ring gradients).</summary>
    Rgb SamplePalette(double f) => PaletteFx.Sample(Palette, f);

    static double Coord(LedPos p, int axis) => axis switch
    {
        0 => p.X,
        1 => p.Y,
        _ => RingCoord(p),
    };

    static double RingCoord(LedPos p)
    {
        double ang = Math.Atan2(p.Y - 0.5, p.X - 0.5);        // -pi..pi
        return Frac(ang / (Math.PI * 2.0) + 1.0);
    }

    // Shared helpers (the private copies here were byte-identical to Fx's).
    static double Frac(double v) => Fx.Frac(v);
    static Rgb Lerp(Rgb a, Rgb b, double f) => Fx.Lerp(a, b, f);
}

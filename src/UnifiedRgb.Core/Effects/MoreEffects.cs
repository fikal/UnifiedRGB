namespace UnifiedRgb.Core.Effects;

/// <summary>Samples a looped palette at a normalized position, lerping between
/// adjacent colors and wrapping the last back to the first so scrolls seam
/// cleanly.</summary>
static class PaletteFx
{
    public static Rgb Sample(IReadOnlyList<Rgb> pal, double u)
    {
        int n = pal.Count;
        if (n == 0) return new Rgb(255, 255, 255);
        if (n == 1) return pal[0];
        u -= Math.Floor(u);                 // wrap to 0..1
        double f = u * n;
        int i = (int)f % n;
        int j = (i + 1) % n;
        return Fx.Lerp(pal[i], pal[j], f - Math.Floor(f));
    }
}

/// <summary>A smooth blend of all your chosen colors laid across the rig,
/// scrolling slowly. Two colors = a clean fade; add more for a full ribbon.</summary>
public sealed class Gradient : IEffect, IPaletteEffect
{
    public string Name => "Gradient";
    public bool UsesBaseColor => false;
    public IReadOnlyList<Rgb> Palette { get; set; } = System.Array.Empty<Rgb>();
    public double LoopSeconds(double speed) => Fx.Loop(6.0, speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        double scroll = t * speed / 6.0;                // one full palette every 6s
        var dg = Geo.Diag(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double d = dg[i];
            buf[i] = PaletteFx.Sample(Palette, d + scroll);
        }
    }
}

/// <summary>The whole rig fades through your colors in turn - color to color,
/// holding each a beat. A calm, room-filling mood cycle.</summary>
public sealed class PaletteCycle : IEffect, IPaletteEffect
{
    public string Name => "Palette Cycle";
    public bool UsesBaseColor => false;
    public IReadOnlyList<Rgb> Palette { get; set; } = System.Array.Empty<Rgb>();
    public double LoopSeconds(double speed) => Fx.Loop(2.0 * Math.Max(1, Palette.Count), speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        double u = t * speed / (2.0 * Math.Max(1, Palette.Count));   // ~2s per color
        var c = PaletteFx.Sample(Palette, u);
        for (int i = 0; i < buf.Length; i++) buf[i] = c;
    }
}

/// <summary>Sparkles that each pop in one of your chosen colors - a multi-color
/// twinkle. Great with a festive two- or three-color palette.</summary>
public sealed class Confetti : IEffect, IPaletteEffect
{
    public string Name => "Confetti";
    public bool UsesBaseColor => false;
    public IReadOnlyList<Rgb> Palette { get; set; } = System.Array.Empty<Rgb>();
    public double LoopSeconds(double speed) => Fx.Loop((2.0 * Math.PI), speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        var pal = Palette;   // one snapshot per frame (see LivePalette)
        int n = Math.Max(1, pal.Count);
        for (int i = 0; i < buf.Length; i++)
        {
            // Rate/phase/color-pick are per-LED constants — cached tables.
            double s = Math.Sin(t * speed * Fx.SparkleRate(i) * 2.0 + Fx.SparklePhase(i));
            double s2 = s * s;
            double v = s > 0 ? s2 * s2 : 0.0;
            var col = pal.Count > 0 ? pal[(int)(Fx.SparklePick(i) * n) % n] : new Rgb(255, 255, 255);
            buf[i] = ColorUtil.Scale(col, 0.03 + 0.97 * v);
        }
    }
}

/// <summary>Circadian white-point: cool daylight around midday, warming to a low
/// amber deep at night, with a gentle brightness dip after dark. Auto - it reads
/// the wall clock, so the rig tracks the time of day on its own. Not a looped
/// animation, so it isn't baked; it streams and re-colors as the hours pass.</summary>
public sealed class TimeWarmth : IEffect
{
    public string Name => "Time Warmth";
    public bool UsesBaseColor => false;
    public bool Bakeable => false;
    public bool HasSpeed => false;      // clock-driven; the slider does nothing

    // Cool morning/afternoon white vs. warm-amber night.
    static readonly Rgb Day = new(205, 222, 255);
    static readonly Rgb Night = new(255, 138, 40);

    static double Smooth(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    // DateTime.Now does a timezone conversion per call and this ran per frame
    // per channel to produce a color that changes once a minute — cache it.
    static long _clockStamp;
    static double _cachedHour;

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        long tick = Environment.TickCount64;
        if (tick - System.Threading.Volatile.Read(ref _clockStamp) > 5000)
        {
            var nowDt = DateTime.Now;
            _cachedHour = nowDt.Hour + nowDt.Minute / 60.0;
            System.Threading.Volatile.Write(ref _clockStamp, tick);
        }
        double hour = _cachedHour;
        // Distance in hours from the ~13:00 solar peak, wrapped over 24h.
        double d = Math.Abs(hour - 13.0);
        if (d > 12) d = 24 - d;
        double warmth = Smooth(2.5, 10.5, d);           // 0 = cool midday, 1 = warm night
        var c = Fx.Lerp(Day, Night, warmth);
        double val = 1.0 - 0.35 * warmth;               // dim a little after dark
        c = ColorUtil.Scale(c, val);
        for (int i = 0; i < buf.Length; i++) buf[i] = c;
    }
}

/// <summary>Northern-lights curtains: soft vertical bands that ripple and drift,
/// their hue wandering around the color you pick so it shimmers between nearby
/// shades instead of one flat tone.</summary>
public sealed class Aurora : IEffect
{
    public string Name => "Aurora";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop((2.0 * Math.PI), speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        var (h0, s0, _) = ColorUtil.RgbToHsv(baseColor);
        if (s0 < 0.15) s0 = 0.7;                        // near-white picks still shimmer
        double ph = t * speed;
        for (int i = 0; i < buf.Length; i++)
        {
            double x = pos[i].X, y = pos[i].Y;
            // Layered slow sines make wide, wandering curtains.
            double band = Math.Sin(x * 6.0 + ph) + 0.6 * Math.Sin(x * 11.0 - ph * 1.3 + y * 2.0);
            double v = 0.12 + 0.88 * Math.Pow(0.5 + 0.5 * Math.Sin(band + y * 1.5), 1.6);
            double hue = h0 + 45.0 * Math.Sin(band * 0.6 + ph * 0.4);   // drift +/-45 around the pick
            buf[i] = ColorUtil.HsvToRgb(hue, s0, v);
        }
    }
}

/// <summary>A slow organic plasma field - blobs of light that swell and merge -
/// tinted through hues near the color you choose.</summary>
public sealed class Plasma : IEffect
{
    public string Name => "Plasma";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop((2.0 * Math.PI), speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        var (h0, s0, _) = ColorUtil.RgbToHsv(baseColor);
        if (s0 < 0.15) s0 = 0.85;
        double ph = t * speed;
        for (int i = 0; i < buf.Length; i++)
        {
            double x = pos[i].X * 6.0, y = pos[i].Y * 6.0;
            double f = Math.Sin(x + ph)
                     + Math.Sin(y * 1.3 + ph * 0.9)
                     + Math.Sin((x + y) * 0.7 + ph * 1.1)
                     + Math.Sin(Math.Sqrt(x * x + y * y) + ph * 0.7);
            f *= 0.25;                                  // -> roughly -1..1
            double v = 0.20 + 0.80 * (0.5 + 0.5 * f);
            double hue = h0 + 40.0 * f;                 // hue swims around the pick
            buf[i] = ColorUtil.HsvToRgb(hue, s0, v);
        }
    }
}

/// <summary>Droplets streaking downward in the chosen color - reads as rain on a
/// keyboard and as a top-to-bottom cascade on a stacked fan tower.</summary>
public sealed class Rain : IEffect
{
    public string Name => "Rain";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop(2.0, speed);
    // Strip fallback: rates that divide the 2 s loop (1, 2, 3 whole drops).
    static readonly double[] StripRates = { 0.5, 1.0, 1.5 };

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        // A strip has one Y for every LED: the column drops below would pulse
        // whole regions at once. Let the drops fall along its length instead.
        if (Geo.IsFlat(pos))
        {
            Span<double> heads = stackalloc double[StripRates.Length];
            for (int s = 0; s < heads.Length; s++)
                heads[s] = Fx.Frac(t * speed * StripRates[s] + Fx.Hash(s));
            for (int i = 0; i < buf.Length; i++)
            {
                double near = 1.0;
                for (int s = 0; s < heads.Length; s++)
                {
                    double d = Fx.Frac(heads[s] - pos[i].X);
                    if (d < near) near = d;
                }
                double lit = near < 0.25 ? 1.0 - near / 0.25 : 0.0;
                buf[i] = ColorUtil.Scale(baseColor, 0.03 + 0.97 * lit * lit);
            }
            return;
        }

        // 17 column buckets: hoist each column's rate/phase and CURRENT drop
        // head out of the per-LED loop (they were re-hashed per LED per frame).
        Span<double> drop = stackalloc double[17];
        for (int c = 0; c <= 16; c++)
        {
            double rate = 0.6 + Fx.Hash(c, 3) * 1.1;
            drop[c] = Fx.Frac(t * speed * rate + Fx.Hash(c));   // 0..1 head position
        }
        for (int i = 0; i < buf.Length; i++)
        {
            // Each LED belongs to a "column" bucket by X; drops fall per column
            // on their own phase and speed so the rain never marches in lockstep.
            int col = Math.Clamp((int)(pos[i].X * 16), 0, 16);
            double dist = drop[col] - pos[i].Y;                       // >0 = above this LED
            double v = dist >= 0 && dist < 0.35 ? 1.0 - dist / 0.35 : 0.0;   // fading tail
            buf[i] = ColorUtil.Scale(baseColor, 0.03 + 0.97 * v * v);
        }
    }
}

/// <summary>A living candle flame in the color you pick - a warm flicker that
/// dips and flares, brightest low and softer at the top.</summary>
public sealed class Candle : IEffect
{
    public string Name => "Candle";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop((2.0 * Math.PI), speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        // A couple of incommensurate sines = an organic, non-repeating flicker.
        double ph = t * speed;
        double flick = 0.72
                     + 0.14 * Math.Sin(ph * 2.1)
                     + 0.09 * Math.Sin(ph * 5.3 + 1.7)
                     + 0.05 * Math.Sin(ph * 11.0 + 0.4);
        for (int i = 0; i < buf.Length; i++)
        {
            double top = pos[i].Y;                     // a touch dimmer toward the top of the flame
            double v = Math.Clamp(flick - top * 0.22, 0.05, 1.0);
            buf[i] = ColorUtil.Scale(baseColor, v);
        }
    }
}

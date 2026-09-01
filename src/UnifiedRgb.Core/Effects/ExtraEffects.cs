namespace UnifiedRgb.Core.Effects;

/*-----------------------------------------------------------*\
| The extended effect library: L-Connect concepts re-created  |
| natively (Meteor, Taichi, Tide, Runway, Scan, Twinkle,      |
| Disco, Heartbeat, Electric) plus originals (Fire, Matrix,   |
| Starfield, Police, Lava). All stateless - instances are     |
| shared across channels, every value derives from the clock  |
| and per-LED hashes, so devices stay phase-locked.           |
\*-----------------------------------------------------------*/

static class Fx
{
    public static double Frac(double v) => v - Math.Floor(v);

    /// <summary>The standard loop-period formula: K seconds at speed 1, scaled
    /// by speed with a floor so speed 0 can't divide to infinity. Was written
    /// out ~30 times across the effect library.</summary>
    public static double Loop(double k, double speed) => k / Math.Max(0.1, Math.Abs(speed));

    /// <summary>Deterministic pseudo-random 0..1 per (LED, salt).</summary>
    public static double Hash(int i, int j = 0)
        => Frac(Math.Sin(i * 127.1 + j * 311.7 + 0.137) * 43758.5453);

    /// <summary>Wrapped distance on a 0..1 ring.</summary>
    public static double WrapDist(double a, double b)
    {
        double d = Math.Abs(a - b);
        return Math.Min(d, 1.0 - d);
    }

    public static Rgb Lerp(Rgb a, Rgb b, double f) => new(
        (byte)(a.R + (b.R - a.R) * f),
        (byte)(a.G + (b.G - a.G) * f),
        (byte)(a.B + (b.B - a.B) * f));

    /*----- cached per-LED constants for the sparkle effects (Twinkle/Confetti):
           Hash() is Sin+Floor, and these were recomputed 2-3x per LED per frame
           forever despite depending only on the LED index. 4096 covers any real
           device; indices past it fall back to the live hash. -----*/
    const int SparkleN = 4096;
    static readonly double[] _spRate = new double[SparkleN];
    static readonly double[] _spPhase = new double[SparkleN];
    static readonly double[] _spPick = new double[SparkleN];
    static Fx()
    {
        for (int i = 0; i < SparkleN; i++)
        {
            _spRate[i] = (1 + (int)(Hash(i) * 4)) * 0.5;      // 0.5,1,1.5,2
            _spPhase[i] = Hash(i, 7) * Math.PI * 2;
            _spPick[i] = Hash(i, 13);
        }
    }
    public static double SparkleRate(int i) => i < SparkleN ? _spRate[i] : (1 + (int)(Hash(i) * 4)) * 0.5;
    public static double SparklePhase(int i) => i < SparkleN ? _spPhase[i] : Hash(i, 7) * Math.PI * 2;
    public static double SparklePick(int i) => i < SparkleN ? _spPick[i] : Hash(i, 13);
}

/// <summary>A comet with a fading tail sweeping across the target.</summary>
public sealed class Meteor : IEffect
{
    public string Name => "Meteor";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop(2.5, speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double head = Fx.Frac(t * speed * 0.4);
        var dg = Geo.Diag(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double d = dg[i];
            double behind = Fx.Frac(head - d);            // tail trails the head
            double v = behind < 0.35 ? Math.Pow(1.0 - behind / 0.35, 2) : 0.0;
            if (behind < 0.03) v = 1.0;                    // bright core
            buf[i] = ColorUtil.Scale(baseColor, 0.02 + 0.98 * v);
        }
    }
}

/// <summary>Three bright dots chasing around the target, airfield style.</summary>
public sealed class Runway : IEffect
{
    public string Name => "Runway";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop((1.0 / 0.3), speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double p0 = Fx.Frac(t * speed * 0.3);
        var dg = Geo.Diag(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double d = dg[i];
            double v = 0;
            for (int k = 0; k < 3; k++)
            {
                double dd = Fx.WrapDist(d, Fx.Frac(p0 + k / 3.0));
                if (dd < 0.06) v = Math.Max(v, 1.0 - dd / 0.06);
            }
            buf[i] = ColorUtil.Scale(baseColor, 0.05 + 0.95 * v);
        }
    }
}

/// <summary>A bar sweeping back and forth (Cylon/KITT).</summary>
public sealed class Scan : IEffect
{
    public string Name => "Scan";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop((1.0 / 0.35), speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double u = Fx.Frac(t * speed * 0.35);
        double p = 1.0 - Math.Abs(1.0 - 2.0 * u);         // ping-pong 0..1..0
        var dg = Geo.Diag(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double d = dg[i];
            double dd = Math.Abs(d - p);
            double v = dd < 0.09 ? 1.0 - dd / 0.09 : 0.0;
            buf[i] = ColorUtil.Scale(baseColor, 0.04 + 0.96 * v);
        }
    }
}

/// <summary>Two colors rotating around the center, yin-yang style. Uses the two
/// palette colors the user picks; falls back to the base color and its
/// complement when no palette is set.</summary>
public sealed class Taichi : IEffect, IPaletteEffect
{
    public string Name => "Taichi";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop((2.0 * Math.PI / 1.2), speed);
    public IReadOnlyList<Rgb> Palette { get; set; } = System.Array.Empty<Rgb>();
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        Rgb a = Palette.Count > 0 ? Palette[0] : baseColor;
        Rgb b = Palette.Count > 1 ? Palette[1]
              : new Rgb((byte)(255 - a.R), (byte)(255 - a.G), (byte)(255 - a.B));
        double rot = t * speed * 1.2;
        var angles = Geo.Angle(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double ang = angles[i] + rot;
            double mix = Math.Clamp(Math.Sin(ang) * 3.0, -1, 1) * 0.5 + 0.5;   // soft edges
            buf[i] = Fx.Lerp(a, b, mix);
        }
    }
}

/// <summary>The target fills and drains like a tide, bottom to top.</summary>
public sealed class TideFx : IEffect
{
    public string Name => "Tide";
    public bool UsesBaseColor => true;
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double level = 0.5 + 0.48 * Math.Sin(t * speed * 0.8);
        for (int i = 0; i < buf.Length; i++)
        {
            double fill = 1.0 - pos[i].Y;                  // water rises from the bottom
            double v = Math.Clamp((level - fill) * 8.0 + 0.5, 0.03, 1.0);
            buf[i] = ColorUtil.Scale(baseColor, v);
        }
    }
}

/// <summary>Lub-dub double pulse, like a heartbeat monitor.</summary>
public sealed class Heartbeat : IEffect
{
    public string Name => "Heartbeat";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop(2.0, speed);
    static double Thump(double p, double c, double w)
    {
        double d = (p - c) / w;
        return Math.Exp(-d * d);
    }
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double p = Fx.Frac(t * speed * 0.9);
        double v = 0.05 + 0.95 * Math.Min(1.0, Thump(p, 0.08, 0.05) + 0.65 * Thump(p, 0.28, 0.05));
        var c = ColorUtil.Scale(baseColor, v);
        for (int i = 0; i < buf.Length; i++) buf[i] = c;
    }
}

/// <summary>Every LED sparkles on its own random rhythm.</summary>
public sealed class Twinkle : IEffect
{
    public string Name => "Twinkle";
    public bool UsesBaseColor => true;
    // Per-LED rates snapped to harmonics {0.5,1,1.5,2} so every LED completes a
    // whole number of cycles over a 2*pi loop - looks the same, but loops clean.
    public double LoopSeconds(double speed) => Fx.Loop((2.0 * Math.PI), speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            // Rate/phase are per-LED CONSTANTS - table lookup, not 2 Sin/frame.
            double s = Math.Sin(t * speed * Fx.SparkleRate(i) * 2.0 + Fx.SparklePhase(i));
            double s2 = s * s;
            double v = s > 0 ? s2 * s2 : 0.0;
            buf[i] = ColorUtil.Scale(baseColor, 0.04 + 0.96 * v);
        }
    }
}

/// <summary>Random color blocks snapping to the beat of a step clock.</summary>
public sealed class Disco : IEffect
{
    public string Name => "Disco";
    public bool UsesBaseColor => false;
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        int step = (int)(t * speed * 1.8);
        for (int i = 0; i < buf.Length; i++)
            buf[i] = ColorUtil.HsvToRgb(Fx.Hash(i / 2, step) * 360.0, 1.0, 1.0);
    }
}

/// <summary>Red and blue halves strobing alternately.</summary>
public sealed class Police : IEffect
{
    public string Name => "Police";
    public bool UsesBaseColor => false;
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        double phase = Fx.Frac(t * speed * 0.9);
        bool redActive = phase < 0.5;
        double strobe = Math.Sin(t * speed * 28.0) > 0.15 ? 1.0 : 0.0;
        var red = new Rgb(255, 0, 0);
        var blue = new Rgb(0, 60, 255);
        for (int i = 0; i < buf.Length; i++)
        {
            bool leftSide = pos[i].X < 0.5;
            bool active = leftSide == redActive;
            var c = leftSide ? red : blue;
            buf[i] = ColorUtil.Scale(c, active ? 0.05 + 0.95 * strobe : 0.05);
        }
    }
}

/// <summary>Unstable crackling energy with random arcs.</summary>
public sealed class Electric : IEffect
{
    public string Name => "Electric";
    public bool UsesBaseColor => true;
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double flicker = 0.18 + 0.30 * Math.Abs(Math.Sin(t * 3.7)) * Math.Abs(Math.Sin(t * 7.3 + 1.0));
        int step = (int)(t * speed * 12.0);
        for (int i = 0; i < buf.Length; i++)
        {
            double v = flicker;
            if (Fx.Hash(i, step) > 0.955) v = 1.0;         // arc!
            buf[i] = ColorUtil.Scale(baseColor, v);
        }
    }
}

/// <summary>Flames: hot at the bottom, embers flickering upward.</summary>
public sealed class Fire : IEffect
{
    public string Name => "Fire";
    public bool UsesBaseColor => false;
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            double x = pos[i].X, y = pos[i].Y;
            double n = 0.55 + 0.30 * Math.Sin(x * 12.0 + t * speed * 3.0)
                            + 0.15 * Math.Sin(x * 23.0 - t * speed * 5.0 + Fx.Hash(i) * 6.0);
            double heat = Math.Clamp((1.0 - y * 0.85) * n, 0, 1);
            buf[i] = ColorUtil.HsvToRgb(heat * 45.0, 1.0, Math.Clamp(heat * 1.5, 0.02, 1.0));
        }
    }
}

/// <summary>Sparse cool-white stars blinking in the dark.</summary>
public sealed class Starfield : IEffect
{
    public string Name => "Starfield";
    public bool UsesBaseColor => false;
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        double u = t * speed * 0.5;
        int window = (int)u;
        double ph = Fx.Frac(u);
        for (int i = 0; i < buf.Length; i++)
        {
            bool star = Fx.Hash(i, window) > 0.90;
            double v = star ? Math.Pow(Math.Sin(ph * Math.PI), 2) : 0.02;
            double warm = Fx.Hash(i, 3);
            buf[i] = new Rgb(
                (byte)(v * (200 + warm * 55)),
                (byte)(v * (210 + warm * 30)),
                (byte)(v * 255));
        }
    }
}

/// <summary>Green code streams raining down the target.</summary>
public sealed class MatrixRain : IEffect
{
    public string Name => "Matrix";
    public bool UsesBaseColor => false;
    // Per-column fall speeds snapped to {0.2,0.3,0.4,0.5} so each completes whole
    // drops over a 10s loop - every stream lines up at the wrap.
    public double LoopSeconds(double speed) => Fx.Loop(10.0, speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            int col = (int)(pos[i].X * 12);
            double rate = (2 + (int)(Fx.Hash(col) * 4)) * 0.1;              // 0.2,0.3,0.4,0.5
            double head = Fx.Frac(t * speed * rate + Fx.Hash(col, 3));
            double dd = Fx.Frac(head - pos[i].Y);           // trail above the head
            double v = dd < 0.45 ? Math.Pow(1.0 - dd / 0.45, 2) : 0.0;
            buf[i] = dd < 0.04
                ? new Rgb(190, 255, 190)                    // bright head
                : ColorUtil.Scale(new Rgb(0, 255, 70), v);
        }
    }
}

/// <summary>Slow molten blobs of deep red and orange.</summary>
public sealed class Lava : IEffect
{
    public string Name => "Lava";
    public bool UsesBaseColor => false;
    // Two drift rates set to periods 6s and 12s (2*pi/6, 2*pi/12) so the noise
    // field repeats every 12s instead of never.
    public double LoopSeconds(double speed) => Fx.Loop(12.0, speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            double x = pos[i].X, y = pos[i].Y;
            double n = 0.5 + 0.25 * Math.Sin(x * 6.0 + y * 3.0 + t * speed * (2 * Math.PI / 6))
                           + 0.25 * Math.Sin(x * 3.0 - y * 7.0 - t * speed * (2 * Math.PI / 12) + 2.0);
            buf[i] = ColorUtil.HsvToRgb(8.0 + 26.0 * n, 1.0, 0.20 + 0.80 * Math.Clamp(n, 0, 1));
        }
    }
}

/*----------- More L-Connect concepts, re-created natively -----------*/

/// <summary>A lit band scrolling around the ring, its hue drifting through the
/// spectrum as it travels - the "gradient ribbon".</summary>
public sealed class GradientRibbon : IEffect
{
    public string Name => "Gradient Ribbon";
    public bool UsesBaseColor => false;
    public double LoopSeconds(double speed) => Fx.Loop((1.0 / 0.175), speed);   // 2 bands, 1 hue turn
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        double u = t * speed * 0.35;
        var dg = Geo.Diag(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double d = dg[i];
            double band = Fx.Frac(d - u);
            double v = Math.Pow(Math.Sin(band * Math.PI), 2);         // one bright band
            double hue = 360.0 * Fx.Frac(d * 0.8 - u * 0.5);
            buf[i] = ColorUtil.HsvToRgb(hue, 1.0, 0.05 + 0.95 * v);
        }
    }
}

/// <summary>Two beams sweeping out from the center and back, like beating
/// wings.</summary>
public sealed class Wing : IEffect
{
    public string Name => "Wing";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop((2.0 * Math.PI / 1.1), speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double s = 0.5 * Math.Abs(Math.Sin(t * speed * 1.1));         // 0..0.5 spread
        for (int i = 0; i < buf.Length; i++)
        {
            double dx = Math.Abs(pos[i].X - 0.5);                     // distance from centerline
            double dd = Math.Abs(dx - s);
            double v = dd < 0.09 ? 1.0 - dd / 0.09 : 0.0;
            buf[i] = ColorUtil.Scale(baseColor, 0.04 + 0.96 * v);
        }
    }
}

/// <summary>A comet that sweeps out, then loops back the other way - its tail
/// always trailing behind.</summary>
public sealed class Boomerang : IEffect
{
    public string Name => "Boomerang";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop((1.0 / 0.3), speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double u = Fx.Frac(t * speed * 0.3);
        double p = 1.0 - Math.Abs(1.0 - 2.0 * u);                     // head ping-pongs 0..1..0
        double dir = u < 0.5 ? 1.0 : -1.0;
        var dg = Geo.Diag(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double d = dg[i];
            double behind = Fx.Frac((d - p) * dir);                   // tail trails the travel dir
            double v = behind < 0.3 ? Math.Pow(1.0 - behind / 0.3, 2) : 0.0;
            if (Math.Abs(d - p) < 0.03) v = 1.0;
            buf[i] = ColorUtil.Scale(baseColor, 0.02 + 0.98 * v);
        }
    }
}

/// <summary>A meteor whose head and tail cycle through the rainbow.</summary>
public sealed class MeteorRainbow : IEffect
{
    public string Name => "Meteor Rainbow";
    public bool UsesBaseColor => false;
    // Head repeats every 2.5s, hue every 5s -> loop over 5s (2 head laps, 1 hue).
    public double LoopSeconds(double speed) => Fx.Loop(5.0, speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        double head = Fx.Frac(t * speed * 0.4);
        var dg = Geo.Diag(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double d = dg[i];
            double behind = Fx.Frac(head - d);
            double v = behind < 0.35 ? Math.Pow(1.0 - behind / 0.35, 2) : 0.0;
            if (behind < 0.03) v = 1.0;
            double hue = 360.0 * Fx.Frac(t * speed * 0.2 - behind * 0.4);
            buf[i] = ColorUtil.HsvToRgb(hue, 1.0, v);
        }
    }
}

/// <summary>Two comets chasing from opposite sides of the ring.</summary>
public sealed class DoubleMeteor : IEffect
{
    public string Name => "Double Meteor";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop(2.5, speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double head = Fx.Frac(t * speed * 0.4);
        var dg = Geo.Diag(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double d = dg[i];
            double v = 0;
            for (int k = 0; k < 2; k++)
            {
                double behind = Fx.Frac((head + k * 0.5) - d);
                double vv = behind < 0.3 ? Math.Pow(1.0 - behind / 0.3, 2) : 0.0;
                if (behind < 0.03) vv = 1.0;
                v = Math.Max(v, vv);
            }
            buf[i] = ColorUtil.Scale(baseColor, 0.02 + 0.98 * v);
        }
    }
}

/// <summary>A meteor that changes to a fresh random color on every lap.</summary>
public sealed class ColorfulMeteor : IEffect
{
    public string Name => "Colorful Meteor";
    public bool UsesBaseColor => false;
    // One lap per loop: baked devices show one color per loop (still cycles live).
    public double LoopSeconds(double speed) => Fx.Loop(2.5, speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        double raw = t * speed * 0.4;
        double head = Fx.Frac(raw);
        var col = ColorUtil.HsvToRgb(Fx.Hash((int)raw) * 360.0, 1.0, 1.0);
        var dg = Geo.Diag(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double d = dg[i];
            double behind = Fx.Frac(head - d);
            double v = behind < 0.35 ? Math.Pow(1.0 - behind / 0.35, 2) : 0.0;
            if (behind < 0.03) v = 1.0;
            buf[i] = ColorUtil.Scale(col, v);
        }
    }
}

/// <summary>A wave mirrored about the center, rippling symmetrically out to
/// both edges.</summary>
public sealed class Reflect : IEffect
{
    public string Name => "Reflect";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop(Math.PI, speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double u = t * speed * 2.0;
        for (int i = 0; i < buf.Length; i++)
        {
            double d = Math.Abs((pos[i].X + pos[i].Y) * 0.5 - 0.5) * 2.0;   // folded 0..1
            double v = 0.15 + 0.85 * (0.5 + 0.5 * Math.Sin(u - d * Math.PI * 3.0));
            buf[i] = ColorUtil.Scale(baseColor, v);
        }
    }
}

/*----------- The rest of the L-Connect catalog, native -----------*/

/// <summary>The whole device holds one color that slowly morphs through the
/// spectrum, gently breathing as it goes.</summary>
public sealed class RainbowMorph : IEffect
{
    public string Name => "Rainbow Morph";
    public bool UsesBaseColor => false;
    // Bake one full hue turn (36 deg/s -> 10s). The breathe runs at exactly one
    // cycle per turn too (2*pi/10), so both close cleanly at the loop seam.
    public double LoopSeconds(double speed) => Fx.Loop(10.0, speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        double hue = (t * speed * 36.0) % 360.0;
        double v = 0.6 + 0.4 * (0.5 + 0.5 * Math.Sin(t * speed * (2.0 * Math.PI / 10.0)));
        var c = ColorUtil.HsvToRgb(hue, 1.0, v);
        for (int i = 0; i < buf.Length; i++) buf[i] = c;
    }
}

/// <summary>The device snaps through a sequence of solid colors, one after
/// another.</summary>
public sealed class ColorCycle : IEffect
{
    public string Name => "Color Cycle";
    public bool UsesBaseColor => false;
    // 12 evenly spaced hues, one per second, looping cleanly every 12s so the
    // baked loop returns to the first color (no odd blend at the seam).
    public double LoopSeconds(double speed) => Fx.Loop(12.0, speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        int step = (int)(t * speed) % 12;
        var c = ColorUtil.HsvToRgb(step * 30, 1.0, 1.0);
        for (int i = 0; i < buf.Length; i++) buf[i] = c;
    }
}

/// <summary>Two colors bleed and swirl into each other like flowing paint - the
/// base color and its complement over a drifting plasma field.</summary>
public sealed class Mixing : IEffect
{
    public string Name => "Mixing";
    public bool UsesBaseColor => true;
    // Both sines share one frequency (pi/3 rad/s); their product's time term is
    // cos(2wt), period pi/w = 3s, so the loop closes cleanly.
    const double MixW = Math.PI / 3.0;
    public double LoopSeconds(double speed) => Fx.Loop(3.0, speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        var other = new Rgb((byte)(255 - baseColor.R), (byte)(255 - baseColor.G), (byte)(255 - baseColor.B));
        for (int i = 0; i < buf.Length; i++)
        {
            double x = pos[i].X, y = pos[i].Y;
            double n = 0.5 + 0.5 * Math.Sin(x * 5.0 + t * speed * MixW) * Math.Sin(y * 5.0 - t * speed * MixW);
            buf[i] = Fx.Lerp(baseColor, other, n);
        }
    }
}

/// <summary>A bright arc grows out from the center to both edges and pulls back,
/// over and over.</summary>
public sealed class ReturnArc : IEffect
{
    public string Name => "Return Arc";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop((1.0 / 0.3), speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double p = 1.0 - Math.Abs(1.0 - 2.0 * Fx.Frac(t * speed * 0.3));    // 0..1..0
        for (int i = 0; i < buf.Length; i++)
        {
            double d = Math.Abs((pos[i].X + pos[i].Y) * 0.5 - 0.5) * 2.0;   // folded 0..1
            double dd = Math.Abs(d - p);
            double v = dd < 0.12 ? 1.0 - dd / 0.12 : 0.0;
            buf[i] = ColorUtil.Scale(baseColor, 0.04 + 0.96 * v);
        }
    }
}

/// <summary>Two arcs sweeping in opposite directions around the ring, crossing
/// as they pass.</summary>
public sealed class DoubleArc : IEffect
{
    public string Name => "Double Arc";
    public bool UsesBaseColor => true;
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double p = Fx.Frac(t * speed * 0.25);
        for (int i = 0; i < buf.Length; i++)
        {
            double a = Fx.Frac(Math.Atan2(pos[i].Y - 0.5, pos[i].X - 0.5) / (2 * Math.PI) + 0.5);
            double v = Math.Max(
                Fx.WrapDist(a, p) < 0.07 ? 1.0 - Fx.WrapDist(a, p) / 0.07 : 0.0,
                Fx.WrapDist(a, Fx.Frac(-p)) < 0.07 ? 1.0 - Fx.WrapDist(a, Fx.Frac(-p)) / 0.07 : 0.0);
            buf[i] = ColorUtil.Scale(baseColor, 0.04 + 0.96 * v);
        }
    }
}

/// <summary>The lights part from the center outward like opening doors, then
/// close again.</summary>
public sealed class Door : IEffect
{
    public string Name => "Door";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop((2.0 * Math.PI / 1.2), speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double open = 0.5 + 0.5 * Math.Sin(t * speed * 1.2);               // 0 closed .. 1 open
        for (int i = 0; i < buf.Length; i++)
        {
            double d = Math.Abs((pos[i].X + pos[i].Y) * 0.5 - 0.5) * 2.0;   // 0 center .. 1 edge
            double v = d <= open ? 1.0 : 0.05;                             // lit up to the door edge
            buf[i] = ColorUtil.Scale(baseColor, v);
        }
    }
}

/// <summary>A heartbeat pulse riding on a chase - the double-thump of Heartbeat
/// with dots running around the ring.</summary>
public sealed class HeartbeatRunway : IEffect
{
    public string Name => "Heartbeat Runway";
    public bool UsesBaseColor => true;
    public double LoopSeconds(double speed) => Fx.Loop(2.0, speed);   // beat and chase both 2s
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double ph = Fx.Frac(t * speed * 0.5);
        double beat = ph < 0.15 ? Math.Sin(ph / 0.15 * Math.PI)
                    : ph < 0.35 ? 0.6 * Math.Sin((ph - 0.2) / 0.15 * Math.PI) : 0.0;
        beat = Math.Max(0, beat);
        double p0 = Fx.Frac(t * speed * 0.5);
        var dg = Geo.Diag(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double d = dg[i];
            double chase = 0;
            for (int k = 0; k < 3; k++)
            {
                double dd = Fx.WrapDist(d, Fx.Frac(p0 + k / 3.0));
                if (dd < 0.06) chase = Math.Max(chase, 1.0 - dd / 0.06);
            }
            buf[i] = ColorUtil.Scale(baseColor, Math.Clamp(0.08 + 0.7 * beat + 0.5 * chase, 0, 1));
        }
    }
}

/// <summary>Sharp rhythmic pulses of the whole device, like a drum beat.</summary>
public sealed class Drumming : IEffect
{
    public string Name => "Drumming";
    public bool UsesBaseColor => true;
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double ph = Fx.Frac(t * speed * 0.5);
        // Two quick hits then a rest.
        double v = ph < 0.12 ? 1.0 - ph / 0.12
                 : (ph is >= 0.25 and < 0.37) ? 1.0 - (ph - 0.25) / 0.12 : 0.0;
        var c = ColorUtil.Scale(baseColor, 0.05 + 0.95 * Math.Max(0, v));
        for (int i = 0; i < buf.Length; i++) buf[i] = c;
    }
}

/// <summary>A hazard flash: the whole device blinks the color on and off.</summary>
public sealed class Warning : IEffect
{
    public string Name => "Warning";
    public bool UsesBaseColor => true;
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        bool on = Fx.Frac(t * speed * 0.6) < 0.5;
        var c = on ? baseColor : ColorUtil.Scale(baseColor, 0.03);
        for (int i = 0; i < buf.Length; i++) buf[i] = c;
    }
}

/// <summary>Several rainbow comets chasing at once, colors mixing as they
/// overlap.</summary>
public sealed class MeteorMix : IEffect
{
    public string Name => "Meteor Mix";
    public bool UsesBaseColor => false;
    public double LoopSeconds(double speed) => Fx.Loop(2.5, speed);
    // Hues 0/120/240 are pure R/G/B — the old per-LED loop paid 3 HsvToRgb
    // calls per LED per frame (the worst per-LED color cost in the library)
    // to produce channel values that are just 255*v.
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        double head = Fx.Frac(t * speed * 0.4);
        var dg = Geo.Diag(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double d = dg[i];
            double r = 0, g = 0, b = 0;
            for (int k = 0; k < 3; k++)
            {
                double behind = Fx.Frac((head + k / 3.0) - d);
                double u = 1.0 - behind / 0.3;
                double v = behind < 0.3 ? u * u : 0.0;
                if (behind < 0.03) v = 1.0;
                double ch = 255.0 * v;
                if (k == 0) r += ch; else if (k == 1) g += ch; else b += ch;
            }
            buf[i] = new Rgb((byte)Math.Min(255, r), (byte)Math.Min(255, g), (byte)Math.Min(255, b));
        }
    }
}

/// <summary>A block of color sweeps across, filling the device, then a second
/// pass wipes it clean - like mopping up.</summary>
public sealed class MopUp : IEffect
{
    public string Name => "Mop Up";
    public bool UsesBaseColor => true;
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
    {
        double u = Fx.Frac(t * speed * 0.25);
        bool filling = u < 0.5;
        double edge = filling ? u * 2.0 : (u - 0.5) * 2.0;                 // 0..1 each half
        var dg = Geo.Diag(pos);   // per-channel geometry cache
        for (int i = 0; i < buf.Length; i++)
        {
            double d = dg[i];
            bool lit = filling ? d <= edge : d > edge;                     // fill then wipe
            buf[i] = ColorUtil.Scale(baseColor, lit ? 1.0 : 0.03);
        }
    }
}

/// <summary>Random multicolored sparkles popping all over, like scattered
/// candy.</summary>
public sealed class CandyBox : IEffect
{
    public string Name => "Candy Box";
    public bool UsesBaseColor => false;
    // Loop over 3 windows (u: 0->3): the wrap lands on ph=0 where every sparkle
    // is dark, matching frame 0, so there's no pop at the seam.
    public double LoopSeconds(double speed) => Fx.Loop((3.0 / 0.6), speed);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        double u = t * speed * 0.6;
        int window = (int)u;
        double ph = Fx.Frac(u);
        for (int i = 0; i < buf.Length; i++)
        {
            bool lit = Fx.Hash(i, window) > 0.7;
            double v = lit ? Math.Pow(Math.Sin(ph * Math.PI), 2) : 0.0;
            double hue = Fx.Hash(i, window + 1) * 360.0;
            buf[i] = ColorUtil.HsvToRgb(hue, 1.0, 0.03 + 0.97 * v);
        }
    }
}

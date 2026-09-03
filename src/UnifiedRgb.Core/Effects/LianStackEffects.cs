using UnifiedRgb.Core.Devices;

namespace UnifiedRgb.Core.Effects;

/*-----------------------------------------------------------*\
| Structure-aware effects for the Lian Li fan stack. They     |
| know the real anatomy (per fan: 0-7 center hub, 8-27 outer  |
| ring clockwise from the top, 28-35 left rail top-down,      |
| 36-43 right rail top-down) and treat the whole column as    |
| one machine: two continuous vertical light rails, a border  |
| loop, rings and hubs as separate actors. On anything that   |
| is not a 44-LED-per-fan span they fall back to a generic    |
| position-based look.                                        |
\*-----------------------------------------------------------*/

static class Stack44
{
    public const int PerFan = 44;

    public static bool Applies(IRgbDevice d, int offset, int count)
        => d is LianLiWireless && offset % PerFan == 0 && count % PerFan == 0 && count >= PerFan;

    /// <summary>Position along the stack's BORDER loop (0..1, counter-
    /// clockwise: up the right rail, over the top ring, down the left rail,
    /// around the bottom ring), or -1 for interior LEDs.</summary>
    public static double PerimParam(int fans, int slot, int local)
    {
        int railLen = fans * 8;
        if (local >= 36)                                   // right rail (top-down order)
        {
            int idxFromBottom = (fans - 1 - slot) * 8 + (7 - (local - 36));
            return 0.25 * idxFromBottom / railLen;
        }
        if (local >= 28)                                   // left rail (top-down order)
        {
            int idxFromTop = slot * 8 + (local - 28);
            return 0.50 + 0.25 * idxFromTop / railLen;
        }
        if (local >= 8)                                    // outer ring, k=0 top, clockwise
        {
            double a = (local - 8) / 20.0;                 // 0..1 clockwise from the top
            if (slot == 0)                                 // top fan: right -> over the top -> left
            {
                double ccw = Frac(0.25 - a);
                if (ccw <= 0.5) return 0.25 + 0.25 * (ccw / 0.5);
            }
            if (slot == fans - 1 && a >= 0.25 && a <= 0.75)  // bottom fan: left -> bottom -> right
                return 0.75 + 0.25 * ((0.75 - a) / 0.5);
            return -1;
        }
        return -1;                                         // center hub
    }

    public static double Frac(double v) => Fx.Frac(v);   // shared helper (was a copy)
}

/// <summary>A comet pair racing around the border of the whole fan column
/// (rails + top and bottom rings) while the center hubs breathe.</summary>
public sealed class StackOutline : IEffect
{
    public string Name => "Outline";
    public bool UsesBaseColor => true;

    // These three effects exist only for the wireless fans, so they are ALWAYS
    // baked: the loop must be a true period or the fans pop at every wrap
    // (the inherited 4 s default was 1.4 comet laps - a visible teleport).
    // One lap of the comet pair at 0.35 laps/s, with the hub breath running
    // exactly one cycle per lap.
    const double HeadRate = 0.35;
    const double BreatheRate = 2.0 * Math.PI * HeadRate;
    public double LoopSeconds(double speed) => Fx.Loop(1.0 / HeadRate, speed);

    public void Render(IRgbDevice device, int offset, Rgb[] buf, LedPos[] pos,
                       double t, double speed, Rgb bc)
    {
        if (!Stack44.Applies(device, offset, buf.Length)) { Render(buf, pos, t, speed, bc); return; }
        int fans = buf.Length / 44;
        double head = Stack44.Frac(t * speed * HeadRate);
        double breathe = 0.22 + 0.22 * (0.5 + 0.5 * Math.Sin(t * speed * BreatheRate));
        for (int i = 0; i < buf.Length; i++)
        {
            int slot = i / 44, local = i % 44;
            double p = Stack44.PerimParam(fans, slot, local);
            double v;
            if (p >= 0)
            {
                double d1 = Stack44.Frac(head - p);
                double d2 = Stack44.Frac(head + 0.5 - p);   // second comet, opposite side
                double d = Math.Min(d1, d2);
                v = d < 0.20 ? Math.Pow(1.0 - d / 0.20, 2) : 0.04;
            }
            else v = local < 8 ? breathe : 0.03;            // hubs breathe, mid rings stay dark
            buf[i] = ColorUtil.Scale(bc, v);
        }
    }

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb bc)
    {
        double head = Stack44.Frac(t * speed * HeadRate);
        for (int i = 0; i < buf.Length; i++)
        {
            double d = Stack44.Frac(head - (pos[i].X + pos[i].Y) * 0.5);
            buf[i] = ColorUtil.Scale(bc, d < 0.25 ? Math.Pow(1.0 - d / 0.25, 2) : 0.04);
        }
    }
}

/// <summary>Droplets stream down both side rails; each fan's ring splashes
/// with light as a droplet passes it.</summary>
public sealed class Waterfall : IEffect
{
    public string Name => "Waterfall";
    public bool UsesBaseColor => true;

    // Drops and splashes are all period-1 in their head (0.45 laps/s, constant
    // salts), so one lap is the true period (default 4 s = 1.8 laps: every
    // drop jumped a fifth of the rail backwards at the seam).
    const double DropRate = 0.45;
    public double LoopSeconds(double speed) => Fx.Loop(1.0 / DropRate, speed);

    static double Drops(double y, double t, double speed, int salt)
    {
        double v = 0;
        for (int k = 0; k < 3; k++)
        {
            double head = Stack44.Frac(t * speed * DropRate + k / 3.0 + salt * 0.13);
            double dd = Stack44.Frac(head - y);              // tail above the droplet
            if (dd < 0.16) v = Math.Max(v, 1.0 - dd / 0.16);
        }
        return v;
    }

    static double Splash(int fans, int slot, double t, double speed, int salt)
    {
        double best = 0;
        for (int k = 0; k < 3; k++)
        {
            double head = Stack44.Frac(t * speed * DropRate + k / 3.0 + salt * 0.13);
            double d = Math.Abs(head * fans - (slot + 0.5));
            best = Math.Max(best, Math.Clamp(1.0 - d * 2.0, 0, 1));
        }
        return best;
    }

    public void Render(IRgbDevice device, int offset, Rgb[] buf, LedPos[] pos,
                       double t, double speed, Rgb bc)
    {
        if (!Stack44.Applies(device, offset, buf.Length)) { Render(buf, pos, t, speed, bc); return; }
        int fans = buf.Length / 44;
        int railLen = fans * 8;
        for (int i = 0; i < buf.Length; i++)
        {
            int slot = i / 44, local = i % 44;
            double v;
            if (local >= 36)                                 // right rail
                v = Drops((slot * 8 + (local - 36)) / (double)railLen, t, speed, 1);
            else if (local >= 28)                            // left rail
                v = Drops((slot * 8 + (local - 28)) / (double)railLen, t, speed, 0);
            else if (local >= 8)                             // ring: splash as drops pass
                v = 0.04 + 0.65 * Math.Max(Splash(fans, slot, t, speed, 0), Splash(fans, slot, t, speed, 1));
            else v = 0.03;                                   // hub
            buf[i] = ColorUtil.Scale(bc, v);
        }
    }

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb bc)
    {
        for (int i = 0; i < buf.Length; i++)
            buf[i] = ColorUtil.Scale(bc, 0.04 + 0.96 * Drops(pos[i].Y, t, speed, (int)(pos[i].X * 3)));
    }
}

/// <summary>Rings spin phase-staggered down the stack (a spiral through the
/// column), hubs counter-rotate, rails wash slowly. Rainbow-hued.</summary>
public sealed class Orbit : IEffect
{
    public string Name => "Orbit";
    public bool UsesBaseColor => false;

    // Every rate is a whole number of cycles over one 6 s loop (the RainbowWave
    // convention; 12 s would sit on the baker's clamp and reopen the seam at
    // any speed under 1): hue one full turn, ring comet 3 laps, hub dot 5 laps,
    // rail wash one sine. The inherited 4 s default closed none of them - a
    // 120 deg hue snap on every LED at each wrap.
    const double LoopS = 6.0;
    const double HueRate = 360.0 / LoopS;           // deg/s
    const double RingRate = 0.5;                    // laps/s (3 per loop)
    const double HubRate = 5.0 / LoopS;             // laps/s (5 per loop)
    const double RailRate = 2.0 * Math.PI / LoopS;  // rad/s (1 per loop)
    public double LoopSeconds(double speed) => Fx.Loop(LoopS, speed);

    public void Render(IRgbDevice device, int offset, Rgb[] buf, LedPos[] pos,
                       double t, double speed, Rgb _)
    {
        if (!Stack44.Applies(device, offset, buf.Length)) { Render(buf, pos, t, speed, _); return; }
        int fans = buf.Length / 44;
        for (int i = 0; i < buf.Length; i++)
        {
            int slot = i / 44, local = i % 44;
            double hue = t * speed * HueRate + slot * 35.0;
            if (local >= 28)                                 // rails: slow wash
            {
                double y = (slot * 8 + ((local - 28) % 8)) / (double)(fans * 8);
                double v = 0.12 + 0.18 * (0.5 + 0.5 * Math.Sin(t * speed * RailRate - y * 5.0));
                buf[i] = ColorUtil.HsvToRgb(hue + y * 90.0, 1.0, v);
            }
            else if (local >= 8)                             // ring comet, staggered per fan
            {
                double a = (local - 8) / 20.0;
                double head = Stack44.Frac(t * speed * RingRate + slot * 0.18);
                double d = Stack44.Frac(head - a);
                double v = d < 0.30 ? Math.Pow(1.0 - d / 0.30, 2) : 0.03;
                buf[i] = ColorUtil.HsvToRgb(hue, 1.0, v);
            }
            else                                             // hub: counter-rotating dot
            {
                double a = local / 8.0;
                double head = Stack44.Frac(-t * speed * HubRate + slot * 0.11);
                double d = Stack44.Frac(head - a);
                double v = d < 0.35 ? 1.0 - d / 0.35 : 0.04;
                buf[i] = ColorUtil.HsvToRgb(hue + 180.0, 0.8, v);
            }
        }
    }

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb _)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            double ang = Math.Atan2(pos[i].Y - 0.5, pos[i].X - 0.5);
            double hue = t * speed * HueRate + ang * 60.0;   // same rate: closes under LoopSeconds
            buf[i] = ColorUtil.HsvToRgb(hue, 1.0, 0.6);
        }
    }
}

using UnifiedRgb.Core.Input;

namespace UnifiedRgb.Core.Effects;

/*-----------------------------------------------------------*\
| Reactive typing effects, fed by KeyboardTap (low-level      |
| hook, lazy lifecycle). On key-mapped keyboards (Strafe,     |
| Apex) they light the exact key; on everything else they     |
| degrade gracefully: fades become a whole-target pulse and   |
| ripples radiate from the center — so the fans can flash     |
| along with your typing too. Effects are stateless; all      |
| press state lives in KeyboardTap's ring.                    |
\*-----------------------------------------------------------*/

/// <summary>Keys light in the chosen color while held and fade out after
/// release. Speed = fade rate.</summary>
public sealed class KeyFade : IEffect
{
    public string Name => "Key Fade";
    public bool UsesBaseColor => true;

    [ThreadStatic] static KeyboardTap.KeyEvent[]? _ev;

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
        => Render(null!, 0, buf, pos, t, speed, baseColor);

    public void Render(IRgbDevice? device, int offset, Rgb[] buf, LedPos[] pos,
                       double t, double speed, Rgb baseColor)
    {
        KeyboardTap.Touch();
        var ev = _ev ??= new KeyboardTap.KeyEvent[64];
        int n = KeyboardTap.Snapshot(ev);
        double now = KeyboardTap.Now;
        double rate = 2.5 * Math.Clamp(speed, 0.1, 4);

        var dim = ColorUtil.Scale(baseColor, 0.06);          // resting glow
        for (int i = 0; i < buf.Length; i++) buf[i] = dim;

        if (device is IKeyMappedDevice km)
        {
            for (int e = 0; e < n; e++)
            {
                int led = km.LedForVk(ev[e].Vk) - offset;
                if (led < 0 || led >= buf.Length) continue;
                double level = Level(ev[e], now, rate);
                if (level <= 0.02) continue;
                var lit = ColorUtil.Scale(baseColor, 0.06 + 0.94 * level);
                if (lit.R > buf[led].R || lit.G > buf[led].G || lit.B > buf[led].B)
                    buf[led] = lit;
            }
        }
        else
        {
            // Unmapped device: the whole target glows with the newest press.
            double level = 0;
            for (int e = 0; e < n; e++) level = Math.Max(level, Level(ev[e], now, rate));
            var c = ColorUtil.Scale(baseColor, 0.06 + 0.94 * level);
            for (int i = 0; i < buf.Length; i++) buf[i] = c;
        }
    }

    static double Level(in KeyboardTap.KeyEvent e, double now, double rate)
        => e.Up < 0 ? 1.0 : Math.Exp(-(now - e.Up) * rate);
}

/// <summary>Every press spawns a colored ring that expands across the board
/// from the key itself (or from the center on unmapped devices).
/// Speed = ripple travel speed. Ring color follows the same source options
/// as the custom pattern: rainbow, the wheel color, or the target's palette
/// (one palette color per press). Per-target instances carry the settings;
/// the pill-list instance is identity only.</summary>
public sealed class KeyRipple : IEffect
{
    public string Name => "Key Ripple";
    public bool UsesBaseColor => Color == PatternColor.Solid;

    public PatternColor Color = PatternColor.Rainbow;
    public IList<Rgb>? Palette;

    const double MaxAge = 2.0;

    [ThreadStatic] static KeyboardTap.KeyEvent[]? _ev;

    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb baseColor)
        => Render(null!, 0, buf, pos, t, speed, baseColor);

    public void Render(IRgbDevice? device, int offset, Rgb[] buf, LedPos[] pos,
                       double t, double speed, Rgb baseColor)
    {
        KeyboardTap.Touch();
        var ev = _ev ??= new KeyboardTap.KeyEvent[64];
        int n = KeyboardTap.Snapshot(ev);
        double now = KeyboardTap.Now;
        double v0 = 0.65 * Math.Clamp(speed, 0.1, 4);        // ring speed, widths/sec
        double aspect = Math.Max(1.0, device?.PreviewAspect ?? 2.0);
        var km = device as IKeyMappedDevice;

        for (int i = 0; i < buf.Length; i++)
        {
            double cr = 0, cg = 0, cb = 0;
            for (int e = 0; e < n; e++)
            {
                double age = now - ev[e].Down;
                if (age < 0 || age > MaxAge) continue;

                float ox = 0.5f, oy = 0.5f;
                if (km != null)
                {
                    int led = km.LedForVk(ev[e].Vk) - offset;
                    if (led < 0 || led >= pos.Length) continue;
                    ox = pos[led].X; oy = pos[led].Y;
                }
                double dx = pos[i].X - ox;
                double dy = (pos[i].Y - oy) / aspect;        // y span is physically smaller
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double radius = age * v0;
                double ring = Math.Exp(-Math.Pow((dist - radius) / 0.045, 2));
                double life = 1.0 - age / MaxAge;
                double s = ring * life * life;
                if (s <= 0.01) continue;

                var col = RingColor(ev[e].Down, baseColor);
                cr += col.R * s; cg += col.G * s; cb += col.B * s;
            }
            buf[i] = new Rgb(
                (byte)Math.Min(255, (int)cr),
                (byte)Math.Min(255, (int)cg),
                (byte)Math.Min(255, (int)cb));
        }
    }

    /// <summary>Ring color for one press. Palette picks are keyed off the press
    /// timestamp so a ring keeps its color for its whole life.</summary>
    Rgb RingColor(double pressTime, Rgb baseColor)
    {
        if (Color == PatternColor.Solid) return baseColor;
        var pal = Palette;
        if (Color == PatternColor.Gradient && pal is { Count: > 0 })
            return pal[(int)(pressTime * 997.0) % pal.Count];
        return ColorUtil.HsvToRgb(pressTime * 79.0 % 360.0, 1.0, 1.0);
    }
}

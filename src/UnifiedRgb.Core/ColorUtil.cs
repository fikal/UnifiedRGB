namespace UnifiedRgb.Core;

public static class ColorUtil
{
    /// <summary>HSV (h in degrees, s/v in 0..1) to Rgb.</summary>
    public static Rgb HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = ((int)(h / 60)) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new Rgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    public static Rgb Scale(Rgb c, double factor)
    {
        factor = factor < 0 ? 0 : factor > 1 ? 1 : factor;
        return new Rgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));
    }

    /// <summary>Rgb to HSV (h in degrees 0..360, s/v in 0..1). Lets a base-color
    /// effect find the chosen hue and drift around it.</summary>
    public static (double H, double S, double V) RgbToHsv(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = 0;
        if (d > 1e-6)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max <= 1e-6 ? 0 : d / max, max);
    }
}

namespace UnifiedRgb.Core;

/// <summary>A single RGB color, 8 bits per channel.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Red   = new(255, 0, 0);
    public static readonly Rgb Green = new(0, 255, 0);
    public static readonly Rgb Blue  = new(0, 0, 255);

    /// <summary>Parse "RRGGBB" (optional leading #) without throwing — for
    /// live-typed input (hex boxes) that is empty/partial most keystrokes.</summary>
    public static bool TryFromHex(string? hex, out Rgb rgb)
    {
        rgb = Black;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 ||
            !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int v))
            return false;
        rgb = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>Parse "RRGGBB" (optional leading #); throws on invalid input.</summary>
    public static Rgb FromHex(string hex)
        => TryFromHex(hex, out var c) ? c
         : throw new FormatException($"'{hex}' is not an RRGGBB color");

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

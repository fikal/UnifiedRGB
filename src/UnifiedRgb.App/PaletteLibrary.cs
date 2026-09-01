using System.Text.RegularExpressions;
using UnifiedRgb.Core;

namespace UnifiedRgb.App;

/// <summary>A named palette shown in the library - either a built-in preset or a
/// user-saved one (Custom = removable).</summary>
public sealed record PaletteEntry(string Name, Rgb[] Colors, bool Custom);

/// <summary>The built-in palette collection (trending-style presets) plus the
/// helper that pulls hex colors out of pasted text - a coolors.co URL, a list
/// of hex codes, whatever. No network calls: presets are baked in, and import
/// just scrapes hex tokens from whatever the user pastes.</summary>
public static class PaletteLibrary
{
    static Rgb[] P(params string[] hex)
    {
        var list = new List<Rgb>(hex.Length);
        foreach (var h in hex) if (Rgb.TryFromHex(h, out var c)) list.Add(c);
        return list.ToArray();
    }

    /// <summary>Curated presets - vivid multi-color sets that read well on LEDs.</summary>
    public static readonly IReadOnlyList<PaletteEntry> Presets = new[]
    {
        new PaletteEntry("Sunset",       P("FF6B6B", "FF8E53", "FFC145", "FF6B9D"),                     false),
        new PaletteEntry("Miami",        P("F72585", "B5179E", "7209B7", "560BAD", "480CA8"),           false),
        new PaletteEntry("Ocean",        P("00B4D8", "0077B6", "023E7D", "002855"),                     false),
        new PaletteEntry("Neon",         P("08F7FE", "09FBD3", "FE53BB", "F5D300"),                     false),
        new PaletteEntry("Forest",       P("2D6A4F", "40916C", "52B788", "74C69D", "95D5B2"),           false),
        new PaletteEntry("Cotton Candy", P("FF99C8", "FCF6BD", "D0F4DE", "A9DEF9", "E4C1F9"),           false),
        new PaletteEntry("Inferno",      P("6A040F", "9D0208", "D00000", "E85D04", "FAA307", "FFBA08"), false),
        new PaletteEntry("Cyberpunk",    P("FF2A6D", "05D9E8", "005678", "D1F7FF"),                     false),
        new PaletteEntry("Retro",        P("264653", "2A9D8F", "E9C46A", "F4A261", "E76F51"),           false),
        new PaletteEntry("Pastel Dream", P("CDB4DB", "FFC8DD", "FFAFCC", "BDE0FE", "A2D2FF"),           false),
        new PaletteEntry("Vaporwave",    P("FF71CE", "01CDFE", "05FFA1", "B967FF", "FFFB96"),           false),
        new PaletteEntry("Rainbow",      P("FF0000", "FF7F00", "FFFF00", "00FF00", "0077FF", "4B0082", "9400D3"), false),
        new PaletteEntry("Autumn",       P("606C38", "283618", "DDA15E", "BC6C25"),                     false),
        new PaletteEntry("Berry",        P("800F2F", "A4133C", "C9184A", "FF4D6D", "FF8FA3"),           false),
        new PaletteEntry("Emerald",      P("D8F3DC", "95D5B2", "52B788", "2D6A4F", "1B4332"),           false),
        new PaletteEntry("Coral Reef",   P("FFCDB2", "FFB4A2", "E5989B", "B5838D", "6D6875"),           false),
        new PaletteEntry("Gold & Black", P("14213D", "FCA311", "E5E5E5", "000000"),                     false),
        new PaletteEntry("Ice",          P("CAF0F8", "90E0EF", "48CAE4", "00B4D8", "0096C7"),           false),
        new PaletteEntry("Lava Lamp",    P("F72585", "7209B7", "3A0CA3", "4361EE", "4CC9F0"),           false),
        new PaletteEntry("Mint Choc",    P("386641", "6A994E", "A7C957", "F2E8CF", "BC4749"),           false),
    };

    static readonly Regex HexToken = new("(?<![0-9a-fA-F])[0-9a-fA-F]{6}(?![0-9a-fA-F])", RegexOptions.Compiled);

    /// <summary>Pull every 6-digit hex color out of pasted text (a coolors.co
    /// URL like coolors.co/ffbe0b-fb5607-ff006e, a comma list, #-prefixed, ...).
    /// Order preserved, capped so a giant paste can't balloon the palette.</summary>
    public static Rgb[] ParseColors(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return System.Array.Empty<Rgb>();
        var list = new List<Rgb>();
        foreach (Match m in HexToken.Matches(text))
        {
            if (Rgb.TryFromHex(m.Value, out var c)) list.Add(c);
            if (list.Count >= 16) break;
        }
        return list.ToArray();
    }
}

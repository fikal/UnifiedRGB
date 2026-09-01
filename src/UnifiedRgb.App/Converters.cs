using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.App;

/// <summary>Converts a Core Rgb (used in swatch buttons) to a WPF brush.
/// Brushes are frozen and cached per color — swatch lists re-convert on every
/// refresh and unfrozen brushes each carry DP/dispatcher overhead.</summary>
public sealed class RgbToBrushConverter : System.Windows.Data.IValueConverter
{
    static readonly Dictionary<int, SolidColorBrush> _cache = new();

    internal static SolidColorBrush Cached(byte r, byte g, byte b)
    {
        int key = (r << 16) | (g << 8) | b;
        if (!_cache.TryGetValue(key, out var br))
        {
            if (_cache.Count > 4096) _cache.Clear();
            br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            _cache[key] = br;
        }
        return br;
    }

    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
        => value is Rgb rgb ? Cached(rgb.R, rgb.G, rgb.B) : Brushes.Transparent;
    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Hex string ("RRGGBB") to a WPF brush (frozen, cached).</summary>
public sealed class HexToBrushConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
    {
        var col = LcdController.ParseColor(value as string ?? "FFFFFF");
        return RgbToBrushConverter.Cached(col.R, col.G, col.B);
    }
    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Bool to FontWeight (Bold / Normal).</summary>
public sealed class BoolToWeightConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
        => value is true ? FontWeights.Bold : FontWeights.Normal;
    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Inverse of the built-in BooleanToVisibilityConverter.</summary>
public sealed class InverseBoolToVisConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
        => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c)
        => throw new NotSupportedException();
}

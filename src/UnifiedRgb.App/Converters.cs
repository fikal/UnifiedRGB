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

/// <summary>Above/below dropdown: true (at or above) is the first item.</summary>
public sealed class AboveIndexConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object v, Type t, object? p, System.Globalization.CultureInfo c)
        => v is bool b && !b ? 1 : 0;

    public object ConvertBack(object v, Type t, object? p, System.Globalization.CultureInfo c)
        => v is int i && i == 0;
}

/// <summary>Sensor id to its readable name ("CpuTemp" to "CPU temp").</summary>
public sealed class SensorSourceLabelConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object v, Type t, object? p, System.Globalization.CultureInfo c)
        => v is string s ? UnifiedRgb.Core.Automation.SensorSources.Label(s) : "";

    public object ConvertBack(object v, Type t, object? p, System.Globalization.CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>Unit hint for the readout the Slider template draws above its
/// track. Sliders carry very different meanings (a 0-1 fraction, a 0-255
/// channel, a 0.1-4 multiplier), so the value alone can't be formatted
/// sensibly; each slider says what it is and <see cref="SliderReadout"/>
/// renders it.</summary>
public static class Sl
{
    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.RegisterAttached("Unit", typeof(string), typeof(Sl), new PropertyMetadata(null));

    public static string? GetUnit(DependencyObject o) => (string?)o.GetValue(UnitProperty);
    public static void SetUnit(DependencyObject o, string? v) => o.SetValue(UnitProperty, v);
}

/// <summary>Formats a slider's value for display: (value, maximum, unit).</summary>
public sealed class SliderReadout : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] v, Type t, object? p, System.Globalization.CultureInfo c)
    {
        if (v.Length < 3 || v[0] is not double val || v[1] is not double max) return "";
        return (v[2] as string) switch
        {
            // A fraction of the slider's own range reads as a percentage; a
            // 0-255 channel does too, just scaled differently.
            "%" => $"{Math.Round(max <= 1.0 ? val * 100 : val / max * 100)}%",
            "x" => $"{val:0.0}×",
            "px" => $"{Math.Round(val)} px",
            _ => $"{Math.Round(val)}",
        };
    }

    public object[] ConvertBack(object v, Type[] t, object? p, System.Globalization.CultureInfo c)
        => throw new NotSupportedException();
}

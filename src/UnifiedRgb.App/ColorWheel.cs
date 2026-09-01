using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UnifiedRgb.App;

/// <summary>Hue/saturation color wheel (value comes from the Brightness
/// slider via the ValueLevel property). Click/drag to pick. Two-way
/// SelectedColor dependency property.</summary>
public sealed class ColorWheel : FrameworkElement
{
    WriteableBitmap? _bitmap;
    int _bmpSize;
    bool _dragging;

    public static readonly DependencyProperty SelectedColorProperty =
        DependencyProperty.Register(nameof(SelectedColor), typeof(Color), typeof(ColorWheel),
            new FrameworkPropertyMetadata(Colors.Magenta,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        int size = (int)Math.Min(ActualWidth, ActualHeight);
        if (size < 20) return;

        if (_bitmap == null || _bmpSize != size)
        {
            _bmpSize = size;
            _bitmap = RenderWheel(size);
        }

        double ox = (ActualWidth - size) / 2, oy = (ActualHeight - size) / 2;
        dc.DrawImage(_bitmap, new Rect(ox, oy, size, size));

        // Marker at current hue/sat.
        var (h, s, _) = RgbToHsv(SelectedColor);
        double radius = size / 2.0;
        double ang = h * Math.PI / 180.0;
        double mx = ox + radius + Math.Cos(ang) * s * (radius - 4);
        double my = oy + radius - Math.Sin(ang) * s * (radius - 4);
        dc.DrawEllipse(Brushes.Transparent, new Pen(Brushes.White, 2.5), new Point(mx, my), 8, 8);
        dc.DrawEllipse(Brushes.Transparent, new Pen(Brushes.Black, 1), new Point(mx, my), 9.5, 9.5);
    }

    static WriteableBitmap RenderWheel(int size)
    {
        var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        int stride = size * 4;
        var pixels = new byte[size * stride];
        double radius = size / 2.0;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x - radius, dy = radius - y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                int i = y * stride + x * 4;
                if (dist > radius) continue;   // transparent outside

                double sat = Math.Min(1.0, dist / (radius - 4));
                double hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                if (hue < 0) hue += 360;
                var c = HsvToRgb(hue, sat, 1.0);

                // simple edge antialias
                byte a = dist > radius - 1.5 ? (byte)(255 * (radius - dist) / 1.5) : (byte)255;
                pixels[i]     = c.B;
                pixels[i + 1] = c.G;
                pixels[i + 2] = c.R;
                pixels[i + 3] = a;
            }
        }
        bmp.WritePixels(new Int32Rect(0, 0, size, size), pixels, stride, 0);
        return bmp;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        Pick(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) Pick(e.GetPosition(this));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    void Pick(Point p)
    {
        int size = (int)Math.Min(ActualWidth, ActualHeight);
        if (size < 20) return;
        double ox = (ActualWidth - size) / 2, oy = (ActualHeight - size) / 2;
        double radius = size / 2.0;
        double dx = p.X - ox - radius, dy = radius - (p.Y - oy);
        double dist = Math.Sqrt(dx * dx + dy * dy);
        double sat = Math.Min(1.0, dist / (radius - 4));
        double hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (hue < 0) hue += 360;

        var (_, _, v) = RgbToHsv(SelectedColor);
        if (v <= 0.02) v = 1.0;                 // picking from black: assume full value
        SelectedColor = HsvToRgb(hue, sat, v);
    }

    public static Color HsvToRgb(double h, double s, double v)
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
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    public static (double H, double S, double V) RgbToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }
}

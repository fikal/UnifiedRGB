using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnifiedRgb.Core;

namespace UnifiedRgb.App;

/// <summary>Hue/saturation color wheel (value comes from the Brightness
/// slider via the ValueLevel property). Click/drag to pick. Two-way
/// SelectedColor dependency property.</summary>
public sealed class ColorWheel : FrameworkElement
{
    WriteableBitmap? _bitmap;
    int _bmpPx; double _bmpScale;
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

        // Render at DEVICE pixels: a DIP-sized bitmap drawn into a DIP-sized
        // rect is upscaled by WPF at 125-200 % DPI and reads soft. Same pattern
        // as AppRulesWindow.SnapshotOf.
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int px = Math.Max(20, (int)Math.Round(size * scale));
        if (_bitmap == null || _bmpPx != px || _bmpScale != scale)
        {
            _bmpPx = px; _bmpScale = scale;
            _bitmap = RenderWheel(px, 96.0 * scale);
        }

        double ox = (ActualWidth - size) / 2, oy = (ActualHeight - size) / 2;
        dc.DrawImage(_bitmap, new Rect(ox, oy, size, size));

        // Marker at current hue/sat.
        var (h, s, _) = RgbToHsv(SelectedColor);
        double radius = size / 2.0;
        double ang = h * Math.PI / 180.0;
        double mx = ox + radius + Math.Cos(ang) * s * (radius - 4);
        double my = oy + radius - Math.Sin(ang) * s * (radius - 4);
        dc.DrawEllipse(Brushes.Transparent, MarkerOuter, new Point(mx, my), 8, 8);
        dc.DrawEllipse(Brushes.Transparent, MarkerInner, new Point(mx, my), 9.5, 9.5);
    }

    static readonly Pen MarkerOuter = FrozenPen(Brushes.White, 2.5), MarkerInner = FrozenPen(Brushes.Black, 1);
    static Pen FrozenPen(Brush b, double w) { var p = new Pen(b, w); p.Freeze(); return p; }

    /// <summary>Alt+Tab / a popup mid-drag takes the capture away without a
    /// MouseUp; without this the next hover kept picking with no button down.</summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _bitmap = null;   // re-rasterize for the new monitor
        InvalidateVisual();
    }

    static WriteableBitmap RenderWheel(int size, double dpi)
    {
        var bmp = new WriteableBitmap(size, size, dpi, dpi, PixelFormats.Bgra32, null);
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
        // Left button only: a right/middle press used to pick too, and a pick
        // is a device write - while right-click means "clear" everywhere else.
        if (e.ChangedButton != MouseButton.Left) return;
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
        if (e.ChangedButton != MouseButton.Left) return;
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

    // Media.Color adapters over the one HSV implementation in Core (this file
    // used to carry a second copy of the math, with its own near-black guards).
    public static Color HsvToRgb(double h, double s, double v)
    {
        var c = ColorUtil.HsvToRgb(h, s, v);
        return Color.FromRgb(c.R, c.G, c.B);
    }

    public static (double H, double S, double V) RgbToHsv(Color c)
        => ColorUtil.RgbToHsv(new Rgb(c.R, c.G, c.B));
}

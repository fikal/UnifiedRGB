using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UnifiedRgb.App.Controls;

/// <summary>Circular temperature gauge: a 270° track with a colored arc
/// proportional to 0-100 °C, the reading in the middle, label beneath.
/// Temp = double.NaN renders as "—" with an empty arc.</summary>
public partial class TempGauge : UserControl
{
    public static readonly DependencyProperty TempProperty = DependencyProperty.Register(
        nameof(Temp), typeof(double), typeof(TempGauge),
        new PropertyMetadata(double.NaN, (d, _) => ((TempGauge)d).UpdateVisual()));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(TempGauge),
        new PropertyMetadata("", (d, _) => ((TempGauge)d).UpdateVisual()));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(TempGauge),
        new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x4C, 0xA8, 0xFF)),
            (d, _) => ((TempGauge)d).UpdateVisual()));

    public double Temp { get => (double)GetValue(TempProperty); set => SetValue(TempProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    // Gauge geometry: 270° sweep opening at the bottom (135° -> 405°).
    const double StartDeg = 135, SweepDeg = 270;

    public TempGauge()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateVisual();
        UpdateVisual();
    }

    double _shownTemp = double.MinValue;
    double _shownW, _shownH;

    void UpdateVisual()
    {
        // NaN != NaN, so an unavailable sensor used to "change" every cooling
        // tick and rebuild both arc geometries for nothing. Size changes must
        // still rebuild, so the shown size participates in the check.
        double t = Temp;
        bool same = t.Equals(_shownTemp)               // Equals treats NaN == NaN
                    && ActualWidth.Equals(_shownW) && ActualHeight.Equals(_shownH)
                    && LabelText.Text == Label && ReferenceEquals(Arc.Stroke, Accent)
                    && Track.Data != null;
        _shownTemp = t; _shownW = ActualWidth; _shownH = ActualHeight;
        if (same) return;

        LabelText.Text = Label;
        Arc.Stroke = Accent;

        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        if (double.IsNaN(w) || w <= 0 || double.IsNaN(h) || h <= 0) return;
        var center = new Point(w / 2, h / 2);
        double r = Math.Min(w, h) / 2 - 8;

        Track.Data = ArcGeometry(center, r, StartDeg, SweepDeg);

        if (double.IsNaN(Temp))
        {
            ValueText.Text = "—";
            Arc.Data = null;
            return;
        }
        ValueText.Text = $"{Temp:0}°";
        double frac = Math.Clamp(Temp / 100.0, 0, 1);
        Arc.Data = frac <= 0 ? null : ArcGeometry(center, r, StartDeg, SweepDeg * frac);
    }

    static Geometry ArcGeometry(Point center, double r, double startDeg, double sweepDeg)
    {
        sweepDeg = Math.Min(sweepDeg, 359.9);
        Point At(double deg)
        {
            double rad = deg * Math.PI / 180.0;
            return new Point(center.X + r * Math.Cos(rad), center.Y + r * Math.Sin(rad));
        }
        var fig = new PathFigure { StartPoint = At(startDeg), IsClosed = false };
        fig.Segments.Add(new ArcSegment(At(startDeg + sweepDeg), new Size(r, r), 0,
            sweepDeg > 180, SweepDirection.Clockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        return g;
    }
}

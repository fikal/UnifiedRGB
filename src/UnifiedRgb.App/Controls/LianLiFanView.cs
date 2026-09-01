using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UnifiedRgb.Core;

namespace UnifiedRgb.App.Controls;

/// <summary>Interactive Lian Li fan model drawn as its real light parts:
///   · center/inner hub - a ring of wedges
///   · outer ring       - segments along an octagon outlining the fan
///   · side glow        - the infinity-mirror strips (only if the fan has them)
/// Each segment shows its LIVE color; clicking a part selects it for editing.
/// Part counts are set per device via <see cref="SetParts"/>: the wireless
/// SL-INF is 8/20/16, the wired SL-Infinity is 8/12/0 (no separate side).
///
/// Part ids raised by Clicked (matching MainViewModel.SelectLianPart):
///   0 whole fan · 1 center/inner · 2 outer ring · 3 side glow.</summary>
public sealed class LianLiFanView : FrameworkElement
{
    public Func<(Rgb[] Colors, int SelectedPart)>? Source { get; set; }
    public event Action<int, int>? Clicked;
    public event Action<int>? LedRightClicked;

    readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    Rgb[] _colors = Array.Empty<Rgb>();
    int _selected = -1;

    // Part LED counts (settable per device).
    int _center = 8, _outer = 20, _side = 16;
    // When true the side strips are part of the OUTER part (the wired SL-Infinity
    // drives its ring + sides on ONE 12-LED group - L-Connect's SLInfinityOuter),
    // so the rectangles are drawn but MIRROR the outer colors and select as outer.
    bool _sideInOuter;
    // Side owns real LEDs only when it's a separate part (wireless). In sideInOuter
    // mode the rectangles are cosmetic, sampling the outer ring.
    bool SideOwnsLeds => _side > 0 && !_sideInOuter;
    int SideRectCount => _side > 0 ? 2 * Math.Max(1, _side / 2) : 0;
    int SegCount => _center + _outer + SideRectCount;         // drawn segments
    int BufferLen => _center + _outer + (SideOwnsLeds ? _side : 0);  // real LED colors

    SolidColorBrush[] _ledBrushes = Array.Empty<SolidColorBrush>();
    Geometry[]? _segs;
    Geometry? _octSelOuter, _octSelInner;
    StreamGeometry[]? _blades;
    Size _geoSize;

    static readonly Brush FrameBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x2E)));
    static readonly Brush BodyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1C)));
    static readonly Pen FramePen = FreezeP(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x48)), 1.5));
    static readonly Pen BladePen = FreezeP(new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x38)), 3));
    static readonly Pen SelectPen = FreezeP(new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x6F, 0xFF)), 2.5));
    static readonly Pen SegPen = FreezeP(new Pen(new SolidColorBrush(Color.FromRgb(0x0C, 0x0E, 0x14)), 1));

    static Brush Freeze(Brush b) { b.Freeze(); return b; }
    static Pen FreezeP(Pen p) { p.Freeze(); return p; }

    public LianLiFanView()
    {
        SetParts(8, 20, 16);
        _timer.Tick += (_, _) =>
        {
            if (Source == null || !IsVisible) return;
            (_colors, _selected) = Source();
            InvalidateVisual();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) _timer.Start(); else _timer.Stop(); };
        Cursor = Cursors.Hand;
    }

    /// <summary>Configure the fan's parts: center/inner wedge count, outer ring
    /// segment count, and side-strip LED count (0 = no side). Rebuilds geometry.</summary>
    public void SetParts(int center, int outer, int side, bool sideInOuter = false)
    {
        _center = Math.Max(1, center);
        _outer = Math.Max(1, outer);
        _side = Math.Max(0, side);
        _sideInOuter = sideInOuter;
        _ledBrushes = new SolidColorBrush[SegCount];        // one cached brush per drawn segment
        for (int i = 0; i < _ledBrushes.Length; i++) _ledBrushes[i] = new SolidColorBrush(Colors.Black);
        _colors = new Rgb[BufferLen];                       // real device LED colors
        _segs = null;   // force geometry rebuild
        InvalidateVisual();
    }

    const double RHub = 0.10, RCenterOut = 0.26;
    const double RBlade = 0.42;
    const double SideX = 0.55, SideHalfW = 0.055, SideSpan = 0.42;
    const double ROctIn = 0.72, ROctOut = 0.88;

    (double cx, double cy, double u) Metrics()
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        return (ActualWidth / 2, ActualHeight / 2, s / 2 * 0.96);
    }

    static Point OctPoint(double cx, double cy, double r, double t)
    {
        t -= Math.Floor(t);
        double seg = t * 8;
        int e = (int)seg;
        double f = seg - e;
        double a0 = (-112.5 + 45 * e) * Math.PI / 180;
        double a1 = a0 + 45 * Math.PI / 180;
        double x0 = cx + r * Math.Cos(a0), y0 = cy + r * Math.Sin(a0);
        double x1 = cx + r * Math.Cos(a1), y1 = cy + r * Math.Sin(a1);
        return new Point(x0 + (x1 - x0) * f, y0 + (y1 - y0) * f);
    }

    static StreamGeometry OctBand(double cx, double cy, double rIn, double rOut, double t0, double t1)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(OctPoint(cx, cy, rOut, t0), true, true);
            const int steps = 4;
            for (int s = 1; s <= steps; s++)
                c.LineTo(OctPoint(cx, cy, rOut, t0 + (t1 - t0) * s / steps), true, false);
            for (int s = steps; s >= 0; s--)
                c.LineTo(OctPoint(cx, cy, rIn, t0 + (t1 - t0) * s / steps), true, false);
        }
        g.Freeze();
        return g;
    }

    static StreamGeometry OctOutline(double cx, double cy, double r)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(OctPoint(cx, cy, r, 0), false, true);
            for (int s = 1; s <= 8; s++)
                c.LineTo(OctPoint(cx, cy, r, s / 8.0), true, false);
        }
        g.Freeze();
        return g;
    }

    static StreamGeometry Wedge(double cx, double cy, double u, int i, int count, double rIn, double rOut)
    {
        double a0 = -Math.PI / 2 + (i + 0.06) / (double)count * Math.PI * 2;
        double a1 = -Math.PI / 2 + (i + 0.94) / (double)count * Math.PI * 2;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            var pOutStart = new Point(cx + u * rOut * Math.Cos(a0), cy + u * rOut * Math.Sin(a0));
            var pOutEnd = new Point(cx + u * rOut * Math.Cos(a1), cy + u * rOut * Math.Sin(a1));
            var pInEnd = new Point(cx + u * rIn * Math.Cos(a1), cy + u * rIn * Math.Sin(a1));
            var pInStart = new Point(cx + u * rIn * Math.Cos(a0), cy + u * rIn * Math.Sin(a0));
            c.BeginFigure(pOutStart, true, true);
            c.ArcTo(pOutEnd, new Size(u * rOut, u * rOut), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(pInEnd, true, false);
            c.ArcTo(pInStart, new Size(u * rIn, u * rIn), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    Rect SideRect(double cx, double cy, double u, bool right)
    {
        double x = right ? cx + u * SideX : cx - u * SideX;
        return new Rect(x - u * SideHalfW, cy - u * SideSpan, u * SideHalfW * 2, u * SideSpan * 2);
    }

    void EnsureGeometry(double cx, double cy, double u)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (_segs != null && size == _geoSize) return;
        _geoSize = size;

        var segs = new List<Geometry>(SegCount);
        for (int i = 0; i < _center; i++) segs.Add(Wedge(cx, cy, u, i, _center, RHub, RCenterOut));
        for (int i = 0; i < _outer; i++)
            segs.Add(OctBand(cx, cy, u * ROctIn, u * ROctOut, (i + 0.04) / _outer, (i + 0.96) / _outer));
        if (_side > 0)
        {
            int perBar = Math.Max(1, _side / 2);
            foreach (bool right in new[] { false, true })
            {
                var bar = SideRect(cx, cy, u, right);
                double segH = bar.Height / perBar;
                for (int i = 0; i < perBar; i++)
                {
                    var r = new RectangleGeometry(
                        new Rect(bar.X, bar.Y + i * segH + 1.2, bar.Width, segH - 2.4), 2.5, 2.5);
                    r.Freeze();
                    segs.Add(r);
                }
            }
        }
        _segs = segs.ToArray();

        _octSelOuter = OctOutline(cx, cy, u * (ROctOut + 0.015));
        _octSelInner = OctOutline(cx, cy, u * (ROctIn - 0.015));

        var blades = new StreamGeometry[9];
        for (int i = 0; i < 9; i++)
        {
            double a0 = i / 9.0 * Math.PI * 2;
            var p0 = new Point(cx + u * RCenterOut * Math.Cos(a0), cy + u * RCenterOut * Math.Sin(a0));
            double a1 = a0 + 0.85;
            var p1 = new Point(cx + u * RBlade * Math.Cos(a1), cy + u * RBlade * Math.Sin(a1));
            var mid = new Point(cx + u * (RBlade - 0.05) * Math.Cos(a0 + 0.45), cy + u * (RBlade - 0.05) * Math.Sin(a0 + 0.45));
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(p0, false, false); c.QuadraticBezierTo(mid, p1, true, false); }
            g.Freeze();
            blades[i] = g;
        }
        _blades = blades;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var (cx, cy, u) = Metrics();
        if (u < 20) return;
        EnsureGeometry(cx, cy, u);

        var frameRect = new Rect(cx - u, cy - u, 2 * u, 2 * u);
        dc.DrawRoundedRectangle(FrameBrush, FramePen, frameRect, u * 0.14, u * 0.14);
        dc.DrawEllipse(BodyBrush, null, new Point(cx, cy), u * (RBlade + 0.03), u * (RBlade + 0.03));
        foreach (var b in _blades!) dc.DrawGeometry(null, BladePen, b);

        for (int i = 0; i < _segs!.Length; i++) dc.DrawGeometry(BrushFor(i), SegPen, _segs[i]);
        dc.DrawEllipse(BodyBrush, SegPen, new Point(cx, cy), u * RHub, u * RHub);

        switch (_selected)
        {
            case 0: dc.DrawRoundedRectangle(null, SelectPen, frameRect, u * 0.14, u * 0.14); break;
            case 1: dc.DrawEllipse(null, SelectPen, new Point(cx, cy), u * RCenterOut, u * RCenterOut); break;
            case 2:
                dc.DrawGeometry(null, SelectPen, _octSelOuter!);
                dc.DrawGeometry(null, SelectPen, _octSelInner!);
                if (_sideInOuter && _side > 0)   // wired: sides belong to outer
                {
                    dc.DrawRoundedRectangle(null, SelectPen, Inflate(SideRect(cx, cy, u, false), 3), 4, 4);
                    dc.DrawRoundedRectangle(null, SelectPen, Inflate(SideRect(cx, cy, u, true), 3), 4, 4);
                }
                break;
            case 3 when _side > 0 && !_sideInOuter:
                dc.DrawRoundedRectangle(null, SelectPen, Inflate(SideRect(cx, cy, u, false), 3), 4, 4);
                dc.DrawRoundedRectangle(null, SelectPen, Inflate(SideRect(cx, cy, u, true), 3), 4, 4);
                break;
        }
    }

    static Rect Inflate(Rect r, double d) => new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    // Map a drawn-segment index to the real LED color that feeds it. Center and
    // outer segments read straight through; side rectangles either index the side
    // LED range (wireless) or MIRROR the outer ring (wired sideInOuter).
    int ColorSrc(int seg)
    {
        int baseOuter = _center + _outer;
        if (seg < baseOuter) return seg;
        int sr = seg - baseOuter;
        if (SideOwnsLeds) return baseOuter + Math.Min(sr, _side - 1);
        return _center + (SideRectCount > 0 ? sr * _outer / SideRectCount : 0);   // sample outer
    }

    Brush BrushFor(int seg)
    {
        int src = ColorSrc(seg);
        var c = src < _colors.Length ? _colors[src] : default;
        const byte floor = 22;
        var want = Color.FromRgb(Math.Max(c.R, floor), Math.Max(c.G, floor), Math.Max(c.B, floor));
        var b = _ledBrushes[seg];
        if (b.Color != want) b.Color = want;
        return b;
    }

    int LedAt(Point p)
    {
        if (_segs == null) return -1;
        for (int i = 0; i < _segs.Length; i++)
            if (_segs[i].FillContains(p, 2, ToleranceType.Absolute)) return i;
        return -1;
    }

    int PartOfLed(int led) => led < _center ? 1 : led < _center + _outer ? 2 : _sideInOuter ? 2 : 3;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var (cx, cy, u) = Metrics();
        var p = e.GetPosition(this);
        double dx = p.X - cx, dy = p.Y - cy;
        double r = Math.Sqrt(dx * dx + dy * dy) / u;

        int led = LedAt(p);
        int part;
        if (led >= 0) part = PartOfLed(led);
        else if (_side > 0 && (Inflate(SideRect(cx, cy, u, false), 4).Contains(p)
            || Inflate(SideRect(cx, cy, u, true), 4).Contains(p))) part = _sideInOuter ? 2 : 3;
        else if (r <= RCenterOut + 0.03) part = 1;
        else if (r >= ROctIn - 0.06 && r <= ROctOut + 0.06) part = 2;
        else if (Math.Abs(dx) <= u && Math.Abs(dy) <= u) part = 0;
        else return;
        Clicked?.Invoke(part, led);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        int seg = LedAt(e.GetPosition(this));
        // Only real LEDs are per-LED editable; skip cosmetic (mirrored) side rects.
        bool real = seg >= 0 && (seg < _center + _outer || SideOwnsLeds);
        if (real) { LedRightClicked?.Invoke(seg); e.Handled = true; }
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters p)
        => new PointHitTestResult(this, p.HitPoint);
}

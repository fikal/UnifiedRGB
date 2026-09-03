using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UnifiedRgb.Core.Sensors;

namespace UnifiedRgb.App.Controls;

/// <summary>Draggable temperature -> fan-duty curve editor. X axis is 0-100 °C,
/// Y axis is the duty floor..100 %. Drag a point to reshape; double-click the
/// plot to add a point, right-click a point to remove it. Raises CurveChanged
/// once per edit (a drag reports at release). A live marker shows the current
/// (temp, duty).</summary>
public partial class FanCurveEditor : UserControl
{
    const double MinX = 0, MaxX = 100;
    double _floor = SensorHub.MinDutyPct;
    double MinY => _floor;   // duty floor (0 for fan-stop-capable fans)
    const double MaxY = 100;

    /// <summary>Lowest allowed duty for the edited fan (0 = fan-stop capable).</summary>
    public void SetFloor(int floor) { _floor = floor; Rebuild(); }
    const double Pad = 34;      // left/bottom gutter for axis labels
    const double PadTR = 10;    // top/right gutter

    public event Action<FanCurve>? CurveChanged;

    FanCurve? _curve;
    double? _liveTemp;
    int? _liveDuty;
    Ellipse? _marker;
    Polyline? _line;    // kept so a drag can move points IN PLACE instead of
    Polygon? _fill;     // clearing + rebuilding ~25 elements per mouse-move
    int _dragIndex = -1;
    bool _dragMoved;    // a press that never moved the point is not an edit

    public FanCurveEditor() => InitializeComponent();

    public void SetCurve(FanCurve curve) { _curve = curve; Rebuild(); }

    public void SetLive(double? temp, int? duty)
    {
        _liveTemp = temp; _liveDuty = duty;
        PositionMarker();
    }

    /*--------------------- coordinate mapping ---------------------*/
    double PlotW => Math.Max(1, Plot.ActualWidth - Pad - PadTR);
    double PlotH => Math.Max(1, Plot.ActualHeight - Pad - PadTR);
    double Xpx(double tempC) => Pad + (tempC - MinX) / (MaxX - MinX) * PlotW;
    double Ypx(double duty) => PadTR + (1 - (duty - MinY) / (MaxY - MinY)) * PlotH;
    double TempFromPx(double x) => MinX + Math.Clamp((x - Pad) / PlotW, 0, 1) * (MaxX - MinX);
    double DutyFromPx(double y) => MinY + Math.Clamp(1 - (y - PadTR) / PlotH, 0, 1) * (MaxY - MinY);

    void Plot_SizeChanged(object sender, SizeChangedEventArgs e) => Rebuild();

    void Rebuild()
    {
        Plot.Children.Clear();
        _marker = null; _line = null; _fill = null;
        if (_curve == null || Plot.ActualWidth < 20 || Plot.ActualHeight < 20) return;

        DrawGrid();

        // Filled area under the curve + the line itself.
        var pts = _curve.Points;
        if (pts.Count > 0)
        {
            var line = _line = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x5C, 0x84, 0xFF)),
                StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round,
            };
            var fill = _fill = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x5C, 0x84, 0xFF)) };
            // Extend flat to both edges so the curve reads full-width.
            line.Points.Add(new Point(Xpx(MinX), Ypx(pts[0].DutyPct)));
            fill.Points.Add(new Point(Xpx(MinX), Ypx(MinY)));
            fill.Points.Add(new Point(Xpx(MinX), Ypx(pts[0].DutyPct)));
            foreach (var p in pts)
            {
                var pt = new Point(Xpx(p.TempC), Ypx(p.DutyPct));
                line.Points.Add(pt); fill.Points.Add(pt);
            }
            line.Points.Add(new Point(Xpx(MaxX), Ypx(pts[^1].DutyPct)));
            fill.Points.Add(new Point(Xpx(MaxX), Ypx(pts[^1].DutyPct)));
            fill.Points.Add(new Point(Xpx(MaxX), Ypx(MinY)));
            Plot.Children.Add(fill);
            Plot.Children.Add(line);

            for (int i = 0; i < pts.Count; i++) AddHandle(i, pts[i]);
        }

        AddMarker();
        PositionMarker();
    }

    void DrawGrid()
    {
        var grid = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x38));
        var axisText = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0xA2));
        // Vertical temp lines every 20 °C.
        for (int t = 0; t <= 100; t += 20)
        {
            double x = Xpx(t);
            Plot.Children.Add(new Line { X1 = x, Y1 = Ypx(MaxY), X2 = x, Y2 = Ypx(MinY), Stroke = grid, StrokeThickness = 1 });
            var lbl = new TextBlock { Text = $"{t}°", Foreground = axisText, FontSize = 10 };
            Canvas.SetLeft(lbl, x - 8); Canvas.SetTop(lbl, Ypx(MinY) + 4);
            Plot.Children.Add(lbl);
        }
        // Horizontal duty lines every 20 %.
        for (int d = (int)MinY; d <= 100; d += 20)
        {
            double y = Ypx(d);
            Plot.Children.Add(new Line { X1 = Xpx(MinX), Y1 = y, X2 = Xpx(MaxX), Y2 = y, Stroke = grid, StrokeThickness = 1 });
            var lbl = new TextBlock { Text = $"{d}%", Foreground = axisText, FontSize = 10 };
            Canvas.SetLeft(lbl, 4); Canvas.SetTop(lbl, y - 8);
            Plot.Children.Add(lbl);
        }
    }

    void AddHandle(int index, CurvePoint p)
    {
        var dot = new Ellipse
        {
            Width = 13, Height = 13,
            Fill = new SolidColorBrush(Color.FromRgb(0xE9, 0xEA, 0xF0)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x5C, 0x84, 0xFF)),
            StrokeThickness = 2.5,
            Cursor = Cursors.SizeAll,
            Tag = index,
            ToolTip = $"{p.TempC}°C → {p.DutyPct}%",
        };
        Canvas.SetLeft(dot, Xpx(p.TempC) - 6.5);
        Canvas.SetTop(dot, Ypx(p.DutyPct) - 6.5);
        dot.MouseLeftButtonDown += Handle_MouseDown;
        dot.MouseMove += Handle_MouseMove;
        dot.MouseLeftButtonUp += Handle_MouseUp;
        dot.LostMouseCapture += Handle_LostCapture;
        dot.MouseRightButtonUp += Handle_RightClick;
        Plot.Children.Add(dot);
    }

    void AddMarker()
    {
        _marker = new Ellipse
        {
            Width = 9, Height = 9,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x4C)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Plot.Children.Add(_marker);
    }

    void PositionMarker()
    {
        if (_marker == null || _curve == null) return;
        if (_liveTemp is double t)
        {
            int duty = _liveDuty ?? _curve.DutyAt(t);
            Canvas.SetLeft(_marker, Xpx(Math.Clamp(t, MinX, MaxX)) - 4.5);
            Canvas.SetTop(_marker, Ypx(Math.Clamp(duty, MinY, MaxY)) - 4.5);
            _marker.Visibility = Visibility.Visible;
        }
        else _marker.Visibility = Visibility.Collapsed;
    }

    /*--------------------------- editing ---------------------------*/
    void Handle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse dot && dot.Tag is int i)
        {
            _dragIndex = i;
            _dragMoved = false;
            dot.CaptureMouse();
            e.Handled = true;
        }
    }

    void Handle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragIndex < 0 || _curve == null || sender is not Ellipse dot) return;
        var pos = e.GetPosition(Plot);
        int temp = (int)Math.Round(TempFromPx(pos.X));
        int duty = (int)Math.Round(DutyFromPx(pos.Y));
        // Keep points ordered: clamp temp strictly between neighbors.
        int lo = _dragIndex > 0 ? _curve.Points[_dragIndex - 1].TempC + 1 : (int)MinX;
        int hi = _dragIndex < _curve.Points.Count - 1 ? _curve.Points[_dragIndex + 1].TempC - 1 : (int)MaxX;
        temp = Math.Clamp(temp, lo, hi);
        duty = Math.Clamp(duty, (int)MinY, (int)MaxY);
        // Sub-pixel jitter on a click (or the MouseMove WPF synthesizes on
        // capture) lands on the same point: not an edit, so no model push at
        // release for a no-op.
        var cur = _curve.Points[_dragIndex];
        if (cur.TempC == temp && cur.DutyPct == duty) return;
        _curve.Points[_dragIndex] = new CurvePoint(temp, duty);
        _curve.Preset = "Custom";
        dot.ToolTip = $"{temp}°C → {duty}%";
        // In-place update: move ONLY the dragged handle + the affected line/
        // fill vertices. The old full Rebuild() per mouse-move recreated ~25
        // elements (and deleted the very Ellipse holding mouse capture —
        // dragging worked by the accident that capture survives detachment).
        UpdateDragVisuals(dot, _dragIndex, temp, duty);
        // The model push (a hardware duty write + a fan-config.json serialize
        // and replace, synchronous on the UI thread) happens once, at release
        // - it used to run on every mouse-move of the drag. The live marker
        // follows the local curve meanwhile, so the feedback is unchanged.
        _dragMoved = true;
    }

    void UpdateDragVisuals(Ellipse dot, int index, int temp, int duty)
    {
        var pt = new Point(Xpx(temp), Ypx(duty));
        Canvas.SetLeft(dot, pt.X - 6.5);
        Canvas.SetTop(dot, pt.Y - 6.5);
        int n = _curve!.Points.Count;
        if (_line != null && _line.Points.Count == n + 2)
        {
            _line.Points[1 + index] = pt;
            if (index == 0) _line.Points[0] = new Point(Xpx(MinX), pt.Y);          // leading flat edge
            if (index == n - 1) _line.Points[n + 1] = new Point(Xpx(MaxX), pt.Y);  // trailing flat edge
        }
        if (_fill != null && _fill.Points.Count == n + 4)
        {
            _fill.Points[2 + index] = pt;
            if (index == 0) _fill.Points[1] = new Point(Xpx(MinX), pt.Y);
            if (index == n - 1) _fill.Points[n + 2] = new Point(Xpx(MaxX), pt.Y);
        }
    }

    void Handle_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse dot) dot.ReleaseMouseCapture();   // -> Handle_LostCapture ends the drag
        bool wasDragging = _dragIndex >= 0;
        _dragIndex = -1;
        if (wasDragging) Rebuild();   // one clean rebuild at release
    }

    /// <summary>Alt+Tab / a popup mid-drag takes the capture without a MouseUp;
    /// without this the next hover kept dragging the handle with no button down.
    /// The normal release path runs through here too (ReleaseMouseCapture fires
    /// it), so this is the one place a finished drag reaches the model.</summary>
    void Handle_LostCapture(object sender, MouseEventArgs e)
    {
        if (_dragIndex < 0) return;
        _dragIndex = -1;
        Rebuild();
        if (_dragMoved && _curve != null)
        {
            _dragMoved = false;
            CurveChanged?.Invoke(_curve);
        }
    }

    void Handle_RightClick(object sender, MouseButtonEventArgs e)
    {
        // Remove a point (keep at least two so a curve remains).
        if (_curve == null || _curve.Points.Count <= 2 || sender is not Ellipse dot || dot.Tag is not int i) return;
        _curve.Points.RemoveAt(i);
        _curve.Preset = "Custom";
        Rebuild();
        CurveChanged?.Invoke(_curve);
        e.Handled = true;
    }

    void Plot_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click empty plot to insert a point there.
        if (e.ClickCount != 2 || _curve == null) return;
        var pos = e.GetPosition(Plot);
        int temp = (int)Math.Round(TempFromPx(pos.X));
        int duty = (int)Math.Round(DutyFromPx(pos.Y));
        temp = Math.Clamp(temp, (int)MinX, (int)MaxX);
        duty = Math.Clamp(duty, (int)MinY, (int)MaxY);
        int at = _curve.Points.FindIndex(p => p.TempC > temp);
        if (at < 0) at = _curve.Points.Count;
        if (_curve.Points.Any(p => Math.Abs(p.TempC - temp) < 2)) return;   // too close
        _curve.Points.Insert(at, new CurvePoint(temp, duty));
        _curve.Preset = "Custom";
        Rebuild();
        CurveChanged?.Invoke(_curve);
    }
}

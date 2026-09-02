using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using UnifiedRgb.Core;

namespace UnifiedRgb.App;

public enum PreviewStyle { Dots, Keys, Fan }

/// <summary>Live neon preview of the selected target on ANY device.
/// Styles: Keys = rounded keycap tiles in the device's real layout (keyboards),
/// Fan = neon disc with orbs on the ring, Dots = glowing orbs at LED positions.
/// Content is fitted aspect-correct inside the card. Clicking an LED raises
/// <see cref="LedClicked"/> (index within the target).
///
/// Perf model (this control used to be the app's #1 UI cost — ~19k allocations
/// per second): a 30 Hz DispatcherTimer gated on visibility (NOT the 60+ Hz
/// CompositionTarget.Rendering static event), a redraw only when the sampled
/// colors actually changed, frozen brushes cached per color, and the keycap
/// layout computed once per (positions, size) instead of per frame.</summary>
public sealed class LedPreview : FrameworkElement
{
    /// <summary>True while the window is mid-drag/resize; freezes the preview.</summary>
    public static bool GlobalPause;

    public Func<(Rgb[] Colors, LedPos[] Pos, PreviewStyle Style, double Aspect, LedRect[]? Rects)>? Source { get; set; }
    public event Action<int>? LedClicked;
    public event Action<int>? LedRightClicked;

    Rgb[] _colors = Array.Empty<Rgb>();
    LedPos[] _pos = Array.Empty<LedPos>();
    PreviewStyle _style;
    double _aspect = 1.6;
    LedRect[]? _rects;
    readonly List<Rect> _hitRects = new();
    readonly List<Point> _pts = new();
    double _hitR = 12;

    Rgb[] _shown = Array.Empty<Rgb>();     // colors of the last frame drawn
    readonly DispatcherTimer _timer;

    readonly Brush _card;
    readonly Pen _cardEdge;
    readonly Brush _disc;
    readonly Pen _discEdge;
    readonly Pen _ringPen;
    readonly Pen _hubEdge;
    readonly Brush _hub;
    static readonly Brush CapHighlight = Frozen(Color.FromArgb(42, 255, 255, 255));
    static readonly Brush OrbSpecular = Frozen(Color.FromArgb(130, 255, 255, 255));

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public LedPreview()
    {
        _card = Frozen(Color.FromRgb(0x0C, 0x0E, 0x14));
        _cardEdge = new Pen(Frozen(Color.FromArgb(28, 140, 155, 210)), 1);
        _cardEdge.Freeze();

        _disc = new RadialGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromRgb(0x1a, 0x1c, 0x26), 0),
                new(Color.FromRgb(0x0c, 0x0d, 0x14), 0.7),
                new(Color.FromRgb(0x07, 0x08, 0x0e), 1),
            })
        { GradientOrigin = new Point(0.5, 0.5), Center = new Point(0.5, 0.5), RadiusX = 0.6, RadiusY = 0.6 };
        _disc.Freeze();
        _discEdge = new Pen(Frozen(Color.FromArgb(40, 120, 140, 200)), 1.5);
        _discEdge.Freeze();
        _ringPen = new Pen(Frozen(Color.FromArgb(24, 255, 255, 255)), 1);
        _ringPen.Freeze();
        _hub = new RadialGradientBrush(Color.FromRgb(0x20, 0x22, 0x2c), Color.FromRgb(0x0a, 0x0b, 0x12));
        _hub.Freeze();
        _hubEdge = new Pen(Frozen(Color.FromArgb(50, 200, 210, 255)), 1);
        _hubEdge.Freeze();

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _timer.Start(); Refresh(); }
            else _timer.Stop();
        };
    }

    void Refresh()
    {
        // GlobalPause: set while the window is being dragged/resized —
        // per-frame invalidation during a move starves the move loop and
        // makes the window judder. Lighting itself keeps running.
        if (GlobalPause || Source is null || !IsVisible) return;
        var (oldPos, oldStyle, oldAspect, oldRects) = (_pos, _style, _aspect, _rects);
        (_colors, _pos, _style, _aspect, _rects) = Source();

        // Redraw only when the sampled colors changed — a static color or a
        // slow effect no longer costs a full render pass per tick. The layout
        // counts too: a target switch with identical colors but different
        // geometry (two static-white 8-LED zones) kept drawing - and hit-
        // testing - the old shape until a color changed. Content compare: the
        // VM hands out fresh arrays per pull, so references never match.
        int n = _colors.Length;
        bool same = _shown.Length == n && _style == oldStyle && _aspect == oldAspect
                    && SameSeq(_pos, oldPos) && SameSeq(_rects, oldRects);
        if (same)
            for (int i = 0; i < n; i++)
                if (_shown[i] != _colors[i]) { same = false; break; }
        if (same) return;

        if (_shown.Length != n) _shown = new Rgb[n];
        Array.Copy(_colors, _shown, n);
        InvalidateVisual();
    }

    static bool SameSeq<T>(T[]? a, T[]? b) where T : struct, IEquatable<T>
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }

    /*-----------------------------------------------------*\
    | Frozen-brush cache, shared by every preview instance. |
    | Effects repeat a limited set of colors per frame, so  |
    | caching per color removes the per-LED-per-frame brush |
    | + gradient churn entirely. Cleared if a long rainbow  |
    | session ever piles up too many distinct entries.      |
    \*-----------------------------------------------------*/
    sealed class ColorKit
    {
        public SolidColorBrush? Fill;
        public RadialGradientBrush? Soft;   // wide under-glow (keys)
        public RadialGradientBrush? Orb;    // bright orb glow (dots/fan)
    }
    static readonly Dictionary<int, ColorKit> _kits = new();

    static ColorKit KitFor(Rgb c)
    {
        int key = (c.R << 16) | (c.G << 8) | c.B;
        if (!_kits.TryGetValue(key, out var kit))
        {
            if (_kits.Count > 4096) _kits.Clear();   // pathological rainbow accumulation
            _kits[key] = kit = new ColorKit();
        }
        return kit;
    }

    static SolidColorBrush FillFor(Rgb c)
    {
        var kit = KitFor(c);
        if (kit.Fill == null) { kit.Fill = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)); kit.Fill.Freeze(); }
        return kit.Fill;
    }

    static RadialGradientBrush SoftGlowFor(Rgb c)
    {
        var kit = KitFor(c);
        if (kit.Soft == null)
        {
            kit.Soft = new RadialGradientBrush(Color.FromArgb(60, c.R, c.G, c.B), Colors.Transparent);
            kit.Soft.Freeze();
        }
        return kit.Soft;
    }

    static RadialGradientBrush OrbGlowFor(Rgb c)
    {
        var kit = KitFor(c);
        if (kit.Orb == null)
        {
            kit.Orb = new RadialGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(195, c.R, c.G, c.B), 0),
                    new(Color.FromArgb(75, c.R, c.G, c.B), 0.45),
                    new(Color.FromArgb(0, c.R, c.G, c.B), 1),
                });
            kit.Orb.Freeze();
        }
        return kit.Orb;
    }

    int HitTest(Point p)
    {
        // Exact-footprint hit test first (keycaps of varying widths).
        for (int i = 0; i < _hitRects.Count; i++)
        {
            var r = _hitRects[i];
            if (r.Width > 0 && r.Contains(p)) return i;
        }

        int best = -1; double bestD = double.MaxValue;
        for (int i = 0; i < _pts.Count; i++)
        {
            double d = (p - _pts[i]).Length;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best >= 0 && bestD <= Math.Max(_hitR, 14) ? best : -1;
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        int i = HitTest(e.GetPosition(this));
        if (i >= 0) { LedClicked?.Invoke(i); e.Handled = true; }
    }

    protected override void OnMouseRightButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        int i = HitTest(e.GetPosition(this));
        if (i >= 0) { LedRightClicked?.Invoke(i); e.Handled = true; }
    }

    /// <summary>Largest rect of the given aspect centered inside the card.</summary>
    static Rect FitRect(double w, double h, double aspect, double pad)
    {
        double aw = w - pad * 2, ah = h - pad * 2;
        double cw = aw, ch = cw / aspect;
        if (ch > ah) { ch = ah; cw = ch * aspect; }
        return new Rect((w - cw) / 2, (h - ch) / 2, cw, ch);
    }

    /*-----------------------------------------------------*\
    | Geometry cache: hit rects / centers / cap sizes are a |
    | pure function of (positions, style, area) — compute   |
    | them once and reuse until the target or size changes. |
    \*-----------------------------------------------------*/
    long _geoKey;

    static long GeoHash(LedPos[] pos, LedRect[]? rects, PreviewStyle style, double w, double h)
    {
        unchecked
        {
            long hsh = 1469598103934665603;
            void Mix(long v) { hsh = (hsh ^ v) * 1099511628211; }
            Mix(pos.Length); Mix((int)style);
            Mix(BitConverter.DoubleToInt64Bits(Math.Round(w, 1)));
            Mix(BitConverter.DoubleToInt64Bits(Math.Round(h, 1)));
            for (int i = 0; i < pos.Length; i++)
            { Mix(BitConverter.SingleToInt32Bits(pos[i].X)); Mix(BitConverter.SingleToInt32Bits(pos[i].Y)); }
            if (rects != null)
                for (int i = 0; i < rects.Length; i++)
                { Mix(BitConverter.SingleToInt32Bits(rects[i].X)); Mix(BitConverter.SingleToInt32Bits(rects[i].W)); }
            return hsh;
        }
    }

    Rect[] _capRects = Array.Empty<Rect>();
    double _capH;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 10 || h <= 10) return;
        int n = Math.Min(_colors.Length, _pos.Length);

        long key = GeoHash(_pos, _rects, _style, w, h);
        bool rebuild = key != _geoKey;
        _geoKey = key;
        if (rebuild) { _pts.Clear(); _hitRects.Clear(); }

        if (_style == PreviewStyle.Fan) { RenderFan(dc, w, h, n, rebuild); return; }

        dc.DrawRoundedRectangle(_card, _cardEdge, new Rect(0, 0, w, h), 14, 14);
        if (n == 0) return;

        var area = FitRect(w, h, Math.Max(0.2, _aspect), 34);

        if (_style == PreviewStyle.Keys && _rects is { } geo && geo.Length >= n) RenderExact(dc, area, n, geo, rebuild);
        else if (_style == PreviewStyle.Keys) RenderKeys(dc, area, n, rebuild);
        else RenderDots(dc, area, n, rebuild);
    }

    /*-----------------------------------------------------*\
    | Exact device geometry: caps drawn at their authored   |
    | positions and sizes (wide space/shift, logo bars...). |
    \*-----------------------------------------------------*/
    void RenderExact(DrawingContext dc, Rect area, int n, LedRect[] geo, bool rebuild)
    {
        if (rebuild || _hitRects.Count < n)
        {
            _pts.Clear(); _hitRects.Clear();
            for (int i = 0; i < n; i++)
            {
                if (geo[i].W <= 0) { _hitRects.Add(Rect.Empty); _pts.Add(new Point(-999, -999)); continue; }
                double kw = geo[i].W * area.Width, kh = geo[i].H * area.Height;
                double cx = area.X + geo[i].X * area.Width, cy = area.Y + geo[i].Y * area.Height;
                // Small inset gives keycap seams like the real board.
                double inset = Math.Min(kw, kh) * 0.10;
                var r = new Rect(cx - kw / 2 + inset / 2, cy - kh / 2 + inset / 2,
                                 Math.Max(1, kw - inset), Math.Max(1, kh - inset));
                _hitRects.Add(r);
                _pts.Add(new Point(cx, cy));
            }
            _hitR = 0;   // hit-testing uses the rects
        }

        for (int i = 0; i < n; i++)
        {
            if (_hitRects[i].Width <= 0) continue;
            dc.DrawEllipse(SoftGlowFor(_colors[i]), null, _pts[i], _hitRects[i].Width * 0.85, _hitRects[i].Height * 1.1);
        }
        for (int i = 0; i < n; i++)
        {
            var r = _hitRects[i];
            if (r.Width <= 0) continue;
            dc.DrawRoundedRectangle(FillFor(_colors[i]), null, r, 3.5, 3.5);
            var hl = new Rect(r.X + 1.5, r.Y + 1.5, Math.Max(0, r.Width - 3), Math.Max(1, r.Height * 0.22));
            dc.DrawRoundedRectangle(CapHighlight, null, hl, 3, 3);
        }
    }

    /*-----------------------------------------------------*\
    | Keys: keycap tiles. The layout grid pads narrow rows  |
    | with holes (wide keys own 1 cell + padding), so each  |
    | cap's width is derived from the distance to its row   |
    | neighbors — rows render as continuous strips with     |
    | naturally wide space/shift/enter, like a real board.  |
    \*-----------------------------------------------------*/
    void RenderKeys(DrawingContext dc, Rect area, int n, bool rebuild)
    {
        if (rebuild || _capRects.Length < n)
        {
            var ys = new HashSet<int>();
            for (int i = 0; i < n; i++) ys.Add((int)Math.Round(_pos[i].Y * 1000));
            int rows = Math.Clamp(ys.Count, 2, 12);
            double cellH = area.Height / (rows - 1);
            _capH = cellH * 0.80;

            // Group keys into rows, sorted left-to-right.
            var byRow = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int rk = (int)Math.Round(_pos[i].Y * 1000);
                if (!byRow.TryGetValue(rk, out var list)) byRow[rk] = list = new List<int>();
                list.Add(i);
            }

            // The base column step = smallest common neighbor gap.
            double step = double.MaxValue;
            foreach (var list in byRow.Values)
            {
                list.Sort((a, b) => _pos[a].X.CompareTo(_pos[b].X));
                for (int k = 1; k < list.Count; k++)
                {
                    double d = (_pos[list[k]].X - _pos[list[k - 1]].X) * area.Width;
                    if (d > 0.5) step = Math.Min(step, d);
                }
            }
            if (step == double.MaxValue) step = area.Width / 22;

            _capRects = new Rect[n];
            foreach (var list in byRow.Values)
            {
                for (int k = 0; k < list.Count; k++)
                {
                    int i = list[k];
                    double x = area.X + _pos[i].X * area.Width;
                    double y = area.Y + _pos[i].Y * area.Height;
                    // Extend halfway toward each neighbor (capped) so padding holes
                    // become key width instead of gaps.
                    double left = k > 0 ? (x - (area.X + _pos[list[k - 1]].X * area.Width)) / 2 : step * 0.5;
                    double right = k < list.Count - 1 ? ((area.X + _pos[list[k + 1]].X * area.Width) - x) / 2 : step * 0.5;
                    left = Math.Clamp(left, step * 0.42, step * 2.6);
                    right = Math.Clamp(right, step * 0.42, step * 2.6);
                    _capRects[i] = new Rect(x - left * 0.90, y - _capH / 2, (left + right) * 0.90, _capH);
                }
            }
            _pts.Clear();
            for (int i = 0; i < n; i++)
                _pts.Add(new Point(_capRects[i].X + _capRects[i].Width / 2, _capRects[i].Y + _capRects[i].Height / 2));
            _hitR = Math.Max(step, _capH) * 0.75;
        }

        // Soft under-glow, then caps with a subtle top highlight.
        for (int i = 0; i < n; i++)
            dc.DrawEllipse(SoftGlowFor(_colors[i]), null, _pts[i], _capRects[i].Width * 0.9, _capH * 1.15);
        for (int i = 0; i < n; i++)
        {
            dc.DrawRoundedRectangle(FillFor(_colors[i]), null, _capRects[i], 4, 4);
            var hl = new Rect(_capRects[i].X + 1.5, _capRects[i].Y + 1.5,
                Math.Max(0, _capRects[i].Width - 3), Math.Max(1, _capRects[i].Height * 0.22));
            dc.DrawRoundedRectangle(CapHighlight, null, hl, 3, 3);
        }
    }

    /*-----------------------------------------------------*\
    | Dots: glowing orbs at arbitrary positions.            |
    \*-----------------------------------------------------*/
    void RenderDots(DrawingContext dc, Rect area, int n, bool rebuild)
    {
        double r = Math.Clamp(0.42 * Math.Sqrt(area.Width * area.Height / (n * 3.5)), 5, Math.Min(area.Width, area.Height) * 0.11);
        _hitR = r * 1.6;
        double glowR = r * 2.3;

        if (rebuild || _pts.Count < n)
        {
            _pts.Clear();
            for (int i = 0; i < n; i++)
                _pts.Add(new Point(area.X + _pos[i].X * area.Width, area.Y + _pos[i].Y * area.Height));
        }

        for (int i = 0; i < n; i++)
            dc.DrawEllipse(OrbGlowFor(_colors[i]), null, _pts[i], glowR, glowR);
        for (int i = 0; i < n; i++)
        {
            var p = _pts[i];
            dc.DrawEllipse(FillFor(_colors[i]), null, p, r, r);
            dc.DrawEllipse(OrbSpecular, null, new Point(p.X - r * 0.28, p.Y - r * 0.28), r * 0.32, r * 0.32);
        }
    }

    /*-----------------------------------------------------*\
    | Fan: neon disc + ring orbs + hub.                     |
    \*-----------------------------------------------------*/
    static readonly double[] FanGuides = { 0.40, 0.70 };

    void RenderFan(DrawingContext dc, double w, double h, int n, bool rebuild)
    {
        double side = Math.Min(w, h);
        var c = new Point(w / 2, h / 2);
        double R = side / 2 - 6;

        dc.DrawEllipse(_disc, null, c, R, R);
        dc.DrawEllipse(null, _discEdge, c, R, R);
        // Guides concentric with the orb ring (orbs sit ON the 0.70 circle).
        foreach (double rr in FanGuides)
            dc.DrawEllipse(null, _ringPen, c, R * rr, R * rr);

        if (n == 0) { dc.DrawEllipse(_hub, null, c, R * 0.16, R * 0.16); return; }

        double ringR = R * 0.70;
        double r = R * 0.115;
        _hitR = r * 1.6;
        double glowR = R * 0.30;

        if (rebuild || _pts.Count < n)
        {
            _pts.Clear();
            for (int i = 0; i < n; i++)
                _pts.Add(new Point(c.X + (_pos[i].X - 0.5) * 2 * ringR, c.Y + (_pos[i].Y - 0.5) * 2 * ringR));
        }

        for (int i = 0; i < n; i++)
            dc.DrawEllipse(OrbGlowFor(_colors[i]), null, _pts[i], glowR, glowR);
        for (int i = 0; i < n; i++)
        {
            var p = _pts[i];
            dc.DrawEllipse(FillFor(_colors[i]), null, p, r, r);
            // Centered specular so the ring reads perfectly concentric.
            dc.DrawEllipse(OrbSpecular, null, p, r * 0.30, r * 0.30);
        }
        dc.DrawEllipse(_hub, null, c, R * 0.16, R * 0.16);
        dc.DrawEllipse(null, _hubEdge, c, R * 0.16, R * 0.16);
    }
}

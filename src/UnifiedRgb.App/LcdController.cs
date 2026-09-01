using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UnifiedRgb.Core.Devices;

namespace UnifiedRgb.App;

/// <summary>Drives the Thermalright pump LCD with a live 240x320 display,
/// refreshing every second so the screen stays on. Content is authored in
/// landscape (320x240 - the panel is mounted rotated) and rotated 90 deg
/// clockwise into the device's RGB565 portrait buffer.</summary>
public sealed class LcdController : IDisposable
{
    static readonly bool RotateClockwise = true;

    readonly ThermalrightLcd _lcd;
    byte[]? _latest;
    Thread? _streamThread;
    volatile bool _stop;

    string? _bgPath;
    BitmapSource? _bgCache;

    public LcdDesign Design { get; set; } = LcdDesign.Default();
    public ICpuTempProvider Temp { get; set; } = new NullCpuTempProvider();
    public bool On { get; set; } = true;

    /// <summary>Raised on the UI thread each refresh, so the editor can update.</summary>
    public event Action? Ticked;

    LcdController(ThermalrightLcd lcd) { _lcd = lcd; }

    public static LcdController? TryStart()
    {
        var lcd = ThermalrightLcd.TryOpen();
        return lcd == null ? null : new LcdController(lcd);
    }

    /// <summary>Begin the 1 Hz refresh loop (call after Design is assigned).</summary>
    public void Start()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        _timerRef = timer;
        Tick();
        _streamThread = new Thread(StreamLoop)
        { IsBackground = true, Name = "lcd-stream", Priority = ThreadPriority.BelowNormal };
        _streamThread.Start();
    }
    DispatcherTimer? _timerRef;

    /// <summary>Send frames back-to-back, TRCC-style. The panel firmware falls
    /// back to its built-in screen whenever the stream goes idle for a few
    /// seconds — under the old send-per-tick model that showed as the screen
    /// blinking off for ~a second every 5-8 s. Re-sending the latest frame
    /// continuously keeps the link warm; the sleep caps identical-frame spam
    /// at ~25 fps, and a slow (full-speed USB) link self-paces via the
    /// blocking writes.</summary>
    void StreamLoop()
    {
        int sent = 0; long msSum = 0;
        var report = DateTime.UtcNow;
        while (!_stop)
        {
            var frame = Volatile.Read(ref _latest);
            if (frame == null) { Thread.Sleep(50); continue; }
            long t0 = Environment.TickCount64;
            try { _lcd.ShowFrame(frame); }
            catch (Exception ex)
            {
                UnifiedRgb.Core.Log.Occasional("lcd", "lcd", $"frame send failed: {ex.Message}");
                Thread.Sleep(500);
                continue;
            }
            long ms = Environment.TickCount64 - t0;
            sent++; msSum += ms;
            if ((DateTime.UtcNow - report).TotalMinutes >= 5)
            {
                UnifiedRgb.Core.Log.Info("lcd",
                    $"stream: {sent} frames in 5 min, avg {msSum / Math.Max(sent, 1)} ms/frame");
                sent = 0; msSum = 0; report = DateTime.UtcNow;
            }
            if (ms < 40) Thread.Sleep((int)(40 - ms));
            // Screen off = the shared blank frame: drop from 25 fps to ~2 fps.
            // Enough to keep the panel from falling back to its firmware
            // screen, but not ~720k identical USB transfers per night. Sleep in
            // short slices so flipping the lights back on stays responsive.
            if (ReferenceEquals(frame, _blank))
                for (int i = 0; i < 9 && !_stop && ReferenceEquals(Volatile.Read(ref _latest), _blank); i++)
                    Thread.Sleep(50);
        }
    }

    static int LW => ThermalrightLcd.Height;    // landscape width  = 320
    static int LH => ThermalrightLcd.Width;     // landscape height = 240

    /// <summary>Force an immediate re-render (e.g. after an edit).</summary>
    public void Refresh() => Tick();

    void Tick()
    {
        // Animated background: render fast enough for the GIF's frame rate;
        // static content stays at 1 Hz. Rendering only publishes the latest
        // frame — the stream thread owns the USB link and sends at whatever
        // rate it sustains.
        var want = _gif != null ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1);
        if (_timerRef != null && _timerRef.Interval != want) _timerRef.Interval = want;

        Volatile.Write(ref _latest, On ? RenderDesign() : _blank);
        Ticked?.Invoke();
    }

    // Output frames are ping-ponged: render into whichever buffer the stream
    // thread is NOT currently holding, then publish by reference. The old
    // new-array-per-tick was a 153 KB Large-Object-Heap allocation at up to
    // 10 Hz; the blank (screen off) is one shared cached frame.
    readonly byte[] _outA = new byte[ThermalrightLcd.FrameBytes];
    readonly byte[] _outB = new byte[ThermalrightLcd.FrameBytes];
    readonly byte[] _blank = new byte[ThermalrightLcd.FrameBytes];

    public string ElementText(LcdElement e) => e.Kind switch
    {
        LcdElementKind.Time => DateTime.Now.ToString("h:mm tt"),
        LcdElementKind.Date => DateTime.Now.ToString("ddd MMM d"),
        LcdElementKind.CpuTemp => Temp.ReadCelsius() is double c ? $"{c:0}°C" : "--°C",
        LcdElementKind.GpuTemp => GpuTempText(),
        LcdElementKind.FanRpm => FanRpmText(),
        LcdElementKind.NetSpeed => NetMeter.Read(),
        LcdElementKind.Weather => WeatherText(),
        LcdElementKind.AnalogClock => "",           // drawn, not typeset
        _ => e.Text ?? "",
    };

    static string WeatherText()
    {
        WeatherService.EnsureStarted();
        return WeatherService.Current;
    }

    static string GpuTempText()
    {
        UnifiedRgb.Core.Sensors.SensorHub.Touch();
        return UnifiedRgb.Core.Sensors.SensorHub.GpuTempC is int g ? $"{g}°C" : "--°C";
    }

    static string FanRpmText()
    {
        UnifiedRgb.Core.Sensors.SensorHub.Touch();
        foreach (var f in UnifiedRgb.Core.Sensors.SensorHub.BoardFans)
            if (f.Rpm is int rpm and > 0) return $"{rpm:n0} RPM";
        if (UnifiedRgb.Core.Sensors.SensorHub.GpuFanRpms is { Length: > 0 } g && g[0] > 0)
            return $"{g[0]:n0} RPM";
        return "---- RPM";
    }

    RenderTargetBitmap? _rtb;
    byte[]? _bgra;
    readonly DrawingVisual _visual = new();          // RenderOpen() clears it each tick
    static readonly LinearGradientBrush NoBgBrush = MakeNoBgBrush();
    static LinearGradientBrush MakeNoBgBrush()
    {
        var b = new LinearGradientBrush(Color.FromRgb(12, 16, 40), Color.FromRgb(60, 12, 60), 45);
        b.Freeze();
        return b;
    }

    byte[] RenderDesign()
    {
        var visual = _visual;
        using (var dc = visual.RenderOpen())
        {
            // Always lay an opaque base first: the render surface is reused
            // across ticks, and a background image with alpha would otherwise
            // ghost over the previous frame.
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, LW, LH));
            var bg = CurrentBackgroundFrame();
            if (bg != null)
            {
                Rect r;
                if (Design.BgW > 0.5)
                    r = new Rect(Design.BgX, Design.BgY, Design.BgW, Design.BgH);
                else
                {
                    // Legacy design without a stored rect: centered cover.
                    double scale = Math.Max((double)LW / bg.PixelWidth, (double)LH / bg.PixelHeight);
                    double w = bg.PixelWidth * scale, h = bg.PixelHeight * scale;
                    r = new Rect((LW - w) / 2, (LH - h) / 2, w, h);
                }
                dc.DrawImage(bg, r);
            }
            else
            {
                dc.DrawRectangle(NoBgBrush, null, new Rect(0, 0, LW, LH));
            }

            foreach (var e in Design.Elements)
            {
                if (e.Kind == LcdElementKind.AnalogClock)
                {
                    double r = e.FontSize;
                    DrawClock(dc, e.X + r, e.Y + r, r, ParseColor(e.ColorHex), DateTime.Now);
                    continue;
                }
                var ft = Format(ElementText(e), e);
                dc.DrawText(ft, new Point(e.X, e.Y));
            }
        }

        // Reuse the render surface + pixel buffers: this runs every second and
        // the background fill fully covers the bitmap, so no clear is needed.
        _rtb ??= new RenderTargetBitmap(LW, LH, 96, 96, PixelFormats.Pbgra32);
        _rtb.Render(visual);

        var bgra = _bgra ??= new byte[LW * LH * 4];
        _rtb.CopyPixels(bgra, LW * 4, 0);

        int dw = ThermalrightLcd.Width, dh = ThermalrightLcd.Height;
        // Rotate into the buffer the stream thread is not holding.
        var outp = ReferenceEquals(Volatile.Read(ref _latest), _outA) ? _outB : _outA;
        for (int dy = 0; dy < dh; dy++)
            for (int dx = 0; dx < dw; dx++)
            {
                int lx, ly;
                if (RotateClockwise) { lx = dy; ly = dw - 1 - dx; }
                else                 { lx = dh - 1 - dy; ly = dx; }
                int s = (ly * LW + lx) * 4;
                byte b = bgra[s], g = bgra[s + 1], r = bgra[s + 2];
                int v = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
                int o = (dy * dw + dx) * 2;
                outp[o] = (byte)(v & 0xFF);
                outp[o + 1] = (byte)((v >> 8) & 0xFF);
            }
        return outp;
    }

    /*-----------------------------------------------------*    | Background: still image, or an animated GIF played at  |
    | its own frame delays. GIF frames are composited (each  |
    | over the previous, honoring frame offsets) and scaled  |
    | to the panel once at load, so playback is just picking |
    | the frame whose time has come.                          |
    \*-----------------------------------------------------*/
    List<(BitmapSource Frame, TimeSpan Delay)>? _gif;
    int _gifIndex;
    DateTime _gifDue;

    BitmapSource? CurrentBackgroundFrame()
    {
        EnsureBackgroundLoaded();
        if (_gif is { Count: > 0 })
        {
            // Catch up through as many frames as are due — the old single-step
            // advance made GIFs with sub-100ms delays play in slow motion.
            var now = DateTime.UtcNow;
            for (int guard = 0; now >= _gifDue && guard < _gif.Count; guard++)
            {
                _gifIndex = (_gifIndex + 1) % _gif.Count;
                _gifDue += _gif[_gifIndex].Delay;
            }
            if (now >= _gifDue) _gifDue = now + _gif[_gifIndex].Delay;   // fell far behind: resync
            return _gif[_gifIndex].Frame;
        }
        return _bgCache;
    }

    void EnsureBackgroundLoaded()
    {
        var path = Design.BackgroundImagePath;
        if (path == _bgPath) return;   // already loaded — no per-frame File.Exists syscall
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        { _bgCache = null; _bgPath = null; _gif = null; return; }
        _bgCache = null; _gif = null;
        try
        {
            if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                LoadGif(path);
            else
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(path);
                img.EndInit();
                img.Freeze();
                _bgCache = img;
            }
            _bgPath = path;
        }
        catch (Exception ex)
        {
            UnifiedRgb.Core.Log.Warn("lcd", $"background load failed: {ex.Message}");
            _bgCache = null; _bgPath = null; _gif = null;
        }
    }

    void LoadGif(string path)
    {
        var dec = new GifBitmapDecoder(new Uri(path),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (dec.Frames.Count == 0) return;
        int w = dec.Frames[0].PixelWidth, h = dec.Frames[0].PixelHeight;
        // Composite at panel scale: full-res frames of a large GIF would cost
        // tens of MB for zero visible gain on a 320x240 screen.
        double scale = Math.Min(1.0, Math.Min((double)LW / w, (double)LH / h));
        int cw = Math.Max(1, (int)(w * scale)), chh = Math.Max(1, (int)(h * scale));
        var frames = new List<(BitmapSource, TimeSpan)>();
        BitmapSource? prev = null;
        // 96 composited panel-scale frames ≈ 29 MB worst case (was 150 ≈ 46 MB
        // resident for a tray app); longer GIFs loop their first 96 frames.
        int max = Math.Min(dec.Frames.Count, 96);
        for (int i = 0; i < max; i++)
        {
            var f = dec.Frames[i];
            int left = 0, top = 0, delayCs = 10;
            if (f.Metadata is BitmapMetadata md)
            {
                try { if (md.GetQuery("/imgdesc/Left") is ushort l) left = l; } catch { }
                try { if (md.GetQuery("/imgdesc/Top") is ushort t) top = t; } catch { }
                try { if (md.GetQuery("/grctlext/Delay") is ushort d) delayCs = d; } catch { }
            }
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                if (prev != null) dc.DrawImage(prev, new Rect(0, 0, cw, chh));
                dc.DrawImage(f, new Rect(left * scale, top * scale,
                    f.PixelWidth * scale, f.PixelHeight * scale));
            }
            var rtb = new RenderTargetBitmap(cw, chh, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            prev = rtb;
            frames.Add((rtb, TimeSpan.FromMilliseconds(Math.Max(delayCs * 10, 60))));
        }
        _gif = frames;
        _gifIndex = 0;
        _gifDue = DateTime.UtcNow + frames[0].Item2;
        UnifiedRgb.Core.Log.Info("lcd", $"GIF background: {frames.Count} frame(s) @ {cw}x{chh}");
    }

    /*  FormattedText is cached per element and rebuilt only when its inputs
     *  change. Building one every render tick created a stream of DirectWrite
     *  COM objects released on the GC finalizer thread, where WPF's handle
     *  release is known to crash (AccessViolation in
     *  IDWriteNumberSubstitution.ReleaseHandle). The clock text changes once
     *  a minute - virtually every tick now reuses the cached object.  */
    static readonly Typeface TfNormal = new(new FontFamily("Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface TfBold = new(new FontFamily("Segoe UI"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    sealed class FtCache
    {
        public string? Text; public double Size; public bool Bold; public string? Hex;
        public FormattedText? Ft;
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LcdElement, FtCache> _ftCache = new();

    public static FormattedText Format(string text, LcdElement e)
    {
        var c = _ftCache.GetOrCreateValue(e);
        if (c.Ft != null && c.Text == text && c.Size == e.FontSize && c.Bold == e.Bold && c.Hex == e.ColorHex)
            return c.Ft;
        c.Text = text; c.Size = e.FontSize; c.Bold = e.Bold; c.Hex = e.ColorHex;
        var brush = new SolidColorBrush(ParseColor(e.ColorHex));
        brush.Freeze();
        c.Ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, e.Bold ? TfBold : TfNormal, e.FontSize, brush, 1.0);
        return c.Ft;
    }

    public static Color ParseColor(string hex)
        => UnifiedRgb.Core.Rgb.TryFromHex(hex, out var c)
            ? Color.FromRgb(c.R, c.G, c.B)
            : Colors.White;   // empty/partial while the user types in a hex box

    /*-----------------------------------------------------*\
    | Analog clock face - drawn (not typeset) so it's a     |
    | real dial. Shared by the panel render and the editor  |
    | preview, so both agree to the pixel.                  |
    \*-----------------------------------------------------*/
    static void DrawClock(DrawingContext dc, double cx, double cy, double r, Color color, DateTime now)
    {
        if (r < 4) return;
        var center = new Point(cx, cy);
        var ring = new Pen(new SolidColorBrush(color), Math.Max(1.5, r * 0.05));
        ring.Freeze();
        dc.DrawEllipse(null, ring, center, r, r);

        // Hour ticks (12 of them), longer at the quarters.
        var tickBrush = new SolidColorBrush(color); tickBrush.Freeze();
        for (int i = 0; i < 12; i++)
        {
            double a = i * Math.PI / 6.0;
            double outer = r * 0.92;
            double inner = r * (i % 3 == 0 ? 0.74 : 0.82);
            var p1 = new Point(cx + Math.Sin(a) * inner, cy - Math.Cos(a) * inner);
            var p2 = new Point(cx + Math.Sin(a) * outer, cy - Math.Cos(a) * outer);
            var tp = new Pen(tickBrush, Math.Max(1.0, r * (i % 3 == 0 ? 0.045 : 0.025)));
            tp.Freeze();
            dc.DrawLine(tp, p1, p2);
        }

        double sec = now.Second + now.Millisecond / 1000.0;
        double min = now.Minute + sec / 60.0;
        double hr = (now.Hour % 12) + min / 60.0;

        Hand(dc, center, hr * Math.PI / 6.0, r * 0.50, Math.Max(2.0, r * 0.07), color);   // hour
        Hand(dc, center, min * Math.PI / 30.0, r * 0.78, Math.Max(1.5, r * 0.05), color); // minute
        Hand(dc, center, sec * Math.PI / 30.0, r * 0.85, Math.Max(1.0, r * 0.02),          // second
             Color.FromRgb(255, 80, 80));

        var hub = new SolidColorBrush(color); hub.Freeze();
        dc.DrawEllipse(hub, null, center, r * 0.06, r * 0.06);
    }

    static void Hand(DrawingContext dc, Point c, double angle, double len, double thick, Color color)
    {
        var tip = new Point(c.X + Math.Sin(angle) * len, c.Y - Math.Cos(angle) * len);
        var tail = new Point(c.X - Math.Sin(angle) * len * 0.18, c.Y + Math.Cos(angle) * len * 0.18);
        var pen = new Pen(new SolidColorBrush(color), thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawLine(pen, tail, tip);
    }

    /// <summary>A standalone clock face bitmap for the WYSIWYG editor, sized to the
    /// element's ClockSize (diameter). Refreshed each tick alongside the text.</summary>
    public static ImageSource RenderClockImage(LcdElement e)
    {
        double r = Math.Max(4, e.FontSize);
        int d = (int)Math.Ceiling(r * 2);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            DrawClock(dc, r, r, r, ParseColor(e.ColorHex), DateTime.Now);
        var rtb = new RenderTargetBitmap(d, d, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public void Dispose()
    {
        _stop = true;
        _timerRef?.Stop();
        _streamThread?.Join(1500);
        _lcd.Dispose();
    }
}

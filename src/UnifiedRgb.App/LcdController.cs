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
    readonly ThermalrightLcd _lcd;
    byte[]? _latest;
    // The buffer the stream thread is transmitting right now, and the lock that
    // makes "read the latest and mark it in flight" one step with "pick a free
    // buffer to render into". Without it the renderer chose by what was last
    // PUBLISHED, not what was being SENT: two GIF ticks after a publish it
    // overwrote the frame still going down a slow link, top of one frame and
    // bottom of the next on the panel.
    byte[]? _sending;
    readonly object _pick = new();
    Thread? _streamThread;
    volatile bool _stop;

    string? _bgPath;
    BitmapSource? _bgCache;
    BgVersion _bgVersion;      // size and write time of the loaded file (re-decoded when they change)
    DateTime _bgStatAt;        // the file is re-examined at most every BackgroundStatEvery, not per render tick
    double _bgNatW = 1, _bgNatH = 1;   // natural pixel size of the loaded file
    string? _bgFailedPath;     // the design's background when it is one that did not load

    // Backgrounds that did not load, per file. The memory used to be ONE
    // file, forgotten whenever any other background loaded: a scene sequence
    // alternating with a refused file re-decoded it, and logged it again, at
    // every step. Now each file is remembered at the size and write time it
    // had. Refused (over the size limits) or undecodable stays refused until
    // the file changes: the same bytes fail the same way. Unreadable (locked
    // by an editor mid-save, access denied, gone between the check and the
    // open) is retried on a short back-off capped at BgRetryMax - it was a
    // flat 30 s before the 2026-10 fixes, which then stretched it to an hour
    // for a file that had only been locked for a moment. Bounded: a sequence
    // through more than MaxBgFailures failing files forgets the oldest, which
    // costs one more decode of it per pass.
    readonly Dictionary<string, BgFailure> _bgFailures = new(StringComparer.OrdinalIgnoreCase);
    const int MaxBgFailures = 16;
    static readonly TimeSpan BgRetryMax = TimeSpan.FromSeconds(30);

    readonly record struct BgVersion(bool Exists, long Size, DateTime Written);
    sealed record BgFailure(BgVersion Version, string Why, bool Permanent, int Attempts, DateTime At, DateTime RetryAt);

    /// <summary>How often a background file's write time is re-read. Settable
    /// so the harness does not have to wait out the real interval.</summary>
    internal TimeSpan BackgroundStatEvery = TimeSpan.FromSeconds(2);

    /// <summary>The wait before an unreadable background is first tried again;
    /// it doubles per failure up to BgRetryMax. Settable for the harness.</summary>
    internal TimeSpan BackgroundRetryFirst = TimeSpan.FromSeconds(2);

    /// <summary>Background decodes attempted, successful or not. For the
    /// harness, which pins that a failed file is not decoded over and over.</summary>
    internal int BackgroundDecodes { get; private set; }

    /// <summary>Why the design's background is not showing - refused for its
    /// size, or failed to decode - or null when it shows or there is none.</summary>
    public string? BackgroundProblem { get; private set; }

    public LcdDesign Design { get; set; } = LcdDesign.Default();
    public ICpuTempProvider Temp { get; set; } = new NullCpuTempProvider();
    public bool On { get; set; } = true;

    /// <summary>Raised on the UI thread each refresh, so the editor can update.</summary>
    public event Action? Ticked;

    /// <summary>Raised on the UI thread once the panel has refused
    /// LostAfterFailures frames in a row: the handle is dead (the panel was
    /// unplugged, or re-enumerated under a new path after a resume) and no
    /// amount of resyncing the same handle will bring it back. The designer
    /// answers by letting this controller go and looking for the panel afresh.
    /// Raised once; the loop keeps retrying quietly until it is stopped, so a
    /// listener that chooses not to reopen loses nothing.</summary>
    public event Action? Lost;

    /// <summary>Consecutive failed frames before the panel is declared lost.
    /// Each failure already costs a 500 ms pause and a handshake, so four is
    /// a few seconds of a panel that answers nothing - not a single bad
    /// report. Settable so the harness does not have to wait that long.</summary>
    internal int LostAfterFailures = 4;

    /// <summary>True once any frame has landed on this handle. A controller
    /// lost WITHOUT ever delivering one reopened a panel that is on the bus
    /// but refusing everything - a wedged firmware, not an unplug.</summary>
    public bool DeliveredAny => _delivered;
    volatile bool _delivered;

    /// <summary>The panel's HID interface path, for a USB-level restart.</summary>
    public string? DevicePath => _lcd.DevicePath;

    internal LcdController(ThermalrightLcd lcd) { _lcd = lcd; }

    public static LcdController? TryStart()
    {
        var lcd = ThermalrightLcd.TryOpen();
        return lcd == null ? null : new LcdController(lcd);
    }

    /// <summary>Begin the 1 Hz refresh loop (call after Design is assigned).</summary>
    public void Start()
    {
        _ui = Dispatcher.CurrentDispatcher;
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
    Dispatcher? _ui;   // the thread Start ran on, where Lost is delivered

    /// <summary>Send frames back-to-back, TRCC-style. The panel firmware falls
    /// back to its built-in screen whenever the stream goes idle for a few
    /// seconds — under the old send-per-tick model that showed as the screen
    /// blinking off for ~a second every 5-8 s. Re-sending the latest frame
    /// keeps the link warm: a NEW frame goes out at once (the 40 ms floor caps
    /// a GIF at ~25 fps), while an UNCHANGED one is re-sent as a ~2 fps
    /// keepalive — a static design publishes once a second, and 301 HID
    /// reports per frame at 25 fps was ~7,500 kernel writes/s all day to
    /// redraw the same image. A slow (full-speed USB) link self-paces via the
    /// blocking writes.</summary>
    void StreamLoop()
    {
        int sent = 0; long msSum = 0;
        var report = DateTime.UtcNow;
        byte[]? lastSent = null;
        int failures = 0;
        bool lostSaid = false;
        while (!_stop)
        {
            byte[]? frame;
            lock (_pick) { frame = _latest; _sending = frame; }
            if (frame == null) { Thread.Sleep(50); continue; }
            // Reference identity is enough: RenderDesign always renders into
            // a buffer that is neither published nor in flight, so a new
            // publish is a new reference (and the screen-off blank is one
            // shared array).
            bool unchanged = ReferenceEquals(frame, lastSent);
            long t0 = Environment.TickCount64;
            try { _lcd.ShowFrame(frame); }
            catch (Exception ex)
            {
                UnifiedRgb.Core.Log.Occasional("lcd-frame", "lcd", $"frame send failed: {ex.Message}");
                // A run of refusals is not a bad report, it is a handle with no
                // panel behind it any more. Resync cannot fix that: it talks to
                // the same handle. Say so, once, to whoever can reopen it.
                if (++failures >= LostAfterFailures && !lostSaid && !_stop)
                {
                    lostSaid = true;
                    UnifiedRgb.Core.Log.Warn("lcd", $"the panel refused {failures} frames in a row; treating it as unplugged");
                    _ui?.BeginInvoke(new Action(() => { if (!_stop) Lost?.Invoke(); }));
                }
                Thread.Sleep(500);
                // A frame that stopped part way left the panel's parser waiting
                // for the rest of it, so the next frame's header would be eaten
                // as pixel data. Put it back in step before retrying.
                try { _lcd.Resync(); } catch { }
                continue;   // lastSent untouched: a failed frame is retried promptly
            }
            finally { Volatile.Write(ref _sending, null); }
            failures = 0;
            _delivered = true;
            lastSent = frame;
            long ms = Environment.TickCount64 - t0;
            sent++; msSum += ms;
            if ((DateTime.UtcNow - report).TotalMinutes >= 5)
            {
                UnifiedRgb.Core.Log.Info("lcd",
                    $"stream: {sent} frames in 5 min, avg {msSum / Math.Max(sent, 1)} ms/frame");
                sent = 0; msSum = 0; report = DateTime.UtcNow;
            }
            if (ms < 40) Thread.Sleep((int)(40 - ms));
            // Keepalive cadence for a frame the panel already shows. Sleep in
            // short slices so a fresh publish (or the lights flipping back on)
            // is picked up within 50 ms.
            if (unchanged)
                for (int i = 0; i < 9 && !_stop && ReferenceEquals(Volatile.Read(ref _latest), frame); i++)
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

        var frame = _blank;
        if (On)
        {
            try { frame = RenderDesign(); }
            catch (Exception ex)
            {
                // Element/background values come straight from lcd.json or a
                // shared scene file (a FontSize of 0 or a negative rect throws
                // in FormattedText/Rect). Unguarded, that would reach the app
                // handler and pop a modal error dialog every tick.
                UnifiedRgb.Core.Log.Occasional("lcd-render", "lcd", $"render failed: {ex.Message}");
            }
        }
        Volatile.Write(ref _latest, frame);
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
        LcdElementKind.NowPlaying => MediaLine(),
        LcdElementKind.AnalogClock or LcdElementKind.AlbumArt => "",   // drawn, not typeset
        _ => e.Text ?? "",
    };

    static string WeatherText()
    {
        WeatherService.EnsureStarted();
        return WeatherService.Current;
    }

    /// <summary>Cover art fitted inside a FontSize square at the element's
    /// corner: the box the designer drags is the space it takes, whatever
    /// shape the player handed over.</summary>
    static void DrawArt(DrawingContext dc, LcdElement e, BitmapSource art)
    {
        double box = e.FontSize;
        if (box < 1 || art.PixelWidth <= 0 || art.PixelHeight <= 0) return;
        double scale = Math.Min(box / art.PixelWidth, box / art.PixelHeight);
        double w = art.PixelWidth * scale, h = art.PixelHeight * scale;
        dc.DrawImage(art, new Rect(e.X + (box - w) / 2, e.Y + (box - h) / 2, w, h));
    }

    static string MediaLine()
    {
        MediaService.EnsureStarted();
        return MediaService.Line;
    }

    static string GpuTempText()
    {
        UnifiedRgb.Core.Sensors.SensorHub.TouchTemps();   // temp only: don't arm the Cooling-pane sweep
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
                if (e.Kind == LcdElementKind.AlbumArt)
                {
                    MediaService.EnsureStarted();
                    if (MediaService.Art is BitmapSource art) DrawArt(dc, e, art);
                    continue;                       // nothing playing: nothing drawn
                }
                // A track title can be longer than the panel is wide, so the
                // now-playing line is trimmed to what is left to its right.
                double maxW = e.Kind == LcdElementKind.NowPlaying ? Math.Max(8, LW - e.X) : 0;
                var ft = Format(ElementText(e), e, maxW);
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
        // Rotate 90 deg clockwise into a buffer that is neither published nor
        // being sent. With a slow link both can be busy (the published frame is
        // waiting, the previous one is still going out): then this tick keeps
        // what is published rather than tearing the frame in flight.
        byte[]? outp;
        lock (_pick)
        {
            bool aBusy = ReferenceEquals(_latest, _outA) || ReferenceEquals(_sending, _outA);
            bool bBusy = ReferenceEquals(_latest, _outB) || ReferenceEquals(_sending, _outB);
            outp = !aBusy ? _outA : !bBusy ? _outB : null;
            if (outp == null) return _latest ?? _blank;
        }
        for (int dy = 0; dy < dh; dy++)
            for (int dx = 0; dx < dw; dx++)
            {
                int lx = dy, ly = dw - 1 - dx;
                int s = (ly * LW + lx) * 4;
                byte b = bgra[s], g = bgra[s + 1], r = bgra[s + 2];
                int v = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
                int o = (dy * dw + dx) * 2;
                outp[o] = (byte)(v & 0xFF);
                outp[o + 1] = (byte)((v >> 8) & 0xFF);
            }
        return outp;
    }

    /*-----------------------------------------------------*\
    | Background: still image, or an animated GIF played at  |
    | its own frame delays. GIF frames are composited (each  |
    | onto the canvas the previous frame's DISPOSAL left,    |
    | honoring frame offsets) and scaled to the panel once   |
    | at load, so playback is just picking the frame whose   |
    | time has come. Frames are Pbgra32 with real alpha: the |
    | panel render lays them over its opaque black base.     |
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

    /// <summary>The decoded background for the editor canvas (a GIF's first
    /// frame), or null when the design has none or it failed to load. This is
    /// the ONE decode the panel render and the designer share - the designer
    /// used to hold its own full-resolution copy - cached per (path, write
    /// time) and loaded on demand, so a background drag re-reads a field.
    /// Frozen, so any thread may read it. Scaled down to at most about twice
    /// the panel: both views draw it into the design's rect, never at its own
    /// pixel size, so only BackgroundSize carries the file's real size.</summary>
    public BitmapSource? Background
    {
        get { EnsureBackgroundLoaded(); return _gif is { Count: > 0 } ? _gif[0].Frame : _bgCache; }
    }

    /// <summary>Natural pixel size of the background file (a GIF's logical
    /// screen, not the panel-scaled frames; a still's header size, not the
    /// scaled copy that is kept) - the editor's aspect source, and what its
    /// Fill and Fit placements scale.</summary>
    public (double W, double H) BackgroundSize
    {
        get { EnsureBackgroundLoaded(); return (_bgNatW, _bgNatH); }
    }

    void EnsureBackgroundLoaded()
    {
        var path = Design.BackgroundImagePath;
        if (string.IsNullOrEmpty(path))
        {
            // Also takes down the problem of a background that was just
            // removed: it stayed on the designer with no image set.
            if (_bgPath != null || _bgFailedPath != null) ClearBackground(null, null);
            return;
        }
        var now = DateTime.UtcNow;
        // The file in hand - the one loaded, or the failure on show - is
        // re-examined at most every BackgroundStatEvery, not on every render
        // tick (10 Hz under a GIF) or canvas drag.
        if ((path == _bgPath || path == _bgFailedPath) && now - _bgStatAt < BackgroundStatEvery) return;
        _bgStatAt = now;
        var version = VersionOf(path);
        if (path == _bgPath && version == _bgVersion) return;   // re-saved in place = a new version, decoded below
        string key = FailureKey(path);
        if (_bgFailures.TryGetValue(key, out var failed))
        {
            // Failed before: shown as failed, without a decode or a log line,
            // until the file changes or an unreadable one's back-off runs out.
            if (failed.Version != version) _bgFailures.Remove(key);
            else if (failed.Permanent || now < failed.RetryAt) { ClearBackground(path, failed.Why); return; }
        }
        // A missing file shows nothing and says nothing, and keeps this cheap
        // attribute read per call so it shows the moment it reappears.
        if (!version.Exists) { ClearBackground(null, null); return; }
        _bgCache = null; _gif = null;
        BackgroundDecodes++;
        string name = System.IO.Path.GetFileName(path);
        string? refused;
        try
        {
            refused = path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? LoadGif(path) : LoadStill(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // The bytes could not be read: a sharing violation while an editor
            // saves it, access denied, deleted since the check. Try again soon.
            RememberFailure(key, path, version, permanent: false, $"it could not be read ({ex.Message})", now);
            UnifiedRgb.Core.Log.Occasional("lcd-bg-unreadable", "lcd", $"background {name} could not be read, trying again shortly: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            // The decoder's verdict on bytes it did read (not an image, corrupt,
            // out of memory): the same file fails the same way next time.
            RememberFailure(key, path, version, permanent: true, $"it could not be decoded ({ex.Message})", now);
            UnifiedRgb.Core.Log.Occasional("lcd-bg-undecodable", "lcd", $"background {name} could not be decoded: {ex.Message}");
            return;
        }
        if (refused != null)
        {
            RememberFailure(key, path, version, permanent: true, $"it {refused}", now);
            UnifiedRgb.Core.Log.Occasional("lcd-bg-refused", "lcd", $"background {name} refused: it {refused}");
            return;
        }
        _bgFailures.Remove(key);
        _bgPath = path; _bgVersion = version;
        _bgFailedPath = null;
        BackgroundProblem = null;
    }

    /// <summary>Nothing showing: no background set, its file missing (both
    /// with no problem), or failedPath did not load, for the reason given.</summary>
    void ClearBackground(string? failedPath, string? why)
    {
        _bgCache = null; _gif = null; _bgPath = null; _bgNatW = _bgNatH = 1;
        _bgFailedPath = failedPath;
        BackgroundProblem = why;
    }

    void RememberFailure(string key, string path, BgVersion version, bool permanent, string why, DateTime now)
    {
        bool known = _bgFailures.TryGetValue(key, out var before);
        int attempts = known && before!.Version == version ? before.Attempts + 1 : 1;
        if (!known && _bgFailures.Count >= MaxBgFailures)
        {
            string? oldest = null;
            var at = DateTime.MaxValue;
            foreach (var (k, f) in _bgFailures)
                if (f.At < at) { oldest = k; at = f.At; }
            if (oldest != null) _bgFailures.Remove(oldest);
        }
        _bgFailures[key] = new BgFailure(version, why, permanent, attempts, now,
            permanent ? DateTime.MaxValue : now + RetryDelay(attempts, BackgroundRetryFirst));
        ClearBackground(path, why);
    }

    /// <summary>The wait before an unreadable background's next try:
    /// <paramref name="first"/> after its first failure, doubling per failure
    /// after that, never more than BgRetryMax.</summary>
    internal static TimeSpan RetryDelay(int attempts, TimeSpan first)
    {
        double ms = first.TotalMilliseconds * Math.Pow(2, Math.Clamp(attempts - 1, 0, 30));
        return TimeSpan.FromMilliseconds(Math.Min(ms, BgRetryMax.TotalMilliseconds));
    }

    static BgVersion VersionOf(string path)
    {
        try
        {
            var fi = new System.IO.FileInfo(path);
            return fi.Exists ? new BgVersion(true, fi.Length, fi.LastWriteTimeUtc) : default;
        }
        catch (Exception) { return default; }   // a string that cannot name a file: missing, as File.Exists said before
    }

    static string FailureKey(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception) { return path; }   // not a path GetFullPath takes: the string itself still tells files apart
    }

    // What a background may be, judged from the file's header before a pixel
    // is decoded. A photo straight off a 100 MP camera fits; a header claiming
    // 60000x60000 (a few bytes of file, gigabytes once decoded) does not.
    const int MaxStillSide = 16384;
    const long MaxStillPixels = 120_000_000;
    // A still is kept at no more than twice the panel's 320x240, the most the
    // panel or the designer's canvas (on a 200% display) can show. The cap on
    // the decoded pixel count covers panoramas and other extreme shapes, which
    // cover the panel only at a large size.
    const long MaxStillDecodePixels = 4L * 1024 * 1024;
    // GIFs are read whole into memory for the decoder, so the file is bounded
    // too, at the size an import accepts for an asset (SetupBundle).
    const long MaxGifBytes = 24L * 1024 * 1024;
    const int MaxGifSide = 4096;
    const int MaxGifFrames = 96;

    static string? TooLarge(int w, int h, int maxSide, long maxPixels)
    {
        if (w <= 0 || h <= 0) return $"has no size ({w}x{h})";
        if (w > maxSide || h > maxSide || (long)w * h > maxPixels)
            return $"is {w}x{h}, more than a panel background can be ({maxSide} px a side, {maxPixels / 1_000_000} MP)";
        return null;
    }

    /// <summary>The width to decode a still of natural size w x h at: its longer
    /// side as large as the designer can zoom a background (LcdDesign.MaxBgSize),
    /// never more than the image itself, and within MaxStillDecodePixels however
    /// long and thin it is. Twice the panel was too little: a background zoomed
    /// past "cover" (a crop of a big photo) was drawn from that small decode
    /// and came out visibly soft.</summary>
    internal static int StillDecodeWidth(int w, int h)
    {
        double scale = Math.Min(1.0, LcdDesign.MaxBgSize / Math.Max(w, h));
        double px = w * scale * (h * scale);
        if (px > MaxStillDecodePixels) scale *= Math.Sqrt(MaxStillDecodePixels / px);
        return Math.Clamp((int)Math.Round(w * scale), 1, w);
    }

    /// <summary>A still image. Null when loaded, otherwise why it was refused.
    /// The natural size is read from the header (a delay-created frame with no
    /// cache decodes nothing), and only the scaled copy is kept: a large photo
    /// used to sit fully decoded, at four bytes a pixel, for the life of the
    /// design. One open stream for both, so the file checked is the file
    /// decoded.</summary>
    string? LoadStill(string path)
    {
        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
            System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
        var head = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (head.Frames.Count == 0) return "has no image in it";
        int w = head.Frames[0].PixelWidth, h = head.Frames[0].PixelHeight;
        if (TooLarge(w, h, MaxStillSide, MaxStillPixels) is string why) return why;

        fs.Position = 0;
        int dw = StillDecodeWidth(w, h);
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;   // decode now, so the stream can go
        if (dw < w) img.DecodePixelWidth = dw;        // the height follows the aspect
        img.StreamSource = fs;
        img.EndInit();
        img.Freeze();
        _bgCache = img;
        _bgNatW = w; _bgNatH = h;
        return null;
    }

    /// <summary>An animated GIF, composited to panel-scale frames. Null when
    /// loaded, otherwise why it was refused. The decoder caches nothing and
    /// creates a frame only when it is composited, so only the first
    /// MaxGifFrames are touched (the old OnLoad decoder decoded EVERY frame up
    /// front); and each is scaled to panel size before it is drawn
    /// (PanelScale), because a full-size frame drawn as it is stays decoded on
    /// the frame object until the load returns - lazy creation alone left the
    /// peak where it was.</summary>
    string? LoadGif(string path)
    {
        byte[] bytes;
        using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                   System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
        {
            if (fs.Length > MaxGifBytes)
                return $"is {fs.Length / (1024 * 1024)} MB, more than a background GIF can be ({MaxGifBytes / (1024 * 1024)} MB)";
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }
        using var ms = new System.IO.MemoryStream(bytes, writable: false);
        var dec = new GifBitmapDecoder(ms,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (dec.Frames.Count == 0) return "has no frames";
        // The canvas is the GIF's logical screen, not its first frame: an
        // optimised GIF's first frame can be a sub-rectangle, and a canvas
        // sized to it would crop every later frame that reaches past it.
        // The first frame is the floor in case the header is missing/wrong.
        int w = dec.Frames[0].PixelWidth, h = dec.Frames[0].PixelHeight;
        if (dec.Metadata is BitmapMetadata gmd)
        {
            try { if (gmd.GetQuery("/logscrdesc/Width") is ushort lw && lw > 0) w = Math.Max(w, lw); } catch { }
            try { if (gmd.GetQuery("/logscrdesc/Height") is ushort lh && lh > 0) h = Math.Max(h, lh); } catch { }
        }
        if (TooLarge(w, h, MaxGifSide, (long)MaxGifSide * MaxGifSide) is string tooBig) return tooBig;
        // Composite at panel scale: full-res frames of a large GIF would cost
        // tens of MB for zero visible gain on a 320x240 screen.
        double scale = Math.Min(1.0, Math.Min((double)LW / w, (double)LH / h));
        int cw = Math.Max(1, (int)(w * scale)), chh = Math.Max(1, (int)(h * scale));
        // 96 composited panel-scale frames ≈ 29 MB worst case (was 150 ≈ 46 MB
        // resident for a tray app); longer GIFs loop their first 96 frames.
        int max = Math.Min(dec.Frames.Count, MaxGifFrames);

        // Pass 1: each frame's placement, timing and disposal from its Image
        // Descriptor / Graphic Control Extension. Disposal is what happens to
        // a frame's rectangle once its delay is up, i.e. what the NEXT frame
        // is drawn onto - so the whole sequence is planned before any pixel
        // is rendered (GifCanvas.Plan), and the plan is what the tests pin.
        var draw = new Rect[max];
        var delays = new TimeSpan[max];
        var placed = new (Int32Rect Rect, int Disposal)[max];
        for (int i = 0; i < max; i++)
        {
            var f = dec.Frames[i];
            int left = 0, top = 0, fw = f.PixelWidth, fh = f.PixelHeight, delayCs = 10, disposal = 0;
            if (f.Metadata is BitmapMetadata md)
            {
                try { if (md.GetQuery("/imgdesc/Left") is ushort l) left = l; } catch { }
                try { if (md.GetQuery("/imgdesc/Top") is ushort t) top = t; } catch { }
                // WIC hands each frame over at its own sub-image size, so the
                // descriptor normally agrees with the bitmap. When it does not,
                // the descriptor is the size the offset was written for.
                try { if (md.GetQuery("/imgdesc/Width") is ushort iw && iw > 0 && iw != fw) fw = iw; } catch { }
                try { if (md.GetQuery("/imgdesc/Height") is ushort ih && ih > 0 && ih != fh) fh = ih; } catch { }
                try { if (md.GetQuery("/grctlext/Delay") is ushort d) delayCs = d; } catch { }
                // WIC reports the GCE disposal as a byte (0/1 leave, 2 restore
                // to background, 3 restore to previous). Without it every frame
                // was drawn over the previous composite, so GIFs that rely on
                // 2/3 to erase a moving sprite accumulated its old positions.
                try
                {
                    var q = md.GetQuery("/grctlext/Disposal");
                    if (q is byte db) disposal = db;
                    else if (q is ushort ds) disposal = ds;
                }
                catch { }
            }
            // A frame decodes at its own size whatever the logical screen
            // says, so a forged one is refused here, before pass 2 decodes it.
            if (TooLarge(Math.Max(fw, f.PixelWidth), Math.Max(fh, f.PixelHeight), MaxGifSide, (long)MaxGifSide * MaxGifSide) is string frameTooBig)
                return $"has a frame that {frameTooBig}";
            draw[i] = new Rect(left * scale, top * scale, fw * scale, fh * scale);
            placed[i] = (GifCanvas.PixelRect(draw[i], cw, chh), disposal);
            delays[i] = TimeSpan.FromMilliseconds(Math.Max(delayCs * 10, 60));
        }
        var plan = GifCanvas.Plan(placed);

        // Pass 2: composite. The canvas lives in a Pbgra32 byte buffer between
        // frames because two of the disposal methods edit it in ways a
        // DrawingContext cannot: clearing a rectangle to transparent (drawing
        // "transparent" over pixels blends to a no-op) and putting an earlier
        // state back. All-zero Pbgra32 is transparent, which is what the GIF's
        // "background" is here - the panel render lays its own base under it.
        int stride = cw * 4;
        var canvas = new byte[stride * chh];
        byte[]? beforePrev = null;   // snapshot from before a frame that disposal 3 will undo
        var frames = new List<(BitmapSource, TimeSpan)>(max);
        for (int i = 0; i < max; i++)
        {
            var step = plan[i];
            if (step.RestorePrevious && beforePrev != null) Array.Copy(beforePrev, canvas, canvas.Length);
            if (step.Clear.HasArea) GifCanvas.ClearRect(canvas, stride, step.Clear);
            // Only a frame the next step will want to undo is worth copying.
            beforePrev = i + 1 < max && plan[i + 1].RestorePrevious ? (byte[])canvas.Clone() : null;

            var under = BitmapSource.Create(cw, chh, 96, 96, PixelFormats.Pbgra32, null, canvas, stride);
            var piece = PanelScale(dec.Frames[i], scale);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(under, new Rect(0, 0, cw, chh));
                if (piece != null) dc.DrawImage(piece, draw[i]);
            }
            var rtb = new RenderTargetBitmap(cw, chh, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            // What is shown is the composite AFTER drawing; the frame's own
            // disposal only applies once its delay is up, i.e. at the top of
            // the next iteration, so the canvas carries the drawn state on.
            rtb.CopyPixels(canvas, stride, 0);
            frames.Add((rtb, delays[i]));
        }
        _gif = frames;
        _gifIndex = 0;
        _gifDue = DateTime.UtcNow + frames[0].Item2;
        _bgNatW = w; _bgNatH = h;
        UnifiedRgb.Core.Log.Info("lcd", $"GIF background: {frames.Count} frame(s) @ {cw}x{chh}");
        return null;
    }

    /// <summary>A GIF frame at the scale it is composited at. Drawing the
    /// full-size frame left its decoded pixels cached on the frame object, and
    /// the decoder holds every frame until the load returns - so a large GIF
    /// peaked at its whole decoded animation (measured: a 2048x2048, 60-frame
    /// GIF cost 1.2 GB) however lazily the frames were created. Scaled and
    /// copied out here, only the panel-size copy is ever kept. A frame already
    /// at panel scale or smaller is drawn as it is (and so exactly as before).
    /// Null for a frame that scales to nothing.</summary>
    static BitmapSource? PanelScale(BitmapSource frame, double scale)
    {
        if (scale >= 1.0) return frame;
        int sw = (int)Math.Round(frame.PixelWidth * scale), sh = (int)Math.Round(frame.PixelHeight * scale);
        if (sw < 1 || sh < 1) return null;
        var scaled = new FormatConvertedBitmap(
            new TransformedBitmap(frame, new ScaleTransform((double)sw / frame.PixelWidth, (double)sh / frame.PixelHeight)),
            PixelFormats.Pbgra32, null, 0);
        int stride = scaled.PixelWidth * 4;
        var px = new byte[stride * scaled.PixelHeight];
        scaled.CopyPixels(px, stride, 0);
        var copy = BitmapSource.Create(scaled.PixelWidth, scaled.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, px, stride);
        copy.Freeze();
        return copy;
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
        public string? Text; public double Size; public bool Bold; public string? Hex; public double MaxW;
        public FormattedText? Ft;
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LcdElement, FtCache> _ftCache = new();

    /// <summary>maxWidth 0 leaves the text untrimmed, exactly as before.</summary>
    public static FormattedText Format(string text, LcdElement e, double maxWidth = 0)
    {
        var c = _ftCache.GetOrCreateValue(e);
        if (c.Ft != null && c.Text == text && c.Size == e.FontSize && c.Bold == e.Bold
            && c.Hex == e.ColorHex && c.MaxW == maxWidth)
            return c.Ft;
        var brush = new SolidColorBrush(ParseColor(e.ColorHex));
        brush.Freeze();
        // Build first, commit second: a throwing construction (FontSize <= 0
        // from lcd.json) must leave the cache untouched, or the stale entry
        // would match the bad inputs on the next tick and render silently at
        // the previous size after Tick's one rate-limited log line.
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, e.Bold ? TfBold : TfNormal, e.FontSize, brush, 1.0);
        if (maxWidth > 0)
        {
            ft.MaxTextWidth = maxWidth;
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        c.Text = text; c.Size = e.FontSize; c.Bold = e.Bold; c.Hex = e.ColorHex; c.MaxW = maxWidth;
        c.Ft = ft;
        return ft;
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
        var pens = ClockPensFor(color, r);
        dc.DrawEllipse(null, pens.Ring, center, r, r);

        // Hour ticks (12 of them), longer at the quarters.
        for (int i = 0; i < 12; i++)
        {
            double a = i * Math.PI / 6.0;
            double outer = r * 0.92;
            double inner = r * (i % 3 == 0 ? 0.74 : 0.82);
            var p1 = new Point(cx + Math.Sin(a) * inner, cy - Math.Cos(a) * inner);
            var p2 = new Point(cx + Math.Sin(a) * outer, cy - Math.Cos(a) * outer);
            dc.DrawLine(i % 3 == 0 ? pens.TickMajor : pens.TickMinor, p1, p2);
        }

        double sec = now.Second + now.Millisecond / 1000.0;
        double min = now.Minute + sec / 60.0;
        double hr = (now.Hour % 12) + min / 60.0;

        Hand(dc, center, hr * Math.PI / 6.0, r * 0.50, pens.Hour);
        Hand(dc, center, min * Math.PI / 30.0, r * 0.78, pens.Minute);
        Hand(dc, center, sec * Math.PI / 30.0, r * 0.85, pens.Second);

        dc.DrawEllipse(pens.Hub, null, center, r * 0.06, r * 0.06);
    }

    static void Hand(DrawingContext dc, Point c, double angle, double len, Pen pen)
    {
        var tip = new Point(c.X + Math.Sin(angle) * len, c.Y - Math.Cos(angle) * len);
        var tail = new Point(c.X - Math.Sin(angle) * len * 0.18, c.Y + Math.Cos(angle) * len * 0.18);
        dc.DrawLine(pen, tail, tip);
    }

    // Everything but the hand angles depends only on (color, radius), so the
    // ~18 frozen Pens/Brushes are built once per distinct face rather than per
    // render tick (10 Hz under a GIF, twice that with the editor open). Frozen
    // Freezables are thread-agnostic; the cache itself is only touched on the
    // UI thread (panel render and editor preview both run there). Bounded so
    // a color-wheel drag can't grow it without limit.
    sealed record ClockPens(Pen Ring, Pen TickMajor, Pen TickMinor, Pen Hour, Pen Minute, Pen Second, Brush Hub);
    static readonly Dictionary<(Color, double), ClockPens> _clockPens = new();

    static ClockPens ClockPensFor(Color color, double r)
    {
        if (_clockPens.TryGetValue((color, r), out var pens)) return pens;
        if (_clockPens.Count >= 16) _clockPens.Clear();
        var brush = new SolidColorBrush(color); brush.Freeze();
        var second = new SolidColorBrush(Color.FromRgb(255, 80, 80)); second.Freeze();
        pens = new ClockPens(
            Frozen(new Pen(brush, Math.Max(1.5, r * 0.05))),
            Frozen(new Pen(brush, Math.Max(1.0, r * 0.045))),
            Frozen(new Pen(brush, Math.Max(1.0, r * 0.025))),
            HandPen(brush, Math.Max(2.0, r * 0.07)),
            HandPen(brush, Math.Max(1.5, r * 0.05)),
            HandPen(second, Math.Max(1.0, r * 0.02)),
            brush);
        _clockPens[(color, r)] = pens;
        return pens;
    }

    static Pen Frozen(Pen pen) { pen.Freeze(); return pen; }
    static Pen HandPen(Brush brush, double thick)
        => Frozen(new Pen(brush, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    /// <summary>A standalone clock face bitmap for the WYSIWYG editor, sized to the
    /// element's ClockSize (diameter). Refreshed each tick alongside the text.</summary>
    public static ImageSource RenderClockImage(LcdElement e)
    {
        // Bounded here as well as at load: this allocates a bitmap twice the
        // radius on a side, every second the designer is open, and the element
        // may have arrived through an import that never saw the slider.
        double r = double.IsFinite(e.FontSize) ? Math.Clamp(e.FontSize, 4, LcdDesign.MaxFontSize) : 40;
        int d = (int)Math.Ceiling(r * 2);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            DrawClock(dc, r, r, r, ParseColor(e.ColorHex), DateTime.Now);
        var rtb = new RenderTargetBitmap(d, d, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Stop rendering and publishing, on the thread that started it.
    /// The stream thread notices on its next slice. Split from Dispose so a
    /// designer letting a dead panel go can do the dispatcher-bound half here
    /// and the blocking half (the join, the handle close) off the UI thread:
    /// the join can wait out a stalled write, and the whole window stalling
    /// with it is the one visible cost of an unplug.</summary>
    public void Detach()
    {
        _stop = true;
        _timerRef?.Stop();
    }

    public void Dispose()
    {
        _stop = true;
        _timerRef?.Stop();
        _streamThread?.Join(1500);
        _lcd.Dispose();
    }
}

/// <summary>The disposal arithmetic of LcdController.LoadGif, kept apart from
/// the rendering so it can be tested: nothing in the framework writes a GIF
/// Graphic Control Extension (GifBitmapEncoder drops it), so a GIF with
/// disposal metadata cannot be built inside the harness. A top-level class
/// rather than a nested one so touching it does not run LcdController's
/// WPF static initialisers (brushes, typefaces) on a test thread.</summary>
public static class GifCanvas
{
    /// <summary>What the compositor does to the canvas BEFORE drawing one
    /// frame: put back the canvas as it was before the previous frame drew
    /// (its disposal was 3), or clear a rectangle to transparent (2). Neither
    /// = draw straight over whatever the previous frame left (0/1).</summary>
    public readonly record struct Step(bool RestorePrevious, Int32Rect Clear);

    /// <summary>Plan every frame's step from each frame's rectangle (canvas
    /// pixels) and GIF disposal method (WIC /grctlext/Disposal: 0 unspecified,
    /// 1 leave, 2 restore to background, 3 restore to previous). Frame i's
    /// step is decided by frame i-1: disposal says what happens to a frame
    /// once its delay has elapsed, which is just before the next one is drawn.
    /// Frame 0 always starts on an untouched canvas, and the last frame's
    /// disposal is never applied because nothing follows it (the loop restarts
    /// from the stored first composite). Reserved values 4-7 are treated as
    /// "leave", the way browsers do.</summary>
    public static Step[] Plan(IReadOnlyList<(Int32Rect Rect, int Disposal)> frames)
    {
        var steps = new Step[frames.Count];
        for (int i = 1; i < steps.Length; i++)
        {
            var (rect, disposal) = frames[i - 1];
            steps[i] = disposal switch
            {
                2 => new Step(false, rect),
                3 => new Step(true, Int32Rect.Empty),
                _ => default,
            };
        }
        return steps;
    }

    /// <summary>The whole pixels a scaled frame rectangle touches, clamped to
    /// the canvas. Rounded OUTWARD (floor/ceil): a disposal-2 clear must take
    /// the anti-aliased edge of a frame drawn at a fractional offset with it,
    /// or a one-pixel ghost outline is left behind every step.</summary>
    public static Int32Rect PixelRect(Rect r, int canvasW, int canvasH)
    {
        int x0 = Math.Clamp((int)Math.Floor(r.X), 0, canvasW);
        int y0 = Math.Clamp((int)Math.Floor(r.Y), 0, canvasH);
        int x1 = Math.Clamp((int)Math.Ceiling(r.Right), 0, canvasW);
        int y1 = Math.Clamp((int)Math.Ceiling(r.Bottom), 0, canvasH);
        return x1 > x0 && y1 > y0 ? new Int32Rect(x0, y0, x1 - x0, y1 - y0) : Int32Rect.Empty;
    }

    /// <summary>Zero (= transparent, in premultiplied BGRA) a rectangle of a
    /// Pbgra32 buffer. The rect must already be clamped (PixelRect does).</summary>
    public static void ClearRect(byte[] pbgra, int stride, Int32Rect rect)
    {
        for (int y = rect.Y; y < rect.Y + rect.Height; y++)
            Array.Clear(pbgra, y * stride + rect.X * 4, rect.Width * 4);
    }
}

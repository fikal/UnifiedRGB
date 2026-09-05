using System.Text;
using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Devices;

/// <summary>Razer peripherals over the vendor HID feature-report protocol — the
/// one openrazer (Linux) and OpenRGB reverse-engineered and publish under
/// GPL-2.0, the same licence as this project. No Synapse needed.
///
/// Wire format: one 90-byte report sent as HID feature report 0 (91 bytes with
/// the report id): [status] [transaction id] [remaining packets ×2] [protocol]
/// [data size] [command class] [command id] [80 arguments] [crc] [reserved],
/// crc = XOR of wire bytes 2..87. The device answers on the same report with
/// status 0x02 (ok), 0x01 (busy → retry), 0x03 (failure), 0x04 (timeout, e.g.
/// a wireless mouse that is asleep) or 0x05 (not supported).
///
/// Lighting is the "extended matrix" custom frame (class 0x0F cmd 0x03, one
/// row at a time) followed by "effect custom" (0x0F/0x02, effect 0x08). DPI,
/// DPI stages and polling rate use class 0x04 / 0x00 and are written to the
/// mouse's onboard storage (VARSTORE), so they survive with no software.
///
/// Deliberately NOT done: putting the device in Razer "driver mode"
/// (0x00/0x04 mode 3). That is what makes the DPI button and wheel tilt stop
/// working under openrazer — the mouse then expects the host to do them. We
/// stay in normal mode and only stream frames; the onboard profile keeps
/// buttons and DPI stages.
///
/// The Basilisk V3 Pro is the first model; the table is the place to add more
/// (pid → transaction id + matrix shape, both straight out of openrazer's
/// razermouse_driver.c / mouse.py). The HyperFlux V2 charging pad (0x00CF)
/// hides the paired mouse behind its own USB identity: every known
/// transaction id is asked for firmware/serial, an answer that also has a DPI
/// is the mouse, one without is the pad itself. The pad's LED count is probed
/// by frame width (firmware that refuses an out-of-range column reveals it);
/// when the firmware accepts anything, the count comes from hardware.json
/// (`RazerLedCounts`, set from the Lighting pane's Razer… dialog) or a guess.
/// Everything a new pad needs is therefore discoverable from one build.</summary>
public sealed class RazerHid : IRgbDevice, IBatteryDevice
{
    public const ushort VID = 0x1532;
    const int WIRE_LEN = 90;
    const int FEATURE_LEN = 91;          // report id 0x00 + 90 wire bytes
    const int ARGS = 9;                  // buffer index of arguments[0]
    const int MAX_LEDS_PER_PACKET = 25;  // (80 - 5) / 3
    public const int MaxLeds = 64;       // sanity cap for configured/probed counts
    const int DefaultPadLeds = 20;

    // response status
    const byte ST_BUSY = 0x01, ST_OK = 0x02, ST_FAIL = 0x03, ST_TIMEOUT = 0x04, ST_UNSUPPORTED = 0x05;
    const byte NOSTORE = 0x00, VARSTORE = 0x01, ZERO_LED = 0x00;

    /// <summary>Transaction ids seen across Razer's line-up; the one a device
    /// answers "get firmware version" on is the one it wants for everything.</summary>
    static readonly byte[] TransactionCandidates = { 0x1F, 0x3F, 0x9F, 0xFF };

    enum Kind { Mouse, Pad }

    sealed record Model(ushort Pid, string Name, byte Tid, int Rows, int Cols, Kind Kind,
                        Func<int, RgbZone[]> Zones, Func<int, LedPos[]> Positions, float Aspect);

    /// <summary>1×13 extended matrix: col 0 scroll wheel, col 1 logo, cols 2-12
    /// the underglow strip around the base (openrazer MATRIX_DIMS [1, 13]).</summary>
    static RgbZone[] BasiliskV3ProZones(int _) => new[]
    {
        new RgbZone { Name = "Wheel",     Offset = 0, Count = 1 },
        new RgbZone { Name = "Logo",      Offset = 1, Count = 1 },
        new RgbZone { Name = "Underglow", Offset = 2, Count = 11 },
    };

    static LedPos[] BasiliskV3ProPositions(int _)
    {
        var p = new LedPos[13];
        p[0] = new LedPos(0.5f, 0.18f);   // scroll wheel, front centre
        p[1] = new LedPos(0.5f, 0.62f);   // logo under the palm
        // Underglow: a U around the base — down the left flank, across the
        // rear, up the right flank. Mirrored on real hardware it still reads
        // as one strip; the Razer… dialog's Test chase shows the real order.
        for (int i = 0; i < 11; i++)
        {
            double t = i / 10.0;                    // 0 = left front, 1 = right front
            double ang = Math.PI * (0.5 + t);       // 90° → 270° around the rear
            p[2 + i] = new LedPos((float)(0.5 + 0.42 * Math.Cos(ang)), (float)(0.55 - 0.42 * Math.Sin(ang)));
        }
        return p;
    }

    static RgbZone[] PadZones(int n) => new[] { new RgbZone { Name = "Strip", Offset = 0, Count = n } };

    /// <summary>A pad's strip runs around its edge: n points clockwise around
    /// a rectangle from the top-left corner.</summary>
    static LedPos[] PadPositions(int n)
    {
        var p = new LedPos[n];
        const float w = 1f, h = 0.72f;
        float perim = 2 * (w + h);
        for (int i = 0; i < n; i++)
        {
            float d = perim * i / n;
            float x, y;
            if (d < w) { x = d; y = 0; }
            else if (d < w + h) { x = w; y = d - w; }
            else if (d < 2 * w + h) { x = w - (d - w - h); y = h; }
            else { x = 0; y = h - (d - 2 * w - h); }
            p[i] = new LedPos(x, y / h);
        }
        return p;
    }

    static readonly Model BasiliskV3Pro = new(0x00AA, "Razer Basilisk V3 Pro", 0x1F, 1, 13, Kind.Mouse, BasiliskV3ProZones, BasiliskV3ProPositions, 0.62f);

    static readonly Model[] Models =
    {
        BasiliskV3Pro,                                  // wired / charging cable
        BasiliskV3Pro with { Pid = 0x00AB },            // its own HyperSpeed dongle
    };

    /// <summary>HyperFlux V2 charging pad + built-in receiver. Whatever mouse is
    /// paired sits behind this pid; probed, never assumed.</summary>
    public const ushort HYPERFLUX_V2 = 0x00CF;

    /// <summary>Serialises every feature-report exchange — the driver's frames,
    /// a diagnostic probe and the layout dialog may hold the same collection
    /// open at once, and a reply must be read by its sender.</summary>
    static readonly object Gate = new();

    readonly HidNative.HidHandle _hid;
    readonly Model _model;
    readonly byte _tid;
    readonly object _writeLock = new();
    readonly Rgb[] _frame;
    Rgb[]? _last;
    bool _verified;                   // first frame's reply checked
    int _failures;
    long _nextRetryTick;
    long _lastSendTick;

    public string Name { get; }
    public string Vendor => "Razer";
    public DeviceType Type => _model.Kind == Kind.Pad ? DeviceType.Other : DeviceType.Mouse;
    public int LedCount => _model.Rows * _model.Cols;
    public IReadOnlyList<RgbZone> Zones { get; }
    public IReadOnlyList<LedPos>? LedPositions { get; }
    public float? PreviewAspect => _model.Aspect;

    /// <summary>Firmware version and serial from detection ("?" when the device
    /// was asleep) — shown in diagnostics.</summary>
    public string Firmware { get; private set; } = "?";
    public string Serial { get; private set; } = "?";
    public byte TransactionId => _tid;
    public ushort ProductId => _model.Pid;
    public bool IsPad => _model.Kind == Kind.Pad;
    /// <summary>Where a pad's LED count came from: configured / probed / guessed.</summary>
    public string CountSource { get; private set; } = "known";

    RazerHid(HidNative.HidHandle hid, Model model, byte tid)
    {
        _hid = hid; _model = model; _tid = tid; Name = model.Name;
        _frame = new Rgb[LedCount];
        Zones = model.Zones(LedCount);
        LedPositions = model.Positions(LedCount);
    }

    /*-----------------------------------------------------*\
    | Detection                                             |
    \*-----------------------------------------------------*/

    /// <summary>One device per known pid (the wireless dongle enumerates even
    /// while the mouse sleeps, so a known pid is claimed regardless — the
    /// frames start landing when it wakes). The HyperFlux V2 pad yields one
    /// device per answering identity: the paired mouse and/or the pad.</summary>
    public static List<IRgbDevice> DetectAll()
    {
        var list = new List<IRgbDevice>();
        List<HidNative.HidInfo> all;
        try { all = HidNative.FindAll(); }
        catch (Exception ex) { Log.Error("Razer", ex); return list; }

        var seen = new HashSet<ushort>();
        foreach (var iface in all.Where(IsControlCollection).OrderBy(h => h.ProductId))
        {
            if (!seen.Add(iface.ProductId)) continue;
            var model = Models.FirstOrDefault(m => m.Pid == iface.ProductId);
            if (model != null) { var d = OpenKnown(iface, model); if (d != null) list.Add(d); }
            else if (iface.ProductId == HYPERFLUX_V2) list.AddRange(OpenPad(iface));
            // keyboards, mats, headsets: OpenRGB's for now
        }
        return list;
    }

    static RazerHid? OpenKnown(HidNative.HidInfo iface, Model model)
    {
        HidNative.HidHandle hid;
        try { hid = HidNative.Open(iface.Path); }
        catch (Exception ex) { Log.Warn("Razer", $"{iface.ProductId:X4}: open failed: {ex.Message}"); return null; }
        var id = Identify(hid, model.Tid);
        if (id == null) Log.Info("Razer", $"{model.Name} ({iface.ProductId:X4}) did not answer (asleep?) - claimed anyway, frames retry as it wakes");
        else Log.Info("Razer", $"{model.Name} ({iface.ProductId:X4}) fw {id.Value.Fw} serial {id.Value.Serial} on transaction 0x{model.Tid:X2}");
        var dev = new RazerHid(hid, model, model.Tid) { Firmware = id?.Fw ?? "?", Serial = id?.Serial ?? "?" };
        dev.SetBrightness(0xFF);
        return dev;
    }

    /// <summary>The pad: ask every transaction id who is there. A reply that
    /// also answers a DPI query is the paired mouse; one that doesn't is the
    /// pad's own controller. Each gets its own handle so Dispose stays simple.</summary>
    static List<IRgbDevice> OpenPad(HidNative.HidInfo iface)
    {
        var list = new List<IRgbDevice>();
        HidNative.HidHandle probe;
        try { probe = HidNative.Open(iface.Path); }
        catch (Exception ex) { Log.Warn("Razer", $"HyperFlux V2 pad: open failed: {ex.Message}"); return list; }

        var answers = new List<(byte Tid, string Fw, string Serial, bool HasDpi)>();
        using (probe)
        {
            foreach (byte t in TransactionCandidates)
            {
                var id = Identify(probe, t);
                if (id == null) continue;
                var dq = NewReport(t, 0x04, 0x85, 0x07); dq[ARGS] = VARSTORE;
                var dpi = Exchange(probe, dq);
                answers.Add((t, id.Value.Fw, id.Value.Serial, dpi != null && dpi[1] == ST_OK));
            }
        }
        if (answers.Count == 0)
        {
            Log.Info("Razer", "HyperFlux V2 pad (00CF): no transaction id answered - mouse asleep or not paired; leaving it");
            return list;
        }
        Log.Info("Razer", "HyperFlux V2 pad answers: " + string.Join("; ", answers.Select(a => $"0x{a.Tid:X2} fw {a.Fw} serial {a.Serial}{(a.HasDpi ? " (mouse)" : " (pad)")}")));

        // One identity may answer on several ids (a dongle relaying everything):
        // keep the first id per serial.
        foreach (var a in answers.GroupBy(a => a.Serial).Select(g => g.First()))
        {
            HidNative.HidHandle hid;
            try { hid = HidNative.Open(iface.Path); }
            catch (Exception ex) { Log.Warn("Razer", $"HyperFlux V2: reopen failed: {ex.Message}"); continue; }

            Model model;
            string source = "known";
            if (a.HasDpi)
            {
                // The only pad-compatible mouse in the table; the frames'
                // status and the Test chase confirm the shape.
                model = BasiliskV3Pro with { Pid = HYPERFLUX_V2, Name = "Razer Basilisk V3 Pro (HyperFlux V2)", Tid = a.Tid };
            }
            else
            {
                int? probed = ProbeWidth(hid, a.Tid);
                (int count, source) = ResolveCount(HYPERFLUX_V2, probed);
                Log.Info("Razer", $"HyperFlux V2 pad strip: {count} LEDs ({source}{(probed is int p ? $", frame probe said {p}" : ", frame probe inconclusive")}) - adjust in Lighting > Razer… if the chase doesn't reach the end");
                model = new Model(HYPERFLUX_V2, "Razer HyperFlux V2 pad", a.Tid, 1, count, Kind.Pad, PadZones, PadPositions, 1.4f);
            }
            var dev = new RazerHid(hid, model, a.Tid) { Firmware = a.Fw, Serial = a.Serial, CountSource = source };
            dev.SetBrightness(0xFF);
            list.Add(dev);
        }
        return list;
    }

    static (string Fw, string Serial)? Identify(HidNative.HidHandle hid, byte tid)
    {
        var r = Exchange(hid, NewReport(tid, 0x00, 0x81, 0x02));
        if (r == null || r[1] != ST_OK) return null;
        string fw = $"v{r[ARGS]}.{r[ARGS + 1]}";
        var s = Exchange(hid, NewReport(tid, 0x00, 0x82, 0x16));
        string serial = s != null && s[1] == ST_OK ? Ascii(s, ARGS, 22) : "?";
        return (fw, serial);
    }

    /// <summary>Largest column the firmware accepts in a custom frame + 1, found
    /// by binary search on black frames (7 exchanges). Null when the firmware
    /// refuses nothing up to 64 or fails to answer — no information.</summary>
    static int? ProbeWidth(HidNative.HidHandle hid, byte tid)
    {
        bool? Accepts(int stop)
        {
            int start = Math.Max(0, stop - (MAX_LEDS_PER_PACKET - 1));
            var rep = CustomFrameReport(tid, 0, start, stop, new Rgb[stop - start + 1], 0);
            var r = Exchange(hid, rep);
            if (r == null) return null;
            return r[1] == ST_OK ? true : r[1] is ST_FAIL or ST_UNSUPPORTED ? false : null;
        }
        if (Accepts(0) != true) return null;
        if (Accepts(MaxLeds - 1) != false) return null;
        int lo = 0, hi = MaxLeds - 1;         // lo accepted, hi refused
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            var a = Accepts(mid);
            if (a == null) return null;
            if (a == true) lo = mid; else hi = mid;
        }
        return lo + 1;
    }

    /// <summary>Configured (hardware.json RazerLedCounts["00CF"]) beats probed
    /// beats the guess.</summary>
    internal static (int Count, string Source) ResolveCount(ushort pid, int? probed)
    {
        var counts = HardwareConfig.Load().RazerLedCounts;
        if (counts.TryGetValue($"{pid:X4}", out int n) && n > 0) return (Math.Clamp(n, 1, MaxLeds), "configured");
        if (probed is int p && p > 0) return (Math.Clamp(p, 1, MaxLeds), "probed");
        return (DefaultPadLeds, "guessed");
    }

    /// <summary>The vendor control collection: any Razer collection carrying
    /// the 90-byte feature report (91 with the id).</summary>
    static bool IsControlCollection(HidNative.HidInfo h) =>
        h.VendorId == VID && h.FeatureLength == FEATURE_LEN;

    /*-----------------------------------------------------*\
    | Lighting                                              |
    \*-----------------------------------------------------*/

    public void SetColors(IReadOnlyList<Rgb> colors)
    {
        if (colors.Count == 0) return;
        lock (_writeLock)
        {
            int n = LedCount;
            bool changed = _last == null;
            for (int i = 0; i < n; i++)
            {
                var c = colors[Math.Min(i, colors.Count - 1)];
                if (!changed && _frame[i] != c) changed = true;
                _frame[i] = c;
            }
            // A wireless mouse that dozed off wakes on its onboard effect; the
            // engine's keepalive reaches us every second, so re-send an
            // unchanged frame every 5 s (two feature reports) to take it back.
            long now = Environment.TickCount64;
            if (!changed && now - _lastSendTick < 5000) return;

            // A sleeping wireless mouse fails every write; don't hammer the
            // dongle at frame rate — retry every 2 s (the engine keepalive).
            if (_failures >= 3 && now < _nextRetryTick) return;

            bool ok = SendFrameOf(_frame, _model.Rows, wantReply: !_verified, out byte st);
            if (ok && !_verified)
            {
                if (st != ST_OK) { Log.Warn("Razer", $"{Name}: frame answered status 0x{st:X2} ({StatusName(st)})"); ok = false; }
                else { _verified = true; Log.Info("Razer", $"{Name}: custom frames accepted (transaction 0x{_tid:X2})"); }
            }
            if (ok)
            {
                _failures = 0;
                _lastSendTick = now;
                _last ??= new Rgb[n];
                Array.Copy(_frame, _last, n);
            }
            else
            {
                _last = null;
                if (++_failures == 3)
                    Log.Occasional($"razer:{Name}", "Razer", "frames not accepted (mouse asleep or protocol mismatch) - retrying every 2 s");
                _nextRetryTick = now + 2000;
            }
        }
    }

    /// <summary>One custom-frame packet per (row, ≤25-column chunk), then the
    /// "custom" effect so the device shows the buffer. With wantReply the
    /// replies are read and the worst status returned (the whole protocol
    /// verdict for a new device); otherwise fire-and-forget with a 1 ms gap.
    /// False = the write itself failed (handle gone / no reply).</summary>
    bool SendFrameOf(Rgb[] frame, int rows, bool wantReply, out byte status)
    {
        status = ST_OK;
        int cols = frame.Length / Math.Max(1, rows);
        for (int row = 0; row < rows; row++)
        {
            for (int start = 0; start < cols; start += MAX_LEDS_PER_PACKET)
            {
                int stop = Math.Min(cols - 1, start + MAX_LEDS_PER_PACKET - 1);
                var rep = CustomFrameReport(_tid, row, start, stop, frame, row * cols + start);
                if (!Send(rep, wantReply, out byte st)) return false;
                if (st != ST_OK) status = st;
            }
        }
        var apply = NewReport(_tid, 0x0F, 0x02, 0x0C);
        apply[ARGS + 2] = 0x08;   // effect: custom frame (NOSTORE, ZERO_LED)
        if (!Send(apply, wantReply, out byte st2)) return false;
        if (st2 != ST_OK) status = st2;
        return true;
    }

    /// <summary>Layout dialog: light LEDs 0..count-1 one at a time (white on
    /// black) so the user can see how many exist and in what order, then
    /// clear. Holds the write lock for the whole chase, so an effect channel
    /// simply waits and repaints afterwards. Returns the status text.</summary>
    public string TestChase(int count, int holdMs = 180)
    {
        count = Math.Clamp(count, 1, MaxLeds);
        var frame = new Rgb[count];
        lock (_writeLock)
        {
            byte worst = ST_OK;
            for (int i = 0; i < count; i++)
            {
                Array.Clear(frame);
                frame[i] = Rgb.White;
                if (!SendFrameOf(frame, 1, wantReply: true, out byte st)) { _last = null; return "no reply from the device"; }
                if (st != ST_OK && worst == ST_OK) worst = st;
                Thread.Sleep(holdMs);
            }
            Array.Clear(frame);
            SendFrameOf(frame, 1, wantReply: false, out _);
            _last = null;   // the next engine/static frame is re-sent whole
            return worst == ST_OK ? $"{count} LEDs accepted" : $"status 0x{worst:X2} ({StatusName(worst)}) on the way - the real count is lower";
        }
    }

    /// <summary>0x0F/0x04: brightness for the whole device, not stored.</summary>
    public bool SetBrightness(byte level)
    {
        var rep = NewReport(_tid, 0x0F, 0x04, 0x03);
        rep[ARGS] = NOSTORE; rep[ARGS + 1] = ZERO_LED; rep[ARGS + 2] = level;
        var r = Exchange(_hid, rep);
        return r != null && r[1] == ST_OK;
    }

    /*-----------------------------------------------------*\
    | Mouse settings (onboard, VARSTORE)                    |
    \*-----------------------------------------------------*/

    public (int X, int Y)? GetDpi()
    {
        var rep = NewReport(_tid, 0x04, 0x85, 0x07);
        rep[ARGS] = VARSTORE;
        var r = Exchange(_hid, rep);
        if (r == null || r[1] != ST_OK) return null;
        return (r[ARGS + 1] << 8 | r[ARGS + 2], r[ARGS + 3] << 8 | r[ARGS + 4]);
    }

    public bool SetDpi(int x, int y)
    {
        var r = Exchange(_hid, DpiReport(_tid, x, y));
        return r != null && r[1] == ST_OK;
    }

    /// <summary>Onboard DPI stages (what the DPI button cycles): active stage
    /// (1-based) and up to five X/Y pairs.</summary>
    public (int Active, (int X, int Y)[] Stages)? GetDpiStages()
    {
        var rep = NewReport(_tid, 0x04, 0x86, 0x26);
        rep[ARGS] = VARSTORE;
        var r = Exchange(_hid, rep);
        if (r == null || r[1] != ST_OK) return null;
        return DecodeDpiStages(r.AsSpan(ARGS, 80));
    }

    public bool SetDpiStages(int active, IReadOnlyList<(int X, int Y)> stages)
    {
        var r = Exchange(_hid, DpiStagesReport(_tid, active, stages));
        return r != null && r[1] == ST_OK;
    }

    /// <summary>Polling rate in Hz (125/500/1000), or null.</summary>
    public int? GetPollingRate()
    {
        var r = Exchange(_hid, NewReport(_tid, 0x00, 0x85, 0x01));
        if (r == null || r[1] != ST_OK) return null;
        return PollingHz(r[ARGS]);
    }

    public bool SetPollingRate(int hz)
    {
        byte code = PollingCode(hz);
        if (code == 0) return false;
        var rep = NewReport(_tid, 0x00, 0x05, 0x01);
        rep[ARGS] = code;
        var r = Exchange(_hid, rep);
        return r != null && r[1] == ST_OK;
    }

    /*-----------------------------------------------------*\
    | Battery (wireless models)                             |
    \*-----------------------------------------------------*/

    /// <summary>Charge and charging flag, or null when the device does not
    /// answer. Class 0x07: cmd 0x80 is the level, cmd 0x84 the charging flag,
    /// each in arguments[1] (openrazer's razer_chroma_misc_get_battery_level
    /// and _get_charging_status). The level is 0..255, not a percentage.
    ///
    /// A sleeping wireless mouse answers status 0x04 (timeout) and a wired one
    /// reports a raw 0, both of which are null here rather than 0%: openrazer
    /// reports 0 for gear with no measurable battery, and a low-battery rule
    /// firing every time the mouse naps would be worse than no reading.</summary>
    public BatteryReading? ReadBattery()
    {
        if (_model.Kind == Kind.Pad) return null;      // the pad runs off USB
        var level = Exchange(_hid, NewReport(_tid, 0x07, 0x80, 0x02));
        // Nothing usable came back: don't spend a second round trip on a mouse
        // that is asleep or has no battery to report.
        if (DecodeBattery(level, null) == null) return null;
        return DecodeBattery(level, Exchange(_hid, NewReport(_tid, 0x07, 0x84, 0x02)));
    }

    /// <summary>The two replies as a reading. A missing, short, or unsuccessful
    /// charging reply is not fatal: the level is the useful half, and "not
    /// charging" is the safe assumption.</summary>
    internal static BatteryReading? DecodeBattery(byte[]? level, byte[]? charging)
    {
        if (level == null || level.Length <= ARGS + 1 || level[1] != ST_OK) return null;
        if (level[ARGS + 1] == 0) return null;         // wired, or no battery to measure
        bool onCharger = charging != null && charging.Length > ARGS + 1
                         && charging[1] == ST_OK && charging[ARGS + 1] != 0;
        return new BatteryReading(ScaleCharge(level[ARGS + 1]), onCharger);
    }

    /// <summary>Razer's 0..255 charge as 0..100, rounded rather than truncated
    /// so a full battery reads 100 and not 99.</summary>
    internal static int ScaleCharge(byte raw) => (raw * 100 + 127) / 255;

    /// <summary>Battery 0-100, or null (wired / unsupported).</summary>
    public int? BatteryPercent()
    {
        var r = Exchange(_hid, NewReport(_tid, 0x07, 0x80, 0x02));
        if (r == null || r[1] != ST_OK) return null;
        return (int)Math.Round(r[ARGS + 1] * 100.0 / 255.0);
    }

    /// <summary>0x00/0x84: 0 = normal, 3 = driver mode (never set by us).</summary>
    public int? GetDeviceMode()
    {
        var r = Exchange(_hid, NewReport(_tid, 0x00, 0x84, 0x02));
        return r == null || r[1] != ST_OK ? null : r[ARGS];
    }

    public string DiagnosticInfo()
    {
        var sb = new StringBuilder();
        sb.Append($"pid {_model.Pid:X4} tid 0x{_tid:X2} fw {Firmware} serial {Serial} matrix {_model.Rows}x{_model.Cols} ({CountSource})");
        if (IsPad) return sb.ToString();
        var dpi = GetDpi(); var st = GetDpiStages(); var poll = GetPollingRate(); var bat = BatteryPercent(); var mode = GetDeviceMode();
        sb.Append(dpi is { } d ? $" dpi {d.X}x{d.Y}" : " dpi ?");
        if (st is { } s) sb.Append($" stages[{s.Active}/{s.Stages.Length}] " + string.Join(",", s.Stages.Select(p => p.X == p.Y ? $"{p.X}" : $"{p.X}x{p.Y}")));
        sb.Append(poll is { } p2 ? $" poll {p2} Hz" : " poll ?");
        if (bat is { } b) sb.Append($" battery {b}%");
        if (mode is { } m) sb.Append($" mode {m}");
        return sb.ToString();
    }

    /*-----------------------------------------------------*\
    | Diagnostics probe (read-only)                         |
    \*-----------------------------------------------------*/

    /// <summary>For the support bundle: every Razer control collection, asked
    /// for firmware/serial/mode/DPI/battery on each transaction id. Reads
    /// only. This is what tells us how a pad or dongle routes commands.</summary>
    public static string ProbeAll()
    {
        var sb = new StringBuilder();
        List<HidNative.HidInfo> all;
        try { all = HidNative.FindAll(); }
        catch (Exception ex) { return $"(HID enumeration failed: {ex.Message})"; }
        var seen = new HashSet<ushort>();
        foreach (var iface in all.Where(IsControlCollection).OrderBy(h => h.ProductId))
        {
            if (!seen.Add(iface.ProductId)) continue;
            string product = string.IsNullOrWhiteSpace(iface.Product) ? "" : $"  {iface.Product}";
            sb.AppendLine($"{VID:X4}:{iface.ProductId:X4}{product}  (usage 0x{iface.UsagePage:X4}/0x{iface.Usage:X4})");
            HidNative.HidHandle hid;
            try { hid = HidNative.Open(iface.Path); }
            catch (Exception ex) { sb.AppendLine($"    open failed: {ex.Message}"); continue; }
            using (hid)
            {
                foreach (byte t in TransactionCandidates)
                {
                    var fw = Exchange(hid, NewReport(t, 0x00, 0x81, 0x02));
                    if (fw == null) { sb.AppendLine($"    tid 0x{t:X2}: no reply"); continue; }
                    if (fw[1] != ST_OK) { sb.AppendLine($"    tid 0x{t:X2}: status 0x{fw[1]:X2} ({StatusName(fw[1])})"); continue; }
                    var line = new StringBuilder($"    tid 0x{t:X2}: fw v{fw[ARGS]}.{fw[ARGS + 1]}");
                    var s = Exchange(hid, NewReport(t, 0x00, 0x82, 0x16));
                    if (s != null && s[1] == ST_OK) line.Append($"  serial {Ascii(s, ARGS, 22)}");
                    var m = Exchange(hid, NewReport(t, 0x00, 0x84, 0x02));
                    if (m != null && m[1] == ST_OK) line.Append($"  mode {m[ARGS]}");
                    var dq = NewReport(t, 0x04, 0x85, 0x07); dq[ARGS] = VARSTORE;
                    var d = Exchange(hid, dq);
                    if (d != null && d[1] == ST_OK) line.Append($"  dpi {d[ARGS + 1] << 8 | d[ARGS + 2]}x{d[ARGS + 3] << 8 | d[ARGS + 4]}");
                    else if (d != null) line.Append($"  dpi: status 0x{d[1]:X2}");
                    var b = Exchange(hid, NewReport(t, 0x07, 0x80, 0x02));
                    if (b != null && b[1] == ST_OK) line.Append($"  battery {Math.Round(b[ARGS + 1] * 100.0 / 255.0)}%");
                    sb.AppendLine(line.ToString());
                }
            }
        }
        return sb.Length == 0 ? "(no Razer control collections)" : sb.ToString().TrimEnd();
    }

    /*-----------------------------------------------------*\
    | Wire helpers (internal for the harness)               |
    \*-----------------------------------------------------*/

    /// <summary>A zeroed 91-byte feature buffer: report id 0, then the 90-byte
    /// wire report with transaction id, class, command and data size set.
    /// Arguments go at index ARGS (9); Seal() writes the crc.</summary>
    internal static byte[] NewReport(byte tid, byte cls, byte cmd, byte dataSize)
    {
        var r = new byte[FEATURE_LEN];
        r[2] = tid; r[6] = dataSize; r[7] = cls; r[8] = cmd;
        return r;
    }

    /// <summary>crc = XOR of wire bytes 2..87 → wire byte 88.</summary>
    internal static void Seal(byte[] r)
    {
        byte crc = 0;
        for (int i = 2; i < 88; i++) crc ^= r[1 + i];
        r[1 + 88] = crc;
    }

    internal static byte[] CustomFrameReport(byte tid, int row, int startCol, int stopCol, IReadOnlyList<Rgb> colors, int colorOffset)
    {
        int n = stopCol - startCol + 1;
        var rep = NewReport(tid, 0x0F, 0x03, (byte)(5 + 3 * n));
        rep[ARGS + 2] = (byte)row; rep[ARGS + 3] = (byte)startCol; rep[ARGS + 4] = (byte)stopCol;
        for (int i = 0; i < n; i++)
        {
            var c = colors[colorOffset + i];
            rep[ARGS + 5 + 3 * i] = c.R; rep[ARGS + 6 + 3 * i] = c.G; rep[ARGS + 7 + 3 * i] = c.B;
        }
        return rep;
    }

    internal static byte[] DpiReport(byte tid, int x, int y)
    {
        x = Math.Clamp(x, 100, 45000); y = Math.Clamp(y, 100, 45000);
        var rep = NewReport(tid, 0x04, 0x05, 0x07);
        rep[ARGS] = VARSTORE;
        rep[ARGS + 1] = (byte)(x >> 8); rep[ARGS + 2] = (byte)x;
        rep[ARGS + 3] = (byte)(y >> 8); rep[ARGS + 4] = (byte)y;
        return rep;
    }

    /// <summary>0x04/0x06: [store, active (1-based), count, then per stage
    /// (number, Xhi, Xlo, Yhi, Ylo, 0, 0)], five stages at most.</summary>
    internal static byte[] DpiStagesReport(byte tid, int active, IReadOnlyList<(int X, int Y)> stages)
    {
        int count = Math.Clamp(stages.Count, 1, 5);
        var rep = NewReport(tid, 0x04, 0x06, 0x26);
        rep[ARGS] = VARSTORE;
        rep[ARGS + 1] = (byte)Math.Clamp(active, 1, count);
        rep[ARGS + 2] = (byte)count;
        for (int i = 0; i < count; i++)
        {
            int x = Math.Clamp(stages[i].X, 100, 45000), y = Math.Clamp(stages[i].Y, 100, 45000);
            int o = ARGS + 3 + 7 * i;
            rep[o] = (byte)(i + 1);
            rep[o + 1] = (byte)(x >> 8); rep[o + 2] = (byte)x;
            rep[o + 3] = (byte)(y >> 8); rep[o + 4] = (byte)y;
        }
        return rep;
    }

    internal static (int Active, (int X, int Y)[] Stages) DecodeDpiStages(ReadOnlySpan<byte> args)
    {
        int active = args[1];
        int count = Math.Min((int)args[2], 5);
        var stages = new (int, int)[count];
        for (int i = 0; i < count; i++)
        {
            int o = 3 + 7 * i;
            stages[i] = (args[o + 1] << 8 | args[o + 2], args[o + 3] << 8 | args[o + 4]);
        }
        return (active, stages);
    }

    internal static int PollingHz(byte code) => code switch { 0x01 => 1000, 0x02 => 500, 0x08 => 125, _ => 0 };
    internal static byte PollingCode(int hz) => hz switch { 1000 => 0x01, 500 => 0x02, 125 => 0x08, _ => 0 };
    internal static LedPos[] PadPositionsFor(int n) => PadPositions(n);

    static string StatusName(byte st) => st switch
    {
        ST_BUSY => "busy", ST_OK => "ok", ST_FAIL => "failure", ST_TIMEOUT => "timeout - device asleep?", ST_UNSUPPORTED => "not supported", _ => "unknown",
    };

    static string Ascii(byte[] r, int at, int len)
    {
        int end = at;
        while (end < at + len && end < r.Length && r[end] >= 0x20 && r[end] < 0x7F) end++;
        return Encoding.ASCII.GetString(r, at, end - at).Trim();
    }

    /*-----------------------------------------------------*\
    | Transport                                             |
    \*-----------------------------------------------------*/

    /// <summary>Send and read the matching reply (same class + command),
    /// retrying while the device reports busy. Null = no reply at all.</summary>
    static byte[]? Exchange(HidNative.HidHandle hid, byte[] req)
    {
        lock (Gate)
        {
            Seal(req);
            if (!hid.SetFeature(req)) return null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                Thread.Sleep(attempt == 0 ? 3 : 10);
                var resp = new byte[FEATURE_LEN];
                if (!hid.GetFeature(resp)) return null;
                if (resp[7] != req[7] || resp[8] != req[8]) continue;   // stale reply from an earlier command
                if (resp[1] == ST_BUSY) continue;
                return resp;
            }
            return null;
        }
    }

    /// <summary>Hot path: send; optionally read the reply status.</summary>
    bool Send(byte[] req, bool wantReply, out byte status)
    {
        status = ST_OK;
        if (!wantReply)
        {
            lock (Gate)
            {
                Seal(req);
                if (!_hid.SetFeature(req)) return false;
                Thread.Sleep(1);
                return true;
            }
        }
        var r = Exchange(_hid, req);
        if (r == null) return false;
        status = r[1];
        return true;
    }

    public void Dispose()
    {
        lock (_writeLock) _hid.Dispose();
    }
}

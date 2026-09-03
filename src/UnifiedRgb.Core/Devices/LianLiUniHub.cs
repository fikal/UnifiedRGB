using System.Text.Json;
using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Devices;

/// <summary>Lian Li UNI FAN SL-Infinity WIRED controller (USB HID 0CF2:A102).
/// Streamed live over USB. Protocol from OpenRGB's hardware-verified
/// LianLiUniHubSLInfinityController: per channel send three OUTPUT reports
/// (report id 0xE0, padded to 353 bytes) - start, colour (R,B,G wire order),
/// commit (static mode = display exactly what we send, brightness 0x00=100%);
/// LEDs power-limited to sum(R,B,G)<=460.
///
/// The per-fan LED count is NOT reported by the hub (OpenRGB makes the user
/// configure it too), so it's read from a hot-reloadable config file
/// (lianli-uni-layout.json) - innerPerFan/outerPerFan/fanCount, plus a `tune`
/// flag that paints a per-fan colour probe so the layout can be dialed in live
/// without rebuilding.</summary>
public sealed class LianLiUniHub : IRgbDevice, IZoneWritable, ILianFanDevice
{
    const ushort VID = 0x0CF2, PID = 0xA102;
    const byte TxId = 0xE0;
    const int Channels = 8, MaxPerChannel = 96, Pkt = 353;
    const byte ModeStatic = 0x01, Speed000 = 0x02, DirLtr = 0x00, Bright100 = 0x00;

    readonly HidNative.HidHandle _hid;
    readonly int _featLen;
    readonly object _lock = new();
    bool _disposed;   // set under _lock so no HID op touches a freed handle post-Rescan
    readonly Rgb[] _chan = new Rgb[Channels * MaxPerChannel];   // full per-channel buffer
    // False until the hub has received at least one commit from THIS instance.
    // _chan starts all-black, so the dedup in Write() saw a black first frame
    // (fans saved as off, LightsOff at launch) as "unchanged" and never sent
    // it - the hub kept playing its power-on effect until a non-black colour
    // was applied. Same class as the wireless driver's first-apply field bug.
    bool _primed;

    // SL-Infinity layout: fixed 8 inner + 12 outer per fan (L-Connect's
    // Ene6K77Fan.Constants). fanCount is the one thing the hub can't report, so
    // it defaults to the product max (4) - enough to light any SL-Infinity setup.
    // All three are overridable via an OPTIONAL config file (not auto-created);
    // `tune:true` there paints the per-fan colour probe for other layouts.
    const int Groups = 4, MaxFans = 4;   // 4 connectors, up to 4 fans daisy-chained each
    int _inner = 8, _outer = 12, _fans = 1;
    int _group;                          // active connector 0..3 (color ports 2g / 2g+1)
    bool _tune = false;
    int[] _populated = Array.Empty<int>();

    /// <summary>How many fans are daisy-chained on the ACTIVE connector. The hub
    /// can't report this (chained fans share one tach), so the app sets it from
    /// the saved per-channel count before detection. 1..4.</summary>
    public static int ConfiguredFanCount = 1;
    /// <summary>Which connector (0..3) the app is driving. Set before detection.</summary>
    public static int ConfiguredChannel = 0;
    public int FanCount => _fans;
    public int Channel => _group;
    /// <summary>Connectors with a spinning fan chain (from the tach read). These
    /// are the channels worth offering in the UI.</summary>
    public IReadOnlyList<int> PopulatedChannels => _populated;

    /// <summary>The live hub, so the cooling engine (SensorHub) can read RPM on it
    /// without threading a reference through detection. (Speed on this hub is
    /// motherboard-controlled via its fan cable; the L-Connect SetFanSpeed /
    /// SetFanMotherboardSync feature reports were mapped but never used and are
    /// gone - see git history if a wired-hub speed feature ever lands.)</summary>
    public static LianLiUniHub? Instance { get; private set; }
    int[]? _lastSpeeds;      // last per-group tach read, refreshed on the driver tick

    // ILianFanDevice: two parts per fan (no separately-addressable side).
    public int LianFanCount => _fans;
    public int LianLedsPerFan => _inner + _outer;
    // Cached: WPF re-reads bound properties on every notify, and these used to
    // allocate per get. Rebuilt in BuildLayout(), which runs on every change.
    IReadOnlyList<LianFanPart> _parts = Array.Empty<LianFanPart>();
    IReadOnlyList<string> _fanNames = Array.Empty<string>();
    public IReadOnlyList<LianFanPart> LianFanParts => _parts;
    public IReadOnlyList<string> LianFanNames => _fanNames;
    readonly string _cfg = AppPaths.Config("lianli-uni-layout.json");
    string _cfgSeen = "";
    DateTime _cfgStamp;                     // last-write time seen (absent file = year 1601)
    long _lastRpmTouch;                     // TickCount64 of the last GroupRpm read
    System.Threading.Timer? _timer;

    public string Name => "Lian Li SL-Infinity (wired)";
    public string Vendor => "Lian Li";
    public DeviceType Type => DeviceType.Fan;
    public int LedCount { get; private set; }
    public IReadOnlyList<RgbZone> Zones { get; private set; } = Array.Empty<RgbZone>();
    public IReadOnlyList<LedPos>? LedPositions => _positions;
    public float? PreviewAspect => 1f / Math.Max(1, _fans);
    LedPos[] _positions = Array.Empty<LedPos>();

    LianLiUniHub(HidNative.HidHandle hid, int featLen)
    {
        _hid = hid;
        _featLen = featLen > 0 ? featLen : 7;
        _fans = Math.Clamp(ConfiguredFanCount, 1, MaxFans);   // saved per-channel count
        _group = Math.Clamp(ConfiguredChannel, 0, Groups - 1);
        LoadCfg();                                      // optional file can still override
        // One tach read (feature report + 20 ms settle + GET_REPORT) seeds both
        // the RPM cache and the populated-connector list: a connector reading
        // non-zero has a spinning fan chain. Read-only and safe; it used to be
        // issued twice back-to-back on this synchronous detection path.
        var spd = ReadGroupSpeeds();
        _lastSpeeds = spd;
        _populated = spd == null ? Array.Empty<int>() : Enumerable.Range(0, Groups).Where(g => spd[g] > 0).ToArray();
        BuildLayout();
        Instance = this;
        _timer = new System.Threading.Timer(_ => Tick(), null, 1500, 1500);
    }

    public static LianLiUniHub? TryOpen()
    {
        var ifaces = HidNative.Find(VID, PID);
        if (ifaces.Count == 0) return null;
        foreach (var h in ifaces)
            Log.Info("LianLiUni", $"iface out={h.OutputLength} feat={h.FeatureLength} usage={h.UsagePage:X2}/{h.Usage:X2} '{h.Product}'");
        var r = HidNative.OpenFirst("LianLiUni", VID, PID, h => h.OutputLength >= Pkt, fallbackPick: _ => true);
        if (r == null) return null;
        var dev = new LianLiUniHub(r.Value.Handle, r.Value.Info.FeatureLength);
        Log.Info("LianLiUni", $"opened wired SL-Infinity: {dev.LedCount} LEDs (channel={dev._group + 1} fans={dev._fans} populated=[{string.Join(",", dev._populated.Select(g => g + 1))}] tune={dev._tune})");
        if (dev._tune) dev.Probe();
        return dev;
    }

    /// <summary>Per-group fan tach (L-Connect GetFanSpeed): feature cmd
    /// [E0 50 00], then a 65-byte input report whose data bytes are 4 big-endian
    /// 16-bit group RPMs. A group reading non-zero has a spinning fan chain on
    /// that connector - so this reveals WHICH channels are populated (not how
    /// many fans are daisy-chained on each: the hub doesn't report that, and
    /// L-Connect/OpenRGB both make the user set the per-chain quantity).</summary>
    public int[]? ReadGroupSpeeds()
    {
        try
        {
            if (_disposed) return null;   // hub torn down (Rescan) mid-poll
            if (!SendCmd(0x50, 0x00)) return null;
            var data = ReadInput();
            if (data == null) return null;
            var rpm = new int[4];
            for (int i = 0; i < 4; i++) rpm[i] = (data[i * 2] << 8) | data[i * 2 + 1];
            return rpm;
        }
        catch { return null; }
    }

    /// <summary>Send a short command as a feature report, padded to the device's
    /// feature-report length.</summary>
    bool SendCmd(byte a, byte b)
    {
        var buf = new byte[Math.Max(_featLen, 3)];
        buf[0] = TxId; buf[1] = a; buf[2] = b;
        bool ok = _hid.SetFeature(buf);
        System.Threading.Thread.Sleep(20);
        return ok;
    }

    /// <summary>Read the 65-byte input report (report id + 64 data) via control
    /// GET_REPORT and return the 64 data bytes (report id stripped).</summary>
    byte[]? ReadInput()
    {
        var buf = new byte[65];
        buf[0] = TxId;
        if (!_hid.GetInputReport(buf)) return null;
        var data = new byte[64];
        Array.Copy(buf, 1, data, 0, 64);
        return data;
    }

    /*----- live layout config -----*/
    void LoadCfg()
    {
        try
        {
            if (!File.Exists(_cfg)) { _cfgSeen = ""; return; }   // optional override only
            string txt = File.ReadAllText(_cfg);
            _cfgSeen = txt;
            using var d = JsonDocument.Parse(txt);
            var r = d.RootElement;
            if (r.TryGetProperty("innerPerFan", out var i)) _inner = Math.Clamp(i.GetInt32(), 0, MaxPerChannel);
            if (r.TryGetProperty("outerPerFan", out var o)) _outer = Math.Clamp(o.GetInt32(), 0, MaxPerChannel);
            if (r.TryGetProperty("fanCount", out var f)) _fans = Math.Clamp(f.GetInt32(), 1, MaxFans);
            if (r.TryGetProperty("channel", out var ch)) _group = Math.Clamp(ch.GetInt32(), 0, Groups - 1);
            if (r.TryGetProperty("tune", out var t)) _tune = t.GetBoolean();
            if (_inner + _outer == 0)
            {
                // Write() divides by LEDs-per-fan; zero would throw on every frame
                // until the engine's failure breaker silently killed the channel.
                Log.Warn("LianLiUni", "layout cfg: innerPerFan + outerPerFan is 0 - using the 8/12 defaults");
                _inner = 8; _outer = 12;
            }
        }
        catch (Exception ex) { Log.Warn("LianLiUni", $"layout cfg: {ex.Message}"); }
    }

    void BuildLayout()
    {
        int perFan = _inner + _outer;
        LedCount = _fans * perFan;
        var zones = new List<RgbZone>();
        var pos = new LedPos[LedCount];
        for (int f = 0; f < _fans; f++)
        {
            int b = f * perFan;
            // Whole fan + its two parts (inner ring, outer ring) - each a
            // selectable target in the zone picker, so a fan can be lit as one
            // colour or its inner/outer set independently.
            zones.Add(new RgbZone { Name = $"Fan {f + 1}", Offset = b, Count = perFan, IsFan = true });
            zones.Add(new RgbZone { Name = $"Fan {f + 1} · inner", Offset = b, Count = _inner });
            zones.Add(new RgbZone { Name = $"Fan {f + 1} · outer", Offset = b + _inner, Count = _outer });

            float cy = (f + 0.5f) / _fans;
            for (int l = 0; l < _inner; l++)   // inner ring: smaller radius
            {
                double a = 2 * Math.PI * l / Math.Max(1, _inner);
                pos[b + l] = new LedPos(0.5f + 0.22f * (float)Math.Cos(a), cy + (0.22f / _fans) * (float)Math.Sin(a));
            }
            for (int l = 0; l < _outer; l++)   // outer ring: larger radius
            {
                double a = 2 * Math.PI * l / Math.Max(1, _outer);
                pos[b + _inner + l] = new LedPos(0.5f + 0.45f * (float)Math.Cos(a), cy + (0.45f / _fans) * (float)Math.Sin(a));
            }
        }
        Zones = zones.ToArray(); _positions = pos;
        _parts = new[] { new LianFanPart("Inner ring", 0, _inner), new LianFanPart("Outer ring", _inner, _outer) };
        _fanNames = Enumerable.Range(1, _fans).Select(i => $"Fan {i}").ToList();
    }

    void Tick()
    {
        if (_disposed) return;
        // Tach poll only while someone is actually reading RPMs (the Cooling
        // panel). Unobserved, this used to block colour writes >=20 ms per
        // poll, 40x/min, forever.
        if (Environment.TickCount64 - System.Threading.Volatile.Read(ref _lastRpmTouch) < 10_000)
            RefreshSpeeds();
        try
        {
            // Hot-reload the optional layout file by TIMESTAMP - the old code
            // did a full File.ReadAllText every 1.5 s (~2,400 reads/hour) for
            // a file that virtually never exists. GetLastWriteTimeUtc returns
            // year 1601 for a missing file without throwing.
            var stamp = File.GetLastWriteTimeUtc(_cfg);
            if (stamp == _cfgStamp) return;
            _cfgStamp = stamp;
            if (stamp.Year < 1700) return;                 // file absent
            string txt = File.ReadAllText(_cfg);
            if (txt == _cfgSeen) return;
            LoadCfg();
            BuildLayout();
            Log.Info("LianLiUni", $"layout reloaded: inner={_inner} outer={_outer} fans={_fans} tune={_tune}");
            if (_tune) Probe();
        }
        catch { }
    }

    /// <summary>Refresh the cached per-group tach. Under the same lock as colour
    /// writes so the feature-report read never overlaps an output-report burst.</summary>
    void RefreshSpeeds()
    {
        try { lock (_lock) _lastSpeeds = ReadGroupSpeeds(); } catch { }
    }

    /// <summary>Last-read fan RPM for one connector (0 = stopped/absent).
    /// Reading it marks the tach as observed, which is what keeps the 1.5 s
    /// hardware poll running - stop reading and the poll stops too.</summary>
    public int GroupRpm(int group)
    {
        System.Threading.Volatile.Write(ref _lastRpmTouch, Environment.TickCount64);
        var s = _lastSpeeds;
        return s != null && group >= 0 && group < s.Length ? s[group] : 0;
    }

    /*----- probe: one colour per fan, inner + outer, so the count can be tuned -----*/
    void Probe()
    {
        // Discriminating probe: inner = dim white; outer FIRST half = red, outer
        // SECOND half = blue. Tells us whether the outer 12 splits into ring vs
        // side (separately codable = 3 parts) or is one continuous run (2 parts).
        var innerC = new Rgb(40, 40, 40);
        var oA = new Rgb(255, 0, 0);
        var oB = new Rgb(0, 0, 255);
        int innerPort = 2 * _group, outerPort = 2 * _group + 1;
        lock (_lock)
        {
            Array.Clear(_chan);
            for (int f = 0; f < _fans; f++)
            {
                for (int l = 0; l < _inner && f * _inner + l < MaxPerChannel; l++)
                    _chan[innerPort * MaxPerChannel + f * _inner + l] = innerC;
                for (int l = 0; l < _outer && f * _outer + l < MaxPerChannel; l++)
                    _chan[outerPort * MaxPerChannel + f * _outer + l] = l < _outer / 2 ? oA : oB;
            }
            Flush();
            _primed = true;
        }
    }

    /*----- normal operation: map a full device frame onto the two channels -----*/
    public void SetColors(IReadOnlyList<Rgb> colors) => Write(0, colors);
    public void SetZone(int offset, IReadOnlyList<Rgb> colors) => Write(offset, colors);

    void Write(int offset, IReadOnlyList<Rgb> colors)
    {
        if (_tune) return;   // tuning: the probe owns the display
        lock (_lock)
        {
            int perFan = _inner + _outer;
            int innerPort = 2 * _group, outerPort = 2 * _group + 1;
            bool changed = false;              // frame dedup: a static color used to
                                               // re-send 6 HID reports 60x/s forever
            for (int i = 0; i < colors.Count; i++)
            {
                int gi = offset + i;                       // device LED index
                int f = gi / perFan, within = gi % perFan;
                if (f >= _fans) break;
                // Bounds-guard every index: a fan count larger than what's
                // physically present just addresses LEDs that aren't there (inert,
                // never harmful), and the guard makes an over-large config
                // impossible to overrun a channel's 96-LED region.
                int slot;
                if (within < _inner)
                {
                    int idx = f * _inner + within;
                    if (idx >= MaxPerChannel) continue;
                    slot = innerPort * MaxPerChannel + idx;
                }
                else
                {
                    int idx = f * _outer + (within - _inner);
                    if (idx >= MaxPerChannel) continue;
                    slot = outerPort * MaxPerChannel + idx;
                }
                if (_chan[slot] != colors[i]) { _chan[slot] = colors[i]; changed = true; }
            }
            if (changed || !_primed)           // the hub latches its last commit
            {
                _primed = true;
                Flush();
            }
        }
    }

    void Flush()
    {
        if (_disposed) return;   // hub torn down (Rescan) mid-write
        // Only the ACTIVE connector's two ports (inner=2g, outer=2g+1) - other
        // connectors are left untouched so any fans there aren't blanked.
        SendChannel(2 * _group);
        SendChannel(2 * _group + 1);
    }

    // Reused wire packets (always called under _lock; every meaningful byte is
    // rewritten each call, trailing bytes stay zero) — this path used to
    // allocate 3 x 353 B per channel, twice per frame.
    readonly byte[] _pktStart = new byte[Pkt];
    readonly byte[] _pktCol = new byte[Pkt];
    readonly byte[] _pktCommit = new byte[Pkt];

    void SendChannel(int ch)
    {
        var start = _pktStart;
        start[0] = TxId; start[1] = 0x10; start[2] = 0x60; start[3] = (byte)(1 + ch / 2); start[4] = 0x04;
        _hid.Write(start);

        var col = _pktCol;
        col[0] = TxId; col[1] = (byte)(0x30 + ch);
        int p = 2;
        for (int l = 0; l < MaxPerChannel && p + 2 < col.Length; l++)
        {
            var c = _chan[ch * MaxPerChannel + l];
            int sum = c.R + c.G + c.B;
            float k = sum > 460 ? 460f / sum : 1f;
            col[p++] = (byte)(c.R * k); col[p++] = (byte)(c.B * k); col[p++] = (byte)(c.G * k);
        }
        _hid.Write(col);

        var commit = _pktCommit;
        commit[0] = TxId; commit[1] = (byte)(0x10 + ch);
        commit[2] = ModeStatic; commit[3] = Speed000; commit[4] = DirLtr; commit[5] = Bright100;
        _hid.Write(commit);
    }

    public void Dispose()
    {
        if (Instance == this) Instance = null;
        _timer?.Dispose();                  // stop future polls
        lock (_lock)                        // serialize with any in-flight read/write
        {
            _disposed = true;               // future ops (guarded) skip the HID handle
            _hid.Dispose();
        }
    }
}

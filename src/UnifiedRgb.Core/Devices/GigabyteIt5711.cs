using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Devices;

/// <summary>Gigabyte RGB Fusion 2.0 motherboard, ITE IT5711 controller
/// (048D:5711) — X870E AORUS MASTER X3D.
///
/// The AIO fan rings are per-LED addressable (8 inner-ring LEDs each, GRB
/// order). Header 2 drives fans 1+2 in parallel (splitter); header 4 drives
/// fan 3. Those two zones stream per-LED so the effects engine can animate
/// them. The remaining outputs (spare ARGB headers, LED_C, I/O cover, chipset)
/// are single-color and use the static-effect path.
///
/// Protocol ported from OpenRGB's GigabyteRGBFusion2USBController: 64-byte HID
/// feature reports, report ID 0xCC, on the usage-page 0xFF89 / usage 0x00CC
/// collection.</summary>
public sealed class GigabyteIt5711 : IRgbDevice, IZoneWritable, IHardwareModes
{
    readonly object _writeLock = new();
    // Control-report buffer for Cc/SendZoneEffect, reused under _writeLock the
    // way _streamPkt is (they ran per frame while a static zone was animated).
    readonly byte[] _ctlPkt = new byte[BUF];

    const ushort VID = 0x048D;
    static readonly ushort[] Pids = { 0x5711, 0x8297 };   // X870E gen / RGB Fusion 2 gen (X570 etc.)
    const ushort RGB_USAGE_PAGE = 0xFF89;
    const ushort RGB_USAGE = 0x00CC;
    const int BUF = 64;
    const byte REPORT_ID = 0xCC;
    const byte EFFECT_STATIC = 1;

    enum ZoneKind { Fan, Static }
    // Id: header number (1-4) for Fan zones; effect LED index for Static zones.
    // Order: RGB=0, GRB=1, BGR=2 wire order for fan streaming.
    sealed record ZoneDef(string Name, int Count, ZoneKind Kind, int Id, string Order = "GRB", bool Strip = false);

    // Header number -> static-effect LED index (for headers without an
    // addressable device configured).
    static readonly int[] HeaderEffectIdx = { 0, 5, 6, 7, 8 };

    /// <summary>Fan zones come from hardware.json (which ARGB headers host
    /// addressable devices, LED counts, wire order); the remaining headers and
    /// fixed outputs are single-color static zones.</summary>
    int MaxHeaders => _pid == 0x5711 ? 4 : 2;

    ZoneDef[] BuildZoneDefs()
    {
        var cfg = HardwareConfig.Load();
        var defs = new List<ZoneDef>();
        var fanHeaders = new HashSet<int>();
        foreach (var h in cfg.GigabyteArgbHeaders.Where(h => h.Header >= 1 && h.Header <= MaxHeaders && h.Leds is >= 1 and <= 256))
        {
            if (!fanHeaders.Add(h.Header)) continue;
            defs.Add(new ZoneDef(string.IsNullOrWhiteSpace(h.Name) ? $"ARGB Header {h.Header}" : h.Name,
                                 h.Leds, ZoneKind.Fan, h.Header, NormalizeOrder(h.ColorOrder, h.Header), h.Strip));
        }
        for (int header = 1; header <= MaxHeaders; header++)
            if (!fanHeaders.Contains(header))
                defs.Add(new ZoneDef($"ARGB Header {header}", 1, ZoneKind.Static, HeaderEffectIdx[header]));
        if (_pid == 0x5711)
        {
            defs.Add(new ZoneDef("LED_C (12V RGB)", 1, ZoneKind.Static, 4));
            defs.Add(new ZoneDef("I/O Cover",       1, ZoneKind.Static, 9));
            defs.Add(new ZoneDef("Chipset Accent",  1, ZoneKind.Static, 10));
        }
        else
        {
            // IT8297 boards (X570 GAMING X layout): LED_CPU + 12V RGB headers.
            defs.Add(new ZoneDef("LED_CPU",         1, ZoneKind.Static, 2));
            defs.Add(new ZoneDef("LED_C1/C2 (12V)", 1, ZoneKind.Static, 4));
        }
        return defs.ToArray();
    }

    static readonly string[] KnownOrders = { "RGB", "GRB", "BGR", "RBG", "GBR", "BRG" };

    /// <summary>hardware.json is hand-editable and OrderIdx's switch is ordinal:
    /// a typed "rgb" silently became GRB (red/green swapped, no warning).
    /// Canonicalise once here so ZoneDef.Order is always one of the six.
    /// Internal (not private) so the console harness can pin the mapping.</summary>
    internal static string NormalizeOrder(string? raw, int header)
    {
        string order = (raw ?? "GRB").Trim().ToUpperInvariant();
        if (KnownOrders.Contains(order)) return order;
        Log.Warn("GigabyteIt5711", $"hardware.json: header {header} ColorOrder '{raw}' not recognised - using GRB");
        return "GRB";
    }

    readonly HidNative.HidHandle _hid;
    readonly int[] _zoneOffset;
    readonly int _ledCount;
    readonly Rgb?[] _lastStatic = new Rgb?[16];
    bool _directInit;
    int _effectDisabled;

    readonly ushort _pid;
    public string Name { get; }
    public string Vendor => "Gigabyte";
    public DeviceType Type => DeviceType.Motherboard;
    public int LedCount => _ledCount;
    public IReadOnlyList<RgbZone> Zones { get; }

    readonly LedPos[] _positions;
    readonly ZoneDef[] ZoneDefs;
    public IReadOnlyList<LedPos>? LedPositions => _positions;
    public float? PreviewAspect => 1.35f;  // roughly the board outline

    /// <summary>Positions follow the configured zones: each fan zone is a ring
    /// (rings spread horizontally), static zones scatter around the board.</summary>
    LedPos[] BuildPositions()
    {
        var list = new List<LedPos>();
        int fanZones = ZoneDefs.Count(z => z.Kind == ZoneKind.Fan);
        int fanIdx = 0, staticIdx = 0;
        var staticSpots = new LedPos[]
        {
            new(0.10f, 0.10f), new(0.90f, 0.10f), new(0.50f, 0.88f),
            new(0.08f, 0.40f), new(0.50f, 0.15f), new(0.92f, 0.40f), new(0.30f, 0.90f),
        };
        foreach (var def in ZoneDefs)
        {
            if (def.Kind == ZoneKind.Fan)
            {
                float cx = fanZones <= 1 ? 0.5f : 0.28f + 0.44f * (fanIdx / (float)(fanZones - 1));
                fanIdx++;
                if (def.Strip)
                {
                    // A ribbon is a straight run: on a circle its two halves sit at
                    // the same heights, so every lengthwise effect comes out mirrored
                    // about the midpoint. One row instead - the zone then normalizes
                    // to a flat line and the strip-aware effects animate end to end.
                    for (int i = 0; i < def.Count; i++)
                        list.Add(new(def.Count <= 1 ? 0.5f : 0.05f + 0.90f * (i / (float)(def.Count - 1)), 0.5f));
                }
                else
                    for (int i = 0; i < def.Count; i++)
                    {
                        double a = i / (double)def.Count * Math.PI * 2 - Math.PI / 2;
                        list.Add(new((float)(cx + 0.20 * Math.Cos(a)), (float)(0.5 + 0.20 * Math.Sin(a))));
                    }
            }
            else
            {
                list.Add(staticSpots[Math.Min(staticIdx, staticSpots.Length - 1)]);
                staticIdx++;
            }
        }
        return list.ToArray();
    }

    GigabyteIt5711(HidNative.HidHandle hid, ushort pid)
    {
        _hid = hid;
        _pid = pid;
        Name = pid == 0x5711 ? "Gigabyte X870E AORUS MASTER X3D" : "Gigabyte Motherboard (IT8297)";
        ZoneDefs = BuildZoneDefs();          // fresh from hardware.json each open
        _positions = BuildPositions();
        _zoneOffset = new int[ZoneDefs.Length];
        var zones = new RgbZone[ZoneDefs.Length];
        int off = 0;
        for (int i = 0; i < ZoneDefs.Length; i++)
        {
            _zoneOffset[i] = off;
            zones[i] = new RgbZone { Name = ZoneDefs[i].Name, Offset = off, Count = ZoneDefs[i].Count,
                                     IsFan = ZoneDefs[i].Kind == ZoneKind.Fan };
            off += ZoneDefs[i].Count;
        }
        Zones = zones;
        _ledCount = off;
        ResetController();
    }

    public static GigabyteIt5711? TryOpen()
    {
        foreach (ushort pid in Pids)
        {
            var r = HidNative.OpenFirst("GigabyteIt5711", VID, pid,
                h => h.UsagePage == RGB_USAGE_PAGE && h.Usage == RGB_USAGE,
                fallbackPick: h => h.UsagePage == RGB_USAGE_PAGE);
            if (r != null) return new GigabyteIt5711(r.Value.Handle, pid);
        }
        return null;
    }

    bool Cc(byte a, byte b = 0, byte c = 0)
    {
        lock (_writeLock)   // re-entrant: the streaming callers already hold it
        {
            var buf = _ctlPkt;
            Array.Clear(buf);
            buf[0] = REPORT_ID; buf[1] = a; buf[2] = b; buf[3] = c;
            return _hid.SetFeature(buf);
        }
    }

    void ResetController()
    {
        for (byte reg = 0x20; reg <= 0x27; reg++) Cc(reg);
        if (_pid == 0x5711)
            for (byte reg = 0x90; reg <= 0x92; reg++) Cc(reg);
        ApplyEffect();
    }

    void ApplyEffect() => Cc(0x28, 0xFF, _pid == 0x5711 ? (byte)0x07 : (byte)0x00);

    static (byte Argb, int Mask) HeaderInfo(int header) => header switch
    {
        2 => (0x59, 0x02),
        3 => (0x62, 0x08),
        4 => (0x63, 0x10),
        _ => (0x58, 0x01),
    };

    static byte LedCountEnum(int c) => c <= 32 ? (byte)0 : c <= 64 ? (byte)1 : c <= 256 ? (byte)2 : c <= 512 ? (byte)3 : (byte)4;

    void SetLedCount(int c0, int c1, int c2, int c3)
    {
        byte d1 = LedCountEnum(c0), d2 = LedCountEnum(c1), d3 = LedCountEnum(c2), d4 = LedCountEnum(c3);
        Cc(0x34, (byte)((d2 << 4) | d1), (byte)((d4 << 4) | d3));
    }

    /*-----------------------------------------------------*\
    | Direct (per-LED) mode: set the fan-header LED counts   |
    | and disable their builtin effects, once.               |
    \*-----------------------------------------------------*/
    void EnsureDirectMode()
    {
        if (_directInit) return;
        var counts = new int[4];
        int mask = 0;
        foreach (var def in ZoneDefs)
            if (def.Kind == ZoneKind.Fan) { counts[def.Id - 1] = def.Count; mask |= HeaderInfo(def.Id).Mask; }
        SetLedCount(counts[0], counts[1], counts[2], counts[3]);
        Thread.Sleep(20);
        // Assign, don't OR: the mask is fully derived from ZoneDefs, and a
        // header-test bit OR'd in by SetHeaderLeds must not survive the
        // re-init that restores the tested header's built-in effect.
        _effectDisabled = mask;
        Cc(0x32, (byte)_effectDisabled);
        Thread.Sleep(20);
        _directInit = true;
    }

    /// <summary>Resolve a wire-order string to three channel selectors ONCE per
    /// stream call — the old per-LED-per-frame string switch ran ~60x/s per LED.</summary>
    static (int A, int B, int C) OrderIdx(string order) => order switch
    {
        "RGB" => (0, 1, 2),
        "BGR" => (2, 1, 0),
        "RBG" => (0, 2, 1),
        "GBR" => (1, 2, 0),
        "BRG" => (2, 0, 1),
        _ => (1, 0, 2),              // GRB (typical ARGB fans)
    };

    static byte Chan(Rgb c, int idx) => idx == 0 ? c.R : idx == 1 ? c.G : c.B;

    /// <summary>Stream per-LED colors to a fan header in the configured wire
    /// order, no count/effect re-setup — fast enough for animation. Reads
    /// directly out of the caller's list at an offset (no slice copy).</summary>
    readonly byte[] _streamPkt = new byte[BUF];   // reused: this runs per frame at 60fps

    void StreamHeaderColors(int header, IReadOnlyList<Rgb> src, int srcStart, int count, string order = "GRB")
    {
        var (argb, _) = HeaderInfo(header);
        var (ia, ib, ic) = OrderIdx(order);
        int k = 0;
        while (k < count)
        {
            int n = Math.Min(19, count - k);
            var pkt = _streamPkt;
            Array.Clear(pkt);
            pkt[0] = REPORT_ID; pkt[1] = argb;
            int byteOff = k * 3;
            pkt[2] = (byte)(byteOff & 0xFF); pkt[3] = (byte)((byteOff >> 8) & 0xFF);
            pkt[4] = (byte)(n * 3);
            for (int i = 0; i < n; i++)
            {
                int s = srcStart + k + i;
                var c = s >= 0 && s < src.Count ? src[s] : Rgb.Black;
                int o = 5 + i * 3;
                pkt[o] = Chan(c, ia); pkt[o + 1] = Chan(c, ib); pkt[o + 2] = Chan(c, ic);
            }
            _hid.SetFeature(pkt);
            k += n;
        }
    }

    /// <summary>colors[i] = color for LED i (see zone layout). Fan zones stream
    /// per-LED; single-color zones use the static effect.
    /// Statics + apply go FIRST: effect-register zones only take effect on the
    /// apply packet, so committing them before the (milliseconds-long) fan
    /// streaming keeps the chipset/IO accents in phase with the fans during
    /// animated effects instead of trailing by a write cycle.</summary>
    public void SetColors(IReadOnlyList<Rgb> colors) => WriteZones(0, colors, containedOnly: false);

    /// <summary>Update only the zones fully contained in [offset, offset+count),
    /// leaving all other zones on the hardware untouched.</summary>
    public void SetZone(int offset, IReadOnlyList<Rgb> colors) => WriteZones(offset, colors, containedOnly: true);

    /// <summary>The one statics-then-fans loop behind SetColors and SetZone.
    /// containedOnly restricts the write to zones fully inside the range
    /// (SetZone's contract); a full frame touches every zone, and a short one
    /// blanks the zones past its end through UpdateZone's bounds guard.</summary>
    void WriteZones(int offset, IReadOnlyList<Rgb> colors, bool containedOnly)
    {
        int end = offset + colors.Count;
        lock (_writeLock)
        {
            EnsureDirectMode();
            bool anyStatic = false;
            for (int z = 0; z < ZoneDefs.Length; z++)
                if (ZoneDefs[z].Kind != ZoneKind.Fan && Covered(z))
                    anyStatic |= UpdateZone(z, colors, _zoneOffset[z] - offset);
            if (anyStatic) ApplyEffect();
            for (int z = 0; z < ZoneDefs.Length; z++)
                if (ZoneDefs[z].Kind == ZoneKind.Fan && Covered(z))
                    UpdateZone(z, colors, _zoneOffset[z] - offset);
        }

        bool Covered(int z) => !containedOnly || (_zoneOffset[z] >= offset && _zoneOffset[z] + ZoneDefs[z].Count <= end);
    }

    // Per-zone dedup for FAN zones: a static color on an ARGB header used to
    // re-stream feature reports at 60 fps forever (statics were deduped, fans
    // were not — the exact gap the IRgbDevice contract warns about).
    readonly Dictionary<int, Rgb[]> _lastFan = new();

    /// <summary>Returns true when a static zone actually changed (needs apply).</summary>
    bool UpdateZone(int z, IReadOnlyList<Rgb> src, int srcStart)
    {
        var def = ZoneDefs[z];
        if (def.Kind == ZoneKind.Fan)
        {
            if (!_lastFan.TryGetValue(def.Id, out var last) || last.Length != def.Count)
                _lastFan[def.Id] = last = new Rgb[def.Count];
            else
            {
                bool same = true;
                for (int i = 0; i < def.Count; i++)
                {
                    int s = srcStart + i;
                    var c = s >= 0 && s < src.Count ? src[s] : Rgb.Black;
                    if (last[i] != c) { same = false; break; }
                }
                if (same) return false;
            }
            for (int i = 0; i < def.Count; i++)
            {
                int s = srcStart + i;
                last[i] = s >= 0 && s < src.Count ? src[s] : Rgb.Black;
            }
            StreamHeaderColors(def.Id, src, srcStart, def.Count, def.Order);
            return false;
        }
        if (srcStart < 0 || srcStart >= src.Count || _lastStatic[def.Id] == src[srcStart]) return false;
        _lastStatic[def.Id] = src[srcStart];
        SendZoneEffect(def.Id, src[srcStart]);
        return true;
    }

    void SendZoneEffect(int led, Rgb c) => SendZoneEffect(led, c, null);

    /// <summary>timing: optional (offset, value) overrides on the effect packet
    /// — used by the CLI fade probe to find the field that disables the
    /// firmware's smooth transition between static colors.</summary>
    public void SendZoneEffect(int led, Rgb c, (int Offset, byte Value)[]? timing)
    {
        lock (_writeLock)   // the CLI probe calls this unlocked; the frame path already holds it
        {
            FillZoneEffect(_ctlPkt, led, c, timing);
            _hid.SetFeature(_ctlPkt);
        }
    }

    /// <summary>The static-colour effect packet, filled into a reused buffer.
    /// One builder so the hardware-persistence path and the per-frame static
    /// path cannot drift apart: they are the same bytes, addressed by effect
    /// index.</summary>
    internal static void FillZoneEffect(byte[] pkt, int led, Rgb c, (int Offset, byte Value)[]? timing)
    {
        Array.Clear(pkt);
        pkt[0] = REPORT_ID;
        pkt[1] = (byte)(led < 8 ? 0x20 + led : 0x90 + (led - 8));
        uint zone0 = 1u << led;
        pkt[2] = (byte)(zone0 & 0xFF);
        pkt[3] = (byte)((zone0 >> 8) & 0xFF);
        pkt[11] = EFFECT_STATIC;
        pkt[12] = 0xFF;
        pkt[14] = c.B;
        pkt[15] = c.G;
        pkt[16] = c.R;
        if (timing != null)
            foreach (var (off, val) in timing)
                if (off is > 3 and < BUF) pkt[off] = val;
    }

    /// <summary>Buffer size for a control packet (the tests build one).</summary>
    internal const int PacketBytes = BUF;

    /// <summary>Effect index for a streamed header, which is NOT the header
    /// number it uses in direct mode. Getting these two confused would light
    /// the wrong output on the way out.</summary>
    internal static int EffectIndexOfHeader(int header) => HeaderEffectIdx[header];

    /// <summary>CLI probe support: commit pending zone effects.</summary>
    public void ApplyNow() { lock (_writeLock) ApplyEffect(); }

    /*-----------------------------------------------------*\
    | Hardware persistence: what the board shows once we     |
    | stop streaming.                                        |
    \*-----------------------------------------------------*/

    /// <summary>Static only. The board does have onboard effects, but which
    /// one RGB Fusion last saved is not readable, so "its own profile" would
    /// be a promise this driver cannot keep.</summary>
    public HardwareExitCaps ExitCaps => HardwareExitCaps.Static;
    public IReadOnlyList<string> HardwareEffects => Array.Empty<string>();
    public void SetHardwareEffect(string name, Rgb? color) { }
    public void ReturnToHardware() { }

    /// <summary>Leave every output on one colour with no host driving it.
    ///
    /// The fan headers are the interesting half: while we stream they are in
    /// direct mode with their onboard effects disabled, and direct mode stops
    /// the moment the process does. So they are switched back to the firmware's
    /// own static effect (each header has an effect index of its own) and the
    /// disable mask is cleared, which is what makes the colour survive.</summary>
    public void SetHardwareStatic(Rgb color)
    {
        lock (_writeLock)
        {
            foreach (var def in ZoneDefs)
            {
                // A streamed header is addressed by its effect index here, not
                // by the header number it uses in direct mode.
                int effectIdx = def.Kind == ZoneKind.Fan ? HeaderEffectIdx[def.Id] : def.Id;
                SendZoneEffect(effectIdx, color, null);
            }
            _effectDisabled = 0;
            Cc(0x32, 0x00);          // hand the headers back to the effect engine
            ApplyEffect();

            // The next launch must re-establish direct mode and repaint from
            // scratch: both caches now describe a board state we just replaced.
            _directInit = false;
            Array.Clear(_lastStatic);
            _lastFan.Clear();
        }
    }

    /*-----------------------------------------------------*\
    | Diagnostics (CLI): direct-stream + header scan.        |
    \*-----------------------------------------------------*/
    /// <summary>Light one header directly, bypassing the configured zones (the
    /// app's header-config 'Test' button and the CLI probes). Runs under the
    /// write lock — the effect thread streams frames on this handle
    /// concurrently — and leaves the device marked for re-init so the next
    /// frame repaints the header and restores its counts/effect mask.</summary>
    public void SetHeaderLeds(int header, IReadOnlyList<Rgb> colors, string order = "GRB")
    {
        if (header is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(header));
        lock (_writeLock)
        {
            var (_, mask) = HeaderInfo(header);
            // Keep the other headers' configured counts: zeroing them downgraded
            // a >32-LED header's count enum, and nothing re-sent it afterwards.
            var counts = new int[4];
            foreach (var def in ZoneDefs)
                if (def.Kind == ZoneKind.Fan) counts[def.Id - 1] = def.Count;
            counts[header - 1] = colors.Count;
            SetLedCount(counts[0], counts[1], counts[2], counts[3]);
            Thread.Sleep(20);
            _effectDisabled |= mask;
            Cc(0x32, (byte)_effectDisabled);
            Thread.Sleep(20);
            StreamHeaderColors(header, colors, 0, colors.Count, order);
            ApplyEffect();
            InvalidateHeader(header);
        }
    }

    public void TestAllHeaders(int ledsPerHeader, Rgb[] colorPerHeader)
    {
        lock (_writeLock)
        {
            SetLedCount(ledsPerHeader, ledsPerHeader, ledsPerHeader, ledsPerHeader);
            Thread.Sleep(20);
            _effectDisabled = 0x01 | 0x02 | 0x08 | 0x10;
            Cc(0x32, (byte)_effectDisabled);
            Thread.Sleep(30);
            for (int h = 1; h <= 4; h++)
            {
                var flat = Enumerable.Repeat(colorPerHeader[h - 1], ledsPerHeader).ToList();
                StreamHeaderColors(h, flat, 0, flat.Count);
                InvalidateHeader(h);
            }
            ApplyEffect();
        }
    }

    /// <summary>After a diagnostic write the dedup caches no longer describe
    /// the hardware: drop them so the next SetColors/SetZone repaints the
    /// header instead of deduping the frame away (a Test then Cancel left the
    /// ring white until the colour changed or a rescan), and force
    /// EnsureDirectMode to re-send the ZoneDefs counts and effect mask.</summary>
    void InvalidateHeader(int header)
    {
        _lastFan.Remove(header);
        _lastStatic[HeaderEffectIdx[header]] = null;
        _directInit = false;
    }

    public sealed record HeaderScan(int Header, int Segments, int[] SegmentLeds, int TotalLeds);

    public string DiagnosticInfo()
    {
        var rpt = new byte[BUF]; rpt[0] = REPORT_ID;
        Cc(0x60);
        bool ok = _hid.GetFeature(rpt);
        string product = System.Text.Encoding.ASCII.GetString(rpt, 12, 28).TrimEnd('\0', ' ');
        return ok
            ? $"read=OK product='{product}' device_num={rpt[2]} strip_detect={rpt[3]} " +
              $"support_cmd_flag=0x{rpt[11]:X2} led_count_hi={rpt[8]} led_count_lo={rpt[9]}"
            : "GetFeature FAILED";
    }

    public List<HeaderScan> ScanArgbHeaders()
    {
        var results = new List<HeaderScan>();
        var rpt = new byte[BUF]; rpt[0] = REPORT_ID;
        Cc(0x60);
        if (!_hid.GetFeature(rpt)) return results;

        bool gen2 = rpt[2] == 0 && (rpt[11] & 0x01) != 0 && rpt[3] == 0x01;
        if (!gen2) return results;

        int[] delta = { 4, 5, 0, 1 };
        const byte GEN2_LED_BASE_SCAN = 0x38;
        for (int slot = 0; slot < 4; slot++)
        {
            byte scanCmd = (byte)(GEN2_LED_BASE_SCAN + delta[slot]);
            Cc(scanCmd);
            Thread.Sleep(700);
            Cc((byte)(scanCmd + 2));
            var feat = new byte[BUF]; feat[0] = REPORT_ID;
            if (!_hid.GetFeature(feat)) continue;
            int segCount = feat[1];
            if (segCount is <= 0 or > 15) continue;
            var segs = new int[segCount];
            int total = 0;
            for (int k = 0; k < segCount; k++) { int cnt = feat[2 + k * 2] | (feat[3 + k * 2] << 8); segs[k] = cnt; total += cnt; }
            if (total > 0) results.Add(new HeaderScan(slot + 1, segCount, segs, total));
        }
        return results;
    }

    public void Dispose() => _hid.Dispose();
}

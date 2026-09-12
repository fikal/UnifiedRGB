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

    readonly IHidTransport _hid;
    readonly int[] _zoneOffset;
    readonly int _ledCount;
    readonly Rgb?[] _lastStatic = new Rgb?[16];
    bool _directInit;
    int _effectDisabled;
    /// <summary>A static zone's color only shows on the apply packet. When
    /// that packet is refused the effect registers already hold the new
    /// colors (their reports were accepted, and _lastStatic says so), so what
    /// must be re-sent next time is the apply alone - even if no static zone
    /// changes in that frame.</summary>
    bool _applyPending;
    /// <summary>The color of every LED as last requested, in device order.
    /// SetColors replaces it; SetZone merges a slice into it; the zone writers
    /// read from it. It exists so a SetZone that covers only PART of a zone
    /// (OpenRGB's single-LED SDK command, via LightingController.PushExternalFrame)
    /// can still repaint that zone whole - the hardware takes a header's LEDs
    /// only as a complete stream, and there is no other record of the LEDs the
    /// caller did not mention. Guarded by _writeLock like everything else.</summary>
    readonly Rgb[] _shadow;

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

    internal GigabyteIt5711(IHidTransport hid, ushort pid)
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
        _shadow = new Rgb[_ledCount];
        // One dedup buffer per fan header, allocated here so the failure path
        // in UpdateZone has nothing to allocate and nothing to insert.
        foreach (var def in ZoneDefs)
            if (def.Kind == ZoneKind.Fan) _fanLast[def.Id] = new Rgb[def.Count];
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

    /// <summary>Commit the effect registers. Returns whether the board took the
    /// packet: static zones do not show until it does, so the frame path has
    /// to know when to send it again.</summary>
    /// <summary>Every apply goes through here so the pending flag always
    /// reflects the LAST attempt, whoever made it: the hardware-persistence
    /// path and the CLI probe used to apply without clearing it, and the next
    /// frame then sent one apply the board had already taken.</summary>
    bool ApplyEffect()
    {
        bool ok = Cc(0x28, 0xFF, _pid == 0x5711 ? (byte)0x07 : (byte)0x00);
        _applyPending = !ok;
        return ok;
    }

    static (byte Argb, int Mask) HeaderInfo(int header) => header switch
    {
        2 => (0x59, 0x02),
        3 => (0x62, 0x08),
        4 => (0x63, 0x10),
        _ => (0x58, 0x01),
    };

    static byte LedCountEnum(int c) => c <= 32 ? (byte)0 : c <= 64 ? (byte)1 : c <= 256 ? (byte)2 : c <= 512 ? (byte)3 : (byte)4;

    bool SetLedCount(int c0, int c1, int c2, int c3)
    {
        byte d1 = LedCountEnum(c0), d2 = LedCountEnum(c1), d3 = LedCountEnum(c2), d4 = LedCountEnum(c3);
        return Cc(0x34, (byte)((d2 << 4) | d1), (byte)((d4 << 4) | d3));
    }

    /*-----------------------------------------------------*\
    | Direct (per-LED) mode: set the fan-header LED counts   |
    | and disable their builtin effects, once.               |
    \*-----------------------------------------------------*/
    /// <summary>False when the board refused either setup report. _directInit
    /// latches only on success: a refused count or mask used to be recorded
    /// as done, and the headers then streamed into a firmware effect that was
    /// never disabled (packets accepted, nothing showing) until a rescan.</summary>
    bool EnsureDirectMode()
    {
        if (_directInit) return true;
        var counts = new int[4];
        int mask = 0;
        foreach (var def in ZoneDefs)
            if (def.Kind == ZoneKind.Fan) { counts[def.Id - 1] = def.Count; mask |= HeaderInfo(def.Id).Mask; }
        if (!SetLedCount(counts[0], counts[1], counts[2], counts[3])) return false;
        Thread.Sleep(20);
        // Assign, don't OR: the mask is fully derived from ZoneDefs, and a
        // header-test bit OR'd in by SetHeaderLeds must not survive the
        // re-init that restores the tested header's built-in effect.
        _effectDisabled = mask;
        if (!Cc(0x32, (byte)_effectDisabled)) return false;
        Thread.Sleep(20);
        _directInit = true;
        return true;
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
    /// directly out of the caller's list at an offset (no slice copy).
    /// True only when the board accepted EVERY packet; the stream stops at the
    /// first refusal (each one can cost the 400 ms report timeout, and the
    /// caller re-sends the whole header next frame regardless).</summary>
    readonly byte[] _streamPkt = new byte[BUF];   // reused: this runs per frame at 60fps

    bool StreamHeaderColors(int header, IReadOnlyList<Rgb> src, int srcStart, int count, string order = "GRB")
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
            if (!_hid.SetFeature(pkt)) return false;
            k += n;
        }
        return true;
    }

    /// <summary>colors[i] = color for LED i (see zone layout). Fan zones stream
    /// per-LED; single-color zones use the static effect.
    /// Statics + apply go FIRST: effect-register zones only take effect on the
    /// apply packet, so committing them before the (milliseconds-long) fan
    /// streaming keeps the chipset/IO accents in phase with the fans during
    /// animated effects instead of trailing by a write cycle.
    /// A short frame blanks the FAN LEDs past its end and leaves the static
    /// zones past it as they are, which is what this driver has always done
    /// with them (a static zone past the end was skipped, never blacked).</summary>
    public bool SetColors(IReadOnlyList<Rgb> colors)
    {
        if (_disposed) return false;   // a write that outlives Dispose (a slow drain): refuse quietly, per the contract
        lock (_writeLock)
        {
            for (int z = 0; z < ZoneDefs.Length; z++)
            {
                var def = ZoneDefs[z];
                int off = _zoneOffset[z];
                for (int i = 0; i < def.Count; i++)
                {
                    int idx = off + i;
                    if (idx < colors.Count) _shadow[idx] = colors[idx];
                    else if (def.Kind == ZoneKind.Fan) _shadow[idx] = Rgb.Black;
                }
            }
            return WriteZones(0, _ledCount);
        }
    }

    /// <summary>Drop every cached zone so the next write goes out even if it
    /// is identical (the must-land path and any mode change). _directInit is
    /// deliberately left alone: the board's direct-mode setup is still valid,
    /// and re-running it costs two reports and a 40 ms settle for nothing.</summary>
    public void InvalidateCache()
    {
        lock (_writeLock)
        {
            Array.Clear(_lastStatic);
            Array.Clear(_fanValid);
        }
    }

    /// <summary>Update only LEDs [offset, offset+count), leaving every other
    /// LED on the hardware as it is. The slice is merged into the shadow frame
    /// and every zone it TOUCHES is repainted whole from there: a fan header
    /// takes its LEDs only as a complete stream, so a write to one LED inside
    /// an 8-LED ring re-streams the ring with the other seven unchanged. The
    /// old rule - repaint only zones fully inside the range - sent nothing at
    /// all for that write, and OpenRGB's single-LED command silently did
    /// nothing on this board.</summary>
    public bool SetZone(int offset, IReadOnlyList<Rgb> colors)
    {
        if (_disposed) return false;   // a write that outlives Dispose (a slow drain): refuse quietly, per the contract
        int start = Math.Max(0, offset);
        int end = Math.Min(_ledCount, offset + colors.Count);
        // An empty range asks for nothing, so nothing can have been refused:
        // that is a success, not a failure to deliver.
        if (end <= start) return true;
        lock (_writeLock)
        {
            for (int i = start; i < end; i++) _shadow[i] = colors[i - offset];
            return WriteZones(start, end);
        }
    }

    /// <summary>The one statics-then-apply-then-fans loop behind SetColors and
    /// SetZone: repaint, from the shadow, every zone that intersects the LED
    /// range [start, end). Caller holds _writeLock.
    ///
    /// Nothing here is cached until the board has accepted it. A refused
    /// packet used to be recorded as sent, so an identical frame later - the
    /// engine's keepalive re-send included - produced zero reports and the
    /// zone stayed on its old color until something changed.</summary>
    /// <summary>True only when every zone this call touched is now showing
    /// what the shadow says - each one either accepted its packets or was
    /// deduped because it already had them. Any refusal anywhere makes the
    /// whole call false, which is what lets a static apply or a lights-off
    /// know it has to try again.</summary>
    bool WriteZones(int start, int end)
    {
        if (!EnsureDirectMode())
            // Retried in full on the next call; nothing below was committed.
            return WritePolicy.Refused("gigabyte:init", "GigabyteIt5711",
                "direct-mode setup refused - the frame will be re-sent on the next call");
        bool landed = true, anyStatic = false;
        for (int z = 0; z < ZoneDefs.Length; z++)
            if (ZoneDefs[z].Kind != ZoneKind.Fan && Intersects(z))
            {
                landed &= UpdateZone(z, out bool needsApply);
                anyStatic |= needsApply;
            }
        if ((anyStatic || _applyPending) && !ApplyEffect())
        {
            // The effect registers hold the new color but nothing on the
            // board shows it until the apply lands, so this is a refusal of
            // the whole write, not a detail.
            landed = false;
            Log.Occasional("gigabyte:apply", "GigabyteIt5711",
                "apply packet refused - static zones will be committed on the next call");
        }
        for (int z = 0; z < ZoneDefs.Length; z++)
            if (ZoneDefs[z].Kind == ZoneKind.Fan && Intersects(z))
                landed &= UpdateZone(z, out _);
        return landed;

        bool Intersects(int z) => _zoneOffset[z] < end && _zoneOffset[z] + ZoneDefs[z].Count > start;
    }

    // Per-zone dedup for FAN zones: a static color on an ARGB header used to
    // re-stream feature reports at 60 fps forever (statics were deduped, fans
    // were not — the exact gap the IRgbDevice contract warns about).
    // Indexed by header number (1-4). _fanLast[h] is allocated once in the
    // constructor; _fanValid[h] says whether it describes what the board
    // shows, and is the thing invalidation clears.
    readonly Rgb[]?[] _fanLast = new Rgb[]?[5];
    readonly bool[] _fanValid = new bool[5];

    /// <summary>Repaint zone z from the shadow frame. Returns whether the zone
    /// is now showing the shadow - accepted, or deduped because it already
    /// was. <paramref name="needsApply"/> comes back true when a static zone's
    /// effect packet landed and therefore still needs the commit; a fan zone
    /// never needs one.
    ///
    /// The two answers used to be one bool meaning "needs an apply", which is
    /// false both when a zone was already correct and when its packet was
    /// refused - so nothing above could tell those apart. Dedup caches are
    /// still committed only after the board accepted the packets.</summary>
    bool UpdateZone(int z, out bool needsApply)
    {
        needsApply = false;
        var def = ZoneDefs[z];
        int off = _zoneOffset[z];
        if (def.Kind == ZoneKind.Fan)
        {
            var last = _fanLast[def.Id]!;
            if (_fanValid[def.Id])
            {
                bool same = true;
                for (int i = 0; i < def.Count; i++)
                    if (last[i] != _shadow[off + i]) { same = false; break; }
                if (same) return true;   // already showing it: a skip is a success
            }
            // Earlier chunks can land before a later report fails or throws.
            // The old frame then no longer describes the hardware, even if
            // the next request returns to exactly that color.
            _fanValid[def.Id] = false;
            if (!StreamHeaderColors(def.Id, _shadow, off, def.Count, def.Order))
                return WritePolicy.Refused($"gigabyte:stream:{def.Id}", "GigabyteIt5711",
                    $"header {def.Id} stream refused - will be re-sent on the next call");
            Array.Copy(_shadow, off, last, 0, def.Count);
            _fanValid[def.Id] = true;
            return true;
        }
        var c = _shadow[off];
        if (_lastStatic[def.Id] == c) return true;   // already showing it
        if (!SendZoneEffect(def.Id, c))
            return WritePolicy.Refused($"gigabyte:static:{def.Id}", "GigabyteIt5711",
                $"effect {def.Id} packet refused - will be re-sent on the next call");
        _lastStatic[def.Id] = c;
        needsApply = true;
        return true;
    }

    bool SendZoneEffect(int led, Rgb c) => SendZoneEffect(led, c, null);

    /// <summary>timing: optional (offset, value) overrides on the effect packet
    /// — used by the CLI fade probe to find the field that disables the
    /// firmware's smooth transition between static colors. Returns whether the
    /// board took the report.</summary>
    public bool SendZoneEffect(int led, Rgb c, (int Offset, byte Value)[]? timing)
    {
        lock (_writeLock)   // the CLI probe calls this unlocked; the frame path already holds it
        {
            FillZoneEffect(_ctlPkt, led, c, timing);
            return _hid.SetFeature(_ctlPkt);
        }
    }

    /// <summary>The static-color effect packet, filled into a reused buffer.
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
    // Neither is offered by ExitCaps, so HardwareExit never reaches them; the
    // honest answer to "did anything reach the board" is no.
    public bool SetHardwareEffect(string name, Rgb? color) => false;
    public bool ReturnToHardware() => false;

    /// <summary>Leave every output on one color with no host driving it.
    ///
    /// The fan headers are the interesting half: while we stream they are in
    /// direct mode with their onboard effects disabled, and direct mode stops
    /// the moment the process does. So they are switched back to the firmware's
    /// own static effect (each header has an effect index of its own) and the
    /// disable mask is cleared, which is what makes the color survive.</summary>
    public bool SetHardwareStatic(Rgb color)
    {
        if (_disposed) return false;   // a write that outlives Dispose (a slow drain): refuse quietly, per the contract
        lock (_writeLock)
        {
            // Every step is attempted even after a refusal - a board left with
            // some headers handed back and others still in direct mode is the
            // worst of the three states - but the verdict is the AND of all of
            // them, so an exit that must land sends the whole sequence again
            // rather than believing a partial handover.
            bool ok = true;
            foreach (var def in ZoneDefs)
            {
                // A streamed header is addressed by its effect index here, not
                // by the header number it uses in direct mode.
                int effectIdx = def.Kind == ZoneKind.Fan ? HeaderEffectIdx[def.Id] : def.Id;
                ok &= SendZoneEffect(effectIdx, color, null);
            }
            _effectDisabled = 0;
            ok &= Cc(0x32, 0x00);    // hand the headers back to the effect engine
            ok &= ApplyEffect();

            // The next launch must re-establish direct mode and repaint from
            // scratch: both caches now describe a board state we just replaced.
            _directInit = false;
            Array.Clear(_lastStatic);
            Array.Clear(_fanValid);
            return ok;
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
    /// ring white until the color changed or a rescan), and force
    /// EnsureDirectMode to re-send the ZoneDefs counts and effect mask.</summary>
    void InvalidateHeader(int header)
    {
        _fanValid[header] = false;
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

    // The contract's shape: the flag is set under the write lock, so a write
    // that arrives after this returns false instead of hitting a closed handle
    // and logging a "stopped answering" the device never earned.
    volatile bool _disposed;
    public void Dispose()
    {
        lock (_writeLock)
        {
            _disposed = true;
            _hid.Dispose();
        }
    }
}

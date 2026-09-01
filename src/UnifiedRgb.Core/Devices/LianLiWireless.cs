using System.Text.Json;
using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Devices;

/// <summary>Lian Li UNI FAN SL-INF Wireless fan group, driven natively through
/// the L-Wireless SLV3 transmitter (WinUSB 0416:8040). Protocol reverse-
/// engineered from L-Connect 3's lianli.slv3.dll and hardware-validated.
///
/// ⚠️ SAFETY (a receiver was once soft-bricked by an exploratory write):
///  - Detection NEVER writes to the device. Group addressing (fan MAC, master
///    MAC, channel) comes ONLY from L-Connect's savedDevices.config.
///  - The only bytes this class ever sends are L-Connect-identical static
///    color effect frames — the path proven harmless across many runs.
///  - Identical frames are skipped and sends are rate-limited.
///
/// Transport: 240-byte RF frames split into 4×64B bulk writes
/// [0x10, frag, channel, rxType, 60B]. Effect data = RGB triplets compressed
/// with tinyuz. Fans store the last effect in flash and keep playing it when
/// the host goes quiet. WinUSB is exclusive: if L-Connect's service is
/// running, TryOpen fails quietly and the fans just don't appear.</summary>
public sealed class LianLiWireless : IRgbDevice, IZoneWritable, ILianFanDevice
{
    static readonly Guid IfaceGuid = new("1D4B2365-4749-48EA-B38A-7C6FDDDD7E26");
    const string VidPid = "vid_0416&pid_8040";
    const string ConfigPath = @"C:\ProgramData\Lian-Li\L-Connect 3\slv3\config\savedDevices.config";
    const int LedsPerFan = 44, RfValidLen = 220;
    const int MinSendGapMs = 45;   // RF link pacing: a full frame takes ~60 ms anyway
    const int MaxAnimPackets = 24; // a whole upload must land in one RF burst to confirm; ~21 is proven
                                   // reliable, 64 never confirms. High-entropy effects (Lava, Fire) drop
                                   // frames until they fit - the retry only helps if a cycle can fully land.
    const int MaxAnimFrames = 160; // the receiver clamps the per-frame interval (~77ms), so long loops
                                   // need many frames at a small interval to play at the right speed;
                                   // InfRgbDat holds 1024 frames, so 160 is safe.
    const int MinAnimFrames = 8;   // don't degrade a loop below this many frames
    const int AnimConfirmTimeoutMs = 6000;   // keep resending until the fans echo the index, up to this
    // Live speed calibration: the hardware plays the baked loop at a rate we
    // can't predict exactly, so the user dials this until the fans match their
    // other devices. Multiplies the commanded per-frame interval (higher =
    // slower fans). Persisted by the app; changing it forces a re-upload.
    double _intervalScale = 1.4;
    public double IntervalScale
    {
        get => _intervalScale;
        set
        {
            double v = Math.Clamp(value, 0.3, 4.0);
            if (Math.Abs(v - _intervalScale) < 1e-6) return;
            _intervalScale = v;
            lock (_lock) _lastAnimHash = 0;   // force the next bake to re-upload at the new speed
        }
    }

    readonly WinUsbDevice _usb;
    readonly byte[] _fanMac, _masterMac;
    readonly byte _channel, _rxType;
    readonly int _fanNum;
    readonly object _lock = new();
    readonly Rgb[] _shadow;        // current full frame (zone writes merge here)
    uint _effectIndex;
    Rgb[]? _lastSent;
    long _lastSendMs;

    // Animation delivery, the L-Connect way: RF has no per-packet ack, so a lost
    // packet makes the receiver discard the whole effect and freeze on the old
    // frame. L-Connect never paces or hand-retries - it re-sends the effect every
    // sync cycle until the device's read-back effect_index (in the RX status page)
    // matches the one it wants, then stops. We do the same: keep _pending* set,
    // resend on each telemetry poll, and clear when the fans confirm the index.
    byte[]? _pendingData;
    int _pendingTotalFrame;
    double _pendingIntervalMs;
    readonly byte[] _pendingIndex = new byte[4];
    volatile bool _animPending;
    long _animDeadlineMs;
    int _reportedEffectIndex = -1;

    public string Name { get; }
    public string Vendor => "Lian Li";
    public DeviceType Type => DeviceType.Fan;
    public int LedCount => _fanNum * LedsPerFan;
    public IReadOnlyList<RgbZone> Zones { get; }
    public IReadOnlyList<LedPos>? LedPositions => _positions;

    /// <summary>When true, the effect engine renders this device for the
    /// preview only and does NOT stream frames to the hardware - a baked
    /// animation is playing on the receiver instead. Set by the app's baker.</summary>
    public bool SuppressStreaming { get; set; }

    // ILianFanDevice: three parts per fan (the SL-INF's real light zones).
    // Cached: WPF re-reads bound properties on every notify, and these used
    // to allocate a fresh array/LINQ list per get.
    public int LianFanCount => _fanNum;
    public int LianLedsPerFan => LedsPerFan;   // 44
    static readonly IReadOnlyList<LianFanPart> _parts = new[]
    {
        new LianFanPart("Center", 0, 8), new LianFanPart("Outer ring", 8, 20), new LianFanPart("Side glow", 28, 16),
    };
    public IReadOnlyList<LianFanPart> LianFanParts => _parts;
    IReadOnlyList<string>? _fanNames;
    public IReadOnlyList<string> LianFanNames => _fanNames ??=
        Zones.Where(z => z.Count == LedsPerFan).OrderBy(z => z.Offset).Select(z => z.Name).ToList();
    // Fans are STACKED (front-of-case column), fan 1 on top: whole-group
    // effects flow continuously down the stack.
    public float? PreviewAspect => 1f / Math.Max(1, _fanNum);

    readonly LedPos[] _positions;

    /*  Physical wire order per fan (what the RF frame carries):
     *    0-7 center · 8-17 inner-ring half 1 · 18-25 outer-ring half 1
     *    26-35 inner-ring half 2 · 36-43 outer-ring half 2
     *  (Hardware-confirmed: lighting 8-17 lit HALF the inner ring, and
     *  L-Connect's own scopes are Center / Inner(8-17+26-35) /
     *  Outer(18-25+36-43) - the fan has NO side strips, the "outer" is the
     *  infinity ring around the frame.)
     *
     *  The device presents a LOGICAL order instead, so every ring is one
     *  contiguous zone: 0-7 center, 8-27 inner ring (20), 28-43 outer ring
     *  (16). Transmit() maps logical -> physical.  */
    static readonly int[] LogToPhys = BuildLogToPhys();

    static int[] BuildLogToPhys()
    {
        var m = new int[LedsPerFan];
        for (int i = 0; i < 8; i++) m[i] = i;              // center
        for (int i = 0; i < 10; i++) m[8 + i] = 8 + i;     // inner half 1
        for (int i = 0; i < 10; i++) m[18 + i] = 26 + i;   // inner half 2
        for (int i = 0; i < 8; i++) m[28 + i] = 18 + i;    // outer half 1
        for (int i = 0; i < 8; i++) m[36 + i] = 36 + i;    // outer half 2
        return m;
    }

    // Slot s (top of the stack = slot 0) shows CHAIN fan _slotToChain[s].
    // The user arranges this in the app (fan 4 was physically on top); groups
    // let a future 6-fan setup split into separate effect canvases.
    readonly int[] _slotToChain;

    /// <summary>User arrangement: top-to-bottom chain order + group breaks.
    /// Written by the app (Arrange fans dialog), read at detection.</summary>
    public static (int[] Order, int[] Breaks) LoadLayout(int fanNum)
    {
        try
        {
            string path = AppPaths.Config("lianli-layout.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var order = doc.RootElement.GetProperty("order").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                int[] breaks = doc.RootElement.TryGetProperty("breaks", out var bk)
                    ? bk.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Array.Empty<int>();
                if (order.Length == fanNum && order.OrderBy(x => x).SequenceEqual(Enumerable.Range(0, fanNum)))
                    return (order, breaks.Where(x => x > 0 && x < fanNum).Distinct().OrderBy(x => x).ToArray());
            }
        }
        catch (Exception ex) { Log.Warn("LianLi", $"layout config: {ex.Message}"); }
        return (Enumerable.Range(0, fanNum).ToArray(), Array.Empty<int>());
    }

    LianLiWireless(WinUsbDevice usb, string groupName, byte[] fanMac, byte[] masterMac,
        byte channel, byte rxType, int fanNum)
    {
        _usb = usb; _fanMac = fanMac; _masterMac = masterMac;
        _channel = channel; _rxType = rxType; _fanNum = fanNum;
        _shadow = new Rgb[LedCount];
        Name = $"Lian Li {groupName}";
        (_slotToChain, var breaks) = LoadLayout(fanNum);

        var zones = new List<RgbZone>();
        for (int s = 0; s < fanNum; s++)
        {
            int b = s * LedsPerFan;
            string n = $"Fan {_slotToChain[s] + 1}";
            // Hardware-observed parts: the 20-LED group outlines the OUTSIDE
            // of the fan; the 16-LED group is the SIDE infinity-mirror glow.
            zones.Add(new RgbZone { Name = n, Offset = b, Count = LedsPerFan, IsFan = true });
            zones.Add(new RgbZone { Name = $"{n} - Center", Offset = b, Count = 8, IsFan = true });
            zones.Add(new RgbZone { Name = $"{n} - Outer ring", Offset = b + 8, Count = 20, IsFan = true });
            zones.Add(new RgbZone { Name = $"{n} - Side glow", Offset = b + 28, Count = 16 });
        }
        // Multiple groups: each becomes its own contiguous canvas zone so a
        // group-wide effect flows across just those fans.
        if (breaks.Length > 0)
        {
            var starts = new List<int> { 0 };
            starts.AddRange(breaks);
            starts.Add(fanNum);
            for (int g = 0; g + 1 < starts.Count; g++)
            {
                int s0 = starts[g], s1 = starts[g + 1];
                if (s1 <= s0) continue;
                string label = string.Join("+", Enumerable.Range(s0, s1 - s0).Select(s => _slotToChain[s] + 1));
                zones.Add(new RgbZone
                { Name = $"Group {g + 1} (fans {label})", Offset = s0 * LedsPerFan, Count = (s1 - s0) * LedsPerFan, IsFan = true });
            }
        }
        Zones = zones;
        _positions = BuildPositions();

        // Seed the effect index from the clock so the first frame differs from
        // whatever L-Connect sent last (the receiver ignores repeats).
        _effectIndex = (uint)Environment.TickCount;
        Instance = this;
    }

    LedPos[] BuildPositions()
    {
        var list = new List<LedPos>(LedCount);
        int n = _fanNum;
        for (int f = 0; f < n; f++)
        {
            float cy = (f + 0.5f) / n;
            float sy = 1f / n;   // y-scale keeps circles round under PreviewAspect
            void Ring(int count, float r)
            {
                for (int i = 0; i < count; i++)
                {
                    double a = i / (double)count * Math.PI * 2 - Math.PI / 2;
                    list.Add(new((float)(0.5 + r * Math.Cos(a)), (float)(cy + r * sy * Math.Sin(a))));
                }
            }
            Ring(8, 0.14f);    // center hub
            Ring(20, 0.44f);   // outer ring (outlines the fan)
            // Side glow: two vertical strips at the fan's edges.
            for (int i = 0; i < 8; i++)
                list.Add(new(0.04f, cy + sy * (-0.38f + i * (0.76f / 7))));
            for (int i = 0; i < 8; i++)
                list.Add(new(0.96f, cy + sy * (-0.38f + i * (0.76f / 7))));
        }
        return list.ToArray();
    }

    public static LianLiWireless? TryOpen()
    {
        // 1) Addressing from L-Connect's config — read-only, no device I/O.
        var cfg = ReadConfig();
        if (cfg == null) return null;

        // 2) The transmitter interface. Absent = no hardware; open failure =
        //    L-Connect owns it (WinUSB is exclusive) — both are quiet skips.
        string? path = WinUsbDevice.FindPath(IfaceGuid, VidPid);
        if (path == null) return null;
        var usb = WinUsbDevice.Open(path);
        if (usb == null)
        {
            Log.Info("LianLi", "SLV3 transmitter present but busy (L-Connect running?) - skipped");
            return null;
        }

        var c = cfg.Value;
        Log.Info("LianLi", $"'{c.Group}' fans={c.FanNum} ch={c.Channel} mac={Convert.ToHexString(c.FanMac)}");
        return new LianLiWireless(usb, c.Group, c.FanMac, c.MasterMac, c.Channel, c.RxType, c.FanNum);
    }

    record struct GroupCfg(string Group, byte[] FanMac, byte[] MasterMac, byte Channel, byte RxType, int FanNum);

    static GroupCfg? ReadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            foreach (var v in doc.RootElement.GetProperty("$values").EnumerateArray())
            {
                int fans = v.TryGetProperty("FanNum", out var fn) ? fn.GetInt32() : 0;
                if (fans <= 0) continue;
                byte[]? fanMac = ParseMac(v.GetProperty("MacStr").GetString());
                byte[]? masterMac = ParseMac(v.GetProperty("MasterMacStr").GetString());
                if (fanMac == null || masterMac == null) continue;
                byte channel = (byte)(v.TryGetProperty("channel", out var ch) ? ch.GetInt32() : 8);
                byte rxType = (byte)(v.TryGetProperty("rx_type", out var rt) ? rt.GetInt32() : 1);
                string group = v.TryGetProperty("GroupName", out var gn) && gn.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(gn.GetString()) ? gn.GetString()! : "SL-INF Wireless";
                return new GroupCfg(group, fanMac, masterMac, channel, rxType, fans);
            }
        }
        catch (Exception ex) { Log.Warn("LianLi", $"config parse failed: {ex.Message}"); }
        return null;
    }

    static byte[]? ParseMac(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var parts = s.Split(':');
        if (parts.Length != 6) return null;
        var m = new byte[6];
        for (int i = 0; i < 6; i++) m[i] = Convert.ToByte(parts[i], 16);
        return m;
    }

    /// <summary>Do the RF pacing wait BEFORE taking _lock: the sleep protects
    /// nothing, and holding the lock through it blocked UI-initiated writes
    /// (color-picker drags) for up to MinSendGapMs per streamed frame.
    /// Transmit() re-checks under the lock; any residual wait there is tiny.</summary>
    void PaceOutsideLock()
    {
        long wait = MinSendGapMs - (Environment.TickCount64 - System.Threading.Volatile.Read(ref _lastSendMs));
        if (wait > 0) Thread.Sleep((int)wait);
    }

    public void SetColors(IReadOnlyList<Rgb> colors)
    {
        if (colors.Count < LedCount) return;
        PaceOutsideLock();
        lock (_lock)
        {
            for (int i = 0; i < LedCount; i++) _shadow[i] = colors[i];
            Transmit();
        }
    }

    public void SetZone(int offset, IReadOnlyList<Rgb> colors)
    {
        PaceOutsideLock();
        lock (_lock)
        {
            for (int i = 0; i < colors.Count && offset + i < LedCount; i++)
                _shadow[offset + i] = colors[i];
            Transmit();
        }
    }

    /// <summary>Send the shadow frame as a one-frame static effect. Skips
    /// identical frames and paces the RF link. Caller holds _lock.</summary>
    void Transmit()
    {
        if (_lastSent != null && FramesEqual(_lastSent, _shadow)) return;
        _lastAnimHash = 0;   // a static frame interrupts any playing animation
        _animPending = false;   // stop resending the superseded animation
        // Pace the RF link by WAITING, never by dropping: a discarded frame is
        // a user action that silently never reaches the fans (field bug: first
        // apply after launch did nothing).
        long sinceLast = Environment.TickCount64 - _lastSendMs;
        long wait = MinSendGapMs - sinceLast;
        if (wait > 0) Thread.Sleep((int)wait);
        // Streaming (an effect feeding frames back-to-back)? The next frame
        // replaces this one in a moment and the settle resend insures the
        // final one - a double-send would just halve the animation rate.
        bool streaming = sinceLast < 400;
        _lastSendMs = Environment.TickCount64;
        _lastSent = (Rgb[])_shadow.Clone();

        var data = LianLiTinyuz.Encode(BuildWireBytes());

        // RF is one-way and data packets carry no ack: one lost packet makes
        // the receiver discard the whole effect and keep playing the OLD one
        // (field bug: the app showed the new color, the fans kept the last).
        // Standalone applies get a second immediate send; streams don't (the
        // next frame is the retry, and the settle resend covers the last one).
        SendEffect(data);
        if (!streaming)
        {
            Thread.Sleep(30);
            SendEffect(data);
        }

        // Interference is bursty, so even back-to-back retries can die
        // together. Insurance: once the state has SETTLED for half a second,
        // transmit it once more. Streams skip this (the shadow keeps
        // changing); the final resting state always gets an extra copy.
        (_settleTimer ??= new Timer(SettleResend)).Change(500, Timeout.Infinite);
    }

    Timer? _settleTimer;

    void SettleResend(object? _)
    {
        lock (_lock)
        {
            if (_lastSent == null) return;
            if (!FramesEqual(_lastSent, _shadow)) return;   // newer transmit is coming anyway
            try { SendEffect(LianLiTinyuz.Encode(BuildWireBytes())); }
            catch (Exception ex) { Log.Occasional("LianLi", "settle", $"settle resend failed: {ex.Message}"); }
        }
    }

    /// <summary>Logical shadow (slot order) -> physical wire order (chain
    /// order + per-fan ring interleave) as RGB bytes.</summary>
    byte[] BuildWireBytes()
    {
        var rgb = new byte[LedCount * 3];
        for (int s = 0; s < _fanNum; s++)
        {
            int bLog = s * LedsPerFan;
            int bPhys = _slotToChain[s] * LedsPerFan;
            for (int l = 0; l < LedsPerFan; l++)
            {
                int p = (bPhys + LogToPhys[l]) * 3;
                var c = _shadow[bLog + l];
                rgb[p] = c.R; rgb[p + 1] = c.G; rgb[p + 2] = c.B;
            }
        }
        return rgb;
    }

    /*-----------------------------------------------------*\
    | Multi-frame animation upload (the L-Connect model):    |
    | the whole animation is compressed and sent ONCE, then  |
    | the receiver loops it in hardware at `interval` per     |
    | frame. This replaces streaming single frames for smooth |
    | motion - streaming is capped by RF airtime (~8 fps),    |
    | hardware playback is smooth. Re-upload only on change.  |
    | frames[f] is a full logical (slot-order) LedCount grid. |
    \*-----------------------------------------------------*/
    long _lastAnimHash;

    public void UploadAnimation(IReadOnlyList<Rgb[]> frames, double frameMs)
    {
        if (frames.Count == 0) return;
        long hash = 17;
        foreach (var fr in frames)
            for (int i = 0; i < fr.Length; i++)
                hash = hash * 31 + (fr[i].R << 16 | fr[i].G << 8 | fr[i].B);
        hash = hash * 131 + (long)(frameMs * 16);
        lock (_lock)
        {
            if (hash == _lastAnimHash) return;
            _lastAnimHash = hash;
            _lastSent = null;                 // an animation invalidates the static-frame dedup

            // Adaptive: drop frames until the compressed upload fits a safe RF
            // packet budget. Every packet is one more chance for the 2.4 GHz link
            // to drop a frame, and a receiver missing any index rejects the whole
            // effect (empirically ~17 packets land, ~50 do not). We keep the total
            // loop duration constant, so fewer frames just means coarser motion.
            double totalMs = frames.Count * frameMs;
            // Cap frame count first: the receiver rejects an animation whose
            // decompressed size (frames x leds x 3) exceeds its effect buffer and
            // freezes on the old frame. A dense effect (Starfield) that packs into
            // few packets can still carry too many frames, so bound it up front.
            var cur = frames.Count > MaxAnimFrames ? Downsample(frames, MaxAnimFrames) : frames;

            // Fit the packet budget by, in order of least visible cost:
            //   1. keep full color if it already fits,
            //   2. drop color depth to 5- then 4-bit/channel (halves the packets
            //      on gradients/noise, imperceptible on these LEDs),
            //   3. only then drop frames (coarser motion).
            // A whole upload must land in one RF burst to confirm (~24 packets),
            // so this keeps 64 smooth frames for almost everything and only
            // coarsens the truly high-entropy effects (Fire, Spiral, Ribbon).
            byte[] data = LianLiTinyuz.Encode(BuildFrameBlob(cur, 0xFF));
            int mask = 0xFF, totalPk = Packets(data);
            foreach (int m in new[] { 0xF8, 0xF0 })
            {
                if (totalPk <= MaxAnimPackets) break;
                mask = m;
                data = LianLiTinyuz.Encode(BuildFrameBlob(cur, mask));
                totalPk = Packets(data);
            }
            while (totalPk > MaxAnimPackets && cur.Count > MinAnimFrames)
            {
                int target = Math.Max(MinAnimFrames, cur.Count * MaxAnimPackets / totalPk);
                if (target >= cur.Count) target = cur.Count - 1;
                cur = Downsample(cur, target);
                data = LianLiTinyuz.Encode(BuildFrameBlob(cur, mask));
                totalPk = Packets(data);
            }
            double ms = totalMs / cur.Count;
            Log.Info("LianLi", $"upload animation: {cur.Count}/{frames.Count} frames, {mask:X2}-mask, {data.Length}B, {totalPk} packets, {ms:0.0}ms/frame");

            // Arm read-back reconciliation: allocate a fresh index, stash the
            // effect, fire once now, and let the telemetry poll resend until the
            // fans report this index. Keep telemetry alive for the confirm loop.
            NextEffectIndex(_pendingIndex);
            _pendingData = data;
            _pendingTotalFrame = cur.Count;
            _pendingIntervalMs = ms;
            _animDeadlineMs = Environment.TickCount64 + AnimConfirmTimeoutMs;
            _animPending = true;
            SendEffect(data, cur.Count, ms, _pendingIndex);
        }
        TelemetryTouch();
    }

    int Packets(byte[] data) => (int)Math.Ceiling(data.Length / (double)RfValidLen);

    // Frame-major blob: [frame0: led0 rgb, led1 rgb, ...][frame1 ...], each LED
    // remapped from logical (slot) order to physical wire order. `mask` drops
    // low color bits (0xFF full, 0xF8 5-bit, 0xF0 4-bit) to aid compression.
    byte[] BuildFrameBlob(IReadOnlyList<Rgb[]> frames, int mask)
    {
        byte m = (byte)mask;
        var blob = new byte[frames.Count * LedCount * 3];
        int w = 0;
        foreach (var fr in frames)
        {
            for (int s = 0; s < _fanNum; s++)
            {
                int bLog = s * LedsPerFan, bPhys = _slotToChain[s] * LedsPerFan;
                for (int l = 0; l < LedsPerFan; l++)
                {
                    int p = w + (bPhys + LogToPhys[l]) * 3;
                    var c = fr[bLog + l];
                    blob[p] = (byte)(c.R & m); blob[p + 1] = (byte)(c.G & m); blob[p + 2] = (byte)(c.B & m);
                }
            }
            w += LedCount * 3;
        }
        return blob;
    }

    // Pick `k` frames evenly spread across the loop (keeps frame 0 and the last).
    static IReadOnlyList<Rgb[]> Downsample(IReadOnlyList<Rgb[]> src, int k)
    {
        if (k >= src.Count) return src;
        var outp = new Rgb[k][];
        for (int i = 0; i < k; i++)
            outp[i] = src[k == 1 ? 0 : (int)Math.Round(i * (src.Count - 1.0) / (k - 1))];
        return outp;
    }

    // Allocate the next effect index into a 4-byte buffer (the hardware requires
    // a new index per effect; it echoes the adopted index back in its status).
    void NextEffectIndex(byte[] dst)
    {
        uint idx = ++_effectIndex;
        dst[0] = (byte)(idx >> 24); dst[1] = (byte)(idx >> 16); dst[2] = (byte)(idx >> 8); dst[3] = (byte)idx;
    }

    // Called from the telemetry poll after reading the fans' current effect
    // index. If an animation upload is outstanding, confirm it (index matches) or
    // resend it (still missing) until the deadline - the L-Connect sync model.
    void ReconcileAnimation()
    {
        if (!_animPending) return;
        int want = _pendingIndex[0] << 24 | _pendingIndex[1] << 16 | _pendingIndex[2] << 8 | _pendingIndex[3];
        if (_reportedEffectIndex == want)
        {
            _animPending = false;
            Log.Info("LianLi", "animation confirmed by fans");
            return;
        }
        if (Environment.TickCount64 > _animDeadlineMs)
        {
            _animPending = false;
            Log.Occasional("LianLi", "animtimeout", "animation not confirmed before timeout - giving up resends");
            return;
        }
        lock (_lock)
            if (_animPending && _pendingData != null)
                SendEffect(_pendingData, _pendingTotalFrame, _pendingIntervalMs, _pendingIndex);
    }

    void SendEffect(byte[] data) => SendEffect(data, 1, 200, NextEffectIndexBuf());

    byte[] NextEffectIndexBuf() { var b = new byte[4]; NextEffectIndex(b); return b; }

    void SendEffect(byte[] data, int totalFrame, double intervalMs, byte[] effectIndex)
    {
        // The receiver plays each frame in HALF the interval it's given (measured:
        // 64f/140ms and 150f/60ms both loop in ~4.5s = commanded/2). Send double so
        // the real per-frame time matches what we intend and the fans run at the
        // right speed / in sync with streamed devices.
        double hwInterval = intervalMs * _intervalScale;
        int interval = (int)hwInterval, intervalFrac = (int)(hwInterval * 100 % 100);
        const int subInterval = 0, totalSubFrame = 0;
        byte totalPk = (byte)Math.Ceiling(data.Length / (double)RfValidLen);

        int offset = 0; byte index = 0;
        while (index == 0 || offset < data.Length)
        {
            var rf = new byte[240];
            rf[0] = 0x12; rf[1] = 0x20;
            Array.Copy(_fanMac, 0, rf, 2, 6);
            Array.Copy(_masterMac, 0, rf, 8, 6);
            Array.Copy(effectIndex, 0, rf, 14, 4);
            rf[18] = index;
            rf[19] = (byte)(totalPk + 1);
            if (index == 0)
            {
                int l = data.Length;
                rf[20] = (byte)(l >> 24); rf[21] = (byte)((l >> 16) & 0xFF);
                rf[22] = (byte)((l >> 8) & 0xFF); rf[23] = (byte)(l & 0xFF);
                rf[24] = 0;
                rf[25] = (byte)(totalFrame >> 8); rf[26] = (byte)(totalFrame & 0xFF);
                rf[27] = (byte)LedCount;
                rf[32] = (byte)(interval >> 8); rf[33] = (byte)(interval & 0xFF); rf[34] = (byte)intervalFrac;
                rf[35] = subInterval >> 8; rf[36] = subInterval & 0xFF;
                rf[37] = 1;   // isOuterMatchMax
                rf[38] = totalSubFrame >> 8; rf[39] = totalSubFrame & 0xFF;
            }
            else
            {
                int n = Math.Min(RfValidLen, data.Length - offset);
                Array.Copy(data, offset, rf, 20, n);
                offset += RfValidLen;
            }
            SendRf(rf);
            if (index == 0)
            {
                // Meta packet goes out 4x, 20 ms apart (L-Connect-identical).
                for (int k = 0; k < 3; k++) { Thread.Sleep(20); SendRf(rf); }
            }
            // No pacing on data packets - L-Connect sends them back-to-back and
            // relies on the effect_index read-back retry to cover any lost packet.
            index++;
        }
    }

    readonly byte[] _rfPkt = new byte[64];   // reused fragment (all callers hold _lock)

    void SendRf(byte[] rf)
    {
        byte frag = 0;
        var pkt = _rfPkt;
        for (int off = 0; off < rf.Length; off += 60)
        {
            int n = Math.Min(60, rf.Length - off);
            pkt[0] = 0x10; pkt[1] = frag++; pkt[2] = _channel; pkt[3] = _rxType;
            Array.Copy(rf, off, pkt, 4, n);
            if (n < 60) Array.Clear(pkt, 4 + n, 60 - n);   // stale tail from a longer fragment
            if (!_usb.Write(_usb.BulkOutPipe, pkt))
                Log.Occasional("LianLi", "write", "bulk write failed");
        }
    }

    static bool FramesEqual(Rgb[] a, Rgb[] b)
    {
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /*-----------------------------------------------------*\
    | Telemetry: fan RPMs polled from the RECEIVER interface |
    | (SLV3RX 0416:8041) with the query L-Connect sends once |
    | a second. NEVER send this to the transmitter - 0x10 is |
    | an RF fragment marker there (the 8/17 receiver brick). |
    | Touch-driven: polls only while something is watching.  |
    \*-----------------------------------------------------*/
    const string RxVidPid = "vid_0416&pid_8041";

    public static LianLiWireless? Instance { get; private set; }

    WinUsbDevice? _rx;
    Thread? _telemetryThread;
    long _telemetryTouch;
    volatile int[]? _rpmByChain;

    /// <summary>Keep RPM polling alive; call from every cooling refresh.</summary>
    public void TelemetryTouch()
    {
        Interlocked.Exchange(ref _telemetryTouch, Environment.TickCount64);
        if (_telemetryThread != null) return;
        lock (_lock)
        {
            if (_telemetryThread != null) return;
            _telemetryThread = new Thread(TelemetryLoop) { IsBackground = true, Name = "lianli-telemetry" };
            _telemetryThread.Start();
        }
    }

    /// <summary>Latest RPMs in the user's arranged slot order, or null.</summary>
    int[]? _rpmBySlotCache;
    public int[]? FanRpmsBySlot
    {
        get
        {
            var r = _rpmByChain;
            if (r == null) return null;
            // Refilled in place: callers copy values out (nothing binds the
            // array itself), and this used to allocate per UI refresh.
            var o = _rpmBySlotCache ??= new int[_fanNum];
            for (int s = 0; s < _fanNum; s++) o[s] = _slotToChain[s] < r.Length ? r[_slotToChain[s]] : 0;
            return o;
        }
    }

    public string FanNameAtSlot(int s) => $"Fan {_slotToChain[Math.Clamp(s, 0, _fanNum - 1)] + 1}";

    public int FanCount => _fanNum;

    /// <summary>Chain index of the fan at an arranged slot (stable key for
    /// saved fan modes across re-arrangements).</summary>
    public int ChainOf(int slot) => _slotToChain[Math.Clamp(slot, 0, _fanNum - 1)];

    void TelemetryLoop()
    {
        while (Environment.TickCount64 - Interlocked.Read(ref _telemetryTouch) < 5000 || _animPending)
        {
            try { PollTelemetry(); }
            catch (Exception ex) { Log.Occasional("LianLi", "telemetry", $"telemetry poll failed: {ex.Message}"); }
            // Poll fast while an animation upload awaits confirmation (drives the
            // resend), slow otherwise (RPM/PWM only need a lazy refresh).
            Thread.Sleep(_animPending ? 150 : 1500);
        }
        lock (_lock)
        {
            _rx?.Dispose();
            _rx = null;
            _telemetryThread = null;
        }
    }

    void PollTelemetry()
    {
        if (_rx == null)
        {
            string? path = WinUsbDevice.FindPath(IfaceGuid, RxVidPid);
            if (path == null) return;
            _rx = WinUsbDevice.Open(path);
            if (_rx == null || _rx.BulkInPipe == 0) { _rx?.Dispose(); _rx = null; return; }
        }

        // GetDev(0x10, 1 page) - L-Connect-identical, to the RX only.
        var q = new byte[64];
        q[0] = 0x10; q[1] = 1;
        if (!_rx.Write(_rx.BulkOutPipe, q)) { _rx.Dispose(); _rx = null; return; }

        var page = new byte[434];
        int got = _rx.Read(page);
        if (got < 46 || page[0] != 0x10) return;

        // 42-byte records after the 4-byte header; find our fan group by MAC.
        for (int rec = 4; rec + 42 <= got; rec += 42)
        {
            bool match = true;
            for (int i = 0; i < 6; i++) if (page[rec + i] != _fanMac[i]) { match = false; break; }
            if (!match || page[rec + 18] == 0xFF) continue;   // 0xFF = the controller itself
            var rpms = new int[4];
            for (int f = 0; f < 4; f++)
            {
                // High nibbles carry status flags / firmware version bits.
                int hi = page[rec + 28 + f * 2] & 0x0F;
                rpms[f] = (hi << 8) + page[rec + 28 + f * 2 + 1];
            }
            _rpmByChain = rpms;
            var pwm = new byte[4];
            Array.Copy(page, rec + 36, pwm, 0, 4);
            _pwmByChainTele = pwm;

            // Effect index the fans are actually playing (RX record offset +20).
            _reportedEffectIndex = page[rec + 20] << 24 | page[rec + 21] << 16 | page[rec + 22] << 8 | page[rec + 23];
            ReconcileAnimation();
            return;
        }
    }

    /*-----------------------------------------------------*\
    | Fan PWM control (hardware-validated 8/21): RF frame    |
    | 0x12 0x10 with fanMac, masterMac, rx/channel UNCHANGED |
    | and slaveIndex=1 (NEVER 0 - that unbinds!), plus 4 PWM |
    | bytes (0-255; 6 and 153-155 reserved). The fans latch  |
    | only after several assertions land, so a worker keeps  |
    | sending until telemetry echoes the target, then stops. |
    | Once latched the value persists (like the RGB).        |
    \*-----------------------------------------------------*/
    volatile byte[]? _pwmByChainTele;
    readonly byte[] _pwmTarget = new byte[4];
    bool _pwmSeeded;
    volatile bool _pwmDirty;
    Thread? _pwmThread;

    /// <summary>Current duty percent per arranged slot (from telemetry), or null.</summary>
    public int[]? FanDutyBySlot
    {
        get
        {
            var p = _pwmByChainTele;
            if (p == null) return null;
            var o = new int[_fanNum];
            for (int s = 0; s < _fanNum; s++) o[s] = p[_slotToChain[s]] * 100 / 255;
            return o;
        }
    }

    /// <summary>Set one fan's duty (percent, by arranged slot). Asserts over
    /// RF until the receiver confirms; other fans keep their current duty.</summary>
    public void SetFanDuty(int slot, int percent)
    {
        byte d = (byte)Math.Clamp(percent * 255 / 100, 0, 255);
        if (d == 6) d = 7;
        if (d >= 153 && d <= 155) d = 156;
        lock (_lock)
        {
            // First touch: seed untouched fans from telemetry so setting one
            // fan never yanks the other three.
            if (!_pwmSeeded)
            {
                var tele = _pwmByChainTele;
                for (int i = 0; i < 4; i++) _pwmTarget[i] = tele != null ? tele[i] : (byte)0x66;
                _pwmSeeded = true;
            }
            _pwmTarget[_slotToChain[Math.Clamp(slot, 0, _fanNum - 1)]] = d;
        }
        _pwmDirty = true;
        TelemetryTouch();
        if (_pwmThread == null)
            lock (_lock)
                if (_pwmThread == null)
                {
                    _pwmThread = new Thread(PwmLoop) { IsBackground = true, Name = "lianli-pwm" };
                    _pwmThread.Start();
                }
    }

    void PwmLoop()
    {
        int confirms = 0, attempts = 0;
        while (attempts < 120)   // hard cap ~1 min
        {
            attempts++;
            if (_pwmDirty) { _pwmDirty = false; confirms = 0; }
            byte[] target;
            lock (_lock) target = (byte[])_pwmTarget.Clone();
            var tele = _pwmByChainTele;
            bool match = tele != null && tele.AsSpan(0, 4).SequenceEqual(target);
            if (match)
            {
                if (++confirms >= 3 && !_pwmDirty) break;
            }
            else
            {
                confirms = 0;
                try { lock (_lock) SendPwmFrame(target); }
                catch (Exception ex) { Log.Occasional("LianLi", "pwm", $"pwm send failed: {ex.Message}"); }
            }
            TelemetryTouch();
            Thread.Sleep(400);
        }
        Log.Info("LianLi", attempts >= 120 ? "pwm: gave up waiting for confirmation" : "pwm: confirmed and latched");
        lock (_lock) _pwmThread = null;
        if (_pwmDirty) SetFanDuty(0, _pwmTarget[_slotToChain[0]] * 100 / 255);   // re-arm if changed mid-exit
    }

    /// <summary>Hand the fans to the mainboard PWM line (duty code 6 - the
    /// wire from the wireless controller to a fan header, e.g. SYS_FAN1).
    /// Fire-and-forget burst: this runs on app exit and must not stall; if a
    /// send is lost the fans just keep their latched duty, which is safe.</summary>
    public void FollowPwmLine()
    {
        var target = new byte[] { 6, 6, 6, 6 };
        lock (_lock)
        {
            for (int i = 0; i < 4; i++) _pwmTarget[i] = 6;
            _pwmSeeded = true;
            for (int k = 0; k < 4; k++)
            {
                try { SendPwmFrame(target); } catch { break; }
                Thread.Sleep(80);
            }
        }
        Log.Info("LianLi", "fans handed to the mainboard PWM line (SYS fan header curve)");
    }

    void SendPwmFrame(byte[] pwmByChain)
    {
        var rf = new byte[240];
        rf[0] = 0x12; rf[1] = 0x10;
        Array.Copy(_fanMac, 0, rf, 2, 6);
        Array.Copy(_masterMac, 0, rf, 8, 6);
        rf[14] = _rxType;
        rf[15] = _channel;
        rf[16] = 1;                       // slave index - NEVER 0 (0 = unbind)
        Array.Copy(pwmByChain, 0, rf, 17, 4);
        SendRf(rf);
    }

    public void Dispose()
    {
        Instance = null;
        _settleTimer?.Dispose();
        lock (_lock) { _rx?.Dispose(); _rx = null; }
        _usb.Dispose();
    }
}

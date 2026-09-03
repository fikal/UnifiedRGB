using System.Text;
using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Devices;

/// <summary>Logitech G403 HERO mouse (046D:C08F) via HID++ 2.0.
///
/// Long report (0x11, 20 bytes):
///   [0]=0x11 [1]=device_index [2]=feature_index [3]=(func|SW_ID) [4..]=params
/// The RGB feature (0x8071 RGB_EFFECTS or 0x8070 COLOR_LED_EFFECTS) is found at
/// a dynamic index via IRoot. Setting a static color: claim software control,
/// then SET_EFFECT [cluster, static_effect_idx, R, G, B, 0x02].
///
/// Ported from OpenRGB's LogitechHIDPP20Controller (constants + wire layouts).</summary>
public sealed class LogitechG403 : IRgbDevice
{
    const ushort VID = 0x046D;
    const ushort HIDPP_USAGE_PAGE = 0xFF00;
    const ushort HIDPP_LONG_USAGE = 0x0002;   // the long-report (0x11) top-level collection

    const byte LONG_MSG = 0x11;
    const int  LONG_LEN = 20;
    const byte SW_ID    = 0x07;
    const byte ROOT_IDX = 0x00;
    const byte FN_GET_FEATURE = 0x00;   // IRoot fn0
    const byte FN_GET_INFO    = 0x00;   // 0x8070/0x8071 fn0
    const ushort FEAT_8071 = 0x8071;
    const ushort FEAT_8070 = 0x8070;

    readonly HidNative.HidHandle _hid;
    readonly byte   _dev;
    readonly byte   _rgbIdx;
    readonly ushort _feature;
    readonly byte   _fnSetEffect;
    readonly byte   _fnSwControl;
    readonly bool   _swSimple;
    readonly byte[] _clusterEffect;      // static-effect index per cluster (= per zone)
    readonly Rgb?[] _lastPer;
    readonly bool[] _persisted;          // _lastPer[i] has been committed to onboard memory
    long _lastChangeTick;

    public string Name { get; }
    public string Vendor => "Logitech";
    public DeviceType Type => DeviceType.Mouse;
    public int LedCount => _clusterEffect.Length;
    public IReadOnlyList<RgbZone> Zones { get; }
    public IReadOnlyList<LedPos>? LedPositions { get; }

    LogitechG403(HidNative.HidHandle hid, byte dev, byte rgbIdx, ushort feature, byte[] clusterEffect, string name)
    {
        _hid = hid; _dev = dev; _rgbIdx = rgbIdx; _feature = feature;
        _clusterEffect = clusterEffect;
        _lastPer = new Rgb?[clusterEffect.Length];
        _persisted = new bool[clusterEffect.Length];
        Name = name;
        if (feature == FEAT_8071) { _fnSetEffect = 0x10; _fnSwControl = 0x50; _swSimple = false; }
        else                      { _fnSetEffect = 0x30; _fnSwControl = 0x80; _swSimple = true; }

        // G403-family layout: cluster 0 is the scroll wheel, 1 the logo
        // (empirical). Other counts get generic names.
        Zones = clusterEffect.Length switch
        {
            1 => new[] { new RgbZone { Name = "Mouse", Offset = 0, Count = 1 } },
            2 => new[]
            {
                new RgbZone { Name = "Wheel", Offset = 0, Count = 1 },
                new RgbZone { Name = "Logo",  Offset = 1, Count = 1 },
            },
            _ => Enumerable.Range(0, clusterEffect.Length)
                    .Select(i => new RgbZone { Name = $"Zone {i + 1}", Offset = i, Count = 1 }).ToArray(),
        };
        LedPositions = clusterEffect.Length == 2
            ? new[] { new LedPos(0.5f, 0.2f), new LedPos(0.5f, 0.75f) }   // wheel front, logo rear
            : null;
    }

    /// <summary>Probe every Logitech HID++ interface (any PID) for the RGB
    /// feature — covers most modern Logitech G mice, not just the G403.</summary>
    public static LogitechG403? TryOpen()
    {
        var tried = new HashSet<ushort>();
        // Query writes fixed 20-byte reports, and hidclass rejects a buffer
        // shorter than a collection's report length. Probe the canonical
        // long-report collection (usage 0x0002, exactly 20 B — what OpenRGB's
        // Logitech detectors key on) first, so the one probe per PID doesn't
        // depend on which 0xFF00 collection SetupDi happened to list first.
        foreach (var iface in HidNative.FindAll()
                     .Where(h => h.VendorId == VID && h.UsagePage == HIDPP_USAGE_PAGE && h.OutputLength >= LONG_LEN)
                     .OrderBy(h => h.Usage == HIDPP_LONG_USAGE && h.OutputLength == LONG_LEN ? 0 : h.OutputLength == LONG_LEN ? 1 : 2))
        {
            if (!tried.Add(iface.ProductId)) continue;   // one probe per device
            HidNative.HidHandle? hid = null;
            try { hid = HidNative.Open(iface.Path); } catch { continue; }

            foreach (byte dev in new byte[] { 0xFF, 0x01 })
            {
                foreach (var (feat, _) in new[] { (FEAT_8071, 0), (FEAT_8070, 0) })
                {
                    byte idx = QueryFeatureIndex(hid, dev, feat);
                    if (idx == 0) continue;
                    var clusterEffects = FindClusters(hid, dev, idx, feat);
                    string name = string.IsNullOrWhiteSpace(iface.Product)
                        ? "Logitech Mouse" : $"Logitech {iface.Product.Replace("Gaming Mouse", "").Trim()}";
                    Log.Info("LogitechG403",
                        $"'{name}' (pid {iface.ProductId:X4}) via usage 0x{iface.Usage:X4}, reports out {iface.OutputLength} B / in {iface.InputLength} B");
                    return new LogitechG403(hid, dev, idx, feat, clusterEffects, name);
                }
            }
            hid.Dispose();
        }
        return null;
    }

    static byte QueryFeatureIndex(HidNative.HidHandle hid, byte dev, ushort feature)
    {
        var r = Query(hid, dev, ROOT_IDX, FN_GET_FEATURE,
            new byte[] { (byte)(feature >> 8), (byte)feature });
        return r?[4] ?? 0;      // data[0]
    }

    /// <summary>Enumerate every LED cluster (= zone: wheel, logo, ...) and find
    /// each one's Static effect index (id 0x0001). One entry per cluster.</summary>
    static byte[] FindClusters(HidNative.HidHandle hid, byte dev, byte idx, ushort feature)
    {
        byte fallback = feature == FEAT_8071 ? (byte)0 : (byte)1;   // 0x8070: 0=off, 1=fixed

        // GetInfo -> cluster count (8071: data[2]; 8070: data[0]).
        byte[] infoParms = feature == FEAT_8071 ? new byte[] { 0xFF, 0xFF, 0x00 } : Array.Empty<byte>();
        var info = Query(hid, dev, idx, FN_GET_INFO, infoParms);
        int clusters = info == null ? 1 : feature == FEAT_8071 ? info[4 + 2] : info[4 + 0];
        clusters = Math.Clamp(clusters, 1, 8);

        var result = new byte[clusters];
        for (byte c = 0; c < clusters; c++)
        {
            result[c] = fallback;
            // Per-cluster info -> effect count (8071: data[4]; 8070: data[3]).
            var cinfo = Query(hid, dev, idx, FN_GET_INFO,
                feature == FEAT_8071 ? new byte[] { c, 0xFF } : new byte[] { c });
            if (cinfo == null) continue;
            int effects = feature == FEAT_8071 ? cinfo[4 + 4] : cinfo[4 + 3];

            for (byte j = 0; j < effects && j < 32; j++)
            {
                var einfo = Query(hid, dev, idx, FN_GET_INFO, new byte[] { c, j, 0x00, 0x00 });
                if (einfo == null) continue;
                int effectId = (einfo[4 + 2] << 8) | einfo[4 + 3];
                if (effectId == 0x0001) { result[c] = j; break; }   // Static
            }
        }
        return result;
    }

    static byte[]? Query(HidNative.HidHandle hid, byte dev, byte featIdx, byte func, byte[] parms)
    {
        var buf = new byte[LONG_LEN];
        buf[0] = LONG_MSG; buf[1] = dev; buf[2] = featIdx; buf[3] = (byte)(func | SW_ID);
        Array.Copy(parms, 0, buf, 4, Math.Min(parms.Length, LONG_LEN - 4));
        if (!hid.Write(buf)) return null;

        for (int i = 0; i < 8; i++)
        {
            var rx = new byte[LONG_LEN];
            int got = hid.Read(rx, 200);
            if (got <= 0) return null;
            if ((rx[0] == LONG_MSG || rx[0] == 0x10) && rx[2] == featIdx && (rx[3] & 0x0F) == SW_ID)
                return rx;
        }
        return null;
    }

    void ClaimSoftwareControl()
    {
        var parms = _swSimple ? new byte[] { 0x01, 0x01 } : new byte[] { 0x01, 0x01, 0x01 };
        Query(_hid, _dev, _rgbIdx, _fnSwControl, parms);
    }

    // HID++ transactions are request/reply; serialize concurrent writers
    // (effect worker + static apply) so replies aren't cross-matched.
    readonly object _writeLock = new();

    /// <summary>SET_EFFECT's 'persist' byte writes the colour to the mouse's
    /// onboard memory. It used to ride every changed frame — an animated effect
    /// committed to flash at frame rate, 24/7. Frames now stream without it.
    /// A static apply commits at once (SetColors with persist: true, the
    /// "survives handle close" case the byte exists for), Dispose commits
    /// whatever the mouse is showing, and an effect's frame is only committed
    /// lazily after it has sat unchanged for PersistAfterMs (checked by the
    /// engine's 1 s keepalive). The window is long enough that no animated
    /// effect's hold satisfies it - a Palette Cycle hold is 5 s, Time Warmth
    /// steps once a minute - so only a genuinely parked effect ever writes
    /// flash, and then once.</summary>
    const int PersistAfterMs = 300_000;

    public void SetColors(IReadOnlyList<Rgb> colors) => SetColors(colors, persist: false);

    /// <summary>Stream the colours; with <paramref name="persist"/> also commit
    /// them to onboard memory right away (static applies - the effect engine
    /// streams with false).</summary>
    public void SetColors(IReadOnlyList<Rgb> colors, bool persist)
    {
        if (colors.Count == 0) return;
        lock (_writeLock)
        {
            bool claimed = false, changed = false;
            for (int i = 0; i < _clusterEffect.Length; i++)
            {
                var c = colors[Math.Min(i, colors.Count - 1)];
                if (_lastPer[i] == c) continue;
                if (!claimed) { ClaimSoftwareControl(); claimed = true; }
                _lastPer[i] = c;
                _persisted[i] = false;
                changed = true;
                SendEffect(i, c, persist: false);
            }
            long now = Environment.TickCount64;
            if (changed) _lastChangeTick = now;
            if (persist || (!changed && now - _lastChangeTick >= PersistAfterMs)) CommitPersist();
        }
    }

    void SendEffect(int cluster, Rgb c, bool persist)
    {
        var parms = new byte[LONG_LEN - 4];
        parms[0] = (byte)cluster;
        parms[1] = _clusterEffect[cluster];
        parms[2] = c.R; parms[3] = c.G; parms[4] = c.B;
        parms[5]  = (byte)(c.R != 0 || c.G != 0 || c.B != 0 ? 0x02 : 0x00);
        if (persist) parms[12] = 0x01;   // save to the mouse so it survives handle close
        Query(_hid, _dev, _rgbIdx, _fnSetEffect, parms);
    }

    /// <summary>Re-send, with the persist byte, every cluster whose current
    /// colour has not been saved yet. Caller holds _writeLock.</summary>
    void CommitPersist()
    {
        for (int i = 0; i < _clusterEffect.Length; i++)
        {
            if (_persisted[i] || _lastPer[i] is not Rgb c) continue;
            _persisted[i] = true;
            SendEffect(i, c, persist: true);
        }
    }

    /// <summary>Raw request/response dump for the claim + set-color path, and a
    /// scan of effect indices, to diagnose a device that discovers but won't
    /// light.</summary>
    public string TestVerbose(Rgb c)
    {
        var sb = new StringBuilder();

        var claim = _swSimple ? new byte[] { 0x01, 0x01 } : new byte[] { 0x01, 0x01, 0x01 };
        sb.AppendLine("claim  -> " + Hex(QueryAny(_hid, _dev, _rgbIdx, _fnSwControl, claim)));

        // Hold each of the 3 effect indices for 3s so the user can see which
        // one is the solid static effect (vs an animated/off one).
        for (byte eff = 0; eff <= 2; eff++)
        {
            var p = new byte[LONG_LEN - 4];
            p[0] = 0; p[1] = eff;
            p[2] = c.R; p[3] = c.G; p[4] = c.B; p[5] = 0x02;   // no persist bit while probing
            var resp = QueryAny(_hid, _dev, _rgbIdx, _fnSetEffect, p);
            Console.WriteLine($"  >> holding effect index {eff} for 3s (reply {Hex(resp)}) - watch the mouse");
            Thread.Sleep(3000);
        }
        return sb.ToString();
    }

    static string Hex(byte[]? b) => b == null ? "(no reply)" :
        string.Join(" ", b.Take(8).Select(x => x.ToString("X2")));

    /// <summary>Like Query but returns the first reply of any kind (including
    /// HID++ error replies on feature 0xFF).</summary>
    static byte[]? QueryAny(HidNative.HidHandle hid, byte dev, byte featIdx, byte func, byte[] parms)
    {
        var buf = new byte[LONG_LEN];
        buf[0] = LONG_MSG; buf[1] = dev; buf[2] = featIdx; buf[3] = (byte)(func | SW_ID);
        Array.Copy(parms, 0, buf, 4, Math.Min(parms.Length, LONG_LEN - 4));
        if (!hid.Write(buf)) return null;
        for (int i = 0; i < 8; i++)
        {
            var rx = new byte[LONG_LEN];
            int got = hid.Read(rx, 200);
            if (got <= 0) return null;
            if (rx[0] == LONG_MSG || rx[0] == 0x10) return rx;
        }
        return null;
    }

    /// <summary>Human-readable discovery result for diagnostics.</summary>
    public string DiagnosticInfo() =>
        $"feature=0x{_feature:X4} featIdx={_rgbIdx} deviceIdx=0x{_dev:X2} " +
        $"clusters={_clusterEffect.Length} staticEffectIdx=[{string.Join(",", _clusterEffect)}] " +
        $"fnSetEffect=0x{_fnSetEffect:X2} fnSwControl=0x{_fnSwControl:X2}";

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (!_hid.IsDisposed) CommitPersist();   // save what the mouse is showing before the handle goes
            _hid.Dispose();
        }
    }
}

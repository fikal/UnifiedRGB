using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Devices;

/// <summary>SteelSeries Apex keyboards (Apex Pro TKL Gen 3 et al., 1038:xxxx)
/// — Gen 3 / 2023 wired protocol, ported from OpenRGB's
/// SteelSeriesApexController:
///   init    = feature [0, 0x4B, ...]                       (65-byte class)
///   direct  = feature [0, 0x40, numKeys, {key,R,G,B}...]   (643-byte class)
///   onboard = output  [0, 0x41, ...]  (hand back to hardware profiles)
/// Keys are HID usage codes from the shared Apex table. The direct/control
/// interface is the 0xFFC0 usage-1 collection. SteelSeries GG must not be
/// running (it fights for the device, like iCUE).</summary>
public sealed class SteelSeriesApex : IRgbDevice, IKeyMappedDevice
{
    // Lazy: static initializers run in TEXTUAL order and Keys is declared
    // below — an eager initializer here kills the type (and detection).
    static Dictionary<int, int>? _vkToLed;

    public int LedForVk(int vk)
    {
        var map = _vkToLed;
        if (map == null)
        {
            map = new Dictionary<int, int>();
            for (int i = 0; i < Keys.Length; i++)
            {
                int mapped = Input.HidUsageVk.ToVk(Keys[i]);
                if (mapped > 0) map.TryAdd(mapped, i);
            }
            _vkToLed = map;
        }
        return map.TryGetValue(vk, out int led) ? led : -1;
    }

    const ushort VID = 0x1038;
    // Wired PIDs speaking the Gen3/2023 direct protocol.
    static readonly ushort[] Pids = { 0x1642 /* Apex Pro TKL Gen 3 */, 0x1614 /* Apex Pro TKL 2023 */ };

    const byte PKT_INIT = 0x4B;
    const byte PKT_DIRECT = 0x40;
    const byte PKT_ONBOARD = 0x41;

    // Shared Apex key table (HID usage codes) from OpenRGB.
    static readonly byte[] Keys =
    {
        0x04,0x05,0x06,0x07,0x08,0x09,0x0A,0x0B,0x0C,0x0D,
        0x0E,0x0F,0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17,
        0x18,0x19,0x1A,0x1B,0x1C,0x1D,0x1E,0x1F,0x20,0x21,
        0x22,0x23,0x24,0x25,0x26,0x27,0x28,0x29,0x2A,0x2B,
        0x2C,0x2D,0x2E,0x2F,0x30,0x32,0x33,0x34,0x35,0x36,
        0x37,0x38,0x39,0x3A,0x3B,0x3C,0x3D,0x3E,0x3F,0x40,
        0x41,0x42,0x43,0x44,0x45,0x46,0x47,0x48,0x49,0x4A,
        0x4B,0x4C,0x4D,0x4E,0x4F,0x50,0x51,0x52,0x64,0xE0,
        0xE1,0xE2,0xE3,0xE4,0xE5,0xE6,0xE7,0xF0,0x31,0x87,
        0x88,0x89,0x8A,0x8B,
    };

    readonly HidNative.HidHandle _hid;
    readonly int _featureLen;
    readonly int _outputLen;
    readonly LedPos[] _positions;
    readonly object _writeLock = new();
    Rgb[]? _last;

    public string Name { get; }
    public string Vendor => "SteelSeries";
    public DeviceType Type => DeviceType.Keyboard;
    public int LedCount => Keys.Length;
    public IReadOnlyList<RgbZone> Zones { get; } =
        new[] { new RgbZone { Name = "Keyboard", Offset = 0, Count = 94 } };
    public IReadOnlyList<LedPos>? LedPositions => _positions;
    public float? PreviewAspect => 3.1f;   // TKL

    SteelSeriesApex(HidNative.HidHandle hid, int featureLen, int outputLen, string name)
    {
        _hid = hid;
        _featureLen = featureLen;
        _outputLen = outputLen;
        Name = name;
        _positions = BuildPositions();
        // Enter direct-lighting mode.
        var init = new byte[_featureLen];
        init[1] = PKT_INIT;
        _hid.SetFeature(init);
        Thread.Sleep(10);
    }

    public static SteelSeriesApex? TryOpen()
    {
        foreach (ushort pid in Pids)
        {
            // The control interface: usage page 0xFFC0, large feature report.
            var r = HidNative.OpenFirst("Apex", VID, pid,
                h => h.UsagePage == 0xFFC0 && h.FeatureLength >= 600 && h.OutputLength >= 65);
            if (r == null) continue;
            var info = r.Value.Info;
            string name = string.IsNullOrWhiteSpace(info.Product) ? "SteelSeries Apex" : $"SteelSeries {info.Product}";
            var dev = new SteelSeriesApex(r.Value.Handle, info.FeatureLength, info.OutputLength, name);
            Log.Info("Apex", $"opened {name} (pid {pid:X4}, feat {info.FeatureLength})");
            return dev;
        }
        return null;
    }

    /// <summary>Approximate ANSI TKL layout from HID usage codes, so effects
    /// and the keycap preview follow real geometry.</summary>
    static LedPos[] BuildPositions()
    {
        var pos = new LedPos[Keys.Length];
        for (int i = 0; i < Keys.Length; i++)
        {
            byte u = Keys[i];
            (float x, float y) = u switch
            {
                // Letters (QWERTY rows)
                0x14 => (2f, 2), 0x1A => (3f, 2), 0x08 => (4f, 2), 0x15 => (5f, 2), 0x17 => (6f, 2),
                0x1C => (7f, 2), 0x18 => (8f, 2), 0x0C => (9f, 2), 0x12 => (10f, 2), 0x13 => (11f, 2),
                0x04 => (2.3f, 3), 0x16 => (3.3f, 3), 0x07 => (4.3f, 3), 0x09 => (5.3f, 3), 0x0A => (6.3f, 3),
                0x0B => (7.3f, 3), 0x0D => (8.3f, 3), 0x0E => (9.3f, 3), 0x0F => (10.3f, 3),
                0x1D => (2.8f, 4), 0x1B => (3.8f, 4), 0x06 => (4.8f, 4), 0x19 => (5.8f, 4), 0x05 => (6.8f, 4),
                0x11 => (7.8f, 4), 0x10 => (8.8f, 4),
                // Number row 1..0, -, =
                >= 0x1E and <= 0x27 => (u - 0x1E + 1.3f, 1),
                0x2D => (11.3f, 1), 0x2E => (12.3f, 1),
                // Specials
                0x28 => (13.2f, 3),   // Enter
                0x29 => (0.3f, 0),    // Esc
                0x2A => (13.5f, 1),   // Backspace
                0x2B => (0.8f, 2),    // Tab
                0x2C => (6.8f, 5),    // Space
                0x2F => (12f, 2), 0x30 => (13f, 2), 0x31 => (14f, 2),   // [ ] backslash
                0x33 => (10.8f, 3), 0x34 => (11.8f, 3), 0x35 => (0.3f, 1),
                0x36 => (9.8f, 4), 0x37 => (10.8f, 4), 0x38 => (11.8f, 4),
                0x39 => (0.9f, 3),    // Caps
                // F row
                >= 0x3A and <= 0x3D => (u - 0x3A + 1.8f, 0),
                >= 0x3E and <= 0x41 => (u - 0x3E + 6.2f, 0),
                >= 0x42 and <= 0x45 => (u - 0x42 + 10.6f, 0),
                0x46 => (15.3f, 0), 0x47 => (16.3f, 0), 0x48 => (17.3f, 0),   // PrtSc ScrLk Pause
                0x49 => (15.3f, 1), 0x4A => (16.3f, 1), 0x4B => (17.3f, 1),   // Ins Home PgUp
                0x4C => (15.3f, 2), 0x4D => (16.3f, 2), 0x4E => (17.3f, 2),   // Del End PgDn
                0x4F => (17.3f, 5), 0x50 => (15.3f, 5), 0x51 => (16.3f, 5), 0x52 => (16.3f, 4),   // arrows
                // Modifiers
                0xE0 => (0.5f, 5), 0xE1 => (0.6f, 4), 0xE2 => (2.6f, 5), 0xE3 => (1.5f, 5),
                0xE4 => (14.2f, 5), 0xE5 => (13.4f, 4), 0xE6 => (11f, 5), 0xE7 => (12f, 5),
                0xF0 => (13.1f, 5),   // Fn/menu
                _ => (18.5f, (u & 3)),   // extras (logo, media) off to the side
            };
            pos[i] = new LedPos(x / 19f, y / 5f);
        }
        return pos;
    }

    public void SetColors(IReadOnlyList<Rgb> colors)
    {
        lock (_writeLock)
        {
            if (_last != null && colors.Count == _last.Length && colors.SequenceEqual(_last)) return;

            int n = Math.Min(Keys.Length, colors.Count);
            var buf = new byte[_featureLen];
            buf[1] = PKT_DIRECT;
            buf[2] = (byte)n;
            for (int i = 0; i < n; i++)
            {
                int o = i * 4 + 3;
                buf[o] = Keys[i];
                buf[o + 1] = colors[i].R;
                buf[o + 2] = colors[i].G;
                buf[o + 3] = colors[i].B;
            }
            _hid.SetFeature(buf);
            _last = colors.ToArray();
        }
    }

    public void Dispose()
    {
        // Hand lighting back to the keyboard's onboard profile.
        try
        {
            var buf = new byte[_outputLen];
            buf[1] = PKT_ONBOARD;
            _hid.Write(buf);
        }
        catch { }
        _hid.Dispose();
    }
}

using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Devices;

/// <summary>Corsair Strafe RGB MK.2 keyboard (1B1C:1B48).
/// Ported from the hardware-proven StrafeInit probe: the MK.2 firmware needs a
/// K70-MK2-generation init handshake before it accepts software colors, which
/// mainstream OpenRGB misses (bug #1641). Sends full-keyboard 24-bit color.</summary>
public sealed class CorsairStrafeMk2 : IRgbDevice, IKeyMappedDevice, IHardwareModes
{
    // Lazy: static initializers run in TEXTUAL order, and the Vk table this
    // reads is declared further down — an eager initializer here null-refs
    // the type and the whole device vanishes from detection.
    static Dictionary<int, int>? _vkToLed;

    public int LedForVk(int vk)
    {
        var map = _vkToLed;
        if (map == null)
        {
            map = new Dictionary<int, int>();
            for (int i = 0; i < Vk.Length; i++)
                if (Vk[i] != 0) map.TryAdd(Vk[i], i);
            _vkToLed = map;
        }
        return map.TryGetValue(vk, out int led) ? led : -1;
    }

    const ushort VID = 0x1B1C, PID = 0x1B48;
    const int PKT = 65;

    // K70 MK2 LED-identifier map (physical key -> firmware identifier).
    static readonly int[] Keys =
    {
        0x00,0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0C,0x0D,0x0E,0x0F,0x11,0x12,
        0x14,0x15,0x18,0x19,0x1A,0x1B,0x1C,0x1D,0x1E,0x1F,0x20,0x21,0x24,0x25,0x26,
        0x27,0x28,0x2A,0x2B,0x2C,0x30,0x31,0x32,0x33,0x34,0x35,0x36,0x37,0x38,0x39,
        0x3C,0x3D,0x3E,0x3F,0x40,0x42,0x43,0x44,0x45,0x48,73,74,75,76,78,
        79,80,81,84,85,86,87,88,89,90,91,92,93,96,97,
        98,99,100,101,102,103,104,105,108,109,110,111,112,113,115,
        116,117,120,121,122,123,124,126,127,128,129,132,133,134,135,
        136,137,139,140,141,16,114,47,59,125
    };
    static readonly int[] SkipAnsi = { 0x31,0x3f,0x41,0x42,0x51,0x53,0x55,0x6f,0x7e,0x7f,0x80,0x81 };

    // Hand-authored physical layout (from the actual board): per LED index,
    // {centerX, centerY, width, height} in key units (1u = one standard key).
    // y: -1.35 = top bar (profile/logo/media), 0 = F row, 1.5..5.5 = main rows.
    // Width 0 = hidden (ISO-only keys absent on this ANSI board).
    static float[] K(float x, float y, float w = 1f, float h = 1f) => new[] { x, y, w, h };
    static readonly float[][] LayoutU =
    {
        K(0.5f,0),            // 0  Esc
        K(0.5f,1.5f),         // 1  `~
        K(0.75f,2.5f,1.5f),   // 2  Tab
        K(0.875f,3.5f,1.75f), // 3  Caps
        K(1.125f,4.5f,2.25f), // 4  LShift
        K(0.625f,5.5f,1.25f), // 5  LCtrl
        K(14.5f,0),           // 6  F12
        K(12.5f,1.5f),        // 7  =
        K(4.5f,-1.35f,0.8f,0.7f),   // 8  WinLock
        K(19,2.5f),           // 9  Num7
        K(2.5f,0),            // 10 F1
        K(1.5f,1.5f),         // 11 1
        K(2,2.5f),            // 12 Q
        K(2.25f,3.5f),        // 13 A
        K(1.875f,5.5f,1.25f), // 14 LWin
        K(15.75f,0),          // 15 PrtSc
        K(19,-1.35f,0.9f,0.7f),     // 16 Mute
        K(20,2.5f),           // 17 Num8
        K(3.5f,0),            // 18 F2
        K(2.5f,1.5f),         // 19 2
        K(3,2.5f),            // 20 W
        K(3.25f,3.5f),        // 21 S
        K(2.75f,4.5f),        // 22 Z
        K(3.125f,5.5f,1.25f), // 23 LAlt
        K(16.75f,0),          // 24 ScrLk
        K(14,1.5f,2),         // 25 Backspace
        K(19,0,0.9f,0.7f),    // 26 MediaStop
        K(21,2.5f),           // 27 Num9
        K(4.5f,0),            // 28 F3
        K(3.5f,1.5f),         // 29 3
        K(4,2.5f),            // 30 E
        K(4.25f,3.5f),        // 31 D
        K(3.75f,4.5f),        // 32 X
        K(17.75f,0),          // 33 Pause
        K(15.75f,2.5f),       // 34 Del
        K(20,0,0.9f,0.7f),    // 35 MediaPrev
        K(5.5f,0),            // 36 F4
        K(4.5f,1.5f),         // 37 4
        K(5,2.5f),            // 38 R
        K(5.25f,3.5f),        // 39 F
        K(4.75f,4.5f),        // 40 C
        K(6.875f,5.5f,6.25f), // 41 Space
        K(15.75f,1.5f),       // 42 Ins
        K(16.75f,2.5f),       // 43 End
        K(21,0,0.9f,0.7f),    // 44 MediaPlay
        K(19,3.5f),           // 45 Num4
        K(7,0),               // 46 F5
        K(5.5f,1.5f),         // 47 5
        K(6,2.5f),            // 48 T
        K(6.25f,3.5f),        // 49 G
        K(5.75f,4.5f),        // 50 V
        K(16.75f,1.5f),       // 51 Home
        K(17.75f,2.5f),       // 52 PgDn
        K(22,0,0.9f,0.7f),    // 53 MediaNext
        K(20,3.5f),           // 54 Num5
        K(8,0),               // 55 F6
        K(6.5f,1.5f),         // 56 6
        K(7,2.5f),            // 57 Y
        K(7.25f,3.5f),        // 58 H
        K(6.75f,4.5f),        // 59 B
        K(17.75f,1.5f),       // 60 PgUp
        K(13.625f,4.5f,2.75f),// 61 RShift
        K(19,1.5f),           // 62 NumLock
        K(21,3.5f),           // 63 Num6
        K(9,0),               // 64 F7
        K(7.5f,1.5f),         // 65 7
        K(8,2.5f),            // 66 U
        K(8.25f,3.5f),        // 67 J
        K(7.75f,4.5f),        // 68 N
        K(10.625f,5.5f,1.25f),// 69 RAlt
        K(13,2.5f),           // 70 ]
        K(14.375f,5.5f,1.25f),// 71 RCtrl
        K(20,1.5f),           // 72 Num/
        K(19,4.5f),           // 73 Num1
        K(10,0),              // 74 F8
        K(8.5f,1.5f),         // 75 8
        K(9,2.5f),            // 76 I
        K(9.25f,3.5f),        // 77 K
        K(8.75f,4.5f),        // 78 M
        K(11.875f,5.5f,1.25f),// 79 RWin
        K(14.25f,2.5f,1.5f),  // 80 backslash
        K(16.75f,4.5f),       // 81 Up
        K(21,1.5f),           // 82 Num*
        K(20,4.5f),           // 83 Num2
        K(11.5f,0),           // 84 F9
        K(9.5f,1.5f),         // 85 9
        K(10,2.5f),           // 86 O
        K(10.25f,3.5f),       // 87 L
        K(9.75f,4.5f),        // 88 ,
        K(13.125f,5.5f,1.25f),// 89 Menu
        K(15.75f,5.5f),       // 90 Left
        K(22,1.5f),           // 91 Num-
        K(21,4.5f),           // 92 Num3
        K(12.5f,0),           // 93 F10
        K(10.5f,1.5f),        // 94 0
        K(11,2.5f),           // 95 P
        K(11.25f,3.5f),       // 96 ;
        K(10.75f,4.5f),       // 97 .
        K(13.875f,3.5f,2.25f),// 98 Enter
        K(16.75f,5.5f),       // 99 Down
        K(22,3.0f,1,2),       // 100 Num+
        K(19.5f,5.5f,2),      // 101 Num0
        K(13.5f,0),           // 102 F11
        K(11.5f,1.5f),        // 103 -
        K(12,2.5f),           // 104 [
        K(12.25f,3.5f),       // 105 '
        K(11.75f,4.5f),       // 106 /
        K(3.5f,-1.35f,0.8f,0.7f),   // 107 Brightness
        K(17.75f,5.5f),       // 108 Right
        K(22,5.0f,1,2),       // 109 NumEnter
        K(21,5.5f),           // 110 Num.
        K(0,0,0,0),           // 111 ISO / (hidden on ANSI)
        K(0,0,0,0),           // 112 ISO backslash (hidden on ANSI)
        K(10.375f,-1.35f,1.75f,0.8f), // 113 Logo left
        K(12.125f,-1.35f,1.75f,0.8f), // 114 Logo right
        K(2.5f,-1.35f,0.8f,0.7f),   // 115 Profile
    };

    // Windows virtual-key per LED index (parallel to LayoutU; 0 = no key VK).
    // Powers the reactive typing effects. NumEnter stays 0: the LL hook reports
    // VK_RETURN for it, which already maps to the main Enter LED.
    static readonly ushort[] Vk =
    {
        0x1B,0xC0,0x09,0x14,0xA0,0xA2,0x7B,0xBB,0,   0x67, // 0-9   Esc ` Tab Caps LShift LCtrl F12 = WinLock Num7
        0x70,0x31,0x51,0x41,0x5B,0x2C,0xAD,0x68,0x71,0x32, // 10-19 F1 1 Q A LWin PrtSc Mute Num8 F2 2
        0x57,0x53,0x5A,0xA4,0x91,0x08,0xB2,0x69,0x72,0x33, // 20-29 W S Z LAlt ScrLk Bksp Stop Num9 F3 3
        0x45,0x44,0x58,0x13,0x2E,0xB1,0x73,0x34,0x52,0x46, // 30-39 E D X Pause Del Prev F4 4 R F
        0x43,0x20,0x2D,0x23,0xB3,0x64,0x74,0x35,0x54,0x47, // 40-49 C Space Ins End Play Num4 F5 5 T G
        0x56,0x24,0x22,0xB0,0x65,0x75,0x36,0x59,0x48,0x42, // 50-59 V Home PgDn Next Num5 F6 6 Y H B
        0x21,0xA1,0x90,0x66,0x76,0x37,0x55,0x4A,0x4E,0xA5, // 60-69 PgUp RShift NumLk Num6 F7 7 U J N RAlt
        0xDD,0xA3,0x6F,0x61,0x77,0x38,0x49,0x4B,0x4D,0x5C, // 70-79 ] RCtrl Num/ Num1 F8 8 I K M RWin
        0xDC,0x26,0x6A,0x62,0x78,0x39,0x4F,0x4C,0xBC,0x5D, // 80-89 \ Up Num* Num2 F9 9 O L , Menu
        0x25,0x6D,0x63,0x79,0x30,0x50,0xBA,0xBE,0x0D,0x28, // 90-99 Left Num- Num3 F10 0 P ; . Enter Down
        0x6B,0x60,0x7A,0xBD,0xDB,0xDE,0xBF,0,   0x27,0,    // 100-109 Num+ Num0 F11 - [ ' / Brt Right NumEnter
        0x6E,0,   0,   0,   0,   0,                        // 110-115 Num. ISO ISO Logo Logo Profile
    };

    readonly HidNative.HidHandle _hid;
    readonly LedPos[] _positions;
    readonly LedRect[] _rects;
    readonly float _aspect;
    Rgb[]? _last;

    public string Name => "Corsair Strafe MK.2";
    public string Vendor => "Corsair";
    public DeviceType Type => DeviceType.Keyboard;
    public int LedCount => Keys.Length;
    public IReadOnlyList<RgbZone> Zones { get; } =
        new[] { new RgbZone { Name = "Keyboard", Offset = 0, Count = Keys.Length } };
    public IReadOnlyList<LedPos>? LedPositions => _positions;
    public IReadOnlyList<LedRect>? LedGeometry => _rects;
    public float? PreviewAspect => _aspect;

    CorsairStrafeMk2(HidNative.HidHandle hid)
    {
        _hid = hid;
        (_positions, _rects, _aspect) = BuildGeometry();
        RunInit();
    }

    /// <summary>Normalize the key-unit layout into 0..1 positions + footprints.</summary>
    static (LedPos[], LedRect[], float) BuildGeometry()
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var k in LayoutU)
        {
            if (k[2] <= 0) continue;                     // hidden
            minX = Math.Min(minX, k[0] - k[2] / 2); maxX = Math.Max(maxX, k[0] + k[2] / 2);
            minY = Math.Min(minY, k[1] - k[3] / 2); maxY = Math.Max(maxY, k[1] + k[3] / 2);
        }
        float sx = maxX - minX, sy = maxY - minY;
        var pos = new LedPos[LayoutU.Length];
        var rects = new LedRect[LayoutU.Length];
        for (int i = 0; i < LayoutU.Length; i++)
        {
            var k = LayoutU[i];
            if (k[2] <= 0) { pos[i] = new LedPos(0.5f, 0.5f); rects[i] = new LedRect(0, 0, 0, 0); continue; }
            pos[i] = new LedPos((k[0] - minX) / sx, (k[1] - minY) / sy);
            rects[i] = new LedRect((k[0] - minX) / sx, (k[1] - minY) / sy, k[2] / sx, k[3] / sy);
        }
        return (pos, rects, sx / sy);
    }

    /// <summary>Open the writable control interface (usage page 0xFFC2,
    /// output length 65) if the keyboard is present.</summary>
    public static CorsairStrafeMk2? TryOpen()
    {
        var r = HidNative.OpenFirst("StrafeMk2", VID, PID, h => h.OutputLength >= PKT);
        return r == null ? null : new CorsairStrafeMk2(r.Value.Handle);
    }

    void RunInit()
    {
        // Firmware-info read (best effort).
        var fw = new byte[PKT]; fw[1] = 0x0E; fw[2] = 0x01; _hid.Write(fw);
        _hid.Read(new byte[PKT], 200);

        Send(0x07, 0x04, 0x02);                 // SpecialFunctionControl
        Thread.Sleep(10);
        var p = new byte[PKT]; p[1] = 0x07; p[2] = 0x05; p[3] = 0x02; p[5] = 0x03;
        _hid.Write(p);                          // LightingControl (software mode)
        Thread.Sleep(10);

        // Key-mapping setup: 07 05 08 + 4x 07 40 1E identifier packets.
        p = new byte[PKT]; p[1] = 0x07; p[2] = 0x05; p[3] = 0x08; p[5] = 0x01; _hid.Write(p);
        int id = 0;
        for (int i = 0; i < 4; i++)
        {
            p = new byte[PKT]; p[1] = 0x07; p[2] = 0x40; p[3] = 0x1E;
            for (int j = 0; j < 30; j++)
            {
                foreach (int s in SkipAnsi) if (id == s) id++;
                p[5 + 2 * j] = (byte)id++;
                p[5 + 2 * j + 1] = 0xC0;
            }
            _hid.Write(p);
            Thread.Sleep(5);
        }
        Thread.Sleep(20);
    }

    void Send(byte a, byte b, byte c)
    {
        var p = new byte[PKT]; p[1] = a; p[2] = b; p[3] = c; _hid.Write(p);
    }

    // A frame is three sequential channel streams; concurrent writers (an
    // effect worker + a static apply) would interleave packets and corrupt
    // the latched frame, so the whole transaction is serialized.
    readonly object _writeLock = new();

    // Reused write buffers (all access under _writeLock): the old path built
    // 3x168 B channel arrays + 12 fresh 65 B packets + a colors.ToArray() per
    // frame, and the SequenceEqual dedup boxed two struct enumerators even on
    // the no-op path.
    readonly byte[] _rCh = new byte[168];
    readonly byte[] _gCh = new byte[168];
    readonly byte[] _bCh = new byte[168];
    readonly byte[] _pktBuf = new byte[PKT];

    /// <summary>Only the handback. A static colour would be a software-mode
    /// frame, which is exactly the state we are leaving, so it is no more
    /// durable than "keeps its last colors" already is.</summary>
    public HardwareExitCaps ExitCaps => HardwareExitCaps.ReturnToHardware;
    public IReadOnlyList<string> HardwareEffects => Array.Empty<string>();
    public void SetHardwareStatic(Rgb color) { }
    public void SetHardwareEffect(string name, Rgb? color) { }

    /// <summary>Put the keyboard back on its onboard profile. The counterpart
    /// of RunInit's two software-mode packets, with the hardware value: 0x01
    /// against 0x02, per OpenRGB's CORSAIR_LIGHTING_CONTROL_HARDWARE.
    ///
    /// Flagged rather than assumed final, because "Apply now" can be pressed
    /// with the app still running: the next frame re-runs the init and takes
    /// the keyboard back.</summary>
    public void ReturnToHardware()
    {
        lock (_writeLock)
        {
            var p = new byte[PKT]; p[1] = 0x07; p[2] = 0x05; p[3] = 0x01; p[5] = 0x03;
            _hid.Write(p);
            Thread.Sleep(10);
            Send(0x07, 0x04, 0x01);
            _needInit = true;
            _last = null;                 // repaint, don't dedup against a frame it is no longer showing
        }
    }

    bool _needInit;

    public void SetColors(IReadOnlyList<Rgb> colors)
    {
        lock (_writeLock)
        {
            if (_needInit) { _needInit = false; RunInit(); }
            int n = colors.Count;
            if (_last != null && _last.Length == n)
            {
                bool same = true;
                for (int i = 0; i < n; i++) if (_last[i] != colors[i]) { same = false; break; }
                if (same) return;
            }
            if (_last == null || _last.Length != n) _last = new Rgb[n];
            for (int i = 0; i < n; i++) _last[i] = colors[i];

            Array.Clear(_rCh); Array.Clear(_gCh); Array.Clear(_bCh);
            for (int i = 0; i < Keys.Length && i < n; i++)
            {
                _rCh[Keys[i]] = colors[i].R;
                _gCh[Keys[i]] = colors[i].G;
                _bCh[Keys[i]] = colors[i].B;
            }
            SendChannel(1, _rCh, 1);   // red
            SendChannel(2, _gCh, 1);   // green
            SendChannel(3, _bCh, 2);   // blue (finish)
        }
    }

    void SendChannel(byte channel, byte[] vals, byte finish)
    {
        Stream(1, 60, vals, 0);
        Stream(2, 60, vals, 60);
        Stream(3, 24, vals, 120);
        var p = _pktBuf;
        Array.Clear(p);
        p[1] = 0x07; p[2] = 0x28; p[3] = channel; p[4] = 3; p[5] = finish;
        _hid.Write(p);
        Thread.Sleep(5);   // protocol settle — the keyboard drops packets without it
    }

    void Stream(byte packetId, byte dataSz, byte[] data, int offset)
    {
        var p = _pktBuf;
        Array.Clear(p);
        p[1] = 0x7F; p[2] = packetId; p[3] = dataSz;
        Array.Copy(data, offset, p, 5, dataSz);
        _hid.Write(p);
        Thread.Sleep(2);   // protocol settle
    }

    public void Dispose() => _hid.Dispose();
}

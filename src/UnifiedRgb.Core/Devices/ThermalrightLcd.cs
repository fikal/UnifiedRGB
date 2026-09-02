using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Devices;

/// <summary>Thermalright AIO pump LCD (Frozen Warframe, 0416:5302). A 240x320
/// panel fed raw RGB565 frames (this is NOT an RGB device).
///
/// Protocol decoded from TRCC and verified on hardware (ShowFrame IS the spec -
/// this summary just mirrors it). HID reports are 513 bytes (report id 0 + 512
/// data). A frame is a 20-byte header followed by the 153,600 RGB565 bytes,
/// chunked across 512-byte writes:
///   header = DA DB DC DD | 02 00 01 00 | W(LE16) | H(LE16) | 02 | 00 00 00 |
///            payloadLen(LE32)
/// Handshake DA DB DC DD 00*8 01 00*7 returns the model id.</summary>
public sealed class ThermalrightLcd : IDisposable
{
    const ushort VID = 0x0416, PID = 0x5302;
    const int REPORT = 513;          // 1 report id + 512 data
    const int CHUNK  = 512;
    // Confirmed by USB capture of TRCC: this pump is a 240x320 RGB565 screen.
    public const int Width = 240, Height = 320;
    public const int FrameBytes = Width * Height * 2;   // 153600

    readonly HidNative.HidHandle _hid;

    ThermalrightLcd(HidNative.HidHandle hid) { _hid = hid; }

    public static ThermalrightLcd? TryOpen()
    {
        // The image interface is the collection with a 513-byte output report.
        var r = HidNative.OpenFirst("TrLcd", VID, PID, h => h.OutputLength >= REPORT);
        if (r == null) return null;
        var lcd = new ThermalrightLcd(r.Value.Handle);
        lcd.Handshake();
        return lcd;
    }

    /// <summary>Identify handshake; returns the model id byte (pm) or 0.</summary>
    public byte Handshake()
    {
        var payload = new byte[] { 218, 219, 220, 221, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 };
        WriteReport(payload, payload.Length);
        var rx = new byte[REPORT];
        int got = _hid.Read(rx, 500);
        // reply: [1..4]=magic, [13]=1 -> ident with pm at [6]
        if (got > 13 && rx[1] == 218 && rx[13] == 1) return rx[6];
        return 0;
    }

    /// <summary>Send the identify query and return the raw reply bytes (for
    /// probing whether the pump reports coolant temperature in its telemetry).</summary>
    public byte[] RawReply(int count = 40)
    {
        var payload = new byte[] { 218, 219, 220, 221, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 };
        WriteReport(payload, payload.Length);
        var rx = new byte[REPORT];
        int got = _hid.Read(rx, 500);
        return rx.Take(Math.Min(count, Math.Max(got, 0) == 0 ? count : got)).ToArray();
    }

    /// <summary>Display a full frame of raw RGB565 pixels (little-endian,
    /// length == FrameBytes). Frame = 20-byte header + pixels, streamed in
    /// 512-byte writes on the OUT endpoint (exact format from USB capture).</summary>
    // Reused across frames: this path runs ~25 fps forever, and fresh buffers
    // here were the app's largest allocation source (a 153 KB LOH payload plus
    // 301 chunk copies + 301 report buffers PER FRAME ~= 11.5 MB/s of garbage).
    // Pinned-heap arrays also stop the per-write GCHandle pin from fragmenting
    // gen0 (HidHandle.Transfer pins whatever buffer it is given).
    byte[]? _payload;
    readonly byte[] _report = GC.AllocateArray<byte>(REPORT, pinned: true);

    public void ShowFrame(byte[] rgb565)
    {
        int total = 20 + rgb565.Length;
        if (_payload == null || _payload.Length < total)
            _payload = GC.AllocateArray<byte>(total, pinned: true);
        var payload = _payload;
        payload[0] = 0xDA; payload[1] = 0xDB; payload[2] = 0xDC; payload[3] = 0xDD;
        payload[4] = 0x02; payload[5] = 0x00; payload[6] = 0x01; payload[7] = 0x00;
        payload[8]  = Width & 0xFF;   payload[9]  = (Width >> 8) & 0xFF;
        payload[10] = Height & 0xFF;  payload[11] = (Height >> 8) & 0xFF;
        payload[12] = 0x02;
        payload[13] = 0x00; payload[14] = 0x00; payload[15] = 0x00;
        int len = rgb565.Length;
        payload[16] = (byte)(len & 0xFF);
        payload[17] = (byte)((len >> 8) & 0xFF);
        payload[18] = (byte)((len >> 16) & 0xFF);
        payload[19] = (byte)((len >> 24) & 0xFF);
        Array.Copy(rgb565, 0, payload, 20, rgb565.Length);

        for (int off = 0; off < total; off += CHUNK)
        {
            int n = Math.Min(CHUNK, total - off);
            WriteReport(payload, off, n);
        }
    }

    /// <summary>Write one 513-byte HID output report (report id 0 + data),
    /// copied out of the caller's buffer at an offset — no per-chunk arrays.</summary>
    void WriteReport(byte[] data, int offset, int len)
    {
        lock (_report)                       // handshake vs stream thread
        {
            len = Math.Min(len, REPORT - 1);
            _report[0] = 0;                  // report id
            Array.Copy(data, offset, _report, 1, len);
            if (len < REPORT - 1)
                Array.Clear(_report, 1 + len, REPORT - 1 - len);   // stale tail from a longer chunk
            _hid.Write(_report);
        }
    }

    void WriteReport(byte[] data, int len) => WriteReport(data, 0, len);

    public void Dispose() => _hid.Dispose();
}

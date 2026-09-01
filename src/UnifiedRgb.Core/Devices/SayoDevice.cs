using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Devices;

/// <summary>SayoDevice macropads (O3C etc., 8089:xxxx). Ported from OpenRGB's
/// SayoDeviceController: 64-byte output packets `21 12 [checksum LE16]
/// [payload]`, checksum = 16-bit sum of header+payload as LE words. Lighting =
/// SAYO_CMD_LIGHTING_SET with a packed mode byte and one RGB color (whole-pad
/// static from our engine's point of view).</summary>
public sealed class SayoDevice : IRgbDevice
{
    const ushort VID = 0x8089;
    // NOTE: the O3C (0x0009) is a SCREEN macropad, not a lighting pad. Our single
    // "Pad" LED maps to OpenRGB's SAYO_CMD_LIGHTING_SET, and on the O3C that packet
    // corrupts the LCD (field report: "all messed up on the sayo screen") for zero
    // benefit (1 LED). Until there's real O3C screen support, leave it to SayoDevice's
    // own software - don't claim or write it. Re-add 0x0009 here to bring it back.
    static readonly ushort[] Pids = { 0x0007 /* E1 */ };

    readonly HidNative.HidHandle _hid;
    readonly int _outLen;
    readonly object _writeLock = new();
    Rgb? _last;

    public string Name { get; }
    public string Vendor => "SayoDevice";
    public DeviceType Type => DeviceType.LedController;
    public int LedCount => 1;
    public IReadOnlyList<RgbZone> Zones { get; } =
        new[] { new RgbZone { Name = "Pad", Offset = 0, Count = 1 } };

    SayoDevice(HidNative.HidHandle hid, int outLen, string name)
    {
        _hid = hid; _outLen = outLen; Name = name;
    }

    public static SayoDevice? TryOpen()
    {
        foreach (ushort pid in Pids)
        {
            // Control interface: usage page 0xFF11, usage 0x0002, 64-byte reports.
            var r = HidNative.OpenFirst("Sayo", VID, pid,
                h => h.UsagePage == 0xFF11 && h.Usage == 0x0002 && h.OutputLength >= 64);
            if (r == null) continue;
            var info = r.Value.Info;
            string name = string.IsNullOrWhiteSpace(info.Product) ? "SayoDevice" : $"SayoDevice {info.Product}";
            Log.Info("Sayo", $"opened {name} (pid {pid:X4})");
            return new SayoDevice(r.Value.Handle, info.OutputLength, name);
        }
        return null;
    }

    public void SetColors(IReadOnlyList<Rgb> colors)
    {
        if (colors.Count == 0) return;
        lock (_writeLock)
        {
            var c = colors[0];
            if (_last == c) return;
            _last = c;

            // SAYO_MODE_PACK(speed=3(1x), color=STATIC(0), mode=STATIC(0)).
            byte modeByte = (3 & 0x3) << 6 | (0 & 0x3) << 4 | (0 & 0xF);
            var payload = new byte[]
            {
                0x1C, 0x00, 0x11, 0x00, 0x01, 0x00, 0x00, 0x00,
                0x15, 0x00, 0x28, 0x00, 0x26, 0x00, 0x4C, 0x00,
                0x26, 0x00, 0x00, 0x00, modeByte, 0x00, 0x80, 0x80,
                c.R, c.G, c.B,
            };
            SendPacket(payload);
        }
    }

    void SendPacket(byte[] payload)
    {
        // 16-bit LE-word checksum of the 0x1221 header + payload.
        ushort checksum = 0x1221;
        for (int i = 0; i < payload.Length; i += 2)
        {
            ushort word = payload[i];
            if (i + 1 < payload.Length) word |= (ushort)(payload[i + 1] << 8);
            checksum = (ushort)(checksum + word);
        }

        var packet = new byte[_outLen];
        packet[0] = 0x21;                    // report id
        packet[1] = 0x12;
        packet[2] = (byte)(checksum & 0xFF);
        packet[3] = (byte)(checksum >> 8);
        Array.Copy(payload, 0, packet, 4, payload.Length);
        _hid.Write(packet);
    }

    public void Dispose() => _hid.Dispose();
}

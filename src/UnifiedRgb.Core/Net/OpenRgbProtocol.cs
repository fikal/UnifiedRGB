using System.Text;

namespace UnifiedRgb.Core.Net;

/*-----------------------------------------------------------*\
| The OpenRGB SDK wire format, from the published protocol     |
| description. Shared by the client (which talks to a server)  |
| and the server (which lets other software talk to us).       |
|                                                              |
| Version matters more than it looks. The device blob GREW     |
| over time: v1 inserted the vendor string after the name, v3  |
| added mode brightness, v5 and v6 added more. A server must   |
| answer min(client, ours) and then write the blob that        |
| version describes, or the client silently misreads every     |
| field after the first difference. We top out at 1, so there  |
| are exactly two layouts to write.                            |
\*-----------------------------------------------------------*/
public static class OpenRgbProtocol
{
    public const int HeaderBytes = 16;

    // Packet ids.
    public const uint PktControllerCount = 0;
    public const uint PktControllerData = 1;
    public const uint PktProtocolVersion = 40;
    public const uint PktSetClientName = 50;
    public const uint PktDeviceListUpdated = 100;
    public const uint PktResizeZone = 1000;
    public const uint PktUpdateLeds = 1050;
    public const uint PktUpdateZoneLeds = 1051;
    public const uint PktUpdateSingleLed = 1052;
    public const uint PktSetCustomMode = 1100;
    public const uint PktUpdateMode = 1101;

    /// <summary>The highest protocol we serve. v1 is the last version whose
    /// device blob this code writes; a newer client negotiates down to it.</summary>
    public const uint MaxVersion = 1;

    // device_type, from OpenRGB's RGBControllerInterface.h (GPL-2.0, as this
    // project is). Only the mapping is used, not their code.
    public const int TypeMotherboard = 0, TypeDram = 1, TypeGpu = 2, TypeCooler = 3,
                     TypeLedstrip = 4, TypeKeyboard = 5, TypeMouse = 6, TypeUnknown = 21;

    // zone_type.
    public const int ZoneSingle = 0, ZoneLinear = 1;

    // Mode flags and colour modes.
    public const uint ModeFlagPerLedColor = 1 << 5;
    public const int ModeColorsPerLed = 1;

    /// <summary>Our device kinds as OpenRGB's. The low numbers have been stable
    /// since the protocol was published; only UNKNOWN has moved as types were
    /// appended, and it is only ever an icon.</summary>
    public static int DeviceTypeOf(DeviceType t) => t switch
    {
        DeviceType.Motherboard => TypeMotherboard,
        DeviceType.Dram => TypeDram,
        DeviceType.Gpu => TypeGpu,
        DeviceType.Keyboard => TypeKeyboard,
        DeviceType.Mouse => TypeMouse,
        DeviceType.Cooler or DeviceType.Fan => TypeCooler,
        DeviceType.LedController => TypeLedstrip,
        _ => TypeUnknown,
    };

    /// <summary>OpenRGB packs a colour as 0x00BBGGRR.</summary>
    public static uint ToWire(Rgb c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

    public static Rgb FromWire(uint v) => new((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));

    /*--- header ---*/

    public static void WriteHeader(Span<byte> buf, uint device, uint packetId, int payloadLen)
    {
        buf[0] = (byte)'O'; buf[1] = (byte)'R'; buf[2] = (byte)'G'; buf[3] = (byte)'B';
        BitConverter.TryWriteBytes(buf[4..], device);
        BitConverter.TryWriteBytes(buf[8..], packetId);
        BitConverter.TryWriteBytes(buf[12..], (uint)payloadLen);
    }

    /// <summary>Null when the four magic bytes are not "ORGB", which is the
    /// only thing separating a real client from anything else that connects to
    /// the port.</summary>
    public static (uint Device, uint PacketId, int Size)? ReadHeader(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < HeaderBytes) return null;
        if (buf[0] != 'O' || buf[1] != 'R' || buf[2] != 'G' || buf[3] != 'B') return null;
        return (BitConverter.ToUInt32(buf[4..]), BitConverter.ToUInt32(buf[8..]),
                (int)BitConverter.ToUInt32(buf[12..]));
    }

    /*--- the device blob ---*/

    /// <summary>Serialize one device as OpenRGB controller data: the exact
    /// inverse of OpenRgbClient.ParseDevice, which is how the tests check it.
    ///
    /// One mode, "Direct", flagged per-LED colour: everything this app exposes
    /// is a stream of colours, and advertising modes we cannot actually switch
    /// to would just be a dropdown of dead entries in someone's client.
    ///
    /// Zones are SINGLE or LINEAR. No matrix: a matrix map is a promise about
    /// physical layout, and a wrong one turns a client's per-key effects into
    /// nonsense. Linear is always true.</summary>
    public static byte[] WriteDevice(IRgbDevice device, IReadOnlyList<Rgb> colors, uint version)
    {
        var body = new List<byte>(512);

        Add32(body, DeviceTypeOf(device.Type));
        AddStr(body, device.Name);
        if (version >= 1) AddStr(body, device.Vendor);      // v1 inserted vendor after name
        AddStr(body, $"{device.Vendor} {device.Name}");     // description
        AddStr(body, "");                                   // version
        AddStr(body, "");                                   // serial
        AddStr(body, "");                                   // location

        // Modes: one, and it is the active one.
        Add16(body, 1);
        Add32(body, 0);
        AddStr(body, "Direct");
        Add32(body, 0);                                     // value
        Add32(body, (int)ModeFlagPerLedColor);              // flags
        Add32(body, 0);                                     // speed_min
        Add32(body, 0);                                     // speed_max
        Add32(body, 0);                                     // colors_min
        Add32(body, 0);                                     // colors_max
        Add32(body, 0);                                     // speed
        Add32(body, 0);                                     // direction
        Add32(body, ModeColorsPerLed);                      // color_mode
        Add16(body, 0);                                     // no mode colours

        var zones = ZonesOf(device);
        Add16(body, zones.Count);
        foreach (var z in zones)
        {
            AddStr(body, z.Name);
            Add32(body, z.Count == 1 ? ZoneSingle : ZoneLinear);
            Add32(body, z.Count);                           // leds_min: fixed size, so min...
            Add32(body, z.Count);                           // ...max...
            Add32(body, z.Count);                           // ...and count all agree
            Add16(body, 0);                                 // no matrix map
        }

        // LEDs. The count here is what a client reports as the device's LED
        // count, so it has to match the colour array exactly.
        int leds = device.LedCount;
        Add16(body, leds);
        foreach (var z in zones)
            for (int i = 0; i < z.Count; i++)
                { AddStr(body, $"{z.Name} {i + 1}"); Add32(body, 0); }

        Add16(body, leds);
        for (int i = 0; i < leds; i++)
            Add32(body, (int)ToWire(i < colors.Count ? colors[i] : Rgb.Black));

        // The blob is prefixed with its own length, counting the prefix.
        var blob = new byte[4 + body.Count];
        BitConverter.TryWriteBytes(blob.AsSpan(), (uint)blob.Length);
        body.CopyTo(blob, 4);
        return blob;
    }

    /// <summary>Zones as the wire needs them: covering every LED exactly once,
    /// in order. A device whose declared zones do not add up to its LED count
    /// (or declares none) gets one zone covering everything, because a client
    /// that indexes past the end of a zone is a client that writes to the wrong
    /// LEDs.</summary>
    public static List<(string Name, int Count)> ZonesOf(IRgbDevice device)
    {
        var list = new List<(string, int)>();
        int covered = 0;
        bool contiguous = true;
        foreach (var z in device.Zones)
        {
            if (z.Offset != covered || z.Count <= 0) { contiguous = false; break; }
            list.Add((z.Name, z.Count));
            covered += z.Count;
        }
        if (!contiguous || covered != device.LedCount || list.Count == 0)
        {
            list.Clear();
            list.Add(("All", device.LedCount));
        }
        return list;
    }

    static void Add16(List<byte> b, int v) { b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF)); }

    static void Add32(List<byte> b, int v)
    {
        b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF));
        b.Add((byte)((v >> 16) & 0xFF)); b.Add((byte)((v >> 24) & 0xFF));
    }

    /// <summary>Length-prefixed, null-terminated, and the length counts the
    /// null.</summary>
    static void AddStr(List<byte> b, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s ?? "");
        Add16(b, bytes.Length + 1);
        b.AddRange(bytes);
        b.Add(0);
    }
}

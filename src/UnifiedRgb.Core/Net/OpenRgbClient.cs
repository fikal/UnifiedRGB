using System.Net.Sockets;
using System.Text;

namespace UnifiedRgb.Core.Net;

/*-----------------------------------------------------------*\
| OpenRGB SDK protocol client (clean-room, from the published |
| protocol description — no OpenRGB code). TCP, little-endian:|
|   header = "ORGB" + deviceIndex u32 + packetId u32 + size   |
| We declare protocol version 1, which pins the server to the |
| stable v1 device-blob layout regardless of its own version. |
| One socket, strictly serialized request/response under a    |
| lock; async DeviceListUpdated notifications are skipped.    |
\*-----------------------------------------------------------*/
public sealed class OpenRgbClient : IDisposable
{
    // Packet ids (protocol constants).
    const uint PktControllerCount = 0;
    const uint PktControllerData = 1;
    const uint PktProtocolVersion = 40;
    const uint PktSetClientName = 50;
    const uint PktDeviceListUpdated = 100;
    const uint PktUpdateLeds = 1050;
    const uint PktUpdateZoneLeds = 1051;
    const uint PktSetCustomMode = 1100;

    const uint OurProtocolVersion = 1;

    readonly TcpClient _tcp = new();
    readonly object _io = new();
    NetworkStream _s = null!;

    public uint ServerVersion { get; private set; }

    /// <summary>Device list changed server-side since the last enumerate.</summary>
    public bool ListDirty { get; private set; }

    public sealed record ZoneInfo(string Name, int Type, int LedCount, int MatrixW, int MatrixH, uint[]? Matrix);

    public sealed record DeviceInfo(
        int Index, int Type, string Name, string Vendor, string Description,
        string Version, string Serial, string Location,
        IReadOnlyList<ZoneInfo> Zones, int LedCount, uint[] Colors);

    /// <summary>Connect and handshake. Throws on failure.</summary>
    public static OpenRgbClient Connect(string host = "127.0.0.1", int port = 6742, int timeoutMs = 2000)
    {
        var c = new OpenRgbClient();
        try
        {
            // Every failure path disposes the socket: launch retries connect up
            // to 14 times, and a miss (timeout, refused, handshake read timeout)
            // used to strand a TcpClient + an in-flight ConnectAsync until
            // finalization.
            if (!c._tcp.ConnectAsync(host, port).Wait(timeoutMs))
                throw new TimeoutException($"no OpenRGB server on {host}:{port}");
            c._tcp.NoDelay = true;
            c._s = c._tcp.GetStream();
            c._s.ReadTimeout = 5000;
            c._s.WriteTimeout = 5000;

            // Version exchange, then identify ourselves.
            c.Send(0, PktProtocolVersion, BitConverter.GetBytes(OurProtocolVersion));
            var (_, _, payload) = c.ReadUntil(PktProtocolVersion);
            c.ServerVersion = payload.Length >= 4 ? BitConverter.ToUInt32(payload) : 0;

            byte[] name = Encoding.ASCII.GetBytes("UnifiedRGB\0");
            c.Send(0, PktSetClientName, name);
            Log.Info("openrgb", $"connected (server protocol {c.ServerVersion})");
            return c;
        }
        catch
        {
            try { c.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>Quick probe: is a server listening?</summary>
    public static bool IsServerUp(string host = "127.0.0.1", int port = 6742, int timeoutMs = 250)
    {
        try
        {
            using var probe = new TcpClient();
            return probe.ConnectAsync(host, port).Wait(timeoutMs) && probe.Connected;
        }
        catch { return false; }
    }

    public int GetControllerCount()
    {
        lock (_io)
        {
            Send(0, PktControllerCount, Array.Empty<byte>());
            var (_, _, p) = ReadUntil(PktControllerCount);
            return BitConverter.ToInt32(p);
        }
    }

    public DeviceInfo GetControllerData(int index)
    {
        lock (_io)
        {
            Send((uint)index, PktControllerData, BitConverter.GetBytes(OurProtocolVersion));
            var (_, _, p) = ReadUntil(PktControllerData, (uint)index);
            return ParseDevice(index, p);
        }
    }

    /// <summary>Switch a device to its direct/custom mode so LED writes stick.</summary>
    public void SetCustomMode(int index)
    {
        lock (_io) Send((uint)index, PktSetCustomMode, Array.Empty<byte>());
    }

    public void UpdateLeds(int index, ReadOnlySpan<Rgb> colors)
    {
        lock (_io)
        {
            int payloadLen = 4 + 2 + colors.Length * 4;
            var span = PrepareSend(payloadLen);
            BitConverter.TryWriteBytes(span, (uint)payloadLen);
            BitConverter.TryWriteBytes(span[4..], (ushort)colors.Length);
            for (int i = 0; i < colors.Length; i++)
            {
                int o = 6 + i * 4;
                span[o] = colors[i].R; span[o + 1] = colors[i].G; span[o + 2] = colors[i].B; span[o + 3] = 0;
            }
            FlushSend((uint)index, PktUpdateLeds, payloadLen);
        }
    }

    public void UpdateZoneLeds(int index, int zone, ReadOnlySpan<Rgb> colors)
    {
        lock (_io)
        {
            int payloadLen = 4 + 4 + 2 + colors.Length * 4;
            var span = PrepareSend(payloadLen);
            BitConverter.TryWriteBytes(span, (uint)payloadLen);
            BitConverter.TryWriteBytes(span[4..], (uint)zone);
            BitConverter.TryWriteBytes(span[8..], (ushort)colors.Length);
            for (int i = 0; i < colors.Length; i++)
            {
                int o = 10 + i * 4;
                span[o] = colors[i].R; span[o + 1] = colors[i].G; span[o + 2] = colors[i].B; span[o + 3] = 0;
            }
            FlushSend((uint)index, PktUpdateZoneLeds, payloadLen);
        }
    }

    /*-----------------------------------------------------*\
    | Wire helpers. One reusable send buffer holds header + |
    | payload so every packet is a SINGLE socket write: on a |
    | NoDelay socket the old header/payload pair went out as |
    | two TCP segments per LED update. Guarded by _io (the   |
    | Connect-time sends run before any concurrency).        |
    \*-----------------------------------------------------*/
    byte[] _sendBuf = new byte[1024];

    Span<byte> PrepareSend(int payloadLen)
    {
        int total = 16 + payloadLen;
        if (_sendBuf.Length < total) _sendBuf = new byte[Math.Max(total, _sendBuf.Length * 2)];
        return _sendBuf.AsSpan(16, payloadLen);
    }

    void FlushSend(uint device, uint packetId, int payloadLen)
    {
        var buf = _sendBuf;
        buf[0] = (byte)'O'; buf[1] = (byte)'R'; buf[2] = (byte)'G'; buf[3] = (byte)'B';
        BitConverter.TryWriteBytes(buf.AsSpan(4), device);
        BitConverter.TryWriteBytes(buf.AsSpan(8), packetId);
        BitConverter.TryWriteBytes(buf.AsSpan(12), (uint)payloadLen);
        _s.Write(buf, 0, 16 + payloadLen);
    }

    void Send(uint device, uint packetId, byte[] payload)
    {
        var span = PrepareSend(payload.Length);
        payload.CopyTo(span);
        FlushSend(device, packetId, payload.Length);
    }

    (uint Device, uint PacketId, byte[] Payload) ReadUntil(uint wantId, uint? wantDevice = null)
    {
        for (int guard = 0; guard < 64; guard++)
        {
            var (dev, id, payload) = ReadPacket();
            if (id == PktDeviceListUpdated) { ListDirty = true; continue; }
            if (id == wantId && (wantDevice == null || dev == wantDevice)) return (dev, id, payload);
            // Unexpected packet: skip it (already consumed).
        }
        throw new IOException("OpenRGB: reply not found in stream");
    }

    (uint Device, uint PacketId, byte[] Payload) ReadPacket()
    {
        Span<byte> header = stackalloc byte[16];
        ReadExactly(header);
        if (header[0] != 'O' || header[1] != 'R' || header[2] != 'G' || header[3] != 'B')
            throw new IOException("OpenRGB: bad packet magic");
        uint dev = BitConverter.ToUInt32(header[4..8]);
        uint id = BitConverter.ToUInt32(header[8..12]);
        int size = (int)BitConverter.ToUInt32(header[12..16]);
        if (size is < 0 or > 16_000_000) throw new IOException("OpenRGB: absurd packet size");
        var payload = new byte[size];
        if (size > 0) ReadExactly(payload);
        return (dev, id, payload);
    }

    void ReadExactly(Span<byte> buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = _s.Read(buf[got..]);
            if (n <= 0) throw new IOException("OpenRGB: connection closed");
            got += n;
        }
    }

    /*-----------------------------------------------------*\
    | v1 device blob parsing                                |
    \*-----------------------------------------------------*/
    internal static DeviceInfo ParseDevice(int index, byte[] p)
    {
        int o = 4;                                           // skip duplicate size u32
        int type = ReadI32(p, ref o);
        string name = ReadStr(p, ref o);
        string vendor = ReadStr(p, ref o);
        string description = ReadStr(p, ref o);
        string version = ReadStr(p, ref o);
        string serial = ReadStr(p, ref o);
        string location = ReadStr(p, ref o);

        int numModes = ReadU16(p, ref o);
        _ = ReadI32(p, ref o);                               // active mode
        for (int m = 0; m < numModes; m++)
        {
            _ = ReadStr(p, ref o);                           // mode name
            o += 4 * 9;                                      // value..color_mode (9 u32/i32 fields)
            int nc = ReadU16(p, ref o);
            o += nc * 4;
        }

        int numZones = ReadU16(p, ref o);
        var zones = new List<ZoneInfo>(numZones);
        for (int z = 0; z < numZones; z++)
        {
            string zname = ReadStr(p, ref o);
            int ztype = ReadI32(p, ref o);
            _ = ReadI32(p, ref o);                           // leds_min
            _ = ReadI32(p, ref o);                           // leds_max
            int zcount = ReadI32(p, ref o);
            int matrixLen = ReadU16(p, ref o);
            int mw = 0, mh = 0; uint[]? matrix = null;
            if (matrixLen > 0)
            {
                mh = ReadI32(p, ref o);
                mw = ReadI32(p, ref o);
                // Remote-supplied dimensions: a negative or absurd product must
                // fail this device's parse, not attempt a multi-GB allocation.
                if (mw < 0 || mh < 0 || (long)mw * mh > 65536)
                    throw new InvalidDataException($"zone '{zname}': implausible matrix {mw}x{mh}");
                matrix = new uint[mw * mh];
                for (int i = 0; i < matrix.Length; i++) matrix[i] = (uint)ReadI32(p, ref o);
            }
            zones.Add(new ZoneInfo(zname, ztype, zcount, mw, mh, matrix));
        }

        int numLeds = ReadU16(p, ref o);
        for (int i = 0; i < numLeds; i++)
        {
            _ = ReadStr(p, ref o);                           // led name
            o += 4;                                          // led value
        }

        int numColors = ReadU16(p, ref o);
        var colors = new uint[numColors];
        for (int i = 0; i < numColors; i++) colors[i] = (uint)ReadI32(p, ref o);

        return new DeviceInfo(index, type, name.Trim(), vendor.Trim(), description.Trim(),
                              version.Trim(), serial.Trim(), location.Trim(), zones, numLeds, colors);
    }

    static int ReadI32(byte[] p, ref int o) { int v = BitConverter.ToInt32(p, o); o += 4; return v; }
    static int ReadU16(byte[] p, ref int o) { int v = BitConverter.ToUInt16(p, o); o += 2; return v; }

    static string ReadStr(byte[] p, ref int o)
    {
        int len = ReadU16(p, ref o);                         // includes the trailing null
        string s = len > 0 ? Encoding.ASCII.GetString(p, o, Math.Max(0, len - 1)) : "";
        o += len;
        return s.TrimEnd('\0');
    }

    public void Dispose()
    {
        try { _s?.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
    }
}

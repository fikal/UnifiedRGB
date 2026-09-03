namespace UnifiedRgb.Core.Native;

/// <summary>System SMBus access through a signed PawnIO module. The namazso
/// modules share one contract (ioctl_smbus_xfer; block = 33 bytes [len,data…]
/// LE-packed into 5 cells), so AMD (PIIX4 at PCI 0:14.0) and Intel (I801 PCH)
/// differ only in which module blob loads — each module's main() detects its
/// own chipset and refuses on the wrong one. Every transaction is serialized
/// and performed holding the system-wide "Access_SMBUS.HTP.Method" mutex.
/// Needs elevation (PawnIO open fails otherwise).</summary>
public abstract class PawnSmbus : IDisposable
{
    const ulong PROTO_BYTE = 1, PROTO_BYTE_DATA = 2, PROTO_WORD_DATA = 3, PROTO_BLOCK_DATA = 5;
    const ulong READ = 1, WRITE = 0;

    readonly PawnIO _io;
    readonly Mutex _smbusMutex;
    readonly object _lock = new();

    public abstract string ChipsetName { get; }

    private protected PawnSmbus(PawnIO io)
    {
        _io = io;
        // Another vendor tool can pre-create this mutex with a DACL that
        // refuses us; don't orphan the freshly loaded kernel module (no
        // finalizer) when that happens.
        try { _smbusMutex = new Mutex(false, @"Global\Access_SMBUS.HTP.Method"); }
        catch { io.Dispose(); throw; }
    }

    /// <summary>Open whichever chipset module matches this machine.</summary>
    public static PawnSmbus? TryOpenAny()
        => (PawnSmbus?)SmbusPiix4.TryOpen() ?? SmbusI801.TryOpen();

    private protected static PawnIO? LoadModule(string file)
    {
        if (!PawnIO.IsAvailable) return null;
        var blob = PawnIO.ReadEmbeddedModule(file);
        return blob == null ? null : PawnIO.LoadModule(blob);
    }

    /// <summary>Select the chipset SMBus port (0-4, PIIX4 only). Returns the
    /// previous port, or -1.</summary>
    public int SelectPort(int port)
    {
        lock (_lock)
        {
            bool got = AcquireMutex();
            if (!got) return -1;
            try
            {
                var outv = new ulong[1];
                int n = _io.Execute("ioctl_piix4_port_sel", new[] { (ulong)port }, outv);
                return n >= 1 ? (int)outv[0] : -1;
            }
            finally { if (got) _smbusMutex.ReleaseMutex(); }
        }
    }

    /// <summary>Receive byte (probe). Returns the byte or -1.</summary>
    public int ReadByte(byte addr) => SimpleRead(addr, 0, PROTO_BYTE);

    /// <summary>SMBus read byte data. Returns the byte or -1.</summary>
    public int ReadByteData(byte addr, byte command) => SimpleRead(addr, command, PROTO_BYTE_DATA);

    public bool WriteByteData(byte addr, byte command, byte value)
        => SimpleWrite(addr, command, PROTO_BYTE_DATA, value);

    public bool WriteWordData(byte addr, byte command, ushort value)
        => SimpleWrite(addr, command, PROTO_WORD_DATA, value);

    /// <summary>SMBus block write (up to 32 bytes).</summary>
    public bool WriteBlockData(byte addr, byte command, ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 32) return false;

        // Cells 4..8 carry 33 bytes packed little-endian: [len, data...].
        var bytes = new byte[40];
        bytes[0] = (byte)data.Length;
        data.CopyTo(bytes.AsSpan(1));
        var input = new ulong[4 + 5];
        input[0] = addr; input[1] = WRITE; input[2] = command; input[3] = PROTO_BLOCK_DATA;
        for (int c = 0; c < 5; c++)
            input[4 + c] = BitConverter.ToUInt64(bytes, c * 8);

        lock (_lock)
        {
            bool got = AcquireMutex();
            if (!got) return false;
            try { return _io.Execute("ioctl_smbus_xfer", input, Array.Empty<ulong>()) >= 0; }
            finally { if (got) _smbusMutex.ReleaseMutex(); }
        }
    }

    int SimpleRead(byte addr, byte command, ulong proto)
    {
        lock (_lock)
        {
            bool got = AcquireMutex();
            if (!got) return -1;
            try
            {
                var outv = new ulong[1];
                int n = _io.Execute("ioctl_smbus_xfer", new[] { (ulong)addr, READ, (ulong)command, proto }, outv);
                return n >= 1 ? (int)outv[0] : -1;
            }
            finally { if (got) _smbusMutex.ReleaseMutex(); }
        }
    }

    bool SimpleWrite(byte addr, byte command, ulong proto, ulong value)
    {
        lock (_lock)
        {
            bool got = AcquireMutex();
            if (!got) return false;
            try
            {
                return _io.Execute("ioctl_smbus_xfer",
                    new[] { (ulong)addr, WRITE, (ulong)command, proto, value }, Array.Empty<ulong>()) >= 0;
            }
            finally { if (got) _smbusMutex.ReleaseMutex(); }
        }
    }

    /// <summary>Wait for the machine-wide SMBus mutex. False means another
    /// tool has held it for over 2 s; the caller then FAILS its transaction
    /// instead of issuing it anyway — an interleaved transfer on the shared
    /// bus corrupts both sides (garbage LED block for us, a bad SPD/sensor
    /// read for the other tool). A failed write is repainted next frame.</summary>
    bool AcquireMutex()
    {
        try { return _smbusMutex.WaitOne(2000); }
        catch (AbandonedMutexException) { return true; }
    }

    public void Dispose()
    {
        _io.Dispose();
        _smbusMutex.Dispose();
    }
}

/// <summary>AMD chipset SMBus (PIIX4-compatible controller).</summary>
public sealed class SmbusPiix4 : PawnSmbus
{
    SmbusPiix4(PawnIO io) : base(io) { }
    public override string ChipsetName => "AMD (PIIX4)";

    public static SmbusPiix4? TryOpen()
    {
        var io = LoadModule("SmbusPIIX4.bin");
        return io == null ? null : new SmbusPiix4(io);
    }
}

/// <summary>Intel PCH SMBus (I801 controller).</summary>
public sealed class SmbusI801 : PawnSmbus
{
    SmbusI801(PawnIO io) : base(io) { }
    public override string ChipsetName => "Intel (I801)";

    public static SmbusI801? TryOpen()
    {
        var io = LoadModule("SmbusI801.bin");
        return io == null ? null : new SmbusI801(io);
    }
}

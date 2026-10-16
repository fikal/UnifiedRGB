namespace UnifiedRgb.Core.Native;

/// <summary>What became of an SMBus block write.</summary>
public enum BlockWrite
{
    /// <summary>The host controller took the block.</summary>
    Ok,
    /// <summary>Never issued: another program held the machine-wide SMBus
    /// mutex for the whole wait, or the bus is being closed. Says nothing
    /// about the host - the same block goes through once the bus is free.</summary>
    BusBusy,
    /// <summary>Issued, and the host controller refused it (or it was a length
    /// no SMBus block can carry). The one answer that is about block support.</summary>
    Refused,
}

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

    /// <summary>The name every SMBus tool on Windows agrees on (HWiNFO,
    /// OpenRGB, the vendor suites): holding it is what keeps two programs'
    /// transfers from interleaving on the one bus.</summary>
    internal const string MachineMutexName = @"Global\Access_SMBUS.HTP.Method";

    readonly PawnIO _io;
    readonly Mutex _smbusMutex;
    readonly object _lock = new();

    public abstract string ChipsetName { get; }

    private protected PawnSmbus(PawnIO io) : this(io, MachineMutexName) { }

    /// <summary>The mutex name is a parameter only so the harness can drive
    /// the dispose-during-wait path on a private mutex instead of the real
    /// machine-wide one that the running app, HWiNFO and OpenRGB contend on.</summary>
    private protected PawnSmbus(PawnIO io, string mutexName)
    {
        _io = io;
        // Another vendor tool can pre-create this mutex with a DACL that
        // refuses us; don't orphan the freshly loaded kernel module (no
        // finalizer) when that happens.
        try { _smbusMutex = new Mutex(false, mutexName); }
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

    /// <summary>One module call. Virtual only so the harness can stand in for
    /// a bus (an ENE stick, a NAK, a block write the host refuses) without a
    /// PawnIO driver; the real chipsets never override it.</summary>
    private protected virtual int Xfer(string function, ulong[] input, ulong[] output)
        => _io.Execute(function, input, output);

    /// <summary>Select the chipset SMBus port (0-4, PIIX4 only). Returns the
    /// previous port, or -1.</summary>
    public int SelectPort(int port)
    {
        lock (_lock)
        {
            if (!Begin()) return -1;
            try
            {
                var outv = new ulong[1];
                int n = Xfer("ioctl_piix4_port_sel", new[] { (ulong)port }, outv);
                return n >= 1 ? (int)outv[0] : -1;
            }
            finally { End(); }
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

    /// <summary>SMBus block write (up to 32 bytes). Says which way it failed,
    /// because the two failures mean different things to a driver deciding
    /// whether this host can do blocks at all: see <see cref="BlockWrite"/>.</summary>
    public BlockWrite WriteBlockData(byte addr, byte command, ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 32) return BlockWrite.Refused;

        lock (_lock)
        {
            // Cells 4..8 carry 33 bytes packed little-endian: [len, data...].
            // Both buffers are reused under _lock: this runs twice per stick per
            // changed frame, and was two allocations each time.
            var bytes = _blockBytes; var input = _blockInput;
            Array.Clear(bytes);
            bytes[0] = (byte)data.Length;
            data.CopyTo(bytes.AsSpan(1));
            input[0] = addr; input[1] = WRITE; input[2] = command; input[3] = PROTO_BLOCK_DATA;
            for (int c = 0; c < 5; c++)
                input[4 + c] = BitConverter.ToUInt64(bytes, c * 8);

            // Not getting the bus is not the host refusing the block: another
            // tool (a hardware monitor reading sensors) held the mutex for the
            // whole wait, and nothing went on the wire.
            if (!Begin()) return BlockWrite.BusBusy;
            try { return Xfer("ioctl_smbus_xfer", input, Array.Empty<ulong>()) >= 0 ? BlockWrite.Ok : BlockWrite.Refused; }
            finally { End(); }
        }
    }

    // Per-transaction scratch, reused under _lock (see the block write).
    readonly byte[] _blockBytes = new byte[40];
    readonly ulong[] _blockInput = new ulong[4 + 5];
    readonly ulong[] _simpleInput = new ulong[5];

    int SimpleRead(byte addr, byte command, ulong proto)
    {
        lock (_lock)
        {
            if (!Begin()) return -1;
            try
            {
                var outv = new ulong[1];
                int n = Xfer("ioctl_smbus_xfer", new[] { (ulong)addr, READ, (ulong)command, proto }, outv);
                return n >= 1 ? (int)outv[0] : -1;
            }
            finally { End(); }
        }
    }

    bool SimpleWrite(byte addr, byte command, ulong proto, ulong value)
    {
        lock (_lock)
        {
            if (!Begin()) return false;
            try
            {
                var input = _simpleInput;
                input[0] = addr; input[1] = WRITE; input[2] = command; input[3] = proto; input[4] = value;
                return Xfer("ioctl_smbus_xfer", input, Array.Empty<ulong>()) >= 0;
            }
            finally { End(); }
        }
    }

    /*-----------------------------------------------------*\
    | The mutex handle's lifetime.                           |
    |                                                        |
    | Dispose must not close the handle while a transaction  |
    | is between its wait and its release. It used to: a     |
    | close does not interrupt a wait (the handle is held    |
    | open for the wait's duration), so a wait that          |
    | succeeded AFTER Dispose left this thread owning the    |
    | machine-wide mutex through a handle that could no      |
    | longer release it. ReleaseMutex threw, the catch       |
    | swallowed it, and the mutex stayed owned until the     |
    | thread died - on a pool or worker thread, the life of  |
    | the process: HWiNFO and OpenRGB time out on SMBus and  |
    | every DRAM write waits its full 2 s. So transactions   |
    | are counted, Dispose closes the handle only when none  |
    | is inside, and otherwise the last one out closes it.   |
    \*-----------------------------------------------------*/
    int _inFlight;       // transactions between Begin and End
    int _disposed;       // ints for Interlocked: see Dispose
    int _mutexClosed;    // the handle is closed exactly once, by whoever is last

    /// <summary>How long a transaction waits for the machine-wide mutex.
    /// Settable only so the harness can contend for its PRIVATE mutex without
    /// sitting out the real 2 s on every busy transaction.</summary>
    internal int MutexWaitMs { get; set; } = 2000;

    /// <summary>Count this transaction in and take the machine-wide mutex.
    /// False means do not issue it - disposed, or another tool held the bus
    /// for 2 s - and nothing is held. Every true is paired with End() in a
    /// finally.</summary>
    bool Begin()
    {
        Interlocked.Increment(ref _inFlight);   // full fence: pairs with Dispose's exchange
        if (Volatile.Read(ref _disposed) != 0 || !AcquireMutex()) { Leave(); return false; }
        // Disposed while we waited. The wait can still succeed, and then this
        // thread OWNS the machine-wide mutex: hand it straight back rather
        // than issue a transfer on a bus we have been told to let go of.
        if (Volatile.Read(ref _disposed) != 0) { ReleaseMutexQuietly(); Leave(); return false; }
        return true;
    }

    void End()
    {
        ReleaseMutexQuietly();
        Leave();
    }

    void Leave()
    {
        // The last transaction out after a Dispose closes what Dispose could not.
        if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _disposed) != 0
            && Interlocked.Exchange(ref _mutexClosed, 1) == 0)
            _smbusMutex.Dispose();
    }

    /// <summary>Wait for the machine-wide SMBus mutex. False means another
    /// tool has held it for over 2 s; the caller then FAILS its transaction
    /// instead of issuing it anyway — an interleaved transfer on the shared
    /// bus corrupts both sides (garbage LED block for us, a bad SPD/sensor
    /// read for the other tool). A failed write is repainted next frame.</summary>
    bool AcquireMutex()
    {
        try { return _smbusMutex.WaitOne(MutexWaitMs); }
        // Cannot happen while the in-flight count holds the handle open; kept
        // so a future path that skips Begin fails its transaction instead of
        // throwing out of a driver's SetColors.
        catch (ObjectDisposedException) { return false; }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing it - us killed under a
            // debugger mid-transaction, or another RGB tool crashing. Windows
            // hands ownership to the next waiter regardless, so we DO hold it
            // and carry on; End still releases it.
            //
            // Logged because it is otherwise invisible from outside a debugger,
            // which is the one place it is loudly visible: a first-chance stop
            // on an exception that is caught right here and is not a fault.
            Log.Warn("smbus", "the machine-wide SMBus mutex was abandoned by whoever held it last; taking it over");
            return true;
        }
    }

    /// <summary>Only ever called while this thread owns the mutex and the
    /// in-flight count holds the handle open, so neither catch should fire;
    /// a release must still never throw out of a finally.</summary>
    void ReleaseMutexQuietly()
    {
        try { _smbusMutex.ReleaseMutex(); }
        catch (ObjectDisposedException) { }   // handle already closed: nothing left to release through
        catch (ApplicationException) { }      // not owned by this thread: nothing to give back
    }

    /// <summary>Not under _lock on purpose: a transaction may be two seconds
    /// inside the machine-wide mutex, and the logoff path cannot wait for it.
    /// A straggler fails its transaction instead of throwing, and the mutex
    /// handle outlives Dispose until the last transaction has released it.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Overridable because this type is ABSTRACT: a chipset subclass
    /// that acquires anything of its own has somewhere to release it without
    /// re-implementing IDisposable and losing the flag-then-release order
    /// above, which is what keeps a straggling transaction from throwing.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;
        // A full fence, so this store and the count read below cannot be
        // reordered against Begin's increment and flag read: one side always
        // sees the other.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _io.Dispose();
        // Closed here only when no transaction is inside; otherwise the last
        // one to Leave closes it, after its release.
        if (Volatile.Read(ref _inFlight) == 0 && Interlocked.Exchange(ref _mutexClosed, 1) == 0)
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

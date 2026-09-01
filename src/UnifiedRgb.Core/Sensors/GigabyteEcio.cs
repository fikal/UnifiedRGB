namespace UnifiedRgb.Core.Sensors;

/*-----------------------------------------------------------*\
| Gigabyte fan-firmware handshake. On these boards the classic |
| Super-I/O PWM registers are only a MIRROR: a firmware EC     |
| (the secondary ITE chip, IT879x family) re-drives the fans   |
| continuously, so duty writes stick in the register but never |
| reach a fan. The firmware exposes an 8042-style command      |
| interface — status/command port 0x3F4, data port 0x3F0 —     |
| into its address space; fan-control area 0x900, and byte     |
| 0x47 inside it toggles "vendor fan control". Writing 0 makes |
| the firmware release the PWM registers (≈500ms to honor);    |
| writing 1 hands them back to the BIOS curves.                |
| Protocol layout matches LibreHardwareMonitor's field-proven  |
| EcioPortGigabyteController / IT879xEcioPort.                 |
|                                                              |
| Port access rides the SECONDARY chip's PawnIO whitelist      |
| (find_bars discovered the ECIO ports among its logical-      |
| device BARs).                                                |
\*-----------------------------------------------------------*/
public sealed class GigabyteEcio
{
    const ushort RegisterPort = 0x3F4;   // status read / command write
    const ushort ValuePort = 0x3F0;      // data
    const byte CmdRead = 0xB0, CmdWrite = 0xB1;
    const ushort FanControlArea = 0x900;
    const ushort EnableRegister = 0x47;
    const int TimeoutMs = 1000;

    readonly IteSuperIo _chip;
    readonly object _lock = new();
    bool? _initialVendorState;

    public GigabyteEcio(IteSuperIo secondaryChip) => _chip = secondaryChip;

    // Back off while polling: each PioInb is a kernel ioctl behind the
    // machine-wide ISA mutex, and the old tight loop pegged a core hammering
    // both for up to a full second on a slow EC transition.
    static void Backoff(int spin)
    {
        if (spin < 20) Thread.Yield();
        else Thread.Sleep(1);
    }

    bool WaitInputEmpty()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int spin = 0; ; spin++)
        {
            int s = _chip.PioInb(RegisterPort);
            if (s < 0) return false;                    // port denied/failed
            if ((s & 2) == 0) return true;
            if (sw.ElapsedMilliseconds > TimeoutMs) return false;
            Backoff(spin);
        }
    }

    bool WaitOutputFull()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int spin = 0; ; spin++)
        {
            int s = _chip.PioInb(RegisterPort);
            if (s < 0) return false;
            if ((s & 1) == 1) return true;
            if (sw.ElapsedMilliseconds > TimeoutMs) return false;
            Backoff(spin);
        }
    }

    bool Cmd(byte b) => WaitInputEmpty() && _chip.PioOutb(RegisterPort, b);
    bool Val(byte b) => WaitInputEmpty() && _chip.PioOutb(ValuePort, b);

    /// <summary>One byte from the firmware's address space, or -1.</summary>
    public int ReadByte(ushort offset)
    {
        lock (_lock)
        {
            if (!Cmd(CmdRead) || !Val((byte)(offset >> 8)) || !Val((byte)offset)) return -1;
            if (!WaitOutputFull()) return -1;
            return _chip.PioInb(ValuePort);
        }
    }

    public bool WriteByte(ushort offset, byte value)
    {
        lock (_lock)
        {
            return Cmd(CmdWrite) && Val((byte)(offset >> 8)) && Val((byte)offset) && Val(value);
        }
    }

    /// <summary>Fan-control block version byte, or -1 when the ECIO interface
    /// doesn't answer (older board, ports not whitelisted, …).</summary>
    public int ControllerVersion => ReadByte(FanControlArea);

    /// <summary>Current owner of the fans: true = firmware, false = us,
    /// null = interface unavailable.</summary>
    public bool? VendorControlEnabled
    {
        get
        {
            int v = ReadByte(FanControlArea + EnableRegister);
            return v < 0 ? null : v != 0;
        }
    }

    /// <summary>True once a SetVendorControl(false) has taken ownership and
    /// not yet been handed back.</summary>
    public bool WeOwnTheFans { get; private set; }

    /// <summary>false = firmware releases the PWM registers to us; true =
    /// firmware (BIOS curves) takes them back. ~500ms to take effect.</summary>
    public bool SetVendorControl(bool enabled)
    {
        lock (_lock)
        {
            int cur = ReadByte(FanControlArea + EnableRegister);
            if (cur < 0) return false;
            _initialVendorState ??= cur != 0;
            if ((cur != 0) != enabled)
            {
                if (!WriteByte(FanControlArea + EnableRegister, (byte)(enabled ? 1 : 0))) return false;
                Thread.Sleep(500);
                Log.Info("gbec", enabled
                    ? "vendor fan control RE-ENABLED (BIOS curves back)"
                    : "vendor fan control disabled — we drive the fans now");
            }
            WeOwnTheFans = !enabled;
            return true;
        }
    }
}

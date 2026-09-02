using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Sensors;

/*-----------------------------------------------------------*\
| Gigabyte fan-firmware takeover via the ISA-bridge MMIO path  |
| (signed PawnIO IsaBridgeEC module). On boards where the      |
| ECIO ports don't exist, the firmware EC's RAM is reachable   |
| as an LPC memory window: enable MMIO decode on the bridge,   |
| map the Super-I/O window, and the fan-control block sits at  |
| 0x900 with the "vendor fan control" byte at +0x47. Writing   |
| 0 there makes the firmware release the PWM registers so the  |
| classic Super-I/O duty writes drive the fans; 1 hands them   |
| back to the BIOS curves (~500ms either way).                 |
| Call order mirrors LibreHardwareMonitor's field-proven       |
| IsaBridgeGigabyteController: decode state is saved on open   |
| and restored around every access, so the bus is left the     |
| way we found it.                                             |
\*-----------------------------------------------------------*/
public sealed class GigabyteIsaBridge : IDisposable
{
    const ushort FanControlArea = 0x900;
    const ushort EnableRegister = 0x47;
    // MMIOState: -1=original, 0=disabled, 1=0x2E only, 2=0x4E only, 3=both.
    // Enabling a window that wasn't discovered fails the whole set_state, so
    // we compute the mask from which slots find_superio_mmio actually found.
    const ulong MmioOriginal = unchecked((ulong)-1);

    readonly PawnIO _io;
    readonly object _lock = new();
    ulong _orgState;
    ulong _enableState;   // exactly the windows that exist
    int _slot = -1;

    GigabyteIsaBridge(PawnIO io) => _io = io;

    /// <summary>Null when unsupported (no module, no MMIO window, or no
    /// fan-control block answering with a sane version byte).</summary>
    public static GigabyteIsaBridge? TryOpen()
    {
        if (!PawnIO.IsAvailable) return null;
        var blob = IteSuperIo.ReadEmbedded("IsaBridgeEC.bin");
        if (blob == null) { Log.Info("gbec", "IsaBridgeEC.bin not embedded"); return null; }
        var io = PawnIO.LoadModule(blob);
        if (io == null) { Log.Info("gbec", "IsaBridgeEC module load REJECTED"); return null; }
        var b = new GigabyteIsaBridge(io);
        if (b.Init()) return b;
        b.Dispose();
        return null;
    }

    bool Init()
    {
        // Order matters: find_superio_mmio must run FIRST — the module's
        // test_support() (behind every state ioctl) requires a discovered,
        // supported chip before it reports the bridge as usable.
        var found = new ulong[6];
        if (_io.Execute("ioctl_find_superio_mmio", Array.Empty<ulong>(), found) < 0)
        {
            Log.Info("gbec", "find_superio_mmio failed (no supported ITE chip)");
            return false;
        }
        Log.Info("gbec",
            $"MMIO windows: slot0 base=0x{found[0]:X} chip=0x{found[2]:X4}; slot1 base=0x{found[3]:X} chip=0x{found[5]:X4}");

        // Enable only the discovered windows: bit0 = 0x2E (slot0), bit1 = 0x4E.
        _enableState = (found[0] != 0 ? 1ul : 0ul) | (found[3] != 0 ? 2ul : 0ul);
        if (_enableState == 0) { Log.Info("gbec", "no MMIO window discovered"); return false; }

        var one = new ulong[1];
        if (_io.Execute("ioctl_iomem_mmio_get_org_state", Array.Empty<ulong>(), one) < 0)
        {
            Log.Info("gbec", "get_org_state failed (ISA bridge unsupported)");
            return false;
        }
        _orgState = one[0];
        Log.Info("gbec", $"original decode state {(long)_orgState}, will enable state {_enableState}");

        if (_io.Execute("ioctl_iomem_mmio_set_state", new[] { _enableState }, Array.Empty<ulong>()) < 0)
        {
            Log.Info("gbec", $"set_state({_enableState}) failed");
            return false;
        }
        try
        {
            if (_io.Execute("ioctl_map_superio_mmio", Array.Empty<ulong>(), Array.Empty<ulong>()) < 0)
            {
                Log.Info("gbec", "map failed");
                return false;
            }
            try
            {
                for (int s = 0; s < 2; s++)
                {
                    if ((s == 0 ? found[0] : found[3]) == 0) continue;
                    int ver = Access(s, FanControlArea, write: false, 0);
                    Log.Info("gbec", $"slot {s}: fan-block version byte = {(ver < 0 ? "unreadable" : $"0x{ver:X2}")}");
                    if (ver is > 0 and < 0xFF)
                    {
                        _slot = s;
                        return true;
                    }
                }
                Log.Info("gbec", "no fan-control block behind either MMIO window");
                return false;
            }
            finally { _io.Execute("ioctl_unmap_superio_mmio", Array.Empty<ulong>(), Array.Empty<ulong>()); }
        }
        finally { _io.Execute("ioctl_iomem_mmio_set_state", new[] { MmioOriginal }, Array.Empty<ulong>()); }
    }

    /// <summary>[slot, offset, size, type(0=read/1=write), value] → out[0].</summary>
    int Access(int slot, ushort offset, bool write, byte value)
    {
        var one = new ulong[1];
        int n = _io.Execute("ioctl_access_superio_mmio",
            new ulong[] { (ulong)slot, offset, 1, write ? 1ul : 0ul, value }, one);
        return n >= 0 ? (int)(one[0] & 0xFF) : -1;
    }

    /// <summary>Run one operation inside an enable-decode → map → … → unmap →
    /// restore-decode bracket, so the bus state is only altered momentarily.</summary>
    T WithMmio<T>(Func<T> body, T failure)
    {
        lock (_lock)
        {
            if (_slot < 0) return failure;
            if (_io.Execute("ioctl_iomem_mmio_set_state", new[] { _enableState }, Array.Empty<ulong>()) < 0) return failure;
            try
            {
                if (_io.Execute("ioctl_map_superio_mmio", Array.Empty<ulong>(), Array.Empty<ulong>()) < 0) return failure;
                try { return body(); }
                finally { _io.Execute("ioctl_unmap_superio_mmio", Array.Empty<ulong>(), Array.Empty<ulong>()); }
            }
            finally { _io.Execute("ioctl_iomem_mmio_set_state", new[] { MmioOriginal }, Array.Empty<ulong>()); }
        }
    }

    public bool? VendorControlEnabled
        => WithMmio<bool?>(() => Access(_slot, FanControlArea + EnableRegister, false, 0) switch
        {
            < 0 => null,
            var v => v != 0,
        }, null);

    /// <summary>True once we hold the fans (vendor control off).</summary>
    public bool WeOwnTheFans { get; private set; }

    /// <summary>false = firmware releases the PWM registers to us; true =
    /// BIOS curves take the fans back. ~500ms to take effect.</summary>
    public bool SetVendorControl(bool enabled)
    {
        bool ok = WithMmio(() =>
        {
            int cur = Access(_slot, FanControlArea + EnableRegister, false, 0);
            if (cur < 0) return false;
            if ((cur != 0) == enabled) return true;
            return Access(_slot, FanControlArea + EnableRegister, true, (byte)(enabled ? 1 : 0)) >= 0;
        }, false);
        if (!ok) return false;
        Thread.Sleep(500);
        WeOwnTheFans = !enabled;
        Log.Info("gbec", enabled
            ? "vendor fan control RE-ENABLED (BIOS curves back)"
            : "vendor fan control disabled — we drive the fans now");
        return true;
    }

    public void Dispose()
    {
        try { if (WeOwnTheFans) SetVendorControl(true); } catch { }
        _io.Dispose();
    }

    /// <summary>Probe helper: find + map each discovered window and dump a
    /// byte range so we can locate the fan-control block by eye. Logs to
    /// [gbec-dump]; does not gate on any block being present.</summary>
    public static void DumpWindows(int start, int count)
    {
        if (!PawnIO.IsAvailable) { Log.Info("gbec-dump", "PawnIO unavailable"); return; }
        var blob = IteSuperIo.ReadEmbedded("IsaBridgeEC.bin");
        if (blob == null) { Log.Info("gbec-dump", "no module"); return; }
        using var io = PawnIO.LoadModule(blob);
        if (io == null) { Log.Info("gbec-dump", "module load rejected"); return; }

        var found = new ulong[6];
        if (io.Execute("ioctl_find_superio_mmio", Array.Empty<ulong>(), found) < 0)
        { Log.Info("gbec-dump", "find failed"); return; }
        Log.Info("gbec-dump", $"slot0 base=0x{found[0]:X} size=0x{found[1]:X} chip=0x{found[2]:X4}; slot1 base=0x{found[3]:X} size=0x{found[4]:X} chip=0x{found[5]:X4}");

        ulong enable = (found[0] != 0 ? 1ul : 0ul) | (found[3] != 0 ? 2ul : 0ul);
        if (enable == 0) { Log.Info("gbec-dump", "no window"); return; }
        if (io.Execute("ioctl_iomem_mmio_set_state", new[] { enable }, Array.Empty<ulong>()) < 0)
        { Log.Info("gbec-dump", $"set_state({enable}) failed"); return; }
        try
        {
            if (io.Execute("ioctl_map_superio_mmio", Array.Empty<ulong>(), Array.Empty<ulong>()) < 0)
            { Log.Info("gbec-dump", "map failed"); return; }
            try
            {
                for (int slot = 0; slot < 2; slot++)
                {
                    if ((slot == 0 ? found[0] : found[3]) == 0) continue;
                    for (int off = start; off < start + count; off += 16)
                    {
                        var sb = new System.Text.StringBuilder($"slot{slot} 0x{off:X4}: ");
                        for (int j = 0; j < 16; j++)
                        {
                            var one = new ulong[1];
                            int n = io.Execute("ioctl_access_superio_mmio",
                                new ulong[] { (ulong)slot, (ulong)(off + j), 1, 0, 0 }, one);
                            sb.Append(n >= 0 ? $"{one[0] & 0xFF:X2} " : "-- ");
                        }
                        Log.Info("gbec-dump", sb.ToString());
                    }
                }
            }
            finally { io.Execute("ioctl_unmap_superio_mmio", Array.Empty<ulong>(), Array.Empty<ulong>()); }
        }
        finally { io.Execute("ioctl_iomem_mmio_set_state", new[] { MmioOriginal }, Array.Empty<ulong>()); }
    }
}

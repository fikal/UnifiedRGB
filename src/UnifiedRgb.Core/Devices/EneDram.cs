using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Devices;

/// <summary>ENE (Aura) RGB DRAM sticks (G.Skill Trident Z5 etc.) on the AMD
/// SMBus via the signed PawnIO SmbusPIIX4 module. Protocol ported from
/// OpenRGB's ENESMBusController:
///   register select = write_word(0x00, byteswapped reg), then
///   read = read_byte_data(0x81) / write = write_byte_data(0x01, val) /
///   block write = write_block_data(0x03, data).
/// Colors go to the direct-color register (v2 0x8100 on DDR5) as R,B,G
/// triples in 3-byte blocks with direct mode enabled (0x8020=1 + apply).
/// Detection remaps each DIMM's controller from 0x77 to its own address
/// (slot index 0x80F8 / address 0x80F9), then probes the candidate list.
/// Requires elevation (PawnIO).</summary>
public sealed class EneDram : IRgbDevice
{
    const ushort REG_DEVICE_NAME = 0x1000;
    const ushort REG_CONFIG_TABLE = 0x1C00;
    const int CONFIG_LED_COUNT = 0x02;
    const ushort REG_DIRECT = 0x8020;
    const ushort REG_APPLY = 0x80A0;
    const ushort REG_SLOT_INDEX = 0x80F8;
    const ushort REG_I2C_ADDRESS = 0x80F9;
    const ushort REG_COLORS_DIRECT_V1 = 0x8000;
    const ushort REG_COLORS_DIRECT_V2 = 0x8100;

    static readonly byte[] CandidateAddresses =
    {
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77,
        0x4F, 0x66, 0x67, 0x39, 0x3A, 0x3B, 0x3C, 0x3D,
    };

    // Device-version strings that use the 15-byte v1 color register (all the
    // AUDA0/AUMA0 second-gen controllers use v2 at 0x8100).
    static readonly string[] V1Versions = { "LED-0116", "DIMM_LED-0102" };

    /// <summary>The SMBus (PawnIO kernel handle + machine-wide mutex) is shared
    /// by every stick found in one DetectAll and released by the LAST stick's
    /// Dispose. Before this, no stick owned it: each Rescan leaked a driver
    /// handle and a global mutex (PawnIO has no finalizer).</summary>
    sealed class BusLease
    {
        public readonly PawnSmbus Bus;
        public int Refs;
        public BusLease(PawnSmbus bus) => Bus = bus;
        public void Release() { if (Interlocked.Decrement(ref Refs) == 0) Bus.Dispose(); }
    }

    readonly BusLease _lease;
    readonly PawnSmbus _bus;
    readonly byte _addr;
    readonly ushort _directReg;
    readonly int _ledCount;
    readonly LedPos[] _positions;
    bool _directOn;
    Rgb[]? _last;
    byte[]? _wireBuf;             // reused wire buffer (was allocated per frame)
    bool _batchedBlocks = true;   // full-stick SMBus blocks; reverts on first host rejection

    public string Name { get; }
    public string Vendor => "ENE";
    public DeviceType Type => DeviceType.Dram;
    public int LedCount => _ledCount;
    public IReadOnlyList<RgbZone> Zones { get; }
    public IReadOnlyList<LedPos>? LedPositions => _positions;
    public float? PreviewAspect => 5f;   // LEDs run along the stick's top edge

    EneDram(BusLease lease, byte addr, string name, string version, int ledCount)
    {
        _lease = lease;
        Interlocked.Increment(ref lease.Refs);
        _bus = lease.Bus;
        _addr = addr;
        _ledCount = ledCount;
        _directReg = V1Versions.Contains(version) ? REG_COLORS_DIRECT_V1 : REG_COLORS_DIRECT_V2;
        Name = name;
        Zones = new[] { new RgbZone { Name = "DRAM", Offset = 0, Count = ledCount } };
        _positions = new LedPos[ledCount];
        for (int i = 0; i < ledCount; i++)
            _positions[i] = new LedPos(ledCount <= 1 ? 0.5f : i / (float)(ledCount - 1), 0.5f);
    }

    /*-----------------------------------------------------*\
    | ENE register protocol                                 |
    \*-----------------------------------------------------*/
    static ushort Swap(ushort reg) => (ushort)(((reg << 8) & 0xFF00) | ((reg >> 8) & 0x00FF));

    static int RegRead(PawnSmbus bus, byte addr, ushort reg)
    {
        bus.WriteWordData(addr, 0x00, Swap(reg));
        return bus.ReadByteData(addr, 0x81);
    }

    /// <summary>False when either transaction failed. A failed register select
    /// must not be masked by a data write that then lands in whatever register
    /// was selected last.</summary>
    static bool RegWrite(PawnSmbus bus, byte addr, ushort reg, byte val)
        => bus.WriteWordData(addr, 0x00, Swap(reg)) && bus.WriteByteData(addr, 0x01, val);

    bool RegWriteBlock(ushort reg, ReadOnlySpan<byte> data)
    {
        _bus.WriteWordData(_addr, 0x00, Swap(reg));
        if (_bus.WriteBlockData(_addr, 0x03, data)) return true;
        // Fallback: byte-at-a-time through the auto-increment data register.
        foreach (var b in data)
            if (!_bus.WriteByteData(_addr, 0x01, b)) return false;
        return true;
    }

    /// <summary>Block write with NO byte fallback — used by the batched color
    /// path so a host that rejects large blocks reports failure cleanly and the
    /// caller can revert to small chunks instead of degrading to per-byte I/O.</summary>
    bool TryBlock(ushort reg, ReadOnlySpan<byte> data)
    {
        _bus.WriteWordData(_addr, 0x00, Swap(reg));
        return _bus.WriteBlockData(_addr, 0x03, data);
    }

    /*-----------------------------------------------------*\
    | Detection (OpenRGB's remap-then-probe sequence)       |
    \*-----------------------------------------------------*/
    public static List<IRgbDevice> DetectAll()
    {
        var found = new List<IRgbDevice>();
        var bus = PawnSmbus.TryOpenAny();
        if (bus == null) return found;

        // Remap: while a controller answers at the shared 0x77 address, assign
        // it (per slot) the next free address from the candidate list.
        int addressIdx = -1;
        for (int slot = 0; slot < 8; slot++)
        {
            if (bus.ReadByte(0x77) < 0) break;
            do
            {
                addressIdx++;
                if (addressIdx >= CandidateAddresses.Length) break;
            } while (bus.ReadByte(CandidateAddresses[addressIdx]) >= 0);
            if (addressIdx >= CandidateAddresses.Length) break;

            RegWrite(bus, 0x77, REG_SLOT_INDEX, (byte)slot);
            RegWrite(bus, 0x77, REG_I2C_ADDRESS, (byte)(CandidateAddresses[addressIdx] << 1));
        }

        int stick = 0;
        var lease = new BusLease(bus);
        foreach (byte addr in CandidateAddresses)
        {
            if (!TestForEne(bus, addr)) { Thread.Sleep(1); continue; }

            // Device name/version string (16 bytes at 0x1000).
            var nameBytes = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                int v = RegRead(bus, addr, (ushort)(REG_DEVICE_NAME + i));
                nameBytes[i] = (byte)Math.Max(v, 0);
            }
            string version = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0', ' ');

            int ledCount = RegRead(bus, addr, REG_CONFIG_TABLE + CONFIG_LED_COUNT);
            if (ledCount is <= 0 or > 64) ledCount = 8;

            stick++;
            found.Add(new EneDram(lease, addr, $"ENE DRAM #{stick} (0x{addr:X2})", version, ledCount));
            Thread.Sleep(1);
        }

        if (found.Count == 0) bus.Dispose();
        return found;
    }

    static bool TestForEne(PawnSmbus bus, byte addr)
    {
        int res = bus.ReadByte(addr);
        if (res < 0) res = bus.ReadByteData(addr, 0x00);
        if (res < 0) return false;

        // ENE identity: registers 0xA0..0xAF read back 0x00..0x0F.
        for (int i = 0xA0; i < 0xB0; i++)
            if (bus.ReadByteData(addr, (byte)i) != i - 0xA0) return false;
        return true;
    }

    /// <summary>Verbose write-path diagnostic: enable direct, write red, read
    /// everything back from both color register banks.</summary>
    public string Diagnose()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{Name}: directReg=0x{_directReg:X4} leds={_ledCount}");

        var nameBytes = new byte[16];
        for (int i = 0; i < 16; i++) nameBytes[i] = (byte)Math.Max(RegRead(_bus, _addr, (ushort)(REG_DEVICE_NAME + i)), 0);
        sb.AppendLine($"  version='{System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0', ' ')}'");

        bool w1 = RegWrite(_bus, _addr, REG_DIRECT, 0x01);
        bool w2 = RegWrite(_bus, _addr, REG_APPLY, 0x01);
        int direct = RegRead(_bus, _addr, REG_DIRECT);
        sb.AppendLine($"  enable direct: write={w1}/{w2} readback={direct}");

        bool blk = RegWriteBlock(_directReg, new byte[] { 255, 0, 0 });   // R,B,G = red
        sb.Append($"  block write ok={blk}; readback 0x{_directReg:X4}:");
        for (int i = 0; i < 6; i++) sb.Append($" {RegRead(_bus, _addr, (ushort)(_directReg + i)):X2}");
        sb.AppendLine();

        ushort other = _directReg == REG_COLORS_DIRECT_V2 ? REG_COLORS_DIRECT_V1 : REG_COLORS_DIRECT_V2;
        RegWriteBlock(other, new byte[] { 255, 0, 0 });
        sb.Append($"  other bank 0x{other:X4} readback:");
        for (int i = 0; i < 6; i++) sb.Append($" {RegRead(_bus, _addr, (ushort)(other + i)):X2}");
        sb.AppendLine();

        sb.Append($"  mode=0x{RegRead(_bus, _addr, 0x8021):X2} config[0..7]:");
        for (int i = 0; i < 8; i++) sb.Append($" {RegRead(_bus, _addr, (ushort)(REG_CONFIG_TABLE + i)):X2}");
        return sb.ToString();
    }

    /*-----------------------------------------------------*\
    | Color output                                          |
    \*-----------------------------------------------------*/
    readonly object _writeLock = new();

    public void SetColors(IReadOnlyList<Rgb> colors)
    {
        lock (_writeLock)
        {
            if (_last != null && colors.Count == _last.Length)
            {
                bool same = true;
                for (int i = 0; i < colors.Count; i++) if (_last[i] != colors[i]) { same = false; break; }
                if (same) return;   // (index loop: SequenceEqual boxed two enumerators per frame)
            }

            if (!_directOn)
            {
                // Latch only on success: a NAKed enable used to be recorded as
                // done, leaving the stick on its onboard effect (colour writes
                // landing, nothing showing) until a rescan, with no log line.
                bool w1 = RegWrite(_bus, _addr, REG_DIRECT, 0x01);
                bool w2 = RegWrite(_bus, _addr, REG_APPLY, 0x01);
                _directOn = w1 && w2;
                if (!_directOn)
                    Log.Occasional($"ene:{_addr:X2}", "EneDram",
                        $"direct-mode enable failed at 0x{_addr:X2} (direct={w1} apply={w2}) - will retry on the next frame");
            }

            // Direct colors are R,B,G per LED. BATCHED: the ENE data register
            // auto-increments (the byte fallback in RegWriteBlock relies on
            // exactly that), so a whole 8-LED stick (24 B) fits one SMBus
            // block write — select + block = 2 bus transactions per frame
            // instead of 16, each of which took the machine-wide SMBus mutex
            // and a kernel ioctl. Matches OpenRGB's ENERegisterWriteBlock.
            // Self-healing: if this host rejects large blocks (the old code's
            // comment suggests one once did), the FIRST failure flips this
            // stick back to the proven 3-byte chunks and repaints the same
            // frame through the legacy path.
            var buf = _wireBuf ??= new byte[_ledCount * 3];
            for (int i = 0; i < _ledCount; i++)
            {
                var c = i < colors.Count ? colors[i] : Rgb.Black;
                buf[i * 3 + 0] = c.R;
                buf[i * 3 + 1] = c.B;
                buf[i * 3 + 2] = c.G;
            }
            if (_batchedBlocks)
            {
                bool ok = true;
                for (int off = 0; off < buf.Length && ok; off += 30)   // 30 = 10 LEDs, under the 32 B SMBus cap
                    ok = TryBlock((ushort)(_directReg + off), buf.AsSpan(off, Math.Min(30, buf.Length - off)));
                if (!ok)
                {
                    _batchedBlocks = false;
                    Log.Warn("EneDram", $"host rejected batched block write at 0x{_addr:X2} - reverting to 3-byte chunks");
                }
            }
            if (!_batchedBlocks)
                for (int off = 0; off < buf.Length; off += 3)
                    RegWriteBlock((ushort)(_directReg + off), buf.AsSpan(off, 3));

            // Don't dedup a frame written while direct mode is still off: the
            // next call (engine keepalive or user apply) must repeat the enable.
            if (!_directOn) return;
            if (_last == null || _last.Length != colors.Count) _last = new Rgb[colors.Count];
            for (int i = 0; i < colors.Count; i++) _last[i] = colors[i];
        }
    }

    public void Dispose() => _lease.Release();
}

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
public sealed class EneDram : IRgbDevice, IHardwareModes
{
    const ushort REG_DEVICE_NAME = 0x1000;
    const ushort REG_CONFIG_TABLE = 0x1C00;
    const int CONFIG_LED_COUNT = 0x02;
    internal const ushort REG_DIRECT = 0x8020;
    // Onboard effect engine (OpenRGB's ENESMBusController, GPL-2.0 like this
    // project): a mode register, and effect colors in a SEPARATE window from
    // the direct-mode colors.
    internal const ushort REG_MODE = 0x8021;

    // The effect color window is PAIRED with the direct one: a V1 controller
    // has direct at 0x8000 and effects at 0x8010, 15 bytes each; a V2 has
    // direct at 0x8100 and effects at 0x8160, 30 bytes each. Writing V1's
    // effect register on a V2 stick puts the color in a bank the V2 effect
    // engine does not read, so the mode switch works and the color is
    // whatever the firmware happened to have. DDR5 sticks are V2.
    internal const ushort REG_COLORS_EFFECT_V1 = 0x8010;
    internal const ushort REG_COLORS_EFFECT_V2 = 0x8160;

    /// <summary>How many LEDs fit in each effect window: 15 and 30 bytes, three
    /// bytes per LED. Derived rather than remembered, because overrunning V1's
    /// window walks straight into REG_DIRECT and REG_MODE.</summary>
    internal const int EFFECT_BYTES_V1 = 15, EFFECT_BYTES_V2 = 30;
    internal static int EffectColorLeds(ushort effectReg) =>
        (effectReg == REG_COLORS_EFFECT_V1 ? EFFECT_BYTES_V1 : EFFECT_BYTES_V2) / 3;
    const byte MODE_STATIC = 1, MODE_BREATHING = 2, MODE_FLASHING = 3,
               MODE_SPECTRUM = 4, MODE_RAINBOW = 5;
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
    readonly ushort _effectReg;
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
        bool v1 = V1Versions.Contains(version);
        _directReg = v1 ? REG_COLORS_DIRECT_V1 : REG_COLORS_DIRECT_V2;
        _effectReg = v1 ? REG_COLORS_EFFECT_V1 : REG_COLORS_EFFECT_V2;
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

    // Every ENE transaction is two bus operations: select the register, then
    // move the data. The select can fail on its own (mutex timeout, NAK), and
    // when it does the data operation still runs against WHATEVER REGISTER WAS
    // SELECTED LAST - a color block written after a failed select lands in a
    // mode or control register, and a read returns that register's stale
    // byte as if it were the one asked for. So every helper below
    // short-circuits on the select; only RegWrite used to.

    /// <summary>The byte, or -1 when EITHER transaction failed. A failed select
    /// followed by a successful read would hand detection a stale byte from
    /// the previous register as the device name or LED count.</summary>
    static int RegRead(PawnSmbus bus, byte addr, ushort reg)
    {
        if (!bus.WriteWordData(addr, 0x00, Swap(reg))) return -1;
        return bus.ReadByteData(addr, 0x81);
    }

    /// <summary>False when either transaction failed. A failed register select
    /// must not be masked by a data write that then lands in whatever register
    /// was selected last.</summary>
    static bool RegWrite(PawnSmbus bus, byte addr, ushort reg, byte val)
        => bus.WriteWordData(addr, 0x00, Swap(reg)) && bus.WriteByteData(addr, 0x01, val);

    bool RegWriteBlock(ushort reg, ReadOnlySpan<byte> data)
    {
        if (!_bus.WriteWordData(_addr, 0x00, Swap(reg))) return false;
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
        if (!_bus.WriteWordData(_addr, 0x00, Swap(reg))) return false;
        return _bus.WriteBlockData(_addr, 0x03, data);
    }

    /*-----------------------------------------------------*\
    | Detection (OpenRGB's remap-then-probe sequence)       |
    \*-----------------------------------------------------*/
    public static List<IRgbDevice> DetectAll()
    {
        var found = new List<IRgbDevice>();
        var bus = PawnSmbus.TryOpenAny();
        if (bus == null)
        {
            // Three different situations that all used to look like "you have
            // no RGB memory". Lit RAM is the thing people notice missing first,
            // and every one of these has a different answer.
            if (!Native.PawnIO.IsAvailable)
                DetectionNotes.Report(nameof(EneDram), "RGB memory", BlockReason.DriverMissing,
                    "the PawnIO driver is not installed, and the memory's lighting sits on the "
                    + "SMBus which cannot be reached without it",
                    "install PawnIO from Settings > Devices");
            else if (!DiagnosticReport.IsAdmin())
                DetectionNotes.Report(nameof(EneDram), "RGB memory", BlockReason.NeedsAdministrator,
                    "PawnIO is installed but would not open, which is what happens when the app "
                    + "is not running as administrator",
                    "run UnifiedRGB as administrator");
            else
                DetectionNotes.Report(nameof(EneDram), "RGB memory", BlockReason.Failed,
                    "PawnIO is installed and we are elevated, but no SMBus controller answered",
                    "your chipset may not be one of the two we support (AMD PIIX4, Intel I801)");
            return found;
        }

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
    /*-----------------------------------------------------*\
    | Hardware persistence.                                  |
    \*-----------------------------------------------------*/

    static readonly string[] Effects = { "Breathing", "Flashing", "Spectrum cycle", "Rainbow" };

    public HardwareExitCaps ExitCaps => HardwareExitCaps.Static | HardwareExitCaps.Effects;
    public IReadOnlyList<string> HardwareEffects => Effects;

    /// <summary>Nothing to go back to: the stick has no saved profile, only
    /// whichever mode was last written to it.</summary>
    // Nothing is sent, so nothing landed. ExitCaps does not offer it, so
    // HardwareExit never asks.
    public bool ReturnToHardware() => false;

    public bool SetHardwareStatic(Rgb color) => SetOnboardMode(MODE_STATIC, color);

    public bool SetHardwareEffect(string name, Rgb? color) => SetOnboardMode(name switch
    {
        "Breathing" => MODE_BREATHING,
        "Flashing" => MODE_FLASHING,
        "Spectrum cycle" => MODE_SPECTRUM,
        "Rainbow" => MODE_RAINBOW,
        _ => MODE_STATIC,
    }, color ?? Rgb.White);

    /// <summary>Hand the LEDs to the stick's own effect engine: colors first,
    /// then the mode, then direct mode OFF (which is what actually transfers
    /// control), then apply. Speed and direction are left at whatever the
    /// firmware has, rather than guessed at.</summary>
    /// <summary>True only when every register of the handover landed. The
    /// whole sequence is attempted even after a NAK - stopping half way would
    /// leave the stick in direct mode with an effect color written and no
    /// host driving it, which is darker than either end state - but the
    /// verdict is the AND, so a must-land caller sends the sequence again
    /// rather than believing a partial handover.</summary>
    bool SetOnboardMode(byte mode, Rgb color)
    {
        lock (_writeLock)
        {
            Span<byte> triple = stackalloc byte[3];
            triple[0] = color.R; triple[1] = color.B; triple[2] = color.G;   // same order as direct
            int slots = Math.Min(_ledCount, EffectColorLeds(_effectReg));
            bool ok = true;
            for (int i = 0; i < slots; i++)
                ok &= RegWriteBlock((ushort)(_effectReg + i * 3), triple);

            ok &= RegWrite(_bus, _addr, REG_MODE, mode);
            ok &= RegWrite(_bus, _addr, REG_DIRECT, 0x00);
            ok &= RegWrite(_bus, _addr, REG_APPLY, 0x01);

            // The next launch has to re-enable direct mode and repaint: both of
            // these describe a stick state we have just replaced.
            _directOn = false;
            _last = null;
            return ok;
        }
    }

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

    /// <summary>Drop the cached frame so the next one is written even if it is
    /// identical (the must-land path and any mode change).</summary>
    public void InvalidateCache() { lock (_writeLock) _last = null; }

    public bool SetColors(IReadOnlyList<Rgb> colors)
    {
        lock (_writeLock)
        {
            // (index loop: SequenceEqual boxed two enumerators per frame). An
            // identical frame is skipped and reported as a SUCCESS: the stick
            // is already showing exactly what was asked for.
            if (WritePolicy.Unchanged(_last, colors)) return true;

            if (!_directOn)
            {
                // Latch only on success: a NAKed enable used to be recorded as
                // done, leaving the stick on its onboard effect (color writes
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
            // `landed` is the verdict for the WHOLE frame, whichever path wrote
            // it: the frame is cached only when every chunk was accepted. The
            // fallback path used to discard its results, so a stick that NAKed
            // the color bytes still had the frame recorded in _last, and every
            // identical frame after it - the engine's once-a-second keepalive
            // included - was deduped away. The stick sat on stale colors until
            // the color changed or a rescan.
            bool landed = false;
            if (_batchedBlocks)
            {
                landed = true;
                for (int off = 0; off < buf.Length && landed; off += 30)   // 30 = 10 LEDs, under the 32 B SMBus cap
                    landed = TryBlock((ushort)(_directReg + off), buf.AsSpan(off, Math.Min(30, buf.Length - off)));
                if (!landed)
                {
                    _batchedBlocks = false;
                    Log.Warn("EneDram", $"host rejected batched block write at 0x{_addr:X2} - reverting to 3-byte chunks");
                }
            }
            if (!_batchedBlocks)
            {
                landed = true;
                for (int off = 0; off < buf.Length; off += 3)
                    // Stop at the first refused chunk rather than finishing the
                    // frame: each transaction can wait up to 2 s on the
                    // machine-wide SMBus mutex, and a partial frame is re-sent
                    // in full on the next call anyway.
                    if (!RegWriteBlock((ushort)(_directReg + off), buf.AsSpan(off, 3))) { landed = false; break; }
            }
            if (!landed)
                return WritePolicy.Refused(ref _last, $"ene:{_addr:X2}:frame", "EneDram",
                    $"color write failed at 0x{_addr:X2} - the frame will be re-sent on the next call");

            // Don't dedup a frame written while direct mode is still off, and
            // do not call it delivered either: the color bytes were accepted
            // but the stick is still running its onboard effect, so nothing of
            // this frame is visible. The next call (engine keepalive or user
            // apply) must repeat the enable, and a must-land caller must keep
            // trying rather than stop at a write that changed nothing.
            if (!_directOn) return false;
            WritePolicy.Cache(ref _last, colors);
            return true;
        }
    }

    public void Dispose() => _lease.Release();
}

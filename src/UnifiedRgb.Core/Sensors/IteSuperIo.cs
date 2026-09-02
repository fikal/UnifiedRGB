using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Sensors;

/*-----------------------------------------------------------*\
| Motherboard fan RPM + temperature MONITOR via the ITE       |
| Super-I/O chip, through the signed PawnIO LpcIO module.     |
|                                                             |
| The module only gives raw primitives; the caller owns the   |
| flow (matches the module source, PawnIO.Modules/LpcIO.p):   |
|   select_slot(0|1)   -> register port 0x2E / 0x4E           |
|   pio_outb(port, b)  -> raw write; register port is         |
|                         whitelisted, so the ITE MB-PnP      |
|                         enter key (87 01 55 55|AA) goes     |
|                         through here                        |
|   superio_inb/outb   -> index/data access at the register   |
|                         port (needs config mode entered)    |
|   find_bars()        -> scans LDN base registers WHILE in   |
|                         config mode and whitelists them for |
|                         later pio_inb/outb (the EC window)  |
| IMPORTANT: DEFINE_IOCTL_SIZED enforces EXACT in/out counts  |
| (a declared count of 0 = unchecked), so every call passes   |
| precisely sized arrays.                                     |
|                                                             |
| READ-ONLY by design in phase 1: config-space writes are     |
| limited to the standard enter/exit key and LDN selection —  |
| the same sequence every hardware monitor performs. EC       |
| register reads share the machine-wide ISA-bus mutex so we   |
| don't tear TRCC/BIOS tooling transactions (or they ours).   |
\*-----------------------------------------------------------*/
public sealed class IteSuperIo : IDisposable
{
    readonly PawnIO _io;
    readonly ushort _ecBase;
    readonly ulong _slot;
    readonly Mutex? _isaMutex;
    readonly object _lock = new();

    public ushort ChipId { get; }

    // Environment Controller register map (16-bit tach pairs: low, high).
    static readonly (byte Lo, byte Hi)[] FanRegs =
    {
        (0x0D, 0x18), (0x0E, 0x19), (0x0F, 0x1A), (0x80, 0x81), (0x82, 0x83), (0x4C, 0x4D),
    };
    static readonly byte[] TempRegs = { 0x29, 0x2A, 0x2B };
    static readonly byte[] PwmRegs = { 0x15, 0x16, 0x17, 0x88, 0x89, 0x8A };

    IteSuperIo(PawnIO io, ushort chipId, ushort ecBase, ulong slot, Mutex? isaMutex)
    {
        _io = io; ChipId = chipId; _ecBase = ecBase; _slot = slot; _isaMutex = isaMutex;
    }

    /// <summary>Open every ITE Super-I/O in the machine (0x2E and, on boards
    /// with a secondary fan controller — Gigabyte especially — 0x4E too).
    /// Needs elevation (PawnIO).</summary>
    public static List<IteSuperIo> OpenAll()
    {
        var list = new List<IteSuperIo>();
        if (!PawnIO.IsAvailable) return list;
        var blob = ReadEmbedded("LpcIO.bin");
        if (blob == null) return list;
        for (ulong slot = 0; slot < 2; slot++)
        {
            var s = TryOpen(blob, slot);
            if (s != null) list.Add(s);
        }
        if (list.Count == 0) Log.Info("superio", "no ITE Super-I/O found");
        return list;
    }

    /// <summary>Probe one Super-I/O slot (0 = 0x2E, 1 = 0x4E) for a known ITE
    /// chip with a valid Environment Controller. Each instance gets its own
    /// module handle: the module's slot selection and BAR whitelist are
    /// per-handle state, so two chips must not share one.</summary>
    static IteSuperIo? TryOpen(byte[] blob, ulong slot)
    {
        PawnIO? io = null;
        var isa = OpenIsaMutex();
        bool held = false, keepIsa = false;
        try
        {
            io = PawnIO.LoadModule(blob);
            if (io == null) return null;
            held = TryAcquire(isa, 1000);

            if (Call(io, "ioctl_select_slot", slot) < 0 || !EnterConfig(io, slot))
            {
                io.Dispose(); return null;
            }

            int hi = In1(io, "ioctl_superio_inb", 0x20);
            int lo = In1(io, "ioctl_superio_inb", 0x21);
            ushort chip = (ushort)(((hi & 0xFF) << 8) | (lo & 0xFF));
            // Every ITE Super-I/O identifies as 0x8xxx; reject bus floats.
            if (hi < 0 || lo < 0 || (chip & 0xF000) != 0x8000 || chip == 0x8FFF)
            {
                if (chip != 0xFFFF && chip != 0)
                    Log.Info("superio", $"slot {slot}: id 0x{chip:X4} (not ITE)");
                ExitConfig(io);
                io.Dispose(); return null;
            }

            // LDN 4 = Environment Controller; its I/O base at 0x60/0x61.
            Call(io, "ioctl_superio_outb", 0x07, 0x04);
            int bh = In1(io, "ioctl_superio_inb", 0x60);
            int bl = In1(io, "ioctl_superio_inb", 0x61);
            ushort baseAddr = (ushort)(((bh & 0xFF) << 8) | (bl & 0xFF));

            // Whitelist the LDN base ranges for runtime pio reads — must
            // happen while still in config mode.
            bool bars = Call(io, "ioctl_find_bars") >= 0;
            ExitConfig(io);

            if (bh < 0 || bl < 0 || !bars || baseAddr < 0x100 || baseAddr == 0xFFFF)
            {
                Log.Info("superio", $"slot {slot}: ITE 0x{chip:X4} but EC unusable (base 0x{baseAddr:X4}, bars={bars})");
                io.Dispose(); return null;
            }

            Log.Info("superio", $"ITE chip 0x{chip:X4} at slot {slot}, EC base 0x{baseAddr:X4}");
            keepIsa = true;
            return new IteSuperIo(io, chip, baseAddr, slot, isa);
        }
        catch (Exception ex)
        {
            Log.Warn("superio", $"slot {slot} probe failed: {ex.Message}");
            io?.Dispose();
            return null;
        }
        finally
        {
            // Release BEFORE disposing, and only dispose when the chip isn't
            // keeping the mutex. Closing an OWNED mutex handle does not release
            // it: the kernel object stays owned by this thread until the thread
            // exits, which starved every other hardware monitor (and our own
            // timer-thread reads) on any board whose probe hit an early return.
            if (held) { try { isa?.ReleaseMutex(); } catch { } }
            if (!keepIsa) isa?.Dispose();
        }
    }

    /// <summary>ITE MB-PnP "enter config" key, written to the register port
    /// itself: 87 01 55 55 at 0x2E, 87 01 55 AA at 0x4E.</summary>
    static bool EnterConfig(PawnIO io, ulong slot)
    {
        ulong port = slot == 0 ? 0x2Eul : 0x4Eul;
        byte last = slot == 0 ? (byte)0x55 : (byte)0xAA;
        foreach (byte b in stackalloc byte[] { 0x87, 0x01, 0x55, last })
            if (Call(io, "ioctl_pio_outb", port, b) < 0) return false;
        return true;
    }

    /// <summary>ITE "exit config": write 0x02 to config register 0x02.</summary>
    static void ExitConfig(PawnIO io) => Call(io, "ioctl_superio_outb", 0x02, 0x02);

    /// <summary>Call with no outputs expected; >=0 on success.</summary>
    static long Call(PawnIO io, string fn, params ulong[] args)
        => io.Execute(fn, args, Array.Empty<ulong>());

    /// <summary>Call with exactly one output; returns it, or -1 on failure.</summary>
    static int In1(PawnIO io, string fn, params ulong[] args)
    {
        var outv = new ulong[1];
        return io.Execute(fn, args, outv) >= 0 ? (int)(outv[0] & 0xFF) : -1;
    }

    int EcRead(byte reg)
    {
        // Address port = base+5, data port = base+6 (standard ITE EC window);
        // find_bars whitelisted the range during init.
        if (Call(_io, "ioctl_pio_outb", (ulong)(_ecBase + 5), reg) < 0) return -1;
        return In1(_io, "ioctl_pio_inb", (ulong)(_ecBase + 6));
    }

    bool EcWrite(byte reg, byte val)
    {
        if (Call(_io, "ioctl_pio_outb", (ulong)(_ecBase + 5), reg) < 0) return false;
        return Call(_io, "ioctl_pio_outb", (ulong)(_ecBase + 6), val) >= 0;
    }

    /*-----------------------------------------------------*\
    | Fan control (phase 2). Strategy proven by the field   |
    | hardware monitors: before the first manual write to a |
    | PWM register, SAVE its original value; restoring that |
    | byte hands the header back to the BIOS curve exactly  |
    | as it was — no need to decode per-chip mode bits.     |
    | On this family bit7 of 0x15-0x17 selects SmartFan     |
    | auto; a raw value with bit7 clear = fixed duty.       |
    \*-----------------------------------------------------*/
    readonly byte?[] _origPwm = new byte?[6];

    public int FanCount => FanRegs.Length;

    /// <summary>The PWM register's current raw value, or -1.</summary>
    public int ReadPwmRaw(int fan)
    {
        if ((uint)fan >= (uint)PwmRegs.Length) return -1;
        lock (_lock)
        {
            bool held = TryAcquire(_isaMutex, 250);
            try { return EcRead(PwmRegs[fan]); }
            finally { if (held) { try { _isaMutex?.ReleaseMutex(); } catch { } } }
        }
    }

    /// <summary>Raw EC register access for the CLI recipe probes ONLY — the
    /// app itself goes through the typed fan-control methods.</summary>
    public int ReadEcRaw(byte reg)
    {
        lock (_lock)
        {
            bool held = TryAcquire(_isaMutex, 250);
            try { return EcRead(reg); }
            finally { if (held) { try { _isaMutex?.ReleaseMutex(); } catch { } } }
        }
    }

    /// <summary>See <see cref="ReadEcRaw"/> — probe use only.</summary>
    public bool WriteEcRaw(byte reg, byte val)
    {
        lock (_lock)
        {
            bool held = TryAcquire(_isaMutex, 250);
            try { return EcWrite(reg, val); }
            finally { if (held) { try { _isaMutex?.ReleaseMutex(); } catch { } } }
        }
    }

    /// <summary>Raw port read through this chip's whitelist (the module only
    /// allows ports find_bars discovered on THIS chip's logical devices).
    /// Used by the Gigabyte ECIO interface (0x3F0/0x3F4) that lives on the
    /// secondary chip. -1 = denied/failed.</summary>
    public int PioInb(ushort port)
    {
        lock (_lock)
        {
            bool held = TryAcquire(_isaMutex, 250);
            try { return In1(_io, "ioctl_pio_inb", port); }
            finally { if (held) { try { _isaMutex?.ReleaseMutex(); } catch { } } }
        }
    }

    /// <summary>See <see cref="PioInb"/>.</summary>
    public bool PioOutb(ushort port, byte val)
    {
        lock (_lock)
        {
            bool held = TryAcquire(_isaMutex, 250);
            try { return Call(_io, "ioctl_pio_outb", port, val) >= 0; }
            finally { if (held) { try { _isaMutex?.ReleaseMutex(); } catch { } } }
        }
    }

    /// <summary>Read-only dump of this chip's logical devices: LDN, activate
    /// bit (0x30), BAR0 (0x60/61), BAR1 (0x62/63). Probe use.</summary>
    public List<(int Ldn, int Active, int Bar0, int Bar1)> DumpLdns(int maxLdn = 0x20)
    {
        var list = new List<(int, int, int, int)>();
        lock (_lock)
        {
            bool held = TryAcquire(_isaMutex, 1000);
            try
            {
                if (!EnterConfig(_io, _slot)) return list;
                try
                {
                    for (ulong ldn = 0; ldn <= (ulong)maxLdn; ldn++)
                    {
                        Call(_io, "ioctl_superio_outb", 0x07, ldn);
                        int act = In1(_io, "ioctl_superio_inb", 0x30);
                        int b0h = In1(_io, "ioctl_superio_inb", 0x60);
                        int b0l = In1(_io, "ioctl_superio_inb", 0x61);
                        int b1h = In1(_io, "ioctl_superio_inb", 0x62);
                        int b1l = In1(_io, "ioctl_superio_inb", 0x63);
                        list.Add(((int)ldn, act, (b0h << 8) | b0l, (b1h << 8) | b1l));
                    }
                }
                finally { ExitConfig(_io); }   // never leave the chip in config mode
            }
            finally { if (held) { try { _isaMutex?.ReleaseMutex(); } catch { } } }
        }
        return list;
    }

    /// <summary>Write a raw PWM register value, saving the original first so
    /// RestoreFan can undo it. bit7 clear = fixed duty on this family.</summary>
    public bool WritePwmRaw(int fan, byte value)
    {
        if ((uint)fan >= (uint)PwmRegs.Length) return false;
        lock (_lock)
        {
            bool held = TryAcquire(_isaMutex, 250);
            try
            {
                if (_origPwm[fan] == null)
                {
                    int cur = EcRead(PwmRegs[fan]);
                    if (cur < 0) return false;
                    _origPwm[fan] = (byte)cur;
                }
                if (!EcWrite(PwmRegs[fan], value)) return false;
                Log.Info("superio", $"fan {fan + 1} pwm 0x{PwmRegs[fan]:X2} <- 0x{value:X2} (orig 0x{_origPwm[fan]:X2})");
                return true;
            }
            finally { if (held) { try { _isaMutex?.ReleaseMutex(); } catch { } } }
        }
    }

    /// <summary>Percent → the raw byte written to the PWM register (8-bit
    /// duty assumption; the hold-check in SensorHub.Tick flags it in the log
    /// if this chip's SmartFan overwrites the register instead).</summary>
    internal static byte DutyByte(int percent)
        => (byte)Math.Round(Math.Clamp(percent, 0, 100) * 255.0 / 100);

    /// <summary>Manual duty as percent, clamped 0-100. The caller owns policy
    /// floors (pump minimums etc.).</summary>
    public bool SetFanDutyPercent(int fan, int percent)
        => WritePwmRaw(fan, DutyByte(percent));

    /// <summary>True if this fan has a saved original (a manual write happened).</summary>
    public bool IsFanOverridden(int fan) => (uint)fan < (uint)_origPwm.Length && _origPwm[fan] != null;

    /// <summary>Original register value saved before the first manual write
    /// (for the crash-recovery marker), or null.</summary>
    public byte? SavedPwm(int fan) => (uint)fan < (uint)_origPwm.Length ? _origPwm[fan] : null;

    /// <summary>Hand the header back to whatever ran it before us.</summary>
    public bool RestoreFan(int fan)
    {
        if ((uint)fan >= (uint)_origPwm.Length || _origPwm[fan] is not byte orig) return true;
        lock (_lock)
        {
            bool held = TryAcquire(_isaMutex, 250);
            try
            {
                if (!EcWrite(PwmRegs[fan], orig)) return false;
                _origPwm[fan] = null;
                Log.Info("superio", $"fan {fan + 1} restored to 0x{orig:X2}");
                return true;
            }
            finally { if (held) { try { _isaMutex?.ReleaseMutex(); } catch { } } }
        }
    }

    /// <summary>Crash recovery: write a known original value back even though
    /// this instance never overrode the fan (the previous process did).</summary>
    public bool ForceRestore(int fan, byte original)
    {
        if ((uint)fan >= (uint)PwmRegs.Length) return false;
        lock (_lock)
        {
            bool held = TryAcquire(_isaMutex, 250);
            try { return EcWrite(PwmRegs[fan], original); }
            finally { if (held) { try { _isaMutex?.ReleaseMutex(); } catch { } } }
        }
    }

    public void RestoreAllFans()
    {
        for (int i = 0; i < _origPwm.Length; i++) RestoreFan(i);
    }

    public sealed record Reading(double?[] TempsC, int?[] FanRpm, int?[] FanDutyPct);

    /// <summary>One monitoring sweep: temps (°C), fan RPMs, PWM duty %.
    /// includePwm=false skips the 6 duty registers (12 kernel ioctls) — the
    /// SensorHub fallback path never reads them.</summary>
    public Reading Read(bool includePwm = true)
    {
        lock (_lock)
        {
            // EC access is a stateful two-port sequence; the ISA mutex keeps
            // us and other monitors (TRCC, BIOS tools) from tearing each other.
            bool held = TryAcquire(_isaMutex, 250);
            try
            {
                var temps = new double?[TempRegs.Length];
                for (int i = 0; i < TempRegs.Length; i++)
                {
                    int v = EcRead(TempRegs[i]);
                    temps[i] = v is > 0 and < 127 ? v : null;   // 0/128+/-1 = absent
                }

                var rpm = new int?[FanRegs.Length];
                for (int i = 0; i < FanRegs.Length; i++)
                {
                    int lo = EcRead(FanRegs[i].Lo), hi = EcRead(FanRegs[i].Hi);
                    if (lo < 0 || hi < 0) { rpm[i] = null; continue; }
                    int count = (hi << 8) | lo;
                    rpm[i] = count is > 0 and < 0xFFFF ? (int)(1_350_000.0 / (count * 2)) : null;
                    if (rpm[i] is < 30 or > 20_000) rpm[i] = null;   // implausible
                }

                var duty = new int?[PwmRegs.Length];
                if (includePwm)
                    for (int i = 0; i < PwmRegs.Length; i++)
                    {
                        int v = EcRead(PwmRegs[i]);
                        duty[i] = v >= 0 ? (int)Math.Round(Math.Min(v, 255) * 100.0 / 255) : null;
                    }
                return new Reading(temps, rpm, duty);
            }
            finally
            {
                if (held) { try { _isaMutex?.ReleaseMutex(); } catch { } }
            }
        }
    }

    /// <summary>The machine-wide ISA-bus mutex hardware monitors share
    /// (documented in the LpcIO module as a required courtesy).</summary>
    static Mutex? OpenIsaMutex()
    {
        try { return new Mutex(false, @"Global\Access_ISABUS.HTP.Method"); }
        catch
        {
            try { return new Mutex(false, "Access_ISABUS.HTP.Method"); }
            catch { return null; }
        }
    }

    static bool TryAcquire(Mutex? m, int ms)
    {
        if (m == null) return false;
        try { return m.WaitOne(ms); }
        catch (AbandonedMutexException) { return true; }   // acquired anyway
        catch { return false; }
    }

    internal static byte[]? ReadEmbedded(string file) => PawnIO.ReadEmbeddedModule(file);

    public void Dispose()
    {
        _io.Dispose();
        _isaMutex?.Dispose();
    }
}

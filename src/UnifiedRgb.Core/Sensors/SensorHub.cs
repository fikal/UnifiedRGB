using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Sensors;

/*-----------------------------------------------------------*\
| Shared sensor source for the Cooling panel and the temp-    |
| reactive lighting. Same lazy lifecycle as the audio/keyboard|
| taps: the first reader starts a 1.5s background refresh;    |
| unused for a while, it stops.                               |
|                                                             |
| Ownership split (no two drivers touch one chip):            |
|   CPU temp   -> native PawnIO SMN (RyzenCpuTemperature)     |
|   GPU        -> native NvAPI (temp + per-fan RPM)           |
|   Motherboard-> LibreHardwareMonitor (temps, fan RPM, fan   |
|                 control incl. the vendor takeover) — the    |
|                 one piece that was per-board reverse eng.   |
\*-----------------------------------------------------------*/
public static class SensorHub
{
    const double RefreshSeconds = 1.5;
    const double IdleStopSeconds = 10;

    static readonly object _gate = new();
    static Timer? _timer;
    static long _lastReadTicks;   // last FULL reader (Cooling pane): arms the UI-only sweep
    static long _lastTempTicks;   // last temp-only reader (effects, LCD temp element)
    static bool _running;
    static bool _shutdown;

    static RyzenCpuTemperature? _cpu;
    static LhmFans? _lhm;
    // Board-fan fallback: on Win11 builds where LHM's ring0 driver is blocked
    // (vulnerable-driver blocklist / Memory Integrity) it returns no Super-I/O
    // sensors, so we read the ITE chip directly through PawnIO's signed LpcIO
    // module instead. Read-only monitoring; control stays a later phase.
    static List<IteBoardChip>? _iteChips;
    sealed record IteBoardChip(IteSuperIo Chip, int[] FanSlots, int[] TempSlots);
    static IntPtr _gpu;
    static bool _gpuFanCtl;
    static int _gpuMinDuty = 30;         // card-reported manual minimum
    static bool _gpuManualEngaged;       // we currently hold manual control
    static bool _sourcesOpened;

    public sealed record BoardTemp(string Name, double? TempC);

    static bool _inventoryLogged;

    /// <summary>Name and first reading of every board sensor, once per run.
    /// Super I/O chips expose more temperature slots than the board wires up,
    /// and the spares carry names like "Temperature #4"; this is how we tell
    /// which ones are real, here and in a support bundle.</summary>
    static void LogBoardInventory()
    {
        if (_inventoryLogged) return;
        _inventoryLogged = true;
        try
        {
            Log.Info("lhm", "board temps: " + string.Join(", ",
                BoardTemps.Select(t => $"{t.Name}={(t.TempC is double c ? c.ToString("0.#") : "null")}")));
        }
        catch { }
    }
    public sealed record BoardFan(string Name, int? Rpm, bool CanControl);

    // Latest snapshot. Nullable<double>/<int> are 16/8-byte structs, so a plain
    // auto-property could tear between the timer thread's write and a render-
    // thread read (HasValue = true, Value = 0 for one frame at the null->value
    // transition). Each value is published as a BOXED reference instead:
    // reference writes are atomic, and one box per 1.5 s tick is nothing.
    static object? _cpuTemp, _gpuTemp, _cpuLoad, _gpuLoad, _cpuVolt, _gpuVolt;
    public static double? CpuTempC { get => (double?)Volatile.Read(ref _cpuTemp); private set => Volatile.Write(ref _cpuTemp, value); }
    public static int? GpuTempC { get => (int?)Volatile.Read(ref _gpuTemp); private set => Volatile.Write(ref _gpuTemp, value); }
    public static double? CpuLoadPct { get => (double?)Volatile.Read(ref _cpuLoad); private set => Volatile.Write(ref _cpuLoad, value); }
    public static int? GpuLoadPct { get => (int?)Volatile.Read(ref _gpuLoad); private set => Volatile.Write(ref _gpuLoad, value); }
    /// <summary>CPU Vcore from the board's voltage rails (best-name match).</summary>
    public static double? CpuVoltage { get => (double?)Volatile.Read(ref _cpuVolt); private set => Volatile.Write(ref _cpuVolt, value); }
    public static double? GpuVoltage { get => (double?)Volatile.Read(ref _gpuVolt); private set => Volatile.Write(ref _gpuVolt, value); }
    /// <summary>One RPM per GPU fan (modern coolers have 2-3); null = no data.</summary>
    public static int[]? GpuFanRpms { get; private set; }
    public static BoardTemp[] BoardTemps { get; private set; } = Array.Empty<BoardTemp>();
    public static BoardFan[] BoardFans { get; private set; } = Array.Empty<BoardFan>();

    /// <summary>Charge of a wireless device, by device name.</summary>
    public sealed record BatteryLevel(string Name, int Percent, bool Charging);

    /// <summary>Latest charge of every wireless device. Pushed in by the app's
    /// battery poller on its own slow cadence rather than read during a sweep:
    /// a battery query is a round trip to a sleeping mouse, which has no place
    /// on a path that runs every second.</summary>
    public static BatteryLevel[] Batteries { get; private set; } = Array.Empty<BatteryLevel>();

    /// <summary>Replace the published charges (whole-array swap, so a reader
    /// always sees one consistent set).</summary>
    public static void PublishBatteries(BatteryLevel[] levels) => Batteries = levels;

    /// <summary>Hottest of CPU/GPU — the "how hard is the machine working"
    /// number the temp-reactive lighting rides.</summary>
    public static double? HottestC
    {
        get
        {
            double? c = CpuTempC, g = GpuTempC;
            if (c == null) return g;
            if (g == null) return c;
            return Math.Max(c.Value, g.Value);
        }
    }

    /// <summary>Callers invoke this every time they read; the hub lazily opens
    /// the sensor sources and keeps refreshing while anyone's interested. This
    /// is the FULL-snapshot touch (the Cooling pane, the LCD's RPM element): it
    /// arms the UI-only sweep — GPU RPM/load/voltage, CPU load, the LHM board
    /// sweep and the BoardTemps/BoardFans projections.</summary>
    public static void Touch()
    {
        Interlocked.Exchange(ref _lastReadTicks, DateTime.UtcNow.Ticks);
        EnsureRunning();
    }

    /// <summary>For readers that only need CpuTempC/GpuTempC/HottestC (the
    /// temp-reactive effects, the LCD's GPU-temp element): keeps the hub alive
    /// without arming the UI-only sweep. Those readers used to share Touch(),
    /// so any running temp effect or pump-LCD temp element kept the three
    /// non-blittable NvAPI calls, the Super-I/O sweep and the projections
    /// firing every 1.5 s, window closed, all day.</summary>
    public static void TouchTemps()
    {
        Interlocked.Exchange(ref _lastTempTicks, DateTime.UtcNow.Ticks);
        EnsureRunning();
    }

    /// <summary>Flag-and-arm only, never blocking: the sources are opened by the
    /// first tick on the timer thread (OpenSourcesOnce), so a Touch from a render
    /// tick, an effect frame or the MainViewModel constructor never runs LHM's
    /// driver load, the NvAPI enumeration or ReconcileFans on the caller's (UI)
    /// thread. Readers see null/empty data until that tick has published, which
    /// every caller already renders as "--"/an empty list.</summary>
    static void EnsureRunning()
    {
        if (_running || _shutdown) return;
        lock (_gate)
        {
            if (_running || _shutdown) return;
            _running = true;
            _timer ??= new Timer(_ => Tick(), null, 0, (int)(RefreshSeconds * 1000));
        }
    }

    /// <summary>One-time source open, run at the top of TickCore: on the pool
    /// thread, and under the _ticking guard, so the LHM Computer it closes on a
    /// re-open (after ResetSources) can never be mid-Refresh in another tick.
    /// Under _gate so ResetSources/Shutdown wait for a half-open set of sources
    /// instead of capturing nulls the open then overwrites (a leaked PawnIO
    /// driver handle). A no-op once opened, or after Shutdown.</summary>
    static void OpenSourcesOnce()
    {
        lock (_gate)
        {
            if (_sourcesOpened || _shutdown) return;
            _sourcesOpened = true;
            _lastApplied.Clear();   // fresh backends know nothing: ReconcileFans must write, not dedup
            try { _cpu = RyzenCpuTemperature.TryCreate(); } catch { }
            try { _lhm?.Dispose(); } catch { }   // safe on re-open after ResetSources
            _lhm = null;
            try { _lhm = LhmFans.TryOpen(); } catch { }
            // LHM found nothing (driver blocked, or an unsupported board):
            // fall back to the ITE Super-I/O over PawnIO for monitoring.
            if (_lhm == null)
                try { OpenIteFallback(); } catch (Exception ex) { Log.Warn("sensors", $"ITE fallback failed: {ex.Message}"); }
            try { _gpu = NvApi.EnumGpus().FirstOrDefault().Handle; } catch { }
            try { _gpuFanCtl = _gpu != IntPtr.Zero && NvApi.CanControlGpuFans(_gpu); } catch { }
            try { if (_gpuFanCtl) _gpuMinDuty = NvApi.GetGpuFanMinLevel(_gpu) ?? 30; } catch { }
            string board = _lhm != null ? $"{_lhm.Fans.Count} fans"
                : _iteChips != null ? $"{_iteChips.Sum(c => c.FanSlots.Length)} fans (ITE/PawnIO)"
                : "n/a";
            Log.Info("sensors",
                $"hub started (cpu={(_cpu != null ? "ok" : "n/a")}, board={board}, gpu={(_gpu != IntPtr.Zero ? (_gpuFanCtl ? "ok+fanctl" : "ok") : "n/a")})");
            ReconcileFans();
        }
    }

    static int _ticking;

    /// <summary>System.Threading.Timer fires on the pool regardless of whether
    /// the previous callback finished, so a stalled NvAPI/LHM read (sleep/resume,
    /// driver reset) used to overlap two sweeps: concurrent LHM Update(), a
    /// double-counted hot tick, torn CPU-load deltas. Skip the tick instead —
    /// and never let an exception out of a Timer callback (process-fatal).</summary>
    static void Tick()
    {
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;
        try { TickCore(); }
        catch (Exception ex) { Log.Occasional("sensors-tick", "sensors", () => $"tick failed: {ex.Message}"); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    static void TickCore()
    {
        OpenSourcesOnce();
        _tickNo++;
        bool anyManual;
        lock (_gate) anyManual = _manualFans.Count > 0 || _fanCurves.Count > 0;

        // Never idle-stop while a fan is under manual control: the refresh
        // loop IS the failsafe watchdog. Idle = neither kind of reader recently.
        var now = DateTime.UtcNow;
        long readT = Interlocked.Read(ref _lastReadTicks), tempT = Interlocked.Read(ref _lastTempTicks);
        double sinceUi = (now - new DateTime(readT, DateTimeKind.Utc)).TotalSeconds;
        double sinceAny = (now - new DateTime(Math.Max(readT, tempT), DateTimeKind.Utc)).TotalSeconds;
        if (!anyManual && sinceAny > IdleStopSeconds)
        {
            lock (_gate)
            {
                // A null _timer means ResetSources/Shutdown already took it and
                // is draining this tick: leave _running to them, or a Touch could
                // start a second timer (and re-open the sources) mid-drain.
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                    _running = false;
                }
            }
            return;
        }
        // Split the sweep: with the window closed but a fan curve active, the
        // timer must keep running (it IS the control loop + failsafe), but only
        // the CONTROL-ESSENTIAL reads are needed — CPU/GPU temp, plus the board
        // sweep when a curve sources "Hottest". The GPU RPM/load/voltage calls
        // (non-blittable NvAPI deep-marshals) and the per-tick BoardTemps/
        // BoardFans projections are UI-only: gated on a recent FULL Touch(),
        // not on the temp-only TouchTemps() the effects and LCD use.
        bool uiActive = sinceUi <= IdleStopSeconds;
        bool needBoard = uiActive;
        if (!needBoard)
            lock (_gate) needBoard = _fanCurves.Values.Any(c => c.Source == TempSource.Hottest);

        // Snapshot the sources once: ResetSources/Shutdown null the fields and
        // then wait for this tick before disposing what they held.
        var cpu = _cpu; var lhm = _lhm; var ite = _iteChips;
        try { CpuTempC = cpu?.ReadCelsius(); } catch { CpuTempC = null; }
        try { GpuTempC = _gpu != IntPtr.Zero ? NvApi.GetGpuTemperature(_gpu) : null; } catch { GpuTempC = null; }
        if (uiActive)
        {
            try { GpuFanRpms = _gpu != IntPtr.Zero ? NvApi.GetGpuFanRpms(_gpu) : null; } catch { GpuFanRpms = null; }
            try { GpuLoadPct = _gpu != IntPtr.Zero ? NvApi.GetGpuLoad(_gpu) : null; } catch { GpuLoadPct = null; }
            try { GpuVoltage = _gpu != IntPtr.Zero ? NvApi.GetGpuCoreVoltage(_gpu) : null; } catch { GpuVoltage = null; }
            try { CpuLoadPct = ReadCpuLoad(); } catch { CpuLoadPct = null; }
        }
        if (lhm != null && needBoard)
        {
            try
            {
                lhm.Refresh();
                BoardTemps = lhm.Temps.Select(t => new BoardTemp(t.Name, t.Value)).ToArray();
                LogBoardInventory();
                if (uiActive)
                {
                    BoardFans = lhm.Fans.Select(f => new BoardFan(f.Name, f.CurrentRpm, f.CanControl)).ToArray();
                    CpuVoltage = PickVcore(lhm.Voltages);
                }
            }
            catch { }
        }
        else if (ite != null && needBoard)
        {
            try { ReadIteBoard(ite); } catch { }
        }

        // Remember when each header last actually spun. A motherboard exposes
        // every header whether or not a fan is plugged in; empty ones read 0 RPM
        // (a stray boot blip aside) and only clutter the list once an "apply to
        // all" puts a curve on them. Timestamping real spin lets the UI hide the
        // phantoms while a briefly-stopped real fan rides the debounce.
        if (uiActive)
            lock (_gate)
                for (int i = 0; i < BoardFans.Length; i++)
                    if (BoardFans[i].Rpm is int r && r > 0) _lastSpun[i] = Environment.TickCount64;

        // Drive curve-controlled fans: sample each fan's temp source and apply
        // the interpolated duty (floored). Re-evaluated every tick so the fans
        // follow temperature; ApplyDuty skips the write when the value hasn't
        // changed. Manual fans go through the same call: free while unchanged,
        // and the periodic re-assert inside ApplyDuty is what puts a fan back
        // after the GPU driver or the board quietly took it (resume, TDR) —
        // manual duties used to be written once and never again.
        if (anyManual)
        {
            List<KeyValuePair<int, FanCurve>> curves;
            List<KeyValuePair<int, int>> manual;
            HashSet<int>? busy = null;
            lock (_gate)
            {
                curves = _fanCurves.ToList();
                manual = _manualFans.ToList();
                if (_identifying.Count > 0) busy = new(_identifying);
            }
            foreach (var kv in curves)
            {
                if (busy?.Contains(kv.Key) == true) continue;   // mid-Identify burst: leave it at 100%
                var t = TempFor(kv.Value.Source);
                if (t is double temp)
                    ApplyDuty(kv.Key, Math.Max(FloorFor(kv.Key), kv.Value.DutyAt(temp)));
            }
            foreach (var kv in manual)
                if (busy?.Contains(kv.Key) != true) ApplyDuty(kv.Key, kv.Value);
        }

        // Failsafe: any control + a hot CPU or GPU (three consecutive ticks,
        // ~4.5 s, so a junk reading or a transient spike can't trip it) = hand
        // everything back to auto for this session. Zen 4/5 parts deliberately
        // run all-core loads at a 95°C Tctl target (Tjmax 89 on the 7000-X3D
        // parts), so the CPU line sits ABOVE that: 96 means the CPU's own
        // limiter is losing. The saved curves are KEPT — the failsafe protects
        // the hardware, it shouldn't erase the user's configuration.
        bool tooHot = (CpuTempC is double c && c >= FailsafeCpuC)
                   || (GpuTempC is int g && g >= FailsafeGpuC);
        if (anyManual && tooHot)
        {
            if (++_hotTicks >= FailsafeTicks)
            {
                FailsafeTripped = true;
                Log.Warn("fans", $"FAILSAFE: CPU {CpuTempC:0.0}°C / GPU {GpuTempC}°C with fan control active — restoring all fans to auto (saved curves kept)");
                RestoreAllFans("thermal failsafe", keepConfig: true);
            }
        }
        else _hotTicks = 0;
    }

    /*--- CPU load via kernel32 GetSystemTimes deltas (no perf-counter dep) ---*/
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    static long _lastIdle, _lastKernel, _lastUser;

    static double? ReadCpuLoad()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return null;
        double? result = null;
        if (_lastKernel != 0 || _lastUser != 0)
        {
            // kernel time includes idle; busy = (kernel+user-idle) over total.
            double total = (kernel - _lastKernel) + (user - _lastUser);
            double busy = total - (idle - _lastIdle);
            if (total > 0) result = Math.Clamp(busy / total * 100.0, 0, 100);
        }
        _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
        return result;
    }

    /// <summary>Best guess at Vcore among the board's voltage rails: a
    /// name containing "vcore"/"cpu", else the first rail in Vcore range.</summary>
    static double? PickVcore(IReadOnlyList<LhmFans.Temp> rails)
    {
        double? byName = null, byRange = null;
        foreach (var r in rails)
        {
            if (r.Value is not double v) continue;
            string n = r.Name.ToLowerInvariant();
            if (byName == null && (n.Contains("vcore") || n.Contains("cpu"))) byName = v;
            if (byRange == null && v is > 0.4 and < 1.6) byRange = v;
        }
        return byName ?? byRange;
    }

    /// <summary>Read-only board fallback: open the ITE Super-I/O(s) over PawnIO
    /// and keep the fan/temp slots that answer at open (a register that fails
    /// to read is dropped). A fan header with no pulses reads 0 RPM, not null,
    /// so a fan sitting in BIOS fan-stop at open is kept in the slot list (ITE
    /// fans are read-only and feed the LCD RPM element / diagnostics; the
    /// Cooling pane lists only controllable LHM fans). Needs PawnIO installed
    /// + elevation; a no-op otherwise.</summary>
    static void OpenIteFallback()
    {
        var chips = IteSuperIo.OpenAll();
        if (chips.Count == 0) return;
        var kept = new List<IteBoardChip>();
        foreach (var chip in chips)
        {
            IteSuperIo.Reading r;
            try { r = chip.Read(); } catch { chip.Dispose(); continue; }
            var fanSlots = new List<int>();
            for (int i = 0; i < r.FanRpm.Length; i++) if (r.FanRpm[i] != null) fanSlots.Add(i);
            var tempSlots = new List<int>();
            for (int i = 0; i < r.TempsC.Length; i++) if (r.TempsC[i] != null) tempSlots.Add(i);
            if (fanSlots.Count > 0 || tempSlots.Count > 0)
                kept.Add(new IteBoardChip(chip, fanSlots.ToArray(), tempSlots.ToArray()));
            else chip.Dispose();
        }
        if (kept.Count > 0) _iteChips = kept;
    }

    /// <summary>One monitoring sweep of the ITE fallback chips into BoardTemps/
    /// BoardFans. Read-only here (CanControl = false) - direct EC fan control is
    /// a later phase, so the existing LHM-scoped control paths never target these.</summary>
    static void ReadIteBoard(List<IteBoardChip> chips)
    {
        var temps = new List<BoardTemp>();
        var fans = new List<BoardFan>();
        int fanNo = 1, tempNo = 1;
        foreach (var c in chips)
        {
            IteSuperIo.Reading r;
            try { r = c.Chip.Read(includePwm: false); } catch { continue; }   // duty regs unused here: 12 fewer ioctls/sweep
            foreach (int s in c.TempSlots)
                if (s < r.TempsC.Length) temps.Add(new BoardTemp($"MB Temp {tempNo++}", r.TempsC[s]));
            foreach (int s in c.FanSlots)
                if (s < r.FanRpm.Length) fans.Add(new BoardFan($"Fan {fanNo++}", r.FanRpm[s], CanControl: false));
        }
        BoardTemps = temps.ToArray();
        BoardFans = fans.ToArray();
    }

    /// <summary>Drop the PawnIO sources and re-open everything on the next
    /// reader's first tick. Called after PawnIO is installed so CPU temp and the
    /// ITE board fallback light up without an app restart (both need PawnIO,
    /// which was absent at first open).</summary>
    public static void ResetSources()
    {
        Timer? timer; RyzenCpuTemperature? cpu; List<IteBoardChip>? ite;
        lock (_gate)
        {
            timer = _timer; _timer = null;
            cpu = _cpu; _cpu = null;
            ite = _iteChips; _iteChips = null;
            // Latch: _running stays TRUE until the drain and dispose below are
            // done. With _timer null no new tick can fire, and every Touch/
            // TouchTemps (the effects call it each frame, the LCD each render
            // tick) returns early instead of starting a second timer whose
            // first tick would close the LHM Computer while the drained tick
            // may still be inside Refresh() on it.
            _running = true;
        }
        // Dispose, not just drop: _cpu owns a PawnIO KERNEL DRIVER handle
        // (no finalizer) — nulling it leaked the handle for the process
        // lifetime, and the next Touch() opened a second one. But only after
        // the tick that may still be reading it has finished (see Drain).
        Drain(timer);
        DisposeSources(cpu, ite);
        lock (_gate)
        {
            // _lhm/_gpu are unaffected by PawnIO; leave them, but a full re-open
            // is simplest and safe — clear the latch so the next Touch() rebuilds
            // all (its first tick closes and re-opens LHM with no tick in flight).
            _sourcesOpened = false;
            _running = false;
        }
    }

    /// <summary>App exit: stop the timer and close every source. Only LHM's
    /// Computer.Close() unloads its ring0 driver service — process teardown
    /// leaves it registered — and it was never called on a clean exit. Touch()
    /// is a no-op afterwards so a late render tick can't re-open anything.</summary>
    public static void Shutdown()
    {
        Timer? timer; LhmFans? lhm; RyzenCpuTemperature? cpu; List<IteBoardChip>? ite;
        lock (_gate)
        {
            _shutdown = true;
            timer = _timer; _timer = null; _running = false;
            lhm = _lhm; _lhm = null;
            cpu = _cpu; _cpu = null;
            ite = _iteChips; _iteChips = null;
        }
        Drain(timer);
        try { lhm?.Dispose(); } catch { }   // RestoreAll + Close
        DisposeSources(cpu, ite);
    }

    /// <summary>Stop the timer and wait (bounded) for an in-flight tick. TickCore
    /// reads the source fields lock-free and PawnIO.Execute has no disposed
    /// check, so disposing a source under a running tick raced a live ioctl
    /// against a closed — and, with the re-open right behind it, possibly
    /// recycled — driver handle. Called OUTSIDE _gate: the tick takes it.</summary>
    static void Drain(Timer? timer)
    {
        if (timer == null) return;
        // DisposeAsync completes once the in-flight callback has returned and
        // owns no handle of ours. The Dispose(WaitHandle) form signalled an
        // event this method had already closed when the 2 s wait timed out
        // (a stalled NvAPI/LHM read), and the timer thread's Set on the closed
        // handle threw on the pool — process-fatal.
        try { timer.DisposeAsync().AsTask().Wait(2000); }
        catch (AggregateException) { }
    }

    static void DisposeSources(RyzenCpuTemperature? cpu, List<IteBoardChip>? ite)
    {
        try { cpu?.Dispose(); } catch { }
        try { if (ite != null) foreach (var c in ite) c.Chip.Dispose(); } catch { }
    }

    static double? TempFor(TempSource s) => s switch
    {
        TempSource.Cpu => CpuTempC,
        TempSource.Gpu => GpuTempC is int g ? g : null,
        _ => HottestC,
    };

    /// <summary>The temperature a curve on this source would currently follow
    /// (for the editor's live marker).</summary>
    public static double? CurrentTemp(TempSource s) => TempFor(s);

    /*-----------------------------------------------------*\
    | Fan control (phase 2/3): per-fan mode — manual fixed   |
    | duty OR a temperature curve — driven through LHM, with |
    | a CPU-temp failsafe. Modes persist per fan (by name)   |
    | and are re-applied on launch; any fan NOT configured   |
    | is set back to the BIOS curve, which also cleans up an |
    | unclean exit. Fans are addressed by flat index into    |
    | BoardFans.                                             |
    \*-----------------------------------------------------*/
    public const int MinDutyPct = 30;          // pump-safe floor, no soft-off
    const double FailsafeCpuC = 96;
    const double FailsafeGpuC = 90;
    const int FailsafeTicks = 3;

    /// <summary>Virtual fan index for the GPU's coolers (driven together —
    /// they're one assembly). Routes to NvAPI instead of LHM.</summary>
    public const int GpuFanIndex = 9999;

    /*--- Lian Li wireless fans: sentinel indices routed over RF. Duty writes
          are asserted by the device until the receiver confirms; curves tick
          at the hub rate but quantize to 5% steps so temperature jitter
          doesn't churn the radio. ---*/
    public const int LianFanBase = 20000;
    static bool IsLian(int i) => i >= LianFanBase;
    static Devices.LianLiWireless? Lian => Devices.LianLiWireless.Instance;
    public static int LianFanCount => Lian?.FanCount ?? 0;

    /// <summary>Curve floor: GPU curves may go to 0 (below the card's manual
    /// minimum the driver takes over — see ApplyDuty); board headers keep the
    /// pump-safe 30%.</summary>
    public static int FloorFor(int fanIndex)
        => fanIndex == GpuFanIndex ? 0 : IsLian(fanIndex) ? 20 : MinDutyPct;

    /// <summary>Manual-slider floor: the GPU can't be manually driven below
    /// the level its vBIOS reports (the driver silently clamps), so Manual
    /// mode honors that; curves use FloorFor and the auto-handoff instead.</summary>
    public static int ManualFloorFor(int fanIndex)
        => fanIndex == GpuFanIndex ? Math.Max(1, GpuFanManualMin)
         : IsLian(fanIndex) ? 20 : MinDutyPct;

    public static int GpuFanManualMin { get { lock (_gate) return _gpuMinDuty; } }

    /// <summary>True when the GPU exposes fan control (Turing and newer).</summary>
    public static bool GpuFansControllable { get { lock (_gate) return _gpuFanCtl; } }

    /*--- Write dedup. The tick loop re-evaluates every controlled fan each
          1.5 s and only the LHM backend dedups on its own: the GPU path is
          two deep-marshaled NvAPI calls (+ ~35 allocations) per write and the
          Lian path spawned a worker thread and wrote a log line per write —
          steady state, window closed, forever. Board/GPU duties are still
          re-asserted every ReassertTicks so a driver reset or sleep/resume
          can't leave a fan in auto while the UI says Manual; the Lian
          receiver latches by design, so it is re-sent only when the value or
          the device instance (rescan) changes. ---*/
    const int ReassertTicks = 20;   // ~30 s
    static long _tickNo;
    sealed record Applied(int Duty, long Tick, object? Device);
    static readonly Dictionary<int, Applied> _lastApplied = new();

    /// <summary>Route a duty write to the right backend, skipping it when the
    /// backend already holds that value (see the dedup note above). Lian duties
    /// quantize to 5% steps first so temperature jitter doesn't churn the radio.</summary>
    static bool ApplyDuty(int fanIndex, int percent)
    {
        bool lian = IsLian(fanIndex);
        if (lian) percent = (percent + 2) / 5 * 5;
        object? dev = lian ? Lian : null;
        lock (_gate)
            if (_lastApplied.TryGetValue(fanIndex, out var la) && la.Duty == percent && la.Device == dev
                && (lian || _tickNo - la.Tick < ReassertTicks))
                return true;
        bool ok = ApplyDutyCore(fanIndex, percent);
        if (ok) lock (_gate) _lastApplied[fanIndex] = new(percent, _tickNo, dev);
        return ok;
    }

    /// <summary>The actual backend write. GPU special case: the card clamps
    /// manual levels to its vBIOS minimum (30 on the 5090 — writing 0 silently
    /// becomes 30), so anything below that minimum hands the coolers back to
    /// the DRIVER instead: its auto mode is the only path to idle/zero-RPM
    /// behavior, where the card allows it.</summary>
    static bool ApplyDutyCore(int fanIndex, int percent)
    {
        if (IsLian(fanIndex))
        {
            var lian = Lian;
            if (lian == null) return false;
            lian.SetFanDuty(fanIndex - LianFanBase, percent);
            return true;
        }
        if (fanIndex == GpuFanIndex)
        {
            IntPtr gpu; int minDuty; bool engaged;
            lock (_gate) { gpu = _gpu; minDuty = _gpuMinDuty; engaged = _gpuManualEngaged; }
            if (gpu == IntPtr.Zero) return false;
            if (percent < minDuty)
            {
                if (!engaged) return true;         // already the driver's
                bool ok = NvApi.RestoreGpuFanAuto(gpu);
                if (ok) lock (_gate) _gpuManualEngaged = false;
                return ok;
            }
            bool set = NvApi.SetGpuFanDuty(gpu, percent);
            if (set) lock (_gate) _gpuManualEngaged = true;
            return set;
        }
        LhmFans? lhm; lock (_gate) lhm = _lhm;
        return lhm != null && lhm.SetDuty(fanIndex, percent);
    }

    /// <summary>Route an auto-restore to the right backend (no mode bookkeeping;
    /// the dedup entry is dropped so the next duty write goes through).</summary>
    static void RestoreOne(int fanIndex)
    {
        lock (_gate) _lastApplied.Remove(fanIndex);
        try
        {
            if (IsLian(fanIndex))
                Lian?.SetFanDuty(fanIndex - LianFanBase, 40);   // no BIOS to hand back to - 40% baseline
            else if (fanIndex == GpuFanIndex)
            {
                IntPtr gpu; lock (_gate) gpu = _gpu;
                if (gpu != IntPtr.Zero) NvApi.RestoreGpuFanAuto(gpu);
                lock (_gate) _gpuManualEngaged = false;
            }
            else Lhm?.Restore(fanIndex);
        }
        catch { }
    }

    static readonly Dictionary<int, int> _manualFans = new();       // fanIndex -> percent
    static readonly Dictionary<int, FanCurve> _fanCurves = new();   // fanIndex -> curve
    static readonly Dictionary<int, long> _lastSpun = new();        // board fan INDEX -> last tick RPM > 0
    static int _hotTicks;
    const long SpinKeepMs = 10_000;   // keep a fan visible this long after it last spun

    /// <summary>Has this board fan (by BoardFans index) spun within the last few
    /// seconds? Keyed by index, NOT name - a board can expose duplicate names
    /// ("Fan #1" on two Super-I/O chips), so a name key would let a real fan's
    /// spin keep its empty namesake visible. An empty header never spins; a real
    /// fan that briefly fan-stops rides the debounce instead of flickering out.</summary>
    public static bool SpunRecently(int index)
    {
        lock (_gate)
            return _lastSpun.TryGetValue(index, out var t) && Environment.TickCount64 - t < SpinKeepMs;
    }

    /// <summary>Set when the thermal failsafe forced everything back to auto;
    /// cleared by the next successful set.</summary>
    public static bool FailsafeTripped { get; private set; }

    public static bool AnyControlledFan
    {
        get { lock (_gate) return _manualFans.Count > 0 || _fanCurves.Count > 0; }
    }

    /// <summary>Current manual duty for a fan, or null if it isn't in fixed
    /// manual mode (may still be on a curve).</summary>
    public static int? ManualFanDuty(int fanIndex)
    {
        lock (_gate) return _manualFans.TryGetValue(fanIndex, out var p) ? p : null;
    }

    /// <summary>The curve a fan follows, or null if it isn't in curve mode.</summary>
    public static FanCurve? FanCurveOf(int fanIndex)
    {
        lock (_gate) return _fanCurves.TryGetValue(fanIndex, out var c) ? c : null;
    }

    static LhmFans? Lhm { get { lock (_gate) return _lhm; } }

    static bool Controllable(int fanIndex)
    {
        if (IsLian(fanIndex)) return fanIndex - LianFanBase < LianFanCount;
        if (fanIndex == GpuFanIndex) return GpuFansControllable;
        var lhm = Lhm;
        return lhm != null && fanIndex < lhm.Fans.Count && lhm.Fans[fanIndex].CanControl;
    }

    /// <summary>Manual fixed duty (percent, floored per fan). Replaces
    /// any curve on that fan. Returns false when it isn't controllable.</summary>
    public static bool SetFanDuty(int fanIndex, int percent)
    {
        Touch();
        if (!Controllable(fanIndex)) return false;
        percent = Math.Clamp(percent, ManualFloorFor(fanIndex), 100);
        if (!ApplyDuty(fanIndex, percent)) return false;
        lock (_gate) { _manualFans[fanIndex] = percent; _fanCurves.Remove(fanIndex); FailsafeTripped = false; }
        SaveFanConfig();
        return true;
    }

    /// <summary>Put a fan on a temperature curve. Replaces any fixed duty.
    /// The tick loop applies it continuously.</summary>
    public static bool SetFanCurve(int fanIndex, FanCurve curve)
    {
        Touch();
        if (!Controllable(fanIndex)) return false;
        lock (_gate) { _fanCurves[fanIndex] = curve.Clone(); _manualFans.Remove(fanIndex); FailsafeTripped = false; }
        // Apply immediately so the fan responds without waiting for the tick.
        var t = TempFor(curve.Source);
        if (t is double temp) ApplyDuty(fanIndex, Math.Max(FloorFor(fanIndex), curve.DutyAt(temp)));
        SaveFanConfig();
        return true;
    }

    /// <summary>Hand one fan back to its own automatic control (BIOS curve for
    /// board fans, the driver curve incl. fan-stop for the GPU).</summary>
    public static void RestoreFan(int fanIndex)
    {
        lock (_gate) { _manualFans.Remove(fanIndex); _fanCurves.Remove(fanIndex); }
        RestoreOne(fanIndex);
        SaveFanConfig();
    }

    /// <summary>Hand every fan back to automatic (exit, crash, failsafe).
    /// keepConfig leaves the saved profiles in place (used on app exit so the
    /// next launch restores them).</summary>
    public static void RestoreAllFans(string reason, bool keepConfig = false)
    {
        LhmFans? lhm;
        IntPtr gpu;
        bool gpuCtl;
        lock (_gate) { lhm = _lhm; gpu = _gpu; gpuCtl = _gpuFanCtl; _manualFans.Clear(); _fanCurves.Clear(); _lastApplied.Clear(); }
        try { lhm?.RestoreAll(); } catch { }
        try { if (gpuCtl && gpu != IntPtr.Zero) { NvApi.RestoreGpuFanAuto(gpu); lock (_gate) _gpuManualEngaged = false; } } catch { }
        // Wireless fans: failsafe means FULL BLAST (there is no BIOS curve to
        // fall back to); a plain restore-all returns them to the 40% baseline.
        // App exit (keepConfig) leaves their latched duty untouched.
        bool failsafe = reason.Contains("failsafe");
        try
        {
            if ((!keepConfig || failsafe) && Lian is { } lw)
            {
                int duty = failsafe ? 100 : 40;
                for (int s = 0; s < lw.FanCount; s++) lw.SetFanDuty(s, duty);
            }
        }
        catch { }
        if (!keepConfig) { try { File.Delete(FanConfigFile); } catch { } }
        Log.Info("fans", $"all fans restored to auto ({reason})");
    }

    static readonly HashSet<int> _identifying = new();

    /// <summary>"Which physical fan is this?" — blast the header to 100% for
    /// four seconds, then put it back to whatever mode it was in. Full-blast,
    /// never a stop: a stop test on the pump header would be risky; a burst is
    /// unmistakable and safe.</summary>
    public static async Task IdentifyFan(int fanIndex)
    {
        lock (_gate)
        {
            if (!_identifying.Add(fanIndex)) return;   // already running
        }
        try
        {
            Touch();
            if (!Controllable(fanIndex)) return;
            if (!ApplyDuty(fanIndex, 100)) return;
            string idName = fanIndex == GpuFanIndex ? "GPU"
                : IsLian(fanIndex) ? $"Lian Li slot {fanIndex - LianFanBase + 1}"
                : Lhm?.Fans[fanIndex].Name ?? $"#{fanIndex}";
            Log.Info("fans", $"identify: fan '{idName}' at 100% for 4s");
            await Task.Delay(4000);
            // Put back whatever mode the fan is in NOW, not the one captured
            // before the burst: a slider or mode change made during the four
            // seconds used to be overwritten by the stale capture, and nothing
            // re-asserted the newer manual duty afterwards.
            int? manual;
            FanCurve? curve;
            lock (_gate)
            {
                manual = _manualFans.TryGetValue(fanIndex, out var p) ? p : null;
                curve = _fanCurves.TryGetValue(fanIndex, out var cc) ? cc : null;
            }
            if (manual is int m) ApplyDuty(fanIndex, m);
            else if (curve is FanCurve c && TempFor(c.Source) is double t) ApplyDuty(fanIndex, Math.Max(FloorFor(fanIndex), c.DutyAt(t)));
            else RestoreOne(fanIndex);
        }
        catch (Exception ex) { Log.Warn("fans", $"identify failed: {ex.Message}"); }
        finally
        {
            lock (_gate) _identifying.Remove(fanIndex);
        }
    }

    /*------------------- durable per-fan config -------------------*\
    | Persisted by fan NAME (stable across launches). On start we    |
    | reconcile every fan: configured -> apply its mode; not         |
    | configured -> SetDefault, which also clears a crash-stuck duty.|
    \*--------------------------------------------------------------*/
    static string FanConfigFile => AppPaths.Local("fan-config.json");

    sealed record FanConfigEntry(string Name, string Kind, int Pct, FanCurve? Curve);

    static void SaveFanConfig()
    {
        try
        {
            var entries = new List<FanConfigEntry>();
            lock (_gate)
            {
                var fans = _lhm?.Fans;
                string? NameOf(int i) => i == GpuFanIndex ? "GPU"
                    : IsLian(i) ? (Lian is { } lw ? $"LianLi:{lw.ChainOf(i - LianFanBase)}" : null)
                    : fans != null && i < fans.Count ? fans[i].Name : null;
                foreach (var kv in _manualFans)
                    if (NameOf(kv.Key) is string n) entries.Add(new(n, "manual", kv.Value, null));
                foreach (var kv in _fanCurves)
                    if (NameOf(kv.Key) is string n) entries.Add(new(n, "curve", 0, kv.Value));
            }
            if (entries.Count == 0) { try { File.Delete(FanConfigFile); } catch { } return; }
            SafeFile.WriteAllText(FanConfigFile, System.Text.Json.JsonSerializer.Serialize(new { fans = entries }));
        }
        // Never silent: a save that fails here is a fan mode that will not
        // survive the next launch (this hid a missing %LOCALAPPDATA%\UnifiedRgb
        // on every clean install without the OpenRGB bridge).
        catch (Exception ex) { Log.Warn("fans", $"fan-config save failed: {ex.Message}"); }
    }

    /// <summary>On hub start: apply saved modes and hand every other fan back
    /// to automatic (which also undoes an unclean exit's stuck duty).</summary>
    static void ReconcileFans()
    {
        Dictionary<string, FanConfigEntry> cfg = new();
        try
        {
            if (File.Exists(FanConfigFile))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FanConfigFile));
                if (doc.RootElement.TryGetProperty("fans", out var arr))
                    foreach (var e in arr.EnumerateArray())
                    {
                        var entry = System.Text.Json.JsonSerializer.Deserialize<FanConfigEntry>(e.GetRawText());
                        if (entry != null) cfg[entry.Name] = entry;
                    }
            }
        }
        catch (Exception ex) { Log.Warn("fans", $"fan-config load failed: {ex.Message}"); }

        void Apply(int i, string name, FanConfigEntry e)
        {
            if (e.Kind == "curve" && e.Curve != null)
            {
                // Clone (as SetFanCurve does): the deserialized instance is
                // handed to the UI via FanCurveOf, and the editor mutates its
                // Points list in place while the tick thread reads it. Clone
                // also re-sorts hand-edited points.
                var curve = e.Curve.Clone();
                lock (_gate) _fanCurves[i] = curve;
                var t = TempFor(curve.Source);
                if (t is double temp) ApplyDuty(i, Math.Max(FloorFor(i), curve.DutyAt(temp)));
                Log.Info("fans", $"restored '{name}' -> curve {curve.Preset}");
            }
            else if (e.Kind == "manual")
            {
                // Manual floor, as the slider enforces: below the card's manual
                // minimum the GPU stays in driver auto while the row would say
                // "Manual · N%".
                int pct = Math.Clamp(e.Pct, ManualFloorFor(i), 100);
                lock (_gate) _manualFans[i] = pct;
                ApplyDuty(i, pct);
                Log.Info("fans", $"restored '{name}' -> manual {pct}%");
            }
            else Log.Warn("fans", $"ignoring saved mode for '{name}': kind={e.Kind}, curve={(e.Curve != null)}");
        }

        var lhm = _lhm;
        if (lhm != null)
            for (int i = 0; i < lhm.Fans.Count; i++)
            {
                if (!lhm.Fans[i].CanControl) continue;
                if (cfg.TryGetValue(lhm.Fans[i].Name, out var e)) Apply(i, lhm.Fans[i].Name, e);
                else { try { lhm.Restore(i); } catch { } }   // clean crash-stuck duty
            }

        if (_gpuFanCtl)
        {
            if (cfg.TryGetValue("GPU", out var e)) Apply(GpuFanIndex, "GPU", e);
            else RestoreOne(GpuFanIndex);   // clean crash-stuck duty
        }

        // Lian Li wireless fans: keys are chain-stable so re-arranging slots
        // keeps each physical fan's saved mode. Unconfigured fans just keep
        // their latched duty (nothing to clean - RF duty persists by design).
        if (Lian is { } lwr)
            for (int s = 0; s < lwr.FanCount; s++)
            {
                string key = $"LianLi:{lwr.ChainOf(s)}";
                if (cfg.TryGetValue(key, out var e)) Apply(LianFanBase + s, key, e);
            }
    }
}

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
    /// driver handle). _writeGate is taken FIRST (the one lock order, see its
    /// declaration): ReconcileFans below changes fan modes and writes duties
    /// while _gate is held, and both of those take _writeGate themselves.
    /// A no-op once opened, or after Shutdown.</summary>
    /// <summary>Serialises the open with ResetSources and Shutdown, so neither
    /// can capture a half-open set of sources (a leaked PawnIO driver handle).
    /// Outermost in the lock order: _openGate, then _writeGate, then _gate.</summary>
    static readonly object _openGate = new();

    static void OpenSourcesOnce()
    {
        // Fast path first, and NOT under _openGate: a tick arriving while
        // Shutdown holds that lock and drains the timer would otherwise block
        // on it, and the drain would then wait 2 s for that very tick.
        lock (_gate) { if (_sourcesOpened || _shutdown) return; }
        lock (_openGate)
        {
            lock (_gate) { if (_sourcesOpened || _shutdown) return; }

            // The backends open into locals with no lock held but _openGate.
            // LhmFans.TryOpen is the ring0 driver load plus a full Super-I/O
            // probe - seconds on a first run - and every UI getter takes _gate:
            // held across the open, it parked the Cooling pane's second refresh
            // (1.5 s after the first) on the dispatcher for the whole thing.
            LhmFans? old;
            lock (_gate) { old = _lhm; _lhm = null; }
            // Under _writeGate: a RestoreFan/SetFanDuty on the UI thread must not
            // interleave a write with the old instance's close (a re-open after
            // ResetSources; a no-op the first time).
            lock (_writeGate) { try { old?.Dispose(); } catch { } }

            RyzenCpuTemperature? cpu = null; LhmFans? lhm = null; List<IteBoardChip>? ite = null;
            IntPtr gpu = IntPtr.Zero; bool gpuFanCtl = false; int? gpuMinDuty = null;
            try { cpu = RyzenCpuTemperature.TryCreate(); } catch { }
            try { lhm = LhmFans.TryOpen(); } catch { }
            // LHM found nothing (driver blocked, or an unsupported board):
            // fall back to the ITE Super-I/O over PawnIO for monitoring.
            if (lhm == null)
                try { ite = OpenIteFallback(); } catch (Exception ex) { Log.Warn("sensors", $"ITE fallback failed: {ex.Message}"); }
            try { gpu = NvApi.EnumGpus().FirstOrDefault().Handle; } catch { }
            try { gpuFanCtl = gpu != IntPtr.Zero && NvApi.CanControlGpuFans(gpu); } catch { }
            try { if (gpuFanCtl) gpuMinDuty = NvApi.GetGpuFanMinLevel(gpu) ?? 30; } catch { }

            lock (_writeGate)
            lock (_gate)
            {
                if (_shutdown)
                {
                    // Went down while the backends were opening: publish nothing.
                    try { lhm?.Dispose(); } catch { }
                    DisposeSources(cpu, ite);
                    return;
                }
                _sourcesOpened = true;
                _lastApplied.Clear();   // fresh backends know nothing: ReconcileFans must write, not dedup
                // Board/GPU entries are re-derived below (ReconcileFans restores
                // every unconfigured fan); a wireless one is not - it is a fan the
                // receiver still holds at our duty - so it stays queued.
                _pendingRestore.RemoveWhere(i => !IsLian(i));
                _cpu = cpu; _lhm = lhm;
                if (ite != null) _iteChips = ite;
                _gpu = gpu; _gpuFanCtl = gpuFanCtl;
                if (gpuMinDuty is int min) _gpuMinDuty = min;
                string board = _lhm != null ? $"{_lhm.Fans.Count} fans"
                    : _iteChips != null ? $"{_iteChips.Sum(c => c.FanSlots.Length)} fans (ITE/PawnIO)"
                    : "n/a";
                Log.Info("sensors",
                    $"hub started (cpu={(_cpu != null ? "ok" : "n/a")}, board={board}, gpu={(_gpu != IntPtr.Zero ? (_gpuFanCtl ? "ok+fanctl" : "ok") : "n/a")})");
                ReconcileFans();
            }
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
        bool anyManual, anyPending;
        lock (_gate)
        {
            anyManual = _manualFans.Count > 0 || _fanCurves.Count > 0;
            anyPending = _pendingRestore.Count > 0;
        }

        // Never idle-stop while a fan is under manual control: the refresh
        // loop IS the failsafe watchdog. Nor while a handback is still owed
        // (_pendingRestore): that fan is sitting on our duty with the mode
        // already gone from the dictionaries, and this loop is the only thing
        // that will retry it. Idle = neither kind of reader recently.
        var now = DateTime.UtcNow;
        long readT = Interlocked.Read(ref _lastReadTicks), tempT = Interlocked.Read(ref _lastTempTicks);
        double sinceUi = (now - new DateTime(readT, DateTimeKind.Utc)).TotalSeconds;
        double sinceAny = (now - new DateTime(Math.Max(readT, tempT), DateTimeKind.Utc)).TotalSeconds;
        if (!anyManual && !anyPending && sinceAny > IdleStopSeconds)
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
        // The board sweep (a port-I/O pass under the ISA mutex) is UI-only.
        // A "Hottest" curve used to force it every tick, window closed, for
        // a value HottestC does not even read (it is Max(CPU, GPU)).
        bool needBoard = uiActive;

        // Snapshot the sources once: ResetSources/Shutdown null the fields and
        // then wait for this tick before disposing what they held.
        var cpu = _cpu; var lhm = _lhm; var ite = _iteChips;
        try { CpuTempC = cpu?.ReadCelsius(); } catch { CpuTempC = null; }
        try { GpuTempC = _gpu != IntPtr.Zero ? NvApi.GetGpuTemperature(_gpu) : null; } catch { GpuTempC = null; }
        HasPublished = true;
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
        //
        // Every write below carries the mode generation captured with the
        // snapshot: the user can put a fan back on Auto (RestoreFan) between
        // this snapshot and the write, and an unguarded write then re-took the
        // fan the firmware had just been handed. ApplyDuty refuses a write
        // whose generation is stale; the loops also stop early once they see
        // it move, so the next tick starts from a fresh snapshot.
        if (anyManual || anyPending) RekeyLianIfReplaced();   // a rescan may have re-arranged the wireless slots
        if (anyManual)
        {
            List<KeyValuePair<int, FanCurve>> curves;
            List<KeyValuePair<int, int>> manual;
            HashSet<int>? busy = null;
            long gen;
            lock (_gate)
            {
                curves = _fanCurves.ToList();
                manual = _manualFans.ToList();
                gen = _modeGen;
                if (_identifying.Count > 0) busy = new(_identifying);
            }
            foreach (var kv in curves)
            {
                if (ModeChangedSince(gen)) break;
                if (busy?.Contains(kv.Key) == true) continue;   // mid-Identify burst: leave it at 100%
                var t = TempFor(kv.Value.Source);
                if (t is double temp)
                {
                    bool wasLost;
                    lock (_gate) { _blindTicks[kv.Key] = 0; wasLost = _sourceLost.Remove(kv.Key); }
                    if (wasLost)
                        Log.Info("fans", $"fan {kv.Key}: {kv.Value.Source} temperature is back, the curve is driving it again");
                    ApplyDuty(kv.Key, Math.Max(FloorFor(kv.Key), kv.Value.DutyAt(temp)), gen);
                    continue;
                }

                // No reading. Ride out a short gap; past the grace period the
                // fan goes back to firmware control. The curve is KEPT, so the
                // moment a reading returns the branch above re-arms it.
                int blind; bool alreadyLost;
                lock (_gate)
                {
                    blind = _blindTicks[kv.Key] = _blindTicks.GetValueOrDefault(kv.Key) + 1;
                    alreadyLost = _sourceLost.Contains(kv.Key);
                }
                if (blind < LostSourceGraceTicks) continue;

                // Retried every tick ONLY until the handback succeeds, then
                // latched in _sourceLost: RestoreOne can fail, and a fan
                // recorded as handed back when it is still sitting at our duty
                // is under nobody's control while the log says otherwise. But
                // once it HAS succeeded, calling it again each tick is not a
                // retry - for a wireless fan (no firmware to hand back to) it
                // re-wrote the "keep cooling" duty from a cache the first call
                // had already emptied, and the fan dropped to the 40% floor on
                // the second tick after all.
                if (alreadyLost) continue;
                bool handed;
                lock (_writeGate)
                {
                    // Same rule as the writes: a mode change since the
                    // snapshot means this fan may no longer be ours to hand
                    // back (RestoreFan did it, or it is now Manual).
                    if (ModeChangedSince(gen)) break;
                    handed = RestoreOne(kv.Key, keepCooling: true, retry: true);
                    if (handed) lock (_gate) _sourceLost.Add(kv.Key);
                }
                if (handed)
                    Log.Warn("fans", $"fan {kv.Key}: no {kv.Value.Source} temperature for {LostSourceGraceTicks} ticks "
                                   + "- handed back to firmware control (the curve is kept and re-arms if it returns)");
                else
                    Log.Occasional($"fan-restore:{kv.Key}", "fans",
                        $"fan {kv.Key}: no {kv.Value.Source} temperature and handing it back FAILED - it is still on our last duty");
            }
            foreach (var kv in manual)
            {
                if (ModeChangedSince(gen)) break;
                if (busy?.Contains(kv.Key) != true) ApplyDuty(kv.Key, kv.Value, gen);
            }
        }

        // Handbacks that failed (RestoreAllFans, RestoreFan, a crash-stuck
        // cleanup): the fan is on our duty with no mode left to say so, which
        // is the one state this hub must never leave a fan in. Retry each tick
        // until the backend accepts it. Under _writeGate with a membership
        // re-check, so a SetFanDuty that has just re-claimed the fan (and
        // removed it from the set) can never be undone by this retry.
        if (anyPending)
        {
            List<int> retry;
            lock (_gate) retry = _pendingRestore.ToList();
            foreach (int i in retry)
            {
                bool handed;
                lock (_writeGate)
                {
                    bool still; lock (_gate) still = _pendingRestore.Contains(i);
                    if (!still) continue;
                    handed = RestoreOne(i, retry: true);   // a handback already reported done needs a forced write
                    if (handed) { lock (_gate) _pendingRestore.Remove(i); if (!AnyBoardFanOurs()) MarkBoardControl(false); }
                }
                if (handed) Log.Info("fans", $"fan {i}: handed back to automatic control on retry");
                else Log.Occasional($"pending-restore:{i}", "fans",
                    $"fan {i}: still could not be handed back to automatic control - retrying every tick");
            }
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
    static List<IteBoardChip>? OpenIteFallback()
    {
        var chips = IteSuperIo.OpenAll();
        if (chips.Count == 0) return null;
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
        return kept.Count > 0 ? kept : null;   // the caller publishes it under _gate
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
        lock (_openGate)   // never against an open in flight (see OpenSourcesOnce)
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
        // The timer was taken above and nothing else restarts it: with curves
        // or manual fans configured that left every header holding its last
        // duty and the thermal failsafe unevaluated until the Cooling pane
        // happened to be opened. The idle-stop reclaims it when nothing needs it.
        bool configured;
        lock (_gate) configured = _manualFans.Count > 0 || _fanCurves.Count > 0 || _pendingRestore.Count > 0;
        if (configured) EnsureRunning();
    }

    /// <summary>App exit: stop the timer and close every source. Only LHM's
    /// Computer.Close() unloads its ring0 driver service — process teardown
    /// leaves it registered — and it was never called on a clean exit. Touch()
    /// is a no-op afterwards so a late render tick can't re-open anything.</summary>
    public static void Shutdown()
    {
        lock (_openGate)   // never against an open in flight (see OpenSourcesOnce)
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
        // Drain BEFORE taking _writeGate: the in-flight tick takes _writeGate
        // for each of its duty writes, so waiting for it while holding that
        // lock would be a deadlock. Drain itself gives up after 2 s; the
        // _writeGate acquisition below is NOT bounded - a tick stuck inside a
        // hung backend write holds it - and that is deliberate, since the
        // alternative is disposing LHM underneath that write.
        Drain(timer);
        // Under _writeGate: this is the last handback of the board fans, and a
        // RestoreFan/SetFanDuty still running on the UI thread must not
        // interleave a write with it.
        lock (_writeGate)
            try { lhm?.Dispose(); } catch { }   // RestoreAll + Close
        DisposeSources(cpu, ite);
        }
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
    | is set back to the BIOS curve (as far as this process  |
    | can: see ReconcileFans and the dirty-start marker).    |
    | Fans are addressed by flat index into BoardFans.       |
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

    /*--- The last duty each fan was actually driven to, kept SEPARATE from the
          dedup cache above. _lastApplied is a write-avoidance cache: RestoreOne
          must drop its entry so the next write goes through, ReassertTicks
          ages it, OpenSourcesOnce clears it. None of that changes what the fan
          is physically doing, which is what "keep cooling" needs to know: the
          wireless sensor-loss handback used to read its duty from the dedup
          cache, found it emptied by its own first call, and dropped a fan the
          curve had at 90% to the 40% floor on the next tick. Cleared only
          when the fan is genuinely handed back for good (RestoreFan,
          RestoreAllFans). ---*/
    static readonly Dictionary<int, int> _safeDuty = new();

    /*--- Write serialization + mode generation. The tick snapshots the fan
          modes under _gate, releases it, then writes; a user RestoreFan in
          that window removed the mode AND restored firmware control, and the
          stale write then re-took the fan. Two pieces fix that:
            _modeGen  - bumped under _gate every time _manualFans/_fanCurves
                        change; a tick passes the generation it snapshotted
                        and ApplyDuty refuses the write if it has moved.
            _writeGate- held across (dedup check + backend write) in ApplyDuty
                        and across (mode change + generation bump + firmware
                        handback) in every mode transition, so the check and
                        the write are one atomic step against the transition.
          LOCK ORDER: _writeGate, then _gate - ALWAYS, never the reverse.
          _gate is only ever held briefly around dictionary access, so nothing
          that holds it needs _writeGate except OpenSourcesOnce, which takes
          them in this order. Shutdown drains the tick BEFORE taking
          _writeGate (see there). ---*/
    static readonly object _writeGate = new();
    static long _modeGen;

    /// <summary>Has the fan-mode generation moved since `gen` was captured?
    /// -1 = "no generation" (an unconditional user write) and is never stale.</summary>
    static bool ModeChangedSince(long gen)
    {
        if (gen < 0) return false;
        lock (_gate) return gen != _modeGen;
    }

    /// <summary>Route a duty write to the right backend, skipping it when the
    /// backend already holds that value (see the dedup note above). Lian duties
    /// quantize to 5% steps first so temperature jitter doesn't churn the radio.
    /// `gen` is the mode generation the caller's decision was based on (the
    /// tick's snapshot); the write is refused - false, nothing written - when
    /// a mode transition has happened since. Omit it for a user-initiated
    /// write that IS the transition. `remember` = false for a write that is
    /// not a cooling decision (the Identify burst): it must not become the
    /// "last safe duty" a sensor-loss handback later preserves.</summary>
    static bool ApplyDuty(int fanIndex, int percent, long gen = -1, bool remember = true)
    {
        bool lian = IsLian(fanIndex);
        if (lian) percent = (percent + 2) / 5 * 5;
        object? dev = lian ? Lian : null;
        lock (_writeGate)
        {
            lock (_gate)
            {
                if (gen >= 0 && gen != _modeGen) return false;
                // A wireless slot number only means something against the
                // instance the entries are keyed to. Between a rescan's dispose
                // and the tick's rekey the fresh instance can carry a different
                // slot order, and the receiver latches whatever lands: a write
                // keyed to the old instance would set the wrong physical fan
                // and leave it there. Refuse it; the rekey happens next tick.
                if (lian && !LianKeyMatches(dev)) return false;
                if (_lastApplied.TryGetValue(fanIndex, out var la) && la.Duty == percent && la.Device == dev
                    && (lian || _tickNo - la.Tick < ReassertTicks))
                    return true;
            }
            bool ok = ApplyDutyCore(fanIndex, percent, dev);
            if (ok)
                lock (_gate)
                {
                    _lastApplied[fanIndex] = new(percent, _tickNo, dev);
                    if (remember) _safeDuty[fanIndex] = percent;
                }
            return ok;
        }
    }

    /// <summary>Caller holds _gate. True when a wireless write to `dev` would
    /// land on the instance the Lian entries are keyed to (or nothing is keyed
    /// yet, which is the first reconcile).</summary>
    static bool LianKeyMatches(object? dev)
        => dev != null && (_lianKeyed == null || ReferenceEquals(dev, _lianKeyed));

    /// <summary>The actual backend write. GPU special case: the card clamps
    /// manual levels to its vBIOS minimum (30 on the 5090 — writing 0 silently
    /// becomes 30), so anything below that minimum hands the coolers back to
    /// the DRIVER instead: its auto mode is the only path to idle/zero-RPM
    /// behavior, where the card allows it.</summary>
    /// <param name="dev">The wireless instance the caller validated against
    /// the key (ApplyDuty), written to directly rather than re-read: a rescan
    /// completing between the check and this call would otherwise land the
    /// old slot number on the new instance.</param>
    static bool ApplyDutyCore(int fanIndex, int percent, object? dev = null)
    {
        if (IsLian(fanIndex))
        {
            if (dev is not Devices.LianLiWireless lian) return false;
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
                // "Already the driver's" is only true if the CARD says so: after
                // a crash the flag is false while the card still sits on the
                // dead run's manual duty, and a saved curve idling below the
                // minimum never got the coolers back to auto. Dedup keeps this
                // extra NvAPI read to once per ReassertTicks.
                if (!engaged && NvApi.IsGpuFanManual(gpu) != true) return true;
                bool ok = NvApi.RestoreGpuFanAuto(gpu);
                if (ok) lock (_gate) _gpuManualEngaged = false;
                return ok;
            }
            bool set = NvApi.SetGpuFanDuty(gpu, percent);
            if (set) lock (_gate) _gpuManualEngaged = true;
            return set;
        }
        LhmFans? lhm; lock (_gate) lhm = _lhm;
        if (lhm == null) return false;
        bool landed = lhm.SetDuty(fanIndex, percent);
        if (landed) MarkBoardControl(true);   // a board header is ours from here until it is handed back
        return landed;
    }

    /*--- Dirty-start marker. LibreHardwareMonitor can only undo what it did
          in-process: a run killed with a board header on our duty leaves that
          header where it was, and the next run's "Auto" is the value LHM finds
          there, not the BIOS curve, until a reboot. The marker exists while a
          board header is under our control and goes away with a clean handback;
          a launch that finds it says so, on screen and in the log. ---*/
    static string FanMarkerFile => AppPaths.Local("fan-control.active");
    static bool _markerWritten;

    static void MarkBoardControl(bool active)
    {
        try
        {
            if (active)
            {
                if (_markerWritten) return;
                File.WriteAllText(FanMarkerFile, DateTime.Now.ToString("s"));
                _markerWritten = true;
            }
            else
            {
                _markerWritten = false;
                if (File.Exists(FanMarkerFile)) File.Delete(FanMarkerFile);
            }
        }
        catch (Exception ex) { Log.Occasional("fan-marker", "fans", $"fan-control marker: {ex.Message}"); }
    }

    static void NoteDirtyStart()
    {
        try
        {
            if (!File.Exists(FanMarkerFile)) return;
            File.Delete(FanMarkerFile);
            Log.Warn("fans", "the previous run ended without handing its board fans back: a header may still be on that run's duty, and Auto cannot be the BIOS curve again until a reboot");
            UnifiedRgb.Core.Automation.ActivityLog.Note(UnifiedRgb.Core.Automation.ActivityKind.Problem,
                "UnifiedRGB did not shut down cleanly last time while it was driving a board fan. That fan may still be on the old speed, and Automatic will not be the board's own curve again until you reboot.");
        }
        catch (Exception ex) { Log.Occasional("fan-marker", "fans", $"fan-control marker: {ex.Message}"); }
    }

    /// <summary>Hand one fan back to automatic control: route the restore to the
    /// right backend, no mode bookkeeping (the dedup entry is dropped so the
    /// next duty write goes through; _safeDuty is NOT touched - see it).
    /// Returns what the backend actually reported, so a caller can say the fan
    /// is still ours instead of assuming: a wireless fan with no device to
    /// write to (mid-rescan), a GPU whose NvAPI call refused, a board header
    /// whose takeover release threw. Callers that need this atomic against a
    /// concurrent mode transition hold _writeGate around it.</summary>
    /// <param name="keepCooling">Never REDUCE a wireless fan's duty. Those have
    /// no BIOS curve to fall back on, so handing one back means writing a fixed
    /// 40% - fine when the user asked for it, but when we are handing a fan
    /// back because its sensor died, dropping a fan the curve had at 90% to 40%
    /// halves the cooling at the one moment nothing is watching the
    /// temperature.</param>
    /// <param name="retry">The tick re-trying a handback that was reported
    /// done: forces a real register write through LHM's dedup (see
    /// LhmFans.Restore).</param>
    static bool RestoreOne(int fanIndex, bool keepCooling = false, bool retry = false)
    {
        int safeDuty;
        lock (_gate)
        {
            safeDuty = _safeDuty.GetValueOrDefault(fanIndex);
            _lastApplied.Remove(fanIndex);
        }
        try
        {
            if (IsLian(fanIndex))
            {
                var lian = Lian;
                if (lian == null) return false;   // mid-rescan: nothing to write to, the fan keeps our duty
                // Same instance rule as ApplyDuty: the slot is meaningless
                // against an instance the entries were not keyed to.
                bool keyed; lock (_gate) keyed = LianKeyMatches(lian);
                if (!keyed) return false;
                int duty = keepCooling ? Math.Max(40, safeDuty) : 40;
                lian.SetFanDuty(fanIndex - LianFanBase, duty);   // no BIOS to hand back to
                return true;
            }
            if (fanIndex == GpuFanIndex)
            {
                IntPtr gpu; bool engaged;
                lock (_gate) { gpu = _gpu; engaged = _gpuManualEngaged; }
                if (gpu == IntPtr.Zero) return true;   // never ours: ApplyDutyCore refuses a zero handle
                // Only a confirmed restore clears the engaged flag: a refused
                // call used to be recorded as "the driver has it", and the
                // below-minimum handoff in ApplyDutyCore then skipped the real
                // restore forever ("already the driver's").
                bool ok = NvApi.RestoreGpuFanAuto(gpu);
                if (ok) lock (_gate) _gpuManualEngaged = false;
                // A refusal on a card that is NOT in manual is not a fan of ours
                // left behind: reporting it as one would queue a retry that keeps
                // the hub awake, one NvAPI call per tick, for nothing. The card's
                // own report decides, not the process-local engaged flag: after a
                // crash the flag is false while the card is still on the duty the
                // dead run wrote, and that one MUST be retried.
                return ok || (!engaged && NvApi.IsGpuFanManual(gpu) != true);
            }
            var lhm = Lhm;
            return lhm != null && lhm.Restore(fanIndex, force: retry);
        }
        catch (Exception ex)
        {
            Log.Occasional($"restore-one:{fanIndex}", "fans",
                $"could not hand fan {fanIndex} back to automatic control: {ex.Message}");
            return false;
        }
    }

    /// <summary>Fans whose handback FAILED and are still sitting on our duty
    /// with no mode entry left to say so (RestoreAllFans, RestoreFan, a
    /// crash-stuck cleanup that the board refused). The tick retries them and
    /// stays alive for them; a SetFanDuty/SetFanCurve that re-claims the fan
    /// removes it (it is ours again, on purpose).</summary>
    static readonly HashSet<int> _pendingRestore = new();

    static readonly Dictionary<int, int> _manualFans = new();       // fanIndex -> percent
    static readonly Dictionary<int, FanCurve> _fanCurves = new();   // fanIndex -> curve

    /// <summary>When each curve-controlled fan last had a usable temperature,
    /// and which ones we have handed back because it stopped arriving.
    ///
    /// A curve cannot decide anything without a reading, and the duty it last
    /// wrote LATCHES in the board (LHM software control persists until someone
    /// restores it). So a sensor that dies after the curve settled on 25% left
    /// that fan at 25% forever, with nothing watching: the loop just skipped
    /// ApplyDuty, and the over-temperature failsafe tests `is double`, so a
    /// null reading can never trip it either. Firmware control is the honest
    /// fallback - the BIOS curve is designed for exactly this.</summary>
    static readonly Dictionary<int, int> _blindTicks = new();
    static readonly HashSet<int> _sourceLost = new();

    /// <summary>How many CONSECUTIVE ticks a curve may go without a temperature
    /// before its fan is handed back - about 15 s at the 1.5 s tick.
    ///
    /// Ticks rather than a wall clock, and that is the whole point.
    /// TickCount64 counts time the machine spent SUSPENDED, so a grace period
    /// measured against it is already spent the moment the machine wakes - and
    /// the first tick or two after a resume is exactly when LHM, NvAPI and
    /// PawnIO reads fail, which is the case the grace period exists for. Every
    /// fan on a curve would have been handed back on every single wake.
    /// Counting ticks is immune to that by construction, with no need to hook
    /// power events.</summary>
    const int LostSourceGraceTicks = 10;
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

    /// <summary>The hub has completed at least one read since it started. Until
    /// then a null temperature means "not yet" (LHM is still loading), not
    /// "no source": the sensor-rule status used to say "PawnIO may not be
    /// installed" for the first seconds of every launch.</summary>
    public static bool HasPublished { get; private set; }

    /// <summary>Fans a handback was asked for but has not landed on (retried
    /// every tick). The activity history reads it so "every fan went back to
    /// the board" is only said when it is true.</summary>
    public static int PendingHandbackCount { get { lock (_gate) return _pendingRestore.Count; } }

    public static bool AnyControlledFan
    {
        get { lock (_gate) return _manualFans.Count > 0 || _fanCurves.Count > 0; }
    }

    /// <summary>Current manual duty for a fan, or null if it isn't in fixed
    /// manual mode (may still be on a curve).</summary>
    public static int? ManualFanDuty(int fanIndex)
    {
        RekeyLianIfReplaced();
        lock (_gate) return _manualFans.TryGetValue(fanIndex, out var p) ? p : null;
    }

    /// <summary>The curve a fan follows, or null if it isn't in curve mode.</summary>
    public static FanCurve? FanCurveOf(int fanIndex)
    {
        RekeyLianIfReplaced();
        lock (_gate) return _fanCurves.TryGetValue(fanIndex, out var c) ? c : null;
    }

    static LhmFans? Lhm { get { lock (_gate) return _lhm; } }

    static bool Controllable(int fanIndex)
    {
        RekeyLianIfReplaced();
        if (IsLian(fanIndex)) return fanIndex - LianFanBase < LianFanCount;
        if (fanIndex == GpuFanIndex) return GpuFansControllable;
        var lhm = Lhm;
        return lhm != null && fanIndex < lhm.Fans.Count && lhm.Fans[fanIndex].CanControl;
    }

    /*-------------- wireless fans: slot keys follow a rescan --------------*\
    | Lian entries are keyed by ARRANGED SLOT (LianFanBase + slot) against a |
    | particular LianLiWireless instance. The layout dialog saves a new      |
    | slot->chain order and triggers a Rescan, which REPLACES the instance   |
    | with one whose slots point at different physical fans - and every Lian |
    | entry here still said "slot 2", so the curve the user put on the top   |
    | fan silently started driving whichever fan was now arranged second.    |
    | The chain index is the physical identity (ReconcileFans already keys   |
    | the saved config by it), so when the instance changes, every entry is  |
    | moved from its old slot to the new slot that holds the same chain.     |
    \*-----------------------------------------------------------------------*/
    /// <summary>The instance the Lian entries are currently keyed against.
    /// May be disposed (a replaced instance); only ChainOf is ever called on
    /// it, which reads a plain array and is safe after Dispose.</summary>
    static Devices.LianLiWireless? _lianKeyed;

    /// <summary>Move the Lian-keyed entries onto the current instance's slots
    /// if a rescan has replaced it since they were keyed. Called before every
    /// read or write of a fan mode so the UI and the tick agree on which fan
    /// "slot N" is. No-op mid-rescan (Instance null): the entries stay keyed
    /// to the old instance and are moved when the new one appears.</summary>
    static void RekeyLianIfReplaced()
    {
        var lian = Lian;
        if (lian == null) return;
        if (ReferenceEquals(lian, Volatile.Read(ref _lianKeyed))) return;   // fast path, racy on purpose: re-checked under the locks
        lock (_writeGate)
        lock (_gate)
        {
            var old = _lianKeyed;
            if (ReferenceEquals(lian, old)) return;
            _lianKeyed = lian;
            if (old == null) return;   // first instance: nothing was keyed before it

            // old slot -> new slot (or -1) by chain. ChainOf clamps against
            // FanCount-1, so neither array is built for a zero-fan instance.
            var oldChains = new int[old.FanCount];
            for (int s = 0; s < oldChains.Length; s++) oldChains[s] = old.ChainOf(s);
            var newChains = new int[lian.FanCount];
            for (int s = 0; s < newChains.Length; s++) newChains[s] = lian.ChainOf(s);
            int[] map = LianSlotMap(oldChains, newChains);

            int before = _manualFans.Keys.Count(IsLian) + _fanCurves.Keys.Count(IsLian);
            RekeyLianEntries(_manualFans, map);
            RekeyLianEntries(_fanCurves, map);
            RekeyLianEntries(_blindTicks, map);
            RekeyLianEntries(_safeDuty, map);
            RekeyLianEntries(_sourceLost, map);
            RekeyLianEntries(_pendingRestore, map);
            int after = _manualFans.Keys.Count(IsLian) + _fanCurves.Keys.Count(IsLian);
            // The dedup cache keys the device instance too, so its Lian entries
            // could never match again; drop them rather than move them.
            foreach (int k in _lastApplied.Keys.Where(IsLian).ToList()) _lastApplied.Remove(k);
            _modeGen++;   // the tick's snapshot is keyed by the old slots
            Log.Info("fans", $"wireless fans re-keyed after rescan: {after} mode(s) followed their fan"
                + (before > after ? $", {before - after} dropped (fan no longer present)" : ""));
        }
    }

    /// <summary>For each old arranged slot, the new slot holding the same
    /// chain, or -1 when that chain is gone. Pure; internal for the tests.</summary>
    internal static int[] LianSlotMap(int[] oldChains, int[] newChains)
    {
        var map = new int[oldChains.Length];
        for (int s = 0; s < oldChains.Length; s++) map[s] = Array.IndexOf(newChains, oldChains[s]);
        return map;
    }

    /// <summary>Re-key every Lian entry (LianFanBase + old slot) through
    /// `slotMap` (old slot -> new slot, -1 = drop). Non-Lian keys are left
    /// alone, as is an old slot beyond the map (the old instance never had it).
    /// Pure over the dictionary; internal for the tests.</summary>
    internal static void RekeyLianEntries<T>(Dictionary<int, T> d, int[] slotMap)
    {
        var lianEntries = d.Where(kv => IsLian(kv.Key)).ToList();
        foreach (var kv in lianEntries) d.Remove(kv.Key);
        foreach (var kv in lianEntries)
        {
            int s = kv.Key - LianFanBase;
            if (s < slotMap.Length && slotMap[s] >= 0) d[LianFanBase + slotMap[s]] = kv.Value;
        }
    }

    internal static void RekeyLianEntries(HashSet<int> set, int[] slotMap)
    {
        var lianKeys = set.Where(IsLian).ToList();
        foreach (int k in lianKeys) set.Remove(k);
        foreach (int k in lianKeys)
        {
            int s = k - LianFanBase;
            if (s < slotMap.Length && slotMap[s] >= 0) set.Add(LianFanBase + slotMap[s]);
        }
    }

    /// <summary>Manual fixed duty (percent, floored per fan). Replaces
    /// any curve on that fan. Returns false when it isn't controllable.</summary>
    public static bool SetFanDuty(int fanIndex, int percent)
    {
        Touch();
        RekeyLianIfReplaced();
        if (!Controllable(fanIndex)) return false;
        percent = Math.Clamp(percent, ManualFloorFor(fanIndex), 100);
        // Write + mode change as one step under _writeGate: an in-flight tick
        // (still holding a curve snapshot for this fan) can otherwise land its
        // write between the two and leave the fan on the curve's duty with the
        // row saying Manual.
        lock (_writeGate)
        {
            if (!ApplyDuty(fanIndex, percent)) return false;
            lock (_gate)
            {
                _manualFans[fanIndex] = percent; _fanCurves.Remove(fanIndex);
                _pendingRestore.Remove(fanIndex);   // ours again, on purpose
                _modeGen++;
                FailsafeTripped = false;
            }
        }
        SaveFanConfig();
        return true;
    }

    /// <summary>Put a fan on a temperature curve. Replaces any fixed duty.
    /// The tick loop applies it continuously.</summary>
    public static bool SetFanCurve(int fanIndex, FanCurve curve)
    {
        Touch();
        RekeyLianIfReplaced();
        if (!Controllable(fanIndex)) return false;
        lock (_writeGate)
        {
            lock (_gate)
            {
                _fanCurves[fanIndex] = curve.Clone(); _manualFans.Remove(fanIndex);
                _pendingRestore.Remove(fanIndex);   // ours again, on purpose
                _modeGen++;
                FailsafeTripped = false;
                // A fresh curve starts with a clean slate: it has not gone blind
                // yet, and its grace period starts now rather than inheriting the
                // last one's timestamp.
                _sourceLost.Remove(fanIndex); _blindTicks[fanIndex] = 0;
            }
            // Apply immediately so the fan responds without waiting for the tick.
            var t = TempFor(curve.Source);
            if (t is double temp) ApplyDuty(fanIndex, Math.Max(FloorFor(fanIndex), curve.DutyAt(temp)));
        }
        SaveFanConfig();
        return true;
    }

    /// <summary>Hand one fan back to its own automatic control (BIOS curve for
    /// board fans, the driver curve incl. fan-stop for the GPU). A handback the
    /// backend refuses is queued for the tick to retry, not forgotten.</summary>
    public static void RestoreFan(int fanIndex)
    {
        RekeyLianIfReplaced();
        bool handed;
        // Mode removal + generation bump + the firmware handback under one
        // _writeGate hold: this is the transition the tick's stale writes must
        // never straddle (it snapshotted this fan on a curve, and would put
        // our duty back on it right after the firmware got it).
        lock (_writeGate)
        {
            lock (_gate)
            {
                _manualFans.Remove(fanIndex); _fanCurves.Remove(fanIndex);
                _blindTicks.Remove(fanIndex); _sourceLost.Remove(fanIndex);
                _safeDuty.Remove(fanIndex);
                _modeGen++;
            }
            handed = RestoreOne(fanIndex);
            if (!handed) lock (_gate) _pendingRestore.Add(fanIndex);
        }
        if (!handed)
        {
            Log.Warn("fans", $"fan {fanIndex}: set to Auto but handing it back FAILED - it is still on our last duty, retrying every tick");
            EnsureRunning();   // the retry lives in the tick; make sure there is one
        }
        SaveFanConfig();
        if (!AnyBoardFanOurs()) MarkBoardControl(false);
    }

    /// <summary>A board (LHM) header still under our control, or owed a handback.</summary>
    static bool AnyBoardFanOurs()
    {
        lock (_gate)
            return _manualFans.Keys.Concat(_fanCurves.Keys).Concat(_pendingRestore)
                .Any(i => !IsLian(i) && i != GpuFanIndex);
    }

    /// <summary>Hand every fan back to automatic (exit, crash, failsafe).
    /// keepConfig leaves the saved profiles in place (used on app exit so the
    /// next launch restores them).</summary>
    public static void RestoreAllFans(string reason, bool keepConfig = false)
    {
        LhmFans? lhm;
        IntPtr gpu;
        bool gpuCtl;
        // Each step reports rather than swallowing. This is the thermal path:
        // the line at the end used to claim every fan was back on auto even
        // when all three of these threw, which is the worst kind of log line,
        // one that is affirmatively wrong about hardware. A refusal counts the
        // same as a throw: a fan the backend would not release is still ours,
        // and goes into _pendingRestore for the tick to keep trying.
        var failed = new List<string>();
        var pending = new List<int>();
        lock (_writeGate)
        {
            lock (_gate)
            {
                lhm = _lhm; gpu = _gpu; gpuCtl = _gpuFanCtl;
                _manualFans.Clear(); _fanCurves.Clear(); _lastApplied.Clear();
                _blindTicks.Clear(); _sourceLost.Clear();
                _safeDuty.Clear(); _pendingRestore.Clear();
                _modeGen++;
            }
            try
            {
                if (lhm != null)
                {
                    var f = lhm.RestoreAll();
                    if (f.Count > 0)
                    {
                        failed.Add($"board fans {string.Join("/", f.Select(x => $"'{x.Name}'"))} (board refused)");
                        pending.AddRange(f.Select(x => x.Index));
                    }
                }
            }
            catch (Exception ex)
            {
                failed.Add($"board fans ({ex.Message})");
                // RestoreAll itself threw before it could say which: assume every
                // controllable header, and let the per-fan retry sort it out.
                if (lhm != null)
                    for (int i = 0; i < lhm.Fans.Count; i++) if (lhm.Fans[i].CanControl) pending.Add(i);
            }
            try
            {
                if (gpuCtl && gpu != IntPtr.Zero)
                {
                    bool engaged; lock (_gate) engaged = _gpuManualEngaged;
                    if (NvApi.RestoreGpuFanAuto(gpu)) lock (_gate) _gpuManualEngaged = false;
                    // Only a fan actually in manual is worth a retry (see RestoreOne).
                    else if (engaged || NvApi.IsGpuFanManual(gpu) == true) { failed.Add("GPU fans (NvAPI refused)"); pending.Add(GpuFanIndex); }
                }
            }
            catch (Exception ex) { failed.Add($"GPU fans ({ex.Message})"); pending.Add(GpuFanIndex); }
            // Wireless fans: failsafe means FULL BLAST (there is no BIOS curve to
            // fall back to); a plain restore-all returns them to the 40% baseline.
            // App exit (keepConfig) leaves their latched duty untouched. Not
            // queued for retry: there is no per-fan "auto" to reach, and a
            // missing instance (mid-rescan) means the next ReconcileFans owns it.
            bool failsafe = reason.Contains("failsafe");
            try
            {
                if ((!keepConfig || failsafe) && Lian is { } lw)
                {
                    int duty = failsafe ? 100 : 40;
                    for (int s = 0; s < lw.FanCount; s++) lw.SetFanDuty(s, duty);
                }
            }
            catch (Exception ex) { failed.Add($"wireless fans ({ex.Message})"); }
            lock (_gate) foreach (int i in pending) _pendingRestore.Add(i);
        }

        if (!keepConfig)
        {
            try { File.Delete(FanConfigFile); }
            catch (Exception ex) { Log.Warn("fans", $"could not clear the saved fan config: {ex.Message}"); }
        }

        if (failed.Count == 0) Log.Info("fans", $"all fans restored to auto ({reason})");
        else Log.Error("fans", $"NOT fully restored ({reason}): {string.Join(", ", failed)} still under our control"
            + (pending.Count > 0 ? " - retrying every tick" : ""));
        // Every board header is the board's again (or queued for the retry,
        // which clears the marker itself once the last one lands).
        if (pending.Count == 0) MarkBoardControl(false);
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
            if (!ApplyDuty(fanIndex, 100, remember: false)) return;   // a burst, not a cooling decision
            string idName = fanIndex == GpuFanIndex ? "GPU"
                : IsLian(fanIndex) ? $"Lian Li slot {fanIndex - LianFanBase + 1}"
                : Lhm?.Fans[fanIndex].Name ?? $"#{fanIndex}";
            Log.Info("fans", $"identify: fan '{idName}' at 100% for 4s");
            await Task.Delay(4000);
            // Put back whatever mode the fan is in NOW, not the one captured
            // before the burst: a slider or mode change made during the four
            // seconds used to be overwritten by the stale capture, and nothing
            // re-asserted the newer manual duty afterwards.
            // Under _writeGate so the mode read and the put-back are one step
            // against a RestoreFan/SetFanDuty landing between them.
            int? manual;
            FanCurve? curve;
            lock (_writeGate)
            {
                lock (_gate)
                {
                    manual = _manualFans.TryGetValue(fanIndex, out var p) ? p : null;
                    curve = _fanCurves.TryGetValue(fanIndex, out var cc) ? cc : null;
                }
                if (manual is int m) ApplyDuty(fanIndex, m);
                else if (curve is FanCurve c && TempFor(c.Source) is double t) ApplyDuty(fanIndex, Math.Max(FloorFor(fanIndex), c.DutyAt(t)));
                else if (!RestoreOne(fanIndex))
                {
                    // The burst left it at 100% and the backend would not take
                    // it back: the tick retries, and stays alive to do so.
                    lock (_gate) _pendingRestore.Add(fanIndex);
                    Log.Warn("fans", $"identify: fan {fanIndex} could not be handed back after the burst - retrying every tick");
                }
            }
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
    | configured -> SetDefault. NOTE: that does NOT clear a duty a    |
    | crashed run left behind - LHM can only restore what it saw at  |
    | its own first write in THIS process - which is what the        |
    | fan-control.active marker below is for.                        |
    \*--------------------------------------------------------------*/
    static string FanConfigFile => AppPaths.Local("fan-config.json");

    sealed record FanConfigEntry(string Name, string Kind, int Pct, FanCurve? Curve);

    static void SaveFanConfig()
    {
        RekeyLianIfReplaced();
        try
        {
            var entries = new List<FanConfigEntry>();
            lock (_gate)
            {
                var fans = _lhm?.Fans;
                // Lian slots are resolved to chains through the instance they
                // are KEYED against (_lianKeyed), not whatever Instance is at
                // this instant: a rescan between the rekey above and here
                // would otherwise save the modes under the wrong fans.
                string? NameOf(int i) => i == GpuFanIndex ? "GPU"
                    : IsLian(i) ? (_lianKeyed is { } lw && i - LianFanBase < lw.FanCount ? $"LianLi:{lw.ChainOf(i - LianFanBase)}" : null)
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
    /// to automatic. A duty a KILLED run left on a header is not undone by
    /// that: LibreHardwareMonitor's SetDefault restores the register value it
    /// captured at its first write in this process, so on a fresh process it
    /// has nothing to restore - and worse, a header still in software mode
    /// from the dead run is what it captures as "default". The marker file
    /// makes that visible instead of silent.</summary>
    static void ReconcileFans()
    {
        NoteDirtyStart();
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
                lock (_gate) { _fanCurves[i] = curve; _pendingRestore.Remove(i); _modeGen++; }
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
                lock (_gate) { _manualFans[i] = pct; _pendingRestore.Remove(i); _modeGen++; }
                ApplyDuty(i, pct);
                Log.Info("fans", $"restored '{name}' -> manual {pct}%");
            }
            else Log.Warn("fans", $"ignoring saved mode for '{name}': kind={e.Kind}, curve={(e.Curve != null)}");
        }

        // A handback the backend refuses is queued for the tick to retry, like
        // any other failed handback.
        var lhm = _lhm;
        if (lhm != null)
            for (int i = 0; i < lhm.Fans.Count; i++)
            {
                if (!lhm.Fans[i].CanControl) continue;
                if (cfg.TryGetValue(lhm.Fans[i].Name, out var e)) Apply(i, lhm.Fans[i].Name, e);
                else
                {
                    bool ok; try { ok = lhm.Restore(i); } catch { ok = false; }   // back to the board (this run's view of it)
                    if (!ok) lock (_gate) _pendingRestore.Add(i);
                }
            }

        if (_gpuFanCtl)
        {
            if (cfg.TryGetValue("GPU", out var e)) Apply(GpuFanIndex, "GPU", e);
            else if (!RestoreOne(GpuFanIndex)) lock (_gate) _pendingRestore.Add(GpuFanIndex);   // the card reports a stuck duty itself (see RestoreOne)
        }

        // Lian Li wireless fans: keys are chain-stable so re-arranging slots
        // keeps each physical fan's saved mode. Unconfigured fans just keep
        // their latched duty (nothing to clean - RF duty persists by design).
        // The slot keys written here belong to THIS instance: record it so
        // RekeyLianIfReplaced can move them when a rescan replaces it.
        if (Lian is { } lwr)
        {
            // Through the rekey, not a plain assignment: entries keyed to an
            // instance a rescan already replaced (a re-open can land inside
            // that one-tick window) must MOVE to the new slots, not be
            // re-labelled in place.
            RekeyLianIfReplaced();
            for (int s = 0; s < lwr.FanCount; s++)
            {
                string key = $"LianLi:{lwr.ChainOf(s)}";
                if (cfg.TryGetValue(key, out var e)) Apply(LianFanBase + s, key, e);
            }
        }
    }
}

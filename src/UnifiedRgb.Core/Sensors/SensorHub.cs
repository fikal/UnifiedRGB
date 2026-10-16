using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Sensors;

/// <summary>Why every fan is being handed back. It used to be a free-text
/// reason plus a keepConfig flag, with the behaviour picked by
/// reason.Contains("failsafe"), and every caller passed keepConfig: true -
/// so the delete-the-config and 40%-baseline branches were dead.</summary>
public enum FanHandback
{
    /// <summary>Clean app exit. Wireless fans keep their latched duty (the
    /// caller does the optional hand-off to the board's PWM line).</summary>
    Exit,
    /// <summary>The process is dying. Wireless fans we were driving go to
    /// 100% (nobody will run their curve, and they have no BIOS to fall
    /// back to) unless the caller hands them to the board's PWM line.</summary>
    Crash,
    /// <summary>The thermal failsafe. Every wireless fan goes to 100%, and
    /// FailsafeTripped is set.</summary>
    Failsafe,
}

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
    /// number the temp-reactive lighting rides. Lighting only: it falls back to
    /// whichever reading exists, which is right for a glow and wrong for a fan
    /// curve (see ControlHottest).</summary>
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
            ReleaseOldBoard();

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
            int reloads = Volatile.Read(ref _fanConfigReloads);
            var cfg = LoadFanConfigFile();   // file I/O, outside the locks

            bool resave;
            lock (_writeGate)
            lock (_gate)
            {
                if (_shutdown)
                {
                    // Went down while the backends were opening: publish nothing.
                    try { lhm?.Dispose(); } catch { }   // the hub is gone: closing is all that is left, and Dispose logs its own failures
                    DisposeSources(cpu, ite);
                    return;
                }
                resave = PublishOpened(cpu, lhm, ite, gpu, gpuFanCtl, gpuMinDuty, cfg, reloads);
            }
            // Still under _openGate, so a Shutdown right behind this open
            // waits for the file to say what the fans were just set to.
            if (resave) SaveFanConfig();
        }
    }

    /// <summary>The first half of an open: take the previous LHM instance out
    /// and close it (a re-open after ResetSources; a no-op the first time).
    /// Caller holds _openGate and neither of the other locks.</summary>
    static void ReleaseOldBoard()
    {
        LhmFans? old;
        lock (_gate) { old = _lhm; _lhm = null; }
        // Under _writeGate: a RestoreFan/SetFanDuty on the UI thread must not
        // interleave a write with the old instance's close.
        lock (_writeGate) { try { old?.Dispose(); } catch { } }   // Dispose logs a header it could not release; ClosedClean below says the rest
        if (old != null && !old.ClosedClean)
        {
            // The new instance cannot hand those headers back: LHM only
            // restores what the same instance took over. They stay on our
            // last duty until a reboot, so the marker must survive for the
            // next launch to say so (it used to be deleted by the next
            // handback of some other fan).
            PinBoardMarker();
            Log.Warn("fans", "re-opening the board sensors left a header on our last duty (see the line above): it stays there until a reboot");
        }
    }

    /// <summary>The second half of an open: publish the fresh backends and
    /// drive the fans from the saved config. True when what the fans were set
    /// to differs from the file as read (see SessionConfig), and the caller
    /// should save it. Caller holds _writeGate and _gate.</summary>
    /// <param name="reloads">_fanConfigReloads when `cfg` was read.</param>
    static bool PublishOpened(RyzenCpuTemperature? cpu, LhmFans? lhm, List<IteBoardChip>? ite,
        IntPtr gpu, bool gpuFanCtl, int? gpuMinDuty, Dictionary<string, FanConfigEntry>? cfg, int reloads)
    {
        _sourcesOpened = true;
        _lastApplied.Clear();   // fresh backends know nothing: ReconcileFans must write, not dedup
        // Board/GPU entries are re-derived below (ReconcileFans restores
        // every unconfigured fan); a wireless one is not - it is a fan the
        // receiver still holds at our duty - so it stays queued.
        _pendingRestore.RemoveWhere(i => !IsLian(i));
        // The board and GPU modes come off their indices and go back on by
        // NAME (SessionConfig + ReconcileFans). A re-open after ResetSources
        // used to leave them keyed to the OLD enumeration: when LHM numbered
        // the headers differently the next time, a curve kept driving whatever
        // header now sat at its index, and the next save filed it under that
        // fan's name. Taken before ReconcileFans replaces the names they map by.
        var running = TakeBoardAndGpuModes();
        _cpu = cpu; _lhm = lhm;
        if (ite != null) _iteChips = ite;
        _gpu = gpu; _gpuFanCtl = gpuFanCtl; _gpuHasRead = false;
        if (gpuMinDuty is int min) _gpuMinDuty = min;
        string board = _lhm != null ? $"{_lhm.Fans.Count} fans"
            : _iteChips != null ? $"{_iteChips.Sum(c => c.FanSlots.Length)} fans (ITE/PawnIO)"
            : "n/a";
        Log.Info("sensors",
            $"hub started (cpu={(_cpu != null ? "ok" : "n/a")}, board={board}, gpu={(_gpu != IntPtr.Zero ? (_gpuFanCtl ? "ok+fanctl" : "ok") : "n/a")})");
        // A ReloadFanConfig (a setup import) that landed after the read
        // above put the newer file in _fileEntries: applying the copy read
        // here would put the old config back, and the next save would
        // write it over the import.
        var file = (reloads == _fanConfigReloads ? cfg : _fileEntries) ?? _fileEntries;
        // A choice this session made wins over the file as read: an Auto
        // clicked during the seconds this open takes found no board to hand
        // the fan to, its save kept the file entry (the fan was not
        // addressable with _lhm null), and the open then put the mode the
        // file was read with back on the fan.
        var effective = SessionConfig(file, running, _knownNames, _suspended, out bool changed);
        // A fan this session decided that is not here this time is no longer
        // decided: its mode rides in the file (effective carries it) and is
        // applied by name when it is back, instead of the next save dropping
        // it as a fan on Auto.
        var present = new HashSet<string>(StringComparer.Ordinal);
        if (lhm != null) foreach (var f in lhm.Fans) if (f.CanControl) present.Add(f.Name);
        if (gpuFanCtl) present.Add("GPU");
        _knownNames.RemoveWhere(n => !IsLianName(n) && !present.Contains(n));
        ReconcileFans(effective);
        return changed;
    }

    /// <summary>Every board and GPU mode, by the name it was set under, taken
    /// off its index (with that index's per-fan state) so the reconcile can
    /// put it back on whichever index the name has now. Caller holds _gate.</summary>
    static Dictionary<string, FanConfigEntry> TakeBoardAndGpuModes()
    {
        var byName = new Dictionary<string, FanConfigEntry>(StringComparer.Ordinal);
        foreach (var kv in _manualFans)
            if (!IsLian(kv.Key) && FanNameOf(kv.Key) is string n) byName[n] = new(n, "manual", kv.Value, null);
        foreach (var kv in _fanCurves)
            if (!IsLian(kv.Key) && FanNameOf(kv.Key) is string n) byName[n] = new(n, "curve", 0, kv.Value);
        foreach (int k in _manualFans.Keys.Where(k => !IsLian(k)).ToList()) _manualFans.Remove(k);
        foreach (int k in _fanCurves.Keys.Where(k => !IsLian(k)).ToList()) _fanCurves.Remove(k);
        foreach (int k in _blindTicks.Keys.Where(k => !IsLian(k)).ToList()) _blindTicks.Remove(k);
        foreach (int k in _safeDuty.Keys.Where(k => !IsLian(k)).ToList()) _safeDuty.Remove(k);
        _sourceLost.RemoveWhere(k => !IsLian(k));
        _lastSpun.Clear();   // keyed by board index too: a stale one keeps an empty header listed
        _modeGen++;
        return byName;
    }

    static bool IsLianName(string name) => name.StartsWith("LianLi:", StringComparison.Ordinal);

    /// <summary>The config an open reconciles the board and GPU fans against:
    /// the file, except that every board/GPU fan this session has DECIDED
    /// (`decided`) gets what the session last had on it - its running mode,
    /// or no entry (Auto) - and a suspended one keeps the file's entry (the
    /// reconcile skips it anyway). Undecided fans get the file, as before.
    /// Wireless names are left alone: ReconcileLian already never re-applies
    /// a fan this session decided. `changed` = the result differs from the
    /// file, which the caller then saves. Null in, null out (unreadable).
    /// Pure; internal for the tests.</summary>
    internal static Dictionary<string, FanConfigEntry>? SessionConfig(Dictionary<string, FanConfigEntry>? file,
        IReadOnlyDictionary<string, FanConfigEntry> running, IEnumerable<string> decided,
        IReadOnlyDictionary<string, FanConfigEntry> suspended, out bool changed)
    {
        changed = false;
        if (file == null) return null;
        var result = new Dictionary<string, FanConfigEntry>(file, StringComparer.Ordinal);
        foreach (string n in decided)
        {
            if (IsLianName(n)) continue;
            if (running.TryGetValue(n, out var e)) { result[n] = e; changed = true; }
            else if (!suspended.ContainsKey(n) && result.Remove(n)) changed = true;
        }
        return result;
    }

    /// <summary>Bumped by every ReloadFanConfig, under _gate. See OpenSourcesOnce.</summary>
    static int _fanConfigReloads;

    static int _ticking;

    /// <summary>A tick that never returns (a hung NvAPI, LHM or PawnIO call)
    /// leaves _ticking set, and every later tick is skipped: the control loop
    /// AND the thermal failsafe stop, and nothing used to say so. The watch
    /// times the tick in flight so a skipped tick can.</summary>
    static readonly StallWatch _stall = new(StallLogMs);
    const long StallLogMs = 20_000;   // past the curves' 15 s grace period

    /// <summary>Times the tick in flight. Begin/End run only on the ticking
    /// thread (inside the _ticking guard); Stalled is read by the ticks that
    /// are skipped. Pure; internal for the tests.</summary>
    internal sealed class StallWatch
    {
        readonly long _thresholdMs;
        long _startedMs = -1;
        public StallWatch(long thresholdMs) => _thresholdMs = thresholdMs;
        public void Begin(long nowMs) => Volatile.Write(ref _startedMs, nowMs);
        /// <summary>How long the finished tick took, when that was over the
        /// threshold (worth a "running again" line); otherwise null.</summary>
        public long? End(long nowMs)
        {
            long began = Volatile.Read(ref _startedMs);
            Volatile.Write(ref _startedMs, -1);
            return began >= 0 && nowMs - began > _thresholdMs ? nowMs - began : null;
        }
        /// <summary>How long the tick in flight has been running, when that is
        /// over the threshold; otherwise null.</summary>
        public long? Stalled(long nowMs)
        {
            long began = Volatile.Read(ref _startedMs);
            return began >= 0 && nowMs - began > _thresholdMs ? nowMs - began : null;
        }
    }

    /// <summary>System.Threading.Timer fires on the pool regardless of whether
    /// the previous callback finished, so a stalled NvAPI/LHM read (sleep/resume,
    /// driver reset) used to overlap two sweeps: concurrent LHM Update(), a
    /// double-counted hot tick, torn CPU-load deltas. Skip the tick instead —
    /// and never let an exception out of a Timer callback (process-fatal).</summary>
    static void Tick()
    {
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0)
        {
            if (_stall.Stalled(Environment.TickCount64) is long stuck)
                Log.Occasional("sensors-stalled", "sensors",
                    $"a sensor tick has been stuck for {stuck / 1000} s: fan curves and the thermal failsafe are NOT running until it returns");
            return;
        }
        _stall.Begin(Environment.TickCount64);
        try { TickCore(); }
        catch (Exception ex) { Log.Occasional("sensors-tick", "sensors", () => $"tick failed: {ex.Message}"); }
        finally
        {
            if (_stall.End(Environment.TickCount64) is long took)
                Log.Warn("sensors", $"a sensor tick took {took / 1000} s; fan control and the failsafe are running again");
            Volatile.Write(ref _ticking, 0);
        }
    }

    static void TickCore()
    {
        OpenSourcesOnce();
        // A wireless receiver that appeared after the open (late at boot, or a
        // rescan that brought new fans) gets its saved modes now, not at the
        // next launch.
        if (Lian is { } lianNow && !ReferenceEquals(lianNow, Volatile.Read(ref _lianReconciledFor)))
        {
            try { ReconcileLateLian(lianNow); }
            catch (Exception ex) { Log.Occasional("fans-lian-reconcile", "fans", $"applying the wireless fans' saved modes failed: {ex.Message}"); }
        }
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
        var cpu = _cpu; var lhm = _lhm; var ite = _iteChips; var gpu = _gpu;
        double? cpuT; int? gpuT;
        try { cpuT = cpu?.ReadCelsius(); } catch { cpuT = null; }   // a failed read is "no reading"; WatchSources says so once it persists
        try { gpuT = gpu != IntPtr.Zero ? NvApi.GetGpuTemperature(gpu) : null; } catch { gpuT = null; }   // as above
        CpuTempC = cpuT; GpuTempC = gpuT;
        if (gpuT != null) lock (_gate) _gpuHasRead = true;
        HasPublished = true;
        WatchSources(cpu, cpuT, GpuSourceLive, gpuT);   // last use of `cpu`: a re-open may dispose it

        // Failsafe FIRST, straight after the temperatures are published: it
        // used to run after the per-fan write loops, so a fan whose write threw
        // skipped it on every tick. Any control + a hot CPU or GPU (three
        // consecutive ticks, ~4.5 s, so a junk reading or a transient spike
        // can't trip it) = hand everything back to auto for this session. Zen
        // 4/5 parts deliberately run all-core loads at a 95°C Tctl target
        // (Tjmax 89 on the 7000-X3D parts), so the CPU line sits ABOVE that: 96
        // means the CPU's own limiter is losing. The modes are SUSPENDED, not
        // erased: the failsafe protects the hardware, it shouldn't erase the
        // user's configuration.
        bool tooHot = (cpuT is double c && c >= FailsafeCpuC)
                   || (gpuT is int g && g >= FailsafeGpuC);
        if (anyManual && tooHot)
        {
            if (++_hotTicks >= FailsafeTicks)
            {
                _hotTicks = 0;
                Log.Warn("fans", $"FAILSAFE: CPU {cpuT:0.0}°C / GPU {gpuT}°C with fan control active — restoring all fans to auto (saved curves kept)");
                RestoreAllFans(FanHandback.Failsafe);
                anyManual = false;   // every mode is suspended now: nothing below is ours to drive
                lock (_gate) anyPending = _pendingRestore.Count > 0;
            }
        }
        else _hotTicks = 0;

        if (uiActive)
        {
            try { GpuFanRpms = gpu != IntPtr.Zero ? NvApi.GetGpuFanRpms(gpu) : null; } catch { GpuFanRpms = null; }
            try { GpuLoadPct = gpu != IntPtr.Zero ? NvApi.GetGpuLoad(gpu) : null; } catch { GpuLoadPct = null; }
            try { GpuVoltage = gpu != IntPtr.Zero ? NvApi.GetGpuCoreVoltage(gpu) : null; } catch { GpuVoltage = null; }
            try { CpuLoadPct = ReadCpuLoad(); } catch { CpuLoadPct = null; }
        }
        if (lhm != null && needBoard)
        {
            try
            {
                // Under _writeGate: LHM's Computer is not thread-safe, and a
                // SetFanDuty/RestoreFan on the UI thread could run its control
                // write in the middle of this sweep.
                lock (_writeGate) lhm.Refresh();
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
        //
        // Each fan is its own try: one fan whose backend threw used to end the
        // loop, and every fan after it went undriven.
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
                try { if (!DriveCurve(kv.Key, kv.Value, gen)) break; }
                catch (Exception ex)
                {
                    Log.Occasional($"fan-tick:{kv.Key}", "fans", $"fan {kv.Key}: driving its curve failed: {ex.Message}");
                }
            }
            foreach (var kv in manual)
            {
                if (ModeChangedSince(gen)) break;
                if (busy?.Contains(kv.Key) == true) continue;
                try { ApplyDuty(kv.Key, kv.Value, gen); }
                catch (Exception ex)
                {
                    Log.Occasional($"fan-tick:{kv.Key}", "fans", $"fan {kv.Key}: re-asserting its duty failed: {ex.Message}");
                }
            }
        }

        // Handbacks that failed (RestoreAllFans, RestoreFan, a crash-stuck
        // cleanup): the fan is on our duty with no mode left to say so, which
        // is the one state this hub must never leave a fan in. Retry each tick
        // until the backend accepts it.
        if (anyPending)
        {
            List<int> retry;
            lock (_gate) retry = _pendingRestore.ToList();
            foreach (int i in retry)
            {
                try { RetryHandback(i); }
                catch (Exception ex)
                {
                    Log.Occasional($"pending-restore:{i}", "fans", $"fan {i}: retrying its handback failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>One curve step for one fan (the tick's loop body). False = the
    /// mode generation moved under it, and the loop should stop.</summary>
    static bool DriveCurve(int fan, FanCurve curve, long gen)
    {
        var t = TempFor(curve.Source);
        if (t is double temp)
        {
            bool wasLost;
            lock (_gate) { _blindTicks[fan] = 0; wasLost = _sourceLost.Remove(fan); }
            if (wasLost)
                Log.Info("fans", $"fan {fan}: {curve.Source} temperature is back, the curve is driving it again");
            ApplyDuty(fan, Math.Max(FloorFor(fan), curve.DutyAt(temp)), gen);
            return true;
        }

        // No reading. Ride out a short gap; past the grace period the
        // fan goes back to firmware control. The curve is KEPT, so the
        // moment a reading returns the branch above re-arms it.
        int blind; bool alreadyLost;
        lock (_gate)
        {
            blind = _blindTicks[fan] = _blindTicks.GetValueOrDefault(fan) + 1;
            alreadyLost = _sourceLost.Contains(fan);
        }
        if (blind < LostSourceGraceTicks) return true;

        // Retried every tick ONLY until the handback succeeds, then
        // latched in _sourceLost: RestoreOne can fail, and a fan
        // recorded as handed back when it is still sitting at our duty
        // is under nobody's control while the log says otherwise. But
        // once it HAS succeeded, calling it again each tick is not a
        // retry - for a wireless fan (no firmware to hand back to) it
        // re-wrote the "keep cooling" duty from a cache the first call
        // had already emptied, and the fan dropped to the 40% floor on
        // the second tick after all.
        if (alreadyLost) return true;
        bool handed;
        lock (_writeGate)
        {
            // Same rule as the writes: a mode change since the
            // snapshot means this fan may no longer be ours to hand
            // back (RestoreFan did it, or it is now Manual).
            if (ModeChangedSince(gen)) return false;
            // Same rule as ApplyDuty: a burst that began after the snapshot
            // owns the fan (at 100%) until its put-back, which hands it back
            // itself when there is still no reading.
            bool bursting; lock (_gate) bursting = _identifying.Contains(fan);
            if (bursting) return true;
            handed = RestoreOne(fan, keepCooling: true, retry: true);
            if (handed) lock (_gate) _sourceLost.Add(fan);
        }
        if (handed)
            Log.Warn("fans", $"fan {fan}: no {curve.Source} temperature for {LostSourceGraceTicks} ticks "
                           + "- handed back to firmware control (the curve is kept and re-arms if it returns)");
        else
            Log.Occasional($"fan-restore:{fan}", "fans",
                $"fan {fan}: no {curve.Source} temperature and handing it back FAILED - it is still on our last duty");
        return true;
    }

    /// <summary>One retry of a failed handback (the tick's pending loop body).
    /// Under _writeGate with a membership re-check, so a SetFanDuty that has
    /// just re-claimed the fan (and removed it from the set) can never be
    /// undone by this retry.</summary>
    static void RetryHandback(int i)
    {
        bool handed, clearMarker = false;
        lock (_writeGate)
        {
            bool still; lock (_gate) still = _pendingRestore.Contains(i);
            if (!still) return;
            handed = RestoreOne(i, retry: true);   // a handback already reported done needs a forced write
            if (handed) { lock (_gate) _pendingRestore.Remove(i); clearMarker = !AnyBoardFanOurs(); }
        }
        if (clearMarker) MarkBoardControl(false);
        if (handed) Log.Info("fans", $"fan {i}: handed back to automatic control on retry");
        else Log.Occasional($"pending-restore:{i}", "fans",
            $"fan {i}: still could not be handed back to automatic control - retrying every tick");
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
        IntPtr gpu; bool gpuCtl, gpuOwed;
        lock (_gate)
        {
            _shutdown = true;
            timer = _timer; _timer = null; _running = false;
            lhm = _lhm; _lhm = null;
            cpu = _cpu; _cpu = null;
            ite = _iteChips; _iteChips = null;
            // The GPU too. It used to stay published after Shutdown, and the
            // board fans were the only ones handed back here: an Identify burst
            // on the GPU (exit within its four seconds) or a GPU handback still
            // queued left the card in NvAPI manual until the next launch.
            // Cleared now, so nothing after this point - a draining tick, a
            // late put-back - can write NvAPI: ApplyDutyCore refuses a zero
            // handle. Owed is read now, before a draining tick can drop it.
            gpu = _gpu; gpuCtl = _gpuFanCtl;
            _gpu = IntPtr.Zero; _gpuFanCtl = false;
            gpuOwed = _pendingRestore.Contains(GpuFanIndex) || _identifying.Contains(GpuFanIndex);
        }
        // Drain BEFORE taking _writeGate: the in-flight tick takes _writeGate
        // for each of its duty writes, so waiting for it while holding that
        // lock would be a deadlock. Drain itself gives up after 2 s; the
        // _writeGate acquisition below is NOT bounded - a tick stuck inside a
        // hung backend write holds it - and that is deliberate, since the
        // alternative is disposing LHM underneath that write.
        Drain(timer);
        bool owedClean = true;
        // Under _writeGate: this is the last handback of the board fans, and a
        // RestoreFan/SetFanDuty still running on the UI thread must not
        // interleave a write with it.
        lock (_writeGate)
        {
            // A board handback still owed gets the tick's forced retry one last
            // time: Dispose's RestoreAll skips a header LHM believes is already
            // the board's, which is the one a release that threw leaves behind.
            List<int> owedBoard;
            lock (_gate) owedBoard = _pendingRestore.Where(i => !IsLian(i) && i != GpuFanIndex).ToList();
            if (lhm != null)
                foreach (int i in owedBoard)
                {
                    if (lhm.Restore(i, force: true)) lock (_gate) _pendingRestore.Remove(i);
                    else owedClean = false;   // LhmFans logs the refusal itself
                }
            try { lhm?.Dispose(); } catch { }   // RestoreAll + Close; a header it could not release is logged there and leaves ClosedClean false

            bool engaged; lock (_gate) engaged = _gpuManualEngaged;
            if (gpuCtl && gpu != IntPtr.Zero && (engaged || gpuOwed))
            {
                bool ok;
                try { ok = GpuRestoreAuto(gpu); }
                catch (Exception ex) { ok = false; Log.Warn("fans", $"closing: handing the GPU fans back threw: {ex.Message}"); }
                if (ok) lock (_gate) { _gpuManualEngaged = false; _pendingRestore.Remove(GpuFanIndex); }
                else Log.Error("fans", "closing: the GPU fans could not be handed back to the driver - they stay on our last duty until the next launch");
            }
        }
        // Every board header is the board's again: the marker has done its
        // job. A clean exit with no fan under control skips RestoreAllFans, so
        // an Identify burst's marker used to outlive it, and the next launch
        // warned about a crash that never happened. Only when this run opened
        // LHM: otherwise the marker on disk is a previous run's evidence, not
        // ours to clear.
        if (lhm != null && lhm.ClosedClean && owedClean) MarkBoardControl(false);
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
        _ => ControlHottest(),
    };

    /// <summary>"Hottest" for a fan curve. It used to be HottestC, which falls
    /// back to the GPU alone when the CPU reads nothing: a PawnIO hiccup left
    /// every Hottest curve (the board fans' default) following the GPU, CPU
    /// fans at the floor under an all-core load, and the grace-period handback
    /// never ran because the reading was never null. A source that OPENED and
    /// now reads nothing makes it null, so that handback applies. A source that
    /// never opened (no PawnIO, an Intel CPU, no NVIDIA card) is simply not
    /// part of the max, as before.</summary>
    static double? ControlHottest()
    {
        bool cpuOpen; lock (_gate) cpuOpen = _cpu != null;
        return HottestOf(CpuTempC, cpuOpen, GpuTempC, GpuSourceLive);
    }

    /// <summary>The GPU counts as an opened source once it has produced a
    /// reading since the open. The CPU source only opens on a real reading
    /// (RyzenCpuTemperature.TryCreate), but an NvAPI handle exists for a card
    /// whose temperature may never read (a powered-down laptop GPU): that one
    /// stays out of the max instead of nulling every Hottest curve.</summary>
    static bool GpuSourceLive { get { lock (_gate) return _gpu != IntPtr.Zero && _gpuHasRead; } }
    static bool _gpuHasRead;

    /// <summary>The curve's Hottest from the two readings and whether each
    /// source opened. Pure; internal for the tests.</summary>
    internal static double? HottestOf(double? cpu, bool cpuOpen, double? gpu, bool gpuOpen)
    {
        if ((cpuOpen && cpu == null) || (gpuOpen && gpu == null)) return null;
        if (cpu == null) return gpu;
        if (gpu == null) return cpu;
        return Math.Max(cpu.Value, gpu.Value);
    }

    /// <summary>What the thermal failsafe cannot see while it guards fan
    /// control: "CPU", "GPU", "CPU and GPU", or null. The failsafe trips on a
    /// reading, so a missing one is a hot part it would never notice. The
    /// Cooling pane shows it. The GPU half only counts once the GPU has read
    /// (GpuSourceLive): a machine without an NVIDIA card has no GPU reading
    /// to lose.</summary>
    public static string? FailsafeBlindSpot
    {
        get
        {
            return BlindSpot(HasPublished && AnyControlledFan, CpuTempC != null, GpuSourceLive, GpuTempC != null);
        }
    }

    /// <summary>Pure half of FailsafeBlindSpot; internal for the tests.</summary>
    internal static string? BlindSpot(bool guarding, bool cpuReading, bool gpuOpen, bool gpuReading)
    {
        if (!guarding) return null;
        bool cpu = !cpuReading, gpu = gpuOpen && !gpuReading;
        return cpu && gpu ? "CPU and GPU" : cpu ? "CPU" : gpu ? "GPU" : null;
    }

    /*--- Temperature-source health. A source that opened and then stops
          reading used to go dark without a line, and nothing ever re-opened
          the CPU one: one PawnIO hiccup and the CPU half of the failsafe was
          blind for the rest of the session. ---*/
    static readonly SourceWatch _cpuWatch = new(), _gpuWatch = new();

    /// <summary>Consecutive-miss bookkeeping for one temperature source. Pure;
    /// internal for the tests.</summary>
    internal sealed class SourceWatch
    {
        public enum Change { None, Lost, Back, Reopen }

        /// <summary>Two misses (~3 s) before "lost" is said: the first read
        /// after a resume often fails, and that is not worth a line.</summary>
        public const int LostAfterMisses = 2;
        /// <summary>First re-open at the curves' grace period (~15 s), then
        /// about once a minute while it stays dark.</summary>
        public const int FirstReopenAt = 10, ReopenEvery = 40;

        int _misses;
        bool _lost;

        public Change Observe(bool open, bool reading)
        {
            if (!open) { _misses = 0; _lost = false; return Change.None; }
            if (reading)
            {
                _misses = 0;
                if (!_lost) return Change.None;
                _lost = false;
                return Change.Back;
            }
            _misses++;
            if (!_lost && _misses >= LostAfterMisses) { _lost = true; return Change.Lost; }
            if (_misses == FirstReopenAt || (_misses > FirstReopenAt && (_misses - FirstReopenAt) % ReopenEvery == 0))
                return Change.Reopen;
            return Change.None;
        }
    }

    /// <summary>Lost/back lines are rate-limited per source and direction: a
    /// source that flaps would otherwise write a pair every few seconds.</summary>
    static void WatchSources(RyzenCpuTemperature? cpu, double? cpuT, bool gpuOpen, int? gpuT)
    {
        switch (_cpuWatch.Observe(cpu != null, cpuT != null))
        {
            case SourceWatch.Change.Lost:
                Log.Occasional("cpu-temp-lost", "sensors", "the CPU temperature stopped reading: curves on CPU or Hottest hand their fans back to the board "
                    + "after the grace period, and the thermal failsafe cannot see the CPU until it returns");
                break;
            case SourceWatch.Change.Back:
                Log.Occasional("cpu-temp-back", "sensors", "the CPU temperature is reading again");
                break;
            case SourceWatch.Change.Reopen:
                if (cpu != null) ReopenCpuSource(cpu);
                break;
        }
        // No re-open for the GPU: its handle comes from the driver's own
        // enumeration, and a driver reset brings the readings back by itself.
        switch (_gpuWatch.Observe(gpuOpen, gpuT != null))
        {
            case SourceWatch.Change.Lost:
                Log.Occasional("gpu-temp-lost", "sensors", "the GPU temperature stopped reading: curves on GPU or Hottest hand their fans back "
                    + "after the grace period, and the thermal failsafe cannot see the GPU until it returns");
                break;
            case SourceWatch.Change.Back:
                Log.Occasional("gpu-temp-back", "sensors", "the GPU temperature is reading again");
                break;
        }
    }

    /// <summary>Swap a CPU source that has stopped reading for a fresh one, on
    /// the tick thread. Not under _openGate: ResetSources/Shutdown hold that
    /// while they drain this very tick. The swap is a compare under _gate
    /// instead: if either of them took the field first, the fresh source is
    /// theirs to never see and is closed here, and the dead one is theirs to
    /// dispose after the drain.</summary>
    static void ReopenCpuSource(RyzenCpuTemperature dead)
    {
        RyzenCpuTemperature? fresh = null;
        try { fresh = RyzenCpuTemperature.TryCreate(); }
        catch (Exception ex) { Log.Occasional("cpu-reopen", "sensors", $"re-opening the CPU temperature failed: {ex.Message}"); }
        if (fresh == null)
        {
            Log.Occasional("cpu-reopen", "sensors", "the CPU temperature is still not reading and re-opening it did not help - trying again in a minute");
            return;
        }
        bool swapped;
        lock (_gate)
        {
            swapped = !_shutdown && ReferenceEquals(_cpu, dead);
            if (swapped) _cpu = fresh;
        }
        var close = swapped ? dead : fresh;
        try { close.Dispose(); }
        catch (Exception ex) { Log.Occasional("cpu-reopen-close", "sensors", $"closing a CPU temperature source failed: {ex.Message}"); }
        if (swapped) Log.Info("sensors", "the CPU temperature source was re-opened");
    }

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
                // An Identify burst owns the fan until its put-back. The tick
                // skips a bursting fan, but only if the burst began before its
                // snapshot: one that began after it (Identify does not move the
                // generation) had its 100% overwritten with the curve duty by
                // this very write, and every later tick skipped the fan, so it
                // sat there for the rest of the burst. The burst's own write and
                // its put-back carry no generation and are not refused.
                if (gen >= 0 && _identifying.Contains(fanIndex)) return false;
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
                if (!engaged && GpuIsManual(gpu) != true) return true;
                bool ok = GpuRestoreAuto(gpu);
                if (ok) lock (_gate) _gpuManualEngaged = false;
                return ok;
            }
            bool set = GpuSetDuty(gpu, percent);
            if (set) lock (_gate) _gpuManualEngaged = true;
            return set;
        }
        LhmFans? lhm; lock (_gate) lhm = _lhm;
        if (lhm == null) return false;
        bool landed = lhm.SetDuty(fanIndex, percent);
        if (landed) MarkBoardControl(true);   // a board header is ours from here until it is handed back
        return landed;
    }

    /*--- The three NvAPI calls that change (or ask) who drives the GPU's
          coolers, behind one seam: the harness drives the GPU branches -
          Shutdown's handback, an Identify burst - against a fake card instead
          of the real one. Nothing in the app reassigns them. ---*/
    internal static Func<IntPtr, int, bool> GpuSetDuty = NvApi.SetGpuFanDuty;
    internal static Func<IntPtr, bool> GpuRestoreAuto = NvApi.RestoreGpuFanAuto;
    internal static Func<IntPtr, bool?> GpuIsManual = NvApi.IsGpuFanManual;

    /*--- Dirty-start marker. LibreHardwareMonitor can only undo what it did
          in-process: a run killed with a board header on our duty leaves that
          header where it was, and the next run's "Auto" is the value LHM finds
          there, not the BIOS curve, until a reboot. The marker exists while a
          board header is under our control and goes away with a clean handback;
          a launch that finds it says so, on screen and in the log. ---*/
    internal static string FanMarkerFile => AppPaths.Local("fan-control.active");
    static bool _markerWritten;
    // Set once a handback is known to have been abandoned (a re-open whose old
    // LHM instance could not release a header): that header is on our duty
    // until a reboot, so no later handback of another fan may delete the
    // marker for the rest of this run.
    static bool _markerPinned;

    internal static void MarkBoardControl(bool active)
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
                if (_markerPinned) return;
                _markerWritten = false;
                if (File.Exists(FanMarkerFile)) File.Delete(FanMarkerFile);
            }
        }
        catch (Exception ex) { Log.Occasional("fan-marker", "fans", $"fan-control marker: {ex.Message}"); }
    }

    /// <summary>Keep the marker for the rest of this run (see _markerPinned),
    /// writing it again if a handback had already removed it.</summary>
    static void PinBoardMarker()
    {
        _markerPinned = true;
        _markerWritten = false;
        MarkBoardControl(true);
    }

    static bool _dirtyStartNoted;

    /// <summary>Once per PROCESS, at the first open. It ran on every
    /// ReconcileFans, and a re-open (ResetSources after a PawnIO install) found
    /// this run's own live marker - a header under our control right now -
    /// reported it as an unclean shutdown, and deleted it.</summary>
    internal static void NoteDirtyStart()
    {
        if (_dirtyStartNoted) return;
        _dirtyStartNoted = true;
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
                bool ok = GpuRestoreAuto(gpu);
                if (ok) lock (_gate) _gpuManualEngaged = false;
                // A refusal on a card that is NOT in manual is not a fan of ours
                // left behind: reporting it as one would queue a retry that keeps
                // the hub awake, one NvAPI call per tick, for nothing. The card's
                // own report decides, not the process-local engaged flag: after a
                // crash the flag is false while the card is still on the duty the
                // dead run wrote, and that one MUST be retried.
                return ok || (!engaged && GpuIsManual(gpu) != true);
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

    /// <summary>Modes the thermal failsafe (or an exit) took off the fans, by
    /// saved fan name. The failsafe used to CLEAR _manualFans/_fanCurves, and
    /// SaveFanConfig rebuilds the file from memory: the next slider move saved
    /// only that fan, and setting any fan to Auto deleted the file. Kept here
    /// they are still written, the fans stay on auto, and the user re-arms a
    /// fan by picking a mode for it (next launch re-applies them all).</summary>
    static readonly Dictionary<string, FanConfigEntry> _suspended = new(StringComparer.Ordinal);

    /// <summary>fan-config.json as last read or written; null = not read yet
    /// (or unreadable). Entries for fans this session has not decided - not
    /// opened yet, or not seen at all - are carried through every save
    /// (MergeFanConfig) instead of being dropped.</summary>
    static Dictionary<string, FanConfigEntry>? _fileEntries;

    /// <summary>The names in that snapshot, sorted. For the tests.</summary>
    internal static string[] SavedFanNames
    {
        get { lock (_gate) return _fileEntries?.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>(); }
    }

    /// <summary>Saved names this session has DECIDED: reconciled against the
    /// file at an open, or given a mode (or Auto) by the user. Only a decided,
    /// present fan without a mode loses its saved entry on a save.</summary>
    static readonly HashSet<string> _knownNames = new(StringComparer.Ordinal);

    /// <summary>LHM fan names by index, from the last open. Kept across the
    /// re-open window (_lhm is null for seconds while ResetSources re-opens),
    /// so a save in that window still names the board fans that have modes.</summary>
    static string[] _boardFanNames = Array.Empty<string>();

    /// <summary>The wireless instance whose fans were last reconciled against
    /// the saved config. A receiver that appears after the open (late at boot)
    /// or a rescan's new instance is reconciled by the next tick.</summary>
    static Devices.LianLiWireless? _lianReconciledFor;

    /// <summary>Bumped when modes change behind the UI's back: the failsafe
    /// suspending them, a config reload, a reconcile. The Cooling rows rebuild
    /// on it (a user's own edits do not bump it: the row already shows them,
    /// and a rebuild mid-drag would yank the slider).</summary>
    public static long ModeEpoch => Interlocked.Read(ref _modeEpoch);
    static long _modeEpoch;

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

    /// <summary>Set when the thermal failsafe forced everything back to auto
    /// (the modes are suspended, not erased); cleared by the next successful
    /// set, which re-arms that fan. Other fans stay suspended until picked.</summary>
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

    /// <summary>Does anything still have to be handed back: a fan under a
    /// mode, a handback that has not landed yet (the tick's retry queue), or
    /// an Identify burst in flight. The exit and crash paths ask this, not
    /// AnyControlledFan: a burst on the GPU left it in NvAPI manual at 100%
    /// until the next launch, and a queued handback was never retried.</summary>
    public static bool HandbackOwed
    {
        get { lock (_gate) return _manualFans.Count > 0 || _fanCurves.Count > 0 || _pendingRestore.Count > 0 || _identifying.Count > 0; }
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

    /// <summary>The fixed duty the failsafe suspended on this fan (kept and
    /// saved, but the fan is on auto until the user picks a mode), or null.</summary>
    public static int? SuspendedFanDuty(int fanIndex)
    {
        RekeyLianIfReplaced();
        lock (_gate)
            return FanNameOf(fanIndex) is string n && _suspended.TryGetValue(n, out var e) && e.Kind == "manual" ? e.Pct : null;
    }

    /// <summary>The curve the failsafe suspended on this fan, or null. The
    /// hub's own instance: clone it before editing (as with FanCurveOf).</summary>
    public static FanCurve? SuspendedFanCurveOf(int fanIndex)
    {
        RekeyLianIfReplaced();
        lock (_gate)
            return FanNameOf(fanIndex) is string n && _suspended.TryGetValue(n, out var e) && e.Kind == "curve" ? e.Curve : null;
    }

    /// <summary>The name a fan's mode is saved under: "GPU", "LianLi:{chain}"
    /// (through the instance the wireless entries are keyed to, not whatever
    /// Instance is this instant), or the LHM sensor name. Null when this
    /// session cannot name it. Caller holds _gate.</summary>
    static string? FanNameOf(int i)
        => i == GpuFanIndex ? "GPU"
         : IsLian(i) ? (_lianKeyed is { } lw && i - LianFanBase < lw.FanCount ? LianName(lw.ChainOf(i - LianFanBase)) : null)
         : i >= 0 && i < _boardFanNames.Length ? _boardFanNames[i] : null;

    static string LianName(int chain) => $"LianLi:{chain}";

    /// <summary>Can this session drive the fan saved under `name` right now?
    /// Caller holds _gate.</summary>
    static bool Addressable(string name)
    {
        if (name == "GPU") return _gpuFanCtl;
        if (name.StartsWith("LianLi:", StringComparison.Ordinal))
        {
            if (_lianKeyed is not { } lw) return false;
            for (int s = 0; s < lw.FanCount; s++) if (LianName(lw.ChainOf(s)) == name) return true;
            return false;
        }
        return _lhm is { } l && l.Fans.Any(f => f.CanControl && f.Name == name);
    }

    /// <summary>The user picked a mode (or Auto) for this fan: whatever the
    /// failsafe suspended on it is superseded, and its saved entry is now
    /// this session's to rewrite. Caller holds _gate.</summary>
    static void NoteUserDecided(int fanIndex)
    {
        if (FanNameOf(fanIndex) is not string n) return;
        _suspended.Remove(n);
        _knownNames.Add(n);
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
                NoteUserDecided(fanIndex);          // re-arms a fan the failsafe suspended
                _modeGen++;
                FailsafeTripped = false;
            }
        }
        SaveFanConfig();
        return true;
    }

    /// <summary>Put a fan on a temperature curve. Replaces any fixed duty.
    /// The tick loop applies it continuously. Refuses a curve with no points
    /// (see UsableCurve).</summary>
    public static bool SetFanCurve(int fanIndex, FanCurve curve)
    {
        if (!UsableCurve(curve)) return false;
        Touch();
        RekeyLianIfReplaced();
        if (!Controllable(fanIndex)) return false;
        lock (_writeGate)
        {
            lock (_gate)
            {
                _fanCurves[fanIndex] = curve.Clone(); _manualFans.Remove(fanIndex);
                _pendingRestore.Remove(fanIndex);   // ours again, on purpose
                NoteUserDecided(fanIndex);          // re-arms a fan the failsafe suspended
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
                NoteUserDecided(fanIndex);   // Auto drops a suspended mode too, and only this fan's entry
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

    /// <summary>Hand every fan back to automatic. The modes are SUSPENDED, not
    /// deleted, on every path: they stay in what SaveFanConfig writes, so no
    /// later save can drop them and the next launch re-applies them.</summary>
    /// <param name="wirelessHandoff">Crash only: the caller is about to hand
    /// the wireless fans to the board's PWM line, so leave them alone.</param>
    public static void RestoreAllFans(FanHandback why, bool wirelessHandoff = false)
    {
        string reason = why switch
        {
            FanHandback.Exit => "app exit",
            FanHandback.Crash => "crash",
            _ => "thermal failsafe",
        };
        LhmFans? lhm;
        IntPtr gpu;
        bool gpuCtl;
        List<int> lianControlled;
        var unnamed = new List<int>();
        // Each step reports rather than swallowing. This is the thermal path:
        // the line at the end used to claim every fan was back on auto even
        // when all three of these threw, which is the worst kind of log line,
        // one that is affirmatively wrong about hardware. A refusal counts the
        // same as a throw: a fan the backend would not release is still ours,
        // and goes into _pendingRestore for the tick to keep trying.
        var failed = new List<string>();
        var pending = new List<int>();
        bool lianWritten = false;
        // Owed before this call: handbacks the tick was still retrying, and fans
        // mid-Identify on the burst's 100%. At exit nothing retries the first
        // after this (they used to be cleared below and forgotten), and the
        // second's put-back never runs once the dispatcher is gone.
        List<int> owed, bursting;
        Dictionary<int, int> lastCooling;
        HashSet<int> heldByFailsafe;
        bool gpuOurs;
        lock (_writeGate)
        {
            lock (_gate)
            {
                lhm = _lhm; gpu = _gpu; gpuCtl = _gpuFanCtl;
                lianControlled = _manualFans.Keys.Concat(_fanCurves.Keys).Where(IsLian).Distinct().ToList();
                owed = _pendingRestore.ToList();
                bursting = _identifying.ToList();
                // Did this session drive the GPU at all: we hold it in manual, it
                // has a mode, a GPU handback is queued, or a burst is on it.
                gpuOurs = _gpuManualEngaged || _manualFans.ContainsKey(GpuFanIndex) || _fanCurves.ContainsKey(GpuFanIndex)
                       || owed.Contains(GpuFanIndex) || bursting.Contains(GpuFanIndex);
                lastCooling = new(_safeDuty);
                heldByFailsafe = bursting.Where(i => IsLian(i) && FanNameOf(i) is string n && _suspended.ContainsKey(n)).ToHashSet();
                SuspendModes(_manualFans, _fanCurves, _suspended, FanNameOf, unnamed);
                _lastApplied.Clear();
                _blindTicks.Clear(); _sourceLost.Clear();
                _safeDuty.Clear(); _pendingRestore.Clear();
                _modeGen++;
                if (why == FanHandback.Failsafe) FailsafeTripped = true;
            }
            Interlocked.Increment(ref _modeEpoch);   // the Cooling rows must stop showing modes that are not running
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
                    // A header whose handback was still owed gets the tick's
                    // forced retry: RestoreAll skips a header LHM already
                    // believes is on the board's curve, which is exactly the
                    // one a release that threw half-way leaves behind.
                    var stuck = owed.Where(i => !IsLian(i) && i != GpuFanIndex && !pending.Contains(i))
                                    .Where(i => !lhm.Restore(i, force: true)).ToList();
                    if (stuck.Count > 0)
                    {
                        failed.Add($"board fan(s) {string.Join("/", stuck)} (handback still refused)");
                        pending.AddRange(stuck);
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
                // Exit and crash hand the card back only if it is ours. A board
                // handback still queued now brings an exit here with no GPU mode
                // anywhere, and the unconditional "auto" took the coolers from
                // whatever else was driving them (MSI Afterburner) on a card
                // UnifiedRGB never touched that session. The failsafe keeps the
                // unconditional handback: on that path the driver's own curve,
                // which follows the temperature, is the safe place for the card.
                if (gpuCtl && gpu != IntPtr.Zero && (gpuOurs || why == FanHandback.Failsafe))
                {
                    bool engaged; lock (_gate) engaged = _gpuManualEngaged;
                    if (GpuRestoreAuto(gpu)) lock (_gate) _gpuManualEngaged = false;
                    // Only a fan actually in manual is worth a retry (see RestoreOne).
                    else if (engaged || GpuIsManual(gpu) == true) { failed.Add("GPU fans (NvAPI refused)"); pending.Add(GpuFanIndex); }
                }
            }
            catch (Exception ex) { failed.Add($"GPU fans ({ex.Message})"); pending.Add(GpuFanIndex); }
            // Wireless fans have no BIOS curve to fall back to (see FanHandback).
            // Not queued for retry: there is no per-fan "auto" to reach, and a
            // missing instance (mid-rescan) means the next ReconcileFans owns it.
            try
            {
                if (Lian is { } lw)
                {
                    if (why == FanHandback.Failsafe)
                    {
                        for (int s = 0; s < lw.FanCount; s++) lw.SetFanDuty(s, 100);
                        lianWritten = lw.FanCount > 0;
                    }
                    else if (why == FanHandback.Crash && !wirelessHandoff && lianControlled.Count > 0)
                    {
                        // Only the ones we were driving: an unconfigured fan is
                        // left latched at launch too, so a crash that put it at
                        // 100% would leave it there for good. Every fan when the
                        // slots are keyed to a replaced instance - better a loud
                        // fan than the wrong one left at a curve's idle duty.
                        bool keyed; lock (_gate) keyed = LianKeyMatches(lw);
                        var slots = keyed
                            ? lianControlled.Select(i => i - LianFanBase).Where(s => s < lw.FanCount)
                            : Enumerable.Range(0, lw.FanCount);
                        foreach (int s in slots) { lw.SetFanDuty(s, 100); lianWritten = true; }
                    }
                    // Exit and crash (not the failsafe, whose 100% covers every
                    // wireless fan, nor a hand-off to the PWM line): a wireless
                    // handback still owed is the user's Auto, the 40% RestoreFan
                    // writes; a fan mid-Identify would stay latched at the burst's
                    // 100%, so it goes back on the duty its mode last wrote (what
                    // Exit leaves every other wireless fan on), or 40% with no
                    // mode - as the burst's own put-back would have. A crash
                    // leaves a fan it was driving on its 100%, and a fan the
                    // failsafe is holding at 100% stays there.
                    if (why != FanHandback.Failsafe && !wirelessHandoff)
                    {
                        bool keyed; lock (_gate) keyed = LianKeyMatches(lw);
                        var back = new Dictionary<int, int>();
                        foreach (int i in owed.Where(IsLian)) back[i] = 40;
                        foreach (int i in bursting.Where(IsLian))
                        {
                            if (heldByFailsafe.Contains(i)) continue;
                            if (!lianControlled.Contains(i)) back[i] = 40;
                            else if (why == FanHandback.Exit) back[i] = lastCooling.TryGetValue(i, out int d) ? d : 40;
                        }
                        foreach (var kv in back)
                        {
                            int s = kv.Key - LianFanBase;
                            if (keyed && s < lw.FanCount) { lw.SetFanDuty(s, kv.Value); lianWritten = true; }
                            else failed.Add($"wireless fan slot {s + 1} (not keyed to this receiver)");
                        }
                    }
                }
            }
            catch (Exception ex) { failed.Add($"wireless fans ({ex.Message})"); }
            lock (_gate) foreach (int i in pending) _pendingRestore.Add(i);
        }

        // The wireless duty goes out on the driver's background thread, which a
        // dying process takes down with it: give it time for a couple of sends.
        if (why == FanHandback.Crash && lianWritten) Thread.Sleep(900);

        if (unnamed.Count > 0)
            Log.Warn("fans", $"could not name fan(s) {string.Join(", ", unnamed)} to keep their mode; they are handed back and their saved mode may be lost");
        if (failed.Count == 0) Log.Info("fans", $"all fans restored to auto ({reason}; their modes are kept)");
        else Log.Error("fans", $"NOT fully restored ({reason}): {string.Join(", ", failed)} still under our control"
            + (pending.Count > 0 ? " - retrying every tick" : ""));
        // Every board header is the board's again (or queued for the retry,
        // which clears the marker itself once the last one lands).
        if (pending.Count == 0) MarkBoardControl(false);
    }

    /// <summary>Move every active mode into `suspended`, by saved name, and
    /// clear the active dictionaries. A fan this session cannot name goes into
    /// `unnamed` (its mode is lost; the caller says so). Pure over its
    /// arguments; internal for the tests.</summary>
    internal static void SuspendModes(Dictionary<int, int> manual, Dictionary<int, FanCurve> curves,
        Dictionary<string, FanConfigEntry> suspended, Func<int, string?> nameOf, List<int> unnamed)
    {
        foreach (var kv in manual)
            if (nameOf(kv.Key) is string n) suspended[n] = new(n, "manual", kv.Value, null);
            else unnamed.Add(kv.Key);
        foreach (var kv in curves)
            if (nameOf(kv.Key) is string n) suspended[n] = new(n, "curve", 0, kv.Value);
            else unnamed.Add(kv.Key);
        manual.Clear();
        curves.Clear();
    }

    static readonly HashSet<int> _identifying = new();

    /// <summary>"Which physical fan is this?" — blast the header to 100% for
    /// four seconds, then put it back to whatever mode it was in. Full-blast,
    /// never a stop: a stop test on the pump header would be risky; a burst is
    /// unmistakable and safe.</summary>
    public static async Task IdentifyFan(int fanIndex)
    {
        bool downAtStart;
        lock (_gate)
        {
            if (!_identifying.Add(fanIndex)) return;   // already running
            downAtStart = _shutdown;
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
            await Task.Delay(IdentifyBurstMs);
            // Put back whatever mode the fan is in NOW, not the one captured
            // before the burst: a slider or mode change made during the four
            // seconds used to be overwritten by the stale capture, and nothing
            // re-asserted the newer manual duty afterwards.
            // Under _writeGate so the mode read and the put-back are one step
            // against a RestoreFan/SetFanDuty landing between them.
            int? manual;
            FanCurve? curve;
            bool clearMarker = false, down, heldByFailsafe;
            lock (_writeGate)
            {
                lock (_gate)
                {
                    manual = _manualFans.TryGetValue(fanIndex, out var p) ? p : null;
                    curve = _fanCurves.TryGetValue(fanIndex, out var cc) ? cc : null;
                    down = _shutdown && !downAtStart;
                    heldByFailsafe = IsLian(fanIndex) && FanNameOf(fanIndex) is string n && _suspended.ContainsKey(n);
                }
                // The app went down during the burst: Shutdown handed every
                // fan back (the exit's RestoreAllFans saw this burst) and
                // closed the backends, so a put-back now could only fail and
                // queue a retry nothing will run.
                if (down) return;
                if (manual is int m) ApplyDuty(fanIndex, m);
                else if (curve is FanCurve c && TempFor(c.Source) is double t) ApplyDuty(fanIndex, Math.Max(FloorFor(fanIndex), c.DutyAt(t)));
                // A wireless fan whose mode the failsafe suspended was on the
                // failsafe's 100%, which is where the burst left it: the 40%
                // "auto" below would cut its cooling while the failsafe that
                // put it there is still in force.
                else if (heldByFailsafe) { /* left on the failsafe's 100% */ }
                else if (RestoreOne(fanIndex))
                {
                    // The burst wrote the dirty-start marker and the put-back
                    // never cleared it, so a clean exit afterwards (with no fan
                    // under control, RestoreAllFans is skipped) left the next
                    // launch warning about a crash that never happened.
                    clearMarker = !AnyBoardFanOurs();
                }
                else
                {
                    // The burst left it at 100% and the backend would not take
                    // it back: the tick retries, and stays alive to do so.
                    lock (_gate) _pendingRestore.Add(fanIndex);
                    Log.Warn("fans", $"identify: fan {fanIndex} could not be handed back after the burst - retrying every tick");
                }
            }
            if (clearMarker) MarkBoardControl(false);
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

    /// <summary>One saved fan mode. Internal for the tests.</summary>
    internal sealed record FanConfigEntry(string Name, string Kind, int Pct, FanCurve? Curve);

    /// <summary>A curve with no points has nothing to interpolate: DutyAt
    /// answers 0, the floor turns that into 30%, and the fan sat there at any
    /// temperature. A hand-edited fan-config.json ("Points": [] or null) did
    /// exactly that.</summary>
    internal static bool UsableCurve(FanCurve? c) => c != null && c.Points.Count > 0;

    /// <summary>Why a saved entry cannot be applied, or null when it can.
    /// Pure; internal for the tests.</summary>
    internal static string? EntryProblem(FanConfigEntry e) => e.Kind switch
    {
        "curve" => UsableCurve(e.Curve) ? null : "a curve with no points",
        "manual" => null,
        _ => $"unknown kind '{e.Kind}'",
    };

    /// <summary>Write the saved modes. MERGES with what the file held: it used
    /// to be rebuilt from memory alone, so the first fan edit of a session
    /// permanently dropped every fan that session had not loaded - the Lian
    /// dongle late at boot, NvAPI not ready, LHM failing to open or mid
    /// re-open. Now a saved entry is dropped only for a fan this session has
    /// decided (see _knownNames) and can still drive, with no mode or
    /// suspended mode left on it - i.e. one that is genuinely on Auto.</summary>
    static void SaveFanConfig()
    {
        RekeyLianIfReplaced();
        try
        {
            bool have; lock (_gate) have = _fileEntries != null;
            // A save before the first open (a wireless fan edited in the first
            // second) has no snapshot yet: read the file, outside the lock.
            var fromDisk = have ? null : LoadFanConfigFile();
            List<FanConfigEntry> merged;
            lock (_gate)
            {
                _fileEntries ??= fromDisk;
                var live = LiveEntries(_manualFans, _fanCurves, _suspended, FanNameOf);
                merged = MergeFanConfig(_fileEntries?.Values ?? Enumerable.Empty<FanConfigEntry>(), live,
                    n => _knownNames.Contains(n) && Addressable(n));
                _fileEntries = merged.ToDictionary(e => e.Name, StringComparer.Ordinal);
            }
            if (merged.Count == 0)
            {
                // Nothing configured: a missing file says the same. Never silent:
                // a delete that failed brings the old modes back at the next launch.
                try { File.Delete(FanConfigFile); }
                catch (Exception ex) { Log.Warn("fans", $"could not remove {FanConfigFile} (every fan is on Auto now): {ex.Message}"); }
                return;
            }
            SafeFile.WriteAllText(FanConfigFile, System.Text.Json.JsonSerializer.Serialize(new { fans = merged }));
        }
        // Never silent: a save that fails here is a fan mode that will not
        // survive the next launch (this hid a missing %LOCALAPPDATA%\UnifiedRgb
        // on every clean install without the OpenRGB bridge).
        catch (Exception ex) { Log.Warn("fans", $"fan-config save failed: {ex.Message}"); }
    }

    /// <summary>The entries memory says: every active mode by name, then every
    /// suspended one (a fan never has both). Pure over its arguments; internal
    /// for the tests.</summary>
    internal static List<FanConfigEntry> LiveEntries(IReadOnlyDictionary<int, int> manual,
        IReadOnlyDictionary<int, FanCurve> curves, IReadOnlyDictionary<string, FanConfigEntry> suspended,
        Func<int, string?> nameOf)
    {
        var list = new List<FanConfigEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in manual)
            if (nameOf(kv.Key) is string n && seen.Add(n)) list.Add(new(n, "manual", kv.Value, null));
        foreach (var kv in curves)
            if (nameOf(kv.Key) is string n && seen.Add(n)) list.Add(new(n, "curve", 0, kv.Value));
        foreach (var e in suspended.Values)
            if (seen.Add(e.Name)) list.Add(e);
        return list;
    }

    /// <summary>What a save writes: the live entries, plus every previous
    /// entry for a name that is not live and that this session has not
    /// `decided`. Pure; internal for the tests.</summary>
    internal static List<FanConfigEntry> MergeFanConfig(IEnumerable<FanConfigEntry> previous,
        List<FanConfigEntry> live, Func<string, bool> decided)
    {
        var result = new List<FanConfigEntry>(live);
        var names = new HashSet<string>(live.Select(e => e.Name), StringComparer.Ordinal);
        foreach (var e in previous)
            if (!names.Contains(e.Name) && !decided(e.Name) && names.Add(e.Name)) result.Add(e);
        return result;
    }

    /// <summary>fan-config.json by name: empty when there is none, null when
    /// it exists but cannot be read (logged; the next save tries again).</summary>
    static Dictionary<string, FanConfigEntry>? LoadFanConfigFile()
    {
        try
        {
            if (!File.Exists(FanConfigFile)) return new(StringComparer.Ordinal);
            return ParseFanConfig(File.ReadAllText(FanConfigFile));
        }
        catch (Exception ex) { Log.Warn("fans", $"fan-config load failed: {ex.Message}"); return null; }
    }

    /// <summary>Parse the file's text. Throws on malformed JSON (the caller
    /// logs it). Internal for the tests.</summary>
    internal static Dictionary<string, FanConfigEntry> ParseFanConfig(string json)
    {
        var cfg = new Dictionary<string, FanConfigEntry>(StringComparer.Ordinal);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("fans", out var arr))
            foreach (var e in arr.EnumerateArray())
            {
                var entry = System.Text.Json.JsonSerializer.Deserialize<FanConfigEntry>(e.GetRawText());
                if (entry != null && !string.IsNullOrEmpty(entry.Name)) cfg[entry.Name] = entry;
            }
        return cfg;
    }

    /// <summary>On hub start (and a re-open, and a reload): apply saved modes
    /// and hand every other fan back to automatic. A duty a KILLED run left on
    /// a header is not undone by that: LibreHardwareMonitor's SetDefault
    /// restores the register value it captured at its first write in this
    /// process, so on a fresh process it has nothing to restore - and worse, a
    /// header still in software mode from the dead run is what it captures as
    /// "default". The marker file makes that visible instead of silent. A mode
    /// the failsafe suspended stays suspended: a re-open must not quietly
    /// re-arm it. Caller holds _writeGate and _gate.</summary>
    /// <param name="cfg">The file as just read; null = unreadable.</param>
    static void ReconcileFans(Dictionary<string, FanConfigEntry>? cfg)
    {
        NoteDirtyStart();
        if (cfg != null) _fileEntries = new(cfg, StringComparer.Ordinal);
        else if (_fileEntries != null) cfg = _fileEntries;   // a re-open whose read failed: the last good copy is just as current
        // Unreadable and never read: apply nothing, and decide nothing either,
        // so the next save carries the file's entries through instead of
        // judging them against a config this pass never saw.
        bool loaded = cfg != null;
        var saved = cfg ?? new(StringComparer.Ordinal);
        Interlocked.Increment(ref _modeEpoch);

        bool Wanted(string name, out FanConfigEntry e)
        {
            e = null!;
            return saved.TryGetValue(name, out e!) && !_suspended.ContainsKey(name);
        }

        // A handback the backend refuses is queued for the tick to retry, like
        // any other failed handback.
        var lhm = _lhm;
        if (lhm != null)
        {
            _boardFanNames = lhm.Fans.Select(f => f.Name).ToArray();
            for (int i = 0; i < lhm.Fans.Count; i++)
            {
                if (!lhm.Fans[i].CanControl) continue;
                string name = lhm.Fans[i].Name;
                if (loaded) _knownNames.Add(name);
                if (Wanted(name, out var e) && ApplySaved(i, name, e)) continue;
                bool ok; try { ok = lhm.Restore(i); } catch { ok = false; }   // back to the board (this run's view of it)
                if (!ok) lock (_gate) _pendingRestore.Add(i);
            }
        }

        if (_gpuFanCtl)
        {
            if (loaded) _knownNames.Add("GPU");
            if (!(Wanted("GPU", out var e) && ApplySaved(GpuFanIndex, "GPU", e))
                && !RestoreOne(GpuFanIndex))
                lock (_gate) _pendingRestore.Add(GpuFanIndex);   // the card reports a stuck duty itself (see RestoreOne)
        }

        if (Lian is { } lwr) ReconcileLian(lwr, saved, loaded);
    }

    /// <summary>Apply one saved mode to fan `i`. False (and logged) when the
    /// entry is unusable: the caller hands the fan back instead. Caller holds
    /// _writeGate and _gate.</summary>
    static bool ApplySaved(int i, string name, FanConfigEntry e)
    {
        if (EntryProblem(e) is string why)
        {
            Log.Warn("fans", $"ignoring saved mode for '{name}': {why} - it goes back to automatic control instead");
            return false;
        }
        if (e.Kind == "curve")
        {
            // Clone (as SetFanCurve does): the deserialized instance is handed
            // to the UI via FanCurveOf, and the editor mutates its Points list
            // in place while the tick thread reads it. Clone also re-sorts
            // hand-edited points.
            var curve = e.Curve!.Clone();
            lock (_gate)
            {
                _fanCurves[i] = curve; _manualFans.Remove(i); _pendingRestore.Remove(i); _modeGen++;
                _sourceLost.Remove(i); _blindTicks[i] = 0;
            }
            var t = TempFor(curve.Source);
            if (t is double temp) ApplyDuty(i, Math.Max(FloorFor(i), curve.DutyAt(temp)));
            Log.Info("fans", $"restored '{name}' -> curve {curve.Preset}");
        }
        else
        {
            // Manual floor, as the slider enforces: below the card's manual
            // minimum the GPU stays in driver auto while the row would say
            // "Manual · N%".
            int pct = Math.Clamp(e.Pct, ManualFloorFor(i), 100);
            lock (_gate) { _manualFans[i] = pct; _fanCurves.Remove(i); _pendingRestore.Remove(i); _modeGen++; }
            ApplyDuty(i, pct);
            Log.Info("fans", $"restored '{name}' -> manual {pct}%");
        }
        return true;
    }

    /// <summary>Lian Li wireless fans: keys are chain-stable, so re-arranging
    /// slots keeps each physical fan's saved mode. Only fans this session has
    /// not decided are applied - a re-open, or a rescan's new instance, must
    /// not undo a choice made since - and an unconfigured fan keeps its
    /// latched duty (RF duty persists by design: nothing to clean). Caller
    /// holds _writeGate and _gate.</summary>
    static void ReconcileLian(Devices.LianLiWireless lw, Dictionary<string, FanConfigEntry> saved, bool loaded)
    {
        // Through the rekey, not a plain assignment: entries keyed to an
        // instance a rescan already replaced (a re-open can land inside that
        // one-tick window) must MOVE to the new slots, not be re-labelled in
        // place. The slot keys written below belong to THIS instance.
        RekeyLianIfReplaced();
        Volatile.Write(ref _lianReconciledFor, lw);
        for (int s = 0; s < lw.FanCount; s++)
        {
            string key = LianName(lw.ChainOf(s));
            if (_knownNames.Contains(key)) continue;
            if (loaded) _knownNames.Add(key);
            if (saved.TryGetValue(key, out var e) && !_suspended.ContainsKey(key)) ApplySaved(LianFanBase + s, key, e);
        }
    }

    /// <summary>The wireless receiver came up after the hub opened (the known
    /// first-tick race at boot), or a rescan replaced it: its fans' saved
    /// modes used to wait for the next launch, while the rows said Auto.</summary>
    static void ReconcileLateLian(Devices.LianLiWireless lw)
    {
        bool need;
        lock (_gate) { if (!_sourcesOpened || _shutdown) return; need = _fileEntries == null; }
        var fromDisk = need ? LoadFanConfigFile() : null;   // file I/O outside the locks
        lock (_writeGate)
        lock (_gate)
        {
            if (!_sourcesOpened || _shutdown || ReferenceEquals(_lianReconciledFor, lw)) return;
            _fileEntries ??= fromDisk;
            ReconcileLian(lw, _fileEntries ?? new(StringComparer.Ordinal), _fileEntries != null);
            Interlocked.Increment(ref _modeEpoch);
        }
    }

    /// <summary>Re-read fan-config.json and drive the fans from it: a setup
    /// import has just replaced the file. The running hub used to keep working
    /// from what it loaded at startup, so the next fan edit rewrote the file
    /// from memory (dropping the imported modes) and an Auto deleted it, all
    /// before the restart the import asked for. Every fan the new file
    /// configures gets that mode; every other fan under our control is handed
    /// back, as at launch. Modes the failsafe suspended go with the old
    /// configuration: the imported file is the user's choice now. Touches no
    /// hardware until the hub has opened (the first open reads the new file
    /// itself). Call from a thread holding none of the hub's locks.</summary>
    public static void ReloadFanConfig()
    {
        RekeyLianIfReplaced();
        var cfg = LoadFanConfigFile();   // file I/O outside the locks
        var released = new List<int>();
        bool opened;
        int applied = 0;
        lock (_writeGate)
        {
            lock (_gate)
            {
                _suspended.Clear();
                _knownNames.Clear();
                _lianReconciledFor = null;
                Interlocked.Increment(ref _modeEpoch);
                Interlocked.Increment(ref _fanConfigReloads);
                opened = _sourcesOpened && !_shutdown;
                if (!opened) _fileEntries = cfg;
                else
                {
                    var before = _manualFans.Keys.Concat(_fanCurves.Keys).ToHashSet();
                    _manualFans.Clear(); _fanCurves.Clear(); _blindTicks.Clear(); _sourceLost.Clear();
                    _modeGen++;
                    FailsafeTripped = false;
                    // The board and GPU fans the new file does not configure are
                    // handed back inside ReconcileFans.
                    ReconcileFans(cfg);
                    applied = _manualFans.Count + _fanCurves.Count;
                    foreach (int i in before)
                        if (!_manualFans.ContainsKey(i) && !_fanCurves.ContainsKey(i)) { released.Add(i); _safeDuty.Remove(i); }
                }
            }
            // A wireless fan has no "auto" for ReconcileFans to reach: it gets
            // the same fixed 40% RestoreFan gives it.
            foreach (int i in released)
                if (IsLian(i) && !RestoreOne(i)) lock (_gate) _pendingRestore.Add(i);
        }
        if (!opened) return;
        Log.Info("fans", $"fan config reloaded: {applied} mode(s) applied, {released.Count} fan(s) handed back");
        if (!AnyBoardFanOurs()) MarkBoardControl(false);
        bool configured;
        lock (_gate) configured = _manualFans.Count > 0 || _fanCurves.Count > 0 || _pendingRestore.Count > 0;
        if (configured) EnsureRunning();   // the curves and any retry live in the tick
    }

    /*------------------------- test seams -------------------------*\
    | The harness shuts the hub down before any suite runs, so no     |
    | timer, driver, PawnIO or NvAPI call can happen in it. These let |
    | it run the hub's own code against fakes (LhmFans' test          |
    | constructor, the Gpu* delegates above) without that changing.   |
    \*--------------------------------------------------------------*/

    /// <summary>How long an Identify burst holds 100%. Settable ONLY so a
    /// test does not wait four seconds per burst.</summary>
    internal static int IdentifyBurstMs = 4000;

    /// <summary>The open's two halves against fakes, under the same locks the
    /// open takes: ReleaseOldBoard, then PublishOpened with the config as it
    /// was read (and `reloads` as it was then), then the save it asks for.</summary>
    internal static void ReleaseBoardForTest() { lock (_openGate) ReleaseOldBoard(); }

    internal static bool PublishForTest(LhmFans? lhm, IntPtr gpu, bool gpuFanCtl,
        Dictionary<string, FanConfigEntry>? cfg, int reloads)
    {
        lock (_openGate)
        {
            bool resave;
            lock (_writeGate)
            lock (_gate)
                resave = PublishOpened(null, lhm, null, gpu, gpuFanCtl, null, cfg, reloads);
            if (resave) SaveFanConfig();
            return resave;
        }
    }

    internal static int FanConfigReloadsForTest => Volatile.Read(ref _fanConfigReloads);
    internal static long ModeGenForTest { get { lock (_gate) return _modeGen; } }

    /// <summary>The tick's per-fan curve step, with the generation it
    /// snapshotted (see TickCore).</summary>
    internal static bool DriveCurveForTest(int fan, FanCurve curve, long gen) => DriveCurve(fan, curve, gen);

    internal static void PublishTempsForTest(double? cpu, int? gpu) { CpuTempC = cpu; GpuTempC = gpu; }

    /// <summary>Forget every fan mode and backend the tests put in, as if the
    /// hub had never opened. Does not touch _shutdown.</summary>
    internal static void ResetFansForTest()
    {
        lock (_writeGate)
        lock (_gate)
        {
            _manualFans.Clear(); _fanCurves.Clear(); _pendingRestore.Clear(); _suspended.Clear();
            _knownNames.Clear(); _blindTicks.Clear(); _sourceLost.Clear(); _safeDuty.Clear();
            _lastApplied.Clear(); _lastSpun.Clear(); _identifying.Clear();
            _boardFanNames = Array.Empty<string>();
            _lhm = null; _gpu = IntPtr.Zero; _gpuFanCtl = false; _gpuManualEngaged = false;
            _sourcesOpened = false; _fileEntries = null;
            FailsafeTripped = false;
            _modeGen++;
        }
        CpuTempC = null; GpuTempC = null;
    }
}

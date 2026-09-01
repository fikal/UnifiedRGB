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
    static long _lastReadTicks;
    static bool _running;

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
    public sealed record BoardFan(string Name, int? Rpm, bool CanControl);

    // Latest snapshot (volatile-ish: reference/primitive writes are atomic).
    public static double? CpuTempC { get; private set; }
    public static int? GpuTempC { get; private set; }
    public static double? CpuLoadPct { get; private set; }
    public static int? GpuLoadPct { get; private set; }
    /// <summary>CPU Vcore from the board's voltage rails (best-name match).</summary>
    public static double? CpuVoltage { get; private set; }
    public static double? GpuVoltage { get; private set; }
    /// <summary>One RPM per GPU fan (modern coolers have 2-3); null = no data.</summary>
    public static int[]? GpuFanRpms { get; private set; }
    public static BoardTemp[] BoardTemps { get; private set; } = Array.Empty<BoardTemp>();
    public static BoardFan[] BoardFans { get; private set; } = Array.Empty<BoardFan>();

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
    /// the sensor sources and keeps refreshing while anyone's interested.</summary>
    public static void Touch()
    {
        Interlocked.Exchange(ref _lastReadTicks, DateTime.UtcNow.Ticks);
        if (_running) return;
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            if (!_sourcesOpened)
            {
                _sourcesOpened = true;
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
            _timer ??= new Timer(_ => Tick(), null, 0, (int)(RefreshSeconds * 1000));
        }
    }

    static void Tick()
    {
        bool anyManual;
        lock (_gate) anyManual = _manualFans.Count > 0 || _fanCurves.Count > 0;

        // Never idle-stop while a fan is under manual control: the refresh
        // loop IS the failsafe watchdog.
        var last = new DateTime(Interlocked.Read(ref _lastReadTicks), DateTimeKind.Utc);
        if (!anyManual && (DateTime.UtcNow - last).TotalSeconds > IdleStopSeconds)
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                _running = false;
            }
            return;
        }
        // Split the sweep: with the window closed but a fan curve active, the
        // timer must keep running (it IS the control loop + failsafe), but only
        // the CONTROL-ESSENTIAL reads are needed — CPU/GPU temp, plus the board
        // sweep when a curve sources "Hottest". The GPU RPM/load/voltage calls
        // (non-blittable NvAPI deep-marshals) and the per-tick BoardTemps/
        // BoardFans projections are UI-only and used to run 24/7 regardless.
        bool uiActive = (DateTime.UtcNow - last).TotalSeconds <= IdleStopSeconds;
        bool needBoard = uiActive;
        if (!needBoard)
            lock (_gate) needBoard = _fanCurves.Values.Any(c => c.Source == TempSource.Hottest);

        try { CpuTempC = _cpu?.ReadCelsius(); } catch { CpuTempC = null; }
        try { GpuTempC = _gpu != IntPtr.Zero ? NvApi.GetGpuTemperature(_gpu) : null; } catch { GpuTempC = null; }
        if (uiActive)
        {
            try { GpuFanRpms = _gpu != IntPtr.Zero ? NvApi.GetGpuFanRpms(_gpu) : null; } catch { GpuFanRpms = null; }
            try { GpuLoadPct = _gpu != IntPtr.Zero ? NvApi.GetGpuLoad(_gpu) : null; } catch { GpuLoadPct = null; }
            try { GpuVoltage = _gpu != IntPtr.Zero ? NvApi.GetGpuCoreVoltage(_gpu) : null; } catch { GpuVoltage = null; }
            try { CpuLoadPct = ReadCpuLoad(); } catch { CpuLoadPct = null; }
        }
        if (_lhm != null && needBoard)
        {
            try
            {
                _lhm.Refresh();
                BoardTemps = _lhm.Temps.Select(t => new BoardTemp(t.Name, t.Value)).ToArray();
                if (uiActive)
                {
                    BoardFans = _lhm.Fans.Select(f => new BoardFan(f.Name, f.CurrentRpm, f.CanControl)).ToArray();
                    CpuVoltage = PickVcore(_lhm.Voltages);
                }
            }
            catch { }
        }
        else if (_iteChips != null && needBoard)
        {
            try { ReadIteBoard(); } catch { }
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
        // the interpolated duty (floored). Re-applied every tick so the fans
        // follow temperature.
        if (anyManual)
        {
            List<KeyValuePair<int, FanCurve>> curves;
            lock (_gate) curves = _fanCurves.ToList();
            foreach (var kv in curves)
            {
                var t = TempFor(kv.Value.Source);
                if (t is double temp)
                    ApplyDuty(kv.Key, Math.Max(FloorFor(kv.Key), kv.Value.DutyAt(temp)));
            }
        }

        // Failsafe: any control + a hot CPU or GPU (two consecutive ticks, so
        // a single junk reading can't trip it) = hand everything back to auto.
        // 92°C CPU is past normal boost even for X3D parts; 90°C GPU likewise.
        bool tooHot = (CpuTempC is double c && c >= FailsafeCpuC)
                   || (GpuTempC is int g && g >= FailsafeGpuC);
        if (anyManual && tooHot)
        {
            if (++_hotTicks >= 2)
            {
                FailsafeTripped = true;
                Log.Warn("fans", $"FAILSAFE: CPU {CpuTempC:0.0}°C / GPU {GpuTempC}°C with fan control active — restoring all fans to auto");
                RestoreAllFans("thermal failsafe");
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
    /// and keep the fan/temp slots that report a real value at open (unwired
    /// tach/temp registers read null and are dropped). Needs PawnIO installed +
    /// elevation; a no-op otherwise.</summary>
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
    static void ReadIteBoard()
    {
        var temps = new List<BoardTemp>();
        var fans = new List<BoardFan>();
        int fanNo = 1, tempNo = 1;
        foreach (var c in _iteChips!)
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

    /// <summary>Drop and re-open the sensor sources on the next read. Called
    /// after PawnIO is installed so CPU temp and the ITE board fallback light up
    /// without an app restart (both need PawnIO, which was absent at first open).</summary>
    public static void ResetSources()
    {
        lock (_gate)
        {
            _timer?.Dispose(); _timer = null; _running = false;
            // Dispose, not just drop: _cpu owns a PawnIO KERNEL DRIVER handle
            // (no finalizer) — nulling it leaked the handle for the process
            // lifetime, and the next Touch() opened a second one.
            try { _cpu?.Dispose(); } catch { }
            _cpu = null;
            try { if (_iteChips != null) foreach (var c in _iteChips) c.Chip.Dispose(); } catch { }
            _iteChips = null;
            // _lhm/_gpu are unaffected by PawnIO; leave them, but a full re-open is
            // simplest and safe — clear the latch so the next Touch() rebuilds all.
            _sourcesOpened = false;
        }
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
    const double FailsafeCpuC = 92;
    const double FailsafeGpuC = 90;

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

    /// <summary>Route a duty write to the right backend. GPU special case:
    /// the card clamps manual levels to its vBIOS minimum (30 on the 5090 —
    /// writing 0 silently becomes 30), so anything below that minimum hands
    /// the coolers back to the DRIVER instead: its auto mode is the only
    /// path to idle/zero-RPM behavior, where the card allows it.</summary>
    static bool ApplyDuty(int fanIndex, int percent)
    {
        if (IsLian(fanIndex))
        {
            var lian = Lian;
            if (lian == null) return false;
            lian.SetFanDuty(fanIndex - LianFanBase, (percent + 2) / 5 * 5);
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

    /// <summary>Route an auto-restore to the right backend (no bookkeeping).</summary>
    static void RestoreOne(int fanIndex)
    {
        try
        {
            if (IsLian(fanIndex))
                Lian?.SetFanDuty(fanIndex - LianFanBase, 40);   // no BIOS to hand back to - 40% baseline
            else if (fanIndex == GpuFanIndex)
            {
                IntPtr gpu; lock (_gate) gpu = _gpu;
                if (gpu != IntPtr.Zero) NvApi.RestoreGpuFanAuto(gpu);
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
        LhmFans? lhm;
        IntPtr gpu;
        lock (_gate) { lhm = _lhm; gpu = _gpu; _manualFans.Remove(fanIndex); _fanCurves.Remove(fanIndex); }
        try
        {
            if (IsLian(fanIndex))
                Lian?.SetFanDuty(fanIndex - LianFanBase, 40);   // no BIOS to hand back to - 40% baseline
            else if (fanIndex == GpuFanIndex)
            {
                if (gpu != IntPtr.Zero) NvApi.RestoreGpuFanAuto(gpu);
                lock (_gate) _gpuManualEngaged = false;
            }
            else lhm?.Restore(fanIndex);
        }
        catch { }
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
        lock (_gate) { lhm = _lhm; gpu = _gpu; gpuCtl = _gpuFanCtl; _manualFans.Clear(); _fanCurves.Clear(); }
        try { lhm?.RestoreAll(); } catch { }
        try { if (gpuCtl && gpu != IntPtr.Zero) { NvApi.RestoreGpuFanAuto(gpu); lock (_gate) _gpuManualEngaged = false; } } catch { }
        // Wireless fans: failsafe means FULL BLAST (there is no BIOS curve to
        // fall back to); a plain restore-all returns them to the 40% baseline.
        // App exit (keepConfig) leaves their latched duty untouched.
        try
        {
            if (!keepConfig && Lian is { } lw)
            {
                int duty = reason.Contains("failsafe") ? 100 : 40;
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
            int? manualBefore;
            FanCurve? curveBefore;
            lock (_gate)
            {
                manualBefore = _manualFans.TryGetValue(fanIndex, out var p) ? p : null;
                curveBefore = _fanCurves.TryGetValue(fanIndex, out var cc) ? cc : null;
            }
            if (!ApplyDuty(fanIndex, 100)) return;
            string idName = fanIndex == GpuFanIndex ? "GPU"
                : IsLian(fanIndex) ? $"Lian Li slot {fanIndex - LianFanBase + 1}"
                : Lhm?.Fans[fanIndex].Name ?? $"#{fanIndex}";
            Log.Info("fans", $"identify: fan '{idName}' at 100% for 4s");
            await Task.Delay(4000);
            if (manualBefore is int m) ApplyDuty(fanIndex, m);
            else if (curveBefore is FanCurve c && TempFor(c.Source) is double t) ApplyDuty(fanIndex, Math.Max(FloorFor(fanIndex), c.DutyAt(t)));
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
        catch { }
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
                lock (_gate) _fanCurves[i] = e.Curve;
                var t = TempFor(e.Curve.Source);
                if (t is double temp) ApplyDuty(i, Math.Max(FloorFor(i), e.Curve.DutyAt(temp)));
                Log.Info("fans", $"restored '{name}' -> curve {e.Curve.Preset}");
            }
            else
            {
                int pct = Math.Clamp(e.Pct, FloorFor(i), 100);
                lock (_gate) _manualFans[i] = pct;
                ApplyDuty(i, pct);
                Log.Info("fans", $"restored '{name}' -> manual {pct}%");
            }
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

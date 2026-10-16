using LibreHardwareMonitor.Hardware;

namespace UnifiedRgb.Core.Sensors;

/*-----------------------------------------------------------*\
| Motherboard fan reading + control via LibreHardwareMonitor.  |
| Scoped HARD to the motherboard: CPU/GPU/RAM/storage/network/ |
| controller subsystems stay disabled so none of LHM's other   |
| dependencies (RAM SPD, disk SMART, HID) ever initialize, and |
| it never fights our native CPU/GPU sensors or our SMBus RGB. |
| LHM owns the Super-I/O chip end to end here — reading the    |
| tachs AND performing the vendor-specific control takeover    |
| (the part that was per-board reverse engineering by hand).   |
\*-----------------------------------------------------------*/
public sealed class LhmFans : IDisposable
{
    /// <summary>One controllable/observable fan: a tach sensor optionally
    /// paired with the control that drives that header.</summary>
    public sealed class Fan
    {
        public required string Name { get; init; }
        public required ISensor Rpm { get; init; }
        public IControl? Control { get; init; }
        public bool CanControl => Control != null;
        public int? CurrentRpm => Rpm.Value is float v and > 0 ? (int)v : (Rpm.Value == 0 ? 0 : null);
    }

    sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer c) => c.Traverse(this);
        public void VisitHardware(IHardware h) { h.Update(); foreach (var s in h.SubHardware) s.Accept(this); }
        public void VisitSensor(ISensor s) { }
        public void VisitParameter(IParameter p) { }
    }

    /// <summary>A motherboard temperature sensor (VRM, chipset, etc.).</summary>
    public sealed class Temp
    {
        public required string Name { get; init; }
        public required ISensor Sensor { get; init; }
        public double? Value => Sensor.Value is float v ? v : null;
    }

    // Null only for the harness's board (see the fan-list constructor).
    readonly Computer? _computer;
    readonly UpdateVisitor _visitor = new();
    // The machine-wide ISA bus mutex, held around every control write (see
    // WithIsaBus). Null where it cannot be opened; writes then go ahead
    // unguarded, as they always did.
    readonly Mutex? _isa;
    const int IsaWaitMs = 300;
    readonly List<Fan> _fans = new();
    readonly List<Temp> _temps = new();
    readonly List<Temp> _voltages = new();   // same shape: name + value

    public IReadOnlyList<Fan> Fans => _fans;
    public IReadOnlyList<Temp> Temps => _temps;
    public IReadOnlyList<Temp> Voltages => _voltages;

    LhmFans(Computer c)
    {
        _computer = c;
        _isa = IteSuperIo.OpenIsaMutex();
    }

    /// <summary>For the harness: a board made of these fans (fake controls)
    /// with no Computer, no driver and no ISA bus mutex behind it, so the fan
    /// hub's real paths can run against it without reaching a chip - or
    /// holding up HWiNFO or the running app on the machine-wide mutex.</summary>
    internal LhmFans(IEnumerable<Fan> fans)
    {
        _computer = null;
        _isa = null;
        _fans.AddRange(fans);
    }

    public static LhmFans? TryOpen()
    {
        Computer? c = null;
        LhmFans? f = null;
        try
        {
            c = new Computer
            {
                IsMotherboardEnabled = true,   // the ONLY subsystem we want
                IsCpuEnabled = false,
                IsGpuEnabled = false,
                IsMemoryEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
                IsPsuEnabled = false,
                IsBatteryEnabled = false,
            };
            c.Open();
            f = new LhmFans(c);
            f.Collect();
            if (f._fans.Count == 0 && f._temps.Count == 0)
            {
                Log.Info("lhm", "no fan/temp sensors found on the motherboard");
                c.Close();
                f._isa?.Dispose();
                return null;
            }
            Log.Info("lhm", $"motherboard: {f._fans.Count} fans "
                + $"({f._fans.Count(x => x.CanControl)} controllable), {f._temps.Count} temps");
            return f;
        }
        catch (Exception ex)
        {
            Log.Warn("lhm", $"open failed: {ex.Message}");
            // Collect() can throw after Open() succeeded (first Update sweep,
            // duplicate control index): close, or the ring0 driver session and
            // the ISA mutex stay held for the process lifetime while the ITE
            // fallback opens the same Super-I/O on top of them.
            try { c?.Close(); } catch { }
            f?._isa?.Dispose();
            return null;
        }
    }

    void Collect()
    {
        if (_computer == null) return;   // the harness's board: its fans came in through the constructor
        _computer.Accept(_visitor);
        foreach (var hw in _computer.Hardware)
            foreach (var sub in Flatten(hw))
            {
                // First control per index: a board exposing two controls with
                // one index used to throw out of ToDictionary and take the WHOLE
                // board (fans and temps) down to the read-only ITE fallback.
                var controls = new Dictionary<int, IControl>();
                foreach (var s in sub.Sensors.Where(s => s.Control != null))
                    if (!controls.TryAdd(s.Index, s.Control!))
                        Log.Warn("lhm", $"'{sub.Name}': two controls share index {s.Index} ('{s.Name}' ignored)");
                foreach (var s in sub.Sensors.Where(s => s.SensorType == SensorType.Fan))
                    _fans.Add(new Fan
                    {
                        Name = Unique(_fanNames, s.Name, sub.Name),
                        Rpm = s,
                        // Pair the fan tach with the control of the same index
                        // (LHM numbers a header's fan + control alike).
                        Control = controls.GetValueOrDefault(s.Index),
                    });
                foreach (var s in sub.Sensors.Where(s => s.SensorType == SensorType.Temperature))
                    _temps.Add(new Temp { Name = Unique(_tempNames, s.Name, sub.Name), Sensor = s });
                foreach (var s in sub.Sensors.Where(s => s.SensorType == SensorType.Voltage))
                    _voltages.Add(new Temp { Name = Unique(_voltNames, s.Name, sub.Name), Sensor = s });
            }
    }

    readonly HashSet<string> _fanNames = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _tempNames = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _voltNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keep sensor names unique across chips. The FIRST use of a name
    /// keeps it exactly: fan labels and fan curves are stored by name, so
    /// renaming an existing sensor would orphan the user's settings. Only the
    /// later collisions get qualified with their chip.</summary>
    static string Unique(HashSet<string> used, string name, string chip)
    {
        if (used.Add(name)) return name;
        string qualified = $"{name} ({ShortChip(chip)})";
        if (used.Add(qualified)) return qualified;
        for (int n = 2; ; n++)
        {
            string numbered = $"{qualified} {n}";
            if (used.Add(numbered)) return numbered;
        }
    }

    /// <summary>"ITE IT8792E" to "IT8792E": the part that tells two otherwise
    /// identical sensors apart, without the vendor noise.</summary>
    static string ShortChip(string chip)
    {
        if (string.IsNullOrWhiteSpace(chip)) return "2nd";
        int space = chip.LastIndexOf(' ');
        return space >= 0 && space < chip.Length - 1 ? chip[(space + 1)..] : chip;
    }

    static IEnumerable<IHardware> Flatten(IHardware h)
    {
        yield return h;
        foreach (var sub in h.SubHardware)
            foreach (var x in Flatten(sub))
                yield return x;
    }

    public void Refresh()
    {
        try { _computer?.Accept(_visitor); } catch { }
    }

    /// <summary>Run one control write while holding the machine-wide ISA bus
    /// mutex ourselves. LHM's IT87 write path waits only 10 ms for that mutex
    /// and, when another monitor (HWiNFO, a vendor tool) holds it, drops the
    /// write without a word, while SetSoftware/SetDefault return normally. A
    /// handback was then reported done with the header still on our duty, and
    /// the dirty-start marker deleted on that report. Windows mutexes are
    /// recursive per thread, so with the mutex held here LHM's own short wait
    /// on this thread succeeds at once and its write cannot be dropped.
    /// False = the bus was not ours within the timeout and nothing was written
    /// (the hub retries). A missing mutex runs the body unguarded, as before.
    /// Internal and static for the tests.</summary>
    internal static bool WithIsaBus(Mutex? isa, int timeoutMs, Func<bool> body)
    {
        bool held = IteSuperIo.TryAcquire(isa, timeoutMs);
        if (isa != null && !held) return false;
        try { return body(); }
        finally
        {
            if (held)
            {
                try { isa!.ReleaseMutex(); }
                catch (Exception ex) { Log.Occasional("lhm-isa-release", "lhm", $"ISA bus mutex release failed: {ex.Message}"); }
            }
        }
    }

    void NoteBusBusy(int index, string what)
        => Log.Occasional($"lhm-busy:{index}", "lhm",
            $"'{_fans[index].Name}': another monitor held the ISA bus for {IsaWaitMs} ms, {what} not written - retrying");

    /// <summary>Drive a fan to a fixed duty percent (0-100). LHM performs the
    /// board's control takeover under the hood.</summary>
    public bool SetDuty(int index, float percent)
    {
        if ((uint)index >= (uint)_fans.Count) return false;
        var ctl = _fans[index].Control;
        if (ctl == null) return false;
        try
        {
            float clamped = Math.Clamp(percent, 0, 100);
            bool landed = WithIsaBus(_isa, IsaWaitMs, () =>
            {
                // LHM writes the chip only from its own change events, and both
                // of Control's setters dedup: re-asserting the value it already
                // holds (the hub does so every ReassertTicks, for a driver reset
                // or a resume) reached nothing. The nudge is two events - the
                // same register byte twice, so no visible step - and the second
                // one is the real write.
                if (ctl.ControlMode == ControlMode.Software && ctl.SoftwareValue == clamped)
                    ctl.SetSoftware(clamped + 0.1f);
                ctl.SetSoftware(clamped);
                return true;
            });
            if (!landed) NoteBusBusy(index, "the duty was");
            return landed;
        }
        catch (Exception ex) { Log.Warn("lhm", $"set duty failed: {ex.Message}"); return false; }
    }

    /// <summary>Hand a fan back to the board's own (BIOS) control. False when
    /// the takeover release THREW: the header is then still on whatever duty we
    /// last wrote, and the caller must not record it as handed back (SensorHub
    /// keeps retrying such fans). A fan we never had control of - no control
    /// paired, or an index that no longer exists - has nothing to release and
    /// reports true.</summary>
    /// <param name="force">A retry of a handback that was reported done: LHM
    /// dedups SetDefault when it already believes the header is on default, so
    /// the retry re-takes and releases it to get a real register write.</param>
    public bool Restore(int index, bool force = false)
    {
        if ((uint)index >= (uint)_fans.Count) return true;
        try
        {
            var ctl = _fans[index].Control;
            if (ctl == null) return true;
            // Never taken by us: SetDefault on it writes nothing, so it needs no
            // bus. Waiting for the bus anyway turned a busy bus (another monitor
            // holding it) into a "board refused" handback for a header we never
            // touched - a false error, a retry, and a stale-marker warning.
            if (!force && ctl.ControlMode != ControlMode.Software) return true;
            bool done = WithIsaBus(_isa, IsaWaitMs, () =>
            {
                if (force && ctl.ControlMode == ControlMode.Default) ctl.SetSoftware(ctl.SoftwareValue);
                ctl.SetDefault();
                return true;
            });
            if (!done) NoteBusBusy(index, "the handback was");
            return done;
        }
        catch (Exception ex)
        {
            // Rate-limited: the hub retries a refused handback every tick, and
            // a header that keeps refusing would otherwise write this line
            // every 1.5 s for as long as the app runs.
            Log.Occasional($"lhm-restore:{index}", "fans",
                $"'{_fans[index].Name}' would not go back to auto: {ex.Message}");
            return false;
        }
    }

    /// <summary>Restore every fan; returns the ones whose release failed (empty
    /// = all back on the BIOS curve). Index for the caller's retry bookkeeping,
    /// name for its log line.</summary>
    public List<(int Index, string Name)> RestoreAll()
    {
        var failed = new List<(int Index, string Name)>();
        for (int i = 0; i < _fans.Count; i++)
            if (!Restore(i)) failed.Add((i, _fans[i].Name));
        return failed;
    }

    /// <summary>Set by Dispose: every header went back to the board (or none
    /// was ever ours). False after a close that left one on our duty, which
    /// stays there until a reboot - SensorHub keeps the dirty-start marker
    /// for it instead of deleting it.</summary>
    public bool ClosedClean { get; private set; }

    public void Dispose()
    {
        // Last chance before the driver goes: a header still under software
        // control after Close() keeps our duty until reboot, so say which.
        try
        {
            var failed = RestoreAll();
            ClosedClean = failed.Count == 0;
            if (failed.Count > 0)
                Log.Warn("lhm", "closing with fans still under software control: "
                    + string.Join(", ", failed.Select(f => $"'{f.Name}'")));
        }
        catch (Exception ex) { Log.Warn("lhm", $"handing the fans back on close failed: {ex.Message}"); }
        try { _computer?.Close(); } catch { }   // closing anyway; nothing left to hand back through it
        _isa?.Dispose();
    }
}

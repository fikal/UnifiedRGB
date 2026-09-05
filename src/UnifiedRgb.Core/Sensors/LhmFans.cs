using LibreHardwareMonitor.Hardware;

namespace UnifiedRgb.Core.Sensors;

/*-----------------------------------------------------------*\
| Motherboard fan reading + control via LibreHardwareMonitor.  |
| Scoped HARD to the motherboard: CPU/GPU/RAM/storage/network/ |
| controller subsystems stay disabled so none of LHM's other  |
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

    readonly Computer _computer;
    readonly UpdateVisitor _visitor = new();
    readonly List<Fan> _fans = new();
    readonly List<Temp> _temps = new();
    readonly List<Temp> _voltages = new();   // same shape: name + value

    public IReadOnlyList<Fan> Fans => _fans;
    public IReadOnlyList<Temp> Temps => _temps;
    public IReadOnlyList<Temp> Voltages => _voltages;

    LhmFans(Computer c) => _computer = c;

    public static LhmFans? TryOpen()
    {
        Computer? c = null;
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
            var f = new LhmFans(c);
            f.Collect();
            if (f._fans.Count == 0 && f._temps.Count == 0)
            {
                Log.Info("lhm", "no fan/temp sensors found on the motherboard");
                c.Close();
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
            return null;
        }
    }

    void Collect()
    {
        _computer.Accept(_visitor);
        foreach (var hw in _computer.Hardware)
            foreach (var sub in Flatten(hw))
            {
                var controls = sub.Sensors.Where(s => s.Control != null)
                    .ToDictionary(s => s.Index, s => s.Control!);
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
        try { _computer.Accept(_visitor); } catch { }
    }

    /// <summary>Drive a fan to a fixed duty percent (0-100). LHM performs the
    /// board's control takeover under the hood.</summary>
    public bool SetDuty(int index, float percent)
    {
        if ((uint)index >= (uint)_fans.Count) return false;
        var ctl = _fans[index].Control;
        if (ctl == null) return false;
        try { ctl.SetSoftware(Math.Clamp(percent, 0, 100)); return true; }
        catch (Exception ex) { Log.Warn("lhm", $"set duty failed: {ex.Message}"); return false; }
    }

    /// <summary>Hand a fan back to the board's own (BIOS) control.</summary>
    public void Restore(int index)
    {
        if ((uint)index >= (uint)_fans.Count) return;
        try { _fans[index].Control?.SetDefault(); } catch { }
    }

    public void RestoreAll()
    {
        for (int i = 0; i < _fans.Count; i++) Restore(i);
    }

    public void Dispose()
    {
        try { RestoreAll(); } catch { }
        try { _computer.Close(); } catch { }
    }
}

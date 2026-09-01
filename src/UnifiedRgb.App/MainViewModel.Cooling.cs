using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Net;

namespace UnifiedRgb.App;

// Cooling panel: sensors, fans, curves — split out of the 3,500-line MainViewModel (mechanical
// partial-class move, no behavior change).
public sealed partial class MainViewModel
{
    /*-----------------------------------------------------*    | Cooling panel: live temps + fan RPMs via SensorHub    |
    \*-----------------------------------------------------*/
    public bool IsCoolingSelected => _selectedLeft?.IsCooling == true;
    public bool ShowCoolingPanel => IsCoolingSelected && !_isSettingsOpen;

    public ObservableCollection<SensorRow> CoolingRows { get; } = new();
    DispatcherTimer? _coolingTimer;

    void StartCoolingRefresh()
    {
        _coolingTimer ??= CreateCoolingTimer();
        _coolingTimer.Start();                 // restart after leaving the panel
        RefreshCoolingRows();
    }

    DispatcherTimer CreateCoolingTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        t.Tick += (_, _) => { if (ShowCoolingPanel) RefreshCoolingRows(); else _coolingTimer?.Stop(); };
        return t;
    }

    // Friendlier defaults for this Gigabyte board's LHM sensors (generic
    // "Temperature #N"/"Fan #N" otherwise). Best-effort by position — every
    // row stays renamable, and Identify spins a fan so you can map it for sure.
    bool IsGigabyteBoard => Devices.OfType<GigabyteIt5711>().Any();
    static readonly string[] GbFanNames = { "CPU Fan", "System Fan 1", "System Fan 2", "System Fan 3", "CPU/Pump (OPT)", "System Fan 4" };

    string FanDefault(int i, string lhmName)
        => IsGigabyteBoard && i < GbFanNames.Length ? GbFanNames[i] : lhmName;

    public ObservableCollection<FanRowModel> CoolingFans { get; } = new();
    public ObservableCollection<SensorRow> CoolingGpuRows { get; } = new();
    public ObservableCollection<SensorRow> CoolingLianUniRows { get; } = new();
    string _fanSig = "";

    FanRowModel? _editingFan;
    public FanRowModel? EditingFan
    {
        get => _editingFan;
        set
        {
            if (ReferenceEquals(_editingFan, value)) return;
            if (_editingFan != null) _editingFan.IsEditing = false;
            _editingFan = value;
            if (_editingFan != null) _editingFan.IsEditing = true;
            OnChanged(nameof(EditingFan));
            OnChanged(nameof(HasEditingFan));
            EditingFanChanged?.Invoke(_editingFan);
        }
    }
    public bool HasEditingFan => _editingFan != null;

    /// <summary>Raised when the edited fan changes (code-behind reloads the graph).</summary>
    public event Action<FanRowModel?>? EditingFanChanged;
    /// <summary>Raised each cooling refresh (code-behind moves the live marker).</summary>
    public event Action? CoolingTick;

    public bool ShowFanFailsafe => UnifiedRgb.Core.Sensors.SensorHub.FailsafeTripped;

    /// <summary>Copy the edited fan's whole setup onto every other fan.</summary>
    public void ApplyToAllFans()
    {
        if (_editingFan == null) return;
        foreach (var f in CoolingFans)
            if (!ReferenceEquals(f, _editingFan)) _editingFan.CopyTo(f);
    }

    Action<string, string> RenameSaver(string def) => (k, v) =>
    {
        var d = _store.Settings.FanLabels ??= new();
        if (v == def) d.Remove(k); else d[k] = v;
        _store.SaveSettings();
    };

    public bool HasControllableFans => CoolingFans.Count > 0;
    public string CoolingEmptyHint => PawnIoMissing
        ? "Fan monitoring needs the PawnIO driver. Install it in Settings."
        : "No controllable fans found.";

    /*--- headline stats: temp gauge + load + voltage per CPU/GPU ---*/
    public double CpuGauge => UnifiedRgb.Core.Sensors.SensorHub.CpuTempC ?? double.NaN;
    public double GpuGauge => UnifiedRgb.Core.Sensors.SensorHub.GpuTempC ?? double.NaN;
    public string CpuLoadText => UnifiedRgb.Core.Sensors.SensorHub.CpuLoadPct is double l ? $"{l:0}%" : "--";
    public string GpuLoadText => UnifiedRgb.Core.Sensors.SensorHub.GpuLoadPct is int g ? $"{g}%" : "--";
    public string CpuVoltText => UnifiedRgb.Core.Sensors.SensorHub.CpuVoltage is double v ? $"{v:0.00} V" : "--";
    public string GpuVoltText => UnifiedRgb.Core.Sensors.SensorHub.GpuVoltage is double gv ? $"{gv:0.00} V" : "--";

    void RefreshCoolingRows()
    {
        UnifiedRgb.Core.Sensors.SensorHub.Touch();

        OnChanged(nameof(CpuGauge));
        OnChanged(nameof(GpuGauge));
        OnChanged(nameof(CpuLoadText));
        OnChanged(nameof(GpuLoadText));
        OnChanged(nameof(CpuVoltText));
        OnChanged(nameof(GpuVoltText));

        // Controllable fans (board + bundled GPU; RPM updated in place).
        UpdateFanModels();

        // GPU fans as a read-only row only when the driver offers no control.
        var gpuRows = new List<SensorRow>();
        if (!UnifiedRgb.Core.Sensors.SensorHub.GpuFansControllable)
            if (UnifiedRgb.Core.Sensors.SensorHub.GpuFanRpms is { Length: > 0 } gfans)
                gpuRows.Add(new SensorRow(gfans.Length == 1 ? "GPU fan" : $"GPU fans × {gfans.Length}", GpuRpmText(gfans)));
        SyncReadoutRows(CoolingGpuRows, gpuRows);

        // Wired SL-Infinity fans: read-only RPM per populated connector. Speed on
        // these is typically motherboard-controlled (the hub's fan cable feeds a
        // SYS_FAN header), so we surface the tach the hub reports but leave speed
        // to the board's fan rows.
        var uniRows = new List<SensorRow>();
        if (LianLiUniHub.Instance is { } uni)
            foreach (int g in uni.PopulatedChannels)
            {
                int rpm = uni.GroupRpm(g);
                string name = uni.PopulatedChannels.Count > 1 ? $"SL-Infinity fans (ch {g + 1})" : "SL-Infinity fans";
                uniRows.Add(new SensorRow(name, rpm > 0 ? $"{rpm:n0} RPM" : "stopped"));
            }
        SyncReadoutRows(CoolingLianUniRows, uniRows);

        // Lian Li wireless fans live in CoolingFans as full curve-capable
        // rows; just keep their telemetry alive while Cooling is visible.
        LianLiWireless.Instance?.TelemetryTouch();
        OnChanged(nameof(ShowLianHandoff));

        OnChanged(nameof(ShowFanFailsafe));
        CoolingTick?.Invoke();
    }

    static string GpuRpmText(int[] rpms)
    {
        var spinning = rpms.Where(r => r > 0).ToArray();
        if (spinning.Length == 0) return "stopped (fan-stop)";
        int avg = (int)Math.Round(spinning.Average());
        return rpms.Length > 1 ? $"{avg:n0} RPM × {rpms.Length}" : $"{avg:n0} RPM";
    }

    void UpdateFanModels()
    {
        var fans = UnifiedRgb.Core.Sensors.SensorHub.BoardFans;
        var desired = new List<int>();
        for (int i = 0; i < fans.Length; i++)
        {
            if (!fans[i].CanControl) continue;
            // Hide any header that isn't currently spinning and hasn't spun in the
            // last few seconds - empty headers (even with a stray curve on them)
            // drop off, while a real fan that just fan-stopped rides the debounce.
            if ((fans[i].Rpm is null or 0) && !UnifiedRgb.Core.Sensors.SensorHub.SpunRecently(i))
                continue;
            desired.Add(i);
        }
        bool gpuRow = UnifiedRgb.Core.Sensors.SensorHub.GpuFansControllable
                      && UnifiedRgb.Core.Sensors.SensorHub.GpuFanRpms is { Length: > 0 };
        var lianDev = LianLiWireless.Instance;
        int lianCount = lianDev?.FanCount ?? 0;
        string sig = string.Join(",", desired.Select(i => fans[i].Name)) + (gpuRow ? "+GPU" : "")
                   + (lianDev != null ? "+Lian:" + string.Join(",", Enumerable.Range(0, lianCount).Select(lianDev.FanNameAtSlot)) : "");
        if (sig != _fanSig)
        {
            _fanSig = sig;
            CoolingFans.Clear();
            foreach (int i in desired) CoolingFans.Add(BuildFanModel(i, fans[i].Name, fans[i].CanControl));
            if (gpuRow) CoolingFans.Add(BuildFanModel(UnifiedRgb.Core.Sensors.SensorHub.GpuFanIndex, "GPU", true));
            for (int s = 0; s < lianCount; s++)
                CoolingFans.Add(BuildFanModel(UnifiedRgb.Core.Sensors.SensorHub.LianFanBase + s,
                    $"Lian Li {lianDev!.FanNameAtSlot(s)}", true));
            EditingFan = CoolingFans.FirstOrDefault();
            OnChanged(nameof(HasControllableFans));
        }
        foreach (var m in CoolingFans)
        {
            if (m.Index >= UnifiedRgb.Core.Sensors.SensorHub.LianFanBase)
            {
                int slot = m.Index - UnifiedRgb.Core.Sensors.SensorHub.LianFanBase;
                var lr = LianLiWireless.Instance?.FanRpmsBySlot;
                m.Rpm = lr != null && slot < lr.Length
                    ? (lr[slot] > 0 ? $"{lr[slot]:n0} RPM" : "stopped") : "--";
            }
            else if (m.Index == UnifiedRgb.Core.Sensors.SensorHub.GpuFanIndex)
                m.Rpm = UnifiedRgb.Core.Sensors.SensorHub.GpuFanRpms is { Length: > 0 } g ? GpuRpmText(g) : "--";
            else
            {
                var f = m.Index < fans.Length ? fans[m.Index] : default;
                m.Rpm = f.Rpm is int rpm ? (rpm == 0 ? "stopped" : $"{rpm:n0} RPM") : "--";
            }
        }
    }

    FanRowModel BuildFanModel(int i, string rawName, bool canControl)
    {
        bool isGpu = i == UnifiedRgb.Core.Sensors.SensorHub.GpuFanIndex;
        string mode = "Auto"; int duty = 50;
        UnifiedRgb.Core.Sensors.FanCurve? curve = null;
        // GPU defaults to following its own temperature.
        var src = isGpu ? UnifiedRgb.Core.Sensors.TempSource.Gpu : UnifiedRgb.Core.Sensors.TempSource.Hottest;
        if (UnifiedRgb.Core.Sensors.SensorHub.ManualFanDuty(i) is int p) { mode = "Manual"; duty = p; }
        else if (UnifiedRgb.Core.Sensors.SensorHub.FanCurveOf(i) is UnifiedRgb.Core.Sensors.FanCurve c)
        { mode = c.MatchesPreset() ? c.Preset : "Custom"; curve = c; src = c.Source; }
        string key = $"fan:{rawName}";
        string def = isGpu ? "GPU fans" : FanDefault(i, rawName);
        return new FanRowModel(i, canControl, def, key,
            _store.Settings.FanLabels?.GetValueOrDefault(key), mode, duty, curve, src, RenameSaver(def),
            minDuty: UnifiedRgb.Core.Sensors.SensorHub.ManualFloorFor(i),
            curveFloor: UnifiedRgb.Core.Sensors.SensorHub.FloorFor(i));
    }

    /// <summary>Update read-only rows in place when the shape is unchanged (so
    /// an in-progress rename keeps its TextBox); rebuild only when rows change.</summary>
    static void SyncReadoutRows(ObservableCollection<SensorRow> target, List<SensorRow> desired)
    {
        static string IdOf(SensorRow r) => r.RenameKey ?? $"s:{r.IsSection}:{r.DefaultLabel}";
        bool sameShape = desired.Count == target.Count;
        if (sameShape)
            for (int i = 0; i < desired.Count; i++)
                if (IdOf(desired[i]) != IdOf(target[i])) { sameShape = false; break; }
        if (sameShape)
            for (int i = 0; i < desired.Count; i++)
                target[i].Value = desired[i].Value;
        else
        {
            target.Clear();
            foreach (var r in desired) target.Add(r);
        }
    }

    static LeftItem Header(string name) => new() { Name = name, Subtitle = "", IsHeader = true };

    void BuildLeftItems()
    {
        // Devices scroll in their own list; the SYSTEM section (Pump LCD,
        // Cooling) is pinned at the bottom of the card so it never needs
        // scrolling to reach, no matter how many devices there are.
        DeviceItems.Clear();
        foreach (var d in Devices)
            DeviceItems.Add(new LeftItem { Name = d.Name, Subtitle = $"{d.Type} • {d.LedCount} LEDs", Device = d });
        foreach (var e in _store.Settings.DisabledDevices ?? new())
            DeviceItems.Add(new LeftItem { Name = e.Name, Subtitle = "disabled", Device = null, IsDisabled = true });

        SystemItems.Clear();
        if (_lcd != null)
            SystemItems.Add(new LeftItem { Name = "Pump LCD", Subtitle = "240 × 320 display", Device = null });
        SystemItems.Add(new LeftItem { Name = "Cooling", Subtitle = "temps · fans · curves", Device = null, IsCooling = true });

        // Keep the current selection across a rebuild - a rebuild replaces every
        // LeftItem, so re-match the same row by name/kind instead of snapping to
        // the first device (changing the Lian fan count rescans and used to kick
        // the selection off the hub). Falls back to the first device on startup.
        var prev = _selectedLeft;
        SelectedLeftItem =
            (prev == null ? null
                : AllLeftItems.FirstOrDefault(i => i.Name == prev.Name
                    && i.IsCooling == prev.IsCooling && i.IsDisabled == prev.IsDisabled))
            ?? DeviceItems.FirstOrDefault() ?? SystemItems.FirstOrDefault();
    }
}

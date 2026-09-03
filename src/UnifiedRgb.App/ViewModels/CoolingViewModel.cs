using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Sensors;

namespace UnifiedRgb.App;

/// <summary>The Cooling pane's view model: live temps + fan RPMs via SensorHub,
/// the controllable-fan rows and the curve editor's selection. The CoolingPane
/// binds to this directly (its DataContext is the main view model's
/// <c>Cooling</c>); the main view model only owns navigation (which pane is on
/// screen) and hands in the few things this needs from it.</summary>
public sealed class CoolingViewModel : INotifyPropertyChanged
{
    readonly SettingsData _settings;
    readonly Action _saveSettings;
    readonly Func<bool> _isGigabyteBoard, _pawnIoMissing, _isOnScreen;
    DispatcherTimer? _timer;

    public CoolingViewModel(SettingsData settings, Action saveSettings,
        Func<bool> isGigabyteBoard, Func<bool> pawnIoMissing, Func<bool> isOnScreen)
    {
        _settings = settings; _saveSettings = saveSettings;
        _isGigabyteBoard = isGigabyteBoard; _pawnIoMissing = pawnIoMissing; _isOnScreen = isOnScreen;
    }

    /// <summary>Start the 1.5 s refresh (pane shown). It self-stops when the
    /// pane leaves the screen or the window hides to the tray, so it never
    /// runs for the process lifetime.</summary>
    public void Start()
    {
        HookWindowVisibility();
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _timer.Tick += (_, _) =>
            {
                HookWindowVisibility();   // Start() may have run before the window existed
                if (_isOnScreen() && MainWindowState.Visible) Refresh(); else _timer.Stop();
            };
        }
        _timer.Start();
        Refresh();
    }

    /// <summary>Stop the refresh (pane left, window hidden to the tray). A
    /// slider value still waiting on its debounce LANDS here rather than being
    /// dropped: a nav click or minimize within 120 ms of the last slider step
    /// used to leave the row saying e.g. "Manual - 55%" while the fan and
    /// fan-config.json stayed at the earlier value.</summary>
    public void Stop()
    {
        _timer?.Stop();
        foreach (var f in CoolingFans) f.FlushPendingDuty();
    }

    /// <summary>Exit only: drop pending slider values instead of flushing them.
    /// Called before RestoreAllFans hands the fans back to the BIOS, so no late
    /// manual write can follow the handback.</summary>
    public void DiscardPendingDuties()
    {
        foreach (var f in CoolingFans) f.CancelPendingDuty();
    }

    // The nav selection survives minimize-to-tray (the window hides, Cooling
    // stays selected), so the tick's on-screen check alone kept SensorHub's
    // UI-only sweep - NvAPI, the LHM board scan, the UniHub poll, wireless
    // telemetry - armed 24/7 in the tray. Follow the window: stop when it
    // hides, restart (with a fresh Refresh) when it returns with Cooling up.
    Window? _hookedWindow;
    void HookWindowVisibility()
    {
        if (_hookedWindow != null) return;
        var w = Application.Current?.MainWindow;
        if (w == null) return;   // Start() before the window exists (startup): hooked on the next Start
        _hookedWindow = w;
        w.IsVisibleChanged += (_, _) =>
        {
            if (!w.IsVisible) Stop();
            else if (_isOnScreen()) Start();
        };
    }

    // Friendlier defaults for this Gigabyte board's LHM sensors (generic
    // "Temperature #N"/"Fan #N" otherwise). Best-effort by position — every
    // row stays renamable, and Identify spins a fan so you can map it for sure.
    static readonly string[] GbFanNames = { "CPU Fan", "System Fan 1", "System Fan 2", "System Fan 3", "CPU/Pump (OPT)", "System Fan 4" };

    string FanDefault(int i, string lhmName)
        => _isGigabyteBoard() && i < GbFanNames.Length ? GbFanNames[i] : lhmName;

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

    public bool ShowFanFailsafe => SensorHub.FailsafeTripped;

    /// <summary>Copy the edited fan's whole setup onto every other fan.</summary>
    public void ApplyToAllFans()
    {
        if (_editingFan == null) return;
        foreach (var f in CoolingFans)
            if (!ReferenceEquals(f, _editingFan)) _editingFan.CopyTo(f);
    }

    Action<string, string> RenameSaver(string def) => (k, v) =>
    {
        var d = _settings.FanLabels ??= new();
        if (v == def) d.Remove(k); else d[k] = v;
        _saveSettings();
    };

    public bool HasControllableFans => CoolingFans.Count > 0;
    public string CoolingEmptyHint => _pawnIoMissing()
        ? "Fan monitoring needs the PawnIO driver. Install it in Settings."
        : "No controllable fans found.";

    /// <summary>PawnIO got installed: the empty-list hint changes wording.</summary>
    public void NotifyPawnIoChanged() => OnChanged(nameof(CoolingEmptyHint));

    /*--- exit handoff: wireless fans follow their SYS-fan sync wire while
          the app is away, so a hardware curve stays in charge ---*/
    public bool ShowLianHandoff => LianLiWireless.Instance != null;
    public bool LianHandoffOnExit
    {
        get => _settings.LianHandoffOnExit;
        set
        {
            if (_settings.LianHandoffOnExit == value) return;
            _settings.LianHandoffOnExit = value;
            _saveSettings();
            OnChanged();
        }
    }

    /*--- headline stats: temp gauge + load + voltage per CPU/GPU ---*/
    public double CpuGauge => SensorHub.CpuTempC ?? double.NaN;
    public double GpuGauge => SensorHub.GpuTempC ?? double.NaN;
    public string CpuLoadText => SensorHub.CpuLoadPct is double l ? $"{l:0}%" : "--";
    public string GpuLoadText => SensorHub.GpuLoadPct is int g ? $"{g}%" : "--";
    public string CpuVoltText => SensorHub.CpuVoltage is double v ? $"{v:0.00} V" : "--";
    public string GpuVoltText => SensorHub.GpuVoltage is double gv ? $"{gv:0.00} V" : "--";

    /// <summary>One refresh, guarded like AutomationService.Tick: a per-tick
    /// fault (a device that throws mid-unplug, an NvAPI hiccup) is logged
    /// rate-limited instead of reaching the app-level handler's dialog every
    /// 1.5 s for as long as Cooling is on screen.</summary>
    void Refresh()
    {
        try { RefreshCore(); }
        catch (Exception ex)
        {
            UnifiedRgb.Core.Log.Occasional("cooling-tick", "cooling", () => $"refresh failed: {ex.Message}");
        }
    }

    void RefreshCore()
    {
        SensorHub.Touch();

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
        if (!SensorHub.GpuFansControllable)
            if (SensorHub.GpuFanRpms is { Length: > 0 } gfans)
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
        var fans = SensorHub.BoardFans;
        var desired = new List<int>();
        for (int i = 0; i < fans.Length; i++)
        {
            if (!fans[i].CanControl) continue;
            // Hide any header that isn't currently spinning and hasn't spun in the
            // last few seconds - empty headers (even with a stray curve on them)
            // drop off, while a real fan that just fan-stopped rides the debounce.
            if ((fans[i].Rpm is null or 0) && !SensorHub.SpunRecently(i))
                continue;
            desired.Add(i);
        }
        bool gpuRow = SensorHub.GpuFansControllable && SensorHub.GpuFanRpms is { Length: > 0 };
        var lianDev = LianLiWireless.Instance;
        int lianCount = lianDev?.FanCount ?? 0;
        string sig = string.Join(",", desired.Select(i => fans[i].Name)) + (gpuRow ? "+GPU" : "")
                   + (lianDev != null ? "+Lian:" + string.Join(",", Enumerable.Range(0, lianCount).Select(lianDev.FanNameAtSlot)) : "");
        if (sig != _fanSig)
        {
            _fanSig = sig;
            // Keep the edited fan across the rebuild: a fan-stopped header
            // dropping off (or returning) used to yank the curve editor to row 1
            // mid-edit.
            int? keep = _editingFan?.Index;
            CoolingFans.Clear();
            foreach (int i in desired) CoolingFans.Add(BuildFanModel(i, fans[i].Name, fans[i].CanControl));
            if (gpuRow) CoolingFans.Add(BuildFanModel(SensorHub.GpuFanIndex, "GPU", true));
            for (int s = 0; s < lianCount; s++)
                CoolingFans.Add(BuildFanModel(SensorHub.LianFanBase + s, $"Lian Li {lianDev!.FanNameAtSlot(s)}", true));
            EditingFan = CoolingFans.FirstOrDefault(m => m.Index == keep) ?? CoolingFans.FirstOrDefault();
            OnChanged(nameof(HasControllableFans));
        }
        foreach (var m in CoolingFans)
        {
            if (m.Index >= SensorHub.LianFanBase)
            {
                int slot = m.Index - SensorHub.LianFanBase;
                var lr = LianLiWireless.Instance?.FanRpmsBySlot;
                m.Rpm = lr != null && slot < lr.Length
                    ? (lr[slot] > 0 ? $"{lr[slot]:n0} RPM" : "stopped") : "--";
            }
            else if (m.Index == SensorHub.GpuFanIndex)
                m.Rpm = SensorHub.GpuFanRpms is { Length: > 0 } g ? GpuRpmText(g) : "--";
            else
            {
                // BoardFans can shrink between rebuilds (LHM re-enumeration): a
                // stale index must read "--", not dereference a null record.
                var f = m.Index < fans.Length ? fans[m.Index] : null;
                m.Rpm = f?.Rpm is int rpm ? (rpm == 0 ? "stopped" : $"{rpm:n0} RPM") : "--";
            }
        }
    }

    FanRowModel BuildFanModel(int i, string rawName, bool canControl)
    {
        bool isGpu = i == SensorHub.GpuFanIndex;
        string mode = "Auto"; int duty = 50;
        FanCurve? curve = null;
        // GPU defaults to following its own temperature.
        var src = isGpu ? TempSource.Gpu : TempSource.Hottest;
        if (SensorHub.ManualFanDuty(i) is int p) { mode = "Manual"; duty = p; }
        // The row gets its OWN copy: FanCurveOf returns the hub's live instance,
        // which the sensor timer thread reads (DutyAt) while the curve editor
        // would otherwise Insert/RemoveAt its Points on the UI thread.
        else if (SensorHub.FanCurveOf(i) is FanCurve c)
        { mode = c.MatchesPreset() ? c.Preset : "Custom"; curve = c.Clone(); src = c.Source; }
        string key = $"fan:{rawName}";
        string def = isGpu ? "GPU fans" : FanDefault(i, rawName);
        return new FanRowModel(i, canControl, def, key,
            _settings.FanLabels?.GetValueOrDefault(key), mode, duty, curve, src, RenameSaver(def),
            minDuty: SensorHub.ManualFloorFor(i),
            curveFloor: SensorHub.FloorFor(i));
    }

    /// <summary>Update read-only rows in place when the shape is unchanged (so
    /// an in-progress rename keeps its TextBox); rebuild only when rows change.</summary>
    static void SyncReadoutRows(ObservableCollection<SensorRow> target, List<SensorRow> desired)
    {
        static string IdOf(SensorRow r) => $"s:{r.IsSection}:{r.Label}";
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

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

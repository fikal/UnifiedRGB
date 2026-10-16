using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
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
    // A getter, not the object: ProfileStore.Reload() (a setup import) replaces
    // the SettingsData instance, and a captured reference kept writing fan
    // labels and the Lian handoff toggle into an orphan nobody saved.
    readonly Func<SettingsData> _settings;
    readonly Action _saveSettings;
    readonly Func<bool> _isGigabyteBoard, _pawnIoMissing, _isOnScreen;
    DispatcherTimer? _timer;

    public CoolingViewModel(Func<SettingsData> settings, Action saveSettings,
        Func<bool> isGigabyteBoard, Func<bool> pawnIoMissing, Func<bool> isOnScreen)
    {
        _settings = settings; _saveSettings = saveSettings;
        _isGigabyteBoard = isGigabyteBoard; _pawnIoMissing = pawnIoMissing; _isOnScreen = isOnScreen;
        // Was a one-line Click handler; the XAML can say it directly.
        ApplyToAllFansCommand = new RelayCommand(_ => ApplyToAllFans());
    }

    public ICommand ApplyToAllFansCommand { get; }

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
        GpuPower.Release();
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
    /// <summary>The GPU power-connector card (WireView Pro II), hidden without one.</summary>
    public GpuPowerCardModel GpuPower { get; } = new();
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

    /// <summary>Fan control is on but the thermal failsafe has no reading for
    /// the CPU (or a GPU it could read before): it would never notice that
    /// part overheating.</summary>
    public bool ShowFailsafeBlind => SensorHub.FailsafeBlindSpot != null;
    public string FailsafeBlindText => SensorHub.FailsafeBlindSpot is string part
        ? $"The overheat failsafe can't read the {part} temperature, so it can't step in if the {part} gets too hot while UnifiedRGB controls your fans."
        : "";

    /// <summary>Copy the edited fan's whole setup onto every other fan.</summary>
    public void ApplyToAllFans()
    {
        if (_editingFan == null) return;
        foreach (var f in CoolingFans)
            if (!ReferenceEquals(f, _editingFan)) _editingFan.CopyTo(f);
    }

    Action<string, string> RenameSaver(string def) => (k, v) =>
    {
        var d = _settings().FanLabels ??= new();
        if (v == def) d.Remove(k); else d[k] = v;
        _saveSettings();
    };

    public bool HasControllableFans => CoolingFans.Count > 0;
    public string CoolingEmptyHint => _pawnIoMissing()
        ? "Fan monitoring needs the PawnIO driver. Install it in Settings."
        : "No controllable fans found.";

    /// <summary>PawnIO got installed: the empty-list hint changes wording.</summary>
    public void NotifyPawnIoChanged() => OnChanged(nameof(CoolingEmptyHint));

    /// <summary>The settings object was replaced (a setup import): rebuild the
    /// fan rows on the next refresh, which re-reads their names, and re-read
    /// the hand-off switch.</summary>
    public void SettingsReplaced()
    {
        _fanSig = "";
        OnChanged(nameof(LianHandoffOnExit));
    }

    /*--- exit handoff: wireless fans follow their SYS-fan sync wire while
          the app is away, so a hardware curve stays in charge ---*/
    public bool ShowLianHandoff => LianLiWireless.Instance != null;
    public bool LianHandoffOnExit
    {
        get => _settings().LianHandoffOnExit;
        set
        {
            if (_settings().LianHandoffOnExit == value) return;
            _settings().LianHandoffOnExit = value;
            _saveSettings();
            OnChanged();
        }
    }

    // The crash handler (App.xaml.cs) has no view model to ask and may run on
    // any thread, so it reads the hand-off choice through this. It only ever
    // honoured it on a clean exit: after a crash the wireless fans sat at
    // their last duty with no BIOS to fall back on. The settings GETTER, not a
    // copied bool, so an import that replaces the settings object cannot
    // leave it stale.
    static Func<SettingsData>? s_crashSettings;

    /// <summary>Point the crash path at the app's settings. Called once by the
    /// app's own view model - an explicit hand-over rather than a side effect
    /// of constructing this class, which every harness-built view model also
    /// does. Null forgets it.</summary>
    internal static void UseForCrashPath(Func<SettingsData>? settings) => Volatile.Write(ref s_crashSettings, settings);

    /// <summary>"Hand the wireless fans to the board on exit", for the crash
    /// path. False when no Cooling view model was ever built.</summary>
    internal static bool HandOffWirelessOnCrash
    {
        get
        {
            var settings = Volatile.Read(ref s_crashSettings);
            try { return settings?.Invoke().LianHandoffOnExit == true; }
            catch { return false; }   // a crash path must not throw; without the setting the fans go to 100% instead
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

        GpuPower.Refresh();

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
        OnChanged(nameof(ShowFailsafeBlind));
        OnChanged(nameof(FailsafeBlindText));
        CoolingTick?.Invoke();
    }

    static string GpuRpmText(int[] rpms)
    {
        var spinning = rpms.Where(r => r > 0).ToArray();
        if (spinning.Length == 0) return "stopped (fan-stop)";
        int avg = (int)Math.Round(spinning.Average());
        return rpms.Length > 1 ? $"{avg:n0} RPM × {rpms.Length}" : $"{avg:n0} RPM";
    }

    /// <summary>Internal for the tests (Refresh would also touch the
    /// WireView's serial port).</summary>
    internal void UpdateFanModels()
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
        // The hub's mode epoch too: the rows used to rebuild only when fan
        // NAMES changed, so after the failsafe they went on showing curves
        // that were no longer running (and a config reload never showed).
        string sig = string.Join(",", desired.Select(i => fans[i].Name)) + (gpuRow ? "+GPU" : "")
                   + (lianDev != null ? "+Lian:" + string.Join(",", Enumerable.Range(0, lianCount).Select(lianDev.FanNameAtSlot)) : "")
                   + "#" + SensorHub.ModeEpoch;
        if (sig != _fanSig)
        {
            _fanSig = sig;
            // Keep the edited fan across the rebuild: a fan-stopped header
            // dropping off (or returning) used to yank the curve editor to row 1
            // mid-edit.
            int? keep = _editingFan?.Index;
            // The old rows go with their debounce timers stopped: a slider
            // value still pending lands now (unless the hub has taken the fan
            // off Manual meanwhile - the failsafe, an import), so the rows
            // built below show it, instead of an orphaned timer writing it
            // 120 ms after the rebuild. DiscardPendingDuties only ever saw the
            // current rows.
            foreach (var old in CoolingFans) old.Retire();
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
        // The same read a row re-does when the hub refuses it (FanRowModel.SyncFromHub).
        var s = FanRowModel.ReadHub(i);
        string key = $"fan:{rawName}";
        string def = isGpu ? "GPU fans" : FanDefault(i, rawName);
        return new FanRowModel(i, canControl, def, key,
            _settings().FanLabels?.GetValueOrDefault(key), s.Mode, s.Duty, s.Curve, s.Source, RenameSaver(def),
            minDuty: SensorHub.ManualFloorFor(i),
            curveFloor: SensorHub.FloorFor(i),
            suspended: s.Suspended);
    }

    /// <summary>Update read-only rows in place when the shape is unchanged (so
    /// an in-progress rename keeps its TextBox); rebuild only when rows change.</summary>
    internal static void SyncReadoutRows(ObservableCollection<SensorRow> target, List<SensorRow> desired)
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

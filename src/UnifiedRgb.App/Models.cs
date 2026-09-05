using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.App;

/// <summary>An entry in the effect dropdown. Effect == null means static
/// (manual color) mode.</summary>
public sealed class EffectChoice
{
    public required string Name { get; init; }
    public IEffect? Effect { get; init; }
    public string Category { get; init; } = "Basics";
    public override string ToString() => Name;
}

/// <summary>One row of the All-effects browser: an effect plus its live
/// favorite state (starring adds it to the pills).</summary>
public sealed class EffectRowVM : System.ComponentModel.INotifyPropertyChanged
{
    public required EffectChoice Choice { get; init; }
    public string Name => Choice.Name;
    bool _fav;
    public bool IsFavorite
    {
        get => _fav;
        // StarBrush too: the glyph flipped but its color stayed (the rows are
        // cached per menu, so no fresh instance ever repainted it either).
        set { _fav = value; Notify(nameof(IsFavorite)); Notify(nameof(Star)); Notify(nameof(StarBrush)); }
    }
    /// <summary>Custom Pattern is always a pill - no star to manage.</summary>
    public bool CanStar => Choice.Name != "Custom Pattern";
    public string Star => _fav ? "\u2605" : "\u2606";
    public System.Windows.Media.Brush StarBrush => _fav ? GoldBrush : GrayBrush;
    // Shared by every row and the title star: frozen, so no change tracking.
    public static readonly System.Windows.Media.Brush GoldBrush = FrozenBrush(0xFF, 0xC9, 0x4C);
    public static readonly System.Windows.Media.Brush GrayBrush = FrozenBrush(0x6A, 0x70, 0x80);
    static System.Windows.Media.Brush FrozenBrush(byte r, byte g, byte b)
    {
        var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new(n));
}

public sealed record EffectCategoryVM(string Name, System.Collections.Generic.List<EffectRowVM> Items);

/// <summary>A row in the left device list: an RGB device, or the pump LCD
/// (Device == null) which opens the display designer instead of lighting.</summary>
public sealed class LeftItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new(n));

    public required string Name { get; init; }

    // Settable, not init: a wireless device's charge lands in here every minute
    // and rebuilding the whole list for it would take the selection with it.
    string _subtitle = "";
    public required string Subtitle
    {
        get => _subtitle;
        set { if (_subtitle == value) return; _subtitle = value; Notify(nameof(Subtitle)); }
    }

    bool _lowBattery;
    /// <summary>Charge at or below BatteryMonitor.LowPercent and off the
    /// charger: the subtitle turns amber.</summary>
    public bool LowBattery
    {
        get => _lowBattery;
        set { if (_lowBattery == value) return; _lowBattery = value; Notify(nameof(LowBattery)); }
    }
    public IRgbDevice? Device { get; init; }
    public bool IsDisabled { get; init; }
    public bool IsCooling { get; init; }
    public bool IsLcd => Device == null && !IsDisabled && !IsCooling;
    public override string ToString() => Name;
}

/// <summary>A controllable motherboard fan in the Cooling panel: a mode
/// (Auto / a curve preset / Custom / Manual), an editable curve, and a temp
/// source. Changing any of these drives the fan live through the hub; the
/// choice persists per fan. One shared curve editor targets whichever row
/// IsEditing.</summary>
public sealed class FanRowModel : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new(n));

    public int Index { get; }
    public bool CanControl { get; }
    public string RenameKey { get; }
    public string DefaultName { get; }
    readonly Action<string, string> _persistName;
    bool _applying;   // suppress re-entrancy while we push a mode

    public static string[] AllModes { get; } =
        { "Auto", "Quiet", "Standard", "High", "Full", "Custom", "Manual" };
    public static string[] Sources { get; } = { "Hottest", "CPU", "GPU" };

    /// <summary>Manual-slider floor (the GPU's vBIOS minimum; 30 for board fans).</summary>
    public int MinDuty { get; }
    /// <summary>Curve floor — GPU curves may reach 0 (below the manual
    /// minimum the driver takes over, incl. zero-RPM where the card allows).</summary>
    public int CurveFloor { get; }

    public FanRowModel(int index, bool canControl, string defaultName, string renameKey,
        string? savedName, string mode, int duty, UnifiedRgb.Core.Sensors.FanCurve? curve,
        UnifiedRgb.Core.Sensors.TempSource source, Action<string, string> persistName,
        int minDuty = 30, int curveFloor = 30)
    {
        Index = index; CanControl = canControl; DefaultName = defaultName;
        RenameKey = renameKey; _persistName = persistName;
        MinDuty = minDuty; CurveFloor = curveFloor;
        _label = savedName ?? defaultName;
        _mode = mode; _duty = Math.Max(duty, minDuty); _source = source;
        _curve = curve ?? UnifiedRgb.Core.Sensors.FanCurve.Preset_(
            mode is "Auto" or "Manual" or "Custom" ? "Standard" : mode, source, curveFloor);
    }

    string _label;
    public string Label
    {
        get => _label;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? DefaultName : value.Trim();
            if (v != _label) { _label = v; _persistName(RenameKey, v); }
            Notify(nameof(Label));
        }
    }

    string _rpm = "—";
    public string Rpm { get => _rpm; set { if (_rpm != value) { _rpm = value; Notify(nameof(Rpm)); } } }

    string _mode;
    public string Mode
    {
        get => _mode;
        set
        {
            if (_mode == value || value == null) return;
            _mode = value;
            Notify(nameof(Mode)); Notify(nameof(IsManual)); Notify(nameof(IsCurve));
            Notify(nameof(IsAuto)); Notify(nameof(ModeSummary));
            if (!_applying) ApplyMode();
        }
    }

    int _duty = 50;
    public int DutyPercent
    {
        get => _duty;
        set
        {
            int v = Math.Clamp(value, MinDuty, 100);
            if (_duty == v) { if (v != value) Notify(nameof(DutyPercent)); return; }
            _duty = v; Notify(nameof(DutyPercent)); Notify(nameof(ModeSummary));
            if (!_applying && _mode == "Manual") ScheduleDutyApply();
        }
    }

    // The slider binds with UpdateSourceTrigger=PropertyChanged, so a drag
    // delivers a value per mouse-move - and each one used to be a synchronous
    // hardware transaction plus a fan-config.json rewrite (temp file +
    // File.Replace) on the UI thread, ~70 of each per drag. Coalesce: the
    // latest value lands once the stream pauses for a moment.
    DispatcherTimer? _dutyApply;
    void ScheduleDutyApply()
    {
        if (_dutyApply == null)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (_mode == "Manual") UnifiedRgb.Core.Sensors.SensorHub.SetFanDuty(Index, _duty);
            };
            _dutyApply = t;
        }
        _dutyApply.Stop(); _dutyApply.Start();
    }

    /// <summary>Drop a slider value still waiting to be applied. Exit only:
    /// the fans are about to be handed back to the BIOS and a late write
    /// would undo that.</summary>
    public void CancelPendingDuty() => _dutyApply?.Stop();

    /// <summary>Apply a slider value still waiting on the debounce right now.
    /// Pane leave / window hide: the value must land, not vanish, or the row
    /// shows a duty the fan and fan-config.json never received.</summary>
    public void FlushPendingDuty()
    {
        if (_dutyApply?.IsEnabled != true) return;
        _dutyApply.Stop();
        if (_mode == "Manual") UnifiedRgb.Core.Sensors.SensorHub.SetFanDuty(Index, _duty);
    }

    UnifiedRgb.Core.Sensors.TempSource _source;
    public string SourceName
    {
        get => _source switch { UnifiedRgb.Core.Sensors.TempSource.Cpu => "CPU", UnifiedRgb.Core.Sensors.TempSource.Gpu => "GPU", _ => "Hottest" };
        set
        {
            var s = value switch { "CPU" => UnifiedRgb.Core.Sensors.TempSource.Cpu, "GPU" => UnifiedRgb.Core.Sensors.TempSource.Gpu, _ => UnifiedRgb.Core.Sensors.TempSource.Hottest };
            if (s == _source) return;
            _source = s; _curve.Source = s; Notify(nameof(SourceName)); Notify(nameof(ModeSummary));
            if (!_applying && IsCurve) UnifiedRgb.Core.Sensors.SensorHub.SetFanCurve(Index, _curve);
        }
    }

    /// <summary>One-line state for the compact fan row, e.g. "Quiet · CPU",
    /// "Manual · 50%", "Auto".</summary>
    public string ModeSummary => _mode switch
    {
        "Auto" => "Auto",
        "Manual" => $"Manual · {_duty}%",
        _ => $"{_mode} · {SourceName}",
    };

    UnifiedRgb.Core.Sensors.FanCurve _curve;
    public UnifiedRgb.Core.Sensors.FanCurve Curve => _curve;

    bool _editing;
    public bool IsEditing { get => _editing; set { if (_editing != value) { _editing = value; Notify(nameof(IsEditing)); } } }

    public bool IsManual => _mode == "Manual";
    public bool IsCurve => _mode is not ("Auto" or "Manual");
    public bool IsAuto => _mode == "Auto";

    /// <summary>Push the current mode to the hardware.</summary>
    public void ApplyMode()
    {
        switch (_mode)
        {
            case "Auto": UnifiedRgb.Core.Sensors.SensorHub.RestoreFan(Index); break;
            case "Manual": UnifiedRgb.Core.Sensors.SensorHub.SetFanDuty(Index, _duty); break;
            case "Custom": _curve.Preset = "Custom"; UnifiedRgb.Core.Sensors.SensorHub.SetFanCurve(Index, _curve); break;
            default:   // a named preset
                _curve = UnifiedRgb.Core.Sensors.FanCurve.Preset_(_mode, _source, CurveFloor);
                Notify(nameof(Curve));
                UnifiedRgb.Core.Sensors.SensorHub.SetFanCurve(Index, _curve);
                break;
        }
    }

    public void Identify() => _ = UnifiedRgb.Core.Sensors.SensorHub.IdentifyFan(Index);

    /// <summary>The editor edited our curve: switch to Custom and re-apply.</summary>
    public void OnCurveEdited()
    {
        _applying = true;
        Mode = "Custom";
        _applying = false;
        UnifiedRgb.Core.Sensors.SensorHub.SetFanCurve(Index, _curve);
    }

    /// <summary>Copy this fan's mode/curve/source onto another row + its fan.</summary>
    public void CopyTo(FanRowModel other)
    {
        other._applying = true;
        other._source = _source;
        other._curve = _curve.Clone();
        other.Mode = _mode;
        other.DutyPercent = _duty;
        other.Notify(nameof(SourceName));
        other.Notify(nameof(Curve));
        other._applying = false;
        other.ApplyMode();
    }
}

/// <summary>One read-only row in the Cooling panel (GPU/Uni-hub readouts);
/// IsSection renders as a group label. Value mutates in place each refresh.
/// (The rename/manual-control surface this class once carried was never
/// constructed or bound - fan rows are FanRowModel.)</summary>
public sealed class SensorRow : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new(n));

    public SensorRow(string label, string value, bool isSection = false)
    {
        _label = label; _value = value; IsSection = isSection;
    }

    public bool IsSection { get; }

    string _label;
    public string Label
    {
        get => _label;
        set { if (_label == value) return; _label = value; Notify(nameof(Label)); }
    }

    string _value;
    public string Value
    {
        get => _value;
        set { if (_value == value) return; _value = value; Notify(nameof(Value)); }
    }
}

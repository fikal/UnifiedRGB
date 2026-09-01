using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
        set { _fav = value; Notify(nameof(IsFavorite)); Notify(nameof(Star)); }
    }
    /// <summary>Custom Pattern is always a pill - no star to manage.</summary>
    public bool CanStar => Choice.Name != "Custom Pattern";
    public string Star => _fav ? "\u2605" : "\u2606";
    public System.Windows.Media.Brush StarBrush => _fav ? GoldBrush : GrayBrush;
    public static readonly System.Windows.Media.Brush GoldBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC9, 0x4C));
    public static readonly System.Windows.Media.Brush GrayBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6A, 0x70, 0x80));
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new(n));
}

public sealed record EffectCategoryVM(string Name, System.Collections.Generic.List<EffectRowVM> Items);

/// <summary>A row in the left device list: an RGB device, or the pump LCD
/// (Device == null) which opens the display designer instead of lighting.</summary>
public sealed class LeftItem
{
    public required string Name { get; init; }
    public required string Subtitle { get; init; }
    public IRgbDevice? Device { get; init; }
    public bool IsDisabled { get; init; }
    public bool IsCooling { get; init; }
    public bool IsHeader { get; init; }
    public bool IsLcd => Device == null && !IsDisabled && !IsCooling && !IsHeader;
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
            if (!_applying && _mode == "Manual") UnifiedRgb.Core.Sensors.SensorHub.SetFanDuty(Index, v);
        }
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

/// <summary>One row in the Cooling panel; IsSection renders as a group label.
/// Rows with a RenameKey are user-renamable (fan rows): the label edits in
/// place and the custom name persists via the onRename callback. Value
/// mutates in place each refresh so an in-progress rename isn't torn down
/// by the refresh timer.</summary>
public sealed class SensorRow : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new(n));

    readonly Action<string, string>? _onRename;
    readonly Action<bool, int>? _onSetManual;
    readonly Action<int>? _onSetDuty;
    readonly Action? _onIdentify;

    public SensorRow(string label, string value, bool isSection = false,
        string? renameKey = null, string? defaultLabel = null, Action<string, string>? onRename = null,
        Action<bool, int>? onSetManual = null, Action<int>? onSetDuty = null, Action? onIdentify = null)
    {
        _label = label; _value = value; IsSection = isSection;
        RenameKey = renameKey; DefaultLabel = defaultLabel ?? label; _onRename = onRename;
        _onSetManual = onSetManual; _onSetDuty = onSetDuty; _onIdentify = onIdentify;
    }

    /// <summary>Blast this fan briefly so the user can spot it in the case.</summary>
    public void Identify() => _onIdentify?.Invoke();

    public bool IsSection { get; }
    public string? RenameKey { get; }
    public string DefaultLabel { get; }
    public bool CanRename => RenameKey != null;

    /*--- manual fan control (rows wired to a controllable header) ---*/
    public bool IsControllable => _onSetManual != null;

    bool _isManual;
    public bool IsManual
    {
        get => _isManual;
        set
        {
            if (_isManual == value) return;
            _isManual = value;
            Notify(nameof(IsManual));
            _onSetManual?.Invoke(value, _duty);
        }
    }

    int _duty = 50;
    public int DutyPercent
    {
        get => _duty;
        set
        {
            int v = Math.Clamp(value, 30, 100);
            if (_duty == v) { if (v != value) Notify(nameof(DutyPercent)); return; }
            _duty = v;
            Notify(nameof(DutyPercent));
            if (_isManual) _onSetDuty?.Invoke(v);
        }
    }

    /// <summary>Refresh-path state sync from the hub (authoritative — e.g.
    /// the thermal failsafe unchecks Manual): no callbacks fired.</summary>
    public void SyncControl(bool manual, int? duty)
    {
        if (_isManual != manual) { _isManual = manual; Notify(nameof(IsManual)); }
        if (duty is int d && _duty != d) { _duty = d; Notify(nameof(DutyPercent)); }
    }

    string _label;
    public string Label
    {
        get => _label;
        set
        {
            // Blank = back to the default name.
            var v = string.IsNullOrWhiteSpace(value) ? DefaultLabel : value.Trim();
            if (v != _label)
            {
                _label = v;
                if (RenameKey != null) _onRename?.Invoke(RenameKey, v);
            }
            Notify(nameof(Label));   // always: snaps the editor back to the stored text
        }
    }

    string _value;
    public string Value
    {
        get => _value;
        set { if (_value == value) return; _value = value; Notify(nameof(Value)); }
    }
}

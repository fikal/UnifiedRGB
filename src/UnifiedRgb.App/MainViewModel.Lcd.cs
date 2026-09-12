namespace UnifiedRgb.App;

// Pump LCD: navigation only. The designer itself (elements, background rect,
// scenes and shows) is LcdDesignerViewModel, bound by LcdDesignerPane.
public sealed partial class MainViewModel
{
    /// <summary>The LCD designer's view model (the LcdDesignerPane's DataContext).</summary>
    public LcdDesignerViewModel Lcd { get; }

    /// <summary>Shows: a timeline of profiles, and therefore of the whole
    /// desk. Top level rather than under the pump panel, which is the
    /// smallest of the three things a show drives.</summary>
    public ShowViewModel Shows { get; }

    bool _isLcdSelected;
    public bool IsLcdSelected
    {
        get => _isLcdSelected;
        set { _isLcdSelected = value; OnChanged(); OnChanged(nameof(ShowLighting)); OnChanged(nameof(ShowPreview)); OnChanged(nameof(ShowGenericPreview)); OnChanged(nameof(ShowLianEditor)); }
    }
    public bool ShowLighting => !_isLcdSelected && !_isSettingsOpen && !_isShowOpen && !IsDisabledSelected && !IsCoolingSelected;

    /// <summary>Turn the pump LCD on (normal render) or off (blank frame). The
    /// panel isn't in Devices, so sleep/wake drives it through here.</summary>
    public void SetPumpLcdOn(bool on) => Lcd.SetOn(on);
}

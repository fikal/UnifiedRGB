using UnifiedRgb.Core.Devices;

namespace UnifiedRgb.App;

// Navigation between the panes (left list) and the Cooling pane's lifecycle.
// The cooling state itself lives in CoolingViewModel (bound by CoolingPane).
public sealed partial class MainViewModel
{
    /// <summary>The Cooling pane's view model (its DataContext).</summary>
    public CoolingViewModel Cooling { get; }

    public bool IsCoolingSelected => _selectedLeft?.IsCooling == true;
    public bool ShowCoolingPanel => IsCoolingSelected && !_isSettingsOpen;

    // Start the refresh timer only while Cooling is on screen; it self-stops
    // when the pane leaves (it used to fire 40x/min for the process lifetime).
    void StartCoolingRefresh() => Cooling.Start();

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
        if (Lcd.Available)
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

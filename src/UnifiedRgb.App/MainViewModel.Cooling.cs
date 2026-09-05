using UnifiedRgb.Core;
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

    /// <summary>"Mouse • 2 LEDs • 74%". The charge is only there for wireless
    /// gear that has answered; everything else reads exactly as before.</summary>
    string SubtitleFor(IRgbDevice d)
    {
        string s = $"{d.Type} • {d.LedCount} LEDs";
        if (_battery.Of(d) is not UnifiedRgb.Core.Sensors.SensorHub.BatteryLevel b) return s;
        return b.Charging ? $"{s} • {b.Percent}% charging" : $"{s} • {b.Percent}%";
    }

    bool IsLowBattery(IRgbDevice d) =>
        _battery.Of(d) is { Charging: false } b && b.Percent <= Services.BatteryMonitor.LowPercent;

    /// <summary>A new charge arrived: retitle the rows in place. Rebuilding the
    /// list instead would drop the selection every minute.</summary>
    void RefreshBatterySubtitles()
    {
        foreach (var item in DeviceItems)
        {
            if (item.Device is not IRgbDevice d) continue;
            item.Subtitle = SubtitleFor(d);
            item.LowBattery = IsLowBattery(d);
        }
    }

    void BuildLeftItems()
    {
        // Devices scroll in their own list; the SYSTEM section (Pump LCD,
        // Cooling) is pinned at the bottom of the card so it never needs
        // scrolling to reach, no matter how many devices there are.
        DeviceItems.Clear();
        foreach (var d in Devices)
            DeviceItems.Add(new LeftItem
            {
                Name = d.Name,
                Subtitle = SubtitleFor(d),
                LowBattery = IsLowBattery(d),
                Device = d,
            });
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
        // Not via the SelectedLeftItem setter: a rebuild is not a user pick,
        // so an open Settings pane (Rescan from its OpenRGB/PawnIO controls)
        // must stay open.
        SelectLeftItem(
            (prev == null ? null
                : AllLeftItems.FirstOrDefault(i => i.Name == prev.Name
                    && i.IsCooling == prev.IsCooling && i.IsDisabled == prev.IsDisabled))
            ?? DeviceItems.FirstOrDefault() ?? SystemItems.FirstOrDefault(),
            closeSettings: false);
        OnChanged(nameof(NoDevicesFound));
    }
}

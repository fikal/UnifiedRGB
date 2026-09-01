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

// Automation, night mode, PawnIO, OpenRGB lifecycle, device disable — split out of the 3,500-line MainViewModel (mechanical
// partial-class move, no behavior change).
public sealed partial class MainViewModel
{
    /*-----------------------------------------------------*\
    | Automation primitives: capture the current lighting,   |
    | restore (frames + running effects) and lights-off.     |
    | Automation transitions ride these so ad-hoc unsaved    |
    | lighting survives a game session or a lock.            |
    \*-----------------------------------------------------*/
    public sealed class LightState
    {
        public required Dictionary<string, Rgb[]> Frames { get; init; }
        public required List<EffectAssignment> Effects { get; init; }
        public string? ProfileName { get; init; }
    }

    public LightState CaptureState() => new()
    {
        Frames = Devices.ToDictionary(d => d.Name, d => (Rgb[])FrameFor(d).Clone()),
        Effects = CaptureEffects(),
        ProfileName = SelectedProfile?.Name,
    };

    public void RestoreState(LightState s)
    {
        foreach (var d in Devices)
        {
            if (!s.Frames.TryGetValue(d.Name, out var saved)) continue;
            var frame = FrameFor(d);
            Array.Copy(saved, frame, Math.Min(saved.Length, frame.Length));
            var dev = d;
            var snap = (Rgb[])frame.Clone();
            _applier.Post(LaneOf(dev), dev, () => { UnifiedRgb.Core.Master.Scale(snap); dev.SetColors(snap); });
        }
        RestoreEffects(s.Effects);
        if (s.ProfileName != null)
            _selectedProfile = Profiles.FirstOrDefault(p => p.Name == s.ProfileName);
        OnChanged(nameof(SelectedProfile));
        OnChanged(nameof(IsStartupProfile));   // direct field write bypasses the setter
        SyncWheelToSelection();
        // Wake the pump LCD back up (LightsOff blanked it during sleep/lock).
        SetPumpLcdOn(true);
    }

    /// <summary>Stop every effect and black every device — WITHOUT touching
    /// the stored frames, so RestoreState/reapply brings it all back.</summary>
    public void LightsOff()
    {
        _engine.StopAll();
        foreach (var d in Devices)
        {
            var dev = d;
            var black = new Rgb[d.LedCount];
            _applier.Post(LaneOf(dev), dev, () => dev.SetColors(black));
        }
        // The pump LCD isn't an RGB device, so blank it separately - otherwise
        // sleep/lock leaves the screen lit.
        SetPumpLcdOn(false);
    }

    /// <summary>Turn the pump LCD on (normal render) or off (blank frame). The
    /// panel isn't in Devices, so sleep/wake drives it through here.</summary>
    public void SetPumpLcdOn(bool on)
    {
        if (_lcd != null) { _lcd.On = on; _lcd.Refresh(); }
    }

    internal SettingsData SettingsData => _store.Settings;
    internal void PersistSettings() => _store.SaveSettings();

    /*--- automation settings surface ---*/
    public ObservableCollection<AutomationRule> AutoRules { get; } = new();

    public bool LockLightsOff
    {
        get => _store.Settings.LockLightsOff;
        set { _store.Settings.LockLightsOff = value; _store.SaveSettings(); OnChanged(); }
    }
    /// <summary>Half-hour choices for the night window dropdowns.</summary>
    public static string[] TimeOptions { get; } =
        Enumerable.Range(0, 48).Select(i => $"{i / 2:00}:{(i % 2) * 30:00}").ToArray();

    public bool NightMode
    {
        get => _store.Settings.NightMode;
        set { _store.Settings.NightMode = value; _store.SaveSettings(); OnChanged(); }
    }
    public string NightStart
    {
        get => _store.Settings.NightStart;
        set { _store.Settings.NightStart = value; _store.SaveSettings(); OnChanged(); }
    }
    public string NightEnd
    {
        get => _store.Settings.NightEnd;
        set { _store.Settings.NightEnd = value; _store.SaveSettings(); OnChanged(); }
    }
    /// <summary>Night mode waits for ~10 min of inactivity instead of firing at
    /// the start time - so an evening session isn't cut off mid-use.</summary>
    public bool NightIdleOnly
    {
        get => _store.Settings.NightIdleOnly;
        set { _store.Settings.NightIdleOnly = value; _store.SaveSettings(); OnChanged(); }
    }
    public bool AppSwitchEnabled
    {
        get => _store.Settings.AppSwitchEnabled;
        set { _store.Settings.AppSwitchEnabled = value; _store.SaveSettings(); OnChanged(); }
    }

    public IReadOnlyList<string> ProfileNames => Profiles.Select(p => p.Name).ToList();

    /// <summary>Add a fully-specified rule (the dialog validates both
    /// halves — a blank rule can never exist again).</summary>
    public void AddAutoRuleExplicit(string process, string profile)
    {
        if (string.IsNullOrWhiteSpace(process) || string.IsNullOrWhiteSpace(profile)) return;
        var r = new AutomationRule { Process = process.Trim(), Profile = profile };
        (_store.Settings.AutomationRules ??= new()).Add(r);
        AutoRules.Add(r);
        _store.SaveSettings();
    }

    /// <summary>Reorder a rule. Order is priority: the TOP matching rule
    /// wins when several match (the matcher returns the first hit).</summary>
    public void MoveAutoRule(AutomationRule r, int newIndex)
    {
        var list = _store.Settings.AutomationRules;
        if (list == null) return;
        int old = list.IndexOf(r);
        if (old < 0) return;
        newIndex = Math.Clamp(newIndex, 0, list.Count - 1);
        if (old == newIndex) return;
        list.RemoveAt(old);
        list.Insert(newIndex, r);
        AutoRules.Move(old, newIndex);
        _store.SaveSettings();
    }

    public void RemoveAutoRule(AutomationRule r)
    {
        _store.Settings.AutomationRules?.Remove(r);
        AutoRules.Remove(r);
        _store.SaveSettings();
    }

    public void PersistAutomation() => _store.SaveSettings();

    string _automationStatus = "";
    /// <summary>Live line from the automation watcher (what app it sees and
    /// what it decided) — the feature is a black box without it.</summary>
    public string AutomationStatus
    {
        get => _automationStatus;
        set { if (_automationStatus == value) return; _automationStatus = value; OnChanged(); }
    }

    /*--- Night-off must never be invisible: it looked like "no effects are
          working". The automation drives this flag; a banner with a Wake
          button shows whenever the lights are off on schedule. ---*/
    bool _nightLightsOff;
    public bool NightLightsOff
    {
        get => _nightLightsOff;
        set { if (_nightLightsOff == value) return; _nightLightsOff = value; OnChanged(); }
    }

    /// <summary>Set by the automation service; the banner button calls it.</summary>
    public Action? WakeLightsHook { get; set; }
    public void WakeLights() => WakeLightsHook?.Invoke();

    /*--- PawnIO driver presence (a field machine lacked it: no CPU temp, no
          fans, no RAM RGB — and nothing in the UI said why) ---*/
    public bool PawnIoMissing => !UnifiedRgb.Core.Native.PawnIoInstaller.IsInstalled;
    public string PawnIoStatusText => PawnIoMissing ? "PawnIO: not installed" : "PawnIO: installed ✓";

    string _pawnIoInstallStatus = "";
    public string PawnIoInstallStatus
    {
        get => _pawnIoInstallStatus;
        set { _pawnIoInstallStatus = value; OnChanged(); }
    }

    bool _pawnIoInstalling;
    /// <summary>True while an install is in flight — drives the busy indicator
    /// and gates the button so it can't be re-clicked mid-install.</summary>
    public bool PawnIoInstalling
    {
        get => _pawnIoInstalling;
        set { _pawnIoInstalling = value; OnChanged(); OnChanged(nameof(PawnIoInstallEnabled)); }
    }
    public bool PawnIoInstallEnabled => !_pawnIoInstalling;

    /// <summary>Download + run the official PawnIO installer, then rescan so
    /// the newly unlocked sensors/devices appear without a restart.</summary>
    public async Task InstallPawnIoAsync()
    {
        if (_pawnIoInstalling) return;   // guard against re-clicks while running
        PawnIoInstalling = true;
        try
        {
            await UnifiedRgb.Core.Native.PawnIoInstaller.InstallAsync(
                s => System.Windows.Application.Current.Dispatcher.Invoke(() => PawnIoInstallStatus = s));
            OnChanged(nameof(PawnIoMissing));
            OnChanged(nameof(PawnIoStatusText));
            if (!PawnIoMissing)
            {
                // CPU temp and the ITE board-fan fallback both need PawnIO, which
                // was absent when the sensor hub first opened - reset it so they
                // appear now instead of only after the next launch.
                UnifiedRgb.Core.Sensors.SensorHub.ResetSources();
                Rescan();
            }
        }
        finally { PawnIoInstalling = false; }
    }

    public bool UseOpenRgb
    {
        get => _store.Settings.UseOpenRgb;
        set
        {
            if (_store.Settings.UseOpenRgb == value) return;
            _store.Settings.UseOpenRgb = value;
            _store.SaveSettings();
            OnChanged();
            if (value) StartOpenRgbBridge();
            else
            {
                OpenRgbLink.Shutdown();
                OpenRgbManager.Stop();
                OpenRgbStatus = "";
                Rescan();
            }
        }
    }

    /// <summary>Bring the bridge up off the UI thread (first run downloads
    /// ~12MB and OpenRGB's own detection takes several seconds), then rescan.
    /// Also turns off OpenRGB detectors for hardware we skipped as natively
    /// driven, so the bundled instance stops touching it at all.</summary>
    async void StartOpenRgbBridge()
    {
        OpenRgbStatus = "starting...";
        bool ok = await Task.Run(() => OpenRgbManager.EnsureRunningAsync(
            s => Application.Current.Dispatcher.Invoke(() => OpenRgbStatus = s)));
        if (!ok) { return; }

        Rescan();
        // A server that vanished between "up" and now = OpenRGB crashed while
        // scanning this machine's hardware. Say so honestly instead of
        // "0 extra devices", and point at the report that names the culprit.
        if (!OpenRgbManager.IsServerUp())
        {
            OpenRgbStatus = "OpenRGB crashed while scanning your hardware. Hit Send in Support so we can see which device.";
            return;
        }
        int bridged = Devices.Count(d => d is OpenRgbDevice);
        OpenRgbStatus = BridgeStatusText(bridged);

        if (await OpenRgbManager.ReleaseNativelyDrivenAsync(OpenRgbLink.LastSkipped))
        {
            Rescan();
            bridged = Devices.Count(d => d is OpenRgbDevice);
            OpenRgbStatus = BridgeStatusText(bridged);
        }
    }

    static string BridgeStatusText(int bridged)
    {
        string s = $"connected, {bridged} extra device(s)";
        foreach (var note in OpenRgbManager.LastPolicyNotes) s += $"\n{note}";
        return s;
    }

    /*-----------------------------------------------------*\
    | Disable / enable a device. Disabled = its whole       |
    | driver family is skipped at detection, so the app     |
    | never opens or writes the hardware — other RGB        |
    | software can own it. Profiles keep its saved state.   |
    \*-----------------------------------------------------*/
    bool IsFamilyDisabled(string family) =>
        _store.Settings.DisabledDevices?.Any(e => e.Family == family) == true;

    public void DisableSelectedDevice()
    {
        var dev = SelectedDevice;
        if (dev == null || !_manager.FamilyOf.TryGetValue(dev, out var family)) return;

        var list = _store.Settings.DisabledDevices ??= new();
        // The family is the skip unit, so siblings (e.g. both RAM sticks)
        // disable together — record a row for each.
        foreach (var sib in Devices.Where(d =>
                     _manager.FamilyOf.TryGetValue(d, out var f) && f == family))
        {
            if (!list.Any(e => e.Name == sib.Name)) list.Add(new DisabledDeviceEntry { Name = sib.Name, Family = family });
        }
        _store.SaveSettings();
        Log.Info("devices", $"disabled family {family}");

        string name = dev.Name;
        Rescan();     // disposes every instance (handles released), redetects without this family
        SelectedLeftItem = AllLeftItems.FirstOrDefault(i => i.IsDisabled && i.Name == name) ?? DeviceItems.FirstOrDefault();
    }

    public void EnableSelectedDevice()
    {
        var item = SelectedLeftItem;
        if (item?.IsDisabled != true) return;
        var list = _store.Settings.DisabledDevices;
        var entry = list?.FirstOrDefault(e => e.Name == item.Name);
        if (entry == null) return;
        list!.RemoveAll(e => e.Family == entry.Family);      // siblings re-enable together
        _store.SaveSettings();
        Log.Info("devices", $"enabled family {entry.Family}");

        Rescan();
        SelectedLeftItem = AllLeftItems.FirstOrDefault(i => i.Name == item.Name) ?? DeviceItems.FirstOrDefault();
    }

    public IReadOnlyList<Rgb> Swatches { get; } = new[]
    {
        Rgb.Red, new Rgb(255,128,0), Rgb.FromHex("FFFF00"), Rgb.Green,
        Rgb.FromHex("00FFFF"), Rgb.Blue, Rgb.FromHex("8000FF"), Rgb.FromHex("FF00FF"),
        Rgb.White, Rgb.FromHex("FF6699"), Rgb.Black,
    };
}

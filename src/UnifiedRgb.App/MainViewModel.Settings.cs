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
using UnifiedRgb.Core.Automation;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Sensors;
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
        /// <summary>Unsaved-changes flag at capture time, so an override round
        /// trip doesn't quietly drop the close-time save prompt.</summary>
        public bool Dirty { get; init; }
    }

    public LightState CaptureState() => new()
    {
        Frames = Devices.ToDictionary(d => d.Name, d => (Rgb[])FrameFor(d).Clone()),
        Effects = CaptureEffects(),
        ProfileName = SelectedProfile?.Name,
        Dirty = _dirty,
    };

    /// <summary>Stop our effects on one device, leaving every other device
    /// running. Used when an SDK client takes a device over: two writers on one
    /// lane would just fight.</summary>
    public void StopEffectsOn(IRgbDevice device) => _engine.StopRange(device, 0, device.LedCount);

    public void RestoreState(LightState s)
    {
        _engine.StopAll();   // before the static writes (see LoadProfile)
        foreach (var d in Devices)
            if (s.Frames.TryGetValue(d.Name, out var saved)) RestoreFrame(d, saved);
        RestoreEffects(s.Effects);
        // Restore the selection exactly - including "no profile selected" (an
        // app rule's profile used to stay selected over restored ad-hoc lighting).
        _selectedProfile = s.ProfileName is null ? null : Profiles.FirstOrDefault(p => p.Name == s.ProfileName);
        _dirty = s.Dirty;
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
        foreach (var d in Devices) _lighting.PushBlack(d);
        // The pump LCD isn't an RGB device, so blank it separately - otherwise
        // sleep/lock leaves the screen lit.
        SetPumpLcdOn(false);
    }

    internal SettingsData SettingsData => _store.Settings;

    /*--- automation settings surface ---*/
    public ObservableCollection<AutomationRule> AutoRules { get; } = new();

    /// <summary>Settings pass-through setter: assign, persist, notify — the
    /// pattern nine bindable settings repeated by hand (a no-op when unchanged).</summary>
    void SetSetting<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        _store.SaveSettings();
        OnChanged(name);
    }

    public bool LockLightsOff
    {
        get => _store.Settings.LockLightsOff;
        set => SetSetting(_store.Settings.LockLightsOff, value, v => _store.Settings.LockLightsOff = v);
    }
    /// <summary>Half-hour choices for the night window dropdowns.</summary>
    public static string[] TimeOptions { get; } =
        Enumerable.Range(0, 48).Select(i => $"{i / 2:00}:{(i % 2) * 30:00}").ToArray();

    public bool NightMode
    {
        get => _store.Settings.NightMode;
        set => SetSetting(_store.Settings.NightMode, value, v => _store.Settings.NightMode = v);
    }
    public string NightStart
    {
        get => _store.Settings.NightStart;
        set => SetSetting(_store.Settings.NightStart, value, v => _store.Settings.NightStart = v);
    }
    public string NightEnd
    {
        get => _store.Settings.NightEnd;
        set => SetSetting(_store.Settings.NightEnd, value, v => _store.Settings.NightEnd = v);
    }
    /// <summary>Night mode waits for ~10 min of inactivity instead of firing at
    /// the start time - so an evening session isn't cut off mid-use.</summary>
    public bool NightIdleOnly
    {
        get => _store.Settings.NightIdleOnly;
        set => SetSetting(_store.Settings.NightIdleOnly, value, v => _store.Settings.NightIdleOnly = v);
    }
    public bool AppSwitchEnabled
    {
        get => _store.Settings.AppSwitchEnabled;
        set => SetSetting(_store.Settings.AppSwitchEnabled, value, v => _store.Settings.AppSwitchEnabled = v);
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

    /*--- schedules ---*/
    public ObservableCollection<ScheduleRule> Schedules { get; } = new();

    public void AddSchedule(ScheduleRule r)
    {
        (_store.Settings.Schedules ??= new()).Add(r);
        Schedules.Add(r);
        _store.SaveSettings();
        OnChanged(nameof(ScheduleSummary));
    }

    public void RemoveSchedule(ScheduleRule r)
    {
        _store.Settings.Schedules?.Remove(r);
        Schedules.Remove(r);
        _store.SaveSettings();
        OnChanged(nameof(ScheduleSummary));
    }

    /// <summary>"Next: lights off at 23:00" for the Settings line, so the
    /// feature says what it is about to do without opening the editor.</summary>
    public string ScheduleSummary
    {
        get
        {
            var next = ScheduleRule.NextChange(_store.Settings.Schedules, DateTime.Now);
            if (next is not (DateTime when, ScheduleRule rule)) return "Nothing scheduled.";
            string what = rule.Action == ScheduleAction.LightsOff
                ? "lights off"
                : $"profile '{rule.Profile}'";
            string day = when.Date == DateTime.Today ? "today"
                : when.Date == DateTime.Today.AddDays(1) ? "tomorrow"
                : when.DayOfWeek.ToString();
            return $"Next: {what} {day} at {rule.Start}.";
        }
    }

    /// <summary>The summary is computed, so it has to be asked to refresh once
    /// the editor closes.</summary>
    public void RefreshScheduleSummary() => OnChanged(nameof(ScheduleSummary));

    /*--- sensor rules ---*/
    public ObservableCollection<SensorRule> SensorRules { get; } = new();

    public bool SensorRulesEnabled
    {
        get => _store.Settings.SensorRulesEnabled;
        set => SetSetting(_store.Settings.SensorRulesEnabled, value, v => _store.Settings.SensorRulesEnabled = v);
    }

    /*-----------------------------------------------------*\
    | OpenRGB SDK server: other software driving our lights. |
    \*-----------------------------------------------------*/

    public bool SdkServerEnabled
    {
        get => _store.Settings.SdkServerEnabled;
        set
        {
            if (_store.Settings.SdkServerEnabled == value) return;
            SetSetting(_store.Settings.SdkServerEnabled, value, v => _store.Settings.SdkServerEnabled = v);
            RestartSdkServer();
        }
    }

    public bool SdkServerLan
    {
        get => _store.Settings.SdkServerLan;
        set
        {
            if (_store.Settings.SdkServerLan == value) return;
            SetSetting(_store.Settings.SdkServerLan, value, v => _store.Settings.SdkServerLan = v);
            RestartSdkServer();   // the bind address changed
        }
    }

    /// <summary>What the settings pane shows under the toggle: where we are
    /// listening and who is connected.</summary>
    public string SdkServerStatus
    {
        get
        {
            if (!SdkServerEnabled) return "Off.";
            if (_sdkServer is not { Running: true }) return "Could not open a port.";
            string where = SdkServerLan ? "on the network" : "on this machine";
            var names = _sdkServer.ClientNames;
            string who = names.Count == 0 ? "No clients connected."
                       : names.Count == 1 ? $"Connected: {names[0]}."
                       : $"Connected: {string.Join(", ", names)}.";
            return $"Listening {where} on port {_sdkServer.Port}. {who}";
        }
    }

    void RestartSdkServer()
    {
        _sdkServer?.Dispose();
        _sdkServer = null;
        if (!SdkServerEnabled) { OnChanged(nameof(SdkServerStatus)); return; }

        _sdkHost ??= new Services.OpenRgbHost(this, _lighting, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _sdkHost.SetDevices(Devices);
        var server = new UnifiedRgb.Core.Net.OpenRgbServer(_sdkHost);
        // Fired from a socket thread; the status line is a UI binding.
        server.ClientsChanged += () => _dispatcher.BeginInvoke(() => OnChanged(nameof(SdkServerStatus)));
        server.Start(SdkServerLan);
        _sdkServer = server;
        OnChanged(nameof(SdkServerStatus));
    }

    /// <summary>Called after every detect, including the first. Starts the
    /// server if the user has it on, and tells any connected client that the
    /// device instances it was addressing have been replaced.</summary>
    void SyncSdkServer()
    {
        if (!SdkServerEnabled) return;
        if (_sdkServer == null) { RestartSdkServer(); return; }
        _sdkHost?.SetDevices(Devices);
        _sdkServer.DeviceListChanged();
    }

    /*-----------------------------------------------------*\
    | Counter-Strike 2 game state.                           |
    \*-----------------------------------------------------*/

    UnifiedRgb.Core.Games.GsiServer? _gsi;

    public bool Cs2Enabled
    {
        get => _store.Settings.Cs2Enabled;
        set
        {
            if (_store.Settings.Cs2Enabled == value) return;
            SetSetting(_store.Settings.Cs2Enabled, value, v => _store.Settings.Cs2Enabled = v);
            if (value) StartGsi();
            else
            {
                // Take the config out too: leaving it behind means the game
                // keeps posting to a port nothing is listening on, and pays the
                // timeout on every update.
                UnifiedRgb.Core.Games.GsiConfig.Uninstall();
                _gsi?.Dispose(); _gsi = null;
                UnifiedRgb.Core.Effects.Cs2Effect.Server = null;
            }
            OnChanged(nameof(Cs2Status));
        }
    }

    /// <summary>The token in the game's config. Made once and kept, so
    /// re-installing does not orphan a config written earlier.</summary>
    string GsiToken
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_store.Settings.GsiToken))
            {
                _store.Settings.GsiToken = UnifiedRgb.Core.Games.GsiServer.NewToken();
                _store.SaveSettings();
            }
            return _store.Settings.GsiToken!;
        }
    }

    public string Cs2Status
    {
        get
        {
            if (!Cs2Enabled) return "Off.";
            if (_gsi is not { Running: true }) return "Could not open a port.";
            if (_gsi.Connected)
            {
                var s = _gsi.State;
                string where = s.Playing ? $"in game, {s.Health} health" : "in game";
                return $"Connected: {where}.";
            }
            return UnifiedRgb.Core.Games.GsiConfig.Cs2CfgFolders().Count == 0
                ? "Counter-Strike 2 was not found. Install the config by hand, or install the game first."
                : "Waiting for the game. Start CS2 with the config installed.";
        }
    }

    void StartGsi()
    {
        if (!Cs2Enabled) return;
        if (_gsi is { Running: true }) return;

        var server = new UnifiedRgb.Core.Games.GsiServer();
        int port = server.Start(GsiToken);
        if (port == 0) { OnChanged(nameof(Cs2Status)); return; }

        // Fired from the listener thread; the status line is a UI binding.
        server.Connectedchanged += () => _dispatcher.BeginInvoke(() => OnChanged(nameof(Cs2Status)));
        _gsi = server;
        UnifiedRgb.Core.Effects.Cs2Effect.Server = server;
    }

    /// <summary>Write the game's config file. Returns what to show the user:
    /// the paths written, or why it could not be.</summary>
    public string InstallCs2Config()
    {
        StartGsi();
        if (_gsi is not { Running: true }) return "The listener could not open a port, so there is nothing to point the game at.";

        var written = UnifiedRgb.Core.Games.GsiConfig.Install(
            $"http://localhost:{_gsi.Port}", GsiToken, out string? error);
        OnChanged(nameof(Cs2Status));

        if (written.Count > 0)
            return $"Installed to {string.Join(", ", written)}. Restart CS2 if it is running.";
        return error ?? "Counter-Strike 2 was not found.";
    }

    /// <summary>The config file's contents, for the user to paste by hand when
    /// writing it failed (a locked folder, a game on a drive we cannot write).</summary>
    public string Cs2ConfigText()
    {
        StartGsi();
        int port = _gsi?.Port ?? UnifiedRgb.Core.Games.GsiServer.DefaultPort;
        return UnifiedRgb.Core.Games.GsiConfig.Build($"http://localhost:{port}", GsiToken);
    }

    /// <summary>Does a profile by this name still exist? The automation calls
    /// this per tick, so it must not allocate the way ProfileNames does.</summary>
    public bool HasProfile(string name)
    {
        foreach (var p in Profiles)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Sources a rule can watch on THIS machine: the headline values
    /// always, plus whatever board temps and fans the hub has actually found.</summary>
    public IReadOnlyList<string> SensorSourceChoices
    {
        get
        {
            SensorHub.Touch();   // a closed Settings pane leaves the lists empty otherwise
            var list = new List<string>
            {
                SensorSources.CpuTemp, SensorSources.GpuTemp, SensorSources.Hottest,
                SensorSources.CpuLoad, SensorSources.GpuLoad,
            };
            foreach (var t in SensorHub.BoardTemps)
                if (!string.IsNullOrWhiteSpace(t.Name)) list.Add(SensorSources.BoardPrefix + t.Name);
            foreach (var f in SensorHub.BoardFans)
                if (!string.IsNullOrWhiteSpace(f.Name)) list.Add(SensorSources.FanPrefix + f.Name);
            // Only wireless gear that has answered: offering a battery rule for
            // a device that has no battery would just be a rule that never fires.
            foreach (var b in SensorHub.Batteries)
                if (!string.IsNullOrWhiteSpace(b.Name)) list.Add(SensorSources.BatteryPrefix + b.Name);
            return list;
        }
    }

    public void AddSensorRule(SensorRule r)
    {
        if (string.IsNullOrWhiteSpace(r.Source) || string.IsNullOrWhiteSpace(r.Profile)) return;
        (_store.Settings.SensorRules ??= new()).Add(r);
        SensorRules.Add(r);
        _store.SaveSettings();
    }

    public void RemoveSensorRule(SensorRule r)
    {
        _store.Settings.SensorRules?.Remove(r);
        SensorRules.Remove(r);
        _store.SaveSettings();
    }

    /// <summary>Reorder: the first firing rule wins, so this is the priority
    /// the user is editing.</summary>
    public void MoveSensorRule(SensorRule r, int newIndex)
    {
        var list = _store.Settings.SensorRules;
        if (list == null) return;
        int old = list.IndexOf(r);
        if (old < 0) return;
        newIndex = Math.Clamp(newIndex, 0, list.Count - 1);
        if (old == newIndex) return;
        list.RemoveAt(old);
        list.Insert(newIndex, r);
        SensorRules.Move(old, newIndex);
        _store.SaveSettings();
    }

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
    /// <summary>Set by the automation while the lights are deliberately off
    /// (locked session / night window). Scene sequences hold their steps so a
    /// timed profile can't relight the case at 3 AM.</summary>
    public bool LightsSuppressed { get; set; }
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
            Cooling.NotifyPawnIoChanged();
            Lcd.NotifyPawnIoChanged();   // the "CPU temp needs PawnIO" banner clears
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
            else StopOpenRgbBridge();
        }
    }

    /// <summary>The in-flight bridge teardown (completed when none). A bridge-up
    /// that follows a quick off/on toggle waits on it, so EnsureRunning can't
    /// launch a new OpenRGB while Stop is still killing the old one.</summary>
    Task _openRgbStop = Task.CompletedTask;

    /// <summary>Take the bridge down off the UI thread: OpenRgbManager.Stop runs
    /// a process snapshot, Kill and up to a 3 s WaitForExit per instance, which
    /// used to freeze the window from the property setter. Rescan follows on
    /// the dispatcher, mirroring StartOpenRgbBridge.</summary>
    async void StopOpenRgbBridge()
    {
        try
        {
            // Stop the effect workers BEFORE the shared socket goes away:
            // every bridged channel otherwise throws ObjectDisposedException
            // per frame (a rate-limited WARN each) until Rescan's own drain,
            // which waits behind OpenRgbManager.Stop's process teardown. The
            // stopped channels are still captured and restored by Rescan.
            _lighting.StopAndDrain();
            OpenRgbStatus = "stopping...";
            // The task never faults (its own catch), so StartOpenRgbBridge can
            // await it bare.
            _openRgbStop = Task.Run(() =>
            {
                try { OpenRgbLink.Shutdown(); OpenRgbManager.Stop(); }
                catch (Exception ex) { Log.Error("openrgb", ex); }
            });
            await _openRgbStop;
            OpenRgbStatus = "";
            Rescan();
        }
        catch (Exception ex)
        {
            Log.Error("openrgb", ex);
            OpenRgbStatus = $"OpenRGB stop failed: {ex.Message}";
        }
    }

    /// <summary>Bring the bridge up off the UI thread (first run downloads
    /// ~12MB and OpenRGB's own detection takes several seconds), then rescan.
    /// Also turns off OpenRGB detectors for hardware we skipped as natively
    /// driven, so the bundled instance stops touching it at all.</summary>
    async void StartOpenRgbBridge()
    {
        // async void: an escaping exception is a DispatcherUnhandledException
        // dialog, so the whole flow is guarded like the other async voids.
        try
        {
            OpenRgbStatus = "starting...";
            await _openRgbStop;   // an off->on toggle: let the teardown finish first
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
        catch (Exception ex)
        {
            Log.Error("openrgb", ex);
            OpenRgbStatus = $"OpenRGB bridge failed: {ex.Message}";
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

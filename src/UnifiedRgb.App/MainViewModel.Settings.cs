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
    /// <summary>Hardware the last scan could SEE but could not fully drive.
    ///
    /// The whole point is that this is visible in the app rather than only in
    /// a log. A device that is present but held by vendor software, or that
    /// needs a driver, used to simply not appear - which reads as "UnifiedRGB
    /// does not support my mouse" when the truth is one sentence long and has
    /// a fix attached.</summary>
    public ObservableCollection<BlockedDevice> BlockedDevices { get; } = new();
    public bool HasBlockedDevices => BlockedDevices.Count > 0;

    void RefreshBlockedDevices()
    {
        BlockedDevices.Clear();
        foreach (var b in UnifiedRgb.Core.DetectionNotes.Current) BlockedDevices.Add(b);
        OnChanged(nameof(HasBlockedDevices));
    }

    /*-----------------------------------------------------*\
    | Device health: the UI-facing half of DeviceHealth.      |
    |                                                         |
    | Two sources, one list, because the user does not care   |
    | which of them a problem came from. A device we HOLD     |
    | reports through the write verdict (connected /          |
    | retrying / not responding / controlled by another app); |
    | hardware we could SEE but never open reports through    |
    | DetectionNotes, whose reasons map onto exactly the same |
    | words (BlockedDevice.Health).                           |
    |                                                         |
    | Rebuilt on a transition, never on a timer. A rig where  |
    | everything works never rebuilds this at all, which is   |
    | the point: the whole feature has to be free while it    |
    | has nothing to say.                                     |
    \*-----------------------------------------------------*/

    /// <summary>One device and what the app can honestly say about it.</summary>
    public sealed class DeviceHealthRow
    {
        public required string Name { get; init; }
        public required UnifiedRgb.Core.DeviceHealthState State { get; init; }

        /// <summary>"connected", "retrying", "not responding", "controlled by
        /// another app" - the one place those words are produced is
        /// DeviceHealth.Describe, so the badge, the log and a support bundle
        /// cannot drift apart.</summary>
        public required string StateText { get; init; }

        /// <summary>The specific thing, when there is one.</summary>
        public string? Detail { get; init; }

        /// <summary>What would fix it, or null. A status with no next step is
        /// just an accusation.</summary>
        public string? Remedy { get; init; }

        /// <summary>Worth a badge. Retrying is not: it clears itself on the
        /// next frame, and a warning that vanishes before it can be read is
        /// noise.</summary>
        public bool IsProblem => UnifiedRgb.Core.DeviceHealth.IsWorthShowing(State);

        public override string ToString()
            => Detail == null ? $"{Name}: {StateText}" : $"{Name}: {StateText} ({Detail})";
    }

    /// <summary>Every device the app knows about, with its health. Rebuilt in
    /// place on a real transition; bindable straight to an ItemsControl.</summary>
    public ObservableCollection<DeviceHealthRow> DeviceHealthRows { get; } = new();

    /// <summary>True when anything on the rig is not simply working. Drives a
    /// single badge rather than a per-row one, because the common case is zero
    /// problems and the second most common is exactly one.</summary>
    public bool AnyDeviceUnhealthy => DeviceHealthRows.Any(r => r.IsProblem);

    /// <summary>One line for the header, or empty when all is well.</summary>
    public string DeviceHealthSummary
    {
        get
        {
            var bad = DeviceHealthRows.Where(r => r.IsProblem).ToList();
            if (bad.Count == 0) return "";
            if (bad.Count == 1) return $"{bad[0].Name}: {bad[0].StateText}";
            return $"{bad.Count} devices need attention";
        }
    }

    /// <summary>Health for one device, for a per-row badge in the device list.
    /// Cheap: a dictionary probe and an uncontended lock.</summary>
    public UnifiedRgb.Core.DeviceHealthState HealthOf(IRgbDevice d)
        => UnifiedRgb.Core.DeviceHealth.Shared.StateOf(d);

    /// <summary>The same, as the words the user reads.</summary>
    public string HealthTextOf(IRgbDevice d)
        => UnifiedRgb.Core.DeviceHealth.Describe(HealthOf(d));

    /// <summary>Devices the SDK bridge has told us it took, as opposed to ones
    /// it has actually painted yet.
    ///
    /// LightingController.IsClaimed only becomes true on a client's FIRST
    /// frame, and BeginExternal calls StopEffectsOn before that frame arrives.
    /// Asking IsClaimed alone would therefore raise the badge and drop it
    /// again in the same breath, which is precisely the flapping the health
    /// hysteresis exists to prevent - so the two are ORed. Cleared wholesale
    /// in RestoreState, which is what the bridge calls when the last client
    /// lets go and what a rescan calls when every claim dies at once.</summary>
    readonly HashSet<IRgbDevice> _sdkHeld = new();

    /// <summary>Non-zero while a rebuild is already queued, so a burst of
    /// transitions (a dongle pulled while six devices are streaming) costs one
    /// dispatcher hop rather than six. An int through Interlocked rather than a
    /// bool: the writers are effect workers, applier lanes and SDK socket
    /// threads, and an unsynchronised read that saw a stale "queued" dropped
    /// that device's transition entirely - the badge for a device that had just
    /// died simply never appeared.</summary>
    int _healthRefreshQueued;

    /// <summary>Subscribe once, at construction. The handler runs on whichever
    /// thread wrote - an effect worker, an applier lane, an SDK socket - so
    /// everything it touches has to get back to the UI first.</summary>
    void HookDeviceHealth()
    {
        UnifiedRgb.Core.DeviceHealth.Shared.Changed += _ =>
        {
            if (Interlocked.Exchange(ref _healthRefreshQueued, 1) == 1) return;
            _dispatcher.BeginInvoke(new Action(() =>
            {
                // Cleared AFTER the rebuild, not before: the rebuild itself
                // re-asserts every device's claim state and can therefore
                // raise transitions of its own, and clearing first would have
                // each of those queue yet another rebuild.
                try { RefreshDeviceHealth(); }
                finally { Interlocked.Exchange(ref _healthRefreshQueued, 0); }
            }));
        };
    }

    /// <summary>Rebuild the rows from both sources. UI thread only.</summary>
    void RefreshDeviceHealth()
    {
        // An SDK client holding a device IS "controlled by another app", and
        // the bridge has no event to tell us: it tells us by calling
        // StopEffectsOn and RestoreState, both of which land here. Re-asking
        // every device whether it is still claimed is a handful of dictionary
        // probes, and it means a stale claim cannot survive a rebuild.
        foreach (var d in Devices)
            UnifiedRgb.Core.DeviceHealth.Shared.SetHeldByOther(
                d, _sdkHeld.Contains(d) || _lighting.IsClaimed(d), "an OpenRGB client");

        DeviceHealthRows.Clear();
        foreach (var d in Devices)
        {
            var state = UnifiedRgb.Core.DeviceHealth.Shared.StateOf(d);
            DeviceHealthRows.Add(new DeviceHealthRow
            {
                Name = d.Name,
                State = state,
                StateText = UnifiedRgb.Core.DeviceHealth.Describe(state),
                Detail = UnifiedRgb.Core.DeviceHealth.Shared.DetailOf(d),
                Remedy = UnifiedRgb.Core.DeviceHealth.Remedy(state),
            });
        }

        // Hardware the scan could see but never opened has no instance to key
        // health by, so it comes from the detection channel instead - the same
        // words, because BlockedDevice.Health maps those reasons onto this
        // enum rather than a parallel one being invented beside it.
        foreach (var b in BlockedDevices)
            DeviceHealthRows.Add(new DeviceHealthRow
            {
                Name = b.What,
                State = b.Health,
                StateText = UnifiedRgb.Core.DeviceHealth.Describe(b.Health),
                Detail = b.Detail,
                Remedy = b.Remedy ?? UnifiedRgb.Core.DeviceHealth.Remedy(b.Health),
            });

        OnChanged(nameof(AnyDeviceUnhealthy));
        OnChanged(nameof(DeviceHealthSummary));
    }

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
        /// <summary>The pump screen at capture time (null = no panel). A rule's
        /// profile can bring its own screen; restoring the LEDs without this
        /// left the panel on the rule's screen after the rule ended.</summary>
        public LcdDesignerViewModel.LcdSnapshot? Screen { get; init; }
    }

    public LightState CaptureState()
    {
        _recoveryLighting.Remember(Devices, FrameFor, CaptureEffects(), replaceEffects: !LightsSuppressed);
        return new()
        {
            Frames = _recoveryLighting.Frames.ToDictionary(p => p.Key, p => (Rgb[])p.Value.Clone()),
            Effects = _recoveryLighting.Effects.ToList(),
            ProfileName = SelectedProfile?.Name,
            Dirty = _dirty,
            Screen = Lcd.SnapshotDesign(),
        };
    }

    /// <summary>Stop our effects on one device, leaving every other device
    /// running. Used when an SDK client takes a device over: two writers on one
    /// lane would just fight.</summary>
    public void StopEffectsOn(IRgbDevice device)
    {
        _engine.StopRange(device, 0, device.LedCount);
        _sdkHeld.Add(device);
        // The SDK bridge calls this and nothing else does, so it is the one
        // hook the app has for "a client just took this device". Saying so
        // here is what turns an invisible takeover into the "controlled by
        // another app" badge, and it costs one dictionary probe.
        UnifiedRgb.Core.DeviceHealth.Shared.SetHeldByOther(device, true, "an OpenRGB client");
    }

    /// <summary>One client let go of one device. The wholesale clear in
    /// RestoreState only runs when the LAST client leaves, so without this a
    /// second client still holding a different device kept every released
    /// device in _sdkHeld - and RefreshDeviceHealth ORs that set, so the
    /// released device re-declared itself "controlled by another app", advising
    /// the user to close a program that had already let go, for the rest of the
    /// session.</summary>
    public void ReleaseHold(IRgbDevice device)
    {
        if (!_sdkHeld.Remove(device)) return;
        UnifiedRgb.Core.DeviceHealth.Shared.SetHeldByOther(device, false, "an OpenRGB client");
        RefreshDeviceHealth();
    }

    public void RestoreState(LightState s, bool honorSuppression = false)
    {
        bool suppressed = honorSuppression && LightsSuppressed;
        _recoveryLighting.Restore(s.Frames, s.Effects);
        _engine.StopAll();   // before the static writes (see LoadProfile)
        foreach (var d in Devices)
            if (s.Frames.TryGetValue(d.Name, out var saved))
            {
                if (suppressed) Array.Copy(saved, FrameFor(d), Math.Min(saved.Length, d.LedCount));
                else RestoreFrame(d, saved);
            }
        if (!suppressed) RestoreEffects(s.Effects);
        // Restore the selection exactly - including "no profile selected" (an
        // app rule's profile used to stay selected over restored ad-hoc lighting).
        _selectedProfile = s.ProfileName is null ? null : Profiles.FirstOrDefault(p => p.Name == s.ProfileName);
        _dirty = s.Dirty;
        OnChanged(nameof(SelectedProfile));
        OnChanged(nameof(IsStartupProfile));   // direct field write bypasses the setter
        SyncWheelToSelection();
        // The screen goes back with the LEDs: the snapshot is the whole desk.
        if (!suppressed && s.Screen != null) Lcd.RestoreDesign(s.Screen);
        // Wake the pump LCD back up (LightsOff blanked it during sleep/lock).
        if (!suppressed) SetPumpLcdOn(true);
        // A restore is what the SDK bridge does when the LAST client lets go
        // (and what a rescan does when every claim dies at once), so it is
        // also the moment a "controlled by another app" badge should clear.
        // The automation calls this too, at the end of an override, and a
        // client that is genuinely still attached will have painted a frame by
        // then - so IsClaimed puts the badge straight back on the next rebuild.
        _sdkHeld.Clear();
        RefreshDeviceHealth();
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
        // With the bridge up, the default port belongs to the bundled OpenRGB,
        // so take the alternate outright rather than racing it for 6742. With
        // the bridge option on but OpenRGB NOT running (install or launch
        // failed) the ecosystem port is free and ours to use, so SDK clients
        // that expect 6742 still find us. IsServerUp probes honestly here: our
        // previous server is already disposed, so it cannot answer the probe.
        // "Up" OR "being launched": the crash-bisect relaunch loop and the
        // detector-config restart leave the port empty for seconds at a time,
        // and a Rescan the user triggers inside that window must not take it.
        bool bridgeOwnsPort = _store.Settings.UseOpenRgb && (OpenRgbManager.IsServerUp() || OpenRgbManager.Launching);
        server.Start(SdkServerLan, bridgeOwnsPort ? OpenRgbServer.AlternatePort : 0);
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
                UnifiedRgb.Core.Games.GsiConfig.ForgetCs2Folders();
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

        UnifiedRgb.Core.Games.GsiConfig.ForgetCs2Folders();   // the user may have just installed the game
        var written = UnifiedRgb.Core.Games.GsiConfig.Install(
            $"http://localhost:{_gsi.Port}", GsiToken, out string? error);
        OnChanged(nameof(Cs2Status));

        if (written.Count > 0)
            return "Installed to:" + Environment.NewLine
                 + string.Join(Environment.NewLine, written) + Environment.NewLine
                 + Environment.NewLine + "Restart CS2 if it is running.";
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

    /*-----------------------------------------------------*\
    | The desk canvas.                                       |
    \*-----------------------------------------------------*/

    public UnifiedRgb.Core.Effects.CanvasLayout Canvas =>
        UnifiedRgb.Core.Effects.CanvasLayout.Current ??= UnifiedRgb.Core.Effects.CanvasLayout.Load();

    public bool CanvasEnabled
    {
        get => Canvas.Enabled;
        set
        {
            if (Canvas.Enabled == value) return;
            var layout = Canvas;
            layout.Enabled = value;
            if (value) layout.AutoArrange(Devices);
            layout.Save();
            OnChanged(nameof(CanvasEnabled));
            OnChanged(nameof(CanvasStatus));
            // Everything already running was started against the old
            // coordinates, so it is restarted here. Without this the switch
            // would only affect effects applied AFTER it, which is not what
            // "render effects across the desk" says.
            ReapplyEffects();
        }
    }

    public string CanvasStatus => Canvas.Enabled
        ? $"On. Effects render across all {Canvas.Items.Count} device(s) as one image."
        : "Off. Effects render per device, exactly as they always have.";

    /// <summary>Pick up a setup that was just imported over the top of us.
    ///
    /// This is not optional politeness. Everything the import wrote is also
    /// held in memory here, and the next routine save (a slider, a profile, a
    /// window move) would write the pre-import copy straight back over it. So
    /// the rule is: reload immediately, in the same gesture as the import.
    ///
    /// Rebuild cached collections as well as settings pass-throughs, then refresh
    /// the runtime output without taking over an SDK client or relighting a
    /// suppressed setup.</summary>
    public void ReloadAfterImport(UnifiedRgb.App.Services.ImportResult result)
    {
        if (!result.Ok) return;

        if (result.ProfilesChanged || result.SettingsChanged)
        {
            _store.Reload();
            Profiles.Clear();
            foreach (var p in _store.Profiles) Profiles.Add(p);
            // The selection is by reference, and every Profile object was just
            // replaced, so re-find it by name or drop it rather than leaving a
            // dangling one selected.
            string? was = _selectedProfile?.Name;
            _selectedProfile = was == null ? null
                : Profiles.FirstOrDefault(p => p.Name.Equals(was, StringComparison.OrdinalIgnoreCase));
            _dirty = false;
            OnChanged(nameof(SelectedProfile));
            OnChanged(nameof(ProfileNames));
        }

        if (result.CanvasChanged)
        {
            UnifiedRgb.Core.Effects.CanvasLayout.Reload();
            SyncCanvas();
            if (!LightsSuppressed) ReapplyEffects();
        }

        if (result.ScenesChanged) Lcd.ReloadScenes(result.CurrentScreenChanged);
        if (result.ProfilesChanged) Lcd.NotifyProfilesChanged();
        if (result.ProfilesChanged || result.SettingsChanged)
        {
            AutoRules.Clear();
            foreach (var rule in _store.Settings.AutomationRules ?? new()) AutoRules.Add(rule);
            Schedules.Clear();
            foreach (var rule in _store.Settings.Schedules ?? new()) Schedules.Add(rule);
            SensorRules.Clear();
            foreach (var rule in _store.Settings.SensorRules ?? new()) SensorRules.Add(rule);
            CustomColors.Clear();
            foreach (var hex in _store.Settings.CustomColors ?? Array.Empty<string>())
                if (Rgb.TryFromHex(hex, out var color)) CustomColors.Add(color);
            _favorites = new HashSet<string>((IEnumerable<string>?)_store.Settings.FavoriteEffects ?? DefaultFavorites,
                StringComparer.OrdinalIgnoreCase);
            RefreshVisibleEffects();
            UnifiedRgb.Core.Master.Brightness = _store.Settings.MasterBrightness;
        }
        if (result.CalibrationChanged) UnifiedRgb.Core.Calibration.Reload();
        if (result.SettingsChanged || result.CalibrationChanged) RefreshCalibrationLighting();

        // Null means "all of them" to WPF, which is exactly what a wholesale
        // settings replacement needs.
        OnChanged(null);
        UnifiedRgb.Core.Log.Info("import", $"reloaded after import: {string.Join(", ", result.Applied)}");
    }

    void SyncCanvas()
    {
        var layout = UnifiedRgb.Core.Effects.CanvasLayout.Current;
        if (layout is not { Enabled: true }) return;
        int before = layout.Items.Count;
        layout.AutoArrange(Devices);
        if (layout.Items.Count != before) layout.Save();
    }

    /// <summary>What a device is showing now, for the desk editor's LED dots.</summary>
    public Rgb[] ComposedFrameFor(IRgbDevice device) => _lighting.ComposedFrame(device);

    /// <summary>Restart every running channel so a layout change takes effect
    /// without the user having to re-pick anything.</summary>
    public void ReapplyEffects()
    {
        var saved = CaptureEffects();
        RestoreEffects(saved);
    }

    /// <summary>Where the automation should land when no rule is matching, or
    /// null to put back the lighting that was on screen when the rule started.
    /// One place decides it, so the service does not have to know the setting.</summary>
    public string? ReturnProfile =>
        _store.Settings.ReturnToStartupProfile && !string.IsNullOrWhiteSpace(_store.Settings.StartupProfile)
            ? _store.Settings.StartupProfile : null;

    public bool ReturnToStartupProfile
    {
        get => _store.Settings.ReturnToStartupProfile;
        set => SetSetting(_store.Settings.ReturnToStartupProfile, value,
                          v => _store.Settings.ReturnToStartupProfile = v);
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

    /// <summary>The live line from the automation watcher. Deliberately NOT
    /// exposed raw any more: every screen asks for its own topic below.
    ///
    /// The "Why the lighting changed" card used to bind this unfiltered, which
    /// made it the one surface that never got the topic treatment. Because
    /// app-rule sentences sit at the bottom of the decision chain - they are what
    /// shows whenever no schedule and no sensor rule is active - that card spent
    /// almost all of its life displaying "this window is focused, switch to
    /// another program to test your rules": testing advice for another feature,
    /// on a card whose job is the history and the pause. The history window is
    /// where "what took over and why" belongs, with timestamps.</summary>
    string _automationStatus = "";

    /*--- One status line, four screens, and only one of them wants any given
          sentence. The app-rule sentences are the fallback in the decision's
          priority chain, so they are what shows whenever no schedule and no
          sensor rule is active: bound raw, the schedules window spent its life
          explaining foreground apps, and "this window is focused, switch to
          another program to test your rules" appeared while the user was
          editing a temperature threshold. Each sentence now carries its topic
          and each screen asks for its own. ---*/
    AutomationTopic _automationTopic = AutomationTopic.None;

    /// <summary>Set the line and what it is about, together: they are one fact
    /// and updating them separately would let a screen match a stale topic
    /// against a new sentence for one notification.</summary>
    public void SetAutomationStatus(string status, AutomationTopic topic)
    {
        if (_automationStatus == status && _automationTopic == topic) return;
        _automationStatus = status;
        _automationTopic = topic;
        OnChanged(nameof(ScheduleStatus));
        OnChanged(nameof(SensorStatus));
        OnChanged(nameof(AppRuleStatus));
    }

    string StatusFor(AutomationTopic topic)
        => _automationTopic == topic || _automationTopic == AutomationTopic.All ? _automationStatus : "";

    /// <summary>The status line, but only when it is about schedules. Empty
    /// otherwise, which collapses the row rather than showing another
    /// feature's news.</summary>
    public string ScheduleStatus => StatusFor(AutomationTopic.Schedule);
    public string SensorStatus => StatusFor(AutomationTopic.Sensor);
    public string AppRuleStatus => StatusFor(AutomationTopic.App);

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
            // Our own SDK server prefers the same port the bridge backend uses.
            // Sitting on 6742 it answered EnsureRunning's "is a server up?" probe
            // and no OpenRGB was ever launched; the Rescan below then stopped
            // our server and the bridge had no backend at all. Move off the port
            // first: SyncSdkServer restarts the server after the Rescan, and with
            // OpenRGB holding 6742 by then it lands on the alternate port.
            if (_sdkServer is { Port: OpenRgbServer.DefaultPort })
            {
                Log.Info("openrgb", $"moving our SDK server off port {OpenRgbServer.DefaultPort} for the bridge backend");
                _sdkServer.Dispose();
                _sdkServer = null;
            }
            bool ok = await Task.Run(() => OpenRgbManager.EnsureRunningAsync(
                s => Application.Current.Dispatcher.Invoke(() => OpenRgbStatus = s)));
            // No backend: bring our SDK server back on its usual port (it was
            // moved aside above, and a Rescan mid-install may have restarted it
            // on the alternate) so a failed bridge does not also cost the user
            // their SDK clients.
            if (!ok) { RestartSdkServer(); return; }

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

using System.IO;
using System.Reflection;
using UnifiedRgb.App;
using UnifiedRgb.App.Services;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Automation;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Invariant probes (2026-10-15 review, worktree inv-hw).       |
|                                                              |
| Rule 1: while the lights are meant to be off, nothing puts    |
|         light on a device, and nothing undoes the black.      |
| Rule 2: every write to a device's persistent memory goes      |
|         through the app's save policy, and that policy never  |
|         stores black / a test colour / a step / a client's.   |
|                                                              |
| Each Check states the INVARIANT, so a failing line is a       |
| demonstrated violation. Fakes only; no hardware is touched.   |
\*-----------------------------------------------------------*/
static class InvariantLightsHwSuite
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly string[] Files = { "profiles.json", "settings.json", "scenes.json", "canvas.json", "lcd.json" };

    public static void Run(Harness t)
    {
        var originals = Files.ToDictionary(n => n, n => File.Exists(AppPaths.Config(n)) ? File.ReadAllBytes(AppPaths.Config(n)) : null);
        double brightness = Master.Brightness;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                NightEditStoresBlackInTheMouse(t);
                NightEditStoresBlackInTheRealG403(t);
                UnlockToStartupProfileLeavesAbsentDeviceBlack(t);
                CalibrationPatchSurvivesTheDark(t);
                IdentifyRelightsInTheDark(t);
                ExitWhileLockedRelightsTheDesk(t);
                SuppressedRescanNeverBlacksNewInstances(t);
                DeskEditorRestartsEffectsInTheDark(t);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                foreach (var (name, bytes) in originals)
                {
                    string path = AppPaths.Config(name);
                    if (bytes == null) { if (File.Exists(path)) File.Delete(path); }
                    else File.WriteAllBytes(path, bytes);
                }
                Master.Brightness = brightness;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure != null) throw new Exception("InvariantProbes failed to run", failure);
    }

    /*---------------- helpers ----------------*/

    /// <summary>Mirrors the OLD LogitechG403 / RazerHid persist: it commits
    /// whatever the device was last SENT (G403: _lastPer, Razer: _last).
    /// (Adjusted for the fix: the interface now hands the driver the colour to
    /// keep - PersistColor(showing) - and the real drivers refuse one they are
    /// not showing. This fake deliberately IGNORES that argument and keeps
    /// committing its last-sent frame, so these probes still prove the app
    /// itself never asks for a save while black is what the device shows.)</summary>
    sealed class ProbePersistDevice : IRgbDevice, IPersistableLighting
    {
        public string Name { get; init; } = "Probe mouse";
        public string Vendor => "Test";
        public DeviceType Type => DeviceType.Mouse;
        public int LedCount { get; init; } = 2;
        public IReadOnlyList<RgbZone> Zones => _zones ??= new[] { new RgbZone { Name = "All", Offset = 0, Count = LedCount } };
        RgbZone[]? _zones;
        readonly object _l = new();
        Rgb[]? _last;
        public readonly List<Rgb[]> Stored = new();
        public Rgb[]? Last { get { lock (_l) return _last?.ToArray(); } }
        public bool SetColors(IReadOnlyList<Rgb> colors) { lock (_l) _last = colors.ToArray(); return true; }
        public bool PersistColor(IReadOnlyList<Rgb> showing)
        {
            lock (_l)
            {
                if (_last == null) return false;
                Stored.Add(_last.ToArray());
                return true;
            }
        }
        public void Dispose() { }
    }

    static void Stop(MainViewModel vm)
    {
        try { vm.Shows.Dispose(); } catch { }
        try { vm.Lcd.Dispose(); } catch { }
        vm.Lighting.StopAndDrain();
        ((LianBakeService)typeof(MainViewModel).GetField("_bake", Instance)!.GetValue(vm)!).Stop();
    }

    static void Tick(AutomationService a) => typeof(AutomationService).GetMethod("Tick", Instance)!.Invoke(a, null);
    static AutomationMode ModeOf(AutomationService a) => (AutomationMode)typeof(AutomationService).GetField("_mode", Instance)!.GetValue(a)!;
    /// <summary>What the 30 s settled-colour timer runs (MainViewModel.PersistSettledLighting).
    /// (Adjusted for the fix: the save now runs on each device's applier lane
    /// under its write gate, so the lanes are drained before anything is read.)</summary>
    static void PersistTimerFires(MainViewModel vm)
    {
        typeof(MainViewModel).GetMethod("PersistSettledLighting", Instance)!.Invoke(vm, null);
        vm.Lighting.Applier.Drain(3000);
    }
    static void Drain(MainViewModel vm, int ms = 3000) => vm.Lighting.Applier.Drain(ms);
    static bool Dark(Rgb[]? f) => f != null && f.All(c => c == Rgb.Black);
    static bool Lit(Rgb[]? f) => f != null && f.Any(c => c != Rgb.Black);

    static void Quiet(MainViewModel vm)
    {
        vm.SettingsData.AppSwitchEnabled = false;
        vm.SettingsData.SensorRulesEnabled = false;
        vm.SettingsData.SdkServerEnabled = false;
        vm.SettingsData.UseOpenRgb = false;
        vm.SettingsData.Cs2Enabled = false;
        vm.Profiles.Clear();
    }

    /*---------------- V1: night-window hand edit -> settled save stores black ----------------*/

    static void NightEditStoresBlackInTheMouse(Harness t)
    {
        t.Section("INV-1: a hand edit inside a dark window clears the suppression without relighting; the settled save then stores BLACK");
        var vm = new MainViewModel(startServices: false);
        try
        {
            Quiet(vm);
            var kbd = new FakeDevice { Name = "INV1 keyboard", LedCount = 2 };
            var mouse = new ProbePersistDevice { Name = "INV1 mouse" };
            vm.Devices.Add(kbd); vm.Devices.Add(mouse);
            vm.SettingsData.LockLightsOff = true;
            vm.SettingsData.Schedules = new() { new ScheduleRule { Start = "23:00", End = "07:00", Days = 0x7F } };

            // The evening: the mouse is red, set by hand.
            vm.SelectedDevice = mouse; vm.Hex = "FF0000";
            Drain(vm);
            t.Check(Lit(mouse.Last), "precondition: the mouse shows its colour");

            using var automation = new AutomationService(vm, false, () => false);
            var monday = new DateTime(2026, 9, 7);
            DateTime now = monday.AddHours(22);
            automation.WallClock = () => now;
            Tick(automation);
            now = monday.AddHours(23.5);
            Tick(automation);
            Drain(vm);
            t.Equal(AutomationMode.ScheduleOff, ModeOf(automation), "precondition: the night window is dark");
            t.Check(Dark(mouse.Last), "precondition: lights-off blacked the mouse");

            // Still at the desk: one keyboard colour.
            vm.SelectedDevice = kbd; vm.Hex = "00FF00";
            Drain(vm);
            Tick(automation);
            Drain(vm);
            t.Check(!vm.LightsSuppressed, "precondition: the override cleared LightsSuppressed (mode " + ModeOf(automation) + ")");

            t.Check(Lit(mouse.Last),
                "INVARIANT (rule 1, the restore side): when the dark is lifted the rest of the desk comes back - " +
                "VIOLATED: only the edited keyboard is lit, the mouse (and every other device) stays on the lights-off black " +
                "with LightsSuppressed already false");

            // 30 s after the keyboard edit the settled-colour timer fires.
            PersistTimerFires(vm);
            t.Check(!mouse.Stored.Any(Dark),
                "INVARIANT (rule 2): the settled-colour save never stores the lights-off black - " +
                "VIOLATED: the settled save committed BLACK (G403: SET_EFFECT persist byte with 000000)");

            // And the morning: the window closes, the mode is already Base, nothing relights.
            now = monday.AddDays(1).AddHours(8);
            Tick(automation);
            Drain(vm);
            t.Check(Lit(mouse.Last),
                "INVARIANT (rule 1, restore side): the end of the dark window brings the desk back - VIOLATED: the mode " +
                "was already Base, so no transition runs and the mouse/board/DRAM stay black into the next day");
        }
        finally { vm.LightsSuppressed = false; Stop(vm); }
    }

    /// <summary>INV-1 again with the REAL LogitechG403 driver over FakeHid, so the
    /// stored bytes are the driver's own: report[16] is SET_EFFECT's persist byte,
    /// report[6..8] the colour.</summary>
    static void NightEditStoresBlackInTheRealG403(Harness t)
    {
        t.Section("INV-1R: same path, real LogitechG403 driver: the persist byte goes out with 000000");
        var vm = new MainViewModel(startServices: false);
        int gap = UnifiedRgb.Core.Devices.LogitechG403.MinFrameGapMs;
        try
        {
            UnifiedRgb.Core.Devices.LogitechG403.MinFrameGapMs = 0;
            Quiet(vm);
            var hid = new FakeHid();
            hid.Accept = (n, report) => { hid.Replies.Enqueue((byte[])report.Clone()); return true; };   // HID++ echo
            var g403 = new UnifiedRgb.Core.Devices.LogitechG403(hid, 0xFF, 14, 0x8070, new byte[] { 1, 1 }, "INV1R G403");
            var kbd = new FakeDevice { Name = "INV1R keyboard", LedCount = 2 };
            vm.Devices.Add(kbd); vm.Devices.Add(g403);
            vm.SettingsData.LockLightsOff = true;
            vm.SettingsData.Schedules = new() { new ScheduleRule { Start = "23:00", End = "07:00", Days = 0x7F } };

            vm.SelectedDevice = g403; vm.Hex = "FF0000";
            Drain(vm);
            PersistTimerFires(vm);   // the evening's settled save: red stored, legitimately
            bool redStored = hid.Writes.Any(w => w.Length > 16 && w[16] == 1 && w[6] == 0xFF);
            t.Check(redStored, "precondition: the evening's red was stored by the settled save");

            using var automation = new AutomationService(vm, false, () => false);
            var monday = new DateTime(2026, 9, 7);
            DateTime now = monday.AddHours(23.5);
            automation.WallClock = () => now;
            Tick(automation);
            Drain(vm);
            t.Equal(AutomationMode.ScheduleOff, ModeOf(automation), "precondition: dark");

            vm.SelectedDevice = kbd; vm.Hex = "00FF00";
            Drain(vm);
            Tick(automation);
            Drain(vm);
            int before = hid.Writes.Count;
            PersistTimerFires(vm);   // 30 s after the keyboard edit
            bool blackStored = hid.Writes.Skip(before).Any(w => w.Length > 16 && w[16] == 1 && w[6] == 0 && w[7] == 0 && w[8] == 0);
            t.Check(!blackStored,
                "INVARIANT (rule 2): the G403 never gets black committed to its onboard memory by the settled save - VIOLATED: " +
                "SET_EFFECT with persist=1 and colour 000000 went out (the mouse now powers up dark without the app)");
        }
        finally
        {
            UnifiedRgb.Core.Devices.LogitechG403.MinFrameGapMs = gap;
            vm.LightsSuppressed = false; Stop(vm);
        }
    }

    /*---------------- V2: unlock to a startup profile that lacks a device ----------------*/

    static void UnlockToStartupProfileLeavesAbsentDeviceBlack(Harness t)
    {
        t.Section("INV-2: unlock -> startup profile that does not name a device: it stays black and the settled save stores black");
        var vm = new MainViewModel(startServices: false);
        try
        {
            Quiet(vm);
            var kbd = new FakeDevice { Name = "INV2 keyboard", LedCount = 2 };
            var mouse = new ProbePersistDevice { Name = "INV2 mouse" };
            vm.Devices.Add(kbd); vm.Devices.Add(mouse);
            // Saved before the mouse was there (or while G HUB held it): no entry for it.
            var startup = new Profile { Name = "INV2 startup", DeviceFrames = new() { [kbd.Name] = new[] { "0000FF", "0000FF" } } };
            vm.Profiles.Add(startup);
            vm.SettingsData.StartupProfile = startup.Name;
            vm.SettingsData.ReturnToStartupProfile = true;   // the shipped default
            vm.SettingsData.LockLightsOff = true;
            // (Adjusted for the V1 fix: the way back goes to the startup profile
            // only when a RULE ran since Base - a bare lock/unlock now restores
            // what was on, mouse included. A scheduled profile window that
            // ends while the session is locked is that rule; its profile does
            // not name the mouse either.)
            var evening = new Profile { Name = "INV2 evening", DeviceFrames = new() { [kbd.Name] = new[] { "00FF00", "00FF00" } } };
            vm.Profiles.Add(evening);
            vm.SettingsData.Schedules = new()
                { new ScheduleRule { Start = "18:00", End = "20:00", Days = 0x7F, Action = ScheduleAction.Profile, Profile = evening.Name } };

            vm.SelectedDevice = mouse; vm.Hex = "FF0000";
            Drain(vm);

            bool? locked = false;
            using var automation = new AutomationService(vm, false, () => locked);
            var monday = new DateTime(2026, 9, 7);
            DateTime now = monday.AddHours(17);
            automation.WallClock = () => now;
            Tick(automation);
            now = monday.AddHours(18.5);
            Tick(automation);
            Drain(vm);
            t.Equal(AutomationMode.ScheduleProfile, ModeOf(automation), "precondition: a rule applied its profile");
            locked = true;
            Tick(automation);
            Drain(vm);
            t.Equal(AutomationMode.Locked, ModeOf(automation), "precondition: locked");
            t.Check(Dark(mouse.Last), "precondition: lights-off blacked the mouse");
            now = monday.AddHours(20.5);   // the window ended while locked
            locked = false;
            Tick(automation);
            Drain(vm);
            t.Equal(AutomationMode.Base, ModeOf(automation), "precondition: unlocked to Base");
            t.Equal(startup.Name, vm.AppliedProfileName, "precondition: the startup profile was the return");

            t.Check(Lit(mouse.Last),
                "INVARIANT (rule 1, restore side): the unlock relights every device - VIOLATED: LoadProfile skips a device " +
                "the profile does not name, so it keeps the lights-off black");
            PersistTimerFires(vm);
            t.Check(!mouse.Stored.Any(Dark),
                "INVARIANT (rule 2): the settled save never stores the lights-off black - VIOLATED");
        }
        finally { vm.LightsSuppressed = false; Stop(vm); }
    }

    /*---------------- V3: calibration aid in the dark ----------------*/

    static void CalibrationPatchSurvivesTheDark(Harness t)
    {
        t.Section("INV-3: the calibration aid lights reference patches while suppressed, and its restore leaves them lit");
        var vm = new MainViewModel(startServices: false);
        CalibrationAid? aid = null;
        try
        {
            Quiet(vm);
            var dev = new FakeDevice { Name = "INV3 fans", LedCount = 2 };
            vm.Devices.Add(dev);
            vm.SelectedDevice = dev; vm.Hex = "3366FF";
            Drain(vm);

            // The SettingsPane wiring, verbatim.
            MainViewModel.LightState? before = null;
            aid = new CalibrationAid(vm.Lighting,
                capture: () => before = vm.CaptureState(),
                restore: () => { if (before != null) vm.RestoreState(before, honorSuppression: true); before = null; },
                refresh: vm.RefreshCalibrationLighting);
            aid.Start(new[] { dev });
            Drain(vm);
            t.Check(Lit(dev.Last), "precondition: the reference patch is up");

            // 23:00 arrives with the calibration window still open (the automation
            // lets ScheduleOff through a calibration: HeldForCalibration).
            vm.LightsOff();
            Drain(vm);
            t.Check(Dark(dev.Last), "precondition: lights-off blacked it");

            aid.Refresh();   // what every slider move calls
            Drain(vm);
            t.Check(Dark(dev.Last),
                "INVARIANT (rule 1): nothing lights a device while suppressed - VIOLATED: CalibrationAid.Push -> PushReference " +
                "has no suppression check");

            aid.Stop();      // the user closes the calibration window
            Drain(vm);
            t.Check(Dark(dev.Last),
                "INVARIANT (rule 1): closing it in the dark leaves the device dark - VIOLATED: RestoreState(honorSuppression) " +
                "copies the frames but never re-blacks, so the white/grey patch stays lit until the window ends");
        }
        finally { aid?.Stop(); vm.LightsSuppressed = false; Stop(vm); }
    }

    /*---------------- V4: identify in the dark ----------------*/

    static void IdentifyRelightsInTheDark(Harness t)
    {
        t.Section("INV-4: Identify inside a dark window restores the lit frame");
        var vm = new MainViewModel(startServices: false);
        try
        {
            Quiet(vm);
            var dev = new FakeDevice { Name = "INV4 strip", LedCount = 2 };
            vm.Devices.Add(dev);
            vm.SelectedDevice = dev; vm.Hex = "FF8800";
            Drain(vm);
            vm.LightsOff();
            Drain(vm);
            t.Check(Dark(dev.Last), "precondition: dark");
            vm.IdentifyDevice(dev);
            Drain(vm, 4000);
            t.Check(Dark(dev.Last),
                "INVARIANT (rule 1): after the blink a suppressed device goes back to BLACK - VIOLATED: IdentifyDevice restores " +
                "ComposedFrame (the user's colour) and raises no LightingApplied, so it stays lit all night");
        }
        finally { vm.LightsSuppressed = false; Stop(vm); }
    }

    /*---------------- V5: exit / logoff / Windows Update restart while locked ----------------*/

    static void ExitWhileLockedRelightsTheDesk(Harness t)
    {
        t.Section("INV-5: closing the app while locked (logoff, shutdown, update restart) relights the desk");
        var vm = new MainViewModel(startServices: false);
        try
        {
            Quiet(vm);
            var dev = new FakeDevice { Name = "INV5 board", LedCount = 2 };
            vm.Devices.Add(dev);
            vm.SettingsData.LockLightsOff = true;
            vm.SettingsData.Schedules = new();
            vm.SelectedDevice = dev; vm.Hex = "00FFAA";
            Drain(vm);
            bool? locked = false;
            var automation = new AutomationService(vm, false, () => locked);
            Tick(automation);
            locked = true;
            Tick(automation);
            Drain(vm);
            t.Check(Dark(dev.Last), "precondition: locked and dark");
            // MainWindow.Closed: _automation.Dispose() runs first.
            automation.Dispose();
            Drain(vm);
            t.Check(Dark(dev.Last),
                "INVARIANT (rule 1): a locked desk is not relit on the way out - VIOLATED: AutomationService.Dispose restores " +
                "_returnPoint while the session is still locked (effects, LCD, wallpaper too); KeepLast devices keep it after exit");
        }
        finally { vm.LightsSuppressed = false; Stop(vm); }
    }

    /*---------------- V6: a rescan that is not a recovery ----------------*/

    static void SuppressedRescanNeverBlacksNewInstances(Harness t)
    {
        t.Section("INV-6: a rescan while suppressed (not via RecoverDevices) never blacks the new instances");
        var vm = new MainViewModel(startServices: false);
        try
        {
            Quiet(vm);
            var old = new FakeDevice { Name = "INV6 dram", LedCount = 2 };
            vm.Devices.Add(old);
            vm.SelectedDevice = old; vm.Hex = "AA00FF";
            Drain(vm);
            vm.LightsOff();
            Drain(vm);
            var fresh = new FakeDevice { Name = "INV6 dram", LedCount = 2 };
            var arrived = new FakeDevice { Name = "INV6 newly found", LedCount = 2 };
            vm.Redetect = () => new IRgbDevice[] { fresh, arrived };
            vm.RescanCommand.Execute(null);   // the same Rescan the hub-layout watcher, bridge, PawnIO install and channel picker call
            Drain(vm);
            t.Check(vm.LightsSuppressed, "precondition: still suppressed");
            t.Check(Dark(arrived.Last),
                "INVARIANT (rule 1): a device that appears during the dark is put dark - VIOLATED: Rescan only maps frames when " +
                "suppressed (" + arrived.WriteCount + " writes to the new device); only RecoverDevices follows it with LightsOff");
        }
        finally { vm.LightsSuppressed = false; Stop(vm); }
    }

    /*---------------- V7: the desk editor's Commit ----------------*/

    static void DeskEditorRestartsEffectsInTheDark(Harness t)
    {
        t.Section("INV-7: a desk-layout edit (CanvasWindow.Commit -> ReapplyEffects) restarts every effect while suppressed");
        var vm = new MainViewModel(startServices: false);
        try
        {
            Quiet(vm);
            var dev = new FakeDevice { Name = "INV7 fans", LedCount = 4 };
            vm.Devices.Add(dev);
            vm.SelectedDevice = dev;
            vm.SelectedEffectChoice = vm.Effects.First(e => e.Name == "Rainbow Wave");
            t.Equal(1, vm.Lighting.Engine.ChannelsFor(dev).Count, "precondition: an effect runs");
            vm.LightsOff();
            Drain(vm);
            t.Equal(0, vm.Lighting.Engine.ChannelsFor(dev).Count, "precondition: lights-off stopped it");
            vm.ReapplyEffects();   // exactly what CanvasWindow.Commit calls when the desk is enabled
            int running = vm.Lighting.Engine.ChannelsFor(dev).Count;
            vm.Lighting.Engine.StopAll();
            t.Equal(0, running,
                "INVARIANT (rule 1): nothing restarts an effect while suppressed - VIOLATED: CanvasWindow.Commit calls " +
                "ReapplyEffects with no LightsSuppressed guard (the CanvasEnabled setter has one); CaptureEffects still " +
                "sees the channels LightsOff stopped, so every effect comes back and runs all night");
        }
        finally { vm.LightsSuppressed = false; Stop(vm); }
    }
}

using System.IO;
using System.Net.Http;
using System.Reflection;
using UnifiedRgb.App;
using UnifiedRgb.App.Services;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Automation;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Games;
using UnifiedRgb.Core.Native;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The lights-off boundary (10/15 invariant fixes), beyond what |
| the reviewers' probes (InvariantLights*) prove.              |
|                                                              |
|  - while suppressed the write boundary records and writes    |
|    nothing lit; a user's lighting action in a dark WINDOW     |
|    wakes the whole desk, one in a LOCKED session waits for    |
|    the unlock and then comes back whole                      |
|  - every way out of the dark repaints what it did not name   |
|  - startup in the dark, sleep, a refused black               |
|  - the settled save stores only the user's stored colour     |
|  - V13 / V14 / V15 / V16                                     |
|                                                              |
| Fakes and the harness's own config folder only.              |
\*-----------------------------------------------------------*/
static class LightsOffBoundarySuite
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly string[] Files = { "profiles.json", "settings.json", "scenes.json", "canvas.json", "lcd.json", "calibration.json" };

    public static void Run(Harness t)
    {
        if (!AppPaths.ConfigDir.StartsWith(Isolation.Root, StringComparison.OrdinalIgnoreCase))
        {
            t.Check(false, "REFUSING to run: AppPaths is not redirected, and this suite rewrites config files");
            return;
        }
        var originals = Files.ToDictionary(n => n, n => File.Exists(AppPaths.Config(n)) ? File.ReadAllBytes(AppPaths.Config(n)) : null);
        double brightness = Master.Brightness;
        int retryMs = LightingController.BlackRetryMs;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                StartsDarkDecides(t);
                EffectPickedInTheDarkWakesWithTheDesk(t);
                EditWhileLockedComesBackWhole(t);
                UnlockStraightIntoARuleThatNamesTooLittle(t);
                RuleWithNothingToApplyDoesNotHalfWake(t);
                EveryWayOutRepaintsWhatItDidNotName(t);
                StartupInTheDark(t);
                SleepIsALock(t);
                RefusedBlackIsRetried(t);
                SettledSaveStoresOnlyTheStoredColour(t);
                RuleProfileIsNotStored(t);
                EngineWritesNothingInTheDark(t);
                RevertedDeviceIsPutBack(t);
                StandaloneCalibrationStopInTheDark(t);
                ClientReleaseDuringCalibrationWaits(t);
                HeaderTestPutBackInTheDark(t);
                BakeUploadYieldsToAClient(t);
                BatteryReadingOfAReplacedDevice(t);
                GsiStopDuringAParse(t);
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
                LightingController.BlackRetryMs = retryMs;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure != null) throw new Exception("LightsOffBoundary failed to run", failure);
    }

    /*---------------- helpers ----------------*/

    static MainViewModel NewVm()
    {
        foreach (string name in Files)
        {
            try { File.Delete(AppPaths.Config(name)); }
            catch (IOException) { /* a file the previous test still holds open: the suite restores the originals at the end anyway */ }
        }
        var vm = new MainViewModel(startServices: false);
        vm.SettingsData.AppSwitchEnabled = false;
        vm.SettingsData.SensorRulesEnabled = false;
        vm.SettingsData.SdkServerEnabled = false;
        vm.SettingsData.UseOpenRgb = false;
        vm.SettingsData.Cs2Enabled = false;
        vm.SettingsData.Schedules = new();
        vm.Profiles.Clear();
        return vm;
    }

    static void Stop(MainViewModel vm)
    {
        vm.LightsSuppressed = false;
        vm.Shows.Dispose(); vm.Lcd.Dispose(); vm.Lighting.StopAndDrain();
        ((LianBakeService)typeof(MainViewModel).GetField("_bake", Instance)!.GetValue(vm)!).Stop();
    }

    static void Tick(AutomationService a) => typeof(AutomationService).GetMethod("Tick", Instance)!.Invoke(a, null);
    static AutomationMode ModeOf(AutomationService a) => (AutomationMode)typeof(AutomationService).GetField("_mode", Instance)!.GetValue(a)!;
    static void Transition(AutomationService a, AutomationMode mode, string? profile)
        => typeof(AutomationService).GetMethod("Transition", Instance)!.Invoke(a, new object?[] { mode, profile, null });
    static void Drain(MainViewModel vm) => vm.Lighting.Applier.Drain(3000);
    static void PersistTimerFires(MainViewModel vm)
    {
        typeof(MainViewModel).GetMethod("PersistSettledLighting", Instance)!.Invoke(vm, null);
        Drain(vm);
    }
    static bool Dark(Rgb[]? f) => f != null && f.All(c => c == Rgb.Black);
    static bool Is(Rgb[]? f, Rgb c) => f != null && f.All(x => x == c);
    static bool NeverLit(FakeDevice d) { lock (d.Writes) return d.Writes.All(w => Dark(w.Frame)); }
    static Profile Make(string name, params (IRgbDevice Dev, string Hex)[] frames)
    {
        var p = new Profile { Name = name };
        foreach (var (dev, hex) in frames) p.DeviceFrames[dev.Name] = Enumerable.Repeat(hex, dev.LedCount).ToArray();
        return p;
    }

    /// <summary>Stores whatever it was last sent, whatever it is asked to keep:
    /// the OLD driver contract, so only the app's own guard stands between it
    /// and a black in flash.</summary>
    sealed class CarelessPersistDevice : IRgbDevice, IPersistableLighting
    {
        public string Name { get; init; } = "Careless mouse";
        public string Vendor => "Test";
        public DeviceType Type => DeviceType.Mouse;
        public int LedCount => 2;
        public IReadOnlyList<RgbZone> Zones => new[] { new RgbZone { Name = "All", Offset = 0, Count = 2 } };
        readonly object _l = new();
        Rgb[]? _last;
        public readonly List<Rgb[]> Stored = new();
        public Rgb[]? Last { get { lock (_l) return _last?.ToArray(); } }
        public bool SetColors(IReadOnlyList<Rgb> colors) { lock (_l) _last = colors.ToArray(); return true; }
        public bool PersistColor(IReadOnlyList<Rgb> showing)
        {
            lock (_l) { if (_last == null) return false; Stored.Add(_last.ToArray()); return true; }
        }
        public void Dispose() { }
    }

    /*---------------- 10/15 leftovers ----------------*/

    /// <summary>Ryan's call (10/15): the mouse's stored colour is the lighting
    /// HE chose. A game rule's or a schedule's profile is not stored; once he
    /// changes the lighting during the rule it is his again.</summary>
    static void RuleProfileIsNotStored(Harness t)
    {
        t.Section("Settled save: a rule's profile is not stored in the mouse; the user's own lighting is");
        var vm = NewVm();
        try
        {
            var mouse = new CarelessPersistDevice();
            vm.Devices.Add(mouse);
            vm.SelectedDevice = mouse; vm.Hex = "FF0000";
            Drain(vm);
            vm.Profiles.Add(Make("Game", (mouse, "00FF00")));
            using var automation = new AutomationService(vm, false, () => false);
            Tick(automation);
            Drain(vm);
            PersistTimerFires(vm);
            t.Check(mouse.Stored.Count == 1 && Is(mouse.Stored[^1], Rgb.Red), "precondition: the user's red is stored");

            Transition(automation, AutomationMode.App, "Game");
            Drain(vm);
            t.Check(Is(mouse.Last, new Rgb(0, 255, 0)), "precondition: the game rule's green is showing");
            PersistTimerFires(vm);
            t.Equal(1, mouse.Stored.Count, "the rule's green is not stored in the mouse (it would outlive the game)");

            vm.SelectedDevice = mouse; vm.Hex = "0000FF";   // the user changes it during the rule
            Drain(vm);
            PersistTimerFires(vm);
            t.Check(mouse.Stored.Count == 2 && Is(mouse.Stored[^1], new Rgb(0, 0, 255)),
                "a colour the user picks during the rule is theirs, and is stored");

            Transition(automation, AutomationMode.App, "Game");   // a rule repaints over it again
            Drain(vm);
            PersistTimerFires(vm);
            t.Equal(2, mouse.Stored.Count, "a rule painting over it again is not stored either");
            Transition(automation, AutomationMode.Base, null);
            Drain(vm);
            PersistTimerFires(vm);
            t.Check(mouse.Stored.Count == 3 && !Is(mouse.Stored[^1], new Rgb(0, 255, 0)),
                "back at the user's own lighting, the save stores it again");
        }
        finally { Stop(vm); }
    }

    /// <summary>The view model keeps every effect it would start in the dark
    /// pending. The engine now has its own floor under that: a channel running
    /// while the lights are off, however it got started, writes nothing.</summary>
    static void EngineWritesNothingInTheDark(Harness t)
    {
        t.Section("Effect engine: a channel running while the lights are off writes nothing, and resumes after");
        var lighting = new LightingController();
        var dev = new FakeDevice { Name = "Floor kbd", LedCount = 4 };
        try
        {
            lighting.Engine.Start(dev, 0, dev.LedCount, lighting.FrameFor(dev), new RainbowCycle(), 1.0, Rgb.Red);
            t.Check(WaitUntil(() => dev.WriteCount > 3, 2000), "precondition: the effect is streaming");
            lighting.Suppressed = true;
            t.Check(lighting.Engine.LightsOff, "the controller's lights-off reaches the engine");
            Thread.Sleep(250);   // a frame already past the check lands first
            int after = dev.WriteCount;
            Thread.Sleep(700);
            t.Equal(after, dev.WriteCount, "a running channel writes nothing while the lights are off (was: it streamed over the dark)");
            lighting.Suppressed = false;
            t.Check(!lighting.Engine.LightsOff && WaitUntil(() => dev.WriteCount > after, 2000),
                "...and writes again as soon as they are back, without a restart");
        }
        finally { lighting.Engine.StopAll(); lighting.Applier.Drain(2000); }
    }

    /// <summary>A controller that drops our lighting on its own (an MSI GPU after
    /// a driver reset), and says so when asked.</summary>
    sealed class RevertingDevice : IRgbDevice, ISelfRevertingLighting
    {
        public string Name { get; init; } = "Reverting GPU";
        public string Vendor => "Test";
        public DeviceType Type => DeviceType.Gpu;
        public int LedCount => 1;
        public IReadOnlyList<RgbZone> Zones => new[] { new RgbZone { Name = "GPU", Offset = 0, Count = 1 } };
        public readonly List<Rgb[]> Writes = new();
        public volatile bool Reverted;
        public volatile bool Tells = true;
        public bool CanTell => Tells;
        public int Asked;
        public bool SetColors(IReadOnlyList<Rgb> colors) { lock (Writes) Writes.Add(colors.ToArray()); Reverted = false; return true; }
        public bool? ShowingOurs() { Interlocked.Increment(ref Asked); return !Reverted; }
        public Rgb[]? Last { get { lock (Writes) return Writes.Count == 0 ? null : Writes[^1]; } }
        public int Count { get { lock (Writes) return Writes.Count; } }
        public void Dispose() { }
    }

    static void RevertedDeviceIsPutBack(Harness t)
    {
        t.Section("A device that dropped our lighting on its own is put back: its colour when lit, black when dark");
        var lighting = new LightingController();
        var gpu = new RevertingDevice();
        var plain = new FakeDevice { Name = "Plain", LedCount = 1 };
        try
        {
            lighting.FrameFor(gpu)[0] = new Rgb(0, 128, 255);
            lighting.PushFrame(gpu);
            lighting.PushFrame(plain);
            lighting.Applier.Drain(2000);
            int writes = gpu.Count, plainWrites = plain.WriteCount;

            lighting.RepairReverts(new IRgbDevice[] { gpu, plain });
            lighting.Applier.Drain(2000);
            t.Check(gpu.Asked == 1, "the device is asked whether it still shows ours");
            t.Equal(writes, gpu.Count, "still showing it: nothing is written");
            t.Equal(plainWrites, plain.WriteCount, "a device that cannot revert on its own is left alone");

            gpu.Reverted = true;   // a driver reset: the card went back to its own mode
            lighting.RepairReverts(new IRgbDevice[] { gpu });
            lighting.Applier.Drain(2000);
            var expected = new[] { new Rgb(0, 128, 255) };
            Master.Finish(gpu, expected);   // calibration and master brightness, as every static write
            t.Check(gpu.Count == writes + 1 && gpu.Last![0] == expected[0],
                "reverted while lit: its stored colour is written back (was: the card's own mode until the next change)");

            lighting.Suppressed = true;
            lighting.PushBlack(gpu);
            lighting.Applier.Drain(2000);
            gpu.Reverted = true;   // ...and again during the dark window
            int dark = gpu.Count;
            lighting.RepairReverts(new IRgbDevice[] { gpu });
            lighting.Applier.Drain(2000);
            t.Check(gpu.Count == dark + 1 && gpu.Last![0] == Rgb.Black,
                "reverted while the lights are off: black is written back (was: the card lit up and stayed lit all night)");

            // A card that cannot report its state (Ryan's 5090 reads its mode
            // back as 0x00): the minute check leaves it alone, a display
            // change writes it again without asking.
            lighting.Suppressed = false;
            lighting.PushFrame(gpu);
            lighting.Applier.Drain(2000);
            gpu.Tells = false;
            int asked = gpu.Asked, before = gpu.Count;
            lighting.RepairReverts(new IRgbDevice[] { gpu });
            lighting.Applier.Drain(2000);
            t.Check(gpu.Asked == asked && gpu.Count == before, "a card that cannot tell is not asked, nor written, by the minute check");
            lighting.RepairReverts(new IRgbDevice[] { gpu }, likelyReset: true);
            lighting.Applier.Drain(2000);
            t.Check(gpu.Asked == asked && gpu.Count == before + 1 && gpu.Last![0] == expected[0],
                "...and on a display change its stored colour is simply written again"); 
        }
        finally { lighting.Suppressed = false; lighting.StopAndDrain(); }
    }

    /*---------------- P1-3: the decision made before the first write ----------------*/

    static void StartsDarkDecides(Harness t)
    {
        t.Section("P1-3: the desk starts dark in a locked session or an open dark window, and only then");
        var s = new SettingsData { LockLightsOff = true, Schedules = new() };
        var monday = new DateTime(2026, 9, 7);
        t.Check(AutomationService.StartsDark(s, monday.AddHours(12), locked: true, idleSeconds: 0), "locked, with lights-off-when-locked on: dark");
        t.Check(!AutomationService.StartsDark(s, monday.AddHours(12), locked: false, idleSeconds: 0), "unlocked, no window: lit");
        t.Check(!AutomationService.StartsDark(s, monday.AddHours(12), locked: null, idleSeconds: 0), "Windows will not say: lit (as the tick would)");
        s.LockLightsOff = false;
        t.Check(!AutomationService.StartsDark(s, monday.AddHours(12), locked: true, idleSeconds: 0), "locked, but the setting is off: lit");
        s.Schedules = new() { new ScheduleRule { Start = "23:00", End = "07:00", Days = 0x7F } };
        t.Check(AutomationService.StartsDark(s, monday.AddDays(1).AddHours(3), locked: false, idleSeconds: 0),
            "03:00 inside a 23:00-07:00 lights-off window: dark (the overnight update restart)");
        t.Check(!AutomationService.StartsDark(s, monday.AddHours(12), locked: false, idleSeconds: 0), "the same window, closed: lit");
        s.Schedules[0].IdleOnly = true;
        t.Check(!AutomationService.StartsDark(s, monday.AddDays(1).AddHours(3), locked: false, idleSeconds: 30),
            "an 'only when I'm away' window with someone at the PC: lit, as the tick decides");
        t.Check(AutomationService.StartsDark(s, monday.AddDays(1).AddHours(3), locked: false, idleSeconds: 3600),
            "...and dark once the PC has been idle");
        s.Schedules[0].IdleOnly = false;
        s.Schedules[0].Enabled = false;
        t.Check(!AutomationService.StartsDark(s, monday.AddDays(1).AddHours(3), locked: false, idleSeconds: 0), "a disabled window: lit");
        s.Schedules = new() { new ScheduleRule { Start = "23:00", End = "07:00", Days = 0x7F, Action = ScheduleAction.Profile, Profile = "x" } };
        t.Check(!AutomationService.StartsDark(s, monday.AddDays(1).AddHours(3), locked: false, idleSeconds: 0), "a PROFILE window is not a dark one");
    }

    /*---------------- root cause 1: an effect picked in a dark window ----------------*/

    static void EffectPickedInTheDarkWakesWithTheDesk(Harness t)
    {
        t.Section("Rule: a lighting action in a dark window wakes the WHOLE desk, the action included");
        var vm = NewVm();
        try
        {
            var kbd = new FakeDevice { Name = "Wake kbd", LedCount = 4 };
            var fans = new FakeDevice { Name = "Wake fans", LedCount = 2 };
            vm.Devices.Add(kbd); vm.Devices.Add(fans);
            vm.SettingsData.Schedules = new() { new ScheduleRule { Start = "23:00", End = "07:00", Days = 0x7F } };
            vm.SelectedDevice = fans; vm.Hex = "123456";
            Drain(vm);
            using var automation = new AutomationService(vm, false, () => false);
            var monday = new DateTime(2026, 9, 7);
            DateTime now = monday.AddHours(23.5);
            automation.WallClock = () => now;
            Tick(automation);
            Drain(vm);
            t.Equal(AutomationMode.ScheduleOff, ModeOf(automation), "precondition: the window is dark");

            int kbdWrites = kbd.WriteCount;
            vm.SelectedDevice = kbd;
            vm.SelectedEffectChoice = vm.Effects.First(e => e.Name == "Rainbow Wave");
            Drain(vm);
            t.Equal(0, vm.Lighting.Engine.ChannelsFor(kbd).Count,
                "while the lights are off the effect is recorded, not started (was: it ran over the dark desk)");
            t.Check(kbd.WriteCount == kbdWrites || Dark(kbd.Last), "...and nothing lit reached the keyboard");
            t.Check(vm.LightsSuppressed, "...the edit alone does not lift the dark: the wake does");

            Tick(automation);   // the wake the edit posted (the harness thread pumps no dispatcher)
            Drain(vm);
            t.Equal(AutomationMode.Base, ModeOf(automation), "the edit woke the desk");
            t.Check(!vm.LightsSuppressed && !vm.NightLightsOff, "...the dark is lifted and the banner gone");
            t.Equal(1, vm.Lighting.Engine.ChannelsFor(kbd).Count, "the effect picked in the dark runs now (was: lost from the capture)");
            t.Check(Is(fans.Last, Rgb.FromHex("123456")), "and the device nobody touched is relit with its own colour");
        }
        finally { Stop(vm); }
    }

    /*---------------- root cause 1: locked ----------------*/

    static void EditWhileLockedComesBackWhole(Harness t)
    {
        t.Section("Rule: a lighting action while LOCKED waits for the unlock, which brings back all of it");
        var vm = NewVm();
        try
        {
            var kbd = new FakeDevice { Name = "Locked kbd", LedCount = 2 };
            var fans = new FakeDevice { Name = "Locked fans", LedCount = 2 };
            vm.Devices.Add(kbd); vm.Devices.Add(fans);
            vm.SettingsData.LockLightsOff = true;
            vm.SelectedDevice = fans; vm.Hex = "102030";
            Drain(vm);
            bool? locked = false;
            using var automation = new AutomationService(vm, false, () => locked);
            Tick(automation);
            locked = true; Tick(automation); Drain(vm);
            t.Check(Dark(kbd.Last) && Dark(fans.Last), "precondition: the lock blacked the desk");

            vm.SelectedDevice = kbd; vm.Hex = "00FF00";   // a hotkey, an SDK-less script: anything that edits
            Drain(vm);
            t.Check(Dark(kbd.Last), "the edit does not light the device while locked (was: it lit just that one)");
            t.Check(vm.LightsSuppressed && ModeOf(automation) == AutomationMode.Locked, "...and the lock still holds");

            locked = false; Tick(automation); Drain(vm);
            t.Check(Is(kbd.Last, Rgb.FromHex("00FF00")), "the unlock shows the edit");
            t.Check(Is(fans.Last, Rgb.FromHex("102030")), "...and relights the rest of the desk (was: left on the black)");
            t.Check(!vm.LightsSuppressed, "...with the dark lifted");
        }
        finally { Stop(vm); }
    }

    /*---------------- INV-2: a rule's profile that names too little ----------------*/

    static void UnlockStraightIntoARuleThatNamesTooLittle(Harness t)
    {
        t.Section("INV-2: unlocking straight into a rule whose profile does not name a device relights that device");
        var vm = NewVm();
        try
        {
            var kbd = new FakeDevice { Name = "Rule kbd", LedCount = 2 };
            var mouse = new FakeDevice { Name = "Rule mouse", LedCount = 2 };
            var spinning = new FakeDevice { Name = "Rule spinning fans", LedCount = 3 };
            vm.Devices.Add(kbd); vm.Devices.Add(mouse); vm.Devices.Add(spinning);
            var evening = Make("Rule evening", (kbd, "0000FF"));
            vm.Profiles.Add(evening);
            vm.SettingsData.LockLightsOff = true;
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
            locked = true; Tick(automation); Drain(vm);
            t.Check(Dark(mouse.Last), "precondition: dark");
            now = monday.AddHours(18.5);   // the rule's window opened while locked
            locked = false; Tick(automation); Drain(vm);
            t.Equal(AutomationMode.ScheduleProfile, ModeOf(automation), "precondition: the unlock lands in the rule");
            t.Check(Is(kbd.Last, Rgb.Blue), "the rule's profile is on the keyboard");
            t.Check(Is(mouse.Last, Rgb.Red), "the mouse it does not name has its own colour back (was: black for good)");

            t.Section("INV-2: an effect a profile does not name stops on statics, not on its last frame");
            Transition(automation, AutomationMode.Base, null);
            vm.SelectedDevice = spinning;
            System.Array.Fill(vm.Lighting.FrameFor(spinning), new Rgb(1, 2, 3));
            vm.SelectedEffectChoice = vm.Effects.First(e => e.Name == "Rainbow Wave");
            t.Check(WaitUntil(() => spinning.Last is { } f && f.Any(c => c != new Rgb(1, 2, 3)), 3000), "precondition: the fans animate");
            vm.ApplyProfile(evening);   // names only the keyboard, carries no effect
            Drain(vm);
            Thread.Sleep(50);
            t.Equal(0, vm.Lighting.Engine.ChannelsFor(spinning).Count, "precondition: the profile stopped the effect");
            t.Check(Is(spinning.Last, new Rgb(1, 2, 3)), "...and the fans show their stored colour, not a frozen effect frame");
        }
        finally { Stop(vm); }
    }

    static void RuleWithNothingToApplyDoesNotHalfWake(Harness t)
    {
        t.Section("INV-2: a rule asking for a deleted profile, out of the dark, restores - it does not half-wake");
        var vm = NewVm();
        try
        {
            var dev = new FakeDevice { Name = "Half-wake desk", LedCount = 2 };
            vm.Devices.Add(dev);
            vm.SelectedDevice = dev; vm.Hex = "00AA00";
            Drain(vm);
            using var automation = new AutomationService(vm, monitor: false);
            Transition(automation, AutomationMode.Locked, null);
            Drain(vm);
            t.Check(Dark(dev.Last), "precondition: dark");
            Transition(automation, AutomationMode.App, "Deleted long ago");
            Drain(vm);
            t.Check(!vm.LightsSuppressed, "the dark is lifted");
            t.Check(Is(dev.Last, new Rgb(0, 0xAA, 0)), "...and the lighting from before it is back (was: black LEDs under a lit LCD)");
        }
        finally { Stop(vm); }
    }

    /*---------------- root cause 2: the backstop ----------------*/

    static void EveryWayOutRepaintsWhatItDidNotName(Harness t)
    {
        t.Section("Root cause 2: a restore relights a device its snapshot does not name");
        var vm = NewVm();
        try
        {
            var a = new FakeDevice { Name = "Leftover A", LedCount = 2 };
            vm.Devices.Add(a);
            vm.SelectedDevice = a; vm.Hex = "AA0000";
            Drain(vm);
            var snapshot = vm.CaptureState();   // names A only
            vm.LightsOff();
            Drain(vm);
            // Turned up during the dark and was put dark, as a rescan does.
            var b = new FakeDevice { Name = "Leftover B", LedCount = 2 };
            vm.Devices.Add(b);
            System.Array.Fill(vm.Lighting.FrameFor(b), new Rgb(0, 0, 0xBB));
            vm.Lighting.PushBlack(b);
            Drain(vm);
            vm.LightsSuppressed = false;
            vm.RestoreState(snapshot);
            Drain(vm);
            t.Check(Is(a.Last, new Rgb(0xAA, 0, 0)), "the snapshot's device is restored");
            t.Check(Is(b.Last, new Rgb(0, 0, 0xBB)), "...and the one it does not name gets its stored colour, not the black");

            t.Section("Root cause 2: RelightLeftovers repaints exactly what is still dark, and starts what the dark kept pending");
            var c = new FakeDevice { Name = "Leftover fans", LedCount = 3 };
            vm.Devices.Add(c);
            vm.SelectedDevice = c;
            vm.SelectedEffectChoice = vm.Effects.First(e => e.Name == "Rainbow Wave");
            vm.LightsOff();
            Drain(vm);
            vm.LightsSuppressed = false;   // a way out of the dark that paints nothing
            Drain(vm);
            t.Check(Dark(a.Last) && vm.Lighting.Engine.ChannelsFor(c).Count == 0,
                "precondition: lifting the suppression on its own repaints nothing");
            vm.RelightLeftovers();
            Drain(vm);
            t.Check(Is(a.Last, new Rgb(0xAA, 0, 0)) && Is(b.Last, new Rgb(0, 0, 0xBB)), "every dark device has its colour back");
            t.Equal(1, vm.Lighting.Engine.ChannelsFor(c).Count, "...and the effect the dark kept pending runs");
            int writes = a.WriteCount;
            vm.RelightLeftovers();
            Drain(vm);
            t.Equal(writes, a.WriteCount, "nothing left dark: a second call writes nothing");
        }
        finally { Stop(vm); }
    }

    /*---------------- P1-3: startup ----------------*/

    static void StartupInTheDark(Harness t)
    {
        t.Section("P1-3: a launch in a locked session writes nothing lit, and the unlock brings the startup profile up whole");
        var vm = NewVm();
        try
        {
            var kbd = new FakeDevice { Name = "Boot kbd", LedCount = 3 };
            var fans = new FakeDevice { Name = "Boot fans", LedCount = 2 };
            vm.Redetect = () => new IRgbDevice[] { kbd, fans };
            var startup = Make("Boot startup", (kbd, "FF00FF"), (fans, "00FFFF"));
            startup.Effects = new() { new EffectAssignment { Device = kbd.Name, Offset = 0, Count = 3, Effect = "Rainbow Wave", BaseColor = "FFFFFF" } };
            vm.Profiles.Add(startup);
            vm.SettingsData.StartupProfile = startup.Name;
            vm.SettingsData.LockLightsOff = true;

            vm.LightsSuppressed = true;   // what the constructor does when StartsDark says so
            vm.StartDesk();
            Drain(vm);
            t.Check(NeverLit(kbd) && NeverLit(fans), "nothing lit reached any device (was: the startup profile lit the desk for 2 s+)");
            t.Check(Dark(kbd.Last) && Dark(fans.Last), "...every device was put dark");
            t.Equal(0, vm.Lighting.Engine.ChannelsFor(kbd).Count, "...and the profile's effect waits");
            t.Equal(startup.Name, vm.AppliedProfileName, "the startup profile IS applied, as a record");

            bool? locked = true;
            using var automation = new AutomationService(vm, false, () => locked);
            t.Equal(AutomationMode.Locked, ModeOf(automation), "the automation adopts the dark it was born into");
            Tick(automation);
            t.Check(vm.LightsSuppressed && NeverLit(kbd), "still locked: still dark");
            locked = false; Tick(automation); Drain(vm);
            t.Equal(AutomationMode.Base, ModeOf(automation), "the unlock");
            t.Check(Is(fans.Last, Rgb.FromHex("00FFFF")), "brings the startup profile's colours");
            t.Equal(1, vm.Lighting.Engine.ChannelsFor(kbd).Count, "...and its effect");
        }
        finally { Stop(vm); }
    }

    /*---------------- SLEEP ----------------*/

    static void SleepIsALock(Harness t)
    {
        t.Section("SLEEP: going to sleep puts the lights off and holds them off until the resume");
        var vm = NewVm();
        try
        {
            var dev = new FakeDevice { Name = "Sleep desk", LedCount = 2 };
            vm.Devices.Add(dev);
            vm.SettingsData.LockLightsOff = true;
            vm.SelectedDevice = dev; vm.Hex = "0055FF";
            Drain(vm);
            bool? locked = false;
            using var automation = new AutomationService(vm, false, () => locked);
            DateTime now = new DateTime(2026, 9, 7, 22, 0, 0);
            automation.WallClock = () => now;
            Tick(automation);

            automation.Suspending();
            Drain(vm);
            t.Equal(AutomationMode.Locked, ModeOf(automation), "suspend is a lock for the lights");
            t.Check(Dark(dev.Last) && vm.LightsSuppressed, "...the desk goes dark before the PC sleeps (was: only if Windows sent the lock first)");
            now = now.AddSeconds(3);
            Tick(automation);   // a timer tick on the way down, session not (yet) locked
            t.Check(vm.LightsSuppressed && Dark(dev.Last), "a tick before the sleep does not relight it");
            now = now.AddHours(8);
            automation.Resumed();
            Drain(vm);
            t.Equal(AutomationMode.Base, ModeOf(automation), "the resume, no sign-in needed: back to Base");
            t.Check(Is(dev.Last, Rgb.FromHex("0055FF")) && !vm.LightsSuppressed, "...and the desk is relit (a change of mode, not a silent un-suppress)");

            t.Section("SLEEP: a resume into a locked session stays dark until the unlock");
            automation.Suspending(); Drain(vm);
            locked = true;
            automation.Resumed(); Drain(vm);
            t.Check(vm.LightsSuppressed && Dark(dev.Last), "locked after the wake: dark");
            locked = false; Tick(automation); Drain(vm);
            t.Check(Is(dev.Last, Rgb.FromHex("0055FF")), "the unlock relights it");

            t.Section("SLEEP: a resume that never arrives does not keep the desk dark for good");
            automation.Suspending(); Drain(vm);
            now = now.AddMinutes(1);
            Tick(automation);
            t.Check(vm.LightsSuppressed, "a minute on (still on the way down, or asleep): dark");
            now = now.AddMinutes(5);
            Tick(automation); Drain(vm);
            t.Check(!vm.LightsSuppressed && Is(dev.Last, Rgb.FromHex("0055FF")), "past the hold, the next tick decides as usual");

            t.Section("SLEEP: with lights-off-when-away turned off, sleep leaves the lights alone");
            vm.SettingsData.LockLightsOff = false;
            int writes = dev.WriteCount;
            automation.Suspending(); Drain(vm);
            t.Equal(AutomationMode.Base, ModeOf(automation), "no dark mode");
            t.Equal(writes, dev.WriteCount, "...and nothing written");
            automation.Resumed();
        }
        finally { Stop(vm); }
    }

    /*---------------- a refused black ----------------*/

    sealed class RefusesBlack : IRgbDevice
    {
        public string Name { get; init; } = "Refuses black";
        public string Vendor => "Test";
        public DeviceType Type => DeviceType.Dram;
        public int LedCount => 2;
        public IReadOnlyList<RgbZone> Zones => new[] { new RgbZone { Name = "All", Offset = 0, Count = 2 } };
        public int RefuseBlacks = int.MaxValue;
        int _blackAttempts;
        public int BlackAttempts => Volatile.Read(ref _blackAttempts);
        readonly object _l = new();
        Rgb[]? _shown;
        public Rgb[]? Shown { get { lock (_l) return _shown?.ToArray(); } }
        public bool SetColors(IReadOnlyList<Rgb> colors)
        {
            if (colors.All(c => c == Rgb.Black) && Interlocked.Increment(ref _blackAttempts) <= RefuseBlacks) return false;
            lock (_l) _shown = colors.ToArray();
            return true;
        }
        public void Dispose() { }
    }

    static void RefusedBlackIsRetried(Harness t)
    {
        t.Section("Lights-off: a refused black is tried again while the lights are meant to be off");
        LightingController.BlackRetryMs = 20;
        var vm = NewVm();
        try
        {
            // An SMBus held by another tool past the 400 ms must-land window.
            var dram = new RefusesBlack { Name = "Busy DRAM", RefuseBlacks = 20 };
            vm.Devices.Add(dram);
            vm.SelectedDevice = dram; vm.Hex = "FF8800";
            Drain(vm);
            vm.LightsOff();
            Drain(vm);
            t.Check(dram.BlackAttempts >= 1 && dram.Shown is { } lit && lit.Any(c => c != Rgb.Black),
                "precondition: the first must-land window ran out with the device still lit");
            t.Check(WaitUntil(() => Dark(dram.Shown), 5000), "a retry lands the black (was: lit all night after one 400 ms try)");

            t.Section("Lights-off: ...and stops trying the moment the lights come back");
            var stubborn = new RefusesBlack { Name = "Never black" };
            vm.Devices.Add(stubborn);
            vm.LightsOff();
            Drain(vm);
            vm.LightsSuppressed = false;
            int attempts = stubborn.BlackAttempts;
            Thread.Sleep(400);
            Drain(vm);
            t.Equal(attempts, stubborn.BlackAttempts, "no black is sent once the dark is over");
        }
        finally { Stop(vm); }
    }

    /*---------------- root cause 3: the settled save ----------------*/

    static void SettledSaveStoresOnlyTheStoredColour(Harness t)
    {
        t.Section("Root cause 3: the settled save stores the user's colour, and only while the device shows it");
        var vm = NewVm();
        try
        {
            var mouse = new CarelessPersistDevice();
            vm.Devices.Add(mouse);
            vm.SelectedDevice = mouse; vm.Hex = "FF0000";
            Drain(vm);
            PersistTimerFires(vm);
            t.Check(mouse.Stored.Count == 1 && Is(mouse.Stored[^1], Rgb.Red), "precondition: the settled red is stored");

            // Something left the lights-off black on it after the dark was lifted
            // (the INV-1 path): a careless driver would commit the black.
            vm.Lighting.PushBlack(mouse);
            Drain(vm);
            PersistTimerFires(vm);
            t.Equal(1, mouse.Stored.Count, "a device showing black is not saved, whatever its driver would store (was: black in flash)");

            vm.Lighting.PushFrame(mouse);
            Drain(vm);
            PersistTimerFires(vm);
            t.Check(mouse.Stored.Count == 2 && Is(mouse.Stored[^1], Rgb.Red), "relit with its colour: saved again");

            // An exit-behaviour preview is on the device until the window closes.
            vm.ApplyExitBehaviorNow(mouse, new ExitBehavior { Mode = ExitMode.Static, ColorHex = "00FF00" });
            Drain(vm);
            PersistTimerFires(vm);
            t.Equal(2, mouse.Stored.Count, "not while a preview is showing");
            vm.EndExitPreview();
            Drain(vm);
            PersistTimerFires(vm);
            t.Check(mouse.Stored.Count == 3 && Is(mouse.Stored[^1], Rgb.Red), "...and again once it is put back");

            // Identify: the blink's put-back is the stored colour, so a save after it is fine.
            vm.IdentifyDevice(mouse);
            vm.Lighting.Applier.Drain(4000);
            PersistTimerFires(vm);
            t.Check(Is(mouse.Stored[^1], Rgb.Red), "after an identify the stored colour, not the blink's white or black");
        }
        finally { Stop(vm); }
    }

    /*---------------- INV-3: a standalone calibration session ----------------*/

    static void StandaloneCalibrationStopInTheDark(Harness t)
    {
        t.Section("INV-3: a calibration with no restore callback, closed in the dark, leaves the device dark");
        var vm = NewVm();
        CalibrationAid? aid = null;
        try
        {
            var dev = new FakeDevice { Name = "Standalone cal", LedCount = 2 };
            vm.Devices.Add(dev);
            vm.SelectedDevice = dev; vm.Hex = "3366FF";
            Drain(vm);
            aid = new CalibrationAid(vm.Lighting);
            aid.Start(new IRgbDevice[] { dev });
            Drain(vm);
            vm.LightsOff();
            Drain(vm);
            aid.Next();
            Drain(vm);
            t.Check(Dark(dev.Last), "a patch asked for after the dark began is not shown");
            aid.Stop();
            Drain(vm);
            t.Check(Dark(dev.Last), "closing it puts the black back, not the statics (a refused PushFrame left the patch up)");
        }
        finally { aid?.Stop(); Stop(vm); }
    }

    /*---------------- V13 ----------------*/

    static void ClientReleaseDuringCalibrationWaits(Harness t)
    {
        t.Section("V13: an SDK client letting go during a calibration does not repaint under the aid");
        var vm = NewVm();
        CalibrationAid? aid = null;
        try
        {
            var dev = new FakeDevice { Name = "V13 fans", LedCount = 2 };
            vm.Devices.Add(dev);
            vm.SelectedDevice = dev;
            vm.SelectedEffectChoice = vm.Effects.First(e => e.Name == "Breathing");
            vm.StopEffectsOn(dev);                                  // a client takes it
            vm.Lighting.PushExternalFrame(dev, 0, new[] { Rgb.Blue, Rgb.Blue });
            Drain(vm);
            MainViewModel.LightState? before = null;
            aid = new CalibrationAid(vm.Lighting,
                capture: () => before = vm.CaptureState(),
                restore: () => { if (before != null) vm.RestoreState(before, honorSuppression: true); before = null; },
                refresh: vm.RefreshCalibrationLighting);
            aid.Start(new IRgbDevice[] { dev });
            Drain(vm);
            var patch = dev.Last!.ToArray();

            // OpenRgbHost.EndExternal's hop, verbatim.
            vm.Lighting.ForgetExternal(dev);
            vm.ReleaseHold(dev);
            vm.RestoreDevice(dev);
            Drain(vm);
            t.Check(dev.Last!.SequenceEqual(patch), "the reference patch stays up (was: the statics were pushed over it)");
            t.Equal(0, vm.Lighting.Engine.ChannelsFor(dev).Count, "...and no effect started under it");

            aid.Stop();
            Drain(vm);
            t.Equal(1, vm.Lighting.Engine.ChannelsFor(dev).Count, "the aid's own restore brings the device's effect back");
        }
        finally { aid?.Stop(); Stop(vm); }
    }

    /*---------------- V11: header test ----------------*/

    static void HeaderTestPutBackInTheDark(Harness t)
    {
        t.Section("V11: undoing a header test decides at the write: black in the dark, the colours when lit");
        var vm = NewVm();
        var hid = new FakeHid();
        var board = new GigabyteIt5711(hid, 0x5711);
        try
        {
            vm.Devices.Add(board);
            System.Array.Fill(vm.Lighting.FrameFor(board), new Rgb(10, 20, 30));
            vm.Lighting.PushFrame(board);
            Drain(vm);
            vm.LightsOff();
            Drain(vm);
            vm.TestHeader(0, 4);
            Drain(vm);
            int writes = hid.Writes.Count + hid.Features.Count;
            vm.EndHeaderTest();
            Drain(vm);
            t.Check(hid.Writes.Count + hid.Features.Count > writes, "the tested header is written again");
            t.Check(vm.Lighting.IsBlackedOut(board), "...with the black, in the dark (was: the stored colours, all night)");
            vm.LightsSuppressed = false;
            vm.RelightLeftovers();
            Drain(vm);
            vm.TestHeader(0, 4);
            vm.EndHeaderTest();
            Drain(vm);
            t.Check(!vm.Lighting.IsBlackedOut(board), "lit: the board's colours go back");
        }
        finally { Stop(vm); board.Dispose(); }
    }

    /*---------------- V14 ----------------*/

    sealed class Transmitter : IUsbWriter
    {
        public byte BulkOutPipe => 1;
        readonly List<byte[]> _writes = new();
        public bool Write(byte pipe, byte[] buffer) { lock (_writes) _writes.Add((byte[])buffer.Clone()); return true; }
        /// <summary>Meta packets of an animation upload (see FixDrivers).</summary>
        public int AnimationMetas()
        {
            lock (_writes)
                return _writes.Count(p => p[1] == 0 && p[4] == 0x12 && p[5] == 0x20 && p[22] == 0 && (p[29] << 8 | p[30]) > 1);
        }
        public void Dispose() { }
    }

    static void BakeUploadYieldsToAClient(Harness t)
    {
        t.Section("V14: a wireless-fan bake in flight does not answer an SDK client's takeover with its animation");
        var instance = typeof(LianLiWireless).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        object? prior = instance.GetValue(null);
        var usb = new Transmitter();
        var fans = new LianLiWireless(usb, "V14", new byte[] { 9, 2, 3, 4, 5, 6 }, new byte[] { 8, 2, 3, 4, 5, 6 }, 8, 1, 1) { ReceiverOpener = () => null };
        var lighting = new LightingController();
        try
        {
            var bake = new LianBakeService(lighting, () => new[] { fans });
            var rebake = typeof(LianBakeService).GetMethod("Rebake", Instance)!;
            fans.SuppressStreaming = true;
            lighting.Engine.Start(fans, 0, fans.LedCount, lighting.FrameFor(fans), new Breathing(), 1.0, Rgb.Red);

            // The upload is held behind the device's write gate (FixDrivers D7);
            // the client takes the device meanwhile.
            var gate = EffectEngine.WriteGateFor(fans);
            Monitor.Enter(gate);
            try
            {
                rebake.Invoke(bake, new object[] { fans, false });
                Thread.Sleep(600);   // rendered, upload queued
                lighting.Hold(fans);
            }
            finally { Monitor.Exit(gate); }
            lighting.Applier.Drain(3000);
            Thread.Sleep(100);
            t.Equal(0, usb.AnimationMetas(), "the queued upload is dropped for a device a client now owns");
            lighting.Unhold(fans);

            // A takeover while the bake is still RENDERING: Cancel abandons it.
            int before = usb.AnimationMetas();
            Monitor.Enter(gate);
            try
            {
                rebake.Invoke(bake, new object[] { fans, false });
                bake.Cancel(fans);
                Thread.Sleep(600);
            }
            finally { Monitor.Exit(gate); }
            lighting.Applier.Drain(3000);
            Thread.Sleep(100);
            t.Equal(before, usb.AnimationMetas(), "a cancelled bake never uploads (StopEffectsOn cancels the device's bake)");
            bake.Stop();
        }
        finally
        {
            lighting.Engine.StopAll();
            lighting.Applier.Drain(3000);
            fans.Dispose();
            instance.SetValue(null, prior);
        }

        t.Section("V14: StopEffectsOn cancels the device's bake");
        string source = SourceOf("src/UnifiedRgb.App/MainViewModel.Settings.cs") ?? "";
        int stop = source.IndexOf("public void StopEffectsOn(", StringComparison.Ordinal);
        int end = stop < 0 ? -1 : source.IndexOf("public void ReleaseHold(", stop, StringComparison.Ordinal);
        t.Check(stop >= 0 && end > stop && source[stop..end].Contains("_bake.Cancel(", StringComparison.Ordinal),
            "the takeover hook cancels the wireless fans' bake in flight");
    }

    /*---------------- V15 ----------------*/

    sealed class SlowBattery : IRgbDevice, IBatteryDevice
    {
        public string Name { get; init; } = "Slow mouse";
        public string Vendor => "Test";
        public DeviceType Type => DeviceType.Mouse;
        public int LedCount => 1;
        public IReadOnlyList<RgbZone> Zones => new[] { new RgbZone { Name = "All", Offset = 0, Count = 1 } };
        public readonly ManualResetEventSlim Reading = new(false), Release = new(false);
        public bool SetColors(IReadOnlyList<Rgb> colors) => true;
        public BatteryReading? ReadBattery()
        {
            Reading.Set();
            Release.Wait(5000);
            return new BatteryReading(55, false);
        }
        public void Dispose() { Reading.Dispose(); Release.Dispose(); }
    }

    static void BatteryReadingOfAReplacedDevice(Harness t)
    {
        t.Section("V15: a battery read that outlives a rescan does not put the replaced device back");
        var applier = new CoalescingApplier();
        var old = new SlowBattery { Name = "V15 mouse" };
        IReadOnlyList<IRgbDevice> list = new IRgbDevice[] { old };
        var monitor = new BatteryMonitor(applier, () => list, () => { });
        try
        {
            monitor.Rescan();                       // first poll: blocks in the read
            t.Check(old.Reading.Wait(3000), "precondition: the read is in progress");
            list = Array.Empty<IRgbDevice>();
            monitor.Rescan();                       // the rescan replaced it meanwhile
            old.Release.Set();
            applier.Drain(3000);
            t.Check(monitor.Of(old) == null, "the old instance's reading is dropped (was: re-added after the rescan cleared it)");
        }
        finally
        {
            old.Release.Set();
            monitor.Stop();
            applier.Drain(3000);
            old.Dispose();
            UnifiedRgb.Core.Sensors.SensorHub.PublishBatteries(Array.Empty<UnifiedRgb.Core.Sensors.SensorHub.BatteryLevel>());
        }
    }

    /*---------------- V16 ----------------*/

    static void GsiStopDuringAParse(Harness t)
    {
        t.Section("V16: a game update parsed while the listener stops does not bring 'connected' back");
        using var gsi = new GsiServer();
        int port = gsi.Start("tok-v16", preferredPort: 27591);
        t.Check(port > 0, "precondition: the listener bound a port");
        if (port == 0) return;
        gsi.AfterParse = () => gsi.Stop();   // Stop lands between the parse and the state write
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string payload = "{\"player\":{\"state\":{\"health\":42}},\"auth\":{\"token\":\"tok-v16\"}}";
        try { http.PostAsync($"http://localhost:{port}/", new StringContent(payload)).GetAwaiter().GetResult(); }
        catch (HttpRequestException) { /* the listener closing under its own reply is an acceptable answer */ }
        Thread.Sleep(200);
        t.Check(!gsi.Connected, "not connected after the stop (was: connected for another ten seconds)");
        t.Check(gsi.State.Health != 42, "...and the parsed state was not written");
    }
}

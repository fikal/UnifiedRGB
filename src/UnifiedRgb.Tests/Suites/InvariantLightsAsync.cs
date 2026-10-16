using System.IO;
using System.Reflection;
using System.Windows.Threading;
using UnifiedRgb.App;
using UnifiedRgb.App.Services;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Invariant review 10/15 (rules 3 and 4): probes that PROVE a  |
| violation. Each check is written as the invariant, so a      |
| FAILING check here is the evidence; a passing one means the  |
| path is already safe.                                        |
|                                                              |
|  P1  a running show's step repaints the calibration aid's    |
|      reference patches (and restarts effects under them)     |
|  P2  the calibration aid's restore honours a dark window by  |
|      writing NOTHING, so a patch pushed during it stays lit  |
|  P3  the identify blink restores the user's colours over a   |
|      dark window                                             |
|  P4  ...and over an SDK client's picture                     |
|  P5  an exit-behaviour preview on a client's device is never |
|      given back to the client                                |
|                                                              |
| Fakes and the harness's own config folder only.              |
\*-----------------------------------------------------------*/
static class InvariantLightsAsyncSuite
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly string[] Files = { "profiles.json", "settings.json", "scenes.json", "canvas.json", "lcd.json", "calibration.json" };

    public static void Run(Harness t)
    {
        if (!AppPaths.ConfigDir.StartsWith(Isolation.Root, StringComparison.OrdinalIgnoreCase))
        {
            t.Check(false, "REFUSING to run: AppPaths is not redirected, and this suite rewrites real config files");
            return;
        }

        var originals = Files.ToDictionary(n => n, n => File.Exists(AppPaths.Config(n)) ? File.ReadAllBytes(AppPaths.Config(n)) : null);
        double brightness = Master.Brightness;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                ShowStepRepaintsCalibrationPatches(t);
                CalibrationPatchOutlivesDarkRestore(t);
                IdentifyRelightsDarkDesk(t);
                IdentifyPaintsOverClient(t);
                ExitPreviewNeverGivenBack(t);
                UnlockReplacesTheDesk(t);
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
        if (failure != null) throw new Exception("InvariantProbes failed", failure);
    }

    /*---------------- P1: show step vs calibration aid ----------------*/

    static CalibrationAid AidLikeSettingsPane(MainViewModel vm)
    {
        // Exactly SettingsPane.Calibration_Click's wiring.
        MainViewModel.LightState? before = null;
        return new CalibrationAid(vm.Lighting,
            capture: () => before = vm.CaptureState(),
            restore: () => { if (before != null) vm.RestoreState(before, honorSuppression: true); before = null; },
            refresh: vm.RefreshCalibrationLighting);
    }

    static void ShowStepRepaintsCalibrationPatches(Harness t)
    {
        t.Section("P1 (rule 4): a show step must not take the calibration aid's devices");
        CleanConfig();
        var vm = new MainViewModel(startServices: false);
        CalibrationAid? aid = null;
        try
        {
            var dev = new FakeDevice { Name = "Probe cal", LedCount = 2 };
            vm.Devices.Add(dev); vm.Profiles.Clear();
            var a = new Profile { Name = "Probe A", DeviceFrames = new() { [dev.Name] = new[] { "FF0000", "FF0000" } } };
            var b = new Profile { Name = "Probe B", DeviceFrames = new() { [dev.Name] = new[] { "00FF00", "00FF00" } } };
            var c = new Profile
            {
                Name = "Probe C",
                DeviceFrames = new() { [dev.Name] = new[] { "0000FF", "0000FF" } },
                Effects = new() { new EffectAssignment { Device = dev.Name, Offset = 0, Count = 2, Effect = "Breathing", BaseColor = "FFFFFF" } },
            };
            vm.Profiles.Add(a); vm.Profiles.Add(b); vm.Profiles.Add(c);
            vm.Lcd.Scenes.Sequences.Clear();
            vm.Lcd.Scenes.Sequences.Add(new SceneSequence
            {
                Name = "Probe show",
                Actions = new() { new SceneAction { Profile = b.Name, DelaySeconds = 60 }, new SceneAction { Profile = c.Name, DelaySeconds = 60 } },
            });
            vm.Shows.Init();
            vm.ApplyProfile(a); vm.Lighting.Applier.Drain(2000);
            t.Check(vm.Shows.Start("Probe show"), "precondition: the show is running");

            aid = AidLikeSettingsPane(vm);
            aid.Start(new IRgbDevice[] { dev });
            vm.Lighting.Applier.Drain(2000);
            var patch = dev.Last!.ToArray();
            t.Check(CalibrationAid.AnyActive && patch[0] != Rgb.Red, $"precondition: the reference patch is up ({patch[0].ToHex()})");

            // The calibration window is modal, but a modal loop pumps the
            // dispatcher, so the show's DispatcherTimer keeps stepping.
            var sequencer = typeof(ShowViewModel).GetField("_sequencer", Flags)!.GetValue(vm.Shows)!;
            var step = typeof(SceneSequencer).GetMethod("Step", Flags)!;
            step.Invoke(sequencer, null);
            vm.Lighting.Applier.Drain(2000);
            t.Check(dev.Last!.SequenceEqual(patch),
                $"the reference patch stays up while the aid owns the device (is #{dev.Last![0].ToHex()}: the show's step painted over it)");

            step.Invoke(sequencer, null);   // the step whose profile carries an effect
            vm.Lighting.Applier.Drain(2000);
            t.Equal(0, vm.Lighting.Engine.ChannelsFor(dev).Count,
                "no effect is started on a device the aid is driving (a worker streams over the patch at 60 fps)");
        }
        finally
        {
            aid?.Stop();   // AnyActive is process-wide
            Stop(vm);
        }
    }

    /*---------------- P2: calibration restore during a dark window ----------------*/

    static void CalibrationPatchOutlivesDarkRestore(Harness t)
    {
        t.Section("P2 (rule 3): closing the calibration aid in a dark window leaves the desk dark");
        CleanConfig();
        var vm = new MainViewModel(startServices: false);
        CalibrationAid? aid = null;
        try
        {
            var dev = new FakeDevice { Name = "Probe dark cal", LedCount = 2 };
            vm.Devices.Add(dev); vm.Profiles.Clear();
            var red = new Profile { Name = "Probe red", DeviceFrames = new() { [dev.Name] = new[] { "FF0000", "FF0000" } } };
            vm.Profiles.Add(red);
            vm.ApplyProfile(red); vm.Lighting.Applier.Drain(2000);

            // (a) the aid is opened while a scheduled dark window is on.
            vm.LightsOff(); vm.Lighting.Applier.Drain(2000);
            t.Check(dev.Last!.All(x => x == Rgb.Black), "precondition: the dark window blacked the device");
            aid = AidLikeSettingsPane(vm);
            aid.Start(new IRgbDevice[] { dev });
            vm.Lighting.Applier.Drain(2000);
            t.Check(dev.Last!.Any(x => x != Rgb.Black), "precondition: the patch is up (the user asked for it)");
            aid.Stop();
            vm.Lighting.Applier.Drain(2000);
            t.Check(dev.Last!.All(x => x == Rgb.Black),
                $"the aid's restore puts the dark window back (is #{dev.Last![0].ToHex()}: the patch stays lit all night, "
                + "and the automation, which never heard of it, stays in ScheduleOff)");

            // (b) the aid was open first; the window began under it (LightsOff
            // blacks the patch); the user then shows the next patch.
            vm.LightsSuppressed = false;
            vm.ApplyProfile(red); vm.Lighting.Applier.Drain(2000);
            aid = AidLikeSettingsPane(vm);
            aid.Start(new IRgbDevice[] { dev });
            vm.LightsOff(); vm.Lighting.Applier.Drain(2000);
            aid.Next();
            vm.Lighting.Applier.Drain(2000);
            aid.Stop();
            vm.Lighting.Applier.Drain(2000);
            t.Check(dev.Last!.All(x => x == Rgb.Black),
                $"a patch shown after the window began is not left lit by the restore (is #{dev.Last![0].ToHex()})");
        }
        finally
        {
            aid?.Stop();
            vm.LightsSuppressed = false;
            Stop(vm);
        }
    }

    /*---------------- P3/P4: the identify blink's put-back ----------------*/

    static void IdentifyRelightsDarkDesk(Harness t)
    {
        t.Section("P3 (rule 3): the identify blink's put-back honours a dark window");
        CleanConfig();
        var vm = new MainViewModel(startServices: false);
        try
        {
            var dev = new FakeDevice { Name = "Probe identify dark", LedCount = 2 };
            vm.Devices.Add(dev); vm.Profiles.Clear();
            var red = new Profile { Name = "Probe red", DeviceFrames = new() { [dev.Name] = new[] { "FF0000", "FF0000" } } };
            vm.Profiles.Add(red);
            vm.ApplyProfile(red); vm.Lighting.Applier.Drain(2000);
            vm.LightsOff(); vm.Lighting.Applier.Drain(2000);
            t.Check(dev.Last!.All(x => x == Rgb.Black), "precondition: the dark window blacked the device");

            vm.IdentifyDevice(dev);   // the desk editor's Identify button
            vm.Lighting.Applier.Drain(4000);
            t.Check(dev.Last!.All(x => x == Rgb.Black),
                $"after the blink the device is dark again (is #{dev.Last![0].ToHex()}: the user's colour, lit for the rest of the window)");
        }
        finally { vm.LightsSuppressed = false; Stop(vm); }
    }

    static void IdentifyPaintsOverClient(Harness t)
    {
        t.Section("P4 (rule 4): the identify blink's put-back gives an SDK client's device back to the client");
        CleanConfig();
        var vm = new MainViewModel(startServices: false);
        try
        {
            vm.SettingsData.SdkServerEnabled = false;
            vm.SettingsData.UseOpenRgb = false;
            var dev = new FakeDevice { Name = "Probe identify client", LedCount = 2 };
            vm.Devices.Add(dev); vm.Profiles.Clear();
            var red = new Profile { Name = "Probe red", DeviceFrames = new() { [dev.Name] = new[] { "FF0000", "FF0000" } } };
            vm.Profiles.Add(red);
            vm.ApplyProfile(red); vm.Lighting.Applier.Drain(2000);

            var host = new OpenRgbHost(vm, vm.Lighting, Dispatcher.CurrentDispatcher);
            host.SetDevices(vm.Devices.ToList());
            host.BeginExternal(dev, 1);   // on the UI thread, so the hop runs inline
            host.PushExternal(dev, 0, new[] { Rgb.Blue, Rgb.Blue });
            vm.Lighting.Applier.Drain(2000);
            var client = dev.Last!.ToArray();
            t.Check(vm.Lighting.IsClientOwned(dev) && client[0] != Rgb.Red, "precondition: a client (Home Assistant, set-once) holds the device");

            vm.IdentifyDevice(dev);
            vm.Lighting.Applier.Drain(4000);
            t.Check(dev.Last!.SequenceEqual(client),
                $"after the blink the client's picture is back (is #{dev.Last![0].ToHex()}: the user's static, under a claim that refuses every static write)");
            host.Shutdown();
        }
        finally { Stop(vm); }
    }

    /*---------------- P5: exit-behaviour preview on a client's device ----------------*/

    sealed class OpsRecorder : IRgbDevice, IHardwareModes
    {
        public string Name { get; init; } = "Ops";
        public string Vendor => "Test";
        public DeviceType Type => DeviceType.Other;
        public int LedCount => 2;
        public IReadOnlyList<RgbZone> Zones => new[] { new RgbZone { Name = "All", Offset = 0, Count = 2 } };
        public HardwareExitCaps ExitCaps => HardwareExitCaps.Static;
        public IReadOnlyList<string> HardwareEffects => Array.Empty<string>();
        readonly object _lock = new();
        readonly List<string> _ops = new();
        public string? LastOp { get { lock (_lock) return _ops.Count == 0 ? null : _ops[^1]; } }
        public bool SetColors(IReadOnlyList<Rgb> colors) { lock (_lock) _ops.Add("frame " + colors[0].ToHex()); return true; }
        public bool SetHardwareStatic(Rgb color) { lock (_lock) _ops.Add("hw-static " + color.ToHex()); return true; }
        public bool SetHardwareEffect(string name, Rgb? color) => false;
        public bool ReturnToHardware() => false;
        public void Dispose() { }
    }

    static void ExitPreviewNeverGivenBack(Harness t)
    {
        t.Section("P5 (rule 4): closing the exit-behaviour window gives a client's device back to the client");
        CleanConfig();
        var vm = new MainViewModel(startServices: false);
        try
        {
            vm.SettingsData.SdkServerEnabled = false;
            vm.SettingsData.UseOpenRgb = false;
            var dev = new OpsRecorder { Name = "Probe exit client" };
            vm.Devices.Add(dev);
            Array.Fill(vm.Lighting.FrameFor(dev), Rgb.Red);
            vm.Lighting.PushFrame(dev);
            vm.Lighting.Applier.Drain(2000);

            var host = new OpenRgbHost(vm, vm.Lighting, Dispatcher.CurrentDispatcher);
            host.SetDevices(vm.Devices.ToList());
            host.BeginExternal(dev, 1);
            host.PushExternal(dev, 0, new[] { Rgb.Blue, Rgb.Blue });
            vm.Lighting.Applier.Drain(2000);
            string? clientOp = dev.LastOp;
            t.Check(clientOp != null && clientOp.StartsWith("frame", StringComparison.Ordinal), $"precondition: the client painted ({clientOp})");

            vm.ApplyExitBehaviorNow(dev, new ExitBehavior { Mode = ExitMode.Static, ColorHex = "00FF00" });
            vm.Lighting.Applier.Drain(2000);
            vm.EndExitPreview();   // ExitBehaviorWindow.OnClosed
            vm.Lighting.Applier.Drain(2000);
            t.Equal(clientOp, dev.LastOp,
                "after the window closes the device shows the client's picture again (was left in the previewed exit state)");
            host.Shutdown();
        }
        finally { Stop(vm); }
    }

    /*---------------- P6: lock round trip ----------------*/

    static void UnlockReplacesTheDesk(Harness t)
    {
        t.Section("P6 (rule 4): a lock/unlock round trip gives back what was on, not the startup profile");
        CleanConfig();
        var vm = new MainViewModel(startServices: false);
        try
        {
            var dev = new FakeDevice { Name = "Probe unlock", LedCount = 2 };
            vm.Devices.Add(dev); vm.Profiles.Clear();
            var startup = new Profile { Name = "Probe startup", DeviceFrames = new() { [dev.Name] = new[] { "FF0000", "FF0000" } } };
            var evening = new Profile { Name = "Probe evening", DeviceFrames = new() { [dev.Name] = new[] { "00FF00", "00FF00" } } };
            vm.Profiles.Add(startup); vm.Profiles.Add(evening);
            vm.Lcd.Scenes.Sequences.Clear();
            vm.Lcd.Scenes.Sequences.Add(new SceneSequence
                { Name = "Probe hand show", Actions = new() { new SceneAction { Profile = evening.Name, DelaySeconds = 60 } } });
            vm.Shows.Init();
            // The shipped defaults: LockLightsOff and ReturnToStartupProfile on,
            // a startup profile set (it is applied at every launch).
            vm.SettingsData.StartupProfile = startup.Name;
            vm.SettingsData.ReturnToStartupProfile = true;
            vm.SettingsData.LockLightsOff = true;
            vm.SettingsData.AppSwitchEnabled = false;
            vm.SettingsData.SensorRulesEnabled = false;
            vm.SettingsData.Schedules = new();

            bool? locked = false;
            using var automation = new AutomationService(vm, false, () => locked);
            var tick = typeof(AutomationService).GetMethod("Tick", Flags)!;
            tick.Invoke(automation, null);

            // The user picks another profile and starts a show by hand.
            vm.ApplyProfile(evening); vm.Lighting.Applier.Drain(2000);
            vm.Shows.SelectedShow = vm.Shows.Shows.Single();
            vm.Shows.Toggle();
            t.Check(vm.Shows.Running, "precondition: the hand-started show runs");

            locked = true; tick.Invoke(automation, null); vm.Lighting.Applier.Drain(2000);
            t.Check(dev.Last!.All(x => x == Rgb.Black), "precondition: the lock blacked the desk");
            locked = false; tick.Invoke(automation, null); vm.Lighting.Applier.Drain(2000);

            t.Equal(evening.Name, vm.AppliedProfileName,
                "the unlock puts back the profile that was on (the checkbox reads 'when a RULE stops matching')");
            t.Equal("00FF00", dev.Last![0].ToHex(), "...and its colours");
            t.Check(vm.Shows.Running, "...and the show the user started is still running (the startup profile names no show, so its apply stopped it)");

            // Unsaved hand edits at Base: designing by hand, then a coffee break.
            vm.Shows.Toggle();   // stop, so no step runs
            vm.ApplyProfile(evening); vm.Lighting.Applier.Drain(2000);
            vm.SelectedDevice = dev;
            vm.Hex = "0000FF";
            vm.Lighting.Applier.Drain(2000);
            t.Check(vm.CaptureState().Dirty && dev.Last![0].ToHex() == "0000FF", "precondition: an unsaved hand edit is on the desk");
            locked = true; tick.Invoke(automation, null); vm.Lighting.Applier.Drain(2000);
            locked = false; tick.Invoke(automation, null); vm.Lighting.Applier.Drain(2000);
            t.Equal("0000FF", dev.Last![0].ToHex(), "the unlock gives the unsaved hand edit back");
            t.Check(vm.CaptureState().Dirty, "...and keeps it marked unsaved, so closing still offers to save it (LoadProfile cleared the flag)");
        }
        finally { Stop(vm); }
    }

    /*---------------- helpers ----------------*/

    static void Stop(MainViewModel vm)
    {
        vm.Shows.Dispose(); vm.Lcd.Dispose(); vm.Lighting.StopAndDrain();
        ((LianBakeService)typeof(MainViewModel).GetField("_bake", Flags)!.GetValue(vm)!).Stop();
    }

    static void CleanConfig()
    {
        foreach (string name in Files)
        {
            try { File.Delete(AppPaths.Config(name)); } catch { }
        }
    }
}

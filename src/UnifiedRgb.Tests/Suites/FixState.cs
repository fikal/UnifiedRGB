using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using UnifiedRgb.App;
using UnifiedRgb.App.Services;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Automation;
// System.Diagnostics (for Stopwatch) has an ActivityKind of its own.
using ActivityKind = UnifiedRgb.Core.Automation.ActivityKind;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The 2026-10-14 review's app-state findings, pinned.          |
|                                                              |
| Saving, the profile list's order, the swatches, the import   |
| reload, the bridge switch, the late pump panel, what a       |
| rollback build keeps, and the automation's memory of who     |
| chose the lighting. Each section names its finding (E1, F4,  |
| ...) so a failure leads straight back to the write-up.       |
|                                                              |
| Everything here runs against fakes and the harness's own     |
| config folder. The files it writes are put back as they were |
| when it started, because the suites after it read them.      |
\*-----------------------------------------------------------*/
static class FixStateSuite
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly string[] Files = { "profiles.json", "settings.json", "scenes.json", "canvas.json", "lcd.json" };

    public static void Run(Harness t)
    {
        var originals = Files.ToDictionary(n => n, n => File.Exists(AppPaths.Config(n)) ? File.ReadAllBytes(AppPaths.Config(n)) : null);
        double brightness = Master.Brightness;
        Exception? failure = null;
        // The view models need an STA, like every other suite that builds one.
        var thread = new Thread(() =>
        {
            try
            {
                SensorStateFollowsTheRule(t);
                WindowSpans(t);
                UnknownFieldsSurvive(t);
                CorruptFileIsSaid(t);
                SaveDuringAShow(t);
                OrderSurvivesASave(t);
                DeskEdits(t);
                ImportReload(t);
                LatePanelHonoursTheBlank(t);
                HandEditOutlivesARepaint(t);
                WakeEndsWithItsWindow(t);
                CalibrationHoldsTheSwitch(t);
                // Last: they install a synchronization context on this thread.
                BridgeSwitchedOffWhileStarting(t);
                BridgeToggledDuringItsRestart(t);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
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
        if (failure != null) throw new Exception("FixState regression failed", failure);
    }

    static void Stop(MainViewModel vm)
    {
        vm.Shows.Dispose(); vm.Lcd.Dispose(); vm.Lighting.StopAndDrain();
        ((LianBakeService)typeof(MainViewModel).GetField("_bake", Instance)!.GetValue(vm)!).Stop();
    }

    static ProfileStore StoreOf(MainViewModel vm) => (ProfileStore)typeof(MainViewModel).GetField("_store", Instance)!.GetValue(vm)!;
    static AutomationMode ModeOf(AutomationService a) => (AutomationMode)typeof(AutomationService).GetField("_mode", Instance)!.GetValue(a)!;
    static void Tick(AutomationService a) => typeof(AutomationService).GetMethod("Tick", Instance)!.Invoke(a, null);
    static void Transition(AutomationService a, AutomationMode mode, string? profile)
        => typeof(AutomationService).GetMethod("Transition", Instance)!.Invoke(a, new object?[] { mode, profile, null });

    /*---------------- F6 ----------------*/

    static void SensorStateFollowsTheRule(Harness t)
    {
        t.Section("F6: a sensor rule keeps its own state through a reorder, an add and a remove");
        var readings = new Dictionary<string, double?> { ["hot"] = 90, ["cool"] = 50, ["third"] = 20 };
        double? Read(string source) => readings.TryGetValue(source, out var v) ? v : null;
        var hot = new SensorRule { Source = "hot", Threshold = 85, HoldSeconds = 5, Profile = "Alert" };
        var cool = new SensorRule { Source = "cool", Threshold = 85, HoldSeconds = 5, Profile = "Calm" };
        var rules = new List<SensorRule> { hot, cool };
        var tracker = new SensorRuleTracker();

        tracker.Step(rules, Read, 0, null, out _);
        t.Equal("Alert", tracker.Step(rules, Read, 6, null, out _)?.Profile, "the 90-degree rule fires after its hold");

        // The rules dialog reorders the SAME list in place, so neither the list
        // object nor its length changes - which is all the old reset looked at.
        rules.RemoveAt(0); rules.Insert(1, hot);
        t.Equal("Alert", tracker.Step(rules, Read, 7, null, out _)?.Profile,
            "after a reorder the hot rule is still the one firing (was: its state moved to the 50-degree rule, and 'Calm' went on)");
        t.Check(!tracker.IsActive(cool), "...and the cool rule did not inherit it");

        var third = new SensorRule { Source = "third", Threshold = 85, HoldSeconds = 5, Profile = "Third" };
        rules.Add(third);
        t.Equal("Alert", tracker.Step(rules, Read, 8, null, out _)?.Profile,
            "adding a rule leaves the firing one firing (was: every state reset, and it dropped out for a whole hold)");
        rules.Remove(cool);
        t.Equal("Alert", tracker.Step(rules, Read, 9, null, out _)?.Profile, "removing another rule leaves it firing too");
        t.Equal(2, tracker.Tracked, "the removed rule's state went with it");

        readings.Remove("third");
        tracker.Step(rules, Read, 10, null, out string? missing);
        t.Equal("third", missing, "an enabled rule with no reading is named for the status line");
    }

    /*---------------- F7, the pure half ----------------*/

    static void WindowSpans(Harness t)
    {
        t.Section("F7: the open window's start and end, as dates");
        var monday = new DateTime(2026, 9, 7);
        var night = new ScheduleRule { Start = "23:00", End = "07:00" };
        t.Check(ScheduleRule.OpenSpan(night, monday.AddHours(23.5)) == (monday.AddHours(23), monday.AddDays(1).AddHours(7)),
            "an overnight window seen the evening it opens ends the next morning");
        t.Check(ScheduleRule.OpenSpan(night, monday.AddDays(1).AddHours(1)) == (monday.AddHours(23), monday.AddDays(1).AddHours(7)),
            "...and seen after midnight it still began the evening before");
        var evening = new ScheduleRule { Start = "18:00", End = "20:00" };
        t.Check(ScheduleRule.OpenSpan(evening, monday.AddHours(19)) == (monday.AddHours(18), monday.AddHours(20)), "a same-day window");
        t.Check(ScheduleRule.OpenSpan(evening, monday.AddHours(21)) == null, "a closed window has no span");
    }

    /*---------------- E9 ----------------*/

    static void UnknownFieldsSurvive(Harness t)
    {
        t.Section("E9: fields a newer build wrote survive this build's load and save");
        string profileJson = """
            {"Name":"Future","FutureBinding":{"Fan":"Pump"},
             "Effects":[{"Device":"D","Effect":"Wave","FutureEffectField":7}],
             "DeviceZones":{"D":[{"Name":"Z","Offset":0,"Count":1,"FutureZoneField":true}]}}
            """;
        string back = JsonSerializer.Serialize(JsonSerializer.Deserialize<Profile>(profileJson));
        t.Check(back.Contains("\"FutureBinding\"") && back.Contains("\"Pump\""), "a profile writes back a field it does not know");
        t.Check(back.Contains("\"FutureEffectField\""), "...so does an effect assignment");
        t.Check(back.Contains("\"FutureZoneField\""), "...and a saved zone layout");
        t.Check(!back.Contains("\"Extra\""), "the bag is never a field of its own in the file");

        string settingsJson = """
            {"FutureSetting":1,
             "AutomationRules":[{"Process":"x","Profile":"y","FutureApp":1}],
             "Schedules":[{"Start":"23:00","End":"07:00","FutureSched":1}],
             "SensorRules":[{"Source":"CpuTemp","Profile":"y","FutureSensor":1}],
             "SavedPalettes":[{"Name":"p","FuturePalette":1}],
             "DisabledDevices":[{"Name":"n","Family":"f","FutureDisabled":1}]}
            """;
        back = JsonSerializer.Serialize(JsonSerializer.Deserialize<SettingsData>(settingsJson));
        foreach (string key in new[] { "FutureSetting", "FutureApp", "FutureSched", "FutureSensor", "FuturePalette", "FutureDisabled" })
            t.Check(back.Contains($"\"{key}\""), $"settings.json keeps {key}");

        string scenesJson = """
            {"FutureStore":1,
             "Scenes":[{"Name":"S","FutureScene":1,"Design":{"FutureDesign":1,"Elements":[{"Kind":"Text","FutureElement":1}]}}],
             "Sequences":[{"Name":"Q","FutureShow":1,"Actions":[{"Profile":"P","FutureStep":1}]}]}
            """;
        var scenes = SceneStore.TryParse(scenesJson)!;
        back = JsonSerializer.Serialize(scenes);
        foreach (string key in new[] { "FutureStore", "FutureScene", "FutureDesign", "FutureElement", "FutureShow", "FutureStep" })
            t.Check(back.Contains($"\"{key}\""), $"scenes.json keeps {key}");
        t.Check(JsonSerializer.Serialize(SceneStore.Clone(scenes.Scenes[0].Design)).Contains("\"FutureDesign\""),
            "a design copied for the editor keeps them too");

        // Through the store: a re-save BUILDS a new profile, so the bag has to be
        // carried from the one it replaces, as an absent device's colors are.
        File.WriteAllText(AppPaths.Config("profiles.json"), """[{"Name":"E9 kept","FutureBinding":"x"}]""");
        var store = new ProfileStore();
        store.TryCapture("E9 kept", Array.Empty<(IRgbDevice, Rgb[])>());
        t.Check(File.ReadAllText(AppPaths.Config("profiles.json")).Contains("\"FutureBinding\""),
            "re-saving a profile keeps what a newer build stored on it (was: a rollback build's save stripped it)");
    }

    /*---------------- E10 ----------------*/

    static void CorruptFileIsSaid(Harness t)
    {
        t.Section("E10: a corrupt store is reset out loud, with the copy's path");
        string dir = AppPaths.ConfigDir;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "harness-corrupt.json");
        File.WriteAllText(path, "{ this is not json");
        ActivityLog.Shared.Clear();
        try
        {
            t.Check(ProfileStore.LoadJson<SettingsData>(path, "harness-corrupt.json") == null, "a corrupt file loads as nothing, so defaults");
            var notes = ActivityLog.Shared.Snapshot()
                .Where(e => e.Kind == ActivityKind.Problem && e.Text.Contains("harness-corrupt.json")).ToList();
            t.Equal(1, notes.Count, "...and the activity history says so (was: a log line only)");
            var copies = Directory.GetFiles(dir, "harness-corrupt.json.corrupt-*");
            t.Check(copies.Length == 1 && notes.Count == 1 && notes[0].Text.Contains(copies[0]),
                "...naming where the old file was kept");
            t.Check(!ProfileStore.IsUnreadable(path), "a corrupt file does not switch saves off: there is nothing intact to protect");
        }
        finally
        {
            File.Delete(path);
            foreach (string copy in Directory.GetFiles(dir, "harness-corrupt.json.corrupt-*")) File.Delete(copy);
        }
    }

    /*---------------- E1 ----------------*/

    static void SaveDuringAShow(Harness t)
    {
        t.Section("E1: the Save button during a show does not write the step into the selected profile");
        var vm = new MainViewModel(startServices: false);
        try
        {
            var store = StoreOf(vm);
            var stage = new FakeDevice { Name = "E1 stage", LedCount = 2 };
            vm.Devices.Add(stage);
            vm.Profiles.Clear(); store.Profiles.Clear();
            var keeper = new Profile { Name = "E1 keeper" };
            keeper.DeviceFrames[stage.Name] = new[] { "ABCDEF", "ABCDEF" };
            vm.Profiles.Add(keeper); store.Profiles.Add(keeper);
            vm.Lcd.Scenes.Sequences.Clear();
            vm.Lcd.Scenes.Sequences.Add(new SceneSequence { Name = "E1 loop", Actions = new()
                { new SceneAction { Profile = keeper.Name, DelaySeconds = 60 } } });
            vm.Shows.Init();

            // Picking a profile fills the name box, and a filled box is the path
            // the button takes - not SaveActiveProfile, which had the guard.
            vm.SelectedProfile = keeper;
            t.Equal(keeper.Name, vm.ProfileName, "selecting a profile fills the name box");
            // The step paints the desk green: the show's lighting, not the user's.
            var live = vm.Lighting.FrameFor(stage);
            live[0] = live[1] = new Rgb(0, 255, 0);
            vm.Shows.Start("E1 loop");
            t.Check(vm.Shows.Running, "the show is running");

            t.Check(vm.SaveProfileCommand.CanExecute(null), "Save is enabled");
            vm.SaveProfileCommand.Execute(null);
            var saved = vm.Profiles.Single(p => p.Name == keeper.Name);
            t.Equal("ABCDEF", saved.DeviceFrames[stage.Name][0],
                "the profile keeps its own colors (was: the step's green was written into it)");
            t.Equal("ABCDEF", new ProfileStore().Profiles.Single(p => p.Name == keeper.Name).DeviceFrames[stage.Name][0],
                "...on disk too");
            t.Check(vm.AppliedProfileName != keeper.Name, "...and the save does not claim the desk is showing that profile");

            // A rename during a show already carried the colors; still does.
            vm.SelectedProfile = saved;
            vm.ProfileName = "E1 renamed";
            vm.SaveProfileCommand.Execute(null);
            var renamed = vm.Profiles.SingleOrDefault(p => p.Name == "E1 renamed");
            t.Check(renamed != null && renamed.DeviceFrames[stage.Name][0] == "ABCDEF",
                "a rename during a show carries the profile's own colors");
            t.Check(vm.Profiles.All(p => p.Name != keeper.Name), "...and is still a rename");

            // A genuinely new name has nothing to carry: the desk is the only
            // lighting there is, and taking it overwrites nobody's profile.
            vm.SelectedProfile = null;
            vm.ProfileName = "E1 fresh";
            vm.SaveProfileCommand.Execute(null);
            var fresh = vm.Profiles.SingleOrDefault(p => p.Name == "E1 fresh");
            t.Check(fresh != null && fresh.DeviceFrames.TryGetValue(stage.Name, out var hex) && hex[0] == "00FF00",
                "a brand-new profile saved during a show records what is on the desk");

            // PAUSED is different: pause exists so the user can change things
            // without the desk moving, and a save must keep what they changed.
            vm.Shows.TogglePaused();
            t.Check(vm.Shows.Running && vm.Shows.Paused, "the show is paused");
            vm.SelectedProfile = vm.Profiles.Single(p => p.Name == "E1 renamed");
            live[0] = live[1] = new Rgb(0x12, 0x34, 0x56);
            vm.SaveProfileCommand.Execute(null);
            t.Equal("123456", vm.Profiles.Single(p => p.Name == "E1 renamed").DeviceFrames[stage.Name][0],
                "a save while the show is PAUSED keeps the user's edit (was: dropped silently)");
            vm.Shows.Stop();
        }
        finally { Stop(vm); }
    }

    /*---------------- E3 ----------------*/

    static string Names(IEnumerable<Profile> list) => string.Join(", ", list.Select(p => p.Name));

    static void OrderSurvivesASave(Harness t)
    {
        t.Section("E3: a re-save or a rename keeps the profile's place, on screen and in the file");
        var vm = new MainViewModel(startServices: false);
        try
        {
            var store = StoreOf(vm);
            vm.Profiles.Clear(); store.Profiles.Clear();
            foreach (string name in new[] { "E3 one", "E3 two", "E3 three" })
            {
                vm.SelectedProfile = null;
                vm.ProfileName = name;
                vm.SaveProfileAsNew();
            }
            t.Equal("E3 one, E3 two, E3 three", Names(vm.Profiles), "three profiles, in the order they were made");

            vm.SelectedProfile = vm.Profiles[0];   // fills the name box: the button re-saves it
            vm.SaveProfileCommand.Execute(null);
            t.Equal("E3 one, E3 two, E3 three", Names(vm.Profiles), "re-saving the first leaves it first on screen");
            t.Equal("E3 one, E3 two, E3 three", Names(new ProfileStore().Profiles),
                "...and in profiles.json (was: moved to the end, so after a restart Ctrl+Alt+1 applied 'E3 two')");

            vm.SelectedProfile = vm.Profiles[1];
            vm.ProfileName = "E3 two renamed";
            vm.SaveProfileCommand.Execute(null);
            t.Equal("E3 one, E3 two renamed, E3 three", Names(vm.Profiles), "a rename takes the old profile's slot on screen");
            t.Equal("E3 one, E3 two renamed, E3 three", Names(new ProfileStore().Profiles), "...and in the file");

            var disk = new ProfileStore();
            disk.Capture("E3 one", Array.Empty<(IRgbDevice, Rgb[])>());
            t.Equal("E3 one, E3 two renamed, E3 three", Names(disk.Profiles), "the store's own Capture replaces in place too");
        }
        finally { Stop(vm); }
    }

    /*---------------- E4, E7, E12 ----------------*/

    static void DeskEdits(Harness t)
    {
        var vm = new MainViewModel(startServices: false);
        try
        {
            var store = StoreOf(vm);
            var dev = new FakeDevice { Name = "Desk edits", LedCount = 2 };
            vm.Devices.Add(dev);
            vm.Profiles.Clear(); store.Profiles.Clear();

            t.Section("E4: a show step leaves the swatches alone");
            var swatches = new Profile { Name = "E4 swatches", CustomColors = new[] { "112233" } };
            vm.Profiles.Add(swatches);
            var mine = new Rgb(0xAA, 0xBB, 0xCC);
            vm.CustomColors.Clear(); vm.CustomColors.Add(mine);
            vm.ApplyProfile(swatches, fromShow: true);
            t.Check(vm.CustomColors.SequenceEqual(new[] { mine }),
                "a show step does not replace the swatch row (was: every step did, and a swatch added mid-show was lost)");
            t.Check(vm.SettingsData.CustomColors?.Contains("112233") != true,
                "...or write the step's swatches into settings.json");
            vm.ApplyProfile(swatches);
            t.Check(vm.CustomColors.SequenceEqual(new[] { Rgb.FromHex("112233") }), "a profile applied by hand still brings its swatches");

            t.Section("E7: deleting the lit profile forgets that the desk is showing it");
            var doomed = new Profile { Name = "E7 doomed", DeviceFrames = new() { [dev.Name] = new[] { "FF0000", "FF0000" } } };
            vm.Profiles.Add(doomed); store.Profiles.Add(doomed);
            vm.ApplyProfile(doomed);
            t.Equal(doomed.Name, vm.AppliedProfileName, "applying it records it as the lit profile");
            vm.SelectedProfile = doomed;
            vm.DeleteProfile();
            t.Check(vm.Profiles.All(p => p.Name != doomed.Name), "it is deleted");
            t.Check(vm.AppliedProfileName == null, "...and no longer named as the lit profile");
            vm.Lcd.Scenes.Sequences.Clear();
            vm.Lcd.Scenes.Sequences.Add(new SceneSequence { Name = "E7 show" });
            vm.Shows.Init();
            vm.Shows.SelectedShow = vm.Shows.Shows.First();
            vm.Shows.AddStep();
            t.Check(vm.Shows.Steps.Count == 1 && vm.Shows.Steps[0].Profile != doomed.Name,
                "a new show step does not default to the deleted profile (was: it named it)");

            t.Section("E12: flipping the desk canvas in the dark does not restart effects");
            vm.SelectedDevice = dev;
            vm.SelectedEffectChoice = vm.Effects.First(e => e.Name == "Rainbow Wave");
            t.Equal(1, vm.Lighting.Engine.ChannelsFor(dev).Count, "an effect is running");
            vm.LightsOff();
            t.Equal(0, vm.Lighting.Engine.ChannelsFor(dev).Count, "the lights go off and the effect stops");
            bool was = vm.CanvasEnabled;
            try
            {
                vm.CanvasEnabled = !was;
                t.Equal(0, vm.Lighting.Engine.ChannelsFor(dev).Count,
                    "switching the canvas inside a dark window leaves it stopped (was: every effect restarted)");
            }
            finally
            {
                vm.CanvasEnabled = was;
                vm.LightsSuppressed = false;
            }
        }
        finally { Stop(vm); }
    }

    /*---------------- F2 and the F13 hook ----------------*/

    static void ImportReload(Harness t)
    {
        var vm = new MainViewModel(startServices: false);
        try
        {
            var store = StoreOf(vm);
            var dev = new FakeDevice { Name = "Import desk", LedCount = 2 };
            vm.Devices.Add(dev);
            vm.Profiles.Clear(); store.Profiles.Clear();

            t.Section("F2: an import keeps the unsaved-changes prompt");
            vm.SelectedProfile = null;
            vm.ProfileName = "F2 kept";
            vm.SaveProfileCommand.Execute(null);
            vm.SelectedDevice = dev;
            vm.Hex = "123456";
            t.Check(vm.NeedsSavePrompt, "an edit after picking a profile needs the save prompt");
            vm.ReloadAfterImport(new ImportResult { Ok = true, ProfilesChanged = true });
            t.Equal("F2 kept", vm.SelectedProfile?.Name, "the profile is still selected after the reload");
            t.Check(vm.NeedsSavePrompt, "...and closing still asks to save the edit (was: the import cleared the flag)");

            t.Section("F13: an import that wrote fan-config.json has the fan hub re-read it");
            int reloads = 0;
            MainViewModel.AfterFanConfigImport = () => reloads++;
            try
            {
                vm.ReloadAfterImport(new ImportResult { Ok = true, FanConfigChanged = true });
                t.Equal(1, reloads, "the fan config is reloaded when the import wrote it");
                vm.ReloadAfterImport(new ImportResult { Ok = true, RestartRecommended = true });
                t.Equal(1, reloads, "...and not when it did not");
            }
            finally { MainViewModel.AfterFanConfigImport = null; }
        }
        finally { Stop(vm); }
    }

    /*---------------- E6 ----------------*/

    static void LatePanelHonoursTheBlank(Harness t)
    {
        t.Section("E6: a pump panel that first appears late honours a blank asked for before it came");
        using var lcd = new LcdDesignerViewModel(() => false);
        lcd.OpenPanel = () => null;
        lcd.Start();            // no panel yet: the finder starts (its timer never runs here)
        lcd.SetOn(false);       // the dark window an autostart can begin in
        // Opens ON, like a real panel - unlike the fakes elsewhere, which open off.
        var panel = new LcdController(new UnifiedRgb.Core.Devices.ThermalrightLcd(new FakeHid()));
        lcd.OpenPanel = () => panel;
        t.Check(lcd.TryReattach(), "the late panel attaches");
        t.Check(!panel.On, "...blank, as asked while it was missing (was: lit inside the dark window)");
    }

    /*---------------- F4 ----------------*/

    static void HandEditOutlivesARepaint(Harness t)
    {
        var vm = new MainViewModel(startServices: false);
        try
        {
            var dev = new FakeDevice { Name = "F4 desk", LedCount = 2 };
            vm.Devices.Add(dev); vm.Profiles.Clear();
            Profile Make(string name, string hex) => new() { Name = name, DeviceFrames = new() { [dev.Name] = new[] { hex, hex } } };
            var baseline = Make("F4 baseline", "FF0000");
            var gameA = Make("F4 game A", "00FF00");
            var gameB = Make("F4 game B", "FFFF00");
            vm.Profiles.Add(baseline); vm.Profiles.Add(gameA); vm.Profiles.Add(gameB);
            vm.SettingsData.ReturnToStartupProfile = false;
            vm.ApplyProfile(baseline);
            vm.SelectedDevice = dev;
            using var automation = new AutomationService(vm, monitor: false);
            string Shown() => vm.Lighting.FrameFor(dev)[0].ToHex();

            t.Section("F4: a hand edit survives a lock and the rule's return");
            Transition(automation, AutomationMode.App, gameA.Name);
            vm.Hex = "0000FF";                                   // the user takes over mid-game
            Transition(automation, AutomationMode.Locked, null);
            Transition(automation, AutomationMode.App, gameA.Name);   // unlocked, still in the game
            t.Equal("00FF00", Shown(), "the rule's profile is back on after the unlock");
            Transition(automation, AutomationMode.Base, null);
            t.Equal("0000FF", Shown(), "the game ends and the hand-picked blue is back (was: the rule's green stuck)");

            t.Section("F4: a hand edit survives a second rule taking over");
            Transition(automation, AutomationMode.App, gameA.Name);
            vm.Hex = "00FFFF";
            Transition(automation, AutomationMode.App, gameB.Name);
            t.Equal("FFFF00", Shown(), "the second rule's profile is on");
            Transition(automation, AutomationMode.Base, null);
            t.Equal("00FFFF", Shown(), "every rule ends and the hand edit is back (was: rule B's yellow stuck)");

            t.Section("F4: a hand edit nothing painted over is simply kept");
            Transition(automation, AutomationMode.App, gameA.Name);
            vm.Hex = "FF00FF";
            Transition(automation, AutomationMode.Base, null);
            t.Equal("FF00FF", Shown(), "the edit is what stays");
        }
        finally { Stop(vm); }
    }

    /*---------------- F7 ----------------*/

    static void WakeEndsWithItsWindow(Harness t)
    {
        var vm = new MainViewModel(startServices: false);
        try
        {
            vm.SettingsData.AppSwitchEnabled = false;
            vm.SettingsData.SensorRulesEnabled = false;
            vm.SettingsData.Schedules = new() { new ScheduleRule { Start = "23:00", End = "07:00", Days = 0x7F } };
            using var automation = new AutomationService(vm, false, () => false);
            var monday = new DateTime(2026, 9, 7);
            DateTime now = monday.AddHours(23.5);
            automation.WallClock = () => now;

            t.Section("F7: waking the lights in a dark window lasts until that window ends");
            Tick(automation);
            t.Equal(AutomationMode.ScheduleOff, ModeOf(automation), "the dark window turns the lights off");
            automation.Wake();
            t.Equal(AutomationMode.Base, ModeOf(automation), "Wake brings them back");
            now = monday.AddDays(1).AddHours(3);
            Tick(automation);
            t.Equal(AutomationMode.Base, ModeOf(automation), "...for the rest of that night");
            // Asleep from 03:00 to 23:30: no tick ever saw the morning's gap.
            now = monday.AddDays(1).AddHours(23.5);
            Tick(automation);
            t.Equal(AutomationMode.ScheduleOff, ModeOf(automation),
                "the next night's window turns the lights off (was: last night's wake kept them on)");

            t.Section("F7: a window that runs straight on from the woken one stays woken");
            vm.SettingsData.Schedules = new()
            {
                new ScheduleRule { Start = "23:00", End = "02:00", Days = 0x7F },
                new ScheduleRule { Start = "02:00", End = "07:00", Days = 0x7F },
            };
            now = monday.AddDays(2).AddHours(23.5);
            Tick(automation);
            t.Equal(AutomationMode.ScheduleOff, ModeOf(automation), "the first window is dark");
            automation.Wake();
            now = monday.AddDays(3).AddHours(2.5);
            Tick(automation);
            t.Equal(AutomationMode.Base, ModeOf(automation), "the second, with no gap between, does not switch the lights back off");
        }
        finally { Stop(vm); }
    }

    /*---------------- F8 ----------------*/

    static void CalibrationHoldsTheSwitch(Harness t)
    {
        var vm = new MainViewModel(startServices: false);
        CalibrationAid? aid = null;
        try
        {
            var dev = new FakeDevice { Name = "F8 desk", LedCount = 2 };
            vm.Devices.Add(dev); vm.Profiles.Clear();
            var evening = new Profile { Name = "F8 evening", DeviceFrames = new() { [dev.Name] = new[] { "102030", "102030" } } };
            vm.Profiles.Add(evening);
            vm.SettingsData.AppSwitchEnabled = false;
            vm.SettingsData.SensorRulesEnabled = false;
            vm.SettingsData.LockLightsOff = true;
            vm.SettingsData.Schedules = new()
                { new ScheduleRule { Start = "18:00", End = "20:00", Days = 0x7F, Action = ScheduleAction.Profile, Profile = evening.Name } };
            bool? locked = false;
            using var automation = new AutomationService(vm, false, () => locked);
            var monday = new DateTime(2026, 9, 7);
            DateTime now = monday.AddHours(17);
            automation.WallClock = () => now;
            Tick(automation);

            t.Section("F8: automation waits for an open colour calibration before it paints");
            aid = new CalibrationAid(vm.Lighting);
            aid.Start(new[] { dev });
            now = monday.AddHours(18.5);
            Tick(automation);
            t.Equal(AutomationMode.Base, ModeOf(automation), "a scheduled profile waits while the reference patches are up");
            locked = true;
            Tick(automation);
            t.Equal(AutomationMode.Locked, ModeOf(automation), "a lock still turns the lights off");
            locked = false;
            Tick(automation);
            t.Equal(AutomationMode.Locked, ModeOf(automation), "...and the relight waits for the calibration to close");
            aid.Stop();
            Tick(automation);
            t.Equal(AutomationMode.ScheduleProfile, ModeOf(automation), "once it closes, the waiting switch happens");
            t.Equal(evening.Name, vm.AppliedProfileName, "...and the profile is on");
        }
        finally
        {
            aid?.Stop();   // AnyActive is process-wide: a failure here must not hold every later suite
            Stop(vm);
        }
    }

    /*---------------- E5 ----------------*/

    /// <summary>Run this thread's dispatcher until the condition holds, so the
    /// bridge's awaits come back here as they would on the UI thread.</summary>
    static bool PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < timeoutMs)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        return condition();
    }

    static void BridgeSwitchedOffWhileStarting(Harness t)
    {
        t.Section("E5: switching the OpenRGB bridge off while it starts takes down what it launched");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var vm = new MainViewModel(startServices: false);
        try
        {
            var launch = new TaskCompletionSource<bool>();
            int stops = 0, rescans = 0;
            vm.LaunchOpenRgb = _ => launch.Task;
            vm.StopOpenRgb = () => Interlocked.Increment(ref stops);
            vm.BridgeRescan = () => rescans++;

            vm.UseOpenRgb = true;                 // the download/launch is under way...
            t.Equal("starting...", vm.OpenRgbStatus, "the bridge is starting");
            vm.UseOpenRgb = false;                // ...and the user switches it off
            t.Check(PumpUntil(() => Volatile.Read(ref stops) == 1 && rescans == 1, 3000),
                "the switch-off runs its teardown (with nothing launched yet to stop) and its rescan");
            launch.SetResult(true);              // the launch it could not reach finishes now
            t.Check(PumpUntil(() => Volatile.Read(ref stops) == 2, 3000),
                "the OpenRGB that finished launching after the switch-off is stopped too (was: left running headless)");
            PumpUntil(() => false, 100);         // anything still queued gets its chance to run
            t.Check(!vm.OpenRgbStatus.StartsWith("connected", StringComparison.Ordinal),
                $"the status does not call an off bridge connected (it reads '{vm.OpenRgbStatus}')");
            t.Equal(1, rescans, "the overtaken start does not rescan for a bridge that is off");
        }
        finally
        {
            Stop(vm);
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    static void BridgeToggledDuringItsRestart(Harness t)
    {
        t.Section("V1-11: an off-on toggle during the post-launch restart waits for it");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var vm = new MainViewModel(startServices: false);
        try
        {
            var release = new TaskCompletionSource<bool>();
            int launches = 0, releases = 0;
            vm.LaunchOpenRgb = _ => { Interlocked.Increment(ref launches); return Task.FromResult(true); };
            vm.StopOpenRgb = () => { };
            vm.BridgeRescan = () => { };
            vm.OpenRgbServerUp = () => true;
            vm.ReleaseNativelyDriven = _ => { Interlocked.Increment(ref releases); return releases == 1 ? release.Task : Task.FromResult(false); };

            vm.UseOpenRgb = true;
            t.Check(PumpUntil(() => Volatile.Read(ref releases) == 1, 3000), "the first start launched and began its detector-release restart");
            vm.UseOpenRgb = false;                // switched off and back on while that restart runs
            vm.UseOpenRgb = true;
            PumpUntil(() => false, 300);
            t.Equal(1, Volatile.Read(ref launches), "the second start does not launch beside the restart still running (was: two relaunch loops killing each other's process)");
            release.SetResult(true);              // the restart finishes...
            t.Check(PumpUntil(() => Volatile.Read(ref launches) == 2, 3000), "...and only then does the second start launch");
        }
        finally
        {
            Stop(vm);
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }
}

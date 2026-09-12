using System.Reflection;
using System.Runtime.CompilerServices;
using UnifiedRgb.App;
using UnifiedRgb.Core;

namespace UnifiedRgb.Tests;

static class LcdScenesSuite
{
    public static void Run(Harness t)
    {
        // WPF editor state needs an STA. No panel is opened: this controller
        // only holds a design, with rendering off and no stream thread.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckScenes(t); ShowThatContainsItsOwnStarter(t); PausingAShow(t); ProfileShowIntegration(t); SnapshotPlayback(t); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Exception("LCD scene regression failed", failure);
    }

    /// <summary>A profile that starts a show, and is also a STEP of that show,
    /// is the ordinary thing to build and looks like it should recurse: applying
    /// it starts the show, whose step applies it, which starts the show...
    ///
    /// Three separate guards stop it, and this pins all three rather than
    /// trusting that any one of them holds.</summary>
    static void ShowThatContainsItsOwnStarter(Harness t)
    {
        t.Section("a show whose step is the profile that started it");

        int applied = 0;
        string current = "Matrix";
        using var vm = new ShowViewModel(
            store: SceneStore.Load, lightsSuppressed: () => false,
            applyProfile: name => { applied++; current = name; return true; },
            profileNames: () => new[] { "Matrix", "Diablo" },
            currentProfile: () => current);

        var action = typeof(ShowViewModel).GetMethod("ApplyStep",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        // Guard 1: a step naming the profile that is ALREADY on does nothing at
        // all. This is the one that breaks the cycle at its tightest point - the
        // first step of a show started by that very profile.
        action.Invoke(vm, new object[] { new SceneAction { Profile = "Matrix" } });
        t.Equal(0, applied, "a step naming the profile already on does not re-apply it");

        // It still works for a step that genuinely changes things.
        action.Invoke(vm, new object[] { new SceneAction { Profile = "Diablo" } });
        t.Equal(1, applied, "a step naming a different profile applies it");
        t.Equal("Diablo", current, "...and that profile becomes the current one");

        // And back round: now Matrix IS a change, so it applies once.
        action.Invoke(vm, new object[] { new SceneAction { Profile = "Matrix" } });
        t.Equal(2, applied, "coming back round applies it exactly once");

        // Guard 2: a step with no profile is a no-op rather than a reset.
        action.Invoke(vm, new object[] { new SceneAction { Profile = null } });
        t.Equal(2, applied, "a step naming no profile does nothing");

        // Guard 3 lives in ShowSequence: asking for the show already running is
        // answered yes without restarting it.
        //
        // And the ordering trap underneath it. The startup profile applies BEFORE
        // InitScenes builds the sequencer, so a show it names arrives too early.
        // Refusing it there meant "show could not start" at every launch with
        // only the first profile ever playing, so the request is remembered.
        t.Check(vm.Start("later"), "a show asked for before the shows load is remembered, not refused");
        t.Equal("later", typeof(ShowViewModel)
            .GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm),
            "...and held until they are ready");
        vm.Stop();
        t.Check(typeof(ShowViewModel)
            .GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm) == null,
            "a show called off before it starts does not start late");
    }

    /// <summary>Pausing a show, as distinct from stopping one. A show moves the
    /// whole desk every few seconds, which is exactly what you do not want while
    /// changing the settings that decide where it moves to.</summary>
    static void PausingAShow(Harness t)
    {
        t.Section("pausing a show");

        int applied = 0;
        var seq = new SceneSequence
        {
            Name = "Evening",
            Actions = { new SceneAction { Profile = "A", DelaySeconds = 30 },
                        new SceneAction { Profile = "B", DelaySeconds = 30 } },
        };
        var sequencer = new SceneSequencer(_ => applied++);

        // Nothing to pause before anything runs, and asking must not invent a
        // state that Start would then have to undo.
        sequencer.Paused = true;
        t.Check(!sequencer.Paused, "pausing when no show is running does nothing");

        sequencer.Start(seq);
        t.Check(sequencer.Running, "the show is running");
        t.Check(!sequencer.Paused, "a show starts running, not paused");

        sequencer.Paused = true;
        t.Check(sequencer.Paused, "it pauses");
        t.Check(sequencer.Running, "...and is still the running show rather than forgotten");
        t.Equal("Evening", sequencer.RunningName, "...under its own name");

        sequencer.Paused = false;
        t.Check(!sequencer.Paused, "it resumes");
        t.Check(sequencer.Running, "...still on the same show");

        // Stop is the other thing, and it forgets.
        sequencer.Stop();
        t.Check(!sequencer.Running, "stop forgets the show");
        t.Check(!sequencer.Paused, "...and leaves nothing paused behind it");

        // A new show always starts running, whatever the last one was doing.
        sequencer.Start(seq);
        sequencer.Paused = true;
        sequencer.Start(seq);
        t.Check(!sequencer.Paused, "starting a show clears a pause left from before");
        sequencer.Stop();

        t.Equal(0, applied, "no step ran: every wait here is 30 seconds and nothing waited that long");
    }

    static void ProfileShowIntegration(Harness t)
    {
        t.Section("show and profile integration");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var vm = new MainViewModel(startServices: false);
        var lcd = (LcdController)RuntimeHelpers.GetUninitializedObject(typeof(LcdController));
        var lcdField = typeof(LcdDesignerViewModel).GetField("_lcd", flags)!;
        lcd.Design = LcdDesign.Default();
        lcdField.SetValue(vm.Lcd, lcd);
        try
        {
            vm.Profiles.Clear();
            var store = vm.Lcd.Scenes;
            store.Scenes.Clear(); store.Sequences.Clear();
            store.Scenes.Add(new LcdScene { Name = "Screen", Design = LcdDesign.Default() });
            store.Sequences.Add(new SceneSequence { Name = "Loop", Actions = new() { new SceneAction { Profile = "A", DelaySeconds = 30 } } });
            vm.Lcd.InitScenes(); vm.Shows.Init(); vm.RefreshPumpRows();
            var a = new Profile { Name = "A", Screen = "Screen", Show = "Loop" };
            var b = new Profile { Name = "B" };
            vm.Profiles.Add(a); vm.Profiles.Add(b);
            vm.ApplyProfile(b);
            vm.Lcd.SelectedSceneName = "Screen";
            lcd.Design.Elements.Clear(); vm.Lcd.TouchLcd();
            typeof(ShowViewModel).GetMethod("ApplyStep", flags)!.Invoke(vm.Shows, new object[] { new SceneAction { Profile = "A" } });
            t.Check(lcd.Design.Elements.Count > 0, "actual show callback restores the edited same-name scene");
            t.Check(vm.Lcd.SnapshotDesign()!.FromShow, "show screen does not become the saved canvas");
            vm.Lcd.ShowScreen("Screen", fromShow: false);
            t.Check(!vm.Lcd.SnapshotDesign()!.FromShow, "manual screen takes canvas ownership back");
            vm.Shows.Start("Loop");
            vm.ApplyProfile(b);
            t.Check(!vm.Shows.Running, "no-show profile stops playback even without a pump screen");
            vm.Shows.Start("Loop");
            vm.ApplyProfile(b, fromShow: true);
            t.Check(vm.Shows.Running, "step profile with no show does not stop its owning show");
            vm.Shows.Stop();

            var absent = new Profile { Name = "Portable", Screen = "Missing screen", Show = "Missing show" };
            vm.Profiles.Add(absent); vm.SelectedProfile = absent;
            vm.RefreshPumpRows();
            var pump = (PumpTarget)typeof(MainViewModel).GetProperty("PumpForSave", flags)!.GetValue(vm)!;
            t.Equal("Missing screen", pump.Screen, "save preserves an unavailable screen beside available shows");
            t.Equal("Missing show", pump.Show, "save preserves an unavailable show beside available screens");
            vm.PumpChoice = vm.PumpRows.First(r => r.Screen == null);
            vm.ShowChoice = MainViewModel.NoShow;
            pump = (PumpTarget)typeof(MainViewModel).GetProperty("PumpForSave", flags)!.GetValue(vm)!;
            t.Check(pump.Screen == null && pump.Show == null, "explicit none choices still clear unavailable bindings");

            var profiles = (ProfileStore)typeof(MainViewModel).GetField("_store", flags)!.GetValue(vm)!;
            profiles.Settings.StartupProfile = "B";
            b.Screen = "Screen";
            store.ActiveSequence = "Loop";
            typeof(MainViewModel).GetMethod("MigrateShowsToProfiles", flags)!.Invoke(vm, null);
            t.Check(vm.Shows.Running, "legacy startup show runs during its migration launch");
            t.Equal("Screen", b.Screen, "legacy migration preserves the independent startup screen");
            vm.Shows.Stop();

            t.Section("a running show cannot write its step into the user's profile");
            // While a show runs, the desk is whatever STEP is up - and a step is
            // somebody else's profile. SaveActiveProfile is reached from the close
            // prompt and, with NO prompt at all, from Session_Ending, so a logoff
            // during a show used to quietly rewrite the saved setup the user had
            // selected with the current step's colors.
            // A device that is PRESENT, with live colors that differ from what the
            // profile stores. Without one, both paths agree by accident.
            var stage = new FakeDevice { Name = "Stage", LedCount = 2 };
            vm.Devices.Add(stage);
            var keeper = new Profile { Name = "Keeper" };
            keeper.DeviceFrames["Stage"] = new[] { "ABCDEF", "ABCDEF" };
            vm.Profiles.Add(keeper); profiles.Profiles.Add(keeper);   // the store is what a capture carries from
            vm.SelectedProfile = keeper;
            // The step paints the desk green - this is the show's lighting, not the
            // user's, and it is what must NOT end up in "Keeper".
            var live = vm.Lighting.FrameFor(stage);
            live[0] = new Rgb(0, 255, 0); live[1] = new Rgb(0, 255, 0);
            vm.Shows.Start("Loop");
            t.Check(vm.Shows.Running, "the show is running for the save test");
            vm.SaveActiveProfile();
            var saved = vm.Profiles.Single(p => p.Name == "Keeper");
            t.Check(saved.DeviceFrames.TryGetValue("Stage", out var kept) && kept[0] == "ABCDEF",
                "saving during a show carries the profile's own colors rather than the step's");
            // The pickers ARE the user's and must still be saved - that is the
            // whole reason a show step no longer clears the unsaved flag.
            vm.PumpChoice = vm.PumpRows.First(r => r.Screen == "Screen");
            vm.SaveActiveProfile();
            t.Equal("Screen", vm.Profiles.Single(p => p.Name == "Keeper").Screen,
                "saving during a show still records the picker choices the user made");
            t.Check(vm.CaptureState().AppliedProfileName != "Keeper",
                "a save that skipped the colors does not claim the desk is showing that profile");
            vm.Shows.Stop();

            // With no show running the save DOES capture the desk, so the desk is
            // now that profile. Clearing this instead cost ApplyStep its "already
            // on" skip - a step naming the just-saved profile restarted every
            // effect channel - and made AddStep default to the first profile in
            // the list rather than the lit one.
            vm.SaveActiveProfile();
            t.Equal("Keeper", vm.CaptureState().AppliedProfileName,
                "saving outside a show records that the lighting IS that profile");
        }
        finally { lcdField.SetValue(vm.Lcd, null); vm.Dispose(); }
    }

    static void SnapshotPlayback(Harness t)
    {
        t.Section("snapshot playback and applied identity");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var vm = new MainViewModel(startServices: false);
        vm.Profiles.Clear();
        var a = new Profile { Name = "Snapshot A" };
        var b = new Profile { Name = "Snapshot B" };
        vm.Profiles.Add(a); vm.Profiles.Add(b);
        vm.Lcd.Scenes.Sequences.Clear();
        vm.Lcd.Scenes.Sequences.Add(new SceneSequence { Name = "Snapshot Loop", Actions = new()
        {
            new SceneAction { Profile = a.Name, DelaySeconds = 30 },
            new SceneAction { Profile = b.Name, DelaySeconds = 30 }
        } });
        vm.Shows.Init();
        vm.ApplyProfile(a);
        var snapshot = vm.CaptureState();
        vm.ApplyProfile(b);
        vm.RestoreState(snapshot);
        t.Equal(a.Name, typeof(MainViewModel).GetField("_appliedProfile", flags)!.GetValue(vm), "restore records the profile actually restored");
        int applies = 0;
        vm.ApplyWallpaperProfile = _ => { applies++; return true; };
        typeof(ShowViewModel).GetMethod("ApplyStep", flags)!.Invoke(vm.Shows, new object[] { new SceneAction { Profile = b.Name } });
        t.Equal(1, applies, "show does not skip a profile left only in the old cache");

        vm.Shows.Start("Snapshot Loop");
        var sequencer = (SceneSequencer)typeof(ShowViewModel).GetField("_sequencer", flags)!.GetValue(vm.Shows)!;
        typeof(SceneSequencer).GetMethod("Step", flags)!.Invoke(sequencer, null);
        typeof(SceneSequencer).GetField("_dueAt", flags)!.SetValue(sequencer, Environment.TickCount64 + 12000);
        vm.Shows.TogglePaused();
        snapshot = vm.CaptureState();
        var playback = snapshot.ShowPlayback!;
        t.Equal(0, playback.Index, "snapshot retains the last applied step");
        t.Check(playback.RemainingMs > 11000 && playback.RemainingMs <= 12000, "snapshot retains partially elapsed delay");
        vm.ApplyProfile(b);
        t.Check(!vm.Shows.Running, "override stopped the original show");
        applies = 0;
        vm.RestoreState(snapshot);
        var restored = vm.Shows.CapturePlayback()!;
        t.Check(vm.Shows.Running && vm.Shows.Paused, "restore resumes the paused show as paused");
        t.Equal(playback, restored, "paused restore retains position and remaining delay exactly");
        t.Equal(0, applies, "restoring playback never applies a step immediately");
        vm.Shows.TogglePaused();
        var running = vm.CaptureState();
        vm.ApplyProfile(b); vm.RestoreState(running);
        restored = vm.Shows.CapturePlayback()!;
        t.Check(vm.Shows.Running && !vm.Shows.Paused, "running snapshot resumes running");
        t.Equal(running.ShowPlayback!.Index, restored.Index, "running restore retains position");
        t.Check(restored.RemainingMs <= running.ShowPlayback.RemainingMs && restored.RemainingMs > running.ShowPlayback.RemainingMs - 1000,
            "running restore continues the remaining wait");
        vm.Shows.Stop();
        var stopped = vm.CaptureState();
        vm.Shows.Start("Snapshot Loop"); vm.RestoreState(stopped);
        t.Check(!vm.Shows.Running, "stopped snapshot stops an override show");

        vm.LightsSuppressed = true;
        applies = 0;
        vm.RestoreState(running, honorSuppression: true);
        typeof(SceneSequencer).GetMethod("Step", flags)!.Invoke(sequencer, null);
        t.Equal(0, applies, "restored show steps remain suppressed while lights are off");
        vm.LightsSuppressed = false;
        vm.Shows.Stop();
    }

    static void CheckScenes(Harness t)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var vm = new LcdDesignerViewModel(() => false);
        var lcd = (LcdController)RuntimeHelpers.GetUninitializedObject(typeof(LcdController));
        var lcdField = typeof(LcdDesignerViewModel).GetField("_lcd", flags)!;
        var scenes = (SceneStore)typeof(LcdDesignerViewModel).GetField("_scenes", flags)!.GetValue(vm)!;
        const string name = "Review regression screen";
        scenes.Scenes.Add(new LcdScene { Name = name, Design = LcdDesign.Default() });
        lcdField.SetValue(vm, lcd);
        try
        {
            // A show step is a PROFILE now, and the profile's screen goes up
            // through ShowScreen(fromShow: true). The ownership guarantees did
            // not change, so they are checked where they now live rather than
            // through a step field that no longer exists.
            vm.SelectedSceneName = name;
            var edited = lcd.Design;
            // A real edit keeps the selected scene's name but returns ownership
            // to the canvas. The next show step must reload the saved design.
            edited.Elements.Clear();
            vm.TouchLcd();
            vm.ShowScreen(name, fromShow: true);
            t.Check(!ReferenceEquals(edited, lcd.Design), "show reloads an edited selected screen");
            t.Check(lcd.Design.Elements.Count > 0, "show restores the saved screen elements");

            var showing = lcd.Design;
            vm.ShowScreen(name, fromShow: true);
            t.Check(ReferenceEquals(showing, lcd.Design), "unchanged show step keeps the current design");

            // And the other half of that rule, which is why the flag exists: by
            // hand, a screen already up is left alone EDITS AND ALL, because
            // switching profiles must not stomp a design in progress.
            vm.TouchLcd();
            var userEdited = lcd.Design;
            vm.ShowScreen(name);
            t.Check(ReferenceEquals(userEdited, lcd.Design), "a profile does not reload a screen the user is editing");

            vm.TouchLcd();
            t.Check(!vm.ShowScreen("Deleted screen", fromShow: true), "a missing screen is refused");
            t.Check(vm.SnapshotDesign() is { FromShow: false }, "missing scene leaves canvas ownership intact");
        }
        finally
        {
            // The stub owns no hardware; let the VM stop its save timer without
            // calling Dispose on an uninitialised panel transport.
            lcdField.SetValue(vm, null);
        }
    }
}

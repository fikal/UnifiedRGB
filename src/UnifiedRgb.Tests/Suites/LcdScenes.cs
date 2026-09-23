using System.Reflection;
using System.Runtime.CompilerServices;
using UnifiedRgb.App;
using UnifiedRgb.Core;

namespace UnifiedRgb.Tests;

static class LcdScenesSuite
{
    /// <summary>The LED ranges a view-model is actually running effects on, read
    /// back from its per-target state. The keys carry the range, which is the
    /// whole point of the hardware-remap tests: a saved range is where the user
    /// put the effect, and the test has to see where it ended up.</summary>
    static HashSet<(int Offset, int Count)> LiveRanges(MainViewModel vm, string device)
    {
        const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
        var map = (System.Collections.IDictionary)typeof(MainViewModel).GetField("_targetFx", f)!.GetValue(vm)!;
        var live = new HashSet<(int, int)>();
        foreach (System.Collections.DictionaryEntry e in map)
        {
            var parts = ((string)e.Key).Split('|');
            if (parts.Length != 3 || parts[0] != device) continue;
            var ch = e.Value!.GetType().GetField("Channel")!.GetValue(e.Value);
            if (ch != null) live.Add((int.Parse(parts[1]), int.Parse(parts[2])));
        }
        return live;
    }

    public static void Run(Harness t)
    {
        // WPF editor state needs an STA. No panel is opened: this controller
        // only holds a design, with rendering off and no stream thread.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckScenes(t); ShowThatContainsItsOwnStarter(t); PausingAShow(t); PauseResumeTiming(t); DeletedProfileStep(t); ProfileShowIntegration(t); SnapshotPlayback(t); DragIsOneUndoStep(t); PanelLossAndReturn(t); ScreenAskedForWhileAway(t); ImportedClockIsBounded(t); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Exception("LCD scene regression failed", failure);
    }

    /// <summary>A panel that stops taking frames is let go and looked for
    /// again, and comes back showing the design it had. The stale handle used
    /// to be retried for the rest of the session: Resync talks to the same
    /// handle, the finder stopped once the panel was found, and the RGB rescan
    /// knows nothing about this controller (2026-09-22 review, finding 8).</summary>
    static void PanelLossAndReturn(Harness t)
    {
        t.Section("pump LCD: a panel that stops answering is declared lost");
        // (a) The controller: a run of refused frames is a dead handle, not a
        // bad report, and it says so once on the thread that started it.
        var dead = new FakeHid { Accept = (_, _) => false };
        var dying = new LcdController(new UnifiedRgb.Core.Devices.ThermalrightLcd(dead)) { On = false, LostAfterFailures = 2 };
        bool lost = false;
        var wait = new System.Windows.Threading.DispatcherFrame();
        dying.Lost += () => { lost = true; wait.Continue = false; };
        var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        guard.Tick += (_, _) => { guard.Stop(); wait.Continue = false; };
        dying.Start();
        guard.Start();
        System.Windows.Threading.Dispatcher.PushFrame(wait);
        guard.Stop();
        dying.Dispose();
        t.Check(lost, "a panel refusing every frame is declared lost after a few refusals");

        t.Section("pump LCD: the designer lets a lost panel go and takes it back, design intact");
        // (b) The designer: the dead controller is released, the design (edits
        // included) is kept while the panel is away, and the next probe puts
        // it straight back up.
        using var vm = new LcdDesignerViewModel(() => false);
        var firstHid = new FakeHid();
        var first = new LcdController(new UnifiedRgb.Core.Devices.ThermalrightLcd(firstHid)) { On = false };
        vm.OpenPanel = () => first;
        vm.Start();
        t.Check(vm.Available, "the panel opened at startup");
        int before = vm.LcdElements.Count;
        vm.AddTextCommand.Execute(null);      // an edit made before the unplug
        t.Equal(before + 1, vm.LcdElements.Count, "an element was added before the unplug");
        var design = first.Design;
        design.BgX = 17;                      // something a read-only getter can be seen to answer from

        vm.OnPanelLost();
        t.Check(!vm.Available, "the dead panel is let go");
        t.Check(SpinWait.SpinUntil(() => firstHid.IsDisposed, 4000), "...and its handle is closed, off the UI thread");
        t.Equal(before + 1, vm.LcdElements.Count, "the editor keeps the design while the panel is away");
        t.Equal(17.0, vm.LcdBgX, "the retained design is what the designer answers for meanwhile");
        vm.SetOn(false);                      // a dark window while it is away

        int probes = 0;
        vm.OpenPanel = () => ++probes < 2 ? null : new LcdController(new UnifiedRgb.Core.Devices.ThermalrightLcd(new FakeHid()));
        t.Check(!vm.TryReattach(), "a probe while the panel is still away finds nothing");
        t.Check(!vm.Available, "...and changes nothing");
        t.Check(vm.TryReattach(), "the next probe takes the panel back");
        t.Check(vm.Available, "...and it is available again");
        var second = (LcdController)typeof(LcdDesignerViewModel).GetField("_lcd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        t.Check(ReferenceEquals(second.Design, design), "the returned panel shows the design it had, edits and all");
        t.Equal(before + 1, second.Design.Elements.Count, "...including the element added before the unplug");
        t.Equal(before + 1, vm.LcdElements.Count, "and the editor was not re-listed on top of itself");
        t.Check(!second.On, "the blank asked for while it was away is honoured on its return");
        t.Check(SpinWait.SpinUntil(() => second.Design.Elements.Count == before + 1 && vm.LcdBgX == 17.0, 1000),
            "...and so is the edit: the panel draws the design as it stood, not the one on disk");
    }

    static LcdController FakePanel() => new(new UnifiedRgb.Core.Devices.ThermalrightLcd(new FakeHid())) { On = false };

    /// <summary>What a profile, a show, a restore or the dropdown asks of the
    /// panel while it is away is kept and goes up when the panel is back.
    /// ShowScreen used to return at once with no panel, and the returning panel
    /// restored the design it had retained - so RGB profile B applied during
    /// an unplug came back next to screen A - and a panel that turned up late
    /// at startup never learned what the startup profile wanted (2026-09-23
    /// review, finding 10).</summary>
    static void ScreenAskedForWhileAway(Harness t)
    {
        t.Section("pump LCD: a screen asked for while the panel is away goes up when it is back");
        using var vm = new LcdDesignerViewModel(() => false);
        vm.OpenPanel = FakePanel;
        vm.Start();
        vm.InitScenes();
        vm.SceneNameInput = "Away A"; vm.SaveScene();
        vm.AddTextCommand.Execute(null);   // B differs from A in content, not only in name: a restore compares content
        vm.SceneNameInput = "Away B"; vm.SaveScene();
        t.Check(vm.ShowScreen("Away A") && vm.CurrentScreen == "Away A", "screen A is up while the panel is present");

        vm.OnPanelLost();
        t.Check(!vm.ShowScreen("Away B"), "with the panel away, a profile's screen is reported as not shown right now...");
        t.Equal("Away A", vm.CurrentScreen, "...and the retained design is still A meanwhile");
        t.Check(!vm.ShowScreen("No such screen"), "a screen that does not exist is still refused while the panel is away");
        t.Check(vm.TryReattach(), "the panel comes back");
        t.Equal("Away B", vm.CurrentScreen, "...on the screen asked for while it was away (was: A, the design it had retained)");
        t.Check(vm.SnapshotDesign() is { FromShow: false }, "a profile applied by hand owns the canvas, as it would have with the panel present");

        // A show's step, with the show's ownership.
        vm.OnPanelLost();
        t.Check(!vm.ShowScreen("Away A", fromShow: true), "a show step while away is reported as not shown right now");
        t.Check(vm.TryReattach() && vm.CurrentScreen == "Away A", "...and goes up on the return");
        t.Check(vm.SnapshotDesign() is { FromShow: true }, "...as the show's, not as the canvas");

        // Two requests while away: the newest is the one honoured, as it would
        // be with the panel present.
        vm.OnPanelLost();
        vm.ShowScreen("Away B"); vm.ShowScreen("Away A");
        t.Check(vm.TryReattach() && vm.CurrentScreen == "Away A", "of two requests made while away, the newest is honoured");
        t.Check(vm.SnapshotDesign() is { FromShow: false }, "...as the canvas: the hand-applied profile came last");

        // An automation restore that lands while the panel is away.
        var beforeRule = vm.SnapshotDesign()!;
        t.Check(vm.ShowScreen("Away B"), "a rule's profile puts B up");
        vm.OnPanelLost();
        vm.RestoreDesign(beforeRule);
        t.Check(vm.TryReattach() && vm.CurrentScreen == "Away A",
            "the rule ended while the panel was away: it comes back on the screen from before the rule (was: B)");

        // The user's own pick from the Screens dropdown, made while away.
        vm.OnPanelLost();
        vm.SelectedSceneName = "Away B";
        t.Check(vm.TryReattach() && vm.CurrentScreen == "Away B", "a screen picked from the dropdown while away goes up on the return");
        t.Equal("Away B", vm.SelectedSceneName, "...and the dropdown says so");

        // A panel that first turns up late learns what the startup profile asked for.
        using var late = new LcdDesignerViewModel(() => false);
        late.OpenPanel = () => null;
        late.Start();
        t.Check(!late.Available, "no panel at startup");
        t.Check(!late.ShowScreen("Away A"), "the startup profile's screen is reported as not shown right now");
        late.OpenPanel = FakePanel;
        t.Check(late.TryReattach(), "the panel turns up later");
        t.Equal("Away A", late.CurrentScreen, "...showing what the startup profile asked for (was: whatever lcd.json held)");
    }

    /// <summary>An imported design is the one way past the size slider. A clock
    /// with an absurd size used to reach the editor's per-second bitmap render
    /// as an impossible dimension and throw on every tick (2026-09-23 review,
    /// finding 12).</summary>
    static void ImportedClockIsBounded(Harness t)
    {
        t.Section("pump LCD: an imported element the size of a wall is bounded");
        var d = LcdDesign.Normalize(new LcdDesign
        {
            Elements = new() { new LcdElement { Kind = LcdElementKind.AnalogClock, FontSize = 1e12, X = 1e300, Y = -5e9 } },
            BgW = 1e9, BgH = double.PositiveInfinity, BgX = -1e300,
        });
        var e = d.Elements[0];
        t.Equal(LcdDesign.MaxFontSize, e.FontSize, "the size is clamped to the largest meaningful one");
        t.Equal(LcdDesign.MaxOffset, e.X, "x is clamped");
        t.Equal(-LcdDesign.MaxOffset, e.Y, "y is clamped the other way");
        t.Equal(LcdDesign.MaxBgSize, d.BgW, "the background width is clamped to the editor's own limit");
        t.Equal(0.0, d.BgH, "a non-finite background height means not set");
        t.Equal(-LcdDesign.MaxOffset, d.BgX, "the background offset is clamped");
        var ordinary = LcdDesign.Normalize(new LcdDesign { Elements = new() { new LcdElement { Kind = LcdElementKind.Time, FontSize = 64, X = 70, Y = 70 } } });
        t.Check(ordinary.Elements[0].FontSize == 64 && ordinary.Elements[0].X == 70, "an ordinary design is untouched");

        // The bitmap is bounded on its own too, for an element that reached
        // the editor without passing through Normalize.
        var wall = new LcdElement { Kind = LcdElementKind.AnalogClock, FontSize = 1e12, ColorHex = "FFFFFF" };
        var image = LcdController.RenderClockImage(wall);
        t.Check(image.Width <= LcdDesign.MaxFontSize * 2 + 1, $"the editor's clock bitmap is bounded ({image.Width} px; was: an impossible size, thrown on every tick)");
        var nan = new LcdElement { Kind = LcdElementKind.AnalogClock, FontSize = double.NaN, ColorHex = "FFFFFF" };
        t.Check(LcdController.RenderClockImage(nan).Width > 0, "a NaN size renders a default face rather than throwing");
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

    /// <summary>Run this thread's dispatcher for a while, so DispatcherTimers
    /// (the sequencer's) actually fire. The other sections never pump, which is
    /// why every wait in them is 30 s: nothing was ever seen to fire.</summary>
    static void Pump(int ms)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var stop = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
        stop.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>The sequencer's real work - stop the clock on pause, resume with
    /// the REMAINING delay rather than a fresh full one - observed firing.</summary>
    static void PauseResumeTiming(Harness t)
    {
        t.Section("pause and resume timing");
        int applied = 0;
        var seq = new SceneSequence
        {
            Name = "Timed",
            Actions = { new SceneAction { Profile = "A", DelaySeconds = 0.6 },
                        new SceneAction { Profile = "B", DelaySeconds = 0.6 } },
        };
        var sequencer = new SceneSequencer(_ => applied++);
        sequencer.Start(seq);
        Pump(200);
        t.Equal(0, applied, "timing: nothing fires before the delay");
        sequencer.Paused = true;                      // ~400 ms of the 600 remain
        Pump(800);
        t.Equal(0, applied, "timing: nothing fires while paused, even past the due time");
        sequencer.Paused = false;
        Pump(150);
        t.Equal(0, applied, "timing: resuming does not fire at once");
        Pump(350);                                    // 500 ms since resume: past the remaining 400, short of a fresh 600
        t.Equal(1, applied, "timing: after resume the step fires after the REMAINING delay, not a full one");
        Pump(700);
        t.Equal(2, applied, "timing: the next step follows at its own delay");
        sequencer.Stop();
    }

    /// <summary>A step naming a profile that no longer exists does nothing -
    /// but SAYS so now, in the log and the activity history, instead of
    /// looking like a show stuck on the previous step.</summary>
    static void DeletedProfileStep(Harness t)
    {
        t.Section("a step whose profile was deleted");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var vm = new MainViewModel(startServices: false);
        var lcd = (LcdController)RuntimeHelpers.GetUninitializedObject(typeof(LcdController));
        lcd.Design = LcdDesign.Default();
        typeof(LcdDesignerViewModel).GetField("_lcd", flags)!.SetValue(vm.Lcd, lcd);
        try
        {
            vm.Profiles.Clear();
            var store = vm.Lcd.Scenes;
            store.Scenes.Clear(); store.Sequences.Clear();
            vm.Lcd.InitScenes(); vm.Shows.Init();
            var a = new Profile { Name = "A" };
            vm.Profiles.Add(a);
            vm.ApplyProfile(a);
            UnifiedRgb.Core.Automation.ActivityLog.Shared.Clear();
            typeof(ShowViewModel).GetMethod("ApplyStep", flags)!.Invoke(vm.Shows, new object[] { new SceneAction { Profile = "Gone" } });
            var notes = UnifiedRgb.Core.Automation.ActivityLog.Shared.Snapshot();
            t.Check(notes.Any(n => n.Kind == UnifiedRgb.Core.Automation.ActivityKind.Problem && n.Text.Contains("'Gone'")),
                    "deleted step: the activity history names the missing profile");
            t.Equal("A", vm.AppliedProfileName, "deleted step: the lighting stays on the previous step");
        }
        finally { vm.Dispose(); }
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

            t.Section("a profile saved for different hardware says so");
            {
                // Real case: a board gained a 30-LED strip, every zone after it moved,
                // and the saved effect kept painting LEDs that used to be somewhere
                // else while a third of the board sat dark. Both halves were silent -
                // the frame was truncated to the saved length, and an effect whose
                // range no longer fitted was dropped with a bare continue.
                var grown = new FakeDevice { Name = "Grown board", LedCount = 79 };
                vm.Devices.Add(grown);
                var stale = new Profile { Name = "Saved at 50" };
                stale.DeviceFrames["Grown board"] = Enumerable.Repeat("FF0000", 50).ToArray();
                stale.Effects = new() { new EffectAssignment { Device = "Grown board", Effect = "Matrix", Offset = 0, Count = 50 },
                                        new EffectAssignment { Device = "Grown board", Effect = "Matrix", Offset = 60, Count = 40 } };
                vm.Profiles.Add(stale); profiles.Profiles.Add(stale);
                UnifiedRgb.Core.Automation.ActivityLog.Shared.Clear();
                vm.ApplyProfile(stale);

                var said = UnifiedRgb.Core.Automation.ActivityLog.Shared.Snapshot()
                    .Where(e => e.Kind == UnifiedRgb.Core.Automation.ActivityKind.Problem).ToList();
                t.Equal(1, said.Count, "one notice, not one per symptom");
                string text = said.Count > 0 ? said[0].Text : "";
                t.Check(text.Contains("50") && text.Contains("79"),
                    "...naming both counts, so the user can see what changed");
                t.Check(text.Contains("save it again"), "...and what to do about it");
                t.Check(text.Contains("60-99"), "...including the effect whose range no longer fits");

                // A profile that DOES match is silent - this fires on every apply.
                var fits = new Profile { Name = "Saved at 79" };
                fits.DeviceFrames["Grown board"] = Enumerable.Repeat("00FF00", 79).ToArray();
                vm.Profiles.Add(fits); profiles.Profiles.Add(fits);
                UnifiedRgb.Core.Automation.ActivityLog.Shared.Clear();
                vm.ApplyProfile(fits);
                t.Equal(0, UnifiedRgb.Core.Automation.ActivityLog.Shared.Snapshot()
                         .Count(e => e.Kind == UnifiedRgb.Core.Automation.ActivityKind.Problem),
                    "a profile that matches the hardware says nothing");
                vm.Devices.Remove(grown);
            }

            t.Section("saved effects follow their zone, not its LED numbers");
            {
                // The board this was found on, to the LED. A 30-LED strip went onto
                // the one free ARGB header; because the header list is walked in
                // file order, the new strip took LEDs 0-29 and pushed every existing
                // zone down by 30. "Meteor on 8..37" was the GPU ribbon when it was
                // saved and is the tail of the NEW strip now, so the ribbon went
                // dark and the strip ran somebody else's effect.
                RgbZone Z(string n, int o, int c) => new() { Name = n, Offset = o, Count = c };
                var before = new[] { Z("AIO Fans 1+2", 0, 8), Z("GPU Ribbon", 8, 30), Z("AIO Fan 3", 38, 8),
                                     Z("ARGB Header 1", 46, 1), Z("LED_C", 47, 1), Z("I/O Cover", 48, 1),
                                     Z("Chipset Accent", 49, 1) };
                var after  = new[] { Z("MOBO Ribbon", 0, 30), Z("AIO Fans 1+2", 30, 8), Z("GPU Ribbon", 38, 30),
                                     Z("AIO Fan 3", 68, 8), Z("LED_C", 76, 1), Z("I/O Cover", 77, 1),
                                     Z("Chipset Accent", 78, 1) };
                var board = new FakeDevice { Name = "Board", LedCount = 79, ZoneSpec = after };
                vm.Devices.Add(board);

                var was = new Profile { Name = "Signal Path" };
                was.DeviceZones["Board"] = before
                    .Select(z => new ZoneSpan { Name = z.Name, Offset = z.Offset, Count = z.Count }).ToArray();
                // Ribbon LEDs blue, everything else red, so where the colours land is visible.
                was.DeviceFrames["Board"] = Enumerable.Range(0, 50)
                    .Select(i => i is >= 8 and < 38 ? "0000FF" : "FF0000").ToArray();
                was.Effects = new()
                {
                    new EffectAssignment { Device = "Board", Effect = "Meteor",    Offset = 8,  Count = 30 },
                    new EffectAssignment { Device = "Board", Effect = "Breathing", Offset = 0,  Count = 8  },
                    new EffectAssignment { Device = "Board", Effect = "Breathing", Offset = 38, Count = 8  },
                };
                vm.Profiles.Add(was); profiles.Profiles.Add(was);
                UnifiedRgb.Core.Automation.ActivityLog.Shared.Clear();
                vm.ApplyProfile(was);

                var ranges = LiveRanges(vm, "Board");
                t.Check(ranges.Contains((38, 30)), "the ribbon's effect moved with the ribbon, to 38..67");
                t.Check(ranges.Contains((30, 8)), "AIO Fans 1+2 followed its zone too");
                t.Check(ranges.Contains((68, 8)), "...and AIO Fan 3");
                t.Check(!ranges.Contains((8, 30)), "nothing is left running on the LED numbers the ribbon used to have");

                var frame = vm.Lighting.FrameFor(board);
                t.Equal(new Rgb(0, 0, 255), frame[38], "the ribbon's saved colour moved with it");
                t.Equal(new Rgb(0, 0, 255), frame[67], "...for the whole ribbon");
                t.Equal(new Rgb(255, 0, 0), frame[30], "the fan zone kept its own saved colour");

                // The strip is new, so the profile has nothing to say about it - and
                // says so rather than leaving the user to work out why it is dark.
                var said = UnifiedRgb.Core.Automation.ActivityLog.Shared.Snapshot()
                    .Where(e => e.Kind == UnifiedRgb.Core.Automation.ActivityKind.Problem).ToList();
                t.Equal(1, said.Count, "one notice about hardware this profile has never seen");
                t.Check(said.Count > 0 && said[0].Text.Contains("MOBO Ribbon"),
                    "...naming the zone that is new, not just a pair of LED counts");

                // Same board, same profile, saved AFTER the strip went on: silent,
                // and nothing moves. This runs on every apply.
                var current = new Profile { Name = "Signal Path (resaved)" };
                current.DeviceZones["Board"] = after
                    .Select(z => new ZoneSpan { Name = z.Name, Offset = z.Offset, Count = z.Count }).ToArray();
                current.DeviceFrames["Board"] = Enumerable.Repeat("00FF00", 79).ToArray();
                current.Effects = new() { new EffectAssignment { Device = "Board", Effect = "Meteor", Offset = 38, Count = 30 } };
                vm.Profiles.Add(current); profiles.Profiles.Add(current);
                UnifiedRgb.Core.Automation.ActivityLog.Shared.Clear();
                vm.ApplyProfile(current);
                t.Check(LiveRanges(vm, "Board").Contains((38, 30)), "a profile saved for this hardware is left exactly where it is");
                t.Equal(0, UnifiedRgb.Core.Automation.ActivityLog.Shared.Snapshot()
                         .Count(e => e.Kind == UnifiedRgb.Core.Automation.ActivityKind.Problem),
                    "and says nothing");
                vm.Devices.Remove(board);
            }

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

    /// <summary>A canvas drag is ONE undo step, driven through the view model's
    /// own gesture API rather than the undo stack underneath it. The stack is
    /// tested directly in the Undo suite; what that cannot catch is the view model
    /// failing to CALL it - the editor can hold a perfectly good stack and still
    /// push an entry per mouse-move, which is the bug a user would actually see.</summary>
    static void DragIsOneUndoStep(Harness t)
    {
        t.Section("designer: a drag is one undo step, through the gesture API");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var vm = new MainViewModel(startServices: false);
        var lcd = (LcdController)RuntimeHelpers.GetUninitializedObject(typeof(LcdController));
        lcd.Design = LcdDesign.Default();
        var lcdField = typeof(LcdDesignerViewModel).GetField("_lcd", flags)!;
        lcdField.SetValue(vm.Lcd, lcd);
        try
        {
            var el = lcd.Design.Elements.FirstOrDefault();
            if (el == null) { t.Check(false, "the default design has an element to drag"); return; }
            var vmType = typeof(LcdDesignerViewModel);
            // Hooked and baselined the way loading a design does it, so an edit
            // reaches the editor and the first one has somewhere to step back to.
            vmType.GetMethod("Hook", flags)!.Invoke(vm.Lcd, new object[] { el });
            vmType.GetField("_baseline", flags)!
                .SetValue(vm.Lcd, vmType.GetMethod("Snapshot", flags)!.Invoke(vm.Lcd, null));

            double start = el.X;
            // Mouse down, a dozen moves, mouse up: ONE drag.
            vm.Lcd.BeginGesture();
            for (int i = 1; i <= 12; i++) el.X = start + i;
            vm.Lcd.EndGesture();
            t.Equal(start + 12, el.X, "the drag moved the element");

            vm.Lcd.Undo();
            t.Equal(start, lcd.Design.Elements[0].X,
                "one undo returns to where the drag began, not to the previous mouse-move");

            // And a second drag is its own step rather than joining the first.
            var el2 = lcd.Design.Elements[0];
            vmType.GetMethod("Hook", flags)!.Invoke(vm.Lcd, new object[] { el2 });
            vm.Lcd.BeginGesture();
            for (int i = 1; i <= 5; i++) el2.X = start + 100 + i;
            vm.Lcd.EndGesture();
            vm.Lcd.Undo();
            t.Equal(start, lcd.Design.Elements[0].X, "undoing the second drag leaves the first one undone");
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

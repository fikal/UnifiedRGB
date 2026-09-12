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
            try { CheckScenes(t); ShowThatContainsItsOwnStarter(t); PausingAShow(t); }
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
            currentProfile: () => current,
            showTookThePanel: () => { });

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

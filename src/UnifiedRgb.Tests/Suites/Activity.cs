using UnifiedRgb.Core.Automation;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Activity history and the temporary pause: the two halves of  |
| "explain why the lighting changed".                          |
|                                                              |
| The history is a bounded ring written from three threads and |
| read from a fourth, so the things worth pinning are the ones |
| that are invisible until they go wrong: that it really does  |
| drop the oldest entry rather than growing, that a flapping   |
| rule folds into one line instead of flushing every other     |
| explanation out of the ring, and that the order the UI reads |
| in is the order it was promised. Those three are what make   |
| the panel useful at the moment it is needed.                 |
|                                                              |
| The pause is tested through AutomationPause rather than      |
| AutomationService for the same reason the rest of the        |
| automation is: the service needs a view model, a dispatcher  |
| and a real clock, so every decision it makes lives in Core   |
| as a pure function and the service only performs them. The   |
| suppress/restore pair is therefore expressed exactly as the  |
| service expresses it: resolve the world, resolve the pause,  |
| and check that only the second one refuses to move.          |
\*-----------------------------------------------------------*/
static class ActivitySuite
{
    public static void Run(Harness t)
    {
        RuntimePauseAndCalibration(t);
        WhiteTheAppPicksForYou(t);
        t.Section("Ring bounds itself (#f1)");
        {
            var log = new ActivityLog();
            t.Equal(0, log.Count, "a fresh log is empty");
            t.Equal(0, log.Snapshot().Length, "an empty log snapshots to nothing");

            // Distinct text every time, or the dedup would (correctly) collapse
            // them and this would be testing the wrong thing.
            for (int i = 0; i < ActivityLog.Capacity + 50; i++)
                log.Add(ActivityKind.RuleActivated, $"entry {i}");

            t.Equal(ActivityLog.Capacity, log.Count, "the ring stops at its capacity");
            var all = log.Snapshot();
            t.Equal(ActivityLog.Capacity, all.Length, "the snapshot is the whole ring and no more");

            // Oldest first out of the door: entry 0..49 are gone, 50 is the
            // oldest survivor and the very last write is the newest.
            t.Equal($"entry {ActivityLog.Capacity + 49}", all[0].Text, "newest first: the last write leads");
            t.Equal("entry 50", all[^1].Text, "oldest dropped first: entry 50 is the eldest survivor");
        }

        t.Section("Newest first, in order (#f1)");
        {
            var log = new ActivityLog();
            log.Add(ActivityKind.RuleActivated, "one");
            log.Add(ActivityKind.LightsOff, "two");
            log.Add(ActivityKind.BackToBase, "three");

            var s = log.Snapshot();
            t.Equal(3, s.Length, "three entries in, three out");
            t.Equal("three", s[0].Text, "order: newest is index 0");
            t.Equal("two", s[1].Text, "order: middle entry stays middle");
            t.Equal("one", s[2].Text, "order: oldest is last");
            t.Equal(ActivityKind.BackToBase, s[0].Kind, "the category rides along with the sentence");
        }

        t.Section("A repeat is folded, not appended (#f1)");
        {
            var log = new ActivityLog();
            log.Add(ActivityKind.RuleActivated, "a rule fired");
            log.Add(ActivityKind.RuleActivated, "a rule fired");
            log.Add(ActivityKind.RuleActivated, "a rule fired");

            t.Equal(1, log.Count, "an identical repeat does not take a new slot");
            var s = log.Snapshot();
            t.Equal(3, s[0].Repeats, "the repeat count carries how many times it happened");
            t.Check(s[0].Display.Contains("(x3)"), "the display line shows the repeat count");

            // Same words, different category, is a different event.
            log.Add(ActivityKind.LightsOff, "a rule fired");
            t.Equal(2, log.Count, "the same sentence under another category is its own entry");

            // And a repeat that is no longer the newest entry starts fresh
            // rather than reaching back: the panel reads as a sequence, so an
            // old line must never silently update below a newer one.
            log.Add(ActivityKind.RuleActivated, "a rule fired");
            t.Equal(3, log.Count, "dedup only looks at the newest entry");

            log.Clear();
            t.Equal(0, log.Count, "clear empties the ring");
        }

        t.Section("Entries carry a usable timestamp and category (#f1)");
        {
            var log = new ActivityLog();
            var before = DateTime.Now.AddSeconds(-1);
            log.Add(ActivityKind.SdkClient, "an OpenRGB client took a device");
            var e = log.Snapshot()[0];
            t.Check(e.At >= before && e.At <= DateTime.Now.AddSeconds(1), "the entry is stamped with now");
            t.Equal("SDK client", e.Category, "the category label is the readable one");
            t.Equal(8, e.TimeText.Length, "the time text is HH:mm:ss");
            t.Equal(1, log.Snapshot()[0].Repeats, "a first entry counts as one");

            // Empty text would render as a blank row with a category and no
            // explanation, which is worse than no row at all.
            log.Add(ActivityKind.SdkClient, "");
            t.Equal(1, log.Count, "an empty sentence is refused");
        }

        t.Section("Change notification (#f1)");
        {
            var log = new ActivityLog();
            int fired = 0;
            log.Changed += () => fired++;
            log.Add(ActivityKind.RuleActivated, "first");
            log.Add(ActivityKind.RuleActivated, "first");   // a fold still changes the entry
            log.Clear();
            t.Equal(3, fired, "add, fold and clear all notify the UI");
        }

        t.Section("Pause suppresses a transition, resume restores it (#f3)");
        {
            // The world: a game rule is on, and a scheduled profile window has
            // just opened. Unpaused, the schedule outranks the app rule and the
            // lighting changes under the user; that is the transition the pause
            // has to swallow.
            var rules = new List<AutomationRule> { new() { Process = "cs2.exe", Profile = "Game" } };
            var world = new AutomationInputs
            {
                Locked = false, LockLightsOff = true,
                ScheduleOff = null, ScheduleProfile = new ScheduleHit("20:00", "Evening"),
                SchedulePaused = false, ScheduleWaitingIdle = false, ScheduleEnd = "20:00",
                AppSwitchEnabled = true, ForegroundProcess = "cs2", ForegroundIsSelf = false,
                AppRules = rules, Sensor = null, SensorUnavailable = null,
            };

            var live = AutomationDecision.Resolve(world);
            t.Equal(AutomationMode.ScheduleProfile, live.Mode, "unpaused: the schedule wins");
            t.Equal("Evening", live.Profile, "unpaused: the schedule's profile is applied");

            // Paused while the app rule was driving: that is what is frozen.
            var frozen = AutomationPause.Freeze(AutomationMode.App);
            t.Equal(AutomationMode.App, frozen, "pause freezes the rule that was already running");

            var held = AutomationPause.Resolve(frozen, "Game", locked: false, lockLightsOff: true);
            t.Equal(AutomationMode.App, held.Mode, "paused: the schedule does not take over");
            t.Equal("Game", held.Profile, "paused: the frozen profile stays on");
            t.Check(held.Status.Contains("paused"), "paused: the status says so rather than going quiet");

            // Resume is simply the pause no longer being consulted, so the same
            // world moves again.
            t.Equal(AutomationMode.ScheduleProfile, AutomationDecision.Resolve(world).Mode,
                "resumed: the suppressed transition happens after all");
        }

        t.Section("Pause and the lock screen (#f3)");
        {
            // Chosen behaviour: the lock still wins while paused. The pause is
            // for someone at the machine; locking says they left, and leaving a
            // rig lit all night because automation was paused an hour earlier
            // is not what anyone meant by "pause".
            var locked = AutomationPause.Resolve(AutomationMode.App, "Game", locked: true, lockLightsOff: true);
            t.Equal(AutomationMode.Locked, locked.Mode, "paused: a session lock still turns the lights off");
            t.Check(locked.Status.Contains("locked"), "paused and locked: the status explains the dark");

            // Unlocking puts back exactly what the pause froze, profile and all,
            // rather than falling through to the base lighting.
            var back = AutomationPause.Resolve(AutomationMode.App, "Game", locked: false, lockLightsOff: true);
            t.Equal(AutomationMode.App, back.Mode, "unlocking while paused returns to the frozen mode");
            t.Equal("Game", back.Profile, "unlocking while paused returns the frozen profile");

            // Lights that are already out are the one state not worth freezing:
            // a pause that appears to do nothing is a pause the user retries.
            t.Equal(AutomationMode.Base, AutomationPause.Freeze(AutomationMode.ScheduleOff),
                "pausing during a scheduled dark window brings the lighting back");
            t.Equal(AutomationMode.Base, AutomationPause.Freeze(AutomationMode.Locked),
                "pausing from a locked state does not freeze the dark");
            t.Equal(AutomationMode.Base, AutomationPause.Freeze(AutomationMode.Base),
                "pausing with no rule running holds base");

            // The lock only wins if the user actually asked for lights out on
            // lock; otherwise the pause holds through it.
            var noLockOff = AutomationPause.Resolve(AutomationMode.App, "Game", locked: true, lockLightsOff: false);
            t.Equal(AutomationMode.App, noLockOff.Mode, "paused: no lock blackout when the setting is off");
        }

        t.Section("No em dashes in the pause copy (#f1)");
        {
            // Same house rule the automation status copy is held to, and these
            // strings sit right beside it in the UI.
            foreach (var s in new[] { AutomationPause.Status, AutomationPause.LockedStatus, AutomationPause.Began, AutomationPause.Ended })
                t.Check(!s.Contains('\u2014'), $"pause copy has no em dash: {s}");
        }
    }
    /// <summary>The white the app chooses ON YOUR BEHALF has to be the same
    /// white you get by choosing it yourself. The 60% guard covered only the
    /// paths a person clicks, so an effect started on a black target handed out
    /// full white and put 100% on the brightness slider - the hazard the guard
    /// exists for, and a different answer from the one the same white gives
    /// when you pick it, which is how it was spotted.</summary>
    static void WhiteTheAppPicksForYou(Harness t)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            UnifiedRgb.App.MainViewModel? vm = null;
            try
            {
                vm = new UnifiedRgb.App.MainViewModel(startServices: false);
                var dev = new FakeDevice { Name = "White guard", LedCount = 4 };
                vm.Devices.Add(dev);
                vm.Profiles.Clear();
                vm.SelectedDevice = dev;

                t.Section("the white the app picks for you");

                // The path that always had the guard, as the control.
                vm.Hex = "FFFFFF";
                t.Equal("999999", vm.Hex, "clicking full white lands at 60%");
                t.Equal(153, vm.Brightness, "...and the brightness slider says 60% too");

                // The path that did not. A black target, then an effect that
                // needs a base color: the app picks the white itself.
                vm.Hex = "000000";
                t.Equal(0, vm.Brightness, "the target starts black");
                var mixing = vm.Effects.FirstOrDefault(e => e.Name == "Mixing");
                t.Check(mixing != null, "the Mixing effect is in the library");
                vm.SelectedEffectChoice = mixing;
                t.Equal("999999", vm.Hex, "an effect started on black gets the SAFE white, not full white");
                t.Equal(153, vm.Brightness, "...and reads the same as the white a click would have given");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (vm != null)
                {
                    vm.Lighting.StopAndDrain(); vm.Lcd.Dispose();
                    var bake = (UnifiedRgb.App.Services.LianBakeService)typeof(UnifiedRgb.App.MainViewModel)
                        .GetField("_bake", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
                    bake.Stop();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure != null) throw new Exception("White guard regression", failure);
    }

    static void RuntimePauseAndCalibration(Harness t)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            UnifiedRgb.App.MainViewModel? vm = null;
            try
            {
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                vm = new UnifiedRgb.App.MainViewModel(startServices: false);
                var dev = new FakeDevice { Name = "Pause test", LedCount = 2 };
                vm.Devices.Add(dev);
                vm.Profiles.Clear();
                var a = new UnifiedRgb.App.Profile { Name = "Rule A", DeviceFrames = new() { [dev.Name] = new[] { "FF0000", "FF0000" } } };
                var b = new UnifiedRgb.App.Profile { Name = "Manual B", DeviceFrames = new() { [dev.Name] = new[] { "00FF00", "00FF00" } } };
                b.Effects = new() { new UnifiedRgb.App.EffectAssignment { Device = dev.Name, Offset = 1, Count = 1, Effect = "Breathing", Speed = 1, BaseColor = "00FF00" } };
                vm.Profiles.Add(a); vm.Profiles.Add(b);
                vm.SettingsData.LockLightsOff = true;
                vm.SettingsData.StartupProfile = a.Name;
                vm.SettingsData.ReturnToStartupProfile = true;
                vm.ApplyProfile(a);
                using var service = new UnifiedRgb.App.Services.AutomationService(vm, monitor: false);
                void Set(string name, object value) => typeof(UnifiedRgb.App.Services.AutomationService).GetField(name, flags)!.SetValue(service, value);
                void Tick() => typeof(UnifiedRgb.App.Services.AutomationService).GetMethod("Tick", flags)!.Invoke(service, null);
                Set("_mode", AutomationMode.App); Set("_activeRuleProfile", a.Name);
                service.Paused = true;
                vm.ApplyProfile(b);
                // A subsequent hand edit raises no profile-applied event.
                vm.Lighting.FrameFor(dev)[0] = new UnifiedRgb.Core.Rgb(0, 0, 255);
                typeof(UnifiedRgb.App.MainViewModel).GetField("_dirty", flags)!.SetValue(vm, true);
                Set("_locked", true); Tick();
                t.Check(vm.LightsSuppressed, "real paused service still suppresses lights on lock");
                Set("_locked", false); Tick();
                var restored = vm.CaptureState();
                t.Equal(b.Name, restored.ProfileName, "paused unlock retains the manually selected profile rather than frozen rule A");
                t.Equal(new UnifiedRgb.Core.Rgb(0, 0, 255), restored.Frames[dev.Name][0], "paused unlock restores the latest hand-edited frame");
                t.Check(restored.Dirty, "paused unlock retains the unsaved flag despite startup-profile return being enabled");
                t.Equal(1, vm.Lighting.Engine.ChannelsFor(dev).Count, "paused unlock resumes the live effect");

                // Un-pausing is not itself a transition. It used to force one,
                // and Base means "back to the startup profile": resuming wiped
                // the hand-edited blue, put rule A's red on, and filed it in the
                // activity log as "no rule matches any more". Nothing has
                // changed here - no schedule, no app rule, not locked - so the
                // only correct answer is to leave the lighting alone.
                service.Paused = false;
                var afterResume = vm.CaptureState();
                t.Equal(b.Name, afterResume.ProfileName, "resuming automation with nothing changed keeps the user's profile");
                t.Equal(new UnifiedRgb.Core.Rgb(0, 0, 255), afterResume.Frames[dev.Name][0],
                    "resuming automation with nothing changed keeps the hand-edited frame");
                t.Check(afterResume.Dirty, "resuming automation with nothing changed keeps the unsaved flag");

                UnifiedRgb.App.MainViewModel.LightState? captured = null;
                var aid = new UnifiedRgb.App.Services.CalibrationAid(vm.Lighting,
                    () => captured = vm.CaptureState(),
                    () => vm.RestoreState(captured!, honorSuppression: true), vm.RefreshCalibrationLighting);
                aid.Start(new[] { dev });
                aid.Stop();
                var afterAid = vm.CaptureState();
                t.Equal(restored.Frames[dev.Name][0], afterAid.Frames[dev.Name][0], "real calibration restore retains unsaved colors");
                t.Check(afterAid.Dirty, "real calibration restore retains the unsaved flag");
                t.Equal(b.Name, afterAid.ProfileName, "real calibration restore retains selected profile without loading its saved colors");
                t.Equal(1, vm.Lighting.Engine.ChannelsFor(dev).Count, "calibration restores the live effect");
                aid.Start(new[] { dev });
                vm.Lighting.Applier.Drain(2000);
                vm.LightsOff(); vm.LightsSuppressed = true;
                vm.Lighting.Applier.Drain(2000);
                aid.Stop(); vm.Lighting.Applier.Drain(2000);
                t.Check(dev.Last!.All(c => c == UnifiedRgb.Core.Rgb.Black), "stopping calibration during suppression leaves physical outputs black");
                vm.LightsSuppressed = false;
                vm.Lighting.Engine.StopAll();
                UnifiedRgb.Core.Calibration.Set(dev.Name, new UnifiedRgb.Core.DeviceCalibration { GainB = 0.5 });
                vm.RefreshCalibrationLighting(); vm.Lighting.Applier.Drain(2000);
                t.Equal((byte)128, dev.Last![0].B, "idle calibration refresh immediately updates static hardware");
                var bakeService = (UnifiedRgb.App.Services.LianBakeService)typeof(UnifiedRgb.App.MainViewModel).GetField("_bake", flags)!.GetValue(vm)!;
                var bakeTimer = (System.Windows.Threading.DispatcherTimer)typeof(UnifiedRgb.App.Services.LianBakeService).GetField("_timer", flags)!.GetValue(bakeService)!;
                t.Check(bakeTimer.IsEnabled, "idle calibration refresh requests a bake refresh");
                UnifiedRgb.Core.Calibration.ResetAll();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (vm != null)
                {
                    UnifiedRgb.Core.Calibration.ResetAll();
                    vm.Lighting.StopAndDrain(); vm.Lcd.Dispose();
                    var bake = (UnifiedRgb.App.Services.LianBakeService)typeof(UnifiedRgb.App.MainViewModel)
                        .GetField("_bake", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
                    bake.Stop();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure != null) throw new Exception("Runtime pause/calibration regression", failure);
    }
}

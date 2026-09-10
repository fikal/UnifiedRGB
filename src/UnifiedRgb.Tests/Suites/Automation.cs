using System.Text.Json;
using UnifiedRgb.Core.Automation;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Automation: everything that changes the lights without the   |
| user touching them, and the order those triggers resolve in. |
|                                                              |
| The app has three independent ways to decide what should be  |
| lit right now: a rule that watches the foreground process, a |
| schedule that owns a window of the week, and a sensor rule.  |
| They overlap constantly, so the interesting behaviour is not |
| any one trigger but the precedence between them, which is    |
| why the matching, the window maths and the resolver live in  |
| one file and are read top to bottom.                         |
|                                                              |
| The schedule sections carry the most detail because the      |
| window maths has the one genuinely tricky rule in the area:  |
| a window that ends before it starts runs overnight and       |
| belongs to the day it STARTED, so 01:00 Tuesday is governed  |
| by Monday's day bit. Round-tripping the day bits is here for |
| the same reason the sensor rule's shape is tested: the mask  |
| is what persists to settings.json, and the seven per-day     |
| flags are view sugar that must never reach the file.         |
|                                                              |
| The last section guards the status strings rather than the   |
| decision. They are user-visible copy, so they are held to    |
| the house no-em-dash rule the same way the UI is.            |
\*-----------------------------------------------------------*/
static class AutomationSuite
{
    public static void Run(Harness t)
    {
        t.Section("App rule matching (#f1)");
        {
            var rules = new List<AutomationRule>
            {
                new() { Process = "", Profile = "Blank" },
                new() { Process = "cs2.exe", Profile = "Game" },
                new() { Process = "chrome", Profile = "Browse" },
            };
            t.Equal("Game", AutomationRule.Match(rules, "cs2"), "app match: .exe suffix tolerated");
            t.Equal("Browse", AutomationRule.Match(rules, "chrome"), "app match: plain name");
            t.Equal("Browse", AutomationRule.Match(rules, "CHROME"), "app match: case insensitive");
            t.Check(AutomationRule.Match(rules, "notepad") == null, "app match: no rule");
            t.Check(AutomationRule.Match(rules, null) == null, "app match: null process");
            t.Check(AutomationRule.Match(null, "cs2") == null, "app match: null rules");
            // A half-filled rule must never swallow every process.
            t.Check(AutomationRule.Match(new List<AutomationRule> { new() { Process = "", Profile = "X" } }, "anything") == null,
                "app match: blank rule matches nothing");
        }

        t.Section("Schedules: window math (#f2)");
        {
            // Monday is bit 0. A window that ends before it starts runs overnight and
            // belongs to the day it STARTED.
            ScheduleRule Night(int days = 0x7F) => new() { Start = "23:00", End = "07:00", Days = days };
            ScheduleRule Evening(int days = 0x1F) => new() { Start = "18:00", End = "20:00", Days = days, Action = ScheduleAction.Profile, Profile = "Evening" };

            var monday = new DateTime(2026, 9, 7);      // a Monday
            var tuesday = new DateTime(2026, 9, 8);
            var saturday = new DateTime(2026, 9, 12);

            t.Equal(0, ScheduleRule.BitOf(DayOfWeek.Monday), "days: Monday is bit 0");
            t.Equal(6, ScheduleRule.BitOf(DayOfWeek.Sunday), "days: Sunday is bit 6");
            t.Equal(5, ScheduleRule.BitOf(DayOfWeek.Saturday), "days: Saturday is bit 5");

            // Same-day window.
            t.Check(ScheduleRule.InWindow(Evening(), monday.AddHours(19)), "window: inside a weekday evening");
            t.Check(!ScheduleRule.InWindow(Evening(), monday.AddHours(17)), "window: before it opens");
            t.Check(!ScheduleRule.InWindow(Evening(), monday.AddHours(20)), "window: the end is exclusive");
            t.Check(!ScheduleRule.InWindow(Evening(), saturday.AddHours(19)), "window: not on a day it does not run");

            // Overnight window, the acceptance case.
            t.Check(ScheduleRule.InWindow(Night(), monday.AddHours(23.5)), "overnight: open on the evening it starts");
            t.Check(ScheduleRule.InWindow(Night(), tuesday.AddHours(1)), "overnight: still open after midnight");
            t.Check(!ScheduleRule.InWindow(Night(), tuesday.AddHours(8)), "overnight: closed after the end time");

            // 01:00 Tuesday belongs to MONDAY's window, so only Monday's bit matters.
            int mondayOnly = 1 << 0, tuesdayOnly = 1 << 1;
            t.Check(ScheduleRule.InWindow(Night(mondayOnly), tuesday.AddHours(1)), "overnight: 01:00 Tue runs on Monday's bit");
            t.Check(!ScheduleRule.InWindow(Night(tuesdayOnly), tuesday.AddHours(1)), "overnight: Tuesday's bit does not cover 01:00 Tue");
            t.Check(ScheduleRule.InWindow(Night(tuesdayOnly), tuesday.AddHours(23.5)), "overnight: Tuesday's bit covers Tue evening");

            // Degenerate and disabled rules never open.
            t.Check(!ScheduleRule.InWindow(new ScheduleRule { Start = "12:00", End = "12:00" }, monday.AddHours(12)), "window: zero length never opens");
            t.Check(!ScheduleRule.InWindow(new ScheduleRule { Start = "bad", End = "07:00" }, monday.AddHours(1)), "window: unparseable time never opens");
            t.Check(!ScheduleRule.InWindow(Night(0), monday.AddHours(23.5)), "window: no days selected never opens");

            // IsActive folds in Enabled and the idle wait.
            var idle = Night(); idle.IdleOnly = true;
            t.Check(!ScheduleRule.IsActive(idle, monday.AddHours(23.5), 60, 600), "active: idle-only waits while you are here");
            t.Check(ScheduleRule.IsActive(idle, monday.AddHours(23.5), 700, 600), "active: idle-only fires once away");
            var offRule = Night(); offRule.Enabled = false;
            t.Check(!ScheduleRule.IsActive(offRule, monday.AddHours(23.5), 0, 600), "active: a disabled rule never fires");
        }

        t.Section("Schedules: next change and description (#f2)");
        {
            var monday = new DateTime(2026, 9, 7, 12, 0, 0);
            var evening = new ScheduleRule { Start = "18:00", End = "20:00", Days = 0x1F, Action = ScheduleAction.Profile, Profile = "Evening" };
            var night = new ScheduleRule { Start = "23:00", End = "07:00", Days = 0x7F };

            var next = ScheduleRule.NextChange(new[] { night, evening }, monday);
            t.Check(next != null && next.Value.When == monday.Date.AddHours(18), "next: the sooner of two windows wins");
            t.Check(next != null && ReferenceEquals(next.Value.Rule, evening), "next: reports which rule it is");

            // Past today's start, it rolls to the next day the rule runs.
            var late = new DateTime(2026, 9, 11, 21, 0, 0);      // Friday evening, after 18:00
            var afterFri = ScheduleRule.NextChange(new[] { evening }, late);
            t.Equal(new DateTime(2026, 9, 14, 18, 0, 0), afterFri?.When ?? default, "next: weekday rule skips the weekend");

            t.Check(ScheduleRule.NextChange(Array.Empty<ScheduleRule>(), monday) == null, "next: nothing scheduled");
            var disabled = new ScheduleRule { Enabled = false, Start = "18:00", End = "20:00" };
            t.Check(ScheduleRule.NextChange(new[] { disabled }, monday) == null, "next: disabled rules are ignored");

            t.Equal("Every day", ScheduleRule.DaysText(0x7F), "days text: every day");
            t.Equal("Weekdays", ScheduleRule.DaysText(0x1F), "days text: weekdays");
            t.Equal("Weekends", ScheduleRule.DaysText(0x60), "days text: weekends");
            t.Equal("Mon Wed Fri", ScheduleRule.DaysText(0b0010101), "days text: a custom set");
            t.Equal("Every day 23:00 to 07:00, lights off", ScheduleRule.Describe(night), "describe: a lights-off window");
            t.Equal("Weekdays 18:00 to 20:00, apply Evening", ScheduleRule.Describe(evening), "describe: a profile window");
        }

        t.Section("Schedules: day bits round-trip (#f2)");
        {
            // The editor toggles seven check boxes; the file stores one int.
            var r = new ScheduleRule { Days = 0 };
            r.Mon = true; r.Wed = true; r.Sun = true;
            t.Equal(0b1000101, r.Days, "day bits: setting flags builds the mask");
            t.Check(r.Mon && r.Wed && r.Sun && !r.Tue && !r.Sat, "day bits: reading flags back");
            r.Mon = false;
            t.Equal(0b1000100, r.Days, "day bits: clearing a flag");

            // Days is what persists; the flags are view sugar and must not be written.
            string json = JsonSerializer.Serialize(new ScheduleRule { Days = 0x1F, Start = "18:00", End = "20:00", Action = ScheduleAction.Profile, Profile = "Evening", IdleOnly = true });
            foreach (var f in new[] { "Days", "Start", "End", "Action", "Profile", "IdleOnly", "Enabled" })
                t.Check(json.Contains($"\"{f}\""), $"ScheduleRule json carries {f}");
            foreach (var f in new[] { "Mon", "Tue", "IsProfileAction" })
                t.Check(!json.Contains($"\"{f}\""), $"ScheduleRule json omits the view-only {f}");

            var back = JsonSerializer.Deserialize<ScheduleRule>(json)!;
            t.Equal(0x1F, back.Days, "ScheduleRule round-trip: days");
            t.Equal(ScheduleAction.Profile, back.Action, "ScheduleRule round-trip: action");
            t.Equal("Evening", back.Profile, "ScheduleRule round-trip: profile");
            t.Check(back.IdleOnly, "ScheduleRule round-trip: idle only");

            var bare = JsonSerializer.Deserialize<ScheduleRule>("{}")!;
            t.Check(bare.Enabled && bare.Days == 0x7F, "ScheduleRule defaults: enabled, every day");
            t.Equal(ScheduleAction.LightsOff, bare.Action, "ScheduleRule defaults to lights off");
        }

        t.Section("Automation precedence (#f1)");
        {
            var appRules = new List<AutomationRule> { new() { Process = "cs2", Profile = "Game" } };
            var hit = new SensorHit(SensorSources.CpuTemp, "Alert", 90, 85, true);

            AutomationInputs Make(bool locked = false, bool off = false, bool sensor = false,
                                  bool app = false, bool schedProfile = false, bool lockOff = true) => new()
            {
                Locked = locked,
                LockLightsOff = lockOff,
                ScheduleOff = off ? new ScheduleHit("07:00", null) : null,
                ScheduleProfile = schedProfile ? new ScheduleHit("22:00", "Evening") : null,
                SchedulePaused = false,
                ScheduleWaitingIdle = false,
                ScheduleEnd = "07:00",
                AppSwitchEnabled = true,
                ForegroundProcess = app ? "cs2" : "notepad",
                ForegroundIsSelf = false,
                AppRules = appRules,
                Sensor = sensor ? hit : null,
                SensorUnavailable = null,
            };

            t.Equal(AutomationMode.Base, AutomationDecision.Resolve(Make()).Mode, "precedence: nothing yields Base");
            t.Equal(AutomationMode.App, AutomationDecision.Resolve(Make(app: true)).Mode, "precedence: app rule");
            t.Equal(AutomationMode.ScheduleProfile, AutomationDecision.Resolve(Make(schedProfile: true)).Mode, "precedence: scheduled profile");
            t.Equal(AutomationMode.ScheduleProfile, AutomationDecision.Resolve(Make(schedProfile: true, app: true)).Mode, "precedence: scheduled profile beats app");
            t.Equal(AutomationMode.Sensor, AutomationDecision.Resolve(Make(sensor: true)).Mode, "precedence: sensor alone");
            t.Equal(AutomationMode.Sensor, AutomationDecision.Resolve(Make(sensor: true, app: true)).Mode, "precedence: sensor beats app");
            t.Equal(AutomationMode.Sensor, AutomationDecision.Resolve(Make(sensor: true, schedProfile: true)).Mode, "precedence: sensor beats a scheduled profile");
            t.Equal(AutomationMode.ScheduleOff, AutomationDecision.Resolve(Make(off: true, sensor: true, app: true)).Mode, "precedence: scheduled dark beats sensor");
            // Overlapping windows: dark wins outright, and the profile takes over only
            // once the dark window closes.
            t.Equal(AutomationMode.ScheduleOff, AutomationDecision.Resolve(Make(off: true, schedProfile: true)).Mode, "precedence: scheduled dark beats a scheduled profile");
            t.Check(AutomationDecision.Resolve(Make(off: true, schedProfile: true)).Profile == null, "precedence: an overlapped profile is not applied");
            t.Equal(AutomationMode.ScheduleProfile, AutomationDecision.Resolve(Make(off: false, schedProfile: true)).Mode, "precedence: the profile takes over when the dark closes");
            t.Equal(AutomationMode.Locked, AutomationDecision.Resolve(Make(locked: true, off: true, sensor: true, app: true)).Mode, "precedence: locked beats all");

            // The winning profile travels with the mode.
            t.Equal("Alert", AutomationDecision.Resolve(Make(sensor: true, app: true)).Profile, "precedence: sensor profile wins");
            t.Equal("Game", AutomationDecision.Resolve(Make(app: true)).Profile, "precedence: app profile applies");
            t.Equal("Evening", AutomationDecision.Resolve(Make(schedProfile: true, app: true)).Profile, "precedence: scheduled profile applies");
            t.Check(AutomationDecision.Resolve(Make(off: true)).Profile == null, "precedence: scheduled dark carries no profile");

            // Unlocking with the sensor still hot lands in Sensor, not Base.
            t.Equal(AutomationMode.Sensor, AutomationDecision.Resolve(Make(locked: false, sensor: true)).Mode,
                "precedence: unlock into a live sensor rule");

            // Lock without the lights-off setting is not an override at all.
            t.Equal(AutomationMode.App, AutomationDecision.Resolve(Make(locked: true, app: true, lockOff: false)).Mode,
                "precedence: locked without lights-off is ignored");
        }

        t.Section("Automation night window + status (#f1)");
        {
            // The caller decides whether a window is open; these are the four shapes
            // it can hand in.
            AutomationInputs Sched(bool off, bool paused = false, bool waiting = false) => new()
            {
                Locked = false, LockLightsOff = true,
                ScheduleOff = off ? new ScheduleHit("07:00", null) : null,
                ScheduleProfile = null, SchedulePaused = paused, ScheduleWaitingIdle = waiting,
                ScheduleEnd = "07:00",
                AppSwitchEnabled = false, ForegroundProcess = null, ForegroundIsSelf = false,
                AppRules = null, Sensor = null, SensorUnavailable = null,
            };
            t.Equal(AutomationMode.ScheduleOff, AutomationDecision.Resolve(Sched(true)).Mode, "schedule: open window turns the lights off");
            t.Equal(AutomationMode.Base, AutomationDecision.Resolve(Sched(false, waiting: true)).Mode, "schedule: waiting for idle leaves the lights on");
            t.Equal(AutomationMode.Base, AutomationDecision.Resolve(Sched(false, paused: true)).Mode, "schedule: an override keeps the lights on");

            t.Check(AutomationDecision.Resolve(Sched(true)).Status.Contains("07:00"), "schedule status names the end time");
            t.Check(AutomationDecision.Resolve(Sched(false, paused: true)).Status.Contains("paused"), "schedule status explains the override");
            t.Check(AutomationDecision.Resolve(Sched(false, waiting: true)).Status.Contains("idle"), "schedule status explains the idle wait");

            // The sensor status carries the numbers behind the decision.
            var withSensor = new AutomationInputs
            {
                Locked = false, LockLightsOff = true, ScheduleOff = null, ScheduleProfile = null,
                SchedulePaused = false, ScheduleWaitingIdle = false, ScheduleEnd = "",
                AppSwitchEnabled = false, ForegroundProcess = null, ForegroundIsSelf = false,
                AppRules = null, Sensor = new SensorHit(SensorSources.CpuTemp, "Alert", 87, 85, true),
                SensorUnavailable = null,
            };
            t.Check(AutomationDecision.Resolve(withSensor).Status.Contains("87°C"), "sensor status shows the reading");
            t.Check(AutomationDecision.Resolve(withSensor).Status.Contains("'Alert'"), "sensor status names the profile");

            // No reading is explained rather than silently doing nothing.
            var missing = new AutomationInputs
            {
                Locked = false, LockLightsOff = true, ScheduleOff = null, ScheduleProfile = null,
                SchedulePaused = false, ScheduleWaitingIdle = false, ScheduleEnd = "",
                AppSwitchEnabled = false, ForegroundProcess = null, ForegroundIsSelf = false,
                AppRules = null, Sensor = null, SensorUnavailable = SensorSources.CpuTemp,
            };
            t.Check(AutomationDecision.Resolve(missing).Status.Contains("PawnIO"), "missing sensor status mentions PawnIO");
            t.Equal(AutomationMode.Base, AutomationDecision.Resolve(missing).Mode, "missing sensor does not change the mode");
        }

        t.Section("No em dashes in automation status copy (#f1)");
        {
            // House style, and these strings are user-visible.
            var seen = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                var x = new AutomationInputs
                {
                    Locked = false, LockLightsOff = true,
                    ScheduleOff = i == 0 ? new ScheduleHit("07:00", null) : null,
                    ScheduleProfile = null, SchedulePaused = i == 1, ScheduleWaitingIdle = i == 2,
                    ScheduleEnd = "07:00",
                    AppSwitchEnabled = true, ForegroundProcess = i == 3 ? "notepad" : null,
                    ForegroundIsSelf = false, AppRules = null, Sensor = null, SensorUnavailable = null,
                };
                seen.Add(AutomationDecision.Resolve(x).Status);
            }
            t.Check(seen.TrueForAll(s => !s.Contains('\u2014')), "automation status copy has no em dashes");
        }
    }
}

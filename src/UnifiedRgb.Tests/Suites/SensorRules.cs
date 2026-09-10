using System.Text.Json;
using UnifiedRgb.Core.Automation;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Sensor-driven automation: the rule record, the evaluator     |
| that decides when a rule is on, and the source catalogue     |
| the rules point at.                                          |
|                                                              |
| These belong together because they are one decision path.    |
| A rule is read back from settings.json, so its persisted     |
| shape is a contract with files already on disk; the          |
| evaluator then turns a stream of readings into on/off, and   |
| the source catalogue says what a reading is called, what     |
| unit it carries, and whether reading it costs a full         |
| hardware sweep.                                              |
|                                                              |
| The evaluator gets four sections of its own because the      |
| failure everyone actually hits is a profile that flaps: a    |
| value sitting on the threshold must not toggle the machine   |
| every poll. Hysteresis and the hold are tested separately,   |
| then together against the oscillating case that motivated    |
| them, then in the below-threshold direction a battery rule   |
| uses, and finally for the list-order tie-break that picks    |
| which of several active rules wins.                          |
\*-----------------------------------------------------------*/
static class SensorRulesSuite
{
    public static void Run(Harness t)
    {
        t.Section("SensorRule persisted shape (#f1)");
        {
            // The rules live in settings.json, so the field names are a contract with
            // files already on disk. An older build strips what it does not know, so a
            // rule must also survive being read back with fields missing.
            var r = new SensorRule { Source = "Board:Fan #2", Above = false, Threshold = 42.5, ClearMargin = 2, HoldSeconds = 7, Profile = "Cool", Enabled = false };
            string json = JsonSerializer.Serialize(r);
            foreach (var field in new[] { "Source", "Above", "Threshold", "ClearMargin", "HoldSeconds", "Profile", "Enabled" })
                t.Check(json.Contains($"\"{field}\""), $"SensorRule json carries {field}");

            var back = JsonSerializer.Deserialize<SensorRule>(json)!;
            t.Equal(r.Source, back.Source, "SensorRule round-trip: source");
            t.Equal(r.Above, back.Above, "SensorRule round-trip: direction");
            t.Equal(r.Threshold, back.Threshold, "SensorRule round-trip: threshold");
            t.Equal(r.HoldSeconds, back.HoldSeconds, "SensorRule round-trip: hold");
            t.Equal(r.Enabled, back.Enabled, "SensorRule round-trip: enabled");

            // Defaults must be sane for a rule an older build wrote back stripped.
            var bare = JsonSerializer.Deserialize<SensorRule>("{\"Profile\":\"X\"}")!;
            t.Equal(SensorSources.CpuTemp, bare.Source, "SensorRule default source");
            t.Check(bare.Above, "SensorRule defaults to above");
            t.Check(bare.Enabled, "SensorRule defaults to enabled");
            t.Check(bare.ClearMargin > 0 && bare.HoldSeconds > 0, "SensorRule defaults cannot chatter");
        }

        t.Section("Sensor rules: hysteresis + hold (#f1)");
        {
            // Rule: fire at or above 85, release below 82, both after a 5 s hold.
            var rule = new SensorRule { Source = SensorSources.CpuTemp, Above = true, Threshold = 85, ClearMargin = 3, HoldSeconds = 5, Profile = "Alert" };
            var st = default(SensorRuleState);

            // Below the line: nothing pending, nothing active.
            st = SensorRuleEvaluator.Step(rule, 70, st, 0);
            t.Check(!st.Active && st.SinceSeconds == null, "sensor: cold value is inactive");

            // Over the line, but the hold has not elapsed.
            st = SensorRuleEvaluator.Step(rule, 86, st, 10);
            t.Check(!st.Active && st.SinceSeconds == 10, "sensor: hold starts, not yet active");
            st = SensorRuleEvaluator.Step(rule, 86, st, 14);
            t.Check(!st.Active, "sensor: still holding at 4 s");
            st = SensorRuleEvaluator.Step(rule, 86, st, 15);
            t.Check(st.Active && st.SinceSeconds == null, "sensor: fires once the hold elapses");

            // Inside the hysteresis band: stays on.
            st = SensorRuleEvaluator.Step(rule, 84, st, 20);
            t.Check(st.Active, "sensor: 84 is inside the band, stays on");
            st = SensorRuleEvaluator.Step(rule, 83, st, 25);
            t.Check(st.Active, "sensor: 83 is inside the band, stays on");

            // Past the margin, held, then released.
            st = SensorRuleEvaluator.Step(rule, 81, st, 30);
            t.Check(st.Active && st.SinceSeconds == 30, "sensor: release hold starts");
            st = SensorRuleEvaluator.Step(rule, 81, st, 35);
            t.Check(!st.Active, "sensor: releases after the hold");

            // A null reading (no PawnIO) is always inactive and forgets the hold.
            var pending = new SensorRuleState(false, 100);
            t.Check(!SensorRuleEvaluator.Step(rule, null, pending, 101).Active, "sensor: null reading is inactive");
            t.Check(SensorRuleEvaluator.Step(rule, null, pending, 101).SinceSeconds == null, "sensor: null reading clears the hold");
        }

        t.Section("Sensor rules: the flapping case (#f1)");
        {
            // The acceptance case: oscillating 84/86 either side of an 85 rule must
            // never toggle, because the value never clears the margin.
            var rule = new SensorRule { Threshold = 85, ClearMargin = 3, HoldSeconds = 5, Profile = "Alert" };
            var st = default(SensorRuleState);
            bool everActive = false;
            for (int i = 0; i < 200; i++)
            {
                st = SensorRuleEvaluator.Step(rule, i % 2 == 0 ? 86 : 84, st, i * 2.0);
                everActive |= st.Active;
            }
            t.Check(!everActive, "sensor: 84/86 flapping never fires (hold resets)");

            // Once genuinely hot it fires, and then the same flapping cannot drop it.
            var st2 = default(SensorRuleState);
            for (int i = 0; i < 5; i++) st2 = SensorRuleEvaluator.Step(rule, 90, st2, i * 2.0);
            t.Check(st2.Active, "sensor: sustained heat fires");
            bool everCleared = false;
            for (int i = 0; i < 200; i++)
            {
                st2 = SensorRuleEvaluator.Step(rule, i % 2 == 0 ? 86 : 84, st2, 100 + i * 2.0);
                everCleared |= !st2.Active;
            }
            t.Check(!everCleared, "sensor: 84/86 flapping never releases (inside the band)");
        }

        t.Section("Sensor rules: below-threshold direction (#f1)");
        {
            // "At or below 15%", e.g. a battery rule. Releases above 15 + margin.
            var rule = new SensorRule { Source = "Battery:Mouse", Above = false, Threshold = 15, ClearMargin = 5, HoldSeconds = 0, Profile = "Low" };
            var st = default(SensorRuleState);
            st = SensorRuleEvaluator.Step(rule, 15, st, 0);
            t.Check(st.Active, "sensor: below-rule fires at the threshold");
            st = SensorRuleEvaluator.Step(rule, 18, st, 1);
            t.Check(st.Active, "sensor: below-rule holds inside the band");
            st = SensorRuleEvaluator.Step(rule, 21, st, 2);
            t.Check(!st.Active, "sensor: below-rule releases past the margin");
        }

        t.Section("Sensor rules: FirstActive picks by list order (#f1)");
        {
            var rules = new List<SensorRule>
            {
                new() { Source = SensorSources.CpuTemp, Threshold = 80, Profile = "First", Enabled = false },
                new() { Source = SensorSources.GpuTemp, Threshold = 80, Profile = "Second" },
                new() { Source = SensorSources.Hottest, Threshold = 80, Profile = "Third" },
                new() { Source = SensorSources.CpuTemp, Threshold = 80, Profile = "Gone" },
            };
            var states = new[]
            {
                new SensorRuleState(true, null), new SensorRuleState(true, null),
                new SensorRuleState(true, null), new SensorRuleState(true, null),
            };
            var values = new double?[] { 90, 91, 92, 93 };

            var hit = SensorRuleEvaluator.FirstActive(rules, states, values);
            t.Equal("Second", hit?.Profile, "sensor: disabled rule is skipped, next wins");

            // A rule pointing at a deleted profile is skipped, not applied blank.
            var only = new List<SensorRule> { rules[3] };
            var hit2 = SensorRuleEvaluator.FirstActive(only, new[] { new SensorRuleState(true, null) },
                new double?[] { 93 }, name => name != "Gone");
            t.Check(hit2 == null, "sensor: rule with a deleted profile is skipped");

            // Nothing active at all.
            t.Check(SensorRuleEvaluator.FirstActive(rules, new SensorRuleState[4], values) == null,
                "sensor: no active rule yields no hit");
            t.Check(SensorRuleEvaluator.FirstActive(null, states, values) == null, "sensor: null rule list is safe");
        }

        t.Section("Sensor sources: labels, units, poll gating (#f1)");
        {
            t.Equal("CPU temp", SensorSources.Label(SensorSources.CpuTemp), "source label: cpu temp");
            t.Equal("°C", SensorSources.Unit(SensorSources.CpuTemp), "source unit: cpu temp");
            t.Equal("%", SensorSources.Unit(SensorSources.GpuLoad), "source unit: gpu load");
            t.Equal("MB Temp #3", SensorSources.Label(SensorSources.BoardPrefix + "Temperature #3"), "source label: board temp is short and marked MB");
            t.Equal("CPU Fan", SensorSources.Label(SensorSources.FanPrefix + "CPU Fan"), "source label: fan name stands alone");

            // Only the sources that need the expensive sweep should ask for it.
            t.Check(!SensorSources.NeedsFullSweep(SensorSources.CpuTemp), "gating: cpu temp uses the cheap touch");
            t.Check(!SensorSources.NeedsFullSweep(SensorSources.Hottest), "gating: hottest uses the cheap touch");
            t.Check(SensorSources.NeedsFullSweep(SensorSources.GpuLoad), "gating: gpu load needs the full sweep");
            t.Check(SensorSources.NeedsFullSweep(SensorSources.FanPrefix + "CPU Fan"), "gating: fan rpm needs the full sweep");

            t.Equal("MB Temp #1 (IT87952E)", SensorSources.Label(SensorSources.BoardPrefix + "Temperature #1 (IT87952E)"),
                "source label: second-chip sensor keeps its qualifier");

            var hit = new SensorHit(SensorSources.CpuTemp, "Alert", 87.4, 85, true);
            t.Equal("CPU temp 87°C at or above 85°C", hit.Describe(), "sensor hit describes itself");
        }
    }
}

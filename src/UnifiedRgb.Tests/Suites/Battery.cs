using UnifiedRgb.Core;
using UnifiedRgb.Core.Automation;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Sensors;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| What a wireless device reports about its charge, and the     |
| now-playing line that shares the same reporting path.        |
|                                                              |
| The decode is the part that has to be right: Razer answers   |
| a raw 0..255 rather than a percentage, and a mouse that is   |
| merely asleep answers with a timeout status. Reading either  |
| of those as a number would make a low-battery rule fire      |
| every night, so the decoder returns null for anything it     |
| cannot vouch for, and the section spells out every status    |
| byte that means "no reading" rather than "flat".             |
|                                                              |
| A charge is then published to the sensor hub and read back   |
| as an ordinary rule source, which is the whole point of      |
| decoding it: the third section drives a real low-battery     |
| rule through the evaluator to prove the two halves meet.     |
|                                                              |
| The now-playing line rides along because it is the same      |
| kind of surface: a short piece of text composed from what a  |
| device or player reports, trimmed and ellipsized so a        |
| podcast episode title cannot overrun whatever is showing it. |
\*-----------------------------------------------------------*/
static class BatterySuite
{
    public static void Run(Harness t)
    {
        t.Section("Now-playing line (#f5)");
        {
            t.Equal("Radiohead · Karma Police", NowPlayingText.Compose("Radiohead", "Karma Police"),
                  "now playing: artist and title");
            t.Equal("Karma Police", NowPlayingText.Compose(null, "Karma Police"), "now playing: title only");
            t.Equal("Radiohead", NowPlayingText.Compose("Radiohead", ""), "now playing: artist only");
            t.Equal("", NowPlayingText.Compose(null, null), "now playing: nothing playing is empty");
            t.Equal("", NowPlayingText.Compose("   ", " "), "now playing: whitespace is nothing");
            t.Equal("A · B", NowPlayingText.Compose("  A  ", "  B  "), "now playing: ends are trimmed");

            // A separator that reads as part of a name is worse than none. No dashes.
            t.Check(!NowPlayingText.Separator.Contains('-') && !NowPlayingText.Separator.Contains('—'),
                  "now playing: the separator is not a dash");

            t.Equal("abc", NowPlayingText.Ellipsize("abc", 3), "ellipsis: what fits is left alone");
            t.Equal("abc…", NowPlayingText.Ellipsize("abcdef", 4), "ellipsis: cut to the budget");
            t.Equal("ab…", NowPlayingText.Ellipsize("ab cdef", 4), "ellipsis: no space stranded before it");
            t.Equal("…", NowPlayingText.Ellipsize("abcdef", 1), "ellipsis: a budget of one");
            t.Equal("", NowPlayingText.Ellipsize("abcdef", 0), "ellipsis: no budget at all");

            // A podcast episode title should never reach the typesetter whole.
            var epic = NowPlayingText.Compose("Some Podcast", new string('x', 500));
            t.Equal(NowPlayingText.MaxChars, epic.Length, "now playing: a huge title is capped");
            t.Check(epic.EndsWith('…'), "now playing: and marked as cut");
        }

        t.Section("Razer battery decode (#f4)");
        {
            // A reply buffer: [1] = status, arguments[0] at index 9, so the byte both
            // battery commands answer in (arguments[1]) is index 10.
            static byte[] Reply(byte status, byte arg1)
            {
                var r = new byte[91];
                r[1] = status; r[10] = arg1;
                return r;
            }
            const byte OK = 0x02, TIMEOUT = 0x04, UNSUPPORTED = 0x05;

            // Razer answers 0..255, not a percentage.
            t.Equal(100, RazerHid.ScaleCharge(255), "battery: 255 is a full charge");
            t.Equal(50, RazerHid.ScaleCharge(128), "battery: half way");
            t.Equal(10, RazerHid.ScaleCharge(26), "battery: a tenth");
            t.Equal(25, RazerHid.ScaleCharge(64), "battery: a quarter");

            var half = RazerHid.DecodeBattery(Reply(OK, 128), Reply(OK, 1));
            t.Check(half is { Percent: 50, Charging: true }, "battery: level and charging flag decode");

            var off = RazerHid.DecodeBattery(Reply(OK, 200), Reply(OK, 0));
            t.Check(off is { Percent: 78, Charging: false }, "battery: off the charger");

            // The level is the useful half; a missing charging reply is not fatal.
            var noChg = RazerHid.DecodeBattery(Reply(OK, 255), null);
            t.Check(noChg is { Percent: 100, Charging: false }, "battery: no charging reply still reads the level");

            // A mouse that is merely asleep must not read as flat, or a low-battery
            // rule would fire every night.
            t.Check(RazerHid.DecodeBattery(Reply(TIMEOUT, 128), Reply(OK, 0)) == null,
                  "battery: a sleeping mouse reads null, not 0%");
            t.Check(RazerHid.DecodeBattery(Reply(UNSUPPORTED, 0), null) == null,
                  "battery: firmware without the command reads null");
            t.Check(RazerHid.DecodeBattery(null, null) == null, "battery: no reply reads null");
            t.Check(RazerHid.DecodeBattery(Reply(OK, 0), null) == null,
                  "battery: a raw 0 is 'no battery', not a flat one");
            t.Check(RazerHid.DecodeBattery(new byte[4], null) == null, "battery: a short reply reads null");
        }

        t.Section("Battery as a rule source (#f4)");
        {
            const string src = SensorSources.BatteryPrefix + "Razer Basilisk V3 Pro";
            t.Equal("Razer Basilisk V3 Pro battery", SensorSources.Label(src), "battery: source reads as a name");
            t.Equal("%", SensorSources.Unit(src), "battery: measured in percent");
            t.Check(!SensorSources.NeedsFullSweep(src),
                  "battery: pushed by the poller, so it must not wake the full sweep");
            t.Check(!SensorSources.NeedsHub(src), "battery: wakes no sweep at all");
            t.Check(SensorSources.NeedsHub(SensorSources.CpuTemp), "battery: temps still need the hub");
            t.Check(SensorSources.NeedsHub(SensorSources.FanPrefix + "Fan #1"), "battery: fans still need the hub");

            SensorHub.PublishBatteries(new[]
            {
                new SensorHub.BatteryLevel("Razer Basilisk V3 Pro", 42, false),
            });
            t.Equal(42.0, SensorSources.Read(src), "battery: a rule reads the published charge");
            t.Check(SensorSources.Read(SensorSources.BatteryPrefix + "Nothing") == null,
                  "battery: an unknown device reads null");

            // Low-battery rule: below 15%, hold cleared.
            var rule = new SensorRule { Source = src, Above = false, Threshold = 15, HoldSeconds = 0, Profile = "Low battery" };
            var state = new SensorRuleState(false, null);
            state = SensorRuleEvaluator.Step(rule, 42, state, 1);
            t.Check(!state.Active, "battery: 42% does not trip a 15% rule");
            state = SensorRuleEvaluator.Step(rule, 12, state, 1);
            t.Check(state.Active, "battery: 12% trips it");
            SensorHub.PublishBatteries(Array.Empty<SensorHub.BatteryLevel>());
        }
    }
}

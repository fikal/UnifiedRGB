using System.Text.Json;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| What the lights are left doing after the app closes.         |
|                                                              |
| This is the one path that has to be right without anybody    |
| watching it run: the process is on its way out, so a driver  |
| that throws or writes the wrong register leaves a machine    |
| lit wrongly until the next boot. The rule the section pins   |
| is that asking a device for something it cannot do sends     |
| NOTHING, rather than throwing or falling back to some other  |
| mode. A static-only board asked for an effect, a handback    |
| keyboard asked for a static, an effect a driver update       |
| renamed away: all of them must leave the device untouched.   |
|                                                              |
| The choice is stored in hardware.json, so it round-trips by  |
| NAME and an older config with no behaviour at all reads as   |
| today's keep-last behaviour.                                 |
|                                                              |
| The Gigabyte and ENE register assertions are here rather     |
| than with their drivers because this is the caller that      |
| reaches them: the hardware-static exit path sends the same   |
| packet the per-frame path does, and ENE's effect color      |
| window sits directly below REG_DIRECT, so the layout is      |
| pinned to the hardware rather than to a remembered number.   |
\*-----------------------------------------------------------*/
static class HardwareExitSuite
{
    public static void Run(Harness t)
    {
        t.Section("Exit behaviors (#f6)");
        {
            // Stored in hardware.json, so it has to survive a round trip by NAME.
            var opts = new JsonSerializerOptions { WriteIndented = true };
            var saved = new ExitBehavior { Mode = ExitMode.Effect, ColorHex = "3050FF", Effect = "Rainbow" };
            string json = JsonSerializer.Serialize(saved, opts);
            t.Check(json.Contains("\"Effect\""), "exit: the mode is written as a name, not a number");
            var back = JsonSerializer.Deserialize<ExitBehavior>(json)!;
            t.Equal(ExitMode.Effect.ToString(), back.Mode.ToString(), "exit: mode round-trips");
            t.Equal("3050FF", back.ColorHex, "exit: color round-trips");
            t.Equal("Rainbow", back.Effect, "exit: effect name round-trips");

            // An older config, written before this feature existed, has no behaviors.
            var none = JsonSerializer.Deserialize<ExitBehavior>("{}")!;
            t.Equal(ExitMode.KeepLast.ToString(), none.Mode.ToString(), "exit: the default is today's behavior");

            var board = new FakeHardwareDevice { Name = "Board", ExitCaps = HardwareExitCaps.Static };
            var stick = new FakeHardwareDevice
            {
                Name = "Stick",
                ExitCaps = HardwareExitCaps.Static | HardwareExitCaps.Effects,
                HardwareEffects = new[] { "Breathing", "Rainbow" },
            };
            var keeb = new FakeHardwareDevice { Name = "Keeb", ExitCaps = HardwareExitCaps.ReturnToHardware };
            var plain = new FakeDevice { Name = "Plain" };

            // Nothing configured, and KeepLast, both send nothing at all: a device the
            // user never touched must not be written to on the way out.
            t.Check(HardwareExit.Apply(board, null) == null, "exit: no config sends nothing");
            t.Check(HardwareExit.Apply(board, new ExitBehavior { Mode = ExitMode.KeepLast }) == null,
                  "exit: keep-last sends nothing");
            t.Check(board.StaticSet == null, "exit: and the device was not touched");

            HardwareExit.Apply(board, new ExitBehavior { Mode = ExitMode.Static, ColorHex = "3050FF" });
            t.Equal(Rgb.FromHex("3050FF"), board.StaticSet, "exit: static sends the chosen color");
            HardwareExit.Apply(board, new ExitBehavior { Mode = ExitMode.Off });
            t.Equal(Rgb.Black, board.StaticSet, "exit: off is static black");

            // Asking a device for something it cannot do leaves it alone rather than
            // throwing on the way out of the process.
            t.Check(HardwareExit.Apply(board, new ExitBehavior { Mode = ExitMode.Effect, Effect = "Rainbow" }) == null,
                  "exit: an effect on a static-only device does nothing");
            t.Check(HardwareExit.Apply(keeb, new ExitBehavior { Mode = ExitMode.Static, ColorHex = "FFFFFF" }) == null,
                  "exit: a static on a handback-only device does nothing");
            t.Check(HardwareExit.Apply(plain, new ExitBehavior { Mode = ExitMode.Static }) == null,
                  "exit: a device with no firmware modes does nothing");

            HardwareExit.Apply(stick, new ExitBehavior { Mode = ExitMode.Effect, Effect = "rainbow", ColorHex = "FF0000" });
            t.Check(stick.EffectSet is { Name: "Rainbow" }, "exit: effect names match case-insensitively");
            t.Equal(Rgb.FromHex("FF0000"), stick.EffectSet!.Value.Color, "exit: the effect gets its color");

            // An effect renamed or dropped by a driver update must not silently become
            // a different one.
            stick.EffectSet = null;
            t.Check(HardwareExit.Apply(stick, new ExitBehavior { Mode = ExitMode.Effect, Effect = "Gone" }) == null,
                  "exit: an effect the device no longer has does nothing");
            t.Check(stick.EffectSet == null, "exit: and nothing was sent instead");

            HardwareExit.Apply(keeb, new ExitBehavior { Mode = ExitMode.ReturnToHardware });
            t.Equal(1, keeb.HandbackCount, "exit: handback reaches the device");

            // What the dropdown offers comes from the device.
            t.Equal(1, HardwareExit.Choices(plain).Count, "exit: an unsupported device offers only keep-last");
            var boardChoices = HardwareExit.Choices(board);
            t.Equal(3, boardChoices.Count, "exit: static-only offers keep-last, static and off");
            t.Equal("Keeps its last colors", HardwareExit.Label(boardChoices[0]), "exit: keep-last is listed first");
            t.Equal(5, HardwareExit.Choices(stick).Count, "exit: static plus two effects");
            t.Equal(2, HardwareExit.Choices(keeb).Count, "exit: keep-last and the saved profile");
            t.Check(HardwareExit.NeedsColor(new ExitBehavior { Mode = ExitMode.Static }), "exit: static needs a color");
            t.Check(!HardwareExit.NeedsColor(new ExitBehavior { Mode = ExitMode.Off }), "exit: off does not");

            // Gigabyte: the hardware-static path sends the SAME static-effect packet
            // the per-frame path does, so pin the layout once. Colors go out B,G,R.
            var pkt = new byte[GigabyteIt5711.PacketBytes];
            GigabyteIt5711.FillZoneEffect(pkt, 5, new Rgb(0x30, 0x50, 0xFF), null);
            t.Equal(0xCC, pkt[0], "gigabyte: report id");
            t.Equal(0x25, pkt[1], "gigabyte: zone 5 is register 0x20 + 5");
            t.Equal(0x20, pkt[2], "gigabyte: zone bitmask low byte");
            t.Equal(0x00, pkt[3], "gigabyte: zone bitmask high byte");
            t.Equal(1, pkt[11], "gigabyte: static effect");
            t.Equal(0xFF, pkt[12], "gigabyte: full brightness");
            t.Equal(0xFF, pkt[14], "gigabyte: blue first");
            t.Equal(0x50, pkt[15], "gigabyte: then green");
            t.Equal(0x30, pkt[16], "gigabyte: then red");

            // Zone 9 and up move to the second register block.
            GigabyteIt5711.FillZoneEffect(pkt, 9, Rgb.Black, null);
            t.Equal(0x91, pkt[1], "gigabyte: zone 9 is register 0x90 + 1");

            // A streamed header is addressed by effect index on the way out, not by
            // the header number it streams on. Confusing the two would light the
            // wrong output.
            t.Equal(5, GigabyteIt5711.EffectIndexOfHeader(1), "gigabyte: header 1 is effect 5");
            t.Equal(8, GigabyteIt5711.EffectIndexOfHeader(4), "gigabyte: header 4 is effect 8");

            // ENE: the effect color window is 15 bytes and REG_DIRECT sits directly
            // after it, so a sixth LED would write straight into the direct and mode
            // registers. This pins the cap to the hardware layout rather than to a
            // number someone remembered.
            t.Equal(0x8021, EneDram.REG_MODE, "ene: mode register");

            // The effect window is PAIRED with the direct one, per generation. Writing
            // V1's effect register on a V2 stick puts the color in a bank the V2
            // effect engine never reads, so the mode changes and the color does not.
            // The earlier version of this test compared two constants from the same
            // file, which passed for any pair someone wrote down.
            t.Equal(0x8010, EneDram.REG_COLORS_EFFECT_V1, "ene: v1 effect colors");
            t.Equal(0x8160, EneDram.REG_COLORS_EFFECT_V2, "ene: v2 effect colors");
            t.Equal(5, EneDram.EffectColorLeds(EneDram.REG_COLORS_EFFECT_V1), "ene: v1 holds five leds in 15 bytes");
            t.Equal(10, EneDram.EffectColorLeds(EneDram.REG_COLORS_EFFECT_V2), "ene: v2 holds ten in 30");

            // V1's window is the tight one: the last byte of a fifth LED stays below
            // REG_DIRECT, and a sixth would land on the direct and mode registers.
            int v1Last = EneDram.REG_COLORS_EFFECT_V1 + 3 * EneDram.EffectColorLeds(EneDram.REG_COLORS_EFFECT_V1) - 1;
            t.Check(v1Last < EneDram.REG_DIRECT, "ene: the v1 cap stays inside its own window");
            t.Check(EneDram.REG_COLORS_EFFECT_V1 + 3 * 6 - 1 >= EneDram.REG_MODE,
                  "ene: a sixth led on v1 would write into the direct and mode registers");
        }
    }
}

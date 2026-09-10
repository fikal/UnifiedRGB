using UnifiedRgb.Core.Sensors;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Fan control: duty maths, curves, and what the driver no      |
| longer does.                                                 |
|                                                              |
| These sections share a hazard rather than a namespace. A     |
| wrong duty byte, a curve that throws on a hand-edited        |
| fan-config.json, or a Clone that aliases its points all end  |
| the same way: fans at the wrong speed with nobody watching.  |
| The IteSuperIo section is the odd one out only in form. It   |
| asserts an ABSENCE, because the dead fan-control block was   |
| removed for safety and a well-meaning re-add would be a      |
| regression no behavioural test could catch.                  |
\*-----------------------------------------------------------*/

static class FansSuite
{
    public static void Run(Harness t)
    {
        t.Section("Fan duty math");
        {
            t.Equal((byte)0, UnifiedRgb.Core.Sensors.IteSuperIo.DutyByte(0), "DutyByte 0%");
            t.Equal((byte)255, UnifiedRgb.Core.Sensors.IteSuperIo.DutyByte(100), "DutyByte 100%");
            t.Equal((byte)128, UnifiedRgb.Core.Sensors.IteSuperIo.DutyByte(50), "DutyByte 50% rounds");
            t.Equal((byte)76, UnifiedRgb.Core.Sensors.IteSuperIo.DutyByte(30), "DutyByte floor value (banker's rounding)");
            t.Equal((byte)255, UnifiedRgb.Core.Sensors.IteSuperIo.DutyByte(150), "DutyByte clamps high");
            t.Equal((byte)0, UnifiedRgb.Core.Sensors.IteSuperIo.DutyByte(-5), "DutyByte clamps low");
        }

        t.Section("Fan curve interpolation");
        {
            var c = new UnifiedRgb.Core.Sensors.FanCurve("t", UnifiedRgb.Core.Sensors.TempSource.Cpu,
                new UnifiedRgb.Core.Sensors.CurvePoint[]
                { new(30, 30), new(50, 50), new(80, 100) });
            t.Equal(30, c.DutyAt(10), "curve below first point clamps");
            t.Equal(30, c.DutyAt(30), "curve at first point");
            t.Equal(40, c.DutyAt(40), "curve midpoint interpolates");
            t.Equal(50, c.DutyAt(50), "curve at knee");
            t.Equal(75, c.DutyAt(65), "curve interpolates second segment");
            t.Equal(100, c.DutyAt(80), "curve at last point");
            t.Equal(100, c.DutyAt(95), "curve above last point clamps");

            var quiet = UnifiedRgb.Core.Sensors.FanCurve.Preset_("Quiet");
            t.Check(quiet.DutyAt(20) == 30 && quiet.DutyAt(90) == 100, "Quiet preset spans floor..100");
            var gpuQuiet = UnifiedRgb.Core.Sensors.FanCurve.Preset_("Quiet", floor: 0);
            t.Check(gpuQuiet.DutyAt(20) == 0 && gpuQuiet.DutyAt(45) == 0, "GPU Quiet is fan-stop when idle");
            t.Check(gpuQuiet.DutyAt(90) == 100, "GPU Quiet still reaches 100 hot");
            t.Check(gpuQuiet.MatchesPreset(), "floored preset still matches itself");
            t.Check(UnifiedRgb.Core.Sensors.FanCurve.Preset_("Full").DutyAt(20) == 100, "Full preset is 100 everywhere");
            t.Check(quiet.MatchesPreset(), "unedited preset matches");
            quiet.Points[0] = new UnifiedRgb.Core.Sensors.CurvePoint(20, 35);
            t.Check(!quiet.MatchesPreset(), "edited preset no longer matches");
        }

        t.Section("FanCurve hardening");
        {
            var curve = new FanCurve { Points = null! };
            t.Equal(0, curve.Points.Count, "null Points (hand-edited fan-config.json) becomes empty");
            t.Equal(0, curve.DutyAt(50), "empty curve yields 0 duty instead of throwing");
            var json = System.Text.Json.JsonSerializer.Deserialize<FanCurve>("{\"Preset\":\"x\",\"Points\":null}");
            t.Check(json != null && json.Points.Count == 0, "JSON null Points round-trips to empty");
        }

        t.Section("FanCurve.Clone contract (#82 #112)");
        {
            var src = new FanCurve("Custom", TempSource.Cpu, new CurvePoint[] { new(60, 80), new(30, 30) }, floor: 20);
            var cl = src.Clone();
            t.Check(!ReferenceEquals(src, cl) && !ReferenceEquals(src.Points, cl.Points), "Clone yields a distinct instance and Points list");
            t.Check(cl.Preset == "Custom" && cl.Source == TempSource.Cpu && cl.Floor == 20 && cl.Points.Count == 2, "Clone copies preset/source/floor/points");
            cl.Points[0] = new CurvePoint(10, 10);
            t.Equal(30, src.Points[0].TempC, "mutating the clone leaves the source untouched");
            var unsorted = new FanCurve { Points = new List<CurvePoint> { new(80, 100), new(20, 20) } };
            t.Equal(20, unsorted.Clone().Points[0].TempC, "Clone re-sorts hand-edited points by temperature");
        }

        t.Section("IteSuperIo dead fan-control block removed (#140 #87 #86)");
        {
            var ite = typeof(IteSuperIo);
            var any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            foreach (var name in new[] { "SetFanDutyPercent", "ReadPwmRaw", "WritePwmRaw", "RestoreFan", "RestoreAllFans", "ForceRestore", "IsFanOverridden" })
                t.Check(ite.GetMethod(name, any) == null, $"IteSuperIo.{name} is gone");
            t.Check(ite.GetProperty("FanCount", any) == null && ite.GetProperty("SavedPwm", any) == null, "IteSuperIo.FanCount/SavedPwm are gone");
            t.Check(ite.GetMethod("ReadEcRaw", any) != null && ite.GetMethod("WriteEcRaw", any) != null && ite.GetMethod("DumpLdns", any) != null,
                "IteSuperIo raw probe primitives kept for the CLI");
        }
    }
}

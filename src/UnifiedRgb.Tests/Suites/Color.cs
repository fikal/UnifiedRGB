using UnifiedRgb.Core;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Color: the value type, the maths, and the master dimmer.    |
|                                                              |
| Everything downstream of here trusts these four sections. An |
| effect is only as correct as the Rgb it hands over, a saved  |
| profile is only as portable as the hex it round-trips, and   |
| every device write passes through Master.Scale on its way to |
| the wire, so a bug in any of it is a bug in all of it. They  |
| live together because they are one dependency chain, not     |
| because they happen to share a file.                         |
\*-----------------------------------------------------------*/

static class ColorSuite
{
    public static void Run(Harness t)
    {
        t.Section("Rgb hex parsing");
        {
            t.Check(Rgb.TryFromHex("FF8000", out var c1) && c1 == new Rgb(255, 128, 0), "TryFromHex plain");
            t.Check(Rgb.TryFromHex("#00ff00", out var c2) && c2 == new Rgb(0, 255, 0), "TryFromHex # + lowercase");
            t.Check(Rgb.TryFromHex(" 0000FF ", out var c3) && c3 == new Rgb(0, 0, 255), "TryFromHex whitespace");
            t.Check(!Rgb.TryFromHex("", out _), "TryFromHex empty rejected");
            t.Check(!Rgb.TryFromHex(null, out _), "TryFromHex null rejected");
            t.Check(!Rgb.TryFromHex("FF80", out _), "TryFromHex partial rejected");
            t.Check(!Rgb.TryFromHex("GGGGGG", out _), "TryFromHex non-hex rejected");
            t.Check(!Rgb.TryFromHex("FF8000AA", out _), "TryFromHex too long rejected");
            t.Equal("#FF8000", new Rgb(255, 128, 0).ToString(), "Rgb.ToString");
            t.Check(Rgb.TryFromHex(new Rgb(12, 34, 56).ToString(), out var rt) && rt == new Rgb(12, 34, 56),
                "ToString/TryFromHex roundtrip");
            bool threw = false;
            try { Rgb.FromHex("nope"); } catch (FormatException) { threw = true; }
            t.Check(threw, "FromHex throws FormatException on junk");
        }

        t.Section("HSV color math");
        {
            t.Equal(new Rgb(255, 0, 0), ColorUtil.HsvToRgb(0, 1, 1), "HSV 0 = red");
            t.Equal(new Rgb(0, 255, 0), ColorUtil.HsvToRgb(120, 1, 1), "HSV 120 = green");
            t.Equal(new Rgb(0, 0, 255), ColorUtil.HsvToRgb(240, 1, 1), "HSV 240 = blue");
            t.Equal(ColorUtil.HsvToRgb(30, 1, 1), ColorUtil.HsvToRgb(390, 1, 1), "HSV wraps at 360");
            t.Equal(ColorUtil.HsvToRgb(30, 1, 1), ColorUtil.HsvToRgb(-330, 1, 1), "HSV negative wraps");
            t.Equal(new Rgb(0, 0, 0), ColorUtil.HsvToRgb(180, 1, 0), "HSV v=0 = black");
            var grey = ColorUtil.HsvToRgb(300, 0, 0.5);
            t.Check(grey.R == grey.G && grey.G == grey.B, "HSV s=0 = grey");
        }

        t.Section("Master.Scale over a range");
        {
            var buf = new[] { new Rgb(200, 200, 200), new Rgb(200, 200, 200), new Rgb(200, 200, 200) };
            double keep = Master.Brightness;
            Master.Brightness = 0.5;
            Master.Scale(buf, 1, 1);
            t.Check(buf[0].R == 200 && buf[2].R == 200, "range scale leaves everything outside the range alone");
            t.Equal((byte)100, buf[1].R, "range scale dims inside the range");
            Master.Scale(buf, 2, 99);          // count past the end
            t.Equal((byte)100, buf[2].R, "range scale clamps to the buffer");
            Master.Scale(buf, -5, 1);          // negative offset
            Master.Brightness = keep;
        }

        t.Section("ColorUtil HSV round-trip lattice (#142)");
        {
            bool ok = true;
            int worst = 0;
            for (int r = 0; r < 256 && ok; r += 15)
                for (int g = 0; g < 256 && ok; g += 15)
                    for (int b = 0; b < 256 && ok; b += 15)
                    {
                        var c = new Rgb((byte)r, (byte)g, (byte)b);
                        var (h, s, v) = ColorUtil.RgbToHsv(c);
                        var back = ColorUtil.HsvToRgb(h, s, v);
                        int d = Math.Max(Math.Abs(back.R - c.R), Math.Max(Math.Abs(back.G - c.G), Math.Abs(back.B - c.B)));
                        worst = Math.Max(worst, d);
                        if (d > 1) ok = false;
                    }
            t.Check(ok, $"RgbToHsv/HsvToRgb round-trips within 1 per channel on the 18^3 lattice (worst {worst})");
            t.Check(ColorUtil.RgbToHsv(new Rgb(0, 0, 0)) == (0, 0, 0), "black -> (0,0,0)");
            t.Check(ColorUtil.RgbToHsv(new Rgb(0, 0, 255)).H == 240, "blue hue is 240");
        }
    }
}

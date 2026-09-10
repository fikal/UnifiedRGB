using UnifiedRgb.App.Services;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Lian Li bake regressions: Custom Pattern loop periods and    |
| upload signature (BakeKey), and the baker's common-window    |
| choice. Pure logic - no device, no dispatcher. Listed in     |
| Suites.cs as "LianBake" and run by name.                     |
\*-----------------------------------------------------------*/
static class LianBakeSuite
{
    public static void Run(Harness t)
    {
        PatternLoops(t);
        PatternBakeKey(t);
        ChooseWindow(t);
    }

    /// <summary>A ring of n LEDs around (0.5, 0.5) - the single-fan geometry
    /// PatternEffect's ring coordinate is built for.</summary>
    static LedPos[] Ring(int n)
    {
        var pos = new LedPos[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            pos[i] = new LedPos((float)(0.5 + 0.5 * Math.Cos(a)), (float)(0.5 + 0.5 * Math.Sin(a)));
        }
        return pos;
    }

    /*---------------- Baked Custom Pattern loops close on their period ----------------*/
    static void PatternLoops(Harness t)
    {
        t.Section("baked Custom Pattern loops close on their period");
        var pos = Ring(8);
        var bc = new Rgb(200, 90, 30);
        var pal = new[] { new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255) };
        foreach (var color in new[] { PatternColor.Rainbow, PatternColor.Gradient, PatternColor.Solid })
            foreach (var motion in new[] { PatternMotion.Rotate, PatternMotion.Chase, PatternMotion.Breathe, PatternMotion.Wave })
                foreach (double speed in new[] { 1.0, 2.5 })
                {
                    var fx = new PatternEffect { Color = color, Motion = motion, Palette = pal };
                    string tag = $"Custom Pattern {color}/{motion} @{speed}";
                    t.Check(fx.Bakeable, $"{tag} is Bakeable");
                    double loop = fx.LoopSeconds(speed);
                    // Solid + Rotate is the one constant combination (rotating a
                    // single color): it reports 0 so it never pins a bake window.
                    bool constant = color == PatternColor.Solid && motion == PatternMotion.Rotate;
                    t.Check(constant ? loop == 0 : loop > 0, $"{tag} LoopSeconds = {loop:F3} {(constant ? "== 0 (constant)" : "> 0 (not time-invariant)")}");
                    // Breathe is one sin(t*speed*2) breath; the moving motions
                    // are one ring turn of move = t*speed*0.25.
                    double want = constant ? 0.0 : motion == PatternMotion.Breathe ? Math.PI / speed : 4.0 / speed;
                    t.Check(Math.Abs(loop - want) < 1e-9, $"{tag} LoopSeconds = {loop:F3}, expected {want:F3}");
                    if (constant) loop = 4.0 / speed;   // any window is a period; use one for the closure check
                    var a = new Rgb[8]; var b = new Rgb[8]; var mid = new Rgb[8];
                    fx.Render(a, pos, 0.37, speed, bc);
                    fx.Render(b, pos, 0.37 + loop, speed, bc);
                    t.Check(SameWithin(a, b, 1), $"{tag} frame at t0 equals frame at t0 + LoopSeconds");
                    // The period must be a REAL one, not just "the frame never
                    // changes": a third of the way through, every animated
                    // combination looks different. Solid + Rotate is the one
                    // constant combination (rotating a single color).
                    if (!(color == PatternColor.Solid && motion == PatternMotion.Rotate))
                    {
                        fx.Render(mid, pos, 0.37 + loop / 3.0, speed, bc);
                        t.Check(!SameWithin(a, mid, 1), $"{tag} actually animates inside its period");
                    }
                }

        // Static has no time term: 0 tells the baker any window is a period.
        foreach (var color in new[] { PatternColor.Rainbow, PatternColor.Gradient, PatternColor.Solid })
        {
            var fx = new PatternEffect { Color = color, Motion = PatternMotion.Static, Palette = pal };
            t.Check(fx.LoopSeconds(1.0) == 0 && fx.LoopSeconds(2.5) == 0, $"Custom Pattern {color}/Static LoopSeconds = 0");
            var a = new Rgb[8]; var b = new Rgb[8];
            fx.Render(a, pos, 0.37, 1.0, bc);
            fx.Render(b, pos, 7.91, 1.0, bc);
            t.Check(SameWithin(a, b, 0), $"Custom Pattern {color}/Static is time-invariant");
        }
    }

    /*---------------- BakeKey tracks every render-affecting setting ----------------*/
    static void PatternBakeKey(Harness t)
    {
        t.Section("BakeKey tracks every render-affecting setting");
        static PatternEffect Base() => new()
        {
            Color = PatternColor.Rainbow, Motion = PatternMotion.Rotate, Density = 1.0, Reverse = false, TailLength = 0.35,
            Palette = new[] { new Rgb(255, 0, 96), new Rgb(0, 160, 255) },
        };
        string key = Base().BakeKey;
        t.Check(key.Length > 0, "PatternEffect.BakeKey is not empty");
        t.Check(Base().BakeKey == key, "PatternEffect.BakeKey is stable for equal settings");
        // Default interface members are reachable through the interface only.
        t.Check(((IEffect)new Breathing()).BakeKey == "", "stateless effect keeps the empty default BakeKey");

        var density = Base(); density.Density = 2.0;
        t.Check(density.BakeKey != key, "BakeKey changes with Density");
        var reverse = Base(); reverse.Reverse = true;
        t.Check(reverse.BakeKey != key, "BakeKey changes with Reverse");
        var motion = Base(); motion.Motion = PatternMotion.Chase;
        t.Check(motion.BakeKey != key, "BakeKey changes with Motion");
        var color = Base(); color.Color = PatternColor.Gradient;
        t.Check(color.BakeKey != key, "BakeKey changes with Color");
        var tail = Base(); tail.TailLength = 0.6;
        t.Check(tail.BakeKey != key, "BakeKey changes with TailLength");
        var palette = Base(); palette.Palette = new[] { new Rgb(255, 0, 96), new Rgb(0, 255, 0) };
        t.Check(palette.BakeKey != key, "BakeKey changes with Palette");
    }

    /*---------------- ChooseWindow: common multiple inside 1.5..12 s ----------------*/
    static void ChooseWindow(Harness t)
    {
        t.Section("ChooseWindow: common multiple inside 1.5..12 s");
        static string Show(double[] p) => "{" + string.Join(",", p) + "}";
        void Accept(double[] periods, double want)
        {
            bool ok = LianBakeService.ChooseWindow(periods, out double got);
            t.Check(ok && Math.Abs(got - want) < 1e-9, $"ChooseWindow {Show(periods)} -> {want} (got {(ok ? got.ToString("0.###") : "false")})");
        }
        void Reject(double[] periods)
        {
            bool ok = LianBakeService.ChooseWindow(periods, out double got);
            t.Check(!ok, $"ChooseWindow {Show(periods)} -> false (got {got:0.###})");
        }

        Accept(Array.Empty<double>(), 1.5);                  // nothing animated: shortest window
        Accept(new[] { 0.0 }, 1.5);                          // a constant channel only
        Accept(new[] { 9.0 }, 9.0);
        Accept(new[] { 9.0, 0.0 }, 9.0);                     // constant channels never constrain
        Accept(new[] { 3.0, 6.0 }, 6.0);
        Accept(new[] { 2.5, 5.0 }, 5.0);
        Accept(new[] { 0.8 }, 1.6);                          // below 1.5 s: two loops
        Accept(new[] { 4.0, 4.04 }, 4.04);                   // 1% off a whole cycle: within tolerance
        Accept(new[] { 12.0 }, 12.0);                        // the ceiling itself is allowed
        Accept(new[] { 4.0, 6.0 }, 12.0);                    // LCM inside the ceiling
        Reject(new[] { 4.0, 9.0 });                          // LCM 36 s: no common window fits
        Reject(new[] { 13.0 });                              // longer than the ceiling
        Reject(new[] { 4.0, 4.5 });                          // 11% off, LCM 36 s
        Reject(new[] { 4.0, Math.PI / 4 });                  // 5.09 cycles at 4 s: 0.09 cycle seam, over the ABSOLUTE tolerance
        // The old max-then-clamp would have "accepted" these by cutting a cycle.
        bool cut = LianBakeService.ChooseWindow(new[] { 4.0, 9.0 }, out double cutT);
        t.Check(!cut && cutT == 0, "ChooseWindow never returns a window that cuts a channel mid-cycle");
    }
}

using System.IO;
using System.Windows;
using UnifiedRgb.App;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Regressions for the 2026-09-10 fixes: a canvas.json that     |
| deserialises but is not usable (null list, null entries,     |
| zero sizes) used to reach startup and throw; GIF backgrounds |
| ignored frame disposal. Listed in Suites.cs as "CanvasLoad"   |
| and run by name.                                              |
| The self-update fix (hash required before download) is        |
| network-bound and has no harness test.                        |
\*-----------------------------------------------------------*/
static class CanvasLoadSuite
{
    public static void Run(Harness t)
    {
        CanvasLayoutIsNormalisedAtLoad(t);
        GifDisposalPlan(t);
    }

    /*---------------- CanvasLayout survives a broken canvas.json (23) ----------------*/
    static void CanvasLayoutIsNormalisedAtLoad(Harness t)
    {
        t.Section("CanvasLayout survives a broken canvas.json");
        // Program.cs redirected AppPaths to a private temp root before any
        // test ran (and refused to start otherwise), so this is the harness's
        // own canvas.json, never the user's. Same fixture pattern as the
        // SceneStore / LcdDesign nulls test (B9): write, load, delete.
        string dir = AppPaths.ConfigDir;
        string path = AppPaths.Config("canvas.json");
        try
        {
            // (a) An explicit null list defeats the property initializer; the
            // app's SyncCanvas then read Items.Count during startup and threw.
            File.WriteAllText(path, "{\"Enabled\":true,\"Items\":null}");
            var a = CanvasLayout.Load();
            t.Check(a.Items != null && a.Items.Count == 0, "canvas.json with Items:null loads an empty list rather than null");
            t.Check(a.Enabled, "the Enabled flag survives the Items repair");
            bool threw = false;
            try
            {
                a.ItemFor("x");
                a.Clone();
                a.AutoArrange(new[] { new FakeDevice { Name = "Fake" } });
            }
            catch { threw = true; }
            t.Check(!threw, "ItemFor / Clone / AutoArrange do not throw on a layout loaded with Items:null");
            t.Check(a.Items!.Count == 1 && a.ItemFor("Fake") != null, "AutoArrange places the device on the repaired list");

            // The accessors are null-safe on their own too: Items is settable
            // and the effect workers read through ItemFor.
            var hand = new CanvasLayout { Items = null! };
            threw = false;
            try { hand.ItemFor("x"); hand.Clone(); hand.AutoArrange(Array.Empty<IRgbDevice>()); }
            catch { threw = true; }
            t.Check(!threw, "ItemFor / Clone / AutoArrange tolerate a null Items set by hand");
            t.Check(hand.Clone().Items != null && hand.Clone().Items.Count == 0, "Clone of a null-Items layout has an empty list");

            // (b) A null entry is dropped; a null name and non-positive sizes
            // are repaired to the defaults (CanvasMapper divides by W/H).
            File.WriteAllText(path, "{\"Items\":[null,{\"Device\":null,\"W\":0,\"H\":-5}]}");
            var b = CanvasLayout.Load();
            t.Check(b.Items.Count == 1, "a null canvas item is dropped");
            t.Check(b.Items.Count == 1 && b.Items[0].Device == "", "a null Device name loads as an empty string");
            t.Check(b.Items.Count == 1 && b.Items[0].W == 200 && b.Items[0].H == 100, "W<=0 / H<0 fall back to the 200x100 defaults");

            // (c) A zero-width desk would clamp every device to X=0.
            File.WriteAllText(path, "{\"Width\":0}");
            t.Check(CanvasLayout.Load().Width == 1600, "Width 0 falls back to 1600");
            File.WriteAllText(path, "{\"Height\":-1}");
            t.Check(CanvasLayout.Load().Height == 900, "a negative Height falls back to 900");

            // The remaining rules: rotation outside the four right angles, a
            // grid override of zero columns (a modulo by zero in the mapper).
            File.WriteAllText(path, "{\"Items\":[{\"Device\":\"d\",\"Rotation\":45,\"LedLayout\":{\"Shape\":\"grid\",\"Cols\":0,\"Rows\":-2}}]}");
            var r = CanvasLayout.Load();
            t.Check(r.Items.Count == 1 && r.Items[0].Rotation == 0, "a rotation that is not 0/90/180/270 is treated as 0");
            t.Check(r.Items.Count == 1 && r.Items[0].LedLayout is { Cols: 1, Rows: 1 }, "LedLayout Cols/Rows below 1 become 1");

            // A well-formed file is loaded as-is and NOT copied aside: the
            // corrupt copy is evidence of damage, not a per-launch backup.
            DeleteCorruptCopies(dir);
            File.WriteAllText(path, "{\"Enabled\":true,\"Width\":1600,\"Height\":900,\"Items\":[{\"Device\":\"k\",\"X\":1,\"Y\":2,\"W\":30,\"H\":40,\"Rotation\":90}]}");
            var ok = CanvasLayout.Load();
            t.Check(ok.Enabled && ok.Items.Count == 1 && ok.Items[0].Rotation == 90 && ok.Items[0].W == 30, "a well-formed canvas.json loads unchanged");
            t.Check(Directory.GetFiles(dir, "canvas.json.corrupt-*").Length == 0, "a well-formed canvas.json is not copied aside");

            // (d) Malformed JSON: defaults, and the original text preserved
            // beside the file so the next Save cannot destroy it silently.
            const string Broken = "{\"Enabled\":true,\"Items\":[";
            File.WriteAllText(path, Broken);
            var d = CanvasLayout.Load();
            t.Check(d != null && !d.Enabled && d.Items.Count == 0 && d.Width == 1600 && d.Height == 900,
                "malformed canvas.json falls back to the defaults instead of throwing");
            var copies = Directory.GetFiles(dir, "canvas.json.corrupt-*");
            t.Check(copies.Length == 1, "a malformed canvas.json is copied aside as canvas.json.corrupt-<stamp>");
            t.Check(copies.Length == 1 && File.ReadAllText(copies[0]) == Broken, "the preserved copy is the original text, byte for byte");
            t.Check(File.Exists(path), "the original is left in place (Save replaces it; the copy is the recovery path)");
            DeleteCorruptCopies(dir);

            // The literal `null` parses without an exception and is just as
            // unusable; it goes the same way as a syntax error.
            File.WriteAllText(path, "null");
            var n = CanvasLayout.Load();
            t.Check(n != null && n.Items != null && n.Items.Count == 0, "a canvas.json containing `null` loads the defaults");
            t.Check(Directory.GetFiles(dir, "canvas.json.corrupt-*").Length == 1, "a `null` canvas.json is copied aside like a syntax error");
        }
        finally
        {
            try { File.Delete(path); } catch { }
            DeleteCorruptCopies(dir);
        }
    }

    static void DeleteCorruptCopies(string dir)
    {
        try
        {
            foreach (string f in Directory.GetFiles(dir, "canvas.json.corrupt-*"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    /*---------------- GIF frame disposal plan (27) ----------------*/
    static void GifDisposalPlan(Harness t)
    {
        t.Section("GIF frame disposal plan");
        // A real GIF with disposal metadata cannot be built here: the
        // framework's GifBitmapEncoder writes no Graphic Control Extension.
        // The compositor's decisions are therefore planned by a pure function
        // that LoadGif also uses, and that is what is pinned.
        var full = new Int32Rect(0, 0, 100, 80);
        var sprite1 = new Int32Rect(10, 10, 20, 20);
        var sprite2 = new Int32Rect(30, 5, 15, 15);

        var plan = GifCanvas.Plan(new[] { (full, 1), (sprite1, 2), (sprite2, 3), (full, 0) });
        t.Check(plan.Length == 4, "one step per frame");
        t.Check(!plan[0].RestorePrevious && !plan[0].Clear.HasArea, "frame 0 starts on an untouched canvas whatever its own disposal says");
        t.Check(!plan[1].RestorePrevious && !plan[1].Clear.HasArea, "disposal 1 (leave) on frame 0: frame 1 draws straight over it");
        t.Check(!plan[2].RestorePrevious && plan[2].Clear == sprite1,
            "disposal 2 on frame 1 clears exactly frame 1's rectangle before frame 2 (it used to accumulate)");
        t.Check(plan[3].RestorePrevious && !plan[3].Clear.HasArea,
            "disposal 3 on frame 2 puts back the canvas from before frame 2 was drawn");

        // Disposal is applied AFTER a frame's delay, so a frame's own value
        // never touches the frame itself - and the last frame's is moot.
        var one = GifCanvas.Plan(new[] { (full, 2) });
        t.Check(one.Length == 1 && !one[0].RestorePrevious && !one[0].Clear.HasArea, "a single frame is drawn before its own disposal matters");
        var last = GifCanvas.Plan(new[] { (sprite1, 0), (sprite2, 3) });
        t.Check(!last[1].RestorePrevious && !last[1].Clear.HasArea, "the last frame's disposal 3 has nothing after it to affect");

        // Reserved values are 'leave', as browsers treat them; disposal 0 and
        // 1 are the same thing.
        var res = GifCanvas.Plan(new[] { (sprite1, 7), (sprite2, 0), (sprite1, 1), (full, 0) });
        t.Check(!res[1].RestorePrevious && !res[1].Clear.HasArea, "a reserved disposal value (4-7) is treated as leave");
        t.Check(!res[2].RestorePrevious && !res[2].Clear.HasArea && !res[3].RestorePrevious && !res[3].Clear.HasArea,
            "disposal 0 and 1 both leave the canvas alone");
        t.Check(GifCanvas.Plan(Array.Empty<(Int32Rect, int)>()).Length == 0, "no frames, no plan");

        // The rectangle a clear covers is the scaled frame rect rounded
        // OUTWARD and clamped, so an anti-aliased edge goes with it and an
        // off-canvas frame clears nothing.
        t.Check(GifCanvas.PixelRect(new Rect(10.4, 10.4, 19.2, 19.2), 100, 80) == new Int32Rect(10, 10, 20, 20),
            "a fractional frame rect clears the whole pixels it touched");
        t.Check(GifCanvas.PixelRect(new Rect(90, 70, 30, 30), 100, 80) == new Int32Rect(90, 70, 10, 10),
            "a frame rect hanging off the canvas is clamped to it");
        t.Check(!GifCanvas.PixelRect(new Rect(120, 5, 10, 10), 100, 80).HasArea, "a frame rect entirely off the canvas has no area");
        t.Check(!GifCanvas.PixelRect(new Rect(5, 5, 0, 10), 100, 80).HasArea, "a zero-width frame rect has no area");

        // And the clear itself: only the rectangle's bytes go to zero.
        const int W = 4, H = 3, Stride = W * 4;
        var buf = new byte[Stride * H];
        Array.Fill(buf, (byte)0xFF);
        GifCanvas.ClearRect(buf, Stride, new Int32Rect(1, 1, 2, 1));
        bool cleared = true, kept = true;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                bool inside = y == 1 && x is 1 or 2;
                for (int c = 0; c < 4; c++)
                {
                    byte v = buf[y * Stride + x * 4 + c];
                    if (inside && v != 0) cleared = false;
                    if (!inside && v != 0xFF) kept = false;
                }
            }
        t.Check(cleared, "ClearRect zeroes every byte inside the rectangle");
        t.Check(kept, "ClearRect leaves every byte outside the rectangle alone");
    }
}

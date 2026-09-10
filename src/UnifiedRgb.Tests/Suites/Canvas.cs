using System.Text.Json;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The whole-desk canvas: where each device physically sits and |
| what that buys you.                                          |
|                                                              |
| These sections belong together because they are one feature  |
| told end to end. The editor's snap guides are how a device   |
| gets placed, the mapping and led-layout sections are how a   |
| placed device turns into coordinates, the carry-across and   |
| zone sections are the payoff (one wave crossing the desk     |
| instead of restarting on every device), and the layout file  |
| is how an arrangement survives a restart.                    |
|                                                              |
| Each section keeps its own braces because they all reach for |
| the same obvious names, layout, device, item, and would      |
| collide if they were flattened into one method body.         |
\*-----------------------------------------------------------*/
static class CanvasSuite
{
    public static void Run(Harness t)
    {
        t.Section("Snap guides, shared by both editors (#f9)");
        {
            // Lines to snap to: the surface's edges and centre, plus each item's.
            var lines = SnapGuides.Lines(1000, new[] { (100.0, 50.0) });
            t.Check(lines.Contains(0) && lines.Contains(500) && lines.Contains(1000), "snap: the surface offers its edges and centre");
            t.Check(lines.Contains(100) && lines.Contains(125) && lines.Contains(150), "snap: and each item its edges and centre");

            // An item's leading edge pulls to a nearby line.
            var (v, line) = SnapGuides.Snap(97, 40, lines);
            t.Equal(100.0, v, "snap: the leading edge pulls into line");
            t.Equal(100.0, line, "snap: and reports what it met");

            // So does its centre and its trailing edge.
            var (byCentre, _) = SnapGuides.Snap(478, 40, lines);
            t.Equal(480.0, byCentre, "snap: the centre pulls too");
            var (byTrailing, _) = SnapGuides.Snap(63, 40, lines);
            t.Equal(60.0, byTrailing, "snap: and the trailing edge");

            // Far from anything, nothing moves. This is what lets you place something
            // deliberately a few units off an edge.
            var (free, noLine) = SnapGuides.Snap(300, 40, lines);
            t.Equal(300.0, free, "snap: nothing near means nothing moves");
            t.Check(noLine == null, "snap: and no guide is drawn");

            // Just outside the threshold is still free; just inside pulls.
            t.Equal(100.0 + SnapGuides.Threshold + 1, SnapGuides.Snap(100 + SnapGuides.Threshold + 1, 10, new List<double> { 100 }).Value,
                  "snap: outside the threshold is left alone");
            t.Equal(100.0, SnapGuides.Snap(100 + SnapGuides.Threshold - 1, 10, new List<double> { 100 }).Value,
                  "snap: inside it pulls");

            // The closest line wins when several are in range.
            t.Equal(100.0, SnapGuides.Snap(101, 10, new List<double> { 100, 130 }).Value,
                  "snap: the nearer of two lines wins");
            t.Equal(130.0, SnapGuides.Snap(129, 10, new List<double> { 100, 130 }).Value,
                  "snap: whichever side it is on");
            // Dead heats resolve to the last line offered rather than arbitrarily:
            // it is not a meaningful choice, but it is a repeatable one.
            var (tied, which) = SnapGuides.Snap(102, 10, new List<double> { 100, 104 });
            t.Equal(104.0, tied, "snap: a dead heat resolves the same way every time");
            t.Check(which != null, "snap: and still reports a line");

            double delta;
            t.Check(SnapGuides.Nearest(new List<double>(), new[] { 5.0 }, out delta) == null, "snap: no lines, no snap");
            t.Equal(0.0, delta, "snap: and no movement");
        }

        t.Section("Whole-desk canvas: effects carry across (#f9)");
        {
            // The point of the feature: two devices side by side on the desk get
            // coordinates that continue from one into the other, so one wave crosses
            // both instead of restarting. Without the canvas each device spans 0..1 on
            // its own and the wave starts over.
            var layout = new CanvasLayout { Enabled = true, Width = 1000, Height = 1000 };
            layout.Items.Add(new CanvasItem { Device = "Left", X = 0, Y = 400, W = 400, H = 200 });
            layout.Items.Add(new CanvasItem { Device = "Right", X = 600, Y = 400, W = 400, H = 200 });

            var left = new FakeDevice { Name = "Left", LedCount = 10 };
            var right = new FakeDevice { Name = "Right", LedCount = 10 };

            var l = CanvasMapper.Positions(left, 0, 10, layout)!;
            var r = CanvasMapper.Positions(right, 0, 10, layout)!;

            // Every LED of the left device is left of every LED of the right one.
            t.Check(l[^1].X < r[0].X, "desk: the left device ends before the right one begins");
            t.Check(l[0].X < l[^1].X, "desk: and runs left to right itself");

            // Without the canvas both span the same 0..1, which is exactly the
            // restarting behaviour the desk view fixes.
            var plainLeft = EffectEngine.ZonePositions(left, 0, 10);
            var plainRight = EffectEngine.ZonePositions(right, 0, 10);
            t.Check(Math.Abs(plainLeft[0].X - plainRight[0].X) < 1e-6,
                  "desk: without a canvas both devices start at the same coordinate");
            t.Check(Math.Abs(plainLeft[^1].X - plainRight[^1].X) < 1e-6,
                  "desk: and end at the same one, which is why a wave restarts");

            // A device placed further right maps further right: the ordering is the
            // arrangement, not the device list.
            layout.ItemFor("Left")!.X = 600;
            layout.ItemFor("Right")!.X = 0;
            var swappedL = CanvasMapper.Positions(left, 0, 10, layout)!;
            var swappedR = CanvasMapper.Positions(right, 0, 10, layout)!;
            t.Check(swappedR[^1].X < swappedL[0].X, "desk: moving a device moves where the effect reaches it");
        }

        t.Section("Whole-desk canvas: zones keep their place (#f9)");
        {
            // ZonePositions renormalizes a range to its own bounding box, which is
            // right for a per-device effect: a zone should fill its own span. It is
            // wrong for the desk, where it would stretch every zone across the whole
            // device rectangle so two zones rendered at the same phase, which is the
            // restarting the desk view exists to stop.
            var device = new FakeDevice { Name = "Strip", LedCount = 10 };

            var firstHalf = EffectEngine.ZonePositions(device, 0, 5);
            var secondHalf = EffectEngine.ZonePositions(device, 5, 5);
            t.Check(Math.Abs(firstHalf[0].X - secondHalf[0].X) < 1e-6,
                  "zones: renormalized, both halves start at the same coordinate");

            var firstDev = EffectEngine.DevicePositions(device, 0, 5);
            var secondDev = EffectEngine.DevicePositions(device, 5, 5);
            t.Check(firstDev[^1].X < secondDev[0].X,
                  "zones: in device coordinates the first half ends before the second begins");
            t.Check(firstDev[0].X < 0.01f, "zones: and the first starts at the device's own start");
            t.Check(secondDev[^1].X > 0.99f, "zones: and the second ends at its end");

            // Which is what the canvas mapping uses, so two zones of one device land on
            // different parts of its rectangle on the desk.
            var layout = new CanvasLayout { Enabled = true, Width = 1000, Height = 1000 };
            layout.Items.Add(new CanvasItem { Device = "Strip", X = 0, Y = 0, W = 1000, H = 100 });
            var deskFirst = CanvasMapper.Positions(device, 0, 5, layout)!;
            var deskSecond = CanvasMapper.Positions(device, 5, 5, layout)!;
            t.Check(deskFirst[^1].X < deskSecond[0].X, "zones: and they stay apart on the desk");

            // A whole-device range is unaffected either way.
            var whole = CanvasMapper.Positions(device, 0, 10, layout)!;
            t.Equal(10, whole.Length, "zones: a whole-device range still maps every led");
            t.Check(whole[0].X < whole[^1].X, "zones: running the length of its rectangle");
        }

        t.Section("Led overrides reach the engine (#f9)");
        {
            // ZonePositions is what every channel renders against, so an override has
            // to win there or the feature does nothing.
            var device = new FakeDevice { Name = "Strip", LedCount = 6 };
            var before = EffectEngine.ZonePositions(device, 0, 6);

            var layout = new CanvasLayout { Enabled = false, Width = 1000, Height = 1000 };
            layout.Items.Add(new CanvasItem
            {
                Device = "Strip",
                LedLayout = new LedLayoutOverride { Shape = "grid", Cols = 3, Rows = 2 },
            });
            var previous = CanvasLayout.Current;
            CanvasLayout.Current = layout;
            try
            {
                var after = EffectEngine.ZonePositions(device, 0, 6);
                // A 3x2 grid is two rows, so the Y coordinates now differ; the flat
                // fallback had them all on one line.
                t.Check(Math.Abs(before[0].Y - before[5].Y) < 1e-6, "override: the fallback is flat");
                t.Check(Math.Abs(after[0].Y - after[5].Y) > 0.5, "override: the grid is not");
                // And it applies with the canvas OFF: fixing a shape is useful on its own.
                t.Check(!layout.Enabled, "override: with the desk switched off");
            }
            finally { CanvasLayout.Current = previous; }

            // Back to normal once the override is gone.
            var plain = EffectEngine.ZonePositions(device, 0, 6);
            t.Check(Math.Abs(plain[0].Y - plain[5].Y) < 1e-6, "override: removing it restores the fallback");
        }

        t.Section("Whole-desk canvas: mapping (#f9)");
        {
            // A 100x100 device at (100,100) on a 1000x1000 desk: local (0,0) is its
            // top-left corner, so it lands at desk 0.1,0.1.
            var item = new CanvasItem { Device = "D", X = 100, Y = 100, W = 100, H = 100 };
            bool Near(LedPos a, double x, double y) => Math.Abs(a.X - x) < 1e-5 && Math.Abs(a.Y - y) < 1e-5;

            t.Check(Near(CanvasMapper.Map(new LedPos(0, 0), item, 1000, 1000), 0.1, 0.1), "canvas: top-left corner");
            t.Check(Near(CanvasMapper.Map(new LedPos(1, 1), item, 1000, 1000), 0.2, 0.2), "canvas: bottom-right corner");
            t.Check(Near(CanvasMapper.Map(new LedPos(0.5f, 0.5f), item, 1000, 1000), 0.15, 0.15), "canvas: the middle");

            // Rotation turns the device's layout inside its rectangle. 90 clockwise
            // sends the top-left corner to the top-right.
            var r90 = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, Rotation = 90 };
            t.Check(Near(CanvasMapper.Map(new LedPos(0, 0), r90, 1000, 1000), 1, 0), "canvas: 90 sends top-left to top-right");
            t.Check(Near(CanvasMapper.Map(new LedPos(1, 0), r90, 1000, 1000), 1, 1), "canvas: and top-right to bottom-right");

            var r180 = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, Rotation = 180 };
            t.Check(Near(CanvasMapper.Map(new LedPos(0, 0), r180, 1000, 1000), 1, 1), "canvas: 180 sends top-left to bottom-right");

            var r270 = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, Rotation = 270 };
            t.Check(Near(CanvasMapper.Map(new LedPos(0, 0), r270, 1000, 1000), 0, 1), "canvas: 270 sends top-left to bottom-left");

            // Four 90s are a full turn.
            var full = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, Rotation = 360 };
            t.Check(Near(CanvasMapper.Map(new LedPos(0.25f, 0.75f), full, 1000, 1000), 0.25, 0.75), "canvas: 360 is no rotation");
            var negative = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, Rotation = -90 };
            t.Check(Near(CanvasMapper.Map(new LedPos(0, 0), negative, 1000, 1000), 0, 1), "canvas: -90 is the same as 270");
            var junk = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, Rotation = 45 };
            t.Check(Near(CanvasMapper.Map(new LedPos(0.25f, 0.75f), junk, 1000, 1000), 0.25, 0.75), "canvas: a rotation we do not do is no rotation");

            var flipX = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, FlipX = true };
            t.Check(Near(CanvasMapper.Map(new LedPos(0, 0.25f), flipX, 1000, 1000), 1, 0.25), "canvas: flipX mirrors left to right");
            var flipY = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, FlipY = true };
            t.Check(Near(CanvasMapper.Map(new LedPos(0.25f, 0), flipY, 1000, 1000), 0.25, 1), "canvas: flipY mirrors top to bottom");

            // Flip happens BEFORE rotation. Doing it after would mirror a different
            // axis than the button the user pressed.
            var both = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, FlipX = true, Rotation = 90 };
            t.Check(Near(CanvasMapper.Map(new LedPos(0, 0), both, 1000, 1000), 1, 1), "canvas: flip is applied before rotation");

            // A device the desk does not know about renders as it always has.
            var layout = new CanvasLayout { Enabled = true, Width = 1000, Height = 1000 };
            layout.Items.Add(new CanvasItem { Device = "Known", X = 0, Y = 0, W = 500, H = 500 });
            var known = new FakeDevice { Name = "Known", LedCount = 4 };
            var unknown = new FakeDevice { Name = "Stranger", LedCount = 4 };
            t.Check(CanvasMapper.Positions(unknown, 0, 4, layout) == null, "canvas: an unplaced device falls back");
            t.Check(CanvasMapper.Positions(known, 0, 4, layout) != null, "canvas: a placed device maps");

            // Off means off: byte-identical to the old behaviour is the whole promise.
            layout.Enabled = false;
            t.Check(CanvasMapper.Positions(known, 0, 4, layout) == null, "canvas: disabled falls back");
            t.Check(CanvasMapper.Positions(known, 0, 4, null) == null, "canvas: no layout at all falls back");
            layout.Enabled = true;

            // A device in the left half of the desk maps into the left half, which is
            // what makes a wave carry from one device to the next.
            var mapped = CanvasMapper.Positions(known, 0, 4, layout)!;
            t.Equal(4, mapped.Length, "canvas: one position per led");
            foreach (var q in mapped)
                t.Check(q.X >= 0 && q.X <= 0.5f && q.Y >= 0 && q.Y <= 0.5f, "canvas: it lands inside its own rectangle");
        }

        t.Section("Whole-desk canvas: led layouts (#f9)");
        {
            bool Near(LedPos a, double x, double y) => Math.Abs(a.X - x) < 1e-5 && Math.Abs(a.Y - y) < 1e-5;

            var strip = CanvasMapper.FromOverride(new LedLayoutOverride { Shape = "strip" }, 5)!;
            t.Equal(5, strip.Length, "layout: a strip has one position per led");
            t.Check(Near(strip[0], 0, 0.5), "layout: strip starts at the left");
            t.Check(Near(strip[4], 1, 0.5), "layout: and ends at the right");
            t.Check(Near(strip[2], 0.5, 0.5), "layout: evenly spaced");

            var one = CanvasMapper.FromOverride(new LedLayoutOverride { Shape = "strip" }, 1)!;
            t.Check(Near(one[0], 0.5, 0.5), "layout: a single led sits in the middle, not at an edge");

            var ring = CanvasMapper.FromOverride(new LedLayoutOverride { Shape = "ring" }, 4)!;
            t.Equal(4, ring.Length, "layout: a ring has one position per led");
            t.Check(Near(ring[0], 0.5, 0), "layout: a ring starts at the top");
            t.Check(Near(ring[1], 1, 0.5), "layout: and runs clockwise");
            t.Check(Near(ring[2], 0.5, 1), "layout: through the bottom");
            t.Check(Near(ring[3], 0, 0.5), "layout: and back up the left");

            // Serpentine: every other row is wired backwards, so led 3 of a 3-wide
            // grid sits under led 2, not under led 0.
            var straight = CanvasMapper.FromOverride(
                new LedLayoutOverride { Shape = "grid", Cols = 3, Rows = 2 }, 6)!;
            t.Check(Near(straight[0], 0, 0), "layout: grid starts top-left");
            t.Check(Near(straight[2], 1, 0), "layout: across the first row");
            t.Check(Near(straight[3], 0, 1), "layout: then back to the left on the next");

            var snake = CanvasMapper.FromOverride(
                new LedLayoutOverride { Shape = "grid", Cols = 3, Rows = 2, Serpentine = true }, 6)!;
            t.Check(Near(snake[2], 1, 0), "layout: serpentine first row is the same");
            t.Check(Near(snake[3], 1, 1), "layout: but the second row starts where the first ended");
            t.Check(Near(snake[5], 0, 1), "layout: and ends where it would have started");

            // A description that cannot hold the LEDs is refused rather than half
            // applied: a wrong layout is worse than the fallback.
            t.Check(CanvasMapper.FromOverride(new LedLayoutOverride { Shape = "grid", Cols = 2, Rows = 2 }, 9) == null,
                  "layout: a grid too small for the leds is refused");
            t.Check(CanvasMapper.FromOverride(new LedLayoutOverride { Shape = "spiral" }, 4) == null,
                  "layout: an unknown shape is refused");
            t.Check(CanvasMapper.FromOverride(null, 4) == null, "layout: no override is no override");
            t.Check(CanvasMapper.FromOverride(new LedLayoutOverride { Shape = "strip" }, 0) == null,
                  "layout: a device with no leds is refused");
        }

        t.Section("Whole-desk canvas: layout file (#f9)");
        {
            var layout = new CanvasLayout { Enabled = true, Width = 1600, Height = 900 };
            var devices = new IRgbDevice[]
            {
                new FakeDevice { Name = "Board", LedCount = 50 },
                new FakeDevice { Name = "Keeb", LedCount = 116 },
            };
            layout.AutoArrange(devices);
            t.Equal(2, layout.Items.Count, "canvas: every device gets a place");

            // Running it again must not shuffle a desk the user has arranged.
            var moved = layout.ItemFor("Board")!;
            moved.X = 42; moved.Y = 43;
            layout.AutoArrange(devices);
            t.Equal(2, layout.Items.Count, "canvas: arranging again adds nothing");
            t.Equal(42.0, layout.ItemFor("Board")!.X, "canvas: and leaves a placed device alone");

            // A new device turning up later gets a place without disturbing the rest.
            layout.AutoArrange(new IRgbDevice[] { new FakeDevice { Name = "Fans", LedCount = 80 } });
            t.Equal(3, layout.Items.Count, "canvas: a new device is placed");
            t.Equal(42.0, layout.ItemFor("Board")!.X, "canvas: the others do not move");

            // Everything lands on the desk, never off the edge.
            foreach (var it in layout.Items)
            {
                t.Check(it.X >= 0 && it.Y >= 0, "canvas: nothing is placed above or left of the desk");
                t.Check(it.X + it.W <= layout.Width + 0.001, "canvas: nothing hangs off the right");
                t.Check(it.Y + it.H <= layout.Height + 0.001, "canvas: nothing hangs off the bottom");
            }

            // A device that has gone away keeps its entry, in case it comes back.
            layout.AutoArrange(new IRgbDevice[] { new FakeDevice { Name = "Keeb", LedCount = 116 } });
            t.Check(layout.ItemFor("Fans") != null, "canvas: an absent device keeps its place");

            // The file: a round trip has to keep every field, including the override.
            layout.ItemFor("Fans")!.LedLayout = new LedLayoutOverride
            { Shape = "grid", Cols = 8, Rows = 10, Serpentine = true };
            layout.ItemFor("Keeb")!.Rotation = 270;
            layout.ItemFor("Keeb")!.FlipY = true;

            string json = JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true });
            var back = JsonSerializer.Deserialize<CanvasLayout>(json)!;
            t.Equal(layout.Items.Count, back.Items.Count, "canvas: the items survive a round trip");
            t.Equal(270, back.ItemFor("Keeb")!.Rotation, "canvas: rotation survives");
            t.Check(back.ItemFor("Keeb")!.FlipY, "canvas: flip survives");
            t.Equal(8, back.ItemFor("Fans")!.LedLayout!.Cols, "canvas: the led override survives");
            t.Check(back.ItemFor("Fans")!.LedLayout!.Serpentine, "canvas: including serpentine");
            t.Equal(42.0, back.ItemFor("Board")!.X, "canvas: and the positions");

            // An older file, written before any of this existed.
            var old = JsonSerializer.Deserialize<CanvasLayout>("{}")!;
            t.Check(!old.Enabled, "canvas: an empty file is a disabled canvas");
            t.Equal(0, old.Items.Count, "canvas: with nothing placed");
            t.Equal(1600, old.Width, "canvas: and a default desk size");

            // A clone must not share items with the original: the editor's undo
            // depends on snapshots that do not move when the live layout does.
            var clone = layout.Clone();
            clone.ItemFor("Board")!.X = 999;
            t.Equal(42.0, layout.ItemFor("Board")!.X, "canvas: a clone is independent");
        }
    }
}

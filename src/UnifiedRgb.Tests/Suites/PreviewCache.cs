using UnifiedRgb.App;
using UnifiedRgb.Core;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The preview's geometry cache, and the promise it makes.      |
|                                                              |
| LedPreview pulls at 30 Hz and compares what it gets by       |
| ReferenceEquals: when the arrays are the ones it already      |
| had, it skips its per-tick compares entirely. That only       |
| works while the cache hands back the SAME instances.          |
|                                                              |
| Break it and nothing fails. No exception, no log line, no     |
| wrong pixel - the preview simply starts doing thirty times    |
| the work a second, and rebuilding position arrays for every   |
| LED on every tick while frames are going out at 60 Hz. It is  |
| the same shape as every other bug this codebase has had:      |
| something stops working and says nothing. So it is pinned     |
| here rather than left to a comment.                           |
\*-----------------------------------------------------------*/
static class PreviewCacheSuite
{
    sealed class Dev : IRgbDevice
    {
        public string Name { get; init; } = "Preview";
        public string Vendor => "Test";
        public DeviceType Type { get; init; } = DeviceType.Other;
        public int LedCount { get; init; } = 8;
        public IReadOnlyList<RgbZone> Zones => Array.Empty<RgbZone>();
        public IReadOnlyList<LedPos>? LedPositions { get; set; }
        public float? PreviewAspect { get; init; }
        public bool SetColors(IReadOnlyList<Rgb> colors) => true;
        public void Dispose() { }
    }

    public static void Run(Harness t)
    {
        var dev = new Dev { LedCount = 8 };
        var frame = Enumerable.Range(0, 8).Select(i => new Rgb((byte)i, 0, 0)).ToArray();
        var cache = new TargetPreview();

        t.Section("the same selection hands back the same instances");
        var a = cache.Of(dev, null, frame);
        var b = cache.Of(dev, null, frame);
        t.Check(ReferenceEquals(a.Pos, b.Pos), "positions are the same array, so the preview's fast path holds");
        t.Check(ReferenceEquals(a.Colors, b.Colors), "the colour buffer is refilled in place, not reallocated");
        t.Equal(a.Style, b.Style, "style does not churn");
        t.Equal(a.Aspect, b.Aspect, "aspect does not churn");

        t.Section("...and the colours still follow the frame");
        var moved = Enumerable.Range(0, 8).Select(i => new Rgb(0, (byte)i, 0)).ToArray();
        var c = cache.Of(dev, null, moved);
        t.Equal(new Rgb(0, 7, 0), c.Colors[7], "reusing the buffer does not mean reusing stale colours");

        t.Section("a changed selection rebuilds");
        var zone = new RgbZone { Name = "Half", Offset = 0, Count = 4 };
        var z = cache.Of(dev, zone, frame);
        t.Check(!ReferenceEquals(a.Pos, z.Pos), "a different zone is a different shape, so the arrays are rebuilt");
        t.Equal(4, z.Colors.Length, "...and sized to the zone");

        // A rescan hands out new device instances AND new position lists. The
        // cache keyed on the device alone would keep the old geometry, which is
        // how a preview ends up drawing the shape of a device that is gone.
        t.Section("new positions from the same device rebuild too");
        dev.LedPositions = Enumerable.Range(0, 8).Select(i => new LedPos(i / 8f, 0.5f)).ToArray();
        var afterRescan = cache.Of(dev, null, frame);
        var again = cache.Of(dev, null, frame);
        t.Check(!ReferenceEquals(z.Pos, afterRescan.Pos), "the device handing out a new position list is a rebuild");
        t.Check(ReferenceEquals(afterRescan.Pos, again.Pos), "...and then it settles again");

        t.Section("a zone wider than the frame does not read past it");
        var wide = new RgbZone { Name = "Wide", Offset = 0, Count = 8 };
        var shortFrame = new[] { new Rgb(9, 9, 9) };
        var w = cache.Of(dev, wide, shortFrame);
        t.Equal(new Rgb(9, 9, 9), w.Colors[0], "what the frame has is used");
        t.Equal(Rgb.Black, w.Colors[7], "...and what it does not is black, not an exception");
    }
}

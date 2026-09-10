using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| ChromaFeed: the hand-off between a Razer Chroma game writing |
| through our shim and the effects that sample what it wrote.  |
|                                                              |
| Both sections drive the same static feed, so they live in    |
| one suite and run in this order on purpose. The first pins   |
| the basic publish and sample contract, including that a      |
| grid smaller than its declared dimensions is rejected        |
| outright rather than sampled past its end. The second covers |
| the slot rules a real game exercises: a keyboard push wins   |
| over a ChromaLink one until its stamp goes stale, and Touch  |
| keeps the feed alive between frames.                         |
|                                                              |
| The second section sleeps past the staleness window, which   |
| is why it is here rather than folded into the first.         |
\*-----------------------------------------------------------*/
static class ChromaSuite
{
    public static void Run(Harness t)
    {
        t.Section("ChromaFeed frame publish");
        {
            ChromaFeed.PushGrid(new[] { new Rgb(10, 0, 0), new Rgb(0, 10, 0), new Rgb(0, 0, 10) }, 1, 3);
            t.Check(ChromaFeed.Active, "PushGrid marks the feed active");
            t.Equal(new Rgb(10, 0, 0), ChromaFeed.Sample(0.05f, 0.5f), "Sample left cell");
            t.Equal(new Rgb(0, 0, 10), ChromaFeed.Sample(0.95f, 0.5f), "Sample right cell");
            t.Equal(new Rgb(0, 0, 10), ChromaFeed.Sample(5f, -3f), "Sample clamps out-of-range coordinates");
            ChromaFeed.PushGrid(new[] { new Rgb(7, 7, 7) }, 6, 22);   // dims larger than the grid: rejected
            t.Equal(new Rgb(10, 0, 0), ChromaFeed.Sample(0.05f, 0.5f), "undersized grid is rejected (dims/grid published atomically)");
            ChromaFeed.PushGrid(new[] { new Rgb(7, 7, 7) }, 1, 1);
            t.Equal(new Rgb(7, 7, 7), ChromaFeed.Sample(0.9f, 0.9f), "1x1 static frame samples everywhere");
        }

        t.Section("ChromaFeed keyboard / ChromaLink slots + Touch (#54)");
        {
            var kb = new[] { new Rgb(7, 7, 7) };
            var cl = new[] { new Rgb(10, 20, 30), new Rgb(11, 21, 31), new Rgb(12, 22, 32), new Rgb(13, 23, 33), new Rgb(14, 24, 34) };
            ChromaFeed.PushGrid(kb, 1, 1);
            ChromaFeed.PushGrid(cl, 1, 5, type: 2);
            t.Equal(kb[0], ChromaFeed.Sample(0.05f, 0.5f), "a fresh keyboard grid wins over a ChromaLink push");
            Thread.Sleep(1100);                                  // keyboard stamp goes stale
            ChromaFeed.PushGrid(cl, 1, 5, type: 2);
            t.Equal(cl[0], ChromaFeed.Sample(0.05f, 0.5f), "stale keyboard yields to the ChromaLink grid (left)");
            t.Equal(cl[4], ChromaFeed.Sample(0.95f, 0.5f), "ChromaLink 1x5 samples across X (right)");
            t.Equal(cl[2], ChromaFeed.Sample(0.5f, 0.9f), "ChromaLink 1x5 ignores Y");
            ChromaFeed.Touch();
            t.Check(ChromaFeed.Active && ChromaFeed.Sample(0.05f, 0.5f) == cl[0], "Touch keeps the feed active without touching a grid");
            ChromaFeed.PushGrid(new[] { new Rgb(9, 9, 9) }, 1, 1);
            t.Equal(new Rgb(9, 9, 9), ChromaFeed.Sample(0.05f, 0.5f), "a new keyboard frame takes over again");
            ChromaFeed.PushGrid(new[] { new Rgb(1, 1, 1) }, 2, 3, type: 2);   // undersized: rejected
            t.Equal(new Rgb(9, 9, 9), ChromaFeed.Sample(0.05f, 0.5f), "undersized ChromaLink grid is rejected");
        }
    }
}

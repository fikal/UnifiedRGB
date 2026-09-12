using System.Diagnostics;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.App.Services;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Engine <-> LightingController write ordering.                |
|                                                              |
| Two paths put frames on a device: effect workers write it    |
| DIRECTLY from their own thread, and everything else (statics,|
| SDK clients, lights-off) goes through the applier lanes from |
| LightingController. These tests pin the two promises that    |
| hold the pair together: a stopped effect can never have the  |
| last word (the per-device write gate), and a lane executes   |
| overlapping external writes in an order that ends on the     |
| latest one (one accumulated frame per device). Listed in     |
| Suites.cs as "EngineLighting" and run by name.               |
\*-----------------------------------------------------------*/
static class EngineLightingSuite
{
    public static void Run(Harness t)
    {
        // Every static push scales by master brightness on the way out; the
        // expectations below compare raw colors, so pin it for the duration
        // and hand back whatever the rest of the harness had.
        double savedBrightness = Master.Brightness;
        Master.Brightness = 1.0;
        try
        {
            ExternalPartialWritesAccumulate(t);
            ExternalWholeFrameSupersedesOlderPartials(t);
            StoppedEffectCannotOutlastStaticReplacement(t);
            PartialAfterWholeFrameStillLandsAfterIt(t);
            StoppedChannelGettingTheGateDoesNotWrite(t);
            ComposedFrameReusesTheCallerBuffers(t);
        }
        finally { Master.Brightness = savedBrightness; }
    }

    /*---------------- the preview pull, without the per-pull garbage ----------------*/
    static void ComposedFrameReusesTheCallerBuffers(Harness t)
    {
        t.Section("LightingController: the preview pull reuses the caller's buffers");
        var lighting = new UnifiedRgb.App.Services.LightingController();
        var dev = new FakeDevice { Name = "Preview", LedCount = 4 };
        var red = new Rgb(255, 0, 0);
        lighting.PushExternalFrame(dev, 0, new[] { red, red, red, red });
        lighting.Applier.Drain(2000);

        // The desk preview asks for every device 30 times a second. A frame clone
        // and a channel list per device per pull was most of what an idle app with
        // that window open allocated.
        var chans = new List<UnifiedRgb.Core.Effects.EffectEngine.Channel>();
        var first = lighting.ComposedFrame(dev, null, chans);
        var again = lighting.ComposedFrame(dev, first, chans);
        t.Check(ReferenceEquals(first, again), "a buffer of the right length is filled rather than replaced");

        // Same ANSWER as the allocating overload - the reuse must not change what
        // the dots are painted with.
        var fresh = lighting.ComposedFrame(dev);
        t.Check(!ReferenceEquals(fresh, again), "the no-buffer overload still hands back its own array");
        t.Check(fresh.Length == again.Length && fresh.SequenceEqual(again),
            "...carrying exactly what the reusing overload produced");

        // A buffer from a DIFFERENT device must not be written past its end, nor
        // quietly truncate this one: the preview walks devices of mixed sizes with
        // one buffer.
        var small = new Rgb[2];
        var grown = lighting.ComposedFrame(dev, small, chans);
        t.Check(!ReferenceEquals(grown, small), "a buffer of the wrong length is replaced, not overrun");
        t.Equal(4, grown.Length, "...with one the device's size");

        // The channel list is the caller's and is refilled, not appended to.
        chans.Add(null!);
        lighting.ComposedFrame(dev, grown, chans);
        t.Check(!chans.Contains(null!), "the channel list is cleared before it is refilled");
    }

    /*---------------- B3: one client's partials compose, and never touch the statics ----------------*/
    static void ExternalPartialWritesAccumulate(Harness t)
    {
        t.Section("LightingController: external partial writes accumulate (B3)");
        {
            var lighting = new UnifiedRgb.App.Services.LightingController();
            var dev = new FakeDevice { Name = "Ext", LedCount = 2 };
            var red = new Rgb(255, 0, 0); var blue = new Rgb(0, 0, 255);

            // An SDK client painting one LED at a time. Each write used to be merged
            // over the user's STORED statics, which never contain the client's own
            // earlier writes, so the second one rebuilt LED 0 from black.
            lighting.PushExternalFrame(dev, 0, new[] { red });
            lighting.Applier.Drain(2000);
            lighting.PushExternalFrame(dev, 1, new[] { blue });
            lighting.Applier.Drain(2000);

            var shown = dev.Last;
            t.Check(shown != null && shown[0] == red && shown[1] == blue,
                "two partial external writes accumulate instead of erasing each other");

            // The user's saved lighting is what gets restored when the client leaves,
            // so the client must never have touched it.
            var stored = lighting.FrameFor(dev);
            t.Check(stored[0] == default && stored[1] == default,
                "an external client does not write into the user's stored statics");

            // Release, and the next client starts from the user's lighting again.
            lighting.ForgetExternal(dev);
            lighting.PushExternalFrame(dev, 1, new[] { blue });
            lighting.Applier.Drain(2000);
            shown = dev.Last;
            t.Check(shown != null && shown[0] == default && shown[1] == blue,
                "after release the next client does not inherit the last one's pixels");
        }
    }

    /*---------------- bug 10: full red / zone blue / full green -> green, green ----------------*/
    static void ExternalWholeFrameSupersedesOlderPartials(Harness t)
    {
        t.Section("external whole frame supersedes older partials");
        var lighting = new LightingController();
        var dev = new ZonedFake { Name = "Ext-Zoned", LedCount = 2 };

        // Park the lane on a job that waits, so the three posts below queue
        // up behind it and coalesce against each other instead of running one
        // at a time - the bug only exists with a backlog.
        using var release = new ManualResetEventSlim(false);
        lighting.Applier.Post(LightingController.LaneOf(dev), "block", () => release.Wait(5000));

        lighting.PushExternalFrame(dev, 0, new[] { Rgb.Red, Rgb.Red });       // whole frame
        lighting.PushExternalFrame(dev, 0, new[] { Rgb.Blue });               // zone(0)
        lighting.PushExternalFrame(dev, 0, new[] { Rgb.Green, Rgb.Green }); // whole frame again
        release.Set();
        lighting.Applier.Drain(2000);

        // With partials on their own key the lane ran full green THEN zone
        // blue, and LED 0 finished blue although the newest whole frame asked
        // for green. Every external write now rides one accumulated frame.
        t.Check(dev.Shown[0] == Rgb.Green && dev.Shown[1] == Rgb.Green,
            "a newer whole external frame wins over the older partial queued behind it (green/green)");
        t.Check(dev.ZoneWrites == 0, "external partials never reach the device as zone writes");
        t.Check(dev.FullWrites == 1, "the three posts coalesced into the one that was posted last");

        // The mirror case: a partial, a whole frame, then the SAME partial
        // again. Re-posted under its own key the partial kept its older queue
        // slot and ran BEFORE the whole frame, so LED 0 ended red.
        var dev2 = new ZonedFake { Name = "Ext-Zoned-2", LedCount = 2 };
        using var release2 = new ManualResetEventSlim(false);
        lighting.Applier.Post(LightingController.LaneOf(dev2), "block", () => release2.Wait(5000));
        lighting.PushExternalFrame(dev2, 0, new[] { Rgb.Blue });
        lighting.PushExternalFrame(dev2, 0, new[] { Rgb.Red, Rgb.Red });
        lighting.PushExternalFrame(dev2, 0, new[] { Rgb.Green });
        release2.Set();
        lighting.Applier.Drain(2000);
        t.Check(dev2.Shown[0] == Rgb.Green && dev2.Shown[1] == Rgb.Red,
            "a partial re-posted after a whole frame lands after it (green/red)");
        t.Check(dev2.FullWrites == 1 && dev2.ZoneWrites == 0, "the mirror case also coalesced into one whole write");
    }

    /*---------------- bug 9: worker blocked in SetColors(red) -> StopAll -> static blue -> blue ----------------*/
    static void StoppedEffectCannotOutlastStaticReplacement(Harness t)
    {
        t.Section("stopped effect cannot outlast static replacement");
        var lighting = new LightingController();
        var dev = new BlockingFake { Name = "Slow-Static", LedCount = 2 };
        var frame = lighting.FrameFor(dev);   // black; the channel composes over it

        // The effect's first frame enters the driver and stays there - the
        // shape of a HID write timeout or the wireless receiver's paced send.
        lighting.Engine.Start(dev, 0, 2, frame, new CountingEffect(), 1, Rgb.Red);
        t.Check(dev.Entered.Wait(2000), "the effect worker enters its first device write");

        // Stop gives the worker 300 ms, which it cannot meet while the write
        // is held; the join times out and logs, and StopAll returns anyway.
        var sw = Stopwatch.StartNew();
        lighting.Engine.StopAll();
        t.Check(sw.ElapsedMilliseconds >= 250, "StopAll waited out the join before giving up on the blocked worker");
        t.Check(dev.WriteCount == 0, "the blocked red write has not completed when StopAll returns");

        // The static replacement: posted to the lane, whose job now has to
        // wait for the gate the worker is still holding.
        Array.Fill(lighting.FrameFor(dev), Rgb.Blue);
        lighting.PushFrame(dev);
        Thread.Sleep(100);   // let the lane job reach the gate and queue behind the worker
        t.Check(dev.WriteCount == 0, "the static write waits on the gate instead of overtaking the effect frame");

        dev.Release.Set();
        lighting.Applier.Drain(2000);
        t.Check(WaitUntil(() => dev.WriteCount >= 2, 2000), "both the stale effect frame and the static land");
        var last = dev.Last;
        t.Check(last != null && last[0] == Rgb.Blue && last[1] == Rgb.Blue,
            "the static replacement lands LAST, after the effect's straggling frame (blue, not red)");
        t.Check(dev.WriteCount == 2, "the stopped worker wrote nothing after its straggler");
    }

    /*---------------- a partial posted after a whole frame still runs after it ----------------*/
    static void PartialAfterWholeFrameStillLandsAfterIt(Harness t)
    {
        t.Section("partial after whole frame still lands after it");
        var lighting = new LightingController();
        var dev = new ZonedFake { Name = "Ext-Order", LedCount = 2 };

        using var release = new ManualResetEventSlim(false);
        lighting.Applier.Post(LightingController.LaneOf(dev), "block", () => release.Wait(5000));

        lighting.PushExternalFrame(dev, 0, new[] { Rgb.Red, Rgb.Red });   // whole
        lighting.PushExternalFrame(dev, 1, new[] { Rgb.Blue });           // partial posted after: composes over it
        release.Set();
        lighting.Applier.Drain(2000);

        // The later partial composes over the whole frame in the accumulated
        // picture, so what lands is red with LED 1 blue - in one write.
        t.Check(dev.Shown[0] == Rgb.Red && dev.Shown[1] == Rgb.Blue,
            "a partial posted after a whole frame still lands after it (red/blue)");
        t.Check(dev.FullWrites == 1 && dev.ZoneWrites == 0, "the pair coalesced into one whole write carrying both");
    }

    /*---------------- a stopped channel that gets the gate after Stop does not write ----------------*/
    static void StoppedChannelGettingTheGateDoesNotWrite(Harness t)
    {
        t.Section("stopped channel getting the gate does not write");
        var engine = new EffectEngine();
        var dev = new BlockingFake { Name = "Gated", LedCount = 2 };
        dev.Release.Set();   // this one never blocks inside the driver

        // An effect whose output differs every frame, so the worker wants to
        // write on EVERY pass (a constant effect dedups down to the 1 s
        // keepalive and the test would prove nothing).
        engine.Start(dev, 0, 2, new Rgb[2], new ChangingEffect(), 1, Rgb.Red);
        t.Check(WaitUntil(() => dev.WriteCount > 0, 2000), "the changing effect streams its first frame");

        int atStop;
        lock (EffectEngine.WriteGateFor(dev))
        {
            // With the gate held here, the worker's next write blocks at the
            // gate - not inside the driver - so nothing lands while we hold it.
            Thread.Sleep(100);
            int held = dev.WriteCount;
            Thread.Sleep(100);
            t.Check(dev.WriteCount == held, "a worker cannot write while another thread holds the device's gate");

            // Stop while the worker is parked at the gate. The join cannot
            // complete (the worker is waiting on us) and gives up after 300 ms.
            engine.StopAll();
            atStop = dev.WriteCount;
        }
        // The gate is free: the worker acquires it, re-checks Running INSIDE
        // it, and must leave without the write it had already rendered.
        Thread.Sleep(300);
        t.Check(dev.WriteCount == atStop, "a stopped channel that acquires the gate after Stop does not write");
    }

    /// <summary>A zone-addressable device that keeps the LED state the
    /// hardware would be showing: SetColors replaces all of it, SetZone only
    /// its range - the exact thing the whole-vs-partial ordering bug is
    /// about, and why a "last frame written" recorder is not enough here.</summary>
    sealed class ZonedFake : IRgbDevice, IZoneWritable
    {
        public string Name { get; init; } = "Zoned";
        public string Vendor => "Test";
        public DeviceType Type => DeviceType.Other;
        public int LedCount { get; init; } = 2;
        public IReadOnlyList<RgbZone> Zones => new[] { new RgbZone { Name = "All", Offset = 0, Count = LedCount } };
        public readonly object Lock = new();
        Rgb[]? _shown;
        public Rgb[] Shown { get { lock (Lock) return (Rgb[])(_shown ??= new Rgb[LedCount]).Clone(); } }
        int _full, _zone;
        public int FullWrites => Volatile.Read(ref _full);
        public int ZoneWrites => Volatile.Read(ref _zone);
        public bool SetColors(IReadOnlyList<Rgb> colors)
        {
            lock (Lock)
            {
                _shown ??= new Rgb[LedCount];
                for (int i = 0; i < colors.Count && i < _shown.Length; i++) _shown[i] = colors[i];
            }
            Interlocked.Increment(ref _full);
            return true;   // canned success: this fake never refuses a write
        }
        public bool SetZone(int offset, IReadOnlyList<Rgb> colors)
        {
            lock (Lock)
            {
                _shown ??= new Rgb[LedCount];
                for (int i = 0; i < colors.Count && offset + i < _shown.Length; i++) _shown[offset + i] = colors[i];
            }
            Interlocked.Increment(ref _zone);
            return true;
        }
        public void Dispose() { }
    }

    /// <summary>A whole-device (non-zone) device whose FIRST SetColors parks
    /// inside the driver until Release is set, signalling Entered on the way
    /// in - the slow write that outlives the engine's 300 ms join. Every
    /// later write goes straight through. Records completed writes only, so
    /// a count of zero means "still inside the driver".</summary>
    sealed class BlockingFake : IRgbDevice
    {
        public string Name { get; init; } = "Blocking";
        public string Vendor => "Test";
        public DeviceType Type => DeviceType.Other;
        public int LedCount { get; init; } = 2;
        public IReadOnlyList<RgbZone> Zones => new[] { new RgbZone { Name = "All", Offset = 0, Count = LedCount } };
        public readonly ManualResetEventSlim Entered = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        readonly List<Rgb[]> _writes = new();
        int _calls;
        public int WriteCount { get { lock (_writes) return _writes.Count; } }
        public Rgb[]? Last { get { lock (_writes) return _writes.Count == 0 ? null : _writes[^1]; } }
        public bool SetColors(IReadOnlyList<Rgb> colors)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.Set();
                Release.Wait(5000);   // bounded: a broken gate must fail the test, not hang the harness
            }
            lock (_writes) _writes.Add(colors.ToArray());
            return true;   // canned success: slow, but never refused
        }
        public void Dispose() { }
    }

    /// <summary>Renders a different color every call, so the engine's
    /// write-boundary dedup never engages and each frame is a write.</summary>
    sealed class ChangingEffect : IEffect
    {
        public string Name => "Changing";
        public bool UsesBaseColor => true;
        int _n;
        public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb bc)
        {
            byte v = (byte)(Interlocked.Increment(ref _n) & 0xFF);
            Array.Fill(buf, new Rgb(v, (byte)(255 - v), v));
        }
    }
}

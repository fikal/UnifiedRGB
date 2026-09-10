using UnifiedRgb.App.Services;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Net;

namespace UnifiedRgb.Tests;

static class ReviewFixesSuite
{
    public static void Run(Harness t)
    {
        ShimPayloads(t);
        WiredRetry(t);
        MouseBlackout(t);
        StaticOrdering(t);
        BakeInputs(t);
    }

    static void ShimPayloads(Harness t)
    {
        t.Section("bundled Chroma payloads are readable without sidecar DLLs");
        foreach (string name in new[] { "RzChromaSDK64.dll", "RzChromaSDK.dll" })
        {
            using var payload = ChromaShimInstaller.OpenBundledShim(name);
            // Native builds are optional in source checkouts.
            if (payload == null) continue;
            using var copy = new System.IO.MemoryStream();
            payload.CopyTo(copy);
            var bytes = copy.ToArray();
            t.Check(bytes.Length > 1024 && bytes[0] == 'M' && bytes[1] == 'Z',
                $"{name} can be copied as a complete PE payload");
            if (name == "RzChromaSDK64.dll")
                t.Check(ChromaShimInstaller.ShimAvailable, "embedded x64 shim is available to installer");
        }
    }

    static void WiredRetry(Harness t)
    {
        t.Section("wired Lian Li retries every refused stage of a frame");
        for (int refused = 1; refused <= 6; refused++)
        {
            var hid = new FakeHid();
            using var hub = new LianLiUniHub(hid, 7);
            var red = Enumerable.Repeat(Rgb.Red, hub.LedCount).ToArray();
            hub.SetColors(red);
            var blue = Enumerable.Repeat(Rgb.Blue, hub.LedCount).ToArray();
            int sent = 0;
            hid.Accept = (_, _) => ++sent != refused;
            hub.SetColors(blue);
            hid.Accept = null;
            hid.Writes.Clear();
            hub.SetColors(blue);
            t.Equal(6, hid.Writes.Count, $"refused report {refused}: identical frame retries both ports");
            hid.Writes.Clear();
            hub.SetColors(blue);
            t.Equal(0, hid.Writes.Count, $"refused report {refused}: successful retry restores dedup");
        }
    }

    static void MouseBlackout(Harness t)
    {
        t.Section("one-shot Logitech blackout survives frame pacing");
        var hid = new FakeHid();
        hid.Accept = (_, p) => { hid.Replies.Enqueue((byte[])p.Clone()); return true; };
        using var mouse = new LogitechG403(hid, 0xFF, 1, 0x8071, new byte[] { 1 }, "Test mouse");
        var lighting = new LightingController();
        int savedGap = LogitechG403.MinFrameGapMs;
        try
        {
            LogitechG403.MinFrameGapMs = 100;
            lighting.Applier.Post(LightingController.LaneOf(mouse), "test", () =>
            {
                mouse.SetColors(new[] { Rgb.Red }, persist: true);
                hid.Writes.Clear();
                lighting.PushBlack(mouse);
            });
            lighting.Applier.Drain(2000);
            t.Check(hid.Writes.Count > 0, "blackout immediately after a static apply reaches the mouse");
            hid.Writes.Clear();
            mouse.SetColors(new[] { Rgb.Black }, persist: false);
            t.Equal(0, hid.Writes.Count, "delivered blackout is cached as black");
        }
        finally { lighting.Applier.Drain(2000); LogitechG403.MinFrameGapMs = savedGap; }
    }

    static void StaticOrdering(Harness t)
    {
        t.Section("static whole/zone writes preserve newest intent");
        double saved = Master.Brightness;
        Master.Brightness = 1;
        try
        {
            for (int scenario = 0; scenario < 3; scenario++)
            {
                var dev = new ZoneDevice();
                var lighting = new LightingController();
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                lighting.Applier.Post(LightingController.LaneOf(dev), "block", () =>
                { entered.Set(); release.Wait(5000); });
                t.Check(entered.Wait(2000), "static lane is parked for deterministic coalescing");
                var frame = lighting.FrameFor(dev);
                void Whole(Rgb c) { Array.Fill(frame, c); lighting.PushFrame(dev); }
                void Zone(Rgb c) { frame[0] = c; lighting.PushZone(dev, dev, 0, 1); }
                try
                {
                    if (scenario == 0) { Whole(Rgb.Red); Zone(Rgb.Blue); Whole(Rgb.Green); }
                    if (scenario == 1) { Zone(Rgb.Blue); Whole(Rgb.Red); Zone(Rgb.Green); }
                    if (scenario == 2) { Whole(Rgb.Red); Zone(Rgb.Blue); lighting.PushBlack(dev); }
                }
                finally { release.Set(); lighting.Applier.Drain(2000); }
                var expected = scenario == 0 ? new[] { Rgb.Green, Rgb.Green }
                    : scenario == 1 ? new[] { Rgb.Green, Rgb.Red } : new[] { Rgb.Black, Rgb.Black };
                t.Check(dev.Frame.SequenceEqual(expected), $"static ordering scenario {scenario} preserves final pixels");
            }
        }
        finally { Master.Brightness = saved; }
    }

    static void BakeInputs(Harness t)
    {
        t.Section("bake invalidation includes static siblings and canvas geometry");
        var channel = new EffectEngine.Channel
        {
            Device = new ZoneDevice(), Offset = 0, Count = 1, Effect = new Breathing(),
            Speed = 1, BaseColor = Rgb.Red, Pos = new[] { new LedPos(0, 0) }
        };
        var channels = new[] { channel };
        var frame = new[] { Rgb.Red, Rgb.Blue };
        string before = LianBakeService.BakeSignature(channels, frame);
        t.Equal(before, LianBakeService.BakeSignature(channels, (Rgb[])frame.Clone()), "unchanged bake remains deduplicated");
        frame[1] = Rgb.Green;
        t.Check(before != LianBakeService.BakeSignature(channels, frame), "static sibling edit invalidates baked frames");
        frame[1] = Rgb.Blue;
        channel.Pos = new[] { new LedPos(0.8f, 0.2f) };
        t.Check(before != LianBakeService.BakeSignature(channels, frame), "canvas move invalidates baked frames");
    }

    sealed class ZoneDevice : IRgbDevice, IZoneWritable
    {
        public Rgb[] Frame = new Rgb[2];
        public string Name => "Review test";
        public string Vendor => "Test";
        public DeviceType Type => DeviceType.Other;
        public int LedCount => 2;
        public IReadOnlyList<RgbZone> Zones => Array.Empty<RgbZone>();
        public void SetColors(IReadOnlyList<Rgb> colors)
        { for (int i = 0; i < colors.Count; i++) Frame[i] = colors[i]; }
        public void SetZone(int offset, IReadOnlyList<Rgb> colors)
        { for (int i = 0; i < colors.Count; i++) Frame[offset + i] = colors[i]; }
        public void Dispose() { }
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The lighting engine and everything that renders into it.     |
|                                                              |
| The suite is the biggest one in the harness because it is    |
| one subject, taken in three passes.                          |
|                                                              |
| First the rendering itself: the palette view a render thread |
| reads, the band and step-clock maths that decide what a      |
| frame looks like, the loop periods the baker relies on, and  |
| the gradient a pattern must agree with. These are pure       |
| functions over a buffer, so they are cheap and they run      |
| first.                                                       |
|                                                              |
| Then the engine's channel bookkeeping: which channels belong |
| to a device, what a new overlapping channel replaces, when a |
| stopped worker is really finished, and how several channels  |
| on one device compose into the single frame a non-zone       |
| device is written with. That composing rule is what makes    |
| the per-device write dedup subtle, so the dedup and the      |
| static-sibling case are tested next to it rather than apart. |
|                                                              |
| Last the live-input effects, which are the reason the engine |
| has an idle throttle at all: an effect fed by audio or key   |
| presses looks static to a frame comparison and must keep     |
| rendering anyway. The audio analyzer sits here too, since    |
| what it feeds is those effects.                              |
|                                                              |
| The engine sections drive real worker threads and real       |
| sleeps, which is why they are at the end of the suite.       |
\*-----------------------------------------------------------*/
static class EffectsSuite
{
    public static void Run(Harness t)
    {
        t.Section("LivePalette (render-thread palette view)");
        {
            var src = new ObservableCollection<Rgb> { new(1, 1, 1), new(2, 2, 2), new(3, 3, 3) };
            var live = new LivePalette(src);
            t.Equal(3, live.Count, "LivePalette tracks initial count");
            t.Equal(new Rgb(2, 2, 2), live[1], "LivePalette indexes the snapshot");
            var snap = live.Snapshot;
            src.Clear();
            t.Equal(0, live.Count, "LivePalette follows Clear()");
            t.Equal(3, snap.Length, "an older snapshot is immutable");
            t.Equal(new Rgb(255, 255, 255), live[0], "empty palette reads white, never throws");
            src.Add(new Rgb(9, 9, 9));
            t.Equal(new Rgb(9, 9, 9), live[5], "out-of-range index clamps to the last color (stale Count from a longer snapshot)");
            t.Equal(new Rgb(9, 9, 9), live[0], "in-range index after rebuild");
        }

        t.Section("AudioBars band bucketing at X = 1.0 (#110 #72)");
        {
            // No capture is required: hue depends only on X, so the checks below hold
            // whatever the analyzer's levels are.
            int n = 132;
            var pos = Line(n);   // includes exactly X = 1.0f
            var buf = new Rgb[n];
            bool threw = false;
            try { new AudioBars().Render(buf, pos, 1.0, 1.0, default); } catch (Exception) { threw = true; }
            t.Check(!threw, "AudioBars X=1.0 buckets to the last band, not one past it");
            t.Check(buf[n - 1].G == 0 && buf[n - 1].B == 0, "AudioBars rightmost LED is a pure-red hue whatever the level");
            t.Check(buf[0].R == 0, "AudioBars leftmost LED carries no red (hue 230)");
            t.Check(((IEffect)new AudioBars()).LiveInput && !((IEffect)new AudioBars()).Bakeable, "AudioBars: live input, not bakeable");
            t.Check(((IEffect)new AudioPulse()).LiveInput && !((IEffect)new AudioPulse()).Bakeable, "AudioPulse: live input, not bakeable");
        }

        t.Section("Reactive effects: flags, empty render, speed sign (#73 #71 #70 #72)");
        {
            IEffect kf = new KeyFade(), kr = new KeyRipple();
            t.Check(!kf.Bakeable && kf.LiveInput, "KeyFade: not bakeable, live input");
            t.Check(!kr.Bakeable && kr.LiveInput, "KeyRipple: not bakeable, live input");
            var pat = new PatternEffect { Motion = PatternMotion.AudioPulse };
            t.Check(((IEffect)pat).LiveInput && !pat.Bakeable, "PatternEffect audio motion is live and not bakeable");
            pat.Motion = PatternMotion.AudioLevel;
            t.Check(((IEffect)pat).LiveInput, "PatternEffect AudioLevel is live");
            pat.Motion = PatternMotion.Rotate;
            t.Check(!((IEffect)pat).LiveInput && pat.Bakeable, "PatternEffect rotate is not live and bakeable");
            t.Check(!((IEffect)new RainbowWave()).LiveInput && ((IEffect)new RainbowWave()).Bakeable, "IEffect.LiveInput defaults to false");
            t.Check(!((IEffect)new TempGlow()).Bakeable && !((IEffect)new ChromaSync()).Bakeable, "sensor/feed effects stay non-bakeable");

            // Unmapped device, no key events: ripple is black, fade is the resting
            // glow, and the sign of speed changes nothing (Reverse encodes as -speed).
            int n = 4096;
            var pos = Line(n);
            var a = new Rgb[n]; var b = new Rgb[n];
            var red = new Rgb(255, 0, 0);
            kr.Render(a, pos, 1.0, 2.0, red);
            t.Check(a.All(c => c == default), "KeyRipple with no presses renders black");
            kr.Render(b, pos, 1.0, -2.0, red);
            t.Check(a.AsSpan().SequenceEqual(b), "KeyRipple frame is identical at speed +2 and -2");
            kf.Render(a, pos, 1.0, 2.0, red);
            t.Check(a.All(c => c == ColorUtil.Scale(red, 0.06)), "KeyFade with no presses renders the 6% resting glow");
            kf.Render(b, pos, 1.0, -2.0, red);
            t.Check(a.AsSpan().SequenceEqual(b), "KeyFade frame is identical at speed +2 and -2");
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++) kr.Render(a, pos, 1.0 + i * 0.001, 1.0, red);
            t.Check(sw.ElapsedMilliseconds < 2000, $"KeyRipple 1000 empty frames on 4096 LEDs stay cheap ({sw.ElapsedMilliseconds} ms)");
        }

        t.Section("Step-clock wrap (#68)");
        {
            t.Equal(123456, Fx.Step(123456.7), "Fx.Step truncates");
            t.Equal(0, Fx.Step(Fx.StepWrap), "Fx.Step wraps to 0 at StepWrap");
            t.Equal(3, Fx.Step(Fx.StepWrap * 7 + 3.9), "Fx.Step wraps a multi-million value");
            t.Check(Fx.Step(2.2e9 * 1.8) >= 0 && Fx.Step(2.2e9 * 1.8) < 1_000_000, "Fx.Step stays in range past int.MaxValue");
            t.Check(Math.Abs(Fx.Frac((3.5e9 + 0.25) % Fx.StepWrap) - Fx.Frac(3.5e9 + 0.25)) < 1e-9, "Frac is unchanged by the integer wrap");
            // Every step-clock effect must still animate at t = 2.2e9 (an (int) cast of
            // the raw product saturated there and froze the pattern).
            var pos = Grid(8, 8);
            var red = new Rgb(255, 0, 0);
            foreach (IEffect fx in new IEffect[] { new Disco(), new Electric(), new Starfield(), new CandyBox(), new ColorfulMeteor(), new ColorCycle() })
            {
                var a = new Rgb[64]; var b = new Rgb[64];
                fx.Render(a, pos, 2.2e9, 1.0, red);
                fx.Render(b, pos, 2.2e9 + 1.0, 1.0, red);
                t.Check(!a.AsSpan().SequenceEqual(b), $"{fx.Name} still animates at t = 2.2e9");
            }
        }

        t.Section("Baked loops close on their period (#67)");
        {
            var pos = Grid(8, 8);
            var bc = new Rgb(200, 90, 30);
            foreach (IEffect fx in new IEffect[] { new StackOutline(), new Waterfall(), new Orbit(), new TideFx(), new Police(), new Fire() })
                foreach (double speed in new[] { 1.0, 2.5 })
                {
                    double loop = fx.LoopSeconds(speed);
                    t.Check(loop >= 1.5 / speed - 1e-9 && loop <= 12.0 / speed + 1e-9, $"{fx.Name} LoopSeconds({speed}) = {loop:F3} inside the baker's clamp scaled by speed");
                    var a = new Rgb[64]; var b = new Rgb[64];
                    fx.Render(a, pos, 0.37, speed, bc);
                    fx.Render(b, pos, 0.37 + loop, speed, bc);
                    t.Check(SameWithin(a, b, 1), $"{fx.Name} frame at t0 equals frame at t0 + LoopSeconds({speed})");
                }
            // Baked at speed 1 these must land inside the 1.5..12 s clamp outright.
            foreach (IEffect fx in new IEffect[] { new StackOutline(), new Waterfall(), new Orbit(), new TideFx(), new Police(), new Fire() })
                t.Check(fx.LoopSeconds(1.0) >= 1.5 && fx.LoopSeconds(1.0) <= 12.0, $"{fx.Name} LoopSeconds(1) = {fx.LoopSeconds(1.0):F3} in 1.5..12");
        }

        t.Section("PatternEffect gradient == PaletteFx.Sample (#143)");
        {
            var pal = new[] { new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255) };
            int n = 32;
            var pos = new LedPos[n];
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                pos[i] = new LedPos((float)(0.5 + 0.5 * Math.Cos(a)), (float)(0.5 + 0.5 * Math.Sin(a)));
            }
            foreach (double density in new[] { 1.0, 2.0 })
            {
                var pe = new PatternEffect { Color = PatternColor.Gradient, Motion = PatternMotion.Static, Palette = pal, Density = density };
                var buf = new Rgb[n];
                pe.Render(buf, pos, 3.3, 1.0, default);
                bool ok = true;
                for (int i = 0; i < n && ok; i++)
                {
                    double u = Fx.Frac(Math.Atan2(pos[i].Y - 0.5, pos[i].X - 0.5) / (Math.PI * 2.0) + 1.0);
                    ok = buf[i] == PaletteFx.Sample(pal, Fx.Frac(u * density));
                }
                t.Check(ok, $"static ring gradient (density {density}) samples PaletteFx.Sample at the ring coordinate");
            }
            t.Equal(new Rgb(255, 255, 255), PaletteFx.Sample(Array.Empty<Rgb>(), 0.3), "PaletteFx.Sample of an empty palette is white");
            t.Equal(pal[0], PaletteFx.Sample(pal, 1.0), "PaletteFx.Sample wraps u = 1 back to the first color");
        }

        t.Section("EffectEngine: ChannelsFor / replace / stop (#75 #74 #123)");
        {
            var engine = new EffectEngine();
            var d1 = new FakeDevice { Name = "D1", LedCount = 4 };
            var d2 = new FakeDevice { Name = "D2", LedCount = 4 };
            var d3 = new FakeDevice { Name = "D3", LedCount = 4 };
            var fx = new CountingEffect();
            var red = new Rgb(255, 0, 0);
            var c1 = engine.Start(d1, 0, 2, new Rgb[4], fx, 1, red);
            var c2 = engine.Start(d2, 0, 4, new Rgb[4], fx, 1, red);
            var c3 = engine.Start(d1, 2, 2, new Rgb[4], fx, 1, red);
            var list = engine.ChannelsFor(d1);
            t.Check(list.Count == 2 && ReferenceEquals(list[0], c1) && ReferenceEquals(list[1], c3), "ChannelsFor returns the device's channels in insertion order");
            t.Equal(0, engine.ChannelsFor(d3).Count, "ChannelsFor on a device with no channels is empty");
            t.Check(ReferenceEquals(engine.FindExact(d1, 2, 2), c3) && engine.FindExact(d1, 0, 4) == null, "FindExact matches the exact range only");
            var c4 = engine.Start(d1, 1, 2, new Rgb[4], fx, 1, red);   // overlaps both d1 channels
            t.Check(!c1.IsRunning && !c3.IsRunning && c4.IsRunning, "Start stops every overlapping channel");
            t.Check(engine.ChannelsFor(d1).Count == 1 && ReferenceEquals(engine.ChannelsFor(d1)[0], c4), "replaced channels leave the list");
            t.Check(c2.IsRunning && engine.ChannelsFor(d2).Count == 1, "another device's channel is untouched");
            engine.StopAll();
            t.Check(!c2.IsRunning && !c4.IsRunning && engine.ChannelsFor(d1).Count == 0 && engine.ChannelsFor(d2).Count == 0, "StopAll clears every channel");

            // A worker whose device write outlasts the 300 ms join must never START a
            // write after StopRange returned (pre-write Running re-check).
            var slow = new FakeDevice { Name = "Slow", LedCount = 2, WriteDelayMs = 400 };
            engine.Start(slow, 0, 2, new Rgb[2], fx, 1, red);
            t.Check(WaitUntil(() => slow.WriteCount > 0, 2000), "slow device receives its first frame");
            Thread.Sleep(30);
            engine.StopRange(slow, 0, 2);
            long stopped = Stopwatch.GetTimestamp();
            Thread.Sleep(600);
            t.Check(slow.Writes.All(w => w.Start <= stopped), "no engine write starts after StopRange returned");
            t.Equal(0, engine.ChannelsFor(slow).Count, "StopRange removed the channel");
        }

        t.Section("EffectEngine: InvalidateBase re-snapshots the live static frame (#69)");
        {
            var engine = new EffectEngine();
            var dev = new FakeDevice { Name = "Base", LedCount = 2 };
            var frame = new Rgb[2];
            var fx = new CountingEffect();
            var red = new Rgb(255, 0, 0); var blue = new Rgb(0, 0, 255);
            engine.Start(dev, 0, 1, frame, fx, 1, red);
            t.Check(WaitUntil(() => dev.WriteCount > 0, 2000), "first composed frame lands");
            var first = dev.Last;
            t.Check(first != null && first[0] == red && first[1] == default, "channel slice over the (black) static base");
            frame[1] = blue;                     // edit the LIVE static frame (a static pick on the sibling zone)
            engine.InvalidateBase(dev);
            t.Check(WaitUntil(() => dev.Last is { } l && l[1] == blue && l[0] == red, 2500), "InvalidateBase re-copies the base within the 1 s keepalive");
            engine.StopAll();
        }

        t.Section("AudioAnalyzer: hops are analyzed once each (B11)");
        {
            // 4 hops' worth of samples. Delivered as one big batch or as small chunks,
            // the analyzer must do the same amount of work: the window it transforms
            // has to ADVANCE per hop. It used to re-transform whichever window was
            // latest when the batch landed, so a big capture callback threw away the
            // intermediate audio and did duplicate FFTs of one window.
            const int Hop = 1024;
            var buf = new float[Hop * 4];
            for (int i = 0; i < buf.Length; i++) buf[i] = (float)Math.Sin(i * 0.01);

            UnifiedRgb.Core.Audio.AudioAnalyzer._analyses = 0;
            UnifiedRgb.Core.Audio.AudioAnalyzer._sinceAnalysis = 0;
            UnifiedRgb.Core.Audio.AudioAnalyzer.OnSamples(buf, buf.Length, 48000);
            int oneBatch = UnifiedRgb.Core.Audio.AudioAnalyzer._analyses;
            t.Equal(4, oneBatch, "one big batch analyzes each hop it contains exactly once");

            UnifiedRgb.Core.Audio.AudioAnalyzer._analyses = 0;
            UnifiedRgb.Core.Audio.AudioAnalyzer._sinceAnalysis = 0;
            for (int off = 0; off < buf.Length; off += 256)
            {
                var chunk = new float[256];
                Array.Copy(buf, off, chunk, 0, 256);
                UnifiedRgb.Core.Audio.AudioAnalyzer.OnSamples(chunk, 256, 48000);
            }
            t.Equal(oneBatch, UnifiedRgb.Core.Audio.AudioAnalyzer._analyses, "the same audio in small chunks analyzes the same number of hops");

            UnifiedRgb.Core.Audio.AudioAnalyzer._analyses = 0;
            UnifiedRgb.Core.Audio.AudioAnalyzer._sinceAnalysis = 0;   // start on a hop boundary
            UnifiedRgb.Core.Audio.AudioAnalyzer.OnSamples(buf, Hop - 1, 48000);
            t.Equal(0, UnifiedRgb.Core.Audio.AudioAnalyzer._analyses, "a partial hop waits for the rest instead of analyzing early");

            // The count alone cannot tell the two implementations apart - the old one
            // ran the same NUMBER of transforms, just all on the batch's final window.
            // So: flush the ring to silence, then hand over one batch whose first hop
            // is silent and whose second is full scale. Analyzed in order, the first
            // window is silent. Analyzed twice at the end, it is loud.
            var quiet = new float[Hop * 4];
            UnifiedRgb.Core.Audio.AudioAnalyzer._sinceAnalysis = 0;
            UnifiedRgb.Core.Audio.AudioAnalyzer.OnSamples(quiet, quiet.Length, 48000);

            var step = new float[Hop * 2];
            for (int i = Hop; i < step.Length; i++) step[i] = 1f;
            UnifiedRgb.Core.Audio.AudioAnalyzer._analyses = 0;
            UnifiedRgb.Core.Audio.AudioAnalyzer._sinceAnalysis = 0;
            UnifiedRgb.Core.Audio.AudioAnalyzer._firstRms = -1;
            UnifiedRgb.Core.Audio.AudioAnalyzer.OnSamples(step, step.Length, 48000);
            t.Equal(2, UnifiedRgb.Core.Audio.AudioAnalyzer._analyses, "the two-hop batch is analyzed as two hops");
            t.Check(UnifiedRgb.Core.Audio.AudioAnalyzer._firstRms >= 0 && UnifiedRgb.Core.Audio.AudioAnalyzer._firstRms < 0.1,
                "the first hop is analyzed as it stood, not as the end of the batch");
        }

        t.Section("EffectEngine: two animated channels on a NON-zone device");
        {
            // The device is written whole (FakeDevice is not IZoneWritable), so both
            // channels build the same full frame. Each used to build it from the
            // statics plus ONLY its own slice, so every write erased the other's
            // animation and the visible result was whichever worker wrote last.
            var engine = new EffectEngine();
            var dev = new FakeDevice { Name = "TwoZone", LedCount = 2 };
            var frame = new Rgb[2];
            var red = new Rgb(255, 0, 0); var blue = new Rgb(0, 0, 255);
            engine.Start(dev, 0, 1, frame, new CountingEffect(), 1, red);
            engine.Start(dev, 1, 1, frame, new CountingEffect(), 1, blue);
            t.Check(WaitUntil(() => dev.Last is { } l && l[0] == red && l[1] == blue, 3000),
                "two animated channels on a non-zone device: both slices reach the hardware");

            // Not just once: EVERY frame must carry both, or the two workers alternate
            // between a correct frame and one that drops a slice.
            // Deduped per DEVICE now, so a constant-colour effect settles and only the
            // 1 s keepalive writes - the point being that whatever does land carries
            // both slices.
            int n = dev.WriteCount;
            t.Check(WaitUntil(() => dev.WriteCount > n + 1, 5000), "the pair keeps writing (keepalive)");
            bool allGood = true;
            lock (dev.Writes)
                foreach (var (_, f) in dev.Writes.Skip(n))
                    if (f[0] != red || f[1] != blue) allGood = false;
            t.Check(allGood, "every subsequent whole-device write preserves both channels");

            // A static picked on a third range must survive the compose too.
            engine.StopAll();
        }

        t.Section("EffectEngine: composing must not defeat the write dedup");
        {
            // Two settled channels on one non-zone device. Composing means each
            // channel's output changes whenever ANY of them changes, so a per-channel
            // dedup would be false every frame and both would stream at 60 fps for as
            // long as the app runs. Deduped per device, a settled pair falls back to
            // the 1 s keepalive.
            var engine = new EffectEngine();
            var dev = new FakeDevice { Name = "Settled", LedCount = 2 };
            var frame = new Rgb[2];
            engine.Start(dev, 0, 1, frame, new CountingEffect(), 1, new Rgb(255, 0, 0));
            engine.Start(dev, 1, 1, frame, new CountingEffect(), 1, new Rgb(0, 0, 255));
            t.Check(WaitUntil(() => dev.WriteCount > 0, 2000), "the pair starts writing");
            Thread.Sleep(300);                      // let both settle
            int settled = dev.WriteCount;
            Thread.Sleep(2000);
            int during = dev.WriteCount - settled;
            // 60 fps x 2 channels would be ~240 in two seconds; the keepalive is ~2-4.
            t.Check(during <= 12, $"a settled pair writes at the keepalive, not per frame (saw {during} in 2 s)");
            engine.StopAll();
        }

        t.Section("EffectEngine: a static zone survives two animated siblings");
        {
            var engine = new EffectEngine();
            var dev = new FakeDevice { Name = "ThreeZone", LedCount = 3 };
            var frame = new Rgb[3];
            var red = new Rgb(255, 0, 0); var blue = new Rgb(0, 0, 255); var green = new Rgb(0, 255, 0);
            frame[2] = green;                    // a static colour on the third zone
            engine.Start(dev, 0, 1, frame, new CountingEffect(), 1, red);
            engine.Start(dev, 1, 1, frame, new CountingEffect(), 1, blue);
            t.Check(WaitUntil(() => dev.Last is { } l && l[0] == red && l[1] == blue && l[2] == green, 3000),
                "two animated channels plus a static third: all three ranges survive");
            engine.StopAll();
        }

        t.Section("EffectEngine: LiveInput bypasses the idle throttle (#72)");
        {
            var engine = new EffectEngine();
            var live = new CountingEffect { Live = true };
            var idle = new CountingEffect { Live = false };
            var d1 = new FakeDevice { Name = "Live", LedCount = 2 };
            var d2 = new FakeDevice { Name = "Idle", LedCount = 2 };
            engine.Start(d1, 0, 2, new Rgb[2], live, 1, new Rgb(1, 2, 3));
            engine.Start(d2, 0, 2, new Rgb[2], idle, 1, new Rgb(1, 2, 3));
            Thread.Sleep(2000);
            engine.StopAll();
            int l = live.Renders, i = idle.Renders;
            t.Check(l >= 55, $"LiveInput effect keeps rendering at full rate while static ({l} renders in 2 s)");
            t.Check(i <= 55, $"non-live static effect drops to the 10 fps check loop ({i} renders in 2 s)");
            t.Check(l > i, $"live renders ({l}) exceed throttled renders ({i})");
        }
    }
}

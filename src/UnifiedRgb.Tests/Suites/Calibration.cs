using System.IO;
using UnifiedRgb.Core;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Per-device and per-zone color calibration: the trim maths,  |
| the lookup table behind it, the override rule between the    |
| two levels, and the file they live in.                       |
|                                                              |
| THE FIRST SECTION IS THE IMPORTANT ONE, and it is not the    |
| interesting one. Calibration sits on the write boundary that |
| every other suite in this harness asserts exact colors      |
| through - the effect engine's frames, the applier's statics, |
| every driver's protocol bytes. If a DEFAULT calibration is   |
| not a byte-for-byte no-op then roughly a thousand checks     |
| elsewhere are wrong, and they would be wrong in the most     |
| expensive way available: off by one on a handful of LEDs,    |
| in suites that have nothing to do with this feature. So the  |
| no-op is pinned first, over the entire byte range, before    |
| anything clever is tested at all.                            |
|                                                              |
| Everything here mutates PROCESS-GLOBAL state (the live trim  |
| tables and calibration.json), so the whole suite runs inside |
| a try/finally that hands the rest of the harness a clean     |
| global back. A failure in the middle must not leak a trim    |
| into the suites that run after it.                           |
\*-----------------------------------------------------------*/
static class CalibrationSuite
{
    public static void Run(Harness t)
    {
        double keepBrightness = Master.Brightness;
        try { Body(t); }
        finally
        {
            Master.Brightness = keepBrightness;
            Calibration.ResetAll();
            // The file as well as the memory: a leftover calibration.json in
            // the isolation root would be picked up by anything that reloads.
            foreach (string f in CalibrationFiles()) TryDelete(f);
        }
    }

    static string PathName => AppPaths.Config("calibration.json");

    static List<string> CalibrationFiles()
    {
        var files = new List<string> { PathName };
        // The corrupt-file tests deliberately leave `.corrupt-<stamp>` copies
        // beside it, and those are just as much this suite's litter.
        string? dir = Path.GetDirectoryName(PathName);
        if (dir == null) return files;
        try { files.AddRange(Directory.GetFiles(dir, "calibration.json.corrupt-*")); } catch { }
        return files;
    }

    static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    /// <summary>A grey ramp plus a spread of saturated and off-axis colors.
    /// The greys are what catch a gamma or cap mistake; the colors are what
    /// catch a per-channel one (a table indexed with the wrong offset produces
    /// perfect greys and wrong everything else).</summary>
    static Rgb[] Probe()
    {
        var list = new List<Rgb>(256 + 64);
        for (int v = 0; v < 256; v++) list.Add(new Rgb((byte)v, (byte)v, (byte)v));
        for (int r = 0; r < 256; r += 51)
            for (int g = 0; g < 256; g += 85)
                for (int b = 0; b < 256; b += 85)
                    list.Add(new Rgb((byte)r, (byte)g, (byte)b));
        return list.ToArray();
    }

    /// <summary>The color the zone sections push. A flat mid-high grey rather
    /// than white, because white is pinned at the top of the range where a
    /// gain above 1 cannot move and gamma cannot move at all: the one signal
    /// guaranteed to hide a trim that is really there.</summary>
    static readonly Rgb Base = new(200, 200, 200);

    /// <summary>An eight-LED frame of it, matching the zoned fake those
    /// sections drive (Ribbon 0-3, Chipset 4-5, Cover 6-7).</summary>
    static Rgb[] Flat()
    {
        var buf = new Rgb[8];
        for (int i = 0; i < buf.Length; i++) buf[i] = Base;
        return buf;
    }

    static bool Same(Rgb[] a, Rgb[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static void Body(Harness t)
    {
        t.Section("nested fan zones and aid restoration");
        Calibration.ResetAll();
        Master.Brightness = 1;
        var nested = new NestedFan();
        Calibration.SetZone(nested.Name, "Fan 1", new DeviceCalibration { GainR = 0.5 });
        Calibration.SetZone(nested.Name, "Outer", new DeviceCalibration { GainR = 0.1 });
        var nestedFrame = Enumerable.Repeat(Rgb.White, 20).ToArray();
        Master.Finish(nested, nestedFrame);
        t.Equal((byte)128, nestedFrame[0].R, "whole fan trim covers the unoverridden inner ring");
        t.Equal((byte)26, nestedFrame[8].R, "outer-ring trim overrides its containing fan trim");
        t.Equal(Calibration.Apply(nested.Name, "Outer", Rgb.White), nestedFrame[19], "outer-ring UI and hardware agree");
        var slice = Enumerable.Repeat(Rgb.White, 4).ToArray();
        Master.Finish(nested, slice, 7);
        t.Equal((byte)128, slice[0].R, "partial writes retain enclosing trim before the nested boundary");
        t.Equal((byte)26, slice[1].R, "partial writes switch to the nested trim at its boundary");
        Calibration.ResetAll();

        var lighting = new UnifiedRgb.App.Services.LightingController();
        int captured = 0, restored = 0, refreshed = 0;
        var live = new UnifiedRgb.App.MainViewModel.LightState { Frames = new(), Effects = new(), Dirty = true, ProfileName = "unsaved edits" };
        UnifiedRgb.App.MainViewModel.LightState? saved = null;
        var aid = new UnifiedRgb.App.Services.CalibrationAid(lighting,
            () => { saved = live; captured++; }, () => { live = saved!; restored++; }, () => refreshed++);
        try
        {
            aid.Refresh();
            t.Equal(1, refreshed, "inactive aid forwards calibration changes to live-output refresh");
            aid.Start(new[] { nested });
            live = new UnifiedRgb.App.MainViewModel.LightState { Frames = new(), Effects = new() };
            aid.Refresh();
            t.Equal(1, refreshed, "active aid only repaints the reference");
            aid.Stop();
            t.Check(live.Dirty && live.ProfileName == "unsaved edits", "aid restores the captured dirty state instead of a saved profile");
            aid.Stop();
            t.Equal(1, captured, "aid captures once before stopping effects");
            t.Equal(1, restored, "closing after stopping the aid does not restore stale state twice");
        }
        finally { aid.Stop(); lighting.StopAndDrain(); }
        Calibration.ResetAll();

        /*-------------------------------------------------*\
        | 1. The no-op. Everything else depends on this.     |
        \*-------------------------------------------------*/
        t.Section("default calibration is a byte-exact no-op");
        {
            var probe = Probe();

            var buf = (Rgb[])probe.Clone();
            Calibration.Apply("Some Keyboard", buf);
            t.Check(Same(probe, buf), $"Apply on an uncalibrated device leaves all {probe.Length} probe colors untouched");

            t.Check(!Calibration.AnyCalibrated, "a fresh calibration has no tables at all (the short circuit is live)");

            // A device explicitly SET to defaults must be indistinguishable
            // from one that was never mentioned. If defaults were stored as a
            // table, a user who opened the pane and touched nothing would
            // start paying for a lookup and, worse, for its rounding.
            Calibration.Set("Some Keyboard", new DeviceCalibration());
            t.Check(!Calibration.AnyCalibrated, "setting an all-default trim stores nothing");
            buf = (Rgb[])probe.Clone();
            Calibration.Apply("Some Keyboard", buf);
            t.Check(Same(probe, buf), "an explicitly-default device is still a no-op");

            // Master.Finish is what every write site now calls. With no trim
            // in play it must produce exactly what Master.Scale produced
            // before this feature existed, at every brightness, or the write
            // path changed under a thousand other assertions.
            //
            // Through a ZONED device, deliberately. The write path now
            // resolves a per-device plan out of the device's zones, and the
            // whole point of the short circuit is that an untrimmed device
            // never reaches any of that: if it did, this is where it would
            // show.
            var plain = new FakeZonedDevice
            {
                Name = "Some Keyboard",
                Zones2 = new[] { ("Keys", 100), ("Logo", 4) },
            };
            bool allMatch = true;
            foreach (double b in new[] { 1.0, 0.999, 0.75, 0.5, 0.37, 0.05 })
            {
                Master.Brightness = b;
                var viaScale = (Rgb[])probe.Clone();
                var viaFinish = (Rgb[])probe.Clone();
                Master.Scale(viaScale);
                Master.Finish(plain, viaFinish);
                if (!Same(viaScale, viaFinish)) allMatch = false;
            }
            Master.Brightness = 1.0;
            t.Check(allMatch, "Master.Finish == Master.Scale for an uncalibrated device at every brightness");

            // And through the slice form, where the buffer starts part way
            // into the device: a plan consulted at all here would be consulted
            // with a non-zero offset, which is the shape most likely to be got
            // wrong.
            var untouched = (Rgb[])probe.Clone();
            Master.Finish(plain, untouched, 0, untouched.Length, 37);
            t.Check(Same(probe, untouched), "a slice at a device offset is untouched on an uncalibrated device");

            // And the identity TABLE itself, in case a future change ever
            // stores one: byte in, same byte out, for all 256 levels.
            var identity = new DeviceCalibration();
            bool tableIdentity = true;
            for (int v = 0; v < 256; v++)
                if (identity.MapR((byte)v) != v || identity.MapG((byte)v) != v || identity.MapB((byte)v) != v)
                    tableIdentity = false;
            t.Check(tableIdentity, "the identity transform maps every one of the 256 levels to itself");
            t.Check(identity.IsIdentity, "a default DeviceCalibration reports IsIdentity");

            Calibration.ResetAll();
        }

        /*-------------------------------------------------*\
        | 2. Gain: white balance, and its clamping.          |
        \*-------------------------------------------------*/
        t.Section("gain");
        {
            var gained = new DeviceCalibration { GainR = 0.5, GainG = 1.0, GainB = 2.0 };
            t.Equal((byte)100, gained.MapR(200), "gain 0.5 halves");
            t.Equal((byte)200, gained.MapG(200), "gain 1.0 is untouched");
            t.Equal((byte)255, gained.MapB(200), "gain 2.0 clips at the top instead of wrapping");

            // The wrap is the failure that matters: an unchecked (byte) cast of
            // 256.4 is 0, so a blown-out white LED would go BLACK.
            bool everWrapped = false;
            var hot = new DeviceCalibration { GainR = 2.0, GainG = 2.0, GainB = 2.0 };
            for (int v = 0; v < 256; v++)
                if (hot.MapR((byte)v) < (v > 127 ? 200 : 0)) everWrapped = true;
            t.Check(!everWrapped, "a gain of 2.0 never wraps a bright value round to black");
            t.Equal((byte)255, hot.MapR(255), "gain 2.0 on full white stays full white");

            var dark = new DeviceCalibration { GainR = 0.0, GainG = 0.0, GainB = 0.0 };
            bool allBlack = true;
            for (int v = 0; v < 256; v++) if (dark.MapR((byte)v) != 0) allBlack = false;
            t.Check(allBlack, "a gain of zero blacks the channel and nothing else");

            // Out-of-range values from a hand-edited file or a bad binding.
            var wild = new DeviceCalibration { GainR = 99, GainG = -5, GainB = double.NaN };
            t.Check(wild.Normalize(), "Normalize reports that it had to change something");
            t.Equal(DeviceCalibration.MaxGain, wild.GainR, "gain clamps to the maximum");
            t.Equal(DeviceCalibration.MinGain, wild.GainG, "gain clamps to the minimum");
            t.Equal(1.0, wild.GainB, "a NaN gain falls back to 1.0 rather than clamping to NaN");
        }

        /*-------------------------------------------------*\
        | 3. The cap: the GPU-logo control.                  |
        \*-------------------------------------------------*/
        t.Section("brightness cap");
        {
            var capped = new DeviceCalibration { MaxBrightness = 0.5 };
            t.Equal((byte)128, capped.MapR(255), "a 50% cap holds full white at 128");
            t.Equal((byte)64, capped.MapR(64), "a cap does not touch values already under it");

            // A cap is a ceiling on the FINAL value, so no gain may lift a
            // value past it. This is why the cap is applied last.
            var cappedAndHot = new DeviceCalibration { GainR = 2.0, MaxBrightness = 0.5 };
            bool overCap = false;
            for (int v = 0; v < 256; v++) if (cappedAndHot.MapR((byte)v) > 128) overCap = true;
            t.Check(!overCap, "no value escapes the cap, even with a gain of 2.0 under it");

            // 0.999 is a no-op in bytes, and IsIdentity is allowed to say so.
            t.Check(new DeviceCalibration { MaxBrightness = 0.999 }.IsIdentity, "a cap of 0.999 is identity in bytes");
            t.Check(!new DeviceCalibration { MaxBrightness = 0.99 }.IsIdentity, "a cap of 0.99 is NOT identity");

            var wild = new DeviceCalibration { MaxBrightness = 7.5 };
            wild.Normalize();
            t.Equal(DeviceCalibration.MaxCap, wild.MaxBrightness, "a cap above 1.0 clamps to 1.0");
            var floored = new DeviceCalibration { MaxBrightness = 0.0 };
            floored.Normalize();
            t.Equal(DeviceCalibration.MinCap, floored.MaxBrightness, "a cap of zero clamps to the floor, so a trimmed device never looks dead");
        }

        /*-------------------------------------------------*\
        | 4. Gamma.                                          |
        \*-------------------------------------------------*/
        t.Section("gamma");
        {
            var g = new DeviceCalibration { Gamma = 2.0 };
            t.Equal((byte)0, g.MapR(0), "gamma leaves black alone");
            t.Equal((byte)255, g.MapR(255), "gamma leaves full scale alone");
            t.Check(g.MapR(128) < 128, "gamma above 1 darkens the midpoint");
            t.Check(new DeviceCalibration { Gamma = 0.5 }.MapR(128) > 128, "gamma below 1 lightens the midpoint");

            // Encode then decode. Only the top two thirds of the range: below
            // that the encoded value has so few codes left that the round trip
            // is limited by 8-bit quantisation rather than by the maths, which
            // is a property of bytes and not a bug in this file.
            var enc = new DeviceCalibration { Gamma = 2.2 };
            var dec = new DeviceCalibration { Gamma = 1.0 / 2.2 };
            int worst = 0;
            for (int v = 64; v < 256; v++)
            {
                int back = dec.MapR(enc.MapR((byte)v));
                worst = Math.Max(worst, Math.Abs(back - v));
            }
            t.Check(worst <= 4, $"gamma 2.2 then 1/2.2 round-trips within 4 codes above 25% (worst {worst})");

            var wild = new DeviceCalibration { Gamma = double.PositiveInfinity };
            wild.Normalize();
            t.Equal(1.0, wild.Gamma, "an infinite gamma falls back to 1.0");
            var steep = new DeviceCalibration { Gamma = 50 };
            steep.Normalize();
            t.Equal(DeviceCalibration.MaxGamma, steep.Gamma, "gamma clamps to its maximum");
        }

        /*-------------------------------------------------*\
        | 5. Order of operations.                            |
        \*-------------------------------------------------*/
        t.Section("order of operations");
        {
            // gamma -> gain -> cap, and calibration before master brightness.
            // Both of these are checked with a case where the WRONG order
            // gives a visibly different number, not merely a rounding drift.

            // If gain ran before gamma, a 0.5 gain under a gamma of 2.0 would
            // come out as 0.5^2 = 0.25 of full scale rather than 0.5 of it.
            var gg = new DeviceCalibration { Gamma = 2.0, GainR = 0.5 };
            t.Equal((byte)128, gg.MapR(255), "gain is applied AFTER gamma (255 -> 128, not 64)");

            // If the cap ran before the gain, a gain of 2.0 would lift the
            // capped value straight back out of the cap.
            var cg = new DeviceCalibration { GainR = 2.0, MaxBrightness = 0.5 };
            t.Equal((byte)128, cg.MapR(255), "the cap is applied AFTER the gain (255 -> 128, not 255)");

            // Calibration before master brightness. Reversed, a gamma of 2.0
            // would act on the already-dimmed value and give 64.
            var ordered = new FakeZonedDevice { Name = "Ordered Device", Leds = 1 };
            Calibration.Set("Ordered Device", new DeviceCalibration { Gamma = 2.0 });
            Master.Brightness = 0.5;
            var one = new[] { new Rgb(255, 255, 255) };
            Master.Finish(ordered, one);
            t.Equal((byte)128, one[0].R, "Master.Finish trims first and dims second (255 -> 128, not 64)");
            Master.Brightness = 1.0;
            Calibration.ResetAll();
        }

        /*-------------------------------------------------*\
        | 6. The lookup table matches the direct maths.      |
        \*-------------------------------------------------*/
        t.Section("lookup table matches a direct computation");
        {
            // Deliberately asymmetric per channel: a table built or indexed
            // with the wrong 256-byte offset still produces perfect greys, so
            // the three channels have to disagree for this to catch anything.
            var cal = new DeviceCalibration { GainR = 0.80, GainG = 1.00, GainB = 1.15, Gamma = 1.35, MaxBrightness = 0.90 };
            Calibration.Set("Table Device", cal);

            var buf = new Rgb[256];
            for (int v = 0; v < 256; v++) buf[v] = new Rgb((byte)v, (byte)v, (byte)v);
            Calibration.Apply("Table Device", buf);

            bool match = true;
            for (int v = 0; v < 256; v++)
            {
                var want = new Rgb(cal.MapR((byte)v), cal.MapG((byte)v), cal.MapB((byte)v));
                if (buf[v] != want) match = false;
            }
            t.Check(match, "the table reproduces Map() exactly for all 256 levels on all three channels");

            // Each channel through its OWN table, on a color where the three
            // inputs differ.
            var mixed = new[] { new Rgb(10, 200, 90) };
            Calibration.Apply("Table Device", mixed);
            t.Equal(new Rgb(cal.MapR(10), cal.MapG(200), cal.MapB(90)), mixed[0], "each channel uses its own gain");

            // The single-color overload is the same transform as the buffer one.
            t.Equal(mixed[0], Calibration.Apply("Table Device", new Rgb(10, 200, 90)), "the single-color overload agrees with the buffer form");

            t.Check(Calibration.AnyCalibrated, "a real trim publishes tables");
            Calibration.ResetAll();
        }

        /*-------------------------------------------------*\
        | 7. Ranges, black, and unknown devices.             |
        \*-------------------------------------------------*/
        t.Section("range form and unknown devices");
        {
            Calibration.Set("Ranged", new DeviceCalibration { MaxBrightness = 0.5 });

            var buf = new[] { new Rgb(255, 255, 255), new Rgb(255, 255, 255), new Rgb(255, 255, 255) };
            Calibration.Apply("Ranged", buf, 1, 1);
            t.Equal((byte)255, buf[0].R, "range apply leaves everything before the range alone");
            t.Equal((byte)128, buf[1].R, "range apply trims inside the range");
            t.Equal((byte)255, buf[2].R, "range apply leaves everything after the range alone");

            Calibration.Apply("Ranged", buf, 2, 99);          // count past the end
            t.Equal((byte)128, buf[2].R, "range apply clamps to the buffer");
            // A range lying entirely before the buffer transforms nothing. That
            // is the arithmetic Master.Scale has always used, and it is the
            // honest reading of the request: the caller named [-5, -4), which
            // does not intersect the buffer, so sliding it up to index 0 would
            // trim an element nobody asked for. It must not throw either.
            Calibration.Apply("Ranged", buf, -5, 1);
            t.Equal((byte)255, buf[0].R, "a range entirely before the buffer changes nothing, and does not throw");

            // A negative offset that DOES overlap trims only the overlap: this
            // one covers index 0 and stops there.
            Calibration.Apply("Ranged", buf, -1, 2);
            t.Equal((byte)128, buf[0].R, "a negative offset trims the part of the range inside the buffer");
            t.Equal((byte)128, buf[1].R, "and leaves the already-trimmed rest of the buffer as it was");

            // An unknown device is untouched even while another device is
            // trimmed - the per-device lookup has to actually miss.
            var other = new[] { new Rgb(255, 255, 255) };
            Calibration.Apply("Never Calibrated", other);
            t.Equal(new Rgb(255, 255, 255), other[0], "an unknown device gets defaults while another device is trimmed");
            t.Check(Calibration.For("Never Calibrated").IsIdentity, "For() on an unknown device returns defaults, not null");

            // Black is a fixed point of the whole transform, which is what lets
            // the lights-off path skip it entirely.
            Calibration.Set("Aggressive", new DeviceCalibration { GainR = 2.0, GainG = 0.1, GainB = 1.7, Gamma = 0.3, MaxBrightness = 0.4 });
            var black = new[] { Rgb.Black, Rgb.Black };
            Calibration.Apply("Aggressive", black);
            t.Check(black[0] == Rgb.Black && black[1] == Rgb.Black, "black stays black under any trim (the lights-off path depends on it)");

            Calibration.ResetAll();
        }

        /*-------------------------------------------------*\
        | 8. The settings API.                               |
        \*-------------------------------------------------*/
        t.Section("settings API");
        {
            Calibration.ResetAll();
            int v0 = Calibration.Version;
            Calibration.Set("Bumper", new DeviceCalibration { GainB = 0.8 });
            t.Check(Calibration.Version != v0, "a change bumps Version so the engine's cached base restages");

            // For() must hand back a COPY: a slider mutates what it is given,
            // and a live object would apply half-finished drags through a table
            // that had not been rebuilt for them.
            var got = Calibration.For("Bumper");
            got.GainB = 0.1;
            t.Equal(0.8, Calibration.For("Bumper").GainB, "For() returns a copy, so mutating it does not change the live trim");

            t.Check(Calibration.CalibratedNames.Contains("Bumper"), "CalibratedNames lists a trimmed device");
            t.Check(Calibration.Fingerprint("Bumper").Length > 0, "a trimmed device has a fingerprint");
            t.Equal("", Calibration.Fingerprint("Never Calibrated"), "an untrimmed device has an empty fingerprint");

            // Device names are matched the forgiving way, because the only
            // thing that ever produces them by hand is the JSON file.
            var byOtherCase = new[] { new Rgb(0, 0, 255) };
            Calibration.Apply("BUMPER", byOtherCase);
            t.Check(byOtherCase[0].B != 255, "device names match case-insensitively");

            // Set() must clamp: the UI cannot install a value the maths would
            // choke on.
            Calibration.Set("Clamped", new DeviceCalibration { Gamma = double.NaN, GainR = 900, MaxBrightness = -3 });
            var c = Calibration.For("Clamped");
            t.Equal(1.0, c.Gamma, "Set() replaces a NaN gamma with 1.0");
            t.Equal(DeviceCalibration.MaxGain, c.GainR, "Set() clamps gain");
            t.Equal(DeviceCalibration.MinCap, c.MaxBrightness, "Set() clamps the cap");

            // Dragging a slider back to default really is uncalibrated again.
            Calibration.Reset("Bumper");
            Calibration.Reset("Clamped");
            t.Check(!Calibration.AnyCalibrated, "resetting the last trimmed device drops the tables entirely");

            Calibration.ResetAll();
        }

        /*-------------------------------------------------*\
        | 9. Per-zone trim, and the override rule.           |
        \*-------------------------------------------------*/
        t.Section("per-zone trim");
        {
            Calibration.ResetAll();

            // Modelled on the board this feature exists for: one device object
            // under one name, carrying a bright multi-LED ribbon strip and two
            // single-purpose accents that look nothing like it.
            var board = new FakeZonedDevice
            {
                Name = "Zoned Board",
                Zones2 = new[] { ("Ribbon", 4), ("Chipset", 2), ("Cover", 2) },
            };
            // Ribbon is LEDs 0-3, Chipset 4-5, Cover 6-7.

            Calibration.SetZone("Zoned Board", "Ribbon", new DeviceCalibration { MaxBrightness = 0.5 });
            t.Check(Calibration.AnyCalibrated, "a zone trim on its own publishes tables");

            var buf = Flat();
            Master.Finish(board, buf);
            t.Equal(new Rgb(128, 128, 128), buf[0], "a zone trim reaches the first LED of its zone");
            t.Equal(new Rgb(128, 128, 128), buf[3], "a zone trim reaches the last LED of its zone");
            bool neighbours = true;
            for (int i = 4; i < 8; i++) if (buf[i] != Base) neighbours = false;
            t.Check(neighbours, "every LED outside the trimmed zone is left byte-identical");

            // The fallback. A device entry still means "everything on this
            // device", and it reaches the zones that have no entry of their
            // own.
            Calibration.Set("Zoned Board", new DeviceCalibration { GainG = 0.5 });
            buf = Flat();
            Master.Finish(board, buf);
            t.Equal(new Rgb(200, 100, 200), buf[4], "a device trim reaches a zone with no entry of its own");
            t.Equal(new Rgb(200, 100, 200), buf[7], "and the zone after that one as well");

            // THE RULE. A zone entry replaces the device entry for its own
            // LEDs. Composition would give 100 in green here (the device's
            // gain, then the zone's cap, which does not bite on 100); the
            // override gives 128, the zone's cap and nothing else.
            t.Equal(new Rgb(128, 128, 128), buf[0], "a zone entry REPLACES the device entry over its own LEDs");
            t.Check(buf[0].G == 128, "the override does not compose: the device's green gain does not also apply inside the trimmed zone (128, not 100)");

            // Names are matched per device. Two boards can both call a zone
            // "Ribbon" and they are not the same setting.
            var otherBoard = new FakeZonedDevice
            {
                Name = "Another Board",
                Zones2 = new[] { ("Ribbon", 4), ("Chipset", 4) },
            };
            var other = Flat();
            Master.Finish(otherBoard, other);
            t.Check(Same(other, Flat()), "a zone trim does not leak onto a different device with a zone of the same name");

            // Dragging a zone's sliders back to default drops its entry, so it
            // follows the device again. See the override rule in Calibration.cs
            // for why default means follow rather than "explicitly neutral".
            Calibration.SetZone("Zoned Board", "Ribbon", new DeviceCalibration());
            t.Check(!Calibration.HasZoneTrim("Zoned Board", "Ribbon"), "a zone dragged back to default keeps no entry of its own");
            buf = Flat();
            Master.Finish(board, buf);
            t.Equal(new Rgb(200, 100, 200), buf[0], "and that zone follows the device trim again");

            Calibration.SetZone("Zoned Board", "Ribbon", new DeviceCalibration { MaxBrightness = 0.5 });

            // What the editor reads. "Has its own" and "shows the same numbers
            // as its device" are different states, and only one of them
            // survives a change to the device trim.
            t.Check(Calibration.ForZone("Zoned Board", "Chipset") == null, "a following zone reports no trim of its own, rather than defaults");
            t.Equal(0.5, Calibration.ForZone("Zoned Board", "Ribbon")!.MaxBrightness, "ForZone returns the zone's own trim");
            t.Equal(0.5, Calibration.Effective("Zoned Board", "Ribbon").MaxBrightness, "Effective on a trimmed zone is the zone's own trim");
            t.Equal(0.5, Calibration.Effective("Zoned Board", "Chipset").GainG, "Effective on a following zone is the DEVICE's trim, which is what its sliders must open on");
            t.Equal(1.0, Calibration.Effective("Zoned Board", "Chipset").MaxBrightness, "and it is that trim whole, not a mixture of the two");
            t.Check(Calibration.CalibratedZones("Zoned Board").Contains("Ribbon"), "CalibratedZones lists a trimmed zone");
            t.Check(Calibration.HasZoneTrim("ZONED BOARD", "ribbon"), "zone lookups are case-insensitive, like device lookups");
            t.Check(Calibration.Fingerprint("Zoned Board").Contains("Ribbon"), "the bake signature notices a zone trim, not just a device one");

            // A stored trim for a zone the device does not have. Renamed zones
            // and swapped boards both produce this, and the wrong answer would
            // be to trim some arbitrary range instead.
            Calibration.SetZone("Zoned Board", "No Such Zone", new DeviceCalibration { GainR = 0.0 });
            buf = Flat();
            Master.Finish(board, buf);
            bool noPhantom = true;
            for (int i = 0; i < buf.Length; i++) if (buf[i].R == 0) noPhantom = false;
            t.Check(noPhantom, "a stored trim for a zone this device does not have trims nothing at all");
            Calibration.ResetZone("Zoned Board", "No Such Zone");

            // The cached plan is keyed by device INSTANCE and stamped with the
            // calibration version. Same instance, changed trim: the next frame
            // has to show the new one.
            Calibration.SetZone("Zoned Board", "Ribbon", new DeviceCalibration { MaxBrightness = 0.25 });
            buf = Flat();
            Master.Finish(board, buf);
            t.Equal((byte)64, buf[0].R, "changing a zone trim restages the cached plan for the same device instance");

            // Master brightness still runs last, over both levels.
            Calibration.SetZone("Zoned Board", "Ribbon", new DeviceCalibration { MaxBrightness = 0.5 });
            Master.Brightness = 0.5;
            buf = Flat();
            Master.Finish(board, buf);
            t.Equal((byte)64, buf[0].R, "the master dimmer runs AFTER a zone trim (128 -> 64)");
            t.Equal((byte)100, buf[4].R, "and after the device trim on the zones that follow it");
            Master.Brightness = 1.0;

            // "Reset this device" has to mean the whole device. A surviving
            // zone override would leave part of it trimmed by a control the
            // user had just cleared.
            Calibration.Reset("Zoned Board");
            t.Check(!Calibration.AnyCalibrated, "resetting a device drops its zone trims too, so the short circuit comes back");
            buf = Flat();
            Master.Finish(board, buf);
            t.Check(Same(buf, Flat()), "and the device is a byte-exact no-op once more");

            Calibration.ResetAll();
        }

        /*-------------------------------------------------*\
        | 10. Where in the device a buffer sits.             |
        \*-------------------------------------------------*/
        t.Section("a slice knows where in the device it sits");
        {
            Calibration.ResetAll();
            var board = new FakeZonedDevice
            {
                Name = "Slice Board",
                Zones2 = new[] { ("Ribbon", 4), ("Chipset", 2), ("Cover", 2) },
            };
            Calibration.SetZone("Slice Board", "Ribbon", new DeviceCalibration { MaxBrightness = 0.5 });
            Calibration.Set("Slice Board", new DeviceCalibration { GainG = 0.5 });

            // THE PushZone SHAPE, and the reason Finish takes a device offset
            // at all. The caller copies ONE zone out of the stored frame, so
            // the slice's own index 0 is device LED 4. Without being told
            // that, the write path would read the plan from the top of the
            // device and trim these two LEDs as if they were the ribbon.
            var chipset = new[] { Base, Base };
            Master.Finish(board, chipset, 4);
            t.Equal(new Rgb(200, 100, 200), chipset[0], "a slice at device offset 4 takes the trim that governs LED 4, not LED 0");
            t.Equal(new Rgb(200, 100, 200), chipset[1], "and so does the rest of it");

            var ribbon = new[] { Base, Base };
            Master.Finish(board, ribbon, 0);
            t.Equal(new Rgb(128, 128, 128), ribbon[0], "a slice at device offset 0 takes the zone that starts there");

            // A slice straddling a zone boundary: device LEDs 2 to 5, so two
            // under the ribbon's own trim and two under the device's.
            var straddle = new[] { Base, Base, Base, Base };
            Master.Finish(board, straddle, 2);
            t.Equal(new Rgb(128, 128, 128), straddle[0], "the part of a straddling slice inside the zone takes the zone's trim");
            t.Equal(new Rgb(128, 128, 128), straddle[1], "all of that part, not just its first LED");
            t.Equal(new Rgb(200, 100, 200), straddle[2], "and the part outside it falls back to the device's");
            t.Equal(new Rgb(200, 100, 200), straddle[3], "all of that part too");

            // The engine's compose shape: a RANGE of the full device frame,
            // where the buffer offset and the device offset are the same
            // number.
            var full = Flat();
            Master.Finish(board, full, 3, 2, 3);
            t.Equal(new Rgb(128, 128, 128), full[3], "a range of the full frame trims LED 3 as part of the ribbon");
            t.Equal(new Rgb(200, 100, 200), full[4], "and LED 4 under the device trim");
            t.Equal(Base, full[2], "and leaves the LED before the range byte-identical");
            t.Equal(Base, full[5], "and the LED after it");

            // Exactly ONE transform per pixel. A plan walk whose segments
            // overlapped would square the gamma, and a squared gamma is not
            // obviously broken on screen: it is merely the wrong color, which
            // is the most expensive kind of wrong there is.
            Calibration.ResetAll();
            Calibration.SetZone("Slice Board", "Ribbon", new DeviceCalibration { Gamma = 2.0 });
            var once = new Rgb[8];
            for (int i = 0; i < once.Length; i++) once[i] = new Rgb(128, 128, 128);
            Master.Finish(board, once, 0, once.Length, 0);
            byte wanted = new DeviceCalibration { Gamma = 2.0 }.MapR(128);
            t.Equal(wanted, once[0].R, "a pixel inside a trimmed zone is transformed exactly once");
            t.Equal((byte)128, once[4].R, "and a pixel outside every trimmed zone is not transformed at all");

            Calibration.ResetAll();
        }

        /*-------------------------------------------------*\
        | 11. calibration.json.                              |
        \*-------------------------------------------------*/
        t.Section("persistence");
        {
            Calibration.ResetAll();
            foreach (string f in CalibrationFiles()) TryDelete(f);

            Calibration.Set("Saved Fans", new DeviceCalibration { GainR = 0.9, GainG = 0.95, GainB = 1.1, Gamma = 1.2, MaxBrightness = 0.7 });
            Calibration.Set("Saved GPU", new DeviceCalibration { MaxBrightness = 0.35 });
            Calibration.Save();
            t.Check(File.Exists(PathName), "Save() writes calibration.json");

            Calibration.ResetAll();
            t.Check(!Calibration.AnyCalibrated, "ResetAll clears the live trims");
            Calibration.Reload();

            var fans = Calibration.For("Saved Fans");
            t.Equal(0.9, fans.GainR, "gain survives a save/load round trip");
            t.Equal(1.2, fans.Gamma, "gamma survives a save/load round trip");
            t.Equal(0.35, Calibration.For("Saved GPU").MaxBrightness, "the cap survives a save/load round trip");
            t.Equal(2, Calibration.CalibratedNames.Count, "both devices came back");

            /*--- Zones in the file. ---*/
            Calibration.SetZone("Saved Fans", "Fan 3 (Header 4)", new DeviceCalibration { MaxBrightness = 0.4 });
            Calibration.Save();
            string written = File.ReadAllText(PathName);
            // The key format is pinned here on purpose: it is what a
            // hand-edited file has to match and what an older build has to be
            // able to skip.
            t.Check(written.Contains("\"Zones\""), "zones are written under their own top-level object, additively");
            t.Check(written.Contains("\"Saved Fans\"") && written.Contains("\"Fan 3 (Header 4)\""),
                    "the zone key is the plain zone name nested under the plain device name");

            Calibration.ResetAll();
            Calibration.Reload();
            t.Equal(0.4, Calibration.ForZone("Saved Fans", "Fan 3 (Header 4)")!.MaxBrightness, "a zone trim survives a save/load round trip");
            t.Equal(0.9, Calibration.For("Saved Fans").GainR, "and the device trim beside it is unchanged");
            t.Check(Calibration.ForZone("Saved GPU", "Fan 3 (Header 4)") == null, "a zone trim is not shared between devices by the file either");

            // The migration that matters: a file written before zones existed
            // has no Zones object at all, and must load meaning exactly what it
            // always meant - the whole device, every zone included.
            Calibration.ResetAll();
            foreach (string f in CalibrationFiles()) TryDelete(f);
            File.WriteAllText(PathName, "{ \"Devices\": { \"Old Board\": { \"MaxBrightness\": 0.5 } } }");
            Calibration.Reload();
            t.Equal(0.5, Calibration.For("Old Board").MaxBrightness, "a device-only calibration.json still loads");
            t.Equal(0, Calibration.CalibratedZones("Old Board").Count, "an old file brings no zone entries with it");
            var oldBoard = new FakeZonedDevice
            {
                Name = "Old Board",
                Zones2 = new[] { ("Ribbon", 4), ("Chipset", 2), ("Cover", 2) },
            };
            var oldFrame = Flat();
            Master.Finish(oldBoard, oldFrame);
            bool wholeDevice = true;
            for (int i = 0; i < oldFrame.Length; i++) if (oldFrame[i] != new Rgb(128, 128, 128)) wholeDevice = false;
            t.Check(wholeDevice, "an old device-only entry still trims every zone of the device, exactly as it did before zones existed");

            // The file is per-machine hardware trimming and is NOT written on
            // first run: its existence is the signal that this desk has been
            // calibrated at all.
            Calibration.ResetAll();
            foreach (string f in CalibrationFiles()) TryDelete(f);
            Calibration.Reload();
            t.Check(!File.Exists(PathName), "a missing calibration.json is not regenerated on load");
            t.Check(!Calibration.AnyCalibrated, "no file means no trim");
        }

        t.Section("a corrupt calibration.json falls back to defaults");
        {
            // The single most important behaviour of the loader: this runs
            // from a static constructor on whatever thread first writes to a
            // device, so a throw here would surface as a
            // TypeInitializationException inside a driver.
            foreach (string junk in new[]
            {
                "{ this is not json at all",
                "null",
                "[]",
                "{ \"Devices\": null }",
                "{ \"Devices\": { \"Ghost\": null } }",
                "{ \"Devices\": { \"\": { \"Gamma\": 2.0 } } }",
                "{ \"Devices\": { \"Wild\": { \"Gamma\": \"banana\" } } }",
                // And the same hand-edits one level down. A missing or null
                // Zones is NOT corrupt, it is simply a file from before zones
                // existed, so those two fall back quietly rather than being
                // copied aside.
                "{ \"Devices\": { }, \"Zones\": null }",
                "{ \"Zones\": [] }",
                "{ \"Zones\": { \"Board\": null } }",
                "{ \"Zones\": { \"Board\": { \"Ribbon\": null } } }",
                "{ \"Zones\": { \"Board\": { \"\": { \"Gamma\": 2.0 } } } }",
                "{ \"Zones\": { \"\": { \"Ribbon\": { \"Gamma\": 2.0 } } } }",
                "{ \"Zones\": { \"Board\": { \"Ribbon\": { \"Gamma\": \"banana\" } } } }",
            })
            {
                Calibration.ResetAll();
                bool threw = false;
                try
                {
                    File.WriteAllText(PathName, junk);
                    Calibration.Reload();
                }
                catch { threw = true; }
                t.Check(!threw, $"a corrupt calibration.json does not throw: {junk}");
                t.Check(!Calibration.AnyCalibrated, $"a corrupt calibration.json falls back to no trim: {junk}");

                // And the write path is still a perfect no-op afterwards,
                // which is the outcome that actually matters to the user.
                var probe = new[] { new Rgb(200, 100, 50) };
                Calibration.Apply("Ghost", probe);
                t.Equal(new Rgb(200, 100, 50), probe[0], "the write path is untouched after a corrupt file");
            }

            // Out-of-range values are clamped rather than rejected: a file with
            // one silly number in it still describes a desk somebody trimmed.
            Calibration.ResetAll();
            File.WriteAllText(PathName, "{ \"Devices\": { \"Loud\": { \"Gamma\": 99, \"GainR\": -1, \"MaxBrightness\": 12 } } }");
            Calibration.Reload();
            var loud = Calibration.For("Loud");
            t.Equal(DeviceCalibration.MaxGamma, loud.Gamma, "a hand-edited gamma is clamped, not discarded");
            t.Equal(DeviceCalibration.MinGain, loud.GainR, "a hand-edited gain is clamped, not discarded");
            t.Equal(DeviceCalibration.MaxCap, loud.MaxBrightness, "a hand-edited cap is clamped, not discarded");

            // Zone entries are clamped the same forgiving way, one level down.
            Calibration.ResetAll();
            File.WriteAllText(PathName, "{ \"Zones\": { \"Loud Board\": { \"Ribbon\": { \"Gamma\": 99, \"MaxBrightness\": 12 } } } }");
            Calibration.Reload();
            var loudZone = Calibration.ForZone("Loud Board", "Ribbon");
            t.Check(loudZone != null, "a hand-written zone entry loads");
            t.Equal(DeviceCalibration.MaxGamma, loudZone!.Gamma, "a hand-edited zone gamma is clamped, not discarded");

            // A file that says "everything is 1.0" leaves the short circuit
            // intact rather than paying for a table that does nothing.
            Calibration.ResetAll();
            File.WriteAllText(PathName, "{ \"Devices\": { \"Untouched\": { \"Gamma\": 1.0, \"GainR\": 1.0, \"GainG\": 1.0, \"GainB\": 1.0, \"MaxBrightness\": 1.0 } } }");
            Calibration.Reload();
            t.Check(!Calibration.AnyCalibrated, "an all-default entry in the file does not switch the short circuit off");

            // And the same for a zone entry, which is what an old build would
            // leave behind after it dropped a trim it could not understand.
            Calibration.ResetAll();
            File.WriteAllText(PathName, "{ \"Zones\": { \"Untouched\": { \"Ribbon\": { \"Gamma\": 1.0, \"MaxBrightness\": 1.0 } } } }");
            Calibration.Reload();
            t.Check(!Calibration.AnyCalibrated, "an all-default ZONE entry does not switch the short circuit off either");

            Calibration.ResetAll();
            foreach (string f in CalibrationFiles()) TryDelete(f);
            Calibration.Reload();
        }

        /*-------------------------------------------------*\
        | 12. The calibration aid's reference patches.      |
        \*-------------------------------------------------*/
        t.Section("calibration aid references");
        {
            // White is NOT full scale, on purpose. Every LED at maximum on
            // every device is the heaviest draw this app can ask for, and the
            // calibration screen is where a user sits on it for minutes. It is
            // also the wrong test signal: gain above 1.0 clips at 255 and gamma
            // cannot move a full-scale value at all, so both controls would
            // appear dead on the one patch people reach for first.
            t.Equal(new Rgb(153, 153, 153), CalibrationReferences.ColorOf(CalibrationReference.White), "white is 60%, not full scale");
            t.Check(CalibrationReferences.LabelOf(CalibrationReference.White).Contains("60%"), "the white pill says it is 60% rather than hiding it");
            t.Equal(new Rgb(128, 128, 128), CalibrationReferences.ColorOf(CalibrationReference.MidGrey), "mid grey is 128 in device units");
            t.Check(CalibrationReferences.Cycle[0] == CalibrationReference.White, "the cycle starts on white, where the mismatch is most obvious");
            t.Check(CalibrationReferences.Cycle.Contains(CalibrationReference.MidGrey), "the cycle includes mid grey, the only patch that shows a gamma error");

            var seen = new HashSet<CalibrationReference>();
            var at = CalibrationReferences.Cycle[0];
            for (int i = 0; i < CalibrationReferences.Cycle.Length; i++) { seen.Add(at); at = CalibrationReferences.Next(at); }
            t.Equal(CalibrationReferences.Cycle.Length, seen.Count, "Next() visits every patch");
            t.Check(at == CalibrationReferences.Cycle[0], "Next() wraps back to the start");

            // Every patch is a color the aid can actually compare with: a
            // patch that came out black on a default install would be useless.
            bool allLit = true;
            foreach (var r in CalibrationReferences.Cycle)
            {
                var c = CalibrationReferences.ColorOf(r);
                if (c.R == 0 && c.G == 0 && c.B == 0) allLit = false;
                if (CalibrationReferences.LabelOf(r).Length == 0) allLit = false;
            }
            t.Check(allLit, "every reference patch is lit and labelled");

            /*--- Which slider does anything for which patch. This is the rule
                  the window dims by, and it exists because the obvious first
                  experiment (pick blue, drag red) moves a control that cannot
                  possibly change the output, which reads as a broken app. ---*/
            var blue = CalibrationReference.Blue;
            t.Check(!CalibrationReferences.Affects(blue, CalibrationControl.GainR), "red gain does nothing on a blue patch");
            t.Check(!CalibrationReferences.Affects(blue, CalibrationControl.GainG), "green gain does nothing on a blue patch");
            t.Check(CalibrationReferences.Affects(blue, CalibrationControl.GainB), "blue gain does work on a blue patch");

            // Gamma pins both ends of the curve: 0^g is 0 and 1^g is 1. A
            // saturated primary is made only of those two values.
            t.Check(!CalibrationReferences.Affects(blue, CalibrationControl.Gamma), "gamma does nothing on a saturated primary");
            t.Check(CalibrationReferences.Affects(CalibrationReference.MidGrey, CalibrationControl.Gamma), "gamma works on mid grey");
            t.Check(CalibrationReferences.Affects(CalibrationReference.QuarterGrey, CalibrationControl.Gamma), "gamma works on dark grey");

            // And the payoff of dropping white to 60%: it is now a midtone, so
            // the two controls that were dead at full scale both come alive.
            t.Check(CalibrationReferences.Affects(CalibrationReference.White, CalibrationControl.Gamma), "gamma works on the 60% white patch");
            foreach (var c in new[] { CalibrationControl.GainR, CalibrationControl.GainG, CalibrationControl.GainB })
                t.Check(CalibrationReferences.Affects(CalibrationReference.White, c), $"{c} works on the white patch, which is what white is for");

            // The cap is a ceiling, so it bites on anything that is lit.
            foreach (var r in CalibrationReferences.Cycle)
                t.Check(CalibrationReferences.Affects(r, CalibrationControl.Cap), $"the brightness cap applies on the {r} patch");

            // Every inert combination explains itself, and no working one does.
            bool notesAgree = true;
            foreach (var r in CalibrationReferences.Cycle)
                foreach (var c in new[] { CalibrationControl.GainR, CalibrationControl.GainG, CalibrationControl.GainB,
                                          CalibrationControl.Gamma, CalibrationControl.Cap })
                {
                    bool affects = CalibrationReferences.Affects(r, c);
                    bool explained = CalibrationReferences.InertBecause(r, c).Length > 0;
                    if (affects == explained) notesAgree = false;
                }
            t.Check(notesAgree, "a control that does nothing always says why, and one that works never does");

            // Every patch says what it is for, since picking one is the
            // question the user actually has at that moment.
            bool purposed = true;
            foreach (var r in CalibrationReferences.Cycle)
                if (CalibrationReferences.PurposeOf(r).Length == 0) purposed = false;
            t.Check(purposed, "every patch says what it is for");
        }

        /*-------------------------------------------------*\
        | 13. The no-op, one more time, at the very end.    |
        \*-------------------------------------------------*/
        t.Section("the write path is left exactly as it was found");
        {
            // Repeated deliberately. Everything above installed trims, wrote
            // files and reset them; this asserts that the suite hands the rest
            // of the harness a genuinely untrimmed global back, because if it
            // does not, the failures show up in unrelated suites.
            t.Check(!Calibration.AnyCalibrated, "the suite leaves no trim behind");
            var probe = Probe();
            var buf = (Rgb[])probe.Clone();
            Master.Brightness = 1.0;
            Master.Finish(new FakeZonedDevice { Name = "Anything At All", Zones2 = new[] { ("A", 2), ("B", 2) } }, buf);
            t.Check(Same(probe, buf), "Master.Finish at full brightness is the identity once more");
        }
    }
    sealed class NestedFan : IRgbDevice
    {
        public string Name => "Nested fan";
        public string Vendor => "Test";
        public DeviceType Type => DeviceType.Fan;
        public int LedCount => 20;
        public IReadOnlyList<RgbZone> Zones => new[] {
            new RgbZone { Name = "Fan 1", Offset = 0, Count = 20 },
            new RgbZone { Name = "Inner", Offset = 0, Count = 8 },
            new RgbZone { Name = "Outer", Offset = 8, Count = 12 } };
        public bool SetColors(IReadOnlyList<Rgb> colors) => true;
        public void Dispose() { }
    }
}

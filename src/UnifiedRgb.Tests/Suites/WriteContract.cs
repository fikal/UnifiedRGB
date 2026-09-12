using UnifiedRgb.App.Services;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The hardware-write contract itself, rather than any one      |
| driver's protocol.                                           |
|                                                              |
| Before SetColors returned a bool, nothing above a driver     |
| could tell a landed frame from a refused one. Every driver   |
| KNEW - they all stopped caching refused frames - but none of |
| them could say so, so the engine cached frames it had not    |
| delivered and a lights-off that never went out looked        |
| exactly like one that did.                                   |
|                                                              |
| What this suite pins:                                        |
|                                                              |
|  - the three verdicts, on real drivers over FakeHid: a       |
|    refused frame is false, an identical frame is true with   |
|    NO report on the wire, and the frame after a recovery is  |
|    really re-sent rather than deduped against something the  |
|    device never took;                                        |
|  - the must-land path: it bypasses dedup, retries a          |
|    transient refusal, and eventually gives up and says so    |
|    instead of returning a quiet success;                     |
|  - the lights-off route the user actually notices, end to    |
|    end through LightingController, with a device that        |
|    refuses the first attempts.                               |
|                                                              |
| The contract is in Devices/WritePolicy.cs and in             |
| ADDING_A_DEVICE.md; this is the executable half of it.       |
\*-----------------------------------------------------------*/
static class WriteContractSuite
{
    public static void Run(Harness t)
    {
        Verdicts(t);
        FrameHelpers(t);
        MustLand(t);
        MustLandBypassesDedup(t);
        MustLandThroughARealDriver(t);
        LightsOffIsGuaranteed(t);
        TerminalHealth(t);
        RecoveryMemory(t);
        SuppressedSdkRelease(t);
    }

    /*---------------- the three verdicts, on real drivers ----------------*/
    static void Verdicts(Harness t)
    {
        t.Section("a driver's verdict: landed, deduped, refused");
        {
            var hid = new FakeHid();
            var pad = new SayoDevice(hid, outLen: 64, name: "Sayo");
            var red = new Rgb(10, 20, 30);

            t.Check(pad.SetColors(new[] { red }), "a frame the device took reports true");
            t.Equal(1, hid.Writes.Count, "...and it really went out");

            // The dedup half. "Skipped because it is already showing this" is a
            // SUCCESS, not a failure to deliver: the device is in exactly the
            // state the caller asked for. It just costs nothing to say so.
            t.Check(pad.SetColors(new[] { red }), "an identical frame reports true");
            t.Equal(1, hid.Writes.Count, "...without putting a report on the wire");

            hid.Accept = (_, _) => false;
            var blue = new Rgb(1, 2, 3);
            t.Check(!pad.SetColors(new[] { blue }), "a refused frame reports false");
            t.Equal(2, hid.Writes.Count, "...having actually been attempted");

            // The bug this whole change is about: the refused frame must not be
            // sitting in the dedup cache, or the identical frame after it - the
            // engine's own keepalive included - is skipped and the pad stays on
            // the OLD color with a "success" recorded for the new one.
            hid.Accept = null;
            t.Check(pad.SetColors(new[] { blue }), "the same frame after recovery reports true");
            t.Equal(3, hid.Writes.Count, "...and was really re-sent, not deduped against a frame the device never took");
        }

        t.Section("the verdict on a feature-report driver");
        {
            // Same three answers on a different transport, so the contract is
            // pinned as a rule rather than as one driver's habit.
            var hid = new FakeHid();
            var kb = new SteelSeriesApex(hid, featureLen: 643, outputLen: 65, name: "Apex");
            var frame = new Rgb[kb.LedCount];
            frame[0] = new Rgb(1, 2, 3);
            int init = hid.Features.Count;

            t.Check(kb.SetColors(frame), "a frame the keyboard took reports true");
            t.Equal(init + 1, hid.Features.Count, "...as one feature report");
            t.Check(kb.SetColors(frame), "an identical frame reports true");
            t.Equal(init + 1, hid.Features.Count, "...with nothing on the wire");

            hid.AcceptFeature = (_, _) => false;
            frame[0] = new Rgb(9, 9, 9);
            t.Check(!kb.SetColors(frame), "a refused feature report reports false");
            hid.AcceptFeature = null;
            t.Check(kb.SetColors(frame), "the recovered frame reports true");
            t.Equal(init + 3, hid.Features.Count, "...and was re-sent rather than cached");
        }

        t.Section("a refused mode-init is retried, not forgotten");
        {
            // The nastiest way for a driver to lie. The init packet is what
            // takes a keyboard OFF its onboard profile; every color report
            // after it is accepted at the HID layer whether or not it landed.
            // So an init that is dropped on the floor leaves a keyboard showing
            // its own lighting while the driver reports true, the frame cache
            // fills, and device health reads Connected - forever, because
            // nothing ever tried again.
            // Retry at once, or these tests sleep out the init backoff. Same knob,
            // same reason as LogitechG403.RetryAfterFailMs; the backoff itself is
            // proved below and in the Devices suite.
            int apexBackoff = SteelSeriesApex.InitRetryAfterFailMs;
            int strafeBackoff = CorsairStrafeMk2.InitRetryAfterFailMs;
            SteelSeriesApex.InitRetryAfterFailMs = 0;
            CorsairStrafeMk2.InitRetryAfterFailMs = 0;
            var hid = new FakeHid { AcceptFeature = (_, _) => false };
            var kb = new SteelSeriesApex(hid, featureLen: 643, outputLen: 65, name: "Apex");
            t.Equal(1, hid.Features.Count, "the constructor attempts the direct-mode init");

            var frame = new Rgb[kb.LedCount];
            frame[0] = new Rgb(4, 5, 6);
            t.Check(!kb.SetColors(frame), "a frame sent while the init is still refused reports false");
            hid.AcceptFeature = null;
            t.Check(kb.SetColors(frame), "the frame lands once the keyboard answers again");
            t.Check(hid.Features.Count >= 4, "...and the init was re-sent rather than assumed done");

            // Corsair, on the report transport, has the same hazard and the
            // extra twist that it latches a re-init flag of its own.
            var chid = new FakeHid { Accept = (_, _) => false };
            var strafe = new CorsairStrafeMk2(chid);
            int afterCtor = chid.Writes.Count;
            t.Check(afterCtor > 1, "the constructor attempts the software-mode init");
            chid.Accept = null;
            var kbFrame = new Rgb[strafe.LedCount];
            kbFrame[0] = new Rgb(7, 8, 9);
            t.Check(strafe.SetColors(kbFrame), "the first frame after the keyboard answers again lands");
            t.Check(chid.Writes.Count > afterCtor + 3,
                "...and re-ran the init sequence rather than streaming color at a keyboard still in hardware mode");
            SteelSeriesApex.InitRetryAfterFailMs = apexBackoff;
            CorsairStrafeMk2.InitRetryAfterFailMs = strafeBackoff;
        }

        t.Section("a refused mode-init is PACED, not re-run on every frame");
        {
            // The other half of the same contract, and the reason this needs a
            // clock at all. The init verdict IS the frame verdict now, so a
            // refused init returns false - and the engine latches its
            // once-a-second keepalive only on a SUCCESSFUL write, so a driver
            // that keeps saying false is called back on EVERY frame, up to 60 Hz.
            // Both inits are expensive and both run under the engine's device
            // gate (Strafe: a blocking HID read plus 60 ms of sleeps), so without
            // pacing, a keyboard that iCUE or SteelSeries GG is holding pins its
            // whole channel on a hot loop for as long as that software runs.
            var hid = new FakeHid { AcceptFeature = (_, _) => false };
            using var kb = new SteelSeriesApex(hid, featureLen: 643, outputLen: 65, name: "Apex paced");
            var frame = new Rgb[kb.LedCount];
            t.Check(!kb.SetColors(frame), "the frame is refused while the init is refused");
            int afterFirst = hid.Features.Count;
            for (int i = 0; i < 20; i++) t.Check(!kb.SetColors(frame), "every frame in the window is still refused");
            t.Equal(afterFirst, hid.Features.Count, "no init was re-attempted inside the backoff window");

            // A caller that has decided the write MUST land is never made to
            // wait: InvalidateCache drops the clock with the frame cache, the
            // same rule LogitechG403 and RazerHid follow.
            kb.InvalidateCache();
            t.Check(!kb.SetColors(frame), "the frame is still refused - the keyboard has not answered");
            t.Check(hid.Features.Count > afterFirst, "...but InvalidateCache let the init be attempted again at once");
        }
    }

    /*---------------- the non-allocating frame helpers ----------------*/
    static void FrameHelpers(Harness t)
    {
        t.Section("WritePolicy frame helpers");
        var frame = new[] { Rgb.Red, Rgb.Green };

        // The contract's answer to a short frame: repeat the last color.
        // Dropping the frame and padding with black have both shipped, and
        // both looked like a dead device.
        t.Equal(Rgb.Red, WritePolicy.ColorAt(frame, 0), "ColorAt: in range");
        t.Equal(Rgb.Green, WritePolicy.ColorAt(frame, 1), "ColorAt: last in range");
        t.Equal(Rgb.Green, WritePolicy.ColorAt(frame, 7), "ColorAt: past the end repeats the last color");

        Rgb[]? last = null;
        t.Check(!WritePolicy.Unchanged(last, frame), "Unchanged: nothing cached is never a match");
        WritePolicy.Cache(ref last, frame);
        t.Check(WritePolicy.Unchanged(last, frame), "Unchanged: the cached frame matches itself");
        t.Check(!WritePolicy.Unchanged(last, new[] { Rgb.Red }), "Unchanged: a different length never matches");
        t.Check(!WritePolicy.Unchanged(last, new[] { Rgb.Red, Rgb.Blue }), "Unchanged: one different LED is enough");

        // Refused does both halves in one call, which is the point of it: the
        // cache is dropped AND the false comes back, so a driver cannot return
        // the failure while forgetting the invalidation.
        t.Check(!WritePolicy.Refused(ref last, "test:refused", "test", "a refusal returns false"),
            "Refused: returns false");
        t.Check(last == null, "Refused: and drops the cached frame");
    }

    /*---------------- the must-land path ----------------*/
    static void MustLand(Harness t)
    {
        t.Section("must-land: retries a transient refusal, gives up on a permanent one");
        {
            var dev = new FlakyDevice { RefuseNext = 0 };
            t.Check(WritePolicy.MustLand(dev, Frame(dev, Rgb.Red), "landed first time"),
                "a write that lands first time reports true");
            t.Equal(1, dev.Attempts, "...after exactly one attempt");
        }
        {
            // Two transient refusals then success: this is the mouse that was
            // asleep, the SMBus that was held, the receiver that was busy.
            var dev = new FlakyDevice { RefuseNext = 2 };
            t.Check(WritePolicy.MustLand(dev, Frame(dev, Rgb.Red), "recovers"),
                "a write that recovers within the budget reports true");
            t.Equal(3, dev.Attempts, "...having been retried until it landed");
        }
        {
            // A device that never takes it. The budget is deliberately tiny so
            // the suite does not sit in a retry loop; the shape is what matters.
            var dev = new FlakyDevice { RefuseForever = true };
            t.Check(!WritePolicy.MustLand(dev, Frame(dev, Rgb.Red), "never lands", budgetMs: 60),
                "a write that never lands reports FALSE rather than a quiet success");
            t.Check(dev.Attempts > 1, "...after more than one attempt");
        }
        {
            // A throwing driver is still a failed delivery here, not an escape
            // route: the process is on its way out and there is nothing left to
            // stop, so the throw counts as one refused attempt.
            var dev = new FlakyDevice { ThrowForever = true };
            t.Check(!WritePolicy.MustLand(dev, Frame(dev, Rgb.Red), "throws", budgetMs: 60),
                "a driver that throws reports false rather than escaping the must-land path");
            t.Check(dev.Attempts > 1, "...and is still retried");
        }

        t.Section("must-land: one zone");
        {
            var dev = new FlakyDevice { RefuseNext = 1 };
            t.Check(WritePolicy.MustLand(dev, dev, 1, new[] { Rgb.Blue }, "zone recovers"),
                "a refused zone write is retried and lands");
            t.Equal(2, dev.ZoneAttempts, "...on the second attempt");
            t.Equal(Rgb.Blue, dev.Shown[1], "...and the zone really carries the color");
        }
    }

    static void MustLandBypassesDedup(Harness t)
    {
        t.Section("must-land bypasses dedup");
        // The case that makes this necessary: the driver believes the device is
        // already showing this frame, but the hardware has since slept, reset,
        // or been handed back to its firmware. Trusting the cache would return
        // a success for a color nothing is displaying, and a lights-off is
        // exactly the frame most likely to match the cache.
        var dev = new FlakyDevice();
        var black = Frame(dev, Rgb.Black);
        t.Check(dev.SetColors(black), "the ordinary path writes the frame");
        t.Equal(1, dev.Attempts, "...once");
        t.Check(dev.SetColors(black), "the ordinary path dedups the identical frame");
        t.Equal(1, dev.Attempts, "...without touching the device");

        t.Check(WritePolicy.MustLand(dev, black, "same frame, must land"),
            "the must-land path reports true");
        t.Equal(2, dev.Attempts, "...having written the frame anyway, cache or no cache");
        t.Equal(1, dev.Invalidations, "...because it invalidated the cache first");
    }

    static void MustLandThroughARealDriver(Harness t)
    {
        t.Section("must-land through a real driver");
        // Same thing over a real protocol and a real transport, so the retry is
        // proved against a driver's own dedup and logging rather than a fake's.
        var hid = new FakeHid();
        var pad = new SayoDevice(hid, outLen: 64, name: "Sayo");
        var frame = new[] { new Rgb(7, 7, 7) };

        hid.Accept = (_, _) => false;
        t.Check(!WritePolicy.MustLand(pad, frame, "pad never answers", budgetMs: 60),
            "a driver that refuses everything fails the must-land path");
        int tries = hid.Writes.Count;
        t.Check(tries > 1, "...having been asked more than once");

        hid.Accept = null;
        t.Check(WritePolicy.MustLand(pad, frame, "pad answers now"),
            "the same frame lands once the device answers again");
        t.Equal(tries + 1, hid.Writes.Count, "...as exactly one more packet");
    }

    /*---------------- the half users notice ----------------*/
    static void LightsOffIsGuaranteed(Harness t)
    {
        t.Section("lights-off is delivered, not merely posted");
        // The field report this exists for: the machine sleeps with a color
        // still lit because the off command was dropped and nothing retried it.
        // A lights-off is terminal - there is no next frame to correct it - so
        // PushBlack must keep trying rather than post once and hope.
        var dev = new FlakyDevice { RefuseNext = 2, LedCount = 2 };
        // Start it lit, or "ended up black" would be true of a device nothing
        // ever wrote to: Rgb's default IS black.
        dev.Shown[0] = Rgb.Red; dev.Shown[1] = Rgb.Red;
        var lighting = new LightingController();
        try
        {
            lighting.PushBlack(dev);
            lighting.Applier.Drain(3000);
            t.Check(dev.Attempts >= 3, "a refused lights-off is retried on the applier lane");
            t.Check(dev.Shown[0] == Rgb.Black && dev.Shown[1] == Rgb.Black,
                "...until the device is really black");
        }
        finally { lighting.Applier.Drain(3000); }
    }

    static void SuppressedSdkRelease(Harness t)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var vm = new UnifiedRgb.App.MainViewModel(startServices: false);
                var dev = new FlakyDevice();
                vm.Devices.Add(dev);
                Array.Fill(vm.Lighting.FrameFor(dev), Rgb.Red);
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                var host = new OpenRgbHost(vm, vm.Lighting, dispatcher);
                host.BeginExternal(dev);
                vm.LightsSuppressed = true;
                vm.LightsOff();
                vm.Lighting.Applier.Drain(3000);
                int before = dev.Attempts;
                host.EndExternal(dev); // queues restoration, as server disposal during recovery does
                var pump = new System.Windows.Threading.DispatcherFrame();
                dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => pump.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(pump);
                vm.Lighting.Applier.Drain(3000);
                t.Equal(before, dev.Attempts, "queued SDK restore sends no colored frame during suppression");
                t.Equal(Rgb.Black, dev.Shown[0], "queued release leaves the hardware black");
                t.Equal(Rgb.Red, vm.Lighting.FrameFor(dev)[0], "the desired color remains ready for unlock");
                host.Shutdown();
                vm.Lighting.StopAndDrain();
                vm.Lcd.Dispose();
                dispatcher.InvokeShutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new InvalidOperationException("suppressed SDK release test", failure);
    }

    static void TerminalHealth(Harness t)
    {
        t.Section("terminal failures feed device health");
        var dev = new FlakyDevice { RefuseForever = true };
        t.Check(!WritePolicy.MustLand(dev, Frame(dev, Rgb.Black), "test blackout", 60), "blackout refusal is returned");
        t.Equal(dev.Attempts, DeviceHealth.Shared.RefusalsOf(dev), "every blackout refusal reaches health");
        var zone = new FlakyDevice { RefuseForever = true };
        WritePolicy.MustLand(zone, zone, 0, new[] { Rgb.Red }, "test zone", 60);
        t.Equal(zone.ZoneAttempts, DeviceHealth.Shared.RefusalsOf(zone), "every zone refusal reaches health");
        var thrown = new FlakyDevice { ThrowForever = true };
        WritePolicy.MustLand(thrown, Frame(thrown, Rgb.Black), "test dead handle", 60);
        t.Equal(thrown.Attempts, DeviceHealth.Shared.RefusalsOf(thrown), "thrown terminal writes reach health too");
        t.Equal(DeviceHealthState.Retrying, DeviceHealth.Shared.StateOf(thrown), "a dead handle no longer looks connected");
        int prior = DeviceHealth.Shared.RefusalsOf(thrown);
        try { DeviceHealth.WriteFrame(thrown, Frame(thrown, Rgb.Black)); }
        catch (InvalidOperationException) { }
        t.Equal(prior + 1, DeviceHealth.Shared.RefusalsOf(thrown), "a thrown streaming frame records one refusal");
        DeviceHealth.Shared.Forget(dev);
        DeviceHealth.Shared.Forget(zone);
        DeviceHealth.Shared.Forget(thrown);
    }

    static void RecoveryMemory(Harness t)
    {
        t.Section("desired lighting survives removal and later arrival");
        var memory = new RecoveryLightingState();
        var first = new FlakyDevice { Name = "Replugged" };
        var frame = Frame(first, Rgb.Red);
        memory.Remember(new[] { first }, _ => frame,
            new[] { new UnifiedRgb.App.EffectAssignment { Device = first.Name, Effect = "Rainbow", Count = 2 } });
        memory.Remember(Array.Empty<IRgbDevice>(), _ => throw new Exception(), Array.Empty<UnifiedRgb.App.EffectAssignment>());
        t.Equal(Rgb.Red, memory.Frames[first.Name][0], "removal-only scan retains the desired color");
        t.Equal(1, memory.Effects.Count, "removal-only scan retains the effect");
        var replacement = new FlakyDevice { Name = first.Name };
        t.Equal(Rgb.Red, memory.Frames[replacement.Name][0], "a new instance matches the retained frame");
        frame[0] = Rgb.Black;
        t.Equal(Rgb.Red, memory.Frames[first.Name][0], "remembered frames are independent snapshots");
        memory.Remember(new[] { replacement }, _ => frame, Array.Empty<UnifiedRgb.App.EffectAssignment>());
        t.Equal(0, memory.Effects.Count, "a present device's explicit static choice removes its old effect");
        memory.ApplyProfile(new UnifiedRgb.App.Profile { Name = "New profile", DeviceFrames = new() { [first.Name] = new[] { "0000FF", "0000FF" } } });
        t.Equal(Rgb.FromHex("0000FF"), memory.Frames[first.Name][0], "a profile change also updates absent desired colors");
    }

    static Rgb[] Frame(IRgbDevice dev, Rgb c)
    {
        var f = new Rgb[dev.LedCount];
        Array.Fill(f, c);
        return f;
    }

    /// <summary>A device that refuses on demand and keeps the state real
    /// hardware would keep: what it is showing, and a dedup cache that only
    /// commits after a write it accepted. FakeHid covers the drivers that
    /// speak HID; this covers the policy above them, including the paths a
    /// transport cannot reach (a device that throws, a cache that must be
    /// bypassed).</summary>
    sealed class FlakyDevice : IRgbDevice, IZoneWritable
    {
        public string Name { get; init; } = "Flaky";
        public string Vendor => "Test";
        public DeviceType Type => DeviceType.Other;
        public int LedCount { get; init; } = 2;
        public IReadOnlyList<RgbZone> Zones => new[] { new RgbZone { Name = "All", Offset = 0, Count = LedCount } };

        /// <summary>Refuse this many more writes, then start accepting.</summary>
        public int RefuseNext;
        public bool RefuseForever;
        public bool ThrowForever;

        public int Attempts;        // full-frame calls that got as far as the device
        public int ZoneAttempts;
        public int Invalidations;
        public readonly Rgb[] Shown = new Rgb[2];

        Rgb[]? _last;

        public void InvalidateCache() { Invalidations++; _last = null; }

        public bool SetColors(IReadOnlyList<Rgb> colors)
        {
            if (ThrowForever) { Attempts++; throw new InvalidOperationException("device is gone"); }
            if (WritePolicy.Unchanged(_last, colors)) return true;
            Attempts++;
            if (RefuseForever || RefuseNext > 0)
            {
                if (RefuseNext > 0) RefuseNext--;
                _last = null;                    // dedup after success, never before
                return false;
            }
            for (int i = 0; i < colors.Count && i < Shown.Length; i++) Shown[i] = colors[i];
            WritePolicy.Cache(ref _last, colors);
            return true;
        }

        public bool SetZone(int offset, IReadOnlyList<Rgb> colors)
        {
            ZoneAttempts++;
            if (RefuseForever || RefuseNext > 0)
            {
                if (RefuseNext > 0) RefuseNext--;
                return false;
            }
            for (int i = 0; i < colors.Count && offset + i < Shown.Length; i++) Shown[offset + i] = colors[i];
            _last = null;                        // a partial write invalidates the whole-frame cache
            return true;
        }

        public void Dispose() { }
    }
}

using System.IO;
using UnifiedRgb.App;
using UnifiedRgb.App.Services;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Games;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The lighting-pipeline findings of the 2026-10-14 review      |
| (D2-D14, E14), one section each, every one a behaviour that  |
| was wrong and can be pinned without hardware.                |
|                                                              |
| Pure pieces first (the applier's key naming, the remap's     |
| scrub, the engine's empty-range refusal), then the engine    |
| and effect threading (the CS2 event diff, the wireless fans' |
| baked-mode re-check), then the view-model paths, which need  |
| a dispatcher and so run on an STA thread of their own: the   |
| freeze a replaced effect left on a zone device, the identify |
| blink and its restore, the exit preview that never ended,    |
| and a startup profile with a hand-broken zone layout.        |
|                                                              |
| D3 (bake loops that did not close) lives with the other loop |
| checks in Effects.cs, which now sweeps every bakeable effect.|
\*-----------------------------------------------------------*/
static class FixLightingSuite
{
    public static void Run(Harness t)
    {
        double savedBrightness = Master.Brightness;
        Master.Brightness = 1.0;
        try
        {
            ApplierNamesTheDevice(t);
            WrittenMarksAreForgotten(t);
            RemapScrubsBrokenSpans(t);
            ZoneLayoutsSurviveDuplicateNames(t);
            EmptyRangesAreRefused(t);
            Cs2EventsFireOncePerSnapshot(t);
            BakedFansAreNotStreamedOver(t);
            OnVm(t, "replaced effect leaves no frozen zone (D2)", ReplacedEffectLeavesNoFrozenZone);
            OnVm(t, "identify blink (D4)", IdentifyIsVisibleAndRestoresFinished);
            OnVm(t, "exit preview (D13)", ExitPreviewIsUndone);
            OnVm(t, "broken saved layout (D10)", BrokenLayoutDoesNotStopTheProfile);
        }
        finally { Master.Brightness = savedBrightness; }
    }

    /*---------------- D9: a failing write says which device ----------------*/
    static void ApplierNamesTheDevice(Harness t)
    {
        t.Section("D9: applier failures name the device, and each has its own rate limit");
        // Unique per run: Log.Occasional's buckets live for the process.
        string tag = Guid.NewGuid().ToString("N")[..6];
        var left = new FakeDevice { Name = $"Mouse L {tag}" };
        var right = new FakeDevice { Name = $"Mouse R {tag}" };

        t.Equal(left.Name, CoalescingApplier.Describe(left), "a device key is described by its name, not its type");
        t.Equal($"({left.Name}, 3)", CoalescingApplier.Describe((left, 3)), "a tuple key is described element by element");
        t.Equal($"(exit-preview, {right.Name})", CoalescingApplier.Describe(("exit-preview", right)), "...whatever position the device is in");
        t.Equal("lane:smbus", CoalescingApplier.Describe("lane:smbus"), "a string key is itself");

        // Two devices of one TYPE failing at once. The key used to format as
        // the type name, so the second failure landed in the first one's
        // one-minute bucket and was never logged at all.
        var applier = new CoalescingApplier();
        applier.Post(left, left, () => throw new IOException("left refused"));
        applier.Post(right, right, () => throw new IOException("right refused"));
        applier.Drain(2000);
        string log = ReadLog();
        t.Check(log.Contains($"write failed on {left.Name} ({left.Name}): left refused", StringComparison.Ordinal),
            "the first device's failure is logged under its name");
        t.Check(log.Contains($"write failed on {right.Name} ({right.Name}): right refused", StringComparison.Ordinal),
            "the second device of the same type is logged too (was: muted by the first's rate limit)");
    }

    static string ReadLog()
    {
        try
        {
            using var fs = new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var rd = new StreamReader(fs);
            return rd.ReadToEnd();
        }
        catch (IOException) { return ""; }   // no log yet: the checks reading it fail and say what was missing
    }

    /*---------------- D6: a rescan does not pin the replaced devices ----------------*/
    static void WrittenMarksAreForgotten(Harness t)
    {
        t.Section("D6: ForgetFrames forgets which devices were written");
        var lighting = new LightingController();
        var dev = new FakeDevice { Name = "Written" };
        lighting.PushFrame(dev);
        lighting.Applier.Drain(2000);
        t.Check(lighting.HasBeenWritten(dev), "a pushed device reads as written");
        lighting.ForgetFrames();
        t.Check(!lighting.HasBeenWritten(dev), "after ForgetFrames it does not (was: kept, pinning the old instance forever)");
    }

    /*---------------- D10: profiles.json is a file people edit ----------------*/
    static void RemapScrubsBrokenSpans(Harness t)
    {
        t.Section("D10: HardwareRemap drops saved spans that mean nothing");
        var dev = new FakeDevice { Name = "Remapped", LedCount = 2 };
        var spans = new ZoneSpan[]
        {
            null!,                                                      // "DeviceZones": { "x": [ null, ... ] }
            new() { Name = "All", Offset = -3, Count = 2 },
            new() { Name = "All", Offset = 0, Count = 0 },
            new() { Name = "All", Offset = int.MaxValue, Count = 5 },
            new() { Name = "All", Offset = 0, Count = 2 },              // the one real entry
        };
        Exception? threw = null;
        bool landed = false, unchanged = false, overflowRefused = false, saneMapped = false;
        try
        {
            var map = new HardwareRemap(dev, spans, savedLeds: 2);
            unchanged = !map.LayoutChanged;
            _ = map.UnknownZones.ToList();
            var into = new Rgb[2];
            map.MapFrame(new[] { Rgb.Red, Rgb.Red }, into);
            landed = into[0] == Rgb.Red && into[1] == Rgb.Red;
            overflowRefused = map.Map(int.MaxValue, 1) == null;
            saneMapped = map.Map(0, 2) == (0, 2);
        }
        catch (Exception ex) { threw = ex; }
        t.Check(threw == null, $"null, negative, empty and overflowing spans do not throw ({threw?.GetType().Name}: {threw?.Message})");
        t.Check(unchanged, "with them dropped, the one real span matches the device: layout unchanged");
        t.Check(landed, "the saved frame still lands");
        t.Check(overflowRefused, "a saved range whose end overflows int is not placed");
        t.Check(saneMapped, "a sane range still maps straight through");
    }

    /*---------------- E14: two devices, one name ----------------*/
    static void ZoneLayoutsSurviveDuplicateNames(Harness t)
    {
        t.Section("E14: Rescan's layout snapshot survives two devices sharing a name");
        var first = new FakeDevice { Name = "Twin", LedCount = 2 };
        var second = new FakeDevice
        {
            Name = "Twin", LedCount = 4,
            ZoneSpec = new[] { new RgbZone { Name = "A", Offset = 0, Count = 2 }, new RgbZone { Name = "B", Offset = 2, Count = 2 } },
        };
        Dictionary<string, ZoneSpan[]>? byName = null;
        Exception? threw = null;
        try { byName = MainViewModel.ZoneLayoutsByName(new IRgbDevice[] { first, second }); }
        catch (Exception ex) { threw = ex; }
        t.Check(threw == null, $"no throw on a repeated name (was: ToDictionary's ArgumentException, every Rescan) ({threw?.Message})");
        t.Check(byName != null && byName["Twin"].Length == 2 && byName["Twin"][1].Name == "B",
            "the last device of a name wins, as RecoveryLightingState does for frames");
    }

    /*---------------- D12: an empty range is never a channel ----------------*/
    static void EmptyRangesAreRefused(Harness t)
    {
        t.Section("D12: EffectEngine.Start refuses an empty range");
        var engine = new EffectEngine();
        var dev = new FakeDevice { Name = "Zero-LED zone", LedCount = 2 };
        try
        {
            var a = engine.Start(dev, 0, 0, new Rgb[2], new CountingEffect(), 1, Rgb.Red);
            var b = engine.Start(dev, 0, 0, new Rgb[2], new CountingEffect(), 1, Rgb.Red);
            t.Check(!a.IsRunning && !b.IsRunning, "the channel handed back is already stopped");
            t.Equal(0, engine.ChannelsFor(dev).Count, "nothing is listed (was: one more worker per re-apply, never replaced)");
            Exception? threw = null;
            try { t.Check(!engine.Start(dev, 1, -4, new Rgb[2], new CountingEffect(), 1, Rgb.Red).IsRunning, "a negative count is refused the same way"); }
            catch (Exception ex) { threw = ex; }
            t.Check(threw == null, $"a negative count does not throw (was: a negative array size) ({threw?.GetType().Name})");
        }
        finally { engine.StopAll(); }
    }

    /*---------------- D14: one effect instance, many renderers ----------------*/
    static void Cs2EventsFireOncePerSnapshot(Harness t)
    {
        t.Section("D14: CS2 detects each event once, however many channels render it");
        var fx = new Cs2Effect();
        GameState current = new() { Playing = true, Health = 100, RoundKills = 0 };
        fx.StateSource = () => Volatile.Read(ref current);
        var pos = Line(8);

        // The same snapshot through two "channels" in turn: one event.
        var buf = new Rgb[8];
        fx.Render(buf, pos, 0, 1, default);
        Volatile.Write(ref current, new GameState { Playing = true, Health = 80, RoundKills = 1 });
        fx.Render(buf, pos, 0, 1, default);
        fx.Render(buf, pos, 0, 1, default);
        t.Equal(1, Volatile.Read(ref fx.HurtEvents), "a hit seen by two renders is one hit");
        t.Equal(1, Volatile.Read(ref fx.KillEvents), "a kill seen by two renders is one kill");

        // Three device workers and a preview hammering the one shared instance
        // while the game posts a hit (and every tenth step a kill) each step.
        // Every event must be counted exactly once: the unguarded diff could
        // let a worker on an older snapshot rewind the health and fire the
        // same hit again on the next render.
        const int Steps = 60;
        int hurtBefore = fx.HurtEvents, killsBefore = fx.KillEvents;
        using var stop = new CancellationTokenSource();
        var renderers = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            var b = new Rgb[8];
            while (!stop.IsCancellationRequested) fx.Render(b, pos, 0, 1, default);
        }) { IsBackground = true }).ToList();
        renderers.ForEach(r => r.Start());
        bool kept = true;
        for (int i = 1; i <= Steps && kept; i++)
        {
            Volatile.Write(ref current, new GameState { Playing = true, Health = 80 - i, RoundKills = 1 + i / 10 });
            kept = WaitUntil(() => Volatile.Read(ref fx.HurtEvents) >= hurtBefore + i, 2000);
        }
        Thread.Sleep(50);   // stragglers still rendering the last snapshot
        stop.Cancel();
        renderers.ForEach(r => r.Join(1000));
        t.Check(kept, "every posted hit is seen");
        t.Equal(Steps, fx.HurtEvents - hurtBefore, "each hit counted exactly once across four concurrent renderers");
        t.Equal(Steps / 10, fx.KillEvents - killsBefore, "each kill counted exactly once");
    }

    /*---------------- D7 (engine side): baked mode wins the race ----------------*/
    static void BakedFansAreNotStreamedOver(Harness t)
    {
        t.Section("D7: a streamed frame waiting at the gate is dropped once baked mode is on");
        var instance = typeof(LianLiWireless).GetProperty("Instance",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        object? prior = instance?.GetValue(null);
        var engine = new EffectEngine();
        var fans = new LianLiWireless(new QuietUsb(), "D7", new byte[6], new byte[6], 8, 1, 1);
        try
        {
            var shadowField = typeof(LianLiWireless).GetField("_shadow",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Rgb[] Shadow() => (Rgb[])((Rgb[])shadowField.GetValue(fans)!).Clone();

            var fx = new ShiftingFx();
            engine.Start(fans, 0, fans.LedCount, new Rgb[fans.LedCount], fx, 1, Rgb.Red);
            t.Check(WaitUntil(() => fx.Renders > 2 && Shadow()[0] != default, 3000), "the fans are streaming");

            Rgb[] before;
            lock (EffectEngine.WriteGateFor(fans))
            {
                // The worker renders its next frame and parks here, at the gate,
                // past its top-of-loop baked-mode check. The bake then switches
                // baked mode on (and would upload, under this same gate).
                Thread.Sleep(200);
                fans.SuppressStreaming = true;
                before = Shadow();
            }
            Thread.Sleep(400);
            t.Check(Shadow().SequenceEqual(before),
                "the parked frame is not written after baked mode came on (was: it landed, cancelling the upload)");
        }
        finally
        {
            engine.StopAll();         // the worker first, then the device it writes
            fans.Dispose();
            instance?.SetValue(null, prior);
        }
    }

    /// <summary>A transmitter that takes every packet and keeps none.</summary>
    sealed class QuietUsb : UnifiedRgb.Core.Native.IUsbWriter
    {
        public byte BulkOutPipe => 1;
        public bool Write(byte pipe, byte[] buffer) => true;   // canned success: the frames are read back from the driver's shadow
        public void Dispose() { }
    }

    /*================ the view-model paths, on an STA thread ================*/

    static void OnVm(Harness t, string what, Action<Harness, MainViewModel> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainViewModel? vm = null;
            try
            {
                vm = new MainViewModel(startServices: false);
                vm.Profiles.Clear();
                Master.Brightness = 1.0;   // the view model loads it from settings
                body(t, vm);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (vm != null)
                {
                    vm.Shows.Dispose(); vm.Lighting.StopAndDrain(); vm.Lcd.Dispose();
                    ((LianBakeService)typeof(MainViewModel).GetField("_bake",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!).Stop();
                }
                Master.Brightness = 1.0;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        t.Check(failure == null, $"{what}: ran without throwing ({failure?.GetType().Name}: {failure?.Message})");
    }

    /*---------------- D2: a replaced effect leaves no zone frozen ----------------*/
    static void ReplacedEffectLeavesNoFrozenZone(Harness t, MainViewModel vm)
    {
        t.Section("D2: an effect or Static on one zone over a whole-device effect");
        var green = new Rgb(0, 255, 0);

        // An effect on zone A over a whole-device effect.
        var dev = new ZoneRecorder { Name = "Board D2" };
        vm.Devices.Add(dev);
        Array.Fill(vm.Lighting.FrameFor(dev), green);            // the statics under everything
        vm.SelectedDevice = dev;
        vm.SelectedTarget = vm.Targets[0];                        // Entire device
        vm.SelectedEffectChoice = vm.Effects.First(e => e.Name == "Rainbow Cycle");
        t.Check(WaitUntil(() => dev.Shown[3] != default && dev.Shown[3] != green, 3000), "the whole-device effect is painting zone B");

        int mark = dev.WriteCount;
        vm.SelectedTarget = vm.Targets[1];                        // zone A
        vm.SelectedEffectChoice = vm.Effects.First(e => e.Name == "Breathing");
        vm.Lighting.Applier.Drain(3000);
        t.Check(WaitUntil(() => dev.Shown[2] == green && dev.Shown[3] == green, 2000),
            "zone B goes back to its static colour (was: frozen on the replaced effect's last frame)");
        t.Check(dev.WritesSince(mark).All(w => w.Offset >= 2 || w.Colors.All(c => c != green)),
            "the restore did not paint over zone A, where the new effect runs");
        Thread.Sleep(150);
        t.Check(dev.Shown[2] == green && dev.Shown[3] == green, "...and stays static: nothing animates zone B any more");

        // Static on zone A over a whole-device effect. Zone A's own state has to
        // read as an effect first, or picking Static is "no change"; set the
        // way a target picked earlier and then overridden would have it.
        var dev2 = new ZoneRecorder { Name = "Board D2 static" };
        vm.Devices.Add(dev2);
        Array.Fill(vm.Lighting.FrameFor(dev2), green);
        vm.SelectedDevice = dev2;
        vm.SelectedTarget = vm.Targets[0];
        vm.SelectedEffectChoice = vm.Effects.First(e => e.Name == "Rainbow Cycle");
        t.Check(WaitUntil(() => dev2.Shown[3] != default && dev2.Shown[3] != green, 3000), "the second board animates");
        var fxFor = typeof(MainViewModel).GetMethod("FxFor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var zoneA = fxFor.Invoke(vm, new object[] { dev2, 0, 2 })!;
        zoneA.GetType().GetProperty("Choice")!.SetValue(zoneA, vm.Effects.First(e => e.Name == "Breathing"));
        vm.SelectedTarget = vm.Targets[1];
        vm.SelectedEffectChoice = vm.Effects[0];                  // Static
        vm.Lighting.Applier.Drain(3000);
        Thread.Sleep(100);
        t.Check(dev2.Shown.All(c => c == green),
            "Static on zone A puts the WHOLE replaced effect's range back to static (was: zone B frozen)");
        t.Equal(0, vm.Lighting.Engine.ChannelsFor(dev2).Count, "nothing is left running on the board");
    }

    /*---------------- D4: identify can be seen, and restores the finished frame ----------------*/
    static void IdentifyIsVisibleAndRestoresFinished(Harness t, MainViewModel vm)
    {
        t.Section("D4: identify restores through the write boundary");
        var still = new FakeDevice { Name = "Identify static", LedCount = 3 };
        vm.Devices.Add(still);
        Array.Fill(vm.Lighting.FrameFor(still), new Rgb(200, 100, 50));
        Master.Brightness = 0.5;
        vm.IdentifyDevice(still);
        vm.Lighting.Applier.Drain(4000);
        Master.Brightness = 1.0;
        t.Equal(new Rgb(100, 50, 25), still.Last?[0] ?? default,
            "the restore is the stored colour at the master brightness (was: 200,100,50 - full brightness, untrimmed)");

        t.Section("D4: the blink is visible on an animating device");
        var busy = new FakeDevice { Name = "Identify animated", LedCount = 3 };
        vm.Devices.Add(busy);
        vm.Lighting.Engine.Start(busy, 0, 3, vm.Lighting.FrameFor(busy), new ShiftingFx(), 1, Rgb.Red);
        t.Check(WaitUntil(() => busy.WriteCount > 3, 3000), "the effect is streaming");
        vm.IdentifyDevice(busy);
        vm.Lighting.Applier.Drain(4000);
        vm.Lighting.Engine.StopRange(busy, 0, 3);

        List<Rgb[]> frames;
        lock (busy.Writes) frames = busy.Writes.Select(w => w.Frame).ToList();
        static bool All(Rgb[] f, Rgb c) => f.All(x => x == c);
        int first = frames.FindIndex(f => All(f, Rgb.White));
        bool solid = first >= 0 && first + 6 < frames.Count;
        for (int i = 0; solid && i < 6; i++)
            solid = All(frames[first + i], i % 2 == 0 ? Rgb.White : Rgb.Black);
        t.Check(solid, "white, dark, white, dark, white, dark with no effect frame in between (was: repainted within ~16 ms of every flash)");
    }

    /*---------------- D13: an exit preview is undone when the window closes ----------------*/
    static void ExitPreviewIsUndone(Harness t, MainViewModel vm)
    {
        t.Section("D13: closing the exit-behaviour window puts the device back");
        var dev = new ExitRecorder { Name = "Exit preview" };
        vm.Devices.Add(dev);
        Array.Fill(vm.Lighting.FrameFor(dev), Rgb.Red);
        vm.Lighting.PushFrame(dev);                               // the app's own colour, as a profile apply leaves it
        vm.Lighting.Applier.Drain(3000);
        var blue = new ExitBehavior { Mode = ExitMode.Static, ColorHex = "0000FF" };

        vm.ApplyExitBehaviorNow(dev, blue);
        vm.Lighting.Applier.Drain(3000);
        t.Equal(Rgb.Blue, dev.HardwareStatic ?? default, "Apply now put the device into its exit state");
        int before = dev.FrameCount;
        vm.EndExitPreview();                                      // ExitBehaviorWindow.OnClosed
        vm.Lighting.Applier.Drain(3000);
        t.Check(dev.FrameCount > before && dev.LastFrame is { } f && f.All(c => c == Rgb.Red),
            "the stored colour is written back (was: the preview stayed until the next profile apply)");
        int settled = dev.FrameCount;
        vm.EndExitPreview();
        vm.Lighting.Applier.Drain(3000);
        t.Equal(settled, dev.FrameCount, "a second close with nothing previewed writes nothing");

        t.Section("D13: ...and back to black during a dark window");
        var dark = new ExitRecorder { Name = "Exit preview dark" };
        vm.Devices.Add(dark);
        Array.Fill(vm.Lighting.FrameFor(dark), Rgb.Red);
        vm.Lighting.PushFrame(dark);
        vm.Lighting.Applier.Drain(3000);
        vm.LightsSuppressed = true;
        try
        {
            vm.ApplyExitBehaviorNow(dark, blue);
            vm.Lighting.Applier.Drain(3000);
            vm.EndExitPreview();
            vm.Lighting.Applier.Drain(3000);
            t.Check(dark.LastFrame is { } d && d.All(c => c == Rgb.Black), "a suppressed desk is not relit by closing the window");
        }
        finally { vm.LightsSuppressed = false; }

        t.Section("D13: ...but a device the app never wrote is left alone");
        var untouched = new ExitRecorder { Name = "Exit preview untouched" };
        vm.Devices.Add(untouched);
        vm.ApplyExitBehaviorNow(untouched, blue);
        vm.Lighting.Applier.Drain(3000);
        int previewed = untouched.FrameCount;
        vm.EndExitPreview();
        vm.Lighting.Applier.Drain(3000);
        t.Equal(previewed, untouched.FrameCount,
            "closing the window does not push the zeroed default frame (was: it blacked a device the app had never driven)");
    }

    /*---------------- D10: a broken saved layout does not stop the profile ----------------*/
    static void BrokenLayoutDoesNotStopTheProfile(Harness t, MainViewModel vm)
    {
        t.Section("D10: a profile with a hand-broken zone layout still applies");
        var dev = new FakeDevice { Name = "Broken layout", LedCount = 2 };
        vm.Devices.Add(dev);
        var p = new Profile { Name = "Hand edited", DeviceFrames = new() { [dev.Name] = new[] { "FF0000", "FF0000" } } };
        p.DeviceZones[dev.Name] = new ZoneSpan[] { null!, new() { Name = "All", Offset = -3, Count = 2 } };
        vm.Profiles.Add(p);
        Exception? threw = null;
        try { vm.ApplyProfile(p); }
        catch (Exception ex) { threw = ex; }
        vm.Lighting.Applier.Drain(2000);
        t.Check(threw == null, $"LoadProfile does not throw (was: NullReferenceException in MapFrame) ({threw?.Message})");
        t.Check(dev.Last is { } f && f.All(c => c == Rgb.Red), "the profile's colours land");
    }

    /*================ fakes ================*/

    /// <summary>Different every render, so the engine's dedup never engages,
    /// and never all-white or all-black, so it cannot be mistaken for a blink.</summary>
    sealed class ShiftingFx : IEffect
    {
        public string Name => "Shifting";
        public bool UsesBaseColor => true;
        int _n;
        public int Renders => Volatile.Read(ref _n);
        public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb bc)
        {
            byte v = (byte)(Interlocked.Increment(ref _n) & 0x7F);
            Array.Fill(buf, new Rgb(v, (byte)(200 - v), 60));
        }
    }

    /// <summary>A two-zone, zone-writable device that keeps what the hardware
    /// would be showing, and every write it took.</summary>
    sealed class ZoneRecorder : IRgbDevice, IZoneWritable
    {
        public string Name { get; init; } = "Zones";
        public string Vendor => "Test";
        public DeviceType Type => DeviceType.Motherboard;
        public int LedCount => 4;
        public IReadOnlyList<RgbZone> Zones { get; } = new[]
        {
            new RgbZone { Name = "A", Offset = 0, Count = 2 },
            new RgbZone { Name = "B", Offset = 2, Count = 2 },
        };
        readonly object _lock = new();
        readonly Rgb[] _shown = new Rgb[4];
        readonly List<(int Offset, Rgb[] Colors)> _writes = new();
        public Rgb[] Shown { get { lock (_lock) return (Rgb[])_shown.Clone(); } }
        public int WriteCount { get { lock (_lock) return _writes.Count; } }
        public List<(int Offset, Rgb[] Colors)> WritesSince(int mark) { lock (_lock) return _writes.Skip(mark).ToList(); }
        public bool SetColors(IReadOnlyList<Rgb> colors) => SetZone(0, colors);
        public bool SetZone(int offset, IReadOnlyList<Rgb> colors)
        {
            lock (_lock)
            {
                for (int i = 0; i < colors.Count && offset + i < _shown.Length; i++) _shown[offset + i] = colors[i];
                _writes.Add((offset, colors.ToArray()));
            }
            return true;   // canned success: this fake never refuses
        }
        public void Dispose() { }
    }

    /// <summary>A device with a hardware static mode, recording both what its
    /// firmware was told and every frame the app streamed to it.</summary>
    sealed class ExitRecorder : IRgbDevice, IHardwareModes
    {
        public string Name { get; init; } = "Exit";
        public string Vendor => "Test";
        public DeviceType Type => DeviceType.Other;
        public int LedCount => 2;
        public IReadOnlyList<RgbZone> Zones => new[] { new RgbZone { Name = "All", Offset = 0, Count = 2 } };
        public HardwareExitCaps ExitCaps => HardwareExitCaps.Static;
        public IReadOnlyList<string> HardwareEffects => Array.Empty<string>();
        readonly object _lock = new();
        readonly List<Rgb[]> _frames = new();
        Rgb? _static;
        public Rgb? HardwareStatic { get { lock (_lock) return _static; } }
        public int FrameCount { get { lock (_lock) return _frames.Count; } }
        public Rgb[]? LastFrame { get { lock (_lock) return _frames.Count == 0 ? null : _frames[^1]; } }
        public bool SetColors(IReadOnlyList<Rgb> colors) { lock (_lock) _frames.Add(colors.ToArray()); return true; }
        public bool SetHardwareStatic(Rgb color) { lock (_lock) _static = color; return true; }
        public bool SetHardwareEffect(string name, Rgb? color) => false;   // not offered: ExitCaps has Static only
        public bool ReturnToHardware() => false;                           // likewise
        public void Dispose() { }
    }
}

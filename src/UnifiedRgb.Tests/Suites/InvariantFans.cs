using System.IO;
using System.Reflection;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using UnifiedRgb.App;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Sensors;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Fan-control invariants from the 2026-10-15 review (V7-V10).  |
|                                                              |
| Every check runs the hub's REAL code - the open's publish    |
| half, ApplyDuty, RestoreAllFans, Shutdown, the Identify      |
| continuation, the Cooling rows - against fakes: a board made |
| of fake LHM controls (LhmFans' test constructor: no Computer,|
| no driver, no ISA mutex), a fake card behind the hub's       |
| NvAPI seam, a fake wireless receiver. The hub itself stays   |
| shut down throughout (no timer, so no tick and no real       |
| source ever opens); the fake GPU handle is withdrawn before  |
| the real NvAPI calls are put back.                          |
\*-----------------------------------------------------------*/
static class InvariantFansSuite
{
    static string CfgPath => AppPaths.Local("fan-config.json");
    static readonly IntPtr FakeCard = (IntPtr)0x5EED;

    static SensorHub.FanConfigEntry Manual(string name, int pct) => new(name, "manual", pct, null);
    static SensorHub.FanConfigEntry Curve(string name) => new(name, "curve", 0, FanCurve.Preset_("Quiet", TempSource.Cpu));

    public static void Run(Harness t)
    {
        SensorHub.Shutdown();   // never opened in the harness (see FixFans)

        var instance = typeof(LianLiWireless).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        object? priorLian = instance.GetValue(null);
        var gpuSet = SensorHub.GpuSetDuty;
        var gpuRestore = SensorHub.GpuRestoreAuto;
        var gpuIsManual = SensorHub.GpuIsManual;
        int burst = SensorHub.IdentifyBurstMs, seedWait = LianLiWireless.PwmSeedWaitMs;
        t.Section("setup");
        t.Check(CfgPath.StartsWith(Isolation.Root, StringComparison.OrdinalIgnoreCase)
             && SensorHub.FanMarkerFile.StartsWith(Isolation.Root, StringComparison.OrdinalIgnoreCase),
            "fan-config.json and the board marker live in the harness's private LocalDir");
        try
        {
            instance.SetValue(null, null);
            LianLiWireless.PwmSeedWaitMs = 0;   // no telemetry here: seed at once
            void SetLian(LianLiWireless? rx) => instance.SetValue(null, rx);

            V7(t);
            V8Gpu(t);
            V8GpuOnlyIfOurs(t);
            V8Pending(t);
            V8Wireless(t, SetLian);
            V9(t, SetLian);
            V10Pure(t);
            V10Reopen(t);
            CallSites(t);
        }
        finally
        {
            // The fake card's handle goes first, THEN the real NvAPI calls come
            // back: never the other way round.
            SensorHub.ResetFansForTest();
            SensorHub.GpuSetDuty = gpuSet;
            SensorHub.GpuRestoreAuto = gpuRestore;
            SensorHub.GpuIsManual = gpuIsManual;
            SensorHub.IdentifyBurstMs = burst;
            LianLiWireless.PwmSeedWaitMs = seedWait;
            instance.SetValue(null, priorLian);
            try { File.Delete(CfgPath); } catch (IOException) { }   // a stuck file only costs the next run a stale entry
            SensorHub.ReloadFanConfig();   // leave the hub holding nothing
            SensorHub.MarkBoardControl(false);
        }
    }

    /// <summary>A clean hub: no modes, no backends, no file, no marker.</summary>
    static void Fresh()
    {
        SensorHub.ResetFansForTest();
        File.Delete(CfgPath);
        SensorHub.MarkBoardControl(false);
        SensorHub.PublishTempsForTest(null, null);
    }

    static void WriteCfg(params SensorHub.FanConfigEntry[] fans)
        => File.WriteAllText(CfgPath, System.Text.Json.JsonSerializer.Serialize(new { fans }));

    static Dictionary<string, SensorHub.FanConfigEntry> ReadCfg()
        => File.Exists(CfgPath) ? SensorHub.ParseFanConfig(File.ReadAllText(CfgPath)) : new(StringComparer.Ordinal);

    /// <summary>The first open: publish `board` (and the fake card, when
    /// given) against the file as it is now.</summary>
    static void Open(LhmFans? board, bool gpu = false)
        => SensorHub.PublishForTest(board, gpu ? FakeCard : IntPtr.Zero, gpu, ReadCfg(), SensorHub.FanConfigReloadsForTest);

    /*--- fakes ---*/

    static (LhmFans Lhm, Dictionary<string, FakeLhmControl> Ctl) Board(params string[] names)
    {
        var ctl = new Dictionary<string, FakeLhmControl>(StringComparer.Ordinal);
        var fans = new List<LhmFans.Fan>();
        foreach (string n in names)
        {
            var c = DispatchProxy.Create<IControl, FakeLhmControl>();
            ctl[n] = (FakeLhmControl)(object)c;
            fans.Add(new LhmFans.Fan { Name = n, Rpm = DispatchProxy.Create<ISensor, FakeLhmSensor>(), Control = c });
        }
        return (new LhmFans(fans), ctl);
    }

    static FakeCardFans InstallCard()
    {
        var card = new FakeCardFans();
        SensorHub.GpuSetDuty = card.SetDuty;
        SensorHub.GpuRestoreAuto = card.RestoreAuto;
        SensorHub.GpuIsManual = card.IsManual;
        return card;
    }

    sealed class FakeUsb : UnifiedRgb.Core.Native.IUsbWriter
    {
        public byte BulkOutPipe => 1;
        public bool Write(byte pipe, byte[] buffer) => true;
        public void Dispose() { }
    }

    static LianLiWireless NewReceiver(string group) =>
        new(new FakeUsb(), group, new byte[] { 1, 2, 3, 4, 5, 6 }, new byte[] { 6, 5, 4, 3, 2, 1 }, 8, 1, 3)
        { ReceiverOpener = () => null };

    static byte Pwm(int percent) => (byte)Math.Clamp(percent * 255 / 100, 0, 255);

    /*-----------------------------------------------------------*\
    | V7: the tick snapshots `busy` (the fans mid-Identify) and    |
    | the mode generation, then writes. A burst that began after   |
    | the snapshot did not move the generation, so the tick's      |
    | write went through and the fan sat on the curve's duty for   |
    | the rest of the burst.                                       |
    \*-----------------------------------------------------------*/
    static void V7(Harness t)
    {
        t.Section("V7: an Identify burst is not cancelled by a tick already in flight");
        Fresh();
        var (board, ctl) = Board("Fan #1", "Fan #2");
        try
        {
            Open(board);
            var fan = ctl["Fan #1"];
            var curve = FanCurve.Preset_("Quiet", TempSource.Cpu);
            int at45 = Math.Max(SensorHub.FloorFor(0), curve.DutyAt(45)), at85 = Math.Max(SensorHub.FloorFor(0), curve.DutyAt(85));
            t.Check(at45 != at85 && at45 < 100, $"(the test curve gives distinct duties: {at45}% at 45°C, {at85}% at 85°C)");
            SensorHub.PublishTempsForTest(45, null);
            t.Check(SensorHub.SetFanCurve(0, curve), "a board fan is put on a curve");
            t.Equal((float)at45, fan.Software, "the curve drives it");
            var running = SensorHub.FanCurveOf(0)!;   // the hub's own instance, as the tick's snapshot holds it

            SensorHub.IdentifyBurstMs = 400;
            long gen = SensorHub.ModeGenForTest;      // the tick snapshotted here, before the click
            var burst = SensorHub.IdentifyFan(0);
            t.Equal(100f, fan.Software, "the burst puts it at 100%");
            SensorHub.DriveCurveForTest(0, running, gen);   // ...and then wrote with that snapshot
            t.Equal(100f, fan.Software, "the in-flight tick does not overwrite the burst (was: the curve's duty for the rest of the burst)");
            t.Check(burst.Wait(5000), "the burst ends");
            t.Equal((float)at45, fan.Software, "the put-back restores the curve's duty (it carries no generation, so it is never refused)");
            SensorHub.PublishTempsForTest(85, null);
            SensorHub.DriveCurveForTest(0, running, SensorHub.ModeGenForTest);
            t.Equal((float)at85, fan.Software, "after the burst the tick drives the fan again");

            // The tick's other write: the sensor-loss handback, once a curve has
            // had no reading for the grace period.
            SensorHub.PublishTempsForTest(null, null);
            SensorHub.IdentifyBurstMs = 600;
            long gen2 = SensorHub.ModeGenForTest;
            var burst2 = SensorHub.IdentifyFan(0);
            int handbacks = fan.Writes.Count(w => w == "default");
            for (int i = 0; i < 15; i++) SensorHub.DriveCurveForTest(0, running, gen2);   // well past the 10-tick grace, all inside the burst
            t.Equal(handbacks, fan.Writes.Count(w => w == "default"), "no sensor-loss handback lands on a fan mid-burst");
            t.Equal(100f, fan.Software, "it stays at the burst's 100%");
            t.Check(burst2.Wait(5000), "the second burst ends");
            t.Check(fan.Writes.Count(w => w == "default") > handbacks && fan.Mode == ControlMode.Default,
                "the put-back hands it back itself (still no reading)");
        }
        finally { SensorHub.ResetFansForTest(); board.Dispose(); }
    }

    /*-----------------------------------------------------------*\
    | V8: the exit and crash paths handed back only when a fan had |
    | a mode, and Shutdown handed back only the board. A GPU burst |
    | at exit left the card in NvAPI manual at 100% until the next |
    | launch; a queued handback was never retried.                 |
    \*-----------------------------------------------------------*/
    static void V8Gpu(Harness t)
    {
        t.Section("V8: an exit during a GPU Identify burst hands the card back");
        Fresh();
        var card = InstallCard();
        Open(null, gpu: true);
        SensorHub.IdentifyBurstMs = 500;
        var burst = SensorHub.IdentifyFan(SensorHub.GpuFanIndex);
        t.Check(card.Manual && card.Duty == 100, "the burst puts the GPU in manual at 100%");
        t.Check(!SensorHub.AnyControlledFan, "no fan has a mode (the old exit gate skipped the handback on exactly this)");
        t.Check(SensorHub.HandbackOwed, "...but a handback is owed");
        if (SensorHub.HandbackOwed) SensorHub.RestoreAllFans(FanHandback.Exit);   // MainViewModel.Dispose, as it now reads
        t.Check(!card.Manual, "exit hands the GPU back to the driver");
        SensorHub.Shutdown();
        t.Check(!SensorHub.GpuFansControllable, "Shutdown unpublishes the GPU");
        int sets = card.Sets;
        t.Check(!SensorHub.SetFanDuty(SensorHub.GpuFanIndex, 80) && card.Sets == sets, "nothing after Shutdown can write NvAPI");
        t.Check(burst.Wait(5000), "the burst ends");
        t.Check(card.Sets == sets && !card.Manual, "...and its put-back after Shutdown writes nothing either");

        t.Section("V8: Shutdown alone hands back a GPU still in manual");
        Fresh();
        card = InstallCard();
        Open(null, gpu: true);
        var burst2 = SensorHub.IdentifyFan(SensorHub.GpuFanIndex);
        t.Check(card.Manual, "a GPU burst is running");
        SensorHub.Shutdown();   // the crash path's last step, should its RestoreAllFans not run
        t.Check(!card.Manual, "Shutdown hands the GPU back (was: the board fans only)");
        t.Check(burst2.Wait(5000), "the burst ends");
        t.Check(!card.Manual, "and nothing puts it back in manual");
        SensorHub.ResetFansForTest();
    }

    /// <summary>The GPU is handed back at exit or crash only when this
    /// session drove it. A board handback owed now brings an exit through
    /// RestoreAllFans with no GPU mode anywhere, and its unconditional
    /// "auto" would take the coolers from MSI Afterburner (or whatever else
    /// holds them) on a card UnifiedRGB never touched.</summary>
    static void V8GpuOnlyIfOurs(Harness t)
    {
        foreach (var why in new[] { FanHandback.Exit, FanHandback.Crash })
        {
            t.Section($"V8: {why} with only a board handback owed leaves the GPU alone");
            Fresh();
            var card = InstallCard();
            var (board, ctl) = Board("Fan #1", "Fan #2");
            try
            {
                Open(board, gpu: true);
                card.Manual = true; card.Duty = 45;   // another tool holds the coolers at 45%
                int sets = card.Sets, restores = card.Restores;
                t.Check(SensorHub.SetFanDuty(0, 70), "a board fan at 70%");
                ctl["Fan #1"].ThrowOnDefault = true;
                SensorHub.RestoreFan(0);
                ctl["Fan #1"].ThrowOnDefault = false;
                t.Check(SensorHub.HandbackOwed && !SensorHub.AnyControlledFan, "only a board handback is owed");
                if (SensorHub.HandbackOwed) SensorHub.RestoreAllFans(why);   // MainViewModel.Dispose / the crash handler
                t.Check(card.Restores == restores && card.Sets == sets,
                    $"{why}: the GPU is not written (was: set to auto, overriding the other tool)");
                t.Check(card.Manual && card.Duty == 45, $"{why}: the other tool keeps the card at its 45%");
                SensorHub.Shutdown();
                t.Check(card.Restores == restores && card.Sets == sets && card.Manual, $"{why}: nor by Shutdown");
            }
            finally { SensorHub.ResetFansForTest(); board.Dispose(); }

            t.Section($"V8: {why} hands back a GPU this session drove");
            Fresh();
            card = InstallCard();
            Open(null, gpu: true);
            t.Check(SensorHub.SetFanDuty(SensorHub.GpuFanIndex, 70) && card.Manual, "the GPU is driven at 70%");
            if (SensorHub.HandbackOwed) SensorHub.RestoreAllFans(why);
            t.Check(!card.Manual, $"{why}: the card goes back to the driver");
            SensorHub.ResetFansForTest();
        }

        t.Section("V8: the failsafe still hands the GPU back regardless");
        Fresh();
        var held = InstallCard();
        var (b2, _) = Board("Fan #1");
        try
        {
            Open(b2, gpu: true);
            held.Manual = true;   // another tool holds the coolers
            t.Check(SensorHub.SetFanDuty(0, 70), "a board fan at 70%");
            SensorHub.RestoreAllFans(FanHandback.Failsafe);
            t.Check(!held.Manual, "too hot: the GPU goes to the driver's own temperature-following curve");
        }
        finally { SensorHub.ResetFansForTest(); b2.Dispose(); }
    }

    static void V8Pending(Harness t)
    {
        t.Section("V8: a handback still queued is retried at exit");
        Fresh();
        var (board, ctl) = Board("Fan #1", "Fan #2");
        try
        {
            Open(board);
            var fan = ctl["Fan #1"];
            t.Check(SensorHub.SetFanDuty(0, 70), "a board fan at 70%");
            fan.ThrowOnDefault = true;
            SensorHub.RestoreFan(0);
            t.Equal(1, SensorHub.PendingHandbackCount, "its release threw: the handback is queued for the tick");
            t.Check(!SensorHub.AnyControlledFan && SensorHub.HandbackOwed, "no mode is left, but a handback is owed (the old exit gate skipped it)");
            t.Check(fan.Mode == ControlMode.Default, "and LHM now believes the header is the board's: a plain RestoreAll writes nothing");
            fan.ThrowOnDefault = false;
            int released = fan.Writes.Count(w => w == "default");
            if (SensorHub.HandbackOwed) SensorHub.RestoreAllFans(FanHandback.Exit);
            t.Check(fan.Writes.Count(w => w == "default") > released, "exit re-takes and releases it (the tick's forced retry), so the chip is really written");
            t.Equal(0, SensorHub.PendingHandbackCount, "...and nothing is left owed");
        }
        finally { SensorHub.ResetFansForTest(); board.Dispose(); }

        t.Section("V8: a handback that never lands keeps the dirty-start marker");
        Fresh();
        var (board2, ctl2) = Board("Fan #1", "Fan #2");
        try
        {
            Open(board2);
            t.Check(SensorHub.SetFanDuty(0, 70) && File.Exists(SensorHub.FanMarkerFile), "a board fan is ours: the marker is written");
            ctl2["Fan #1"].ThrowOnDefault = true;   // and stays refusing to the end
            SensorHub.RestoreFan(0);
            SensorHub.Shutdown();
            t.Check(ctl2["Fan #1"].Mode == ControlMode.Default, "(LHM's own close believed the header released)");
            t.Check(File.Exists(SensorHub.FanMarkerFile),
                "the marker stays for the next launch to warn about (was: deleted, because LHM's close found nothing in software mode)");
        }
        finally { SensorHub.ResetFansForTest(); board2.Dispose(); SensorHub.MarkBoardControl(false); }
    }

    static void V8Wireless(Harness t, Action<LianLiWireless?> setLian)
    {
        t.Section("V8: a wireless fan mid-Identify at exit goes back on its mode's duty");
        Fresh();
        using (var rx = NewReceiver("V8"))
        {
            setLian(rx);
            int a = SensorHub.LianFanBase, b = SensorHub.LianFanBase + 1;
            try
            {
                SensorHub.IdentifyBurstMs = 500;
                t.Check(SensorHub.SetFanDuty(a, 30), "a wireless fan at 30%");
                var burst = SensorHub.IdentifyFan(a);
                t.Equal(Pwm(100), rx.PwmTargetOfSlot(0), "the burst latches it at 100%");
                SensorHub.RestoreAllFans(FanHandback.Exit);
                t.Equal(Pwm(30), rx.PwmTargetOfSlot(0), "exit puts it back on 30%, where Exit leaves every wireless fan (was: latched at 100%)");
                t.Check(burst.Wait(5000), "the burst ends");

                t.Section("V8 (adjacent): a burst does not cut a wireless fan the failsafe holds at 100%");
                Fresh();
                t.Check(SensorHub.SetFanDuty(b, 30), "a wireless fan at 30%");
                SensorHub.RestoreAllFans(FanHandback.Failsafe);
                t.Equal(Pwm(100), rx.PwmTargetOfSlot(1), "the failsafe puts it at 100%");
                var burst2 = SensorHub.IdentifyFan(b);
                t.Check(burst2.Wait(5000), "a burst on it ends");
                t.Equal(Pwm(100), rx.PwmTargetOfSlot(1), "it stays at the failsafe's 100% (was: the 40% wireless 'auto')");
            }
            finally { SensorHub.ResetFansForTest(); setLian(null); }
        }
    }

    /*-----------------------------------------------------------*\
    | V9: a Cooling row rebuild dropped the old rows with their    |
    | 120 ms debounce timers still running, and a row ignored the  |
    | hub refusing its mode. Run on an STA thread with its own     |
    | dispatcher, so the debounce timers really fire.              |
    \*-----------------------------------------------------------*/
    static void V9(Harness t, Action<LianLiWireless?> setLian)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { V9Rows(t, setLian); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) t.Check(false, $"the V9 row checks threw: {failure}");
    }

    /// <summary>Run this thread's dispatcher for `ms`: the debounce timers fire.</summary>
    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
        stop.Start();
        Dispatcher.PushFrame(frame);
    }

    static void V9Rows(Harness t, Action<LianLiWireless?> setLian)
    {
        static void NoSave(string _, string __) { }
        Fresh();
        using var rx = NewReceiver("V9");
        setLian(rx);
        int a = SensorHub.LianFanBase;
        var settings = new SettingsData();
        var vm = new CoolingViewModel(() => settings, () => { }, () => false, () => false, () => false);
        try
        {
            t.Section("V9: a rebuild after the failsafe never lands an orphaned drag");
            t.Check(SensorHub.SetFanDuty(a, 50), "a wireless fan at 50%");
            vm.UpdateFanModels();
            var row = vm.CoolingFans.Single(r => r.Index == a);
            t.Check(row.IsManual && row.DutyPercent == 50, "its row shows Manual 50%");
            row.DutyPercent = 70;
            t.Check(row.HasPendingDuty, "a drag waits on its debounce");
            SensorHub.RestoreAllFans(FanHandback.Failsafe);
            vm.UpdateFanModels();   // the rebuild the failsafe's mode-epoch bump causes
            t.Check(!row.HasPendingDuty, "the rebuild stops the old row's timer (was: left running on a row nobody shows)");
            t.Check(SensorHub.FailsafeTripped && SensorHub.ManualFanDuty(a) == null && SensorHub.SuspendedFanDuty(a) == 50,
                "the drag is not applied to a fan the failsafe suspended (was: re-armed 120 ms later, FailsafeTripped cleared)");
            t.Check(vm.CoolingFans.Single(r => r.Index == a).IsSuspended, "the new row shows the fan paused");
            Pump(300);
            t.Check(SensorHub.FailsafeTripped && SensorHub.ManualFanDuty(a) == null, "and nothing lands later either");

            t.Section("V9: a rebuild with a drag pending applies it");
            t.Check(SensorHub.SetFanDuty(a, 50), "the fan is re-armed at 50%");
            SensorHub.ReloadFanConfig();   // the hub's epoch moves (here, with the modes left as they are)
            vm.UpdateFanModels();
            var row2 = vm.CoolingFans.Single(r => r.Index == a);
            t.Check(row2.IsManual && row2.DutyPercent == 50, "the rebuilt row shows Manual 50%");
            row2.DutyPercent = 75;
            SensorHub.ReloadFanConfig();
            vm.UpdateFanModels();
            t.Equal((int?)75, SensorHub.ManualFanDuty(a), "the pending drag lands at the rebuild (not lost)");
            t.Equal(75, vm.CoolingFans.Single(r => r.Index == a).DutyPercent, "and the new row shows it");
            t.Check(!row2.HasPendingDuty, "the old row has nothing left pending");

            t.Section("V9: a debounce that fires after the failsafe does not re-arm the fan");
            t.Check(SensorHub.SetFanDuty(a, 50), "the fan at 50%");
            var row3 = new FanRowModel(a, true, "Fan", "fan:Fan", null, "Manual", 50, null, TempSource.Hottest, NoSave, minDuty: 20);
            row3.DutyPercent = 85;
            SensorHub.RestoreAllFans(FanHandback.Failsafe);
            Pump(300);   // the row's own 120 ms timer fires in here
            t.Check(SensorHub.FailsafeTripped && SensorHub.ManualFanDuty(a) == null,
                "the late slider value is dropped (was: SetFanDuty re-armed the fan and cleared FailsafeTripped)");
            t.Check(row3.IsSuspended && !row3.IsManual, "the row shows the failsafe's pause instead of the drag");

            t.Section("V9: a row shows the hub's refusal, not the mode it asked for");
            t.Check(SensorHub.SetFanDuty(a, 50), "the fan at 50%");
            var row4 = new FanRowModel(a, true, "Fan", "fan:Fan", null, "Manual", 50, null, TempSource.Hottest, NoSave, minDuty: 20);
            setLian(null);   // the receiver is mid-rescan: the fan cannot be driven
            row4.Mode = "Quiet";
            t.Check(row4.Mode == "Manual" && row4.IsManual && row4.DutyPercent == 50,
                $"a curve the hub refused is not shown as running: the row shows the hub's Manual 50% (was: 'Quiet'; now {row4.ModeSummary})");
            setLian(rx);
            var row5 = new FanRowModel(6, true, "Fan 7", "fan:Fan 7", null, "Auto", 50, null, TempSource.Hottest, NoSave);
            row5.Mode = "Manual";   // a board header, and no board open
            t.Check(row5.IsAuto && row5.Mode == "Auto", "a duty the hub refused leaves the row on Auto");
        }
        finally { SensorHub.ResetFansForTest(); setLian(null); }
    }

    /*-----------------------------------------------------------*\
    | V10: a re-open (ResetSources after a PawnIO install) kept    |
    | the board modes keyed to the OLD enumeration, and re-applied |
    | the config it read before an Auto clicked during the open.   |
    \*-----------------------------------------------------------*/
    static void V10Pure(Harness t)
    {
        t.Section("V10: what a re-open reconciles against");
        var file = new Dictionary<string, SensorHub.FanConfigEntry>(StringComparer.Ordinal)
        {
            ["Fan #1"] = Curve("Fan #1"), ["Fan #2"] = Manual("Fan #2", 50), ["GPU"] = Manual("GPU", 40),
            ["LianLi:0"] = Manual("LianLi:0", 30), ["Fan #9"] = Manual("Fan #9", 70),
        };
        var running = new Dictionary<string, SensorHub.FanConfigEntry>(StringComparer.Ordinal) { ["Fan #1"] = Manual("Fan #1", 55) };
        var decided = new[] { "Fan #1", "Fan #2", "GPU", "LianLi:0" };
        var suspended = new Dictionary<string, SensorHub.FanConfigEntry>(StringComparer.Ordinal) { ["GPU"] = Manual("GPU", 40) };
        var r = SensorHub.SessionConfig(file, running, decided, suspended, out bool changed)!;
        t.Check(r["Fan #1"].Kind == "manual" && r["Fan #1"].Pct == 55, "a decided fan gets the mode the session is running, not the file's");
        t.Check(!r.ContainsKey("Fan #2"), "a decided fan with no mode is on Auto: the file's entry goes");
        t.Check(r["GPU"].Pct == 40, "a suspended fan keeps the file's entry (the reconcile leaves it suspended)");
        t.Check(r["LianLi:0"].Pct == 30, "wireless entries are left to ReconcileLian");
        t.Check(r["Fan #9"].Pct == 70, "an undecided fan gets the file");
        t.Check(changed, "the change is reported, so the open saves it");
        var same = SensorHub.SessionConfig(file, new Dictionary<string, SensorHub.FanConfigEntry>(), Array.Empty<string>(),
            new Dictionary<string, SensorHub.FanConfigEntry>(), out bool unchanged)!;
        t.Check(!unchanged && same.Count == file.Count, "nothing decided (the first open): the file as it is, and no save");
        t.Check(SensorHub.SessionConfig(null, running, decided, suspended, out _) == null, "an unreadable file stays unreadable (null)");
    }

    static void V10Reopen(Harness t)
    {
        t.Section("V10: a re-open re-derives the board modes by name, not by index");
        Fresh();
        var (first, _) = Board("Fan #1", "Fan #2", "Fan #3");
        var (second, c2) = Board("Fan #3", "Fan #1", "Fan #2");   // LHM numbers the headers differently this time
        try
        {
            WriteCfg(Curve("Fan #1"), Manual("Fan #3", 60));
            SensorHub.PublishTempsForTest(50, null);
            Open(first);
            t.Check(SensorHub.FanCurveOf(0) != null && SensorHub.ManualFanDuty(2) == 60, "first open: Fan #1 on its curve (index 0), Fan #3 at 60% (index 2)");
            Reopen(second);
            t.Equal((int?)60, SensorHub.ManualFanDuty(0), "Fan #3, now index 0, keeps its 60%");
            t.Equal(60f, c2["Fan #3"].Software, "...on its own header");
            t.Check(SensorHub.FanCurveOf(1) != null, "Fan #1, now index 1, keeps its curve");
            t.Check(SensorHub.ManualFanDuty(2) == null && SensorHub.FanCurveOf(2) == null,
                "Fan #2, now at index 2, gets no mode (was: Fan #3's 60%, left on the old index for the tick to drive)");
            t.Check(!c2["Fan #2"].Writes.Any(w => w.StartsWith("sw:", StringComparison.Ordinal)), "...and its header is never driven");
            t.Check(SensorHub.SetFanDuty(0, 65), "Fan #3 is set to 65%");
            var saved = ReadCfg();
            t.Check(saved.TryGetValue("Fan #3", out var f3) && f3.Pct == 65
                 && saved.TryGetValue("Fan #1", out var f1) && f1.Kind == "curve" && !saved.ContainsKey("Fan #2"),
                "a save files every mode under the fan that has it (was: the stale 60% saved under 'Fan #2')");
        }
        finally { SensorHub.ResetFansForTest(); first.Dispose(); second.Dispose(); }

        t.Section("V10: an Auto clicked while the board re-opens is not undone");
        Fresh();
        var (before, _) = Board("Fan #1", "Fan #2", "Fan #3");
        var (after, ca) = Board("Fan #1", "Fan #2", "Fan #3");
        try
        {
            WriteCfg(Curve("Fan #1"), Manual("Fan #3", 60));
            SensorHub.PublishTempsForTest(50, null);
            Open(before);
            SensorHub.ResetSources();
            var atRead = ReadCfg();   // the open reads the file here...
            int reloads = SensorHub.FanConfigReloadsForTest;
            SensorHub.ReleaseBoardForTest();   // ...closes the old board, and for seconds there is none
            SensorHub.RestoreFan(2);           // the user puts Fan #3 on Auto in that window
            t.Check(ReadCfg().ContainsKey("Fan #3"), "(its save kept the file entry: no board, so the fan is not addressable)");
            SensorHub.PublishForTest(after, IntPtr.Zero, false, atRead, reloads);
            t.Check(SensorHub.ManualFanDuty(2) == null && SensorHub.FanCurveOf(2) == null,
                "Fan #3 stays on Auto after the open (was: the open re-applied the 60% it had read before the click)");
            t.Check(!ca["Fan #3"].Writes.Any(w => w.StartsWith("sw:", StringComparison.Ordinal)), "its header is never driven");
            t.Check(!ReadCfg().ContainsKey("Fan #3"), "and the file no longer brings it back at the next launch");
            t.Check(SensorHub.FanCurveOf(0) != null, "Fan #1 keeps its curve");
        }
        finally { SensorHub.ResetFansForTest(); before.Dispose(); after.Dispose(); }

        t.Section("V10: a GPU mode set while the board re-opens is not undone");
        Fresh();
        var card = InstallCard();
        try
        {
            WriteCfg(Manual("GPU", 40));
            Open(null, gpu: true);
            t.Check(SensorHub.ManualFanDuty(SensorHub.GpuFanIndex) == 40 && card.Duty == 40, "first open: the GPU at its saved 40%");
            SensorHub.ResetSources();
            var atRead = ReadCfg();
            int reloads = SensorHub.FanConfigReloadsForTest;
            SensorHub.ReleaseBoardForTest();
            t.Check(SensorHub.SetFanDuty(SensorHub.GpuFanIndex, 80), "the user sets the GPU to 80% during the open");
            SensorHub.PublishForTest(null, FakeCard, true, atRead, reloads);
            t.Equal((int?)80, SensorHub.ManualFanDuty(SensorHub.GpuFanIndex), "the GPU keeps 80% after the open (was: the 40% read before)");
            t.Equal(80, card.Duty, "...on the card");
        }
        finally { SensorHub.ResetFansForTest(); }

        t.Section("V10: a fan missing at one re-open keeps its saved mode for the next");
        Fresh();
        var (all, _) = Board("Fan #1", "Fan #2");
        var (partial, _) = Board("Fan #2");
        var (back, _) = Board("Fan #1", "Fan #2");
        try
        {
            WriteCfg(Curve("Fan #1"));
            SensorHub.PublishTempsForTest(50, null);
            Open(all);
            t.Check(SensorHub.FanCurveOf(0) != null, "Fan #1 on its curve");
            Reopen(partial);
            t.Check(SensorHub.SetFanDuty(0, 50), "Fan #2 (alone this time) is set to 50%");
            t.Check(ReadCfg().TryGetValue("Fan #1", out var kept) && kept.Kind == "curve", "the missing fan's curve survives that save");
            Reopen(back);
            t.Check(SensorHub.FanCurveOf(0) != null && SensorHub.ManualFanDuty(1) == 50,
                "...and both fans are back on their modes when it returns (was: Fan #1 put on Auto as 'decided')");
        }
        finally { SensorHub.ResetFansForTest(); all.Dispose(); partial.Dispose(); back.Dispose(); }
    }

    /// <summary>ResetSources, then the open's two halves with the file read
    /// in between, as OpenSourcesOnce does it.</summary>
    static void Reopen(LhmFans board)
    {
        SensorHub.ResetSources();
        var atRead = ReadCfg();
        int reloads = SensorHub.FanConfigReloadsForTest;
        SensorHub.ReleaseBoardForTest();
        SensorHub.PublishForTest(board, IntPtr.Zero, false, atRead, reloads);
    }

    /// <summary>The two call sites the harness cannot run (the app's exit and
    /// its crash handler): pinned in the source.</summary>
    static void CallSites(Harness t)
    {
        t.Section("V8: the exit and crash paths ask HandbackOwed");
        string? vm = SourceOf("src/UnifiedRgb.App/MainViewModel.cs");
        string? app = SourceOf("src/UnifiedRgb.App/App.xaml.cs");
        if (vm == null || app == null) { t.Skip("the call-site checks need the source tree"); return; }
        static bool Gated(string src, string why) => System.Text.RegularExpressions.Regex.IsMatch(src,
            @"if \(UnifiedRgb\.Core\.Sensors\.SensorHub\.HandbackOwed\)\s*UnifiedRgb\.Core\.Sensors\.SensorHub\.RestoreAllFans\(UnifiedRgb\.Core\.Sensors\.FanHandback\." + why);
        t.Check(Gated(vm, "Exit"), "exit hands back whenever anything is owed (was: only with a mode set)");
        t.Check(Gated(app, "Crash"), "so does the crash handler");
    }
}

/// <summary>A fan header's LHM control: remembers the mode and duty, and can
/// refuse a release the way LHM does (the mode flips to Default BEFORE its
/// change event writes the chip, so a write that throws leaves it saying
/// Default with the header still on our duty).</summary>
internal class FakeLhmControl : DispatchProxy
{
    public ControlMode Mode = ControlMode.Undefined;
    public float Software;
    public bool ThrowOnDefault;
    public readonly List<string> Writes = new();

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method?.Name)
        {
            case "get_ControlMode": return Mode;
            case "get_SoftwareValue": return Software;
            case "SetSoftware":
                Mode = ControlMode.Software;
                Software = (float)args![0]!;
                Writes.Add($"sw:{Software}");
                return null;
            case "SetDefault":
                Mode = ControlMode.Default;
                if (ThrowOnDefault) throw new IOException("the Super-I/O write failed");
                Writes.Add("default");
                return null;
        }
        return method != null && method.ReturnType.IsValueType && method.ReturnType != typeof(void)
            ? Activator.CreateInstance(method.ReturnType) : null;
    }
}

/// <summary>A fan's tach: nothing in these checks reads it.</summary>
internal class FakeLhmSensor : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method != null && method.ReturnType.IsValueType && method.ReturnType != typeof(void)
            ? Activator.CreateInstance(method.ReturnType) : null;
}

/// <summary>The GPU's coolers behind the hub's NvAPI seam.</summary>
internal sealed class FakeCardFans
{
    public bool Manual;
    public int Duty, Sets, Restores;

    public bool SetDuty(IntPtr gpu, int percent) { Sets++; Manual = true; Duty = percent; return true; }
    public bool RestoreAuto(IntPtr gpu) { Restores++; Manual = false; return true; }
    public bool? IsManual(IntPtr gpu) => Manual;
}

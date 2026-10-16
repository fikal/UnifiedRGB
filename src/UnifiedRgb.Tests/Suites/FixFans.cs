using System.IO;
using System.Runtime.CompilerServices;
using UnifiedRgb.App;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Sensors;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Fan control fixes from the 2026-10-14 review (B1-B11, F13).  |
|                                                              |
| SensorHub drives real hardware, so nothing here starts it:   |
| every check is on a pure seam the hub itself calls (the      |
| config merge, the suspend bookkeeping, Hottest, the source   |
| and stall watches), on the Cooling row model, or on files in |
| the harness's private LocalDir. The row checks call the      |
| hub's public setters, so the hub is shut down first: after   |
| that no Touch can start its timer, and with it LHM's ring0   |
| driver and NvAPI writes, inside the harness.                 |
\*-----------------------------------------------------------*/
static class FixFansSuite
{
    static SensorHub.FanConfigEntry Manual(string name, int pct) => new(name, "manual", pct, null);
    static SensorHub.FanConfigEntry Curve(string name, string preset = "Quiet")
        => new(name, "curve", 0, FanCurve.Preset_(preset, TempSource.Cpu));

    public static void Run(Harness t)
    {
        SensorHub.Shutdown();   // see the header: the hub must never open its sources in the harness

        t.Section("B1: the failsafe suspends modes instead of erasing them");
        {
            var names = new Dictionary<int, string>
            {
                [0] = "Fan #1", [1] = "Fan #2", [SensorHub.GpuFanIndex] = "GPU", [SensorHub.LianFanBase + 1] = "LianLi:2",
            };
            string? NameOf(int i) => names.TryGetValue(i, out var n) ? n : null;
            var manual = new Dictionary<int, int> { [0] = 55, [SensorHub.GpuFanIndex] = 40 };
            var curves = new Dictionary<int, FanCurve>
            {
                [1] = FanCurve.Preset_("Quiet", TempSource.Cpu),
                [SensorHub.LianFanBase + 1] = FanCurve.Preset_("High", TempSource.Hottest, 20),
                [7] = FanCurve.Preset_("Full"),   // a fan this session cannot name
            };
            var fileBefore = SensorHub.LiveEntries(manual, curves, new Dictionary<string, SensorHub.FanConfigEntry>(), NameOf);
            var suspended = new Dictionary<string, SensorHub.FanConfigEntry>(StringComparer.Ordinal);
            var unnamed = new List<int>();
            SensorHub.SuspendModes(manual, curves, suspended, NameOf, unnamed);

            t.Check(manual.Count == 0 && curves.Count == 0, "suspending clears the active modes (the fans go to auto)");
            t.Equal(4, suspended.Count, "every named mode is kept");
            t.Check(suspended["Fan #1"].Kind == "manual" && suspended["Fan #1"].Pct == 55, "a manual duty is kept as manual, with its duty");
            t.Check(suspended["Fan #2"].Kind == "curve" && suspended["Fan #2"].Curve?.Preset == "Quiet", "a curve is kept as a curve");
            t.Check(unnamed.SequenceEqual(new[] { 7 }), "a fan with no saved name is reported, not silently lost");

            // The next edit after the failsafe: one fan gets a new duty. This
            // used to save ONLY that fan, erasing the other three.
            var known = new HashSet<string>(StringComparer.Ordinal) { "Fan #1", "Fan #2", "GPU", "LianLi:2" };
            manual[0] = 60; suspended.Remove("Fan #1");
            var live = SensorHub.LiveEntries(manual, curves, suspended, NameOf);
            var saved = SensorHub.MergeFanConfig(fileBefore, live, n => known.Contains(n));
            t.Equal(4, saved.Count, "an edit after the failsafe still saves every fan");
            t.Check(saved.Single(e => e.Name == "Fan #1").Pct == 60, "the edited fan saves its new duty");
            t.Check(saved.Any(e => e.Name == "LianLi:2" && e.Kind == "curve"), "a suspended curve is still written");

            // Auto on one fan drops that fan only, and the file is not emptied
            // (an empty save deletes it).
            suspended.Remove("Fan #2");
            var afterAuto = SensorHub.MergeFanConfig(saved, SensorHub.LiveEntries(manual, curves, suspended, NameOf), n => known.Contains(n));
            t.Check(afterAuto.Count == 3 && afterAuto.All(e => e.Name != "Fan #2"), "Auto on one fan drops only that fan's entry");
        }

        t.Section("B3: a save keeps the modes of fans this session has not loaded");
        {
            var file = new[] { Curve("Fan #1"), Manual("GPU", 45), Curve("LianLi:0", "High"), Manual("Fan #3", 70) };
            // LHM failed to open, NvAPI was not ready, the dongle came up late:
            // none of those fans is decided this session. Then one wireless
            // fan is edited, and the save used to write only that one.
            var live = new List<SensorHub.FanConfigEntry> { Manual("LianLi:1", 50) };
            var merged = SensorHub.MergeFanConfig(file, live, _ => false);
            t.Equal(5, merged.Count, "every undecided entry rides through a save");
            t.Check(merged.Any(e => e.Name == "Fan #1") && merged.Any(e => e.Name == "GPU") && merged.Any(e => e.Name == "LianLi:0"),
                "board, GPU and wireless entries all survive");
            // A fan this session decided (and can drive) with no mode is on Auto:
            // its old entry goes.
            var decided = SensorHub.MergeFanConfig(file, live, n => n == "Fan #3");
            t.Check(decided.All(e => e.Name != "Fan #3") && decided.Count == 4, "a decided fan with no mode loses its entry");
            // Live wins over the file for the same name.
            var over = SensorHub.MergeFanConfig(file, new List<SensorHub.FanConfigEntry> { Manual("GPU", 80) }, _ => false);
            t.Check(over.Count(e => e.Name == "GPU") == 1 && over.Single(e => e.Name == "GPU").Pct == 80, "the live mode replaces the saved one");

            // What is written reads back the same.
            string json = System.Text.Json.JsonSerializer.Serialize(new { fans = merged });
            var back = SensorHub.ParseFanConfig(json);
            t.Equal(5, back.Count, "fan-config.json round-trips every entry");
            t.Check(back["LianLi:0"].Curve?.Points.Count == FanCurve.Preset_("High").Points.Count && back["GPU"].Pct == 45,
                "curves and duties round-trip");
        }

        t.Section("residual: a saved curve with no points is refused");
        {
            t.Check(SensorHub.EntryProblem(Curve("Fan #1")) == null, "a real curve is applied");
            t.Check(SensorHub.EntryProblem(Manual("Fan #1", 50)) == null, "a manual duty is applied");
            t.Check(SensorHub.EntryProblem(new SensorHub.FanConfigEntry("Fan #1", "curve", 0, null)) != null, "a curve entry with no curve is refused");
            t.Check(SensorHub.EntryProblem(new SensorHub.FanConfigEntry("Fan #1", "curve", 0, new FanCurve())) != null,
                "an empty curve is refused (it used to pin the fan at the 30% floor at any temperature)");
            t.Check(SensorHub.EntryProblem(new SensorHub.FanConfigEntry("Fan #1", "turbo", 0, null)) != null, "an unknown kind is refused");
            var parsed = SensorHub.ParseFanConfig("{\"fans\":[{\"Name\":\"Fan #1\",\"Kind\":\"curve\",\"Pct\":0,\"Curve\":{\"Preset\":\"x\",\"Points\":null}}]}");
            t.Check(SensorHub.EntryProblem(parsed["Fan #1"]) != null, "a hand-edited \"Points\": null is refused");
            t.Check(!SensorHub.SetFanCurve(0, new FanCurve()), "SetFanCurve refuses an empty curve too");
        }

        t.Section("B2: Hottest does not quietly become GPU-only");
        {
            t.Equal<double?>(null, SensorHub.HottestOf(null, true, 60, true), "an opened CPU that reads nothing makes Hottest null (the grace handback applies)");
            t.Equal<double?>(60, SensorHub.HottestOf(null, false, 60, true), "no CPU source at all: GPU alone, as before");
            t.Equal<double?>(null, SensorHub.HottestOf(70, true, null, true), "a live GPU that stops reading makes it null too");
            t.Equal<double?>(70, SensorHub.HottestOf(70, true, null, false), "no GPU source: CPU alone");
            t.Equal<double?>(80, SensorHub.HottestOf(70, true, 80, true), "both reading: the hotter one");
            t.Equal<double?>(null, SensorHub.HottestOf(null, false, null, false), "nothing at all: null");

            var w = new SensorHub.SourceWatch();
            t.Equal(SensorHub.SourceWatch.Change.None, w.Observe(true, true), "a reading source is quiet");
            t.Equal(SensorHub.SourceWatch.Change.None, w.Observe(true, false), "one missed read is not worth a line");
            t.Equal(SensorHub.SourceWatch.Change.Lost, w.Observe(true, false), "two in a row: lost, said once");
            var seen = new List<SensorHub.SourceWatch.Change>();
            for (int i = 3; i <= 60; i++) seen.Add(w.Observe(true, false));
            t.Check(!seen.Contains(SensorHub.SourceWatch.Change.Lost), "lost is said only once");
            t.Equal(2, seen.Count(c => c == SensorHub.SourceWatch.Change.Reopen), "re-open at the grace period, then about once a minute");
            t.Equal(SensorHub.SourceWatch.Change.Reopen, seen[SensorHub.SourceWatch.FirstReopenAt - 3], "the first re-open lands at the grace period");
            t.Equal(SensorHub.SourceWatch.Change.Back, w.Observe(true, true), "a reading after a loss: back, said once");
            t.Equal(SensorHub.SourceWatch.Change.None, w.Observe(true, true), "and quiet after that");
            var never = new SensorHub.SourceWatch();
            bool anyNoise = false;
            for (int i = 0; i < 60; i++) anyNoise |= never.Observe(false, false) != SensorHub.SourceWatch.Change.None;
            t.Check(!anyNoise, "a source that never opened is never reported lost or re-opened");

            t.Equal<string?>(null, SensorHub.BlindSpot(false, false, true, false), "no fan control: nothing to warn about");
            t.Equal<string?>("CPU", SensorHub.BlindSpot(true, false, false, false), "fan control with no CPU reading: warned");
            t.Equal<string?>("GPU", SensorHub.BlindSpot(true, true, true, false), "a live GPU that stopped reading: warned");
            t.Equal<string?>(null, SensorHub.BlindSpot(true, true, false, false), "a machine with no GPU reading to lose: no warning");
            t.Equal<string?>("CPU and GPU", SensorHub.BlindSpot(true, false, true, false), "both blind: both named");
        }

        t.Section("residual: a stuck tick is reported, not silent");
        {
            var s = new SensorHub.StallWatch(20_000);
            t.Equal<long?>(null, s.Stalled(5_000), "no tick in flight: not stalled");
            s.Begin(1_000);
            t.Equal<long?>(null, s.Stalled(15_000), "a slow tick under the threshold is not reported");
            t.Equal<long?>(25_000, s.Stalled(26_000), "a tick in flight past the threshold is reported with its age");
            t.Equal<long?>(25_000, s.End(26_000), "its end is reported too (fan control running again)");
            t.Equal<long?>(null, s.Stalled(90_000), "and nothing is stalled after it ends");
            s.Begin(100_000);
            t.Equal<long?>(null, s.End(101_500), "an ordinary tick ends without a line");
        }

        t.Section("B5: the ISA bus mutex is held around LHM's writes");
        {
            // A private name, never the real Global\Access_ISABUS.HTP.Method:
            // the harness must not hold up HWiNFO or the running app's sweep.
            string name = $@"Local\UnifiedRgbTests-isa-{Guid.NewGuid():N}";
            using var isa = new Mutex(false, name);
            bool ran = false;
            t.Check(LhmFans.WithIsaBus(null, 50, () => ran = true) && ran, "no mutex on the system: the write runs, as it always did");

            bool lhmWait = false;
            bool ok = LhmFans.WithIsaBus(isa, 300, () =>
            {
                using var lhmSide = new Mutex(false, name);   // LHM opens its own handle to the same mutex
                lhmWait = lhmSide.WaitOne(10);                // and waits 10 ms, on this thread
                if (lhmWait) lhmSide.ReleaseMutex();
                return true;
            });
            t.Check(ok && lhmWait, "with the mutex held here, LHM's own 10 ms wait succeeds (Windows mutexes are recursive per thread)");

            using var holding = new ManualResetEventSlim();
            using var letGo = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                using var other = new Mutex(false, name);
                other.WaitOne();
                holding.Set();
                letGo.Wait(5000);
                other.ReleaseMutex();
            }) { IsBackground = true };
            holder.Start();
            holding.Wait(5000);
            bool wrote = false;
            bool busy = LhmFans.WithIsaBus(isa, 50, () => wrote = true);
            t.Check(!busy && !wrote, "another monitor holding the bus: nothing is written and the write reports NOT landed (the hub retries)");
            letGo.Set();
            holder.Join(5000);

            bool freeAfter = false;
            var probe = new Thread(() =>
            {
                using var other = new Mutex(false, name);
                freeAfter = other.WaitOne(200);
                if (freeAfter) other.ReleaseMutex();
            });
            LhmFans.WithIsaBus(isa, 300, () => true);
            probe.Start(); probe.Join(5000);
            t.Check(freeAfter, "the mutex is released after the write");
        }

        t.Section("B7: the dirty-start marker");
        {
            string marker = SensorHub.FanMarkerFile;
            t.Check(marker.StartsWith(Isolation.Root, StringComparison.OrdinalIgnoreCase), "the marker lives in the harness's private LocalDir");
            File.WriteAllText(marker, "previous run");
            UnifiedRgb.Core.Automation.ActivityLog.Shared.Clear();
            SensorHub.NoteDirtyStart();
            t.Check(!File.Exists(marker), "a previous run's marker is consumed at the first open");
            t.Check(UnifiedRgb.Core.Automation.ActivityLog.Shared.Snapshot().Any(n => n.Kind == UnifiedRgb.Core.Automation.ActivityKind.Problem),
                "and the unclean shutdown is said");
            SensorHub.MarkBoardControl(true);
            UnifiedRgb.Core.Automation.ActivityLog.Shared.Clear();
            SensorHub.NoteDirtyStart();
            t.Check(File.Exists(marker), "a re-open does not take this run's own live marker for a crash (once per process)");
            t.Check(!UnifiedRgb.Core.Automation.ActivityLog.Shared.Snapshot().Any(), "and says nothing");
            SensorHub.MarkBoardControl(false);
            t.Check(!File.Exists(marker), "a clean handback clears it");
            UnifiedRgb.Core.Automation.ActivityLog.Shared.Clear();
        }

        t.Section("B1: a suspended Cooling row shows auto, and re-picking re-arms it");
        // Wireless fans on a fake receiver: a row whose mode the hub REFUSES
        // now shows the refusal (InvariantFans, V9), so re-arming has to land
        // on a fan the hub can actually drive. Board indices could not, here.
        var rowInstance = typeof(UnifiedRgb.Core.Devices.LianLiWireless).GetProperty("Instance",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;
        object? rowPrior = rowInstance.GetValue(null);
        int rowSeedWait = UnifiedRgb.Core.Devices.LianLiWireless.PwmSeedWaitMs;
        UnifiedRgb.Core.Devices.LianLiWireless.PwmSeedWaitMs = 0;
        var rowRx = NewReceiver("B1rows");
        rowInstance.SetValue(null, rowRx);
        try
        {
            static void NoSave(string _, string __) { }
            int rowA = SensorHub.LianFanBase, rowB = SensorHub.LianFanBase + 1;
            var row = new FanRowModel(rowA, true, "Fan", "fan:Fan", null, "Quiet", 50,
                FanCurve.Preset_("Quiet", TempSource.Cpu), TempSource.Cpu, NoSave, suspended: true);
            t.Check(row.IsSuspended && row.Mode == null, "a suspended row lights no profile pill (none is running)");
            t.Check(row.IsAuto && !row.IsCurve && !row.IsManual, "its editor says the board has the fan, not a curve that is not running");
            t.Check(row.ModeSummary.StartsWith("Paused by failsafe", StringComparison.Ordinal) && row.ModeSummary.Contains("Quiet"),
                $"its summary says what is paused ({row.ModeSummary})");
            var notes = new List<string>();
            row.PropertyChanged += (_, e) => notes.Add(e.PropertyName ?? "");
            row.Mode = "Quiet";   // the mode it already had: the setter used to return early on an equal value
            t.Check(!row.IsSuspended && row.Mode == "Quiet" && row.IsCurve, "re-picking the kept profile re-arms the row");
            t.Check(notes.Contains(nameof(FanRowModel.IsSuspended)) && notes.Contains(nameof(FanRowModel.Mode)) && notes.Contains(nameof(FanRowModel.ModeSummary)),
                "and the row repaints");
            notes.Clear();
            row.Mode = "Quiet";
            t.Check(notes.Count == 0, "an armed row still ignores re-setting its own mode");

            var man = new FanRowModel(rowB, true, "Fan 2", "fan:Fan 2", null, "Manual", 65, null, TempSource.Hottest, NoSave, minDuty: 20, suspended: true);
            t.Equal("Paused by failsafe · Manual · 65%", man.ModeSummary, "a paused manual duty shows its duty");
            man.Mode = "Manual";
            t.Check(!man.IsSuspended && man.IsManual && man.DutyPercent == 65, "re-picking Manual re-arms the kept duty");

            var gpu = new FanRowModel(SensorHub.GpuFanIndex, true, "GPU fans", "fan:GPU", null, "Quiet", 50,
                FanCurve.Preset_("Quiet", TempSource.Gpu, 0), TempSource.Gpu, NoSave, curveFloor: 0, suspended: true);
            gpu.Mode = "Auto";
            t.Check(!gpu.IsSuspended && gpu.Mode == "Auto" && gpu.IsAuto, "picking Auto on a paused row drops the paused mode");

            var auto = new FanRowModel(5, true, "Fan 3", "fan:Fan 3", null, "Auto", 50, null, TempSource.Hottest, NoSave, suspended: true);
            t.Check(!auto.IsSuspended && auto.Mode == "Auto", "an Auto row is never shown as paused");
            SensorHub.RestoreFan(rowA); SensorHub.RestoreFan(rowB);
        }
        finally
        {
            rowInstance.SetValue(null, rowPrior);
            rowRx.Dispose();
            UnifiedRgb.Core.Devices.LianLiWireless.PwmSeedWaitMs = rowSeedWait;
            File.Delete(AppPaths.Local("fan-config.json"));
            SensorHub.ReloadFanConfig();   // leave the hub holding nothing the rows saved
        }

        t.Section("B4: the crash path reads the current wireless hand-off choice");
        {
            var first = new SettingsData { LianHandoffOnExit = false };
            SettingsData current = first;
            try
            {
                t.Check(!CoolingViewModel.HandOffWirelessOnCrash, "no settings handed over yet: the crash path does not hand off");
                _ = new CoolingViewModel(() => new SettingsData { LianHandoffOnExit = true }, () => { }, () => false, () => false, () => false);
                t.Check(!CoolingViewModel.HandOffWirelessOnCrash, "constructing a view model does not hijack the crash path (harness-built ones did)");
                CoolingViewModel.UseForCrashPath(() => current);
                t.Check(!CoolingViewModel.HandOffWirelessOnCrash, "hand-off off: the crash path puts the driven wireless fans at 100%");
                first.LianHandoffOnExit = true;
                t.Check(CoolingViewModel.HandOffWirelessOnCrash, "hand-off on: the crash path follows it like a clean exit");
                current = new SettingsData();
                t.Check(!CoolingViewModel.HandOffWirelessOnCrash, "an import that replaces the settings object is followed, not a stale copy");
            }
            finally { CoolingViewModel.UseForCrashPath(null); }
        }

        t.Section("F13: the hub re-reads fan-config.json after an import");
        {
            string path = AppPaths.Local("fan-config.json");
            t.Check(path.StartsWith(Isolation.Root, StringComparison.OrdinalIgnoreCase), "fan-config.json lives in the harness's private LocalDir");
            var imported = new List<SensorHub.FanConfigEntry> { Curve("Imported A"), Manual("Imported B", 60) };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { fans = imported }));
            long epoch = SensorHub.ModeEpoch;
            SensorHub.ReloadFanConfig();   // the hub is not open here: it re-reads and touches no hardware
            t.Check(SensorHub.SavedFanNames.SequenceEqual(new[] { "Imported A", "Imported B" }), "the imported entries replace what the hub held");
            t.Check(SensorHub.ModeEpoch > epoch, "the Cooling rows are told to rebuild");
            File.Delete(path);
            SensorHub.ReloadFanConfig();   // leave the hub empty for whatever runs next, not holding the imported entries
            t.Check(!SensorHub.SavedFanNames.Any(), "...and with the file gone again, the hub holds nothing");
        }

        t.Section("B9: one handback enum; the write probes left Core");
        {
            t.Check(Enum.GetNames<FanHandback>().SequenceEqual(new[] { "Exit", "Crash", "Failsafe" }), "FanHandback is Exit / Crash / Failsafe");
            t.Check(typeof(SensorHub).GetMethod(nameof(SensorHub.RestoreAllFans), new[] { typeof(FanHandback), typeof(bool) }) != null,
                "RestoreAllFans takes the enum");
            var core = typeof(SensorHub).Assembly;
            t.Check(core.GetType("UnifiedRgb.Core.Sensors.GigabyteEcio") == null && core.GetType("UnifiedRgb.Core.Sensors.GigabyteIsaBridge") == null,
                "the Gigabyte fan-firmware write probes no longer ship in the elevated app's Core");
        }

        ThroughTheHub(t);
        CallSites(t);

        t.Section("B8: the failsafe runs before the per-fan writes, which are isolated");
        {
            string? src = SourceOf("src/UnifiedRgb.Core/Sensors/SensorHub.cs");
            if (src == null) { t.Skip("the source-order check needs the source tree"); return; }
            int core = src.IndexOf("static void TickCore()", StringComparison.Ordinal);
            int end = src.IndexOf("static bool DriveCurve(", core, StringComparison.Ordinal);
            string body = core >= 0 && end > core ? src[core..end] : "";
            int failsafe = body.IndexOf("RestoreAllFans(FanHandback.Failsafe)", StringComparison.Ordinal);
            int firstWrite = body.IndexOf("DriveCurve(kv.Key", StringComparison.Ordinal);
            t.Check(failsafe > 0 && firstWrite > 0 && failsafe < firstWrite,
                "the failsafe is evaluated before the curve loop (a throwing fan used to skip it every tick)");
            t.Check(body.Contains("try { if (!DriveCurve(kv.Key, kv.Value, gen)) break; }", StringComparison.Ordinal)
                 && body.Contains("try { ApplyDuty(kv.Key, kv.Value, gen); }", StringComparison.Ordinal)
                 && body.Contains("try { RetryHandback(i); }", StringComparison.Ordinal),
                "each fan's write is its own try, so one throwing fan cannot skip the rest");
        }
    }

    /*------------------------------------------------------------*\
    | The fixes through the hub's REAL entry points - SetFanDuty,   |
    | RestoreAllFans, RestoreFan, SaveFanConfig - rather than the   |
    | pure seams above, which stayed green with every one of B1, B3 |
    | and B4 reverted at its call site. The fans are wireless ones  |
    | on a fake receiver (no USB, no telemetry), which is the one   |
    | backend the hub can drive without opening LHM or NvAPI, and   |
    | the hub stays shut the whole time.                            |
    \*------------------------------------------------------------*/
    sealed class FakeUsb : UnifiedRgb.Core.Native.IUsbWriter
    {
        public byte BulkOutPipe => 1;
        public bool Write(byte pipe, byte[] buffer) => true;
        public void Dispose() { }
    }

    static UnifiedRgb.Core.Devices.LianLiWireless NewReceiver(string group) =>
        new(new FakeUsb(), group, new byte[] { 1, 2, 3, 4, 5, 6 }, new byte[] { 6, 5, 4, 3, 2, 1 }, 8, 1, 3)
        { ReceiverOpener = () => null };

    static void ThroughTheHub(Harness t)
    {
        var instance = typeof(UnifiedRgb.Core.Devices.LianLiWireless).GetProperty("Instance",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;
        object? prior = instance.GetValue(null);
        int seedWait = UnifiedRgb.Core.Devices.LianLiWireless.PwmSeedWaitMs;
        UnifiedRgb.Core.Devices.LianLiWireless.PwmSeedWaitMs = 0;   // no telemetry here: seed at once
        string cfg = AppPaths.Local("fan-config.json");
        int a = SensorHub.LianFanBase, b = SensorHub.LianFanBase + 1, idle = SensorHub.LianFanBase + 2;
        void Clean() { File.Delete(cfg); SensorHub.ReloadFanConfig(); }
        try
        {
            t.Section("B4 (hub): a crash puts the wireless fans it was driving at 100%, or hands them off");
            Clean();
            using (var rx = NewReceiver("B4"))
            {
                instance.SetValue(null, rx);
                t.Check(SensorHub.SetFanDuty(a, 30), "a wireless fan is driven at 30%");
                SensorHub.RestoreAllFans(FanHandback.Crash, wirelessHandoff: true);
                t.Check(rx.PwmTargetOfSlot(a - SensorHub.LianFanBase) < 255, "hand-off chosen: the crash path leaves it to the board's PWM line");
                t.Check(SensorHub.SetFanDuty(a, 30), "...driven again");
                SensorHub.RestoreAllFans(FanHandback.Crash, wirelessHandoff: false);
                t.Equal((byte)255, rx.PwmTargetOfSlot(a - SensorHub.LianFanBase), "no hand-off: the fan we were driving goes to 100% (was: left at 30%, nothing running it)");
                t.Check(rx.PwmTargetOfSlot(idle - SensorHub.LianFanBase) < 255, "...and a fan we never drove is not forced to 100% for good");
            }
            instance.SetValue(null, prior);
            Clean();

            t.Section("B3 (hub): a save keeps the saved modes of fans not here this session");
            File.WriteAllText(cfg, System.Text.Json.JsonSerializer.Serialize(new { fans = new[] { Manual("GPU", 40) } }));
            SensorHub.ReloadFanConfig();
            using (var rx = NewReceiver("B3"))
            {
                instance.SetValue(null, rx);
                t.Check(SensorHub.SetFanDuty(a, 50), "a wireless fan is set while the GPU fans were never opened");
                var saved = SensorHub.ParseFanConfig(File.ReadAllText(cfg));
                t.Check(saved.TryGetValue("GPU", out var gpu) && gpu.Pct == 40,
                    "the GPU's saved mode survives the save (was: the file was rewritten from memory, dropping it)");
                t.Equal(2, saved.Count, "...next to the fan that was just set");

                t.Section("B1 (hub): after the failsafe, the next edit keeps every other fan's saved mode");
                t.Check(SensorHub.SetFanDuty(b, 70), "a second wireless fan is set");
                SensorHub.RestoreAllFans(FanHandback.Failsafe);
                t.Check(SensorHub.FailsafeTripped, "the failsafe tripped");
                t.Equal((int?)50, SensorHub.SuspendedFanDuty(a), "the first fan's mode is suspended, not erased");
                t.Check(SensorHub.ManualFanDuty(a) == null, "...and is not running (the fans are on the failsafe's duty)");
                t.Check(SensorHub.SetFanDuty(b, 80), "the user re-arms one fan");
                saved = SensorHub.ParseFanConfig(File.ReadAllText(cfg));
                t.Equal(3, saved.Count, "the file still holds all three (was: only the edited fan)");
                SensorHub.RestoreFan(b);
                t.Check(File.Exists(cfg) && SensorHub.ParseFanConfig(File.ReadAllText(cfg)).Count == 2,
                    "putting one fan on Auto drops only that fan (was: the file was deleted)");
            }
        }
        finally
        {
            instance.SetValue(null, prior);
            UnifiedRgb.Core.Devices.LianLiWireless.PwmSeedWaitMs = seedWait;
            try { Clean(); } catch (IOException) { }   // leave the hub empty for the next suite; a stuck file only costs the next run a stale entry
        }
    }

    /// <summary>The two fixes that live entirely in a call site the harness
    /// cannot reach without hardware: pinned in the source, as B8 is.</summary>
    static void CallSites(Harness t)
    {
        t.Section("B2 / B7: the call sites");
        string? src = SourceOf("src/UnifiedRgb.Core/Sensors/SensorHub.cs");
        if (src == null) { t.Skip("the call-site checks need the source tree"); return; }
        t.Check(System.Text.RegularExpressions.Regex.IsMatch(src,
                @"static double\? TempFor\(TempSource s\) => s switch\s*\{[^}]*_ => ControlHottest\(\)"),
            "B2: a curve's Hottest is ControlHottest (null when an opened source reads nothing), not HottestC");
        int id = src.IndexOf("public static async Task IdentifyFan", StringComparison.Ordinal);
        int idEnd = id < 0 ? -1 : src.IndexOf("static string FanConfigFile", id, StringComparison.Ordinal);
        string identify = id >= 0 && idEnd > id ? src[id..idEnd] : "";
        t.Check(identify.Contains("clearMarker = !AnyBoardFanOurs()", StringComparison.Ordinal)
             && identify.Contains("MarkBoardControl(false)", StringComparison.Ordinal),
            "B7: Identify's put-back clears the board-control marker when no board fan is ours");
    }
}

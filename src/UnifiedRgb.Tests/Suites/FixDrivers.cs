using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnifiedRgb.App.Services;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Native;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Regressions for the driver and native-I/O findings of the    |
| 2026-10-14 review (A1-A10, B10, D1, D7).                     |
|                                                              |
| Everything here runs over stand-ins: a fake SMBus that plays |
| an ENE stick, a fake WinUSB transmitter, HID transports that |
| count every call, a capture client that never touches an     |
| audio endpoint. Nothing opens a real device, and the SMBus   |
| tests use a private mutex, never the machine-wide one the    |
| running app and HWiNFO contend on.                           |
\*-----------------------------------------------------------*/
static class FixDriversSuite
{
    public static void Run(Harness t)
    {
        DllSearch(t);
        SmbusMutexAfterDispose(t);
        EneIdentifiesBeforeRemap(t);
        EneBlockFallback(t);
        LianStopsAtFirstRefusal(t);
        LianPwmSeed(t);
        LianBakeAfterStaticFrame(t);
        LogitechReplyMatching(t);
        WritesQueuedBehindDispose(t);
        ApexDisposeCancelsAHungFeature(t);
        HidHandleClosedUnderneath(t);
        WasapiSlowConsumer(t);
    }

    /*---------------- A1: native libraries by absolute path only ----------------*/
    static void DllSearch(Harness t)
    {
        t.Section("A1: P/Invokes never search user-writable folders");
        foreach (var asm in new[] { typeof(PawnIO).Assembly, typeof(LianBakeService).Assembly })
        {
            var attr = asm.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
            t.Check(attr != null && attr.Paths == DllImportSearchPath.System32,
                $"{asm.GetName().Name}: DllImports search System32 only (was the default search, PATH included)");
        }
        string? root = RepoRoot();
        if (root == null) t.Skip("the CLI's attribute is checked in source, which a published binary does not carry");
        else
        {
            string cli = Path.Combine(root, "src", "UnifiedRgb.Cli", "AssemblyInfo.cs");
            t.Check(File.Exists(cli) && File.ReadAllText(cli).Contains("DefaultDllImportSearchPaths(DllImportSearchPath.System32)", StringComparison.Ordinal),
                "UnifiedRgb.Cli: DllImports search System32 only");
        }

        var pawn = NativeLibraries.PawnIoCandidates();
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        t.Check(pawn.Length >= 1 && pawn.All(Path.IsPathFullyQualified), "PawnIOLib is looked for by absolute path only");
        t.Check(pawn[0].Equals(Path.Combine(pf, "PawnIO", "PawnIOLib.dll"), StringComparison.OrdinalIgnoreCase),
            $"...first in the real Program Files folder, whatever drive it is on ({pawn[0]})");
        t.Equal(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"), NativeLibraries.NvApiPath(),
            "nvapi64 is loaded from System32 and nowhere else");

        string missing = Path.Combine(TempDir(), "PawnIO", "PawnIOLib.dll");
        bool refused = false;
        try { NativeLibraries.LoadFirst(new[] { missing }, "PawnIOLib"); }
        catch (DllNotFoundException) { refused = true; }
        t.Check(refused, "a library missing from its install folder FAILS to load rather than falling back to the search");

        // The resolver's contract, for every spelling the runtime may ask with:
        // our two libraries come back as a real handle or as "not installed",
        // never as Zero - Zero hands the name back to the default search.
        foreach (string name in new[] { "PawnIOLib", "PawnIOLib.dll", "nvapi64.dll", "nvapi64" })
        {
            bool never0;
            try { never0 = NativeLibraries.Resolve(name, typeof(PawnIO).Assembly, null) != IntPtr.Zero; }
            catch (DllNotFoundException) { never0 = true; }   // not installed on this machine: refused, not searched for
            t.Check(never0, $"resolving '{name}' never falls back to the default search");
        }
        t.Equal(IntPtr.Zero, NativeLibraries.Resolve("kernel32.dll", typeof(PawnIO).Assembly, null),
            "a Windows DLL is left to the (System32-only) default probing");

        // ...and it is actually installed once either user type has run its
        // static constructor: a second resolver for the assembly is refused.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(NvApi).TypeHandle);
        bool installed = false;
        try { NativeLibrary.SetDllImportResolver(typeof(PawnIO).Assembly, (_, _, _) => IntPtr.Zero); }
        catch (InvalidOperationException) { installed = true; }
        t.Check(installed, "touching NvApi installs the resolver for Core (a second one is refused)");
        string? nv = SourceOf("src/UnifiedRgb.Core/Native/NvApi.cs"), pio = SourceOf("src/UnifiedRgb.Core/Native/PawnIO.cs");
        if (nv == null || pio == null) t.Skip("both users register the resolver: checked in source, which a published binary does not carry");
        else t.Check(nv.Contains("NativeLibraries.Register()", StringComparison.Ordinal) && pio.Contains("NativeLibraries.Register()", StringComparison.Ordinal),
            "both NvApi and PawnIO register it from their static constructors, whichever is touched first");

        t.Check(PawnIoInstaller.StagingDir.StartsWith(AppPaths.MachineDir, StringComparison.OrdinalIgnoreCase),
            $"the PawnIO installer is staged under the administrators-only machine folder, not %TEMP% ({PawnIoInstaller.StagingDir})");
    }

    /*---------------- A2: SMBus mutex released after a late wait ----------------*/

    /// <summary>A PawnSmbus over no driver: transfers go to OnXfer (or succeed),
    /// and the mutex is a private one.</summary>
    class StubBus : PawnSmbus
    {
        public StubBus(string mutexName) : base(new PawnIO(), mutexName) { }
        public override string ChipsetName => "stub";
        public Func<string, ulong[], ulong[], int>? OnXfer;
        private protected override int Xfer(string function, ulong[] input, ulong[] output)
            => OnXfer?.Invoke(function, input, output) ?? 0;
    }

    static string PrivateMutexName() => $"UnifiedRgbTests-smbus-{Guid.NewGuid():N}";

    static void SmbusMutexAfterDispose(Harness t)
    {
        t.Section("A2: a transaction whose wait outlives Dispose gives the SMBus mutex back");
        string name = PrivateMutexName();
        using var other = new Mutex(false, name);   // "another tool" holding the bus

        var plain = new StubBus(name);
        t.Check(plain.WriteByteData(0x70, 0x01, 0x42), "an uncontended transaction goes through");
        t.Check(other.WaitOne(0), "...and leaves the mutex free");
        other.ReleaseMutex();
        plain.Dispose();

        var bus = new StubBus(name);
        int issued = 0;
        bus.OnXfer = (_, _, _) => { Interlocked.Increment(ref issued); return 0; };
        other.WaitOne();
        bool? result = null;
        using var done = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        // Kept alive after its transaction: a thread that EXITS abandons any
        // mutex it still owns, which would hide the leak this pins.
        var worker = new Thread(() =>
        {
            result = bus.WriteByteData(0x70, 0x01, 0x42);
            done.Set();
            release.Wait(10_000);
        }) { IsBackground = true, Name = "smbus-straggler" };
        worker.Start();
        WaitUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, 2000);
        Thread.Sleep(100);                     // inside its 2 s wait for the bus
        bus.Dispose();                         // the logoff path: does not wait for it
        other.ReleaseMutex();                  // the wait now succeeds - after Dispose
        t.Check(done.Wait(5000), "the straggler returns");
        t.Check(result == false, "...reporting failure");
        t.Equal(0, Volatile.Read(ref issued), "...without issuing its transfer on a bus it was told to let go of");
        bool free = other.WaitOne(0);
        // The old code closed the handle under the wait: the release threw, was
        // swallowed, and the mutex stayed owned for the life of the thread -
        // HWiNFO and OpenRGB timing out on SMBus, every DRAM write 2 s.
        t.Check(free, "...and the mutex is free again while that thread lives on");
        if (free) other.ReleaseMutex();
        release.Set();
        worker.Join(5000);
    }

    /*---------------- A7 / A6: ENE detection and the batched color path ----------------*/

    /// <summary>A bus with ENE sticks (and anything else) on it. Each device has
    /// a register file, ENE's select/auto-increment protocol and the identity
    /// window at 0xA0-0xAF; a NON-ENE device answers a probe but not the
    /// identity reads. Every write is recorded by address.</summary>
    sealed class FakeEneBus : StubBus
    {
        public sealed class Dev
        {
            public bool Ene = true;
            public readonly Dictionary<int, byte> Regs = new();
            public int Sel;
        }
        public readonly Dictionary<byte, Dev> Devices = new();
        public readonly List<(byte Addr, string Kind, int Len)> Writes = new();
        public int FailSelects;                  // the next N register selects NAK
        public int RefuseBlocksOver = int.MaxValue;   // blocks longer than this are refused

        public FakeEneBus(string mutexName) : base(mutexName) => OnXfer = Respond;

        int Respond(string fn, ulong[] input, ulong[] output)
        {
            if (fn != "ioctl_smbus_xfer") return -1;
            byte addr = (byte)input[0];
            bool read = input[1] == 1;
            byte cmd = (byte)input[2];
            ulong proto = input[3];
            lock (Writes)
            {
                if (!Devices.TryGetValue(addr, out var d)) return -1;   // nothing ACKs
                if (read)
                {
                    int v;
                    if (proto == 1) v = 0x00;                                       // receive byte (probe)
                    else if (!d.Ene) v = 0x5A;                                      // something else: no ENE identity
                    else if (cmd is >= 0xA0 and < 0xB0) v = cmd - 0xA0;             // ENE identity window
                    else if (cmd == 0x81) v = d.Regs.GetValueOrDefault(d.Sel++);    // data register
                    else v = 0;
                    output[0] = (ulong)v;
                    return 1;
                }
                if (proto == 3 && cmd == 0x00)                                      // register select
                {
                    Writes.Add((addr, "select", 2));
                    if (FailSelects > 0) { FailSelects--; return -1; }
                    ushort w = (ushort)input[4];
                    d.Sel = ((w << 8) & 0xFF00) | (w >> 8);
                    return 0;
                }
                if (proto == 2 && cmd == 0x01)                                      // data byte
                {
                    Writes.Add((addr, "byte", 1));
                    Store(addr, d, (byte)input[4]);
                    return 0;
                }
                if (proto == 5 && cmd == 0x03)                                      // block
                {
                    var bytes = new byte[40];
                    for (int c = 0; c < 5; c++) BitConverter.GetBytes(input[4 + c]).CopyTo(bytes, c * 8);
                    int len = bytes[0];
                    Writes.Add((addr, "block", len));
                    if (len > RefuseBlocksOver) return -1;
                    for (int i = 0; i < len; i++) Store(addr, d, bytes[1 + i]);
                    return 0;
                }
                Writes.Add((addr, $"other:{proto}:{cmd:X2}", 0));
                return 0;
            }
        }

        void Store(byte addr, Dev d, byte value)
        {
            d.Regs[d.Sel] = value;
            // The remap: writing the I2C address register moves the controller.
            if (d.Sel == 0x80F9 && value >> 1 != addr)
            {
                Devices.Remove(addr);
                Devices[(byte)(value >> 1)] = d;
            }
            d.Sel++;
        }
    }

    static FakeEneBus.Dev EneStick(int leds = 8)
    {
        var d = new FakeEneBus.Dev();
        d.Regs[0x1C02] = (byte)leds;   // config table: LED count
        return d;
    }

    static void EneIdentifiesBeforeRemap(Harness t)
    {
        t.Section("A7: ENE detection identifies the device at 0x77 before writing to it");
        {
            var bus = new FakeEneBus(PrivateMutexName());
            bus.Devices[0x77] = new FakeEneBus.Dev { Ene = false };   // something that is not ENE
            var found = EneDram.DetectOn(bus);
            t.Equal(0, found.Count, "nothing is detected");
            t.Equal(0, bus.Writes.Count(w => w.Addr == 0x77),
                "and NOTHING is written to the unidentified device at 0x77 (the remap used to write two registers to it)");
            foreach (var d in found) d.Dispose();
        }
        {
            var bus = new FakeEneBus(PrivateMutexName());
            bus.Devices[0x77] = EneStick();
            var found = EneDram.DetectOn(bus);
            t.Check(bus.Writes.Any(w => w.Addr == 0x77), "an ENE controller at 0x77 is still remapped");
            t.Equal(1, found.Count, "...and found at its new address");
            t.Check(found.Count == 1 && found[0].Name.Contains("0x70", StringComparison.Ordinal), $"...the first free candidate ({(found.Count > 0 ? found[0].Name : "none")})");
            foreach (var d in found) d.Dispose();
        }
    }

    static void EneBlockFallback(Harness t)
    {
        t.Section("A6: only refused BLOCK writes drop the batched color path, and only repeatedly");
        static Rgb[] Frame(int n, byte v) => Enumerable.Repeat(new Rgb(v, (byte)(v + 1), (byte)(v + 2)), n).ToArray();
        {
            var bus = new FakeEneBus(PrivateMutexName());
            bus.Devices[0x70] = EneStick();
            var stick = EneDram.DetectOn(bus).Single();
            int n = stick.LedCount;
            t.Check(stick.SetColors(Frame(n, 10)), "a frame lands");
            int BigBlocks() { lock (bus.Writes) return bus.Writes.Count(w => w.Kind == "block" && w.Len == n * 3); }
            int big = BigBlocks();
            t.Check(big >= 1, "...as one whole-stick block");

            bus.FailSelects = 1;   // the bus was busy: the register select timed out
            t.Check(!stick.SetColors(Frame(n, 20)), "a frame whose register select failed is refused (and re-sent next call)");
            int before = BigBlocks();
            t.Check(stick.SetColors(Frame(n, 30)), "the next frame lands");
            // The old code flipped to 3-byte chunks for the rest of the session on
            // ANY failure, select included, and logged it as the host refusing blocks.
            t.Equal(before + 1, BigBlocks(), "...still as one whole-stick block: a failed select says nothing about block support");
            stick.Dispose();
        }
        {
            var bus = new FakeEneBus(PrivateMutexName());
            bus.Devices[0x70] = EneStick();
            var stick = EneDram.DetectOn(bus).Single();
            int n = stick.LedCount;
            int BigBlocks() { lock (bus.Writes) return bus.Writes.Count(w => w.Kind == "block" && w.Len == n * 3); }
            bus.RefuseBlocksOver = 3;   // a host that rejects large blocks
            t.Check(stick.SetColors(Frame(n, 40)), "a refused block still lands the frame, through 3-byte chunks");
            int afterFirst = BigBlocks();
            t.Check(stick.SetColors(Frame(n, 50)), "a second frame lands");
            t.Equal(afterFirst + 1, BigBlocks(), "...and one refused block did not give up on blocks for good");
            stick.SetColors(Frame(n, 60));   // the third refusal in a row
            int afterThird = BigBlocks();
            t.Check(stick.SetColors(Frame(n, 70)), "after repeated refusals frames still land");
            t.Equal(afterThird, BigBlocks(), "...and no longer try the whole-stick block");
            stick.Dispose();
        }
    }

    /*---------------- A4 / A3 / D1 / D7: Lian Li wireless ----------------*/

    /// <summary>The SLV3 transmitter: records every 64-byte fragment, can
    /// refuse one by ordinal.</summary>
    sealed class Transmitter : IUsbWriter
    {
        public byte BulkOutPipe => 1;
        public int RefuseAt = -1;
        readonly List<byte[]> _writes = new();
        public int Attempts { get { lock (_writes) return _writes.Count; } }
        public bool Write(byte pipe, byte[] buffer)
        {
            lock (_writes)
            {
                _writes.Add((byte[])buffer.Clone());
                return _writes.Count != RefuseAt;
            }
        }
        /// <summary>The four PWM bytes of every PWM frame sent (RF 0x12 0x10,
        /// bytes 17-20 = fragment 0 bytes 21-24).</summary>
        public List<byte[]> PwmFrames()
        {
            lock (_writes)
                return _writes.Where(p => p[1] == 0 && p[4] == 0x12 && p[5] == 0x10).Select(p => p[21..25]).ToList();
        }
        /// <summary>Meta packets of multi-frame effects (an animation upload):
        /// RF 0x12 0x20, packet index 0, total frames above 1.</summary>
        public int AnimationMetas()
        {
            lock (_writes)
                return _writes.Count(p => p[1] == 0 && p[4] == 0x12 && p[5] == 0x20 && p[22] == 0 && (p[29] << 8 | p[30]) > 1);
        }
        public void Dispose() { }
    }

    static readonly PropertyInfo LianInstance = typeof(LianLiWireless).GetProperty("Instance",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

    static byte[] Mac(byte seed) => new byte[] { seed, 2, 3, 4, 5, 6 };

    /// <summary>A GetDev status page carrying one record for <paramref name="mac"/>.</summary>
    static byte[] TelemetryPage(byte[] mac, byte[] pwm)
    {
        var page = new byte[434];
        page[0] = 0x10;
        const int rec = 4;
        Array.Copy(mac, 0, page, rec, 6);
        page[rec + 18] = 0;   // a fan record, not the controller's own (0xFF)
        Array.Copy(pwm, 0, page, rec + 36, 4);
        return page;
    }

    static void LianStopsAtFirstRefusal(Harness t)
    {
        t.Section("A4: a stalled Lian transmitter is not fed the rest of the frame");
        object? prior = LianInstance.GetValue(null);
        try
        {
            var usb = new Transmitter();
            using var fans = new LianLiWireless(usb, "Stall", Mac(9), Mac(8), 8, 1, 1) { ReceiverOpener = () => null };
            var red = Enumerable.Repeat(Rgb.Red, fans.LedCount).ToArray();
            usb.RefuseAt = 5;   // the first REPEAT of the meta packet (fragments 1-4 were its first copy)
            t.Check(!fans.SetColors(red), "a refused meta repeat fails the frame");
            t.Equal(5, usb.Attempts, "...and nothing goes out after it: no data packets, no insurance copy");
            Thread.Sleep(700);
            t.Equal(5, usb.Attempts, "...and no settle resend half a second later");
        }
        finally { LianInstance.SetValue(null, prior); }
    }

    static void LianPwmSeed(Harness t)
    {
        t.Section("A3: the first fan write keeps the other fans on the duty telemetry reports");
        object? prior = LianInstance.GetValue(null);
        int savedWait = LianLiWireless.PwmSeedWaitMs;
        try
        {
            {
                var usb = new Transmitter();
                using var fans = new LianLiWireless(usb, "Seed", Mac(1), Mac(2), 8, 1, 3) { ReceiverOpener = () => null };
                int chain = fans.ChainOf(0);
                fans.SetFanDuty(0, 50);   // 127
                Thread.Sleep(150);
                t.Equal(0, usb.PwmFrames().Count, "nothing is asserted before the first telemetry page (the seed waits for it)");
                fans.ApplyTelemetryPage(TelemetryPage(Mac(1), new byte[] { 10, 20, 30, 40 }), 46);
                t.Check(WaitUntil(() => usb.PwmFrames().Count > 0, 3000), "the PWM frame goes out once the page arrives");
                var f = usb.PwmFrames().FirstOrDefault() ?? new byte[4];
                var want = new byte[] { 10, 20, 30, 40 };
                want[chain] = 127;
                // The seed used to run before telemetry had even started, so every
                // other fan got 0x66 - 40%, fixed, for the whole session.
                t.Check(f.SequenceEqual(want),
                    $"the fan asked for gets its duty, the others keep theirs (got {Convert.ToHexString(f)}, want {Convert.ToHexString(want)})");
            }
            {
                LianLiWireless.PwmSeedWaitMs = 100;
                var usb = new Transmitter();
                using var fans = new LianLiWireless(usb, "NoTelemetry", Mac(3), Mac(4), 8, 1, 3) { ReceiverOpener = () => null };
                int chain = fans.ChainOf(1);
                fans.SetFanDuty(1, 100);
                t.Check(WaitUntil(() => usb.PwmFrames().Count > 0, 3000), "with no telemetry at all the write still goes out, after the bounded wait");
                var f = usb.PwmFrames().FirstOrDefault() ?? new byte[4];
                var want = new byte[] { 0x66, 0x66, 0x66, 0x66 };
                want[chain] = 255;
                t.Check(f.SequenceEqual(want), $"...seeding the others with the 40% fallback (got {Convert.ToHexString(f)})");
            }
            {
                var usb = new Transmitter();
                using var fans = new LianLiWireless(usb, "Five", Mac(5), Mac(6), 8, 1, 5) { ReceiverOpener = () => null };
                int slot = Enumerable.Range(0, 5).First(s => fans.ChainOf(s) == 4);
                bool threw = false;
                try { fans.SetFanDuty(slot, 50); }
                catch (Exception) { threw = true; }
                // B8 hardening: the 4-byte PWM target was indexed by the chain number.
                t.Check(!threw, "a fifth fan (chain 4) is refused, not an IndexOutOfRange out of the sensor tick");
                Thread.Sleep(200);
                t.Equal(0, usb.PwmFrames().Count, "...and nothing is sent for it");
            }
        }
        finally
        {
            LianLiWireless.PwmSeedWaitMs = savedWait;
            LianInstance.SetValue(null, prior);
        }
    }

    static void LianBakeAfterStaticFrame(Harness t)
    {
        t.Section("D1/D7: the baker re-uploads after a static frame, and uploads under the write gate");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            object? prior = LianInstance.GetValue(null);
            var usb = new Transmitter();
            var fans = new LianLiWireless(usb, "Bake", Mac(7), Mac(8), 8, 1, 1) { ReceiverOpener = () => null };
            var lighting = new LightingController();
            try
            {
                var bake = new LianBakeService(lighting, () => new[] { fans });
                var rebake = typeof(LianBakeService).GetMethod("Rebake", BindingFlags.Instance | BindingFlags.NonPublic)!;
                fans.SuppressStreaming = true;   // the engine worker renders the preview only
                lighting.Engine.Start(fans, 0, fans.LedCount, lighting.FrameFor(fans), new Breathing(), 1.0, Rgb.Red);

                // D7: the upload takes the device's write gate, the boundary the
                // effect workers' frames share - a straggler frame after it would
                // cancel the animation on the fans.
                var gate = EffectEngine.WriteGateFor(fans);
                Monitor.Enter(gate);
                try
                {
                    rebake.Invoke(bake, new object[] { fans, false });
                    Thread.Sleep(600);   // bake rendered, upload queued on the lane
                    t.Equal(0, usb.AnimationMetas(), "the upload waits while the device's write gate is held");
                }
                finally { Monitor.Exit(gate); }
                t.Check(WaitUntil(() => usb.AnimationMetas() > 0, 5000), "...and goes out once it is released");
                lighting.Applier.Drain(3000);
                t.Check(fans.AnimationActive, "the animation is playing");

                int before = usb.AnimationMetas();
                rebake.Invoke(bake, new object[] { fans, false });
                Thread.Sleep(400);
                lighting.Applier.Drain(3000);
                t.Equal(before, usb.AnimationMetas(), "the same bake while it plays is not uploaded again");

                // LightsOff's black, the unlock restore, identify's put-back: a
                // static frame, which cancels the animation in hardware.
                t.Check(fans.SetColors(Enumerable.Repeat(Rgb.Black, fans.LedCount).ToArray()), "a static frame goes out");
                t.Check(!fans.AnimationActive, "...and the device knows the animation is gone");
                before = usb.AnimationMetas();
                rebake.Invoke(bake, new object[] { fans, false });
                // The old check compared the signature alone, skipped this, and the
                // fans sat black while the preview animated.
                t.Check(WaitUntil(() => usb.AnimationMetas() > before, 5000), "the same bake asked for again IS uploaded again");
                lighting.Applier.Drain(3000);
                t.Check(fans.AnimationActive, "...and plays");
                bake.Stop();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                lighting.Engine.StopAll();
                lighting.Applier.Drain(3000);
                fans.Dispose();
                LianInstance.SetValue(null, prior);
            }
        }) { IsBackground = true, Name = "fix-drivers-bake" };
        thread.Start();
        if (!thread.Join(30_000)) { t.Check(false, "the bake test finished in time"); return; }
        if (failure != null) t.Check(false, $"the bake test threw: {failure}");
    }

    /*---------------- A8: Logitech replies matched on function and device ----------------*/
    static void LogitechReplyMatching(Harness t)
    {
        t.Section("A8: a Logitech reply is taken only for the request it answers");
        int savedGap = LogitechG403.MinFrameGapMs, savedRetry = LogitechG403.RetryAfterFailMs;
        LogitechG403.MinFrameGapMs = 0;
        LogitechG403.RetryAfterFailMs = 0;
        try
        {
            {
                var hid = new FakeHid();
                byte[]? lateClaimReply = null;
                hid.Accept = (n, report) =>
                {
                    var echo = (byte[])report.Clone();
                    if (n == 1) lateClaimReply = echo;                    // the claim's reply is late...
                    else if (n == 2) { hid.Replies.Enqueue(lateClaimReply!); hid.Replies.Enqueue(echo); }   // ...arriving just ahead of cluster 0's
                    // write 3, cluster 1: its reply is lost
                    return true;
                };
                using var mouse = new LogitechG403(hid, 0xFF, 14, 0x8070, new byte[] { 1, 1 }, "G403");
                var red = new Rgb(255, 0, 0);
                // The old matcher took the claim's late reply for cluster 0's, left
                // cluster 0's own for cluster 1 - and called a lost write landed.
                t.Check(!mouse.SetColors(new[] { red, red }), "a cluster whose reply never came is not reported as landed");
                hid.Accept = (_, report) => { hid.Replies.Enqueue((byte[])report.Clone()); return true; };
                int before = hid.Writes.Count;
                t.Check(mouse.SetColors(new[] { red, red }), "the next frame lands");
                t.Equal(before + 2, hid.Writes.Count, "...re-sending cluster 1 (behind a fresh claim), which was never cached as delivered");
            }
            {
                var hid = new FakeHid();
                hid.Accept = (n, report) =>
                {
                    var echo = (byte[])report.Clone();
                    if (n == 2) echo[1] = 0x02;   // the effect's "reply" is from another device behind the receiver
                    hid.Replies.Enqueue(echo);
                    return true;
                };
                using var mouse = new LogitechG403(hid, 0xFF, 14, 0x8070, new byte[] { 1 }, "G403");
                t.Check(!mouse.SetColors(new[] { new Rgb(0, 0, 255) }), "a reply from another device index is not ours");
            }
        }
        finally
        {
            LogitechG403.MinFrameGapMs = savedGap;
            LogitechG403.RetryAfterFailMs = savedRetry;
        }
    }

    /*---------------- A9: a write queued behind Dispose never reaches the handle ----------------*/

    /// <summary>A HID transport that counts every call, including the ones made
    /// after it was disposed - which FakeHid, refusing early, cannot tell
    /// apart from no call at all. Writes are echoed back as replies (HID++);
    /// feature reads answer OK with the command echoed (Razer).</summary>
    sealed class TallyHid : IHidTransport
    {
        readonly Queue<byte[]> _replies = new();
        byte[]? _lastFeature;
        public int CallsAfterDispose;
        public bool FeatureOnly => false;
        public bool IsDisposed { get; private set; }
        public int WriteTimeoutMs { get; set; } = 400;
        void Tally() { if (IsDisposed) Interlocked.Increment(ref CallsAfterDispose); }
        public bool Write(byte[] report)
        {
            Tally();
            if (IsDisposed) return false;
            lock (_replies) _replies.Enqueue((byte[])report.Clone());
            return true;
        }
        public int Read(byte[] buffer, int timeoutMs)
        {
            Tally();
            if (IsDisposed) return 0;
            byte[] r;
            lock (_replies) { if (_replies.Count == 0) return 0; r = _replies.Dequeue(); }
            int n = Math.Min(r.Length, buffer.Length);
            Array.Copy(r, buffer, n);
            return n;
        }
        public bool SetFeature(byte[] report)
        {
            Tally();
            if (IsDisposed) return false;
            _lastFeature = (byte[])report.Clone();
            return true;
        }
        public bool GetFeature(byte[] report)
        {
            Tally();
            if (IsDisposed || _lastFeature == null) return false;
            Array.Clear(report);
            report[1] = 0x02;   // OK
            if (report.Length > 8 && _lastFeature.Length > 8) { report[7] = _lastFeature[7]; report[8] = _lastFeature[8]; }
            return true;
        }
        public bool GetInputReport(byte[] report) => GetFeature(report);
        public void Dispose() => IsDisposed = true;
    }

    /// <summary>Hold the driver's write lock, start a write (it passes any
    /// early flag check and blocks on the lock), Dispose from under the same
    /// lock (re-entrant), then let the write through. It must see the flag.</summary>
    static void QueuedBehindDispose(Harness t, string name, IRgbDevice dev, TallyHid hid)
    {
        var gate = dev.GetType().GetField("_writeLock", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dev);
        if (gate == null) { t.Check(false, $"{name}: has a _writeLock to queue behind"); return; }
        var frame = Enumerable.Repeat(new Rgb(9, 8, 7), dev.LedCount).ToArray();
        bool? result = null;
        Exception? thrown = null;
        Thread writer;
        lock (gate)
        {
            writer = new Thread(() =>
            {
                try { result = dev.SetColors(frame); }
                catch (Exception ex) { thrown = ex; }
            }) { IsBackground = true };
            writer.Start();
            WaitUntil(() => (writer.ThreadState & ThreadState.WaitSleepJoin) != 0, 2000);
            Thread.Sleep(30);
            dev.Dispose();
        }
        writer.Join(5000);
        t.Check(thrown == null && result == false, $"{name}: a write queued behind Dispose reports false ({thrown?.GetType().Name ?? result?.ToString() ?? "no result"})");
        t.Equal(0, hid.CallsAfterDispose, $"{name}: ...and never touches the closed transport");
    }

    static void WritesQueuedBehindDispose(Harness t)
    {
        t.Section("A9: the disposed flag is set and re-checked under each driver's write lock");
        {
            var hid = new TallyHid();
            QueuedBehindDispose(t, "Logitech", new LogitechG403(hid, 0xFF, 14, 0x8070, new byte[] { 1, 1 }, "G403"), hid);
        }
        {
            var hid = new TallyHid();
            var mouse = RazerHid.OpenKnown(() => hid, 0x00AA, RazerHid.BasiliskV3Pro);
            if (mouse == null) t.Check(false, "Razer: the test mouse opened");
            else QueuedBehindDispose(t, "Razer", mouse, hid);
        }
        {
            var hid = new TallyHid();
            QueuedBehindDispose(t, "SteelSeries Apex", new SteelSeriesApex(hid, 643, 65, "Apex"), hid);
        }
        {
            var hid = new TallyHid();
            QueuedBehindDispose(t, "Corsair Strafe", new CorsairStrafeMk2(hid), hid);
        }
        {
            var hid = new TallyHid();
            QueuedBehindDispose(t, "Gigabyte", new GigabyteIt5711(hid, 0x5711), hid);
        }
        {
            var hid = new TallyHid();
            QueuedBehindDispose(t, "Sayo", new SayoDevice(hid, 64, "Sayo"), hid);
        }
    }

    /// <summary>A feature report the driver never completes: it returns only
    /// when CancelPending aborts it, as CancelIoEx aborts the real one.</summary>
    sealed class HungFeatureHid : IHidTransport
    {
        readonly ManualResetEventSlim _cancel = new();
        public volatile bool InSetFeature;
        public bool FeatureOnly => false;
        public bool IsDisposed { get; private set; }
        public int WriteTimeoutMs { get; set; } = 400;
        public bool Write(byte[] report) => !IsDisposed;
        public int Read(byte[] buffer, int timeoutMs) => 0;
        public bool SetFeature(byte[] report)
        {
            InSetFeature = true;
            _cancel.Wait(10_000);
            return false;
        }
        public bool GetFeature(byte[] report) => false;
        public bool GetInputReport(byte[] report) => false;
        public void CancelPending() => _cancel.Set();
        public void Dispose() { IsDisposed = true; _cancel.Dispose(); }
    }

    static void ApexDisposeCancelsAHungFeature(Harness t)
    {
        t.Section("V1: the Apex's Dispose cancels a hung feature report instead of waiting on it");
        var hid = new HungFeatureHid();
        var kb = new SteelSeriesApex(hid, 643, 65, "Apex");
        var frame = Enumerable.Repeat(new Rgb(9, 8, 7), kb.LedCount).ToArray();
        var writer = new Thread(() => kb.SetColors(frame)) { IsBackground = true };
        writer.Start();
        WaitUntil(() => hid.InSetFeature, 2000);
        t.Check(hid.InSetFeature, "the write is stuck in SetFeature, holding the write lock");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var closer = Task.Run(kb.Dispose);
        bool closed = closer.Wait(3000);
        t.Check(closed && clock.ElapsedMilliseconds < 2000,
            $"Dispose returns promptly ({clock.ElapsedMilliseconds} ms) - Rescan runs it on the UI thread");
        t.Check(writer.Join(3000), "...and the cancelled write lets go");
    }

    static void HidHandleClosedUnderneath(Harness t)
    {
        t.Section("A9: a HID handle closed under a feature call reports failure, not an exception");
        string path = Path.Combine(TempDir(), "not-a-hid-device.bin");
        var file = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite);
        var hid = new HidNative.HidHandle(file);
        file.Dispose();   // closed between HidHandle's flag check and the call
        bool threw = false, set = true, get = true, input = true;
        try
        {
            set = hid.SetFeature(new byte[8]);
            get = hid.GetFeature(new byte[8]);
            input = hid.GetInputReport(new byte[8]);
        }
        catch (ObjectDisposedException) { threw = true; }
        t.Check(!threw, "no ObjectDisposedException escapes into the driver");
        t.Check(!set && !get && !input, "...each call reports false, as the transport contract says");
        bool cancelThrew = false;
        try { hid.CancelPending(); } catch (ObjectDisposedException) { cancelThrew = true; }
        t.Check(!cancelThrew, "CancelPending on a handle closed underneath is quiet too");
        hid.Dispose();
        hid.CancelPending();   // after Dispose: a no-op
    }

    /*---------------- A10/B6: WASAPI Dispose while the consumer is busy ----------------*/

    /// <summary>One 16-frame stereo float packet, then silence.</summary>
    sealed class OnePacketCapture : IAudioCaptureClient, IDisposable
    {
        readonly IntPtr _buf = Marshal.AllocHGlobal(16 * 2 * sizeof(float));
        int _packets = 1;
        public int Released;
        public OnePacketCapture() { for (int i = 0; i < 16 * 2; i++) Marshal.WriteInt32(_buf, i * 4, 0); }
        public int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition)
        {
            data = _buf; frames = 16; flags = 0; devicePosition = 0; qpcPosition = 0;
            return 0;
        }
        public int ReleaseBuffer(uint frames) { Interlocked.Increment(ref Released); return 0; }
        public int GetNextPacketSize(out uint frames)
        {
            frames = Interlocked.Exchange(ref _packets, 0) > 0 ? 16u : 0u;
            return 0;
        }
        public void Dispose() => Marshal.FreeHGlobal(_buf);
    }

    static void WasapiSlowConsumer(Harness t)
    {
        t.Section("A10: WASAPI Dispose never frees what the capture thread is still using");
        using var entered = new ManualResetEventSlim(false);
        using var proceed = new ManualResetEventSlim(false);
        using var capture = new OnePacketCapture();
        var wake = new AutoResetEvent(false);
        var loop = new WasapiLoopback((_, _, _) => { entered.Set(); proceed.Wait(5000); });
        loop.StartPollingForTest(capture, wake, 48000, 2);
        t.Check(entered.Wait(3000), "the consumer is inside a delivery");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        loop.Dispose();
        t.Check(clock.ElapsedMilliseconds < 2000, $"Dispose stays bounded while it is ({clock.ElapsedMilliseconds} ms)");
        // The old Dispose freed the event (and the COM objects) after its Join
        // gave up, and the poll thread then waited on it outside any try: an
        // unhandled exception on a background thread, which ends the process.
        t.Check(!wake.SafeWaitHandle.IsClosed, "the wake event is left to the thread still using it");
        proceed.Set();
        t.Check(WaitUntil(() => wake.SafeWaitHandle.IsClosed, 3000), "...which frees it on its way out");
        t.Equal(1, Volatile.Read(ref capture.Released), "the in-flight buffer was handed back");
    }

}

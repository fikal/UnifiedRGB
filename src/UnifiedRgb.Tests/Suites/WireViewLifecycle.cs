using System.Diagnostics;
using UnifiedRgb.Core.Native;
using UnifiedRgb.Core.Sensors;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The WireView poller's lifecycle and discovery, over fake     |
| ports (WireView.ResetForTests swaps the registry scan and   |
| the serial open). Pinned because each of these was a real   |
| bug in the first version:                                    |
|                                                              |
|  - a slow device at shutdown: the timer drain closed an      |
|    event the still-running tick later signalled - an         |
|    unhandled throw on the pool, which kills this process;    |
|  - a real WireView slow to answer after a replug was         |
|    blacklisted for the whole session;                        |
|  - a WireView II on one port hid a Pro II on another, and    |
|    was reopened every second (locking its own app out);     |
|  - a device whose frames never pass the sanity gate was      |
|    reconnected forever;                                      |
|  - Release blocked the UI thread for the in-flight tick.     |
\*-----------------------------------------------------------*/
static class WireViewLifecycleSuite
{
    static readonly byte[] ProII = { 0xEF, 0x05, 0x05 };
    static readonly byte[] WireViewII = { 0xEF, 0x07, 0x02 };
    static readonly byte[] Stranger = { 0x00, 0x11, 0x22 };
    static readonly byte[] Frame = Convert.FromHexString(WireViewSuite.CapturedHex);

    sealed class FakePort : ISerialQuery
    {
        public byte[]? Vendor;          // null = silent
        public byte[]? Sensors = Frame; // null = silent
        public int SensorDelayMs;
        public volatile bool InSensorQuery;
        public volatile bool Disposed;

        public bool Query(byte command, Span<byte> reply)
        {
            if (command == 0x04)
            {
                InSensorQuery = true;
                if (SensorDelayMs > 0) Thread.Sleep(SensorDelayMs);
            }
            var a = command == 0x01 ? Vendor : Sensors;
            if (a == null || a.Length < reply.Length) return false;
            a.AsSpan(0, reply.Length).CopyTo(reply);
            return true;
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>Ports present and how each one opens; counts the opens.</summary>
    sealed class Bench
    {
        public List<string> Ports = new();
        public readonly Dictionary<string, Func<FakePort>> Devices = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Busy = new(StringComparer.OrdinalIgnoreCase);   // port -> Win32 error
        public readonly Dictionary<string, int> Opens = new(StringComparer.OrdinalIgnoreCase);
        public FakePort? Last;

        public void Install(params string[] known) => WireView.ResetForTests(
            () => new List<string>(Ports),
            port =>
            {
                Opens[port] = Opens.GetValueOrDefault(port) + 1;
                if (Busy.TryGetValue(port, out int err)) return (null, err);
                if (!Devices.TryGetValue(port, out var make)) return (null, 2);
                Last = make();
                return (Last, 0);
            },
            known);

        public int OpensOf(string port) => Opens.GetValueOrDefault(port);
    }

    public static void Run(Harness t)
    {
        try { RunCore(t); }
        finally { WireView.ResetForTests(); }   // the real seams back, nothing running
    }

    static void RunCore(Harness t)
    {
        t.Section("A Pro II connects and reads");
        {
            var b = new Bench { Ports = { "COM4" } };
            b.Devices["COM4"] = () => new FakePort { Vendor = ProII };
            b.Install();
            WireView.PollForTests();
            t.Equal(WireView.State.Connected, WireView.Status, "connected");
            t.Equal("WireView Pro II", WireView.Current.Edition, "edition from the vendor query");
            t.Check(WireView.Latest is { Watts: > 26 and < 27 }, "the first poll also reads a frame");
        }

        t.Section("Silent ports: strangers are backed off, never a known WireView");
        {
            var b = new Bench { Ports = { "COM7" } };
            b.Devices["COM7"] = () => new FakePort { Vendor = null };
            b.Install();
            for (int i = 0; i < 6; i++) WireView.PollForTests();
            t.Equal(3, b.OpensOf("COM7"), "an unknown silent port is asked three times, then left for a minute");
            t.Equal(WireView.State.NotFound, WireView.Status, "and it is not reported as anything");

            b = new Bench { Ports = { "COM7" } };
            b.Devices["COM7"] = () => new FakePort { Vendor = null };
            b.Install("COM7");
            for (int i = 0; i < 6; i++) WireView.PollForTests();
            t.Equal(6, b.OpensOf("COM7"), "a port known to be a WireView is asked every poll (it may be mid-boot)");

            // Slow after a replug, then answers: found the moment it does.
            b = new Bench { Ports = { "COM7" } };
            int calls = 0;
            b.Devices["COM7"] = () => new FakePort { Vendor = ++calls < 3 ? null : ProII };
            b.Install();
            for (int i = 0; i < 3; i++) WireView.PollForTests();
            t.Equal(WireView.State.Connected, WireView.Status, "a WireView that answers on its third try is connected, not written off");
        }

        t.Section("A different device is left alone only while it stays plugged in");
        {
            var b = new Bench { Ports = { "COM9" } };
            b.Devices["COM9"] = () => new FakePort { Vendor = Stranger };
            b.Install();
            WireView.PollForTests();
            WireView.PollForTests();
            t.Equal(1, b.OpensOf("COM9"), "answered as something else: not asked again");
            b.Ports = new();
            WireView.PollForTests();                      // unplugged: forgotten
            b.Ports = new() { "COM9" };
            b.Devices["COM9"] = () => new FakePort { Vendor = ProII };
            WireView.PollForTests();
            t.Equal(WireView.State.Connected, WireView.Status, "a WireView later given that COM name is found");
        }

        t.Section("Busy is only claimed for a port known to be a WireView");
        {
            var b = new Bench { Ports = { "COM5" } };
            b.Busy["COM5"] = 5;   // ERROR_ACCESS_DENIED
            b.Install();
            WireView.PollForTests();
            t.Equal(WireView.State.NotFound, WireView.Status, "some other gadget held by its own tool: no card");
            b.Install("COM5");
            WireView.PollForTests();
            t.Equal(WireView.State.Busy, WireView.Status, "our WireView held by the vendor app: Busy");
        }

        t.Section("An unsupported model neither hides a Pro II nor gets reopened every poll");
        {
            var b = new Bench { Ports = { "COM3", "COM4" } };
            b.Devices["COM3"] = () => new FakePort { Vendor = WireViewII };
            b.Devices["COM4"] = () => new FakePort { Vendor = ProII };
            b.Install();
            WireView.PollForTests();
            t.Equal(WireView.State.Connected, WireView.Status, "a WireView II first in the list does not stop the scan");

            b = new Bench { Ports = { "COM3" } };
            b.Devices["COM3"] = () => new FakePort { Vendor = WireViewII };
            b.Install();
            for (int i = 0; i < 5; i++) WireView.PollForTests();
            t.Equal(WireView.State.Unsupported, WireView.Status, "alone, it is reported as unsupported");
            t.Equal(1, b.OpensOf("COM3"), "and opened once, not every poll");
        }

        t.Section("Frames that never pass the sanity gate end as unsupported, not an endless reconnect");
        {
            var bad = (byte[])Frame.Clone();
            bad[11] = 7;   // non-zero padding: fails the gate
            var b = new Bench { Ports = { "COM6" } };
            b.Devices["COM6"] = () => new FakePort { Vendor = ProII, Sensors = bad };
            b.Install();
            for (int i = 0; i < 12; i++) WireView.PollForTests();
            t.Equal(WireView.State.Unsupported, WireView.Status, "three links without one valid frame: unsupported");
            t.Check(WireView.Current.Edition?.Contains("unrecognised", StringComparison.Ordinal) == true, "and it says why");
            t.Equal(3, b.OpensOf("COM6"), "reconnected three times, then left alone");
        }

        t.Section("Release never blocks its caller; the port still closes");
        {
            var b = new Bench { Ports = { "COM4" } };
            b.Devices["COM4"] = () => new FakePort { Vendor = ProII, SensorDelayMs = 1200 };
            b.Install();
            WireView.Touch();
            t.Check(WaitUntil(() => b.Last?.InSensorQuery == true, 3000), "the poller is inside a slow read");
            var sw = Stopwatch.StartNew();
            WireView.Release();
            t.Check(sw.ElapsedMilliseconds < 200, $"Release returned at once ({sw.ElapsedMilliseconds} ms), not after the read");
            t.Check(WaitUntil(() => b.Last!.Disposed, 4000), "the port is closed once the read finishes");
        }

        t.Section("Shutdown under a tick slower than the drain does not crash the process");
        {
            var b = new Bench { Ports = { "COM4" } };
            b.Devices["COM4"] = () => new FakePort { Vendor = ProII, SensorDelayMs = 2600 };
            b.Install();
            WireView.Touch();
            t.Check(WaitUntil(() => b.Last?.InSensorQuery == true, 3000), "the poller is inside a 2.6 s read");
            var sw = Stopwatch.StartNew();
            WireView.Shutdown();   // the drain gives up at 2 s; the tick finishes after
            t.Check(sw.ElapsedMilliseconds < 6000, $"Shutdown is bounded ({sw.ElapsedMilliseconds} ms)");
            Thread.Sleep(1000);    // let the timer finish its callback: the old drain crashed here
            t.Check(b.Last!.Disposed, "the port is closed");
            int opens = b.OpensOf("COM4");
            WireView.Touch();
            Thread.Sleep(300);
            t.Equal(opens, b.OpensOf("COM4"), "no polling after Shutdown");
        }
    }
}

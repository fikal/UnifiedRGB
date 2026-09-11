using System.Net;
using System.Net.Sockets;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Net;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| OpenRGB bridge vs. our own SDK server. Both live on 6742 by |
| default and both speak the same protocol, so the app needs  |
| a way to tell them apart that is not a TCP probe. These     |
| tests pin that (IsOwnListener), the Logitech "covered only  |
| if we actually opened it" rule, and Stop waiting for client |
| threads to leave host callbacks.                            |
|                                                             |
| Listed in Suites.cs as "OpenRgbBridge" and run by name.     |
\*-----------------------------------------------------------*/
static class OpenRgbBridgeSuite
{
    public static void Run(Harness t)
    {
        OwnListenerRegistry(t);
        BridgeProbeIgnoresOurOwnServer(t);
        LogitechCoveredOnlyWhenClaimed(t);
        StopWaitsForCallbacks(t);
        StopGivesUpAfterBudget(t);
        StopFromInsideCallbackReturns(t);
    }

    /*---------------- (a) the own-port registry ----------------*/
    static void OwnListenerRegistry(Harness t)
    {
        t.Section("(a) the own-port registry");
        int port = FreePort();
        t.Check(!OpenRgbServer.IsOwnListener(port), "orgb own: a port nobody bound is not ours");

        var server = new OpenRgbServer(new StubOrgbHost());
        int bound = server.Start(listenOnLan: false, port: port);
        t.Check(bound == port, $"orgb own: bound the requested port {port}");
        t.Check(OpenRgbServer.IsOwnListener(port), "orgb own: a running server's port is ours");

        server.Stop();
        t.Check(!OpenRgbServer.IsOwnListener(port), "orgb own: Stop takes the port out of the registry");

        // Stop twice: the second must not throw or touch the registry (it
        // never had a listener to unregister).
        server.Stop();
        t.Check(!OpenRgbServer.IsOwnListener(port), "orgb own: a second Stop is harmless");

        // Start after Stop on the same instance re-registers.
        bound = server.Start(listenOnLan: false, port: port);
        t.Check(bound == port && OpenRgbServer.IsOwnListener(port), "orgb own: restarting registers again");
        server.Dispose();
        t.Check(!OpenRgbServer.IsOwnListener(port), "orgb own: Dispose unregisters too");
    }

    /*---------------- (b) the bridge probe on 6742 ----------------*/
    static void BridgeProbeIgnoresOurOwnServer(Harness t)
    {
        t.Section("(b) the bridge probe on 6742");
        const int port = OpenRgbManager.Port;   // 6742
        // Someone's OpenRGB (or another app) may genuinely own the port on the
        // build machine; that is not a failure of this code, so skip with a
        // note rather than fail.
        if (!PortIsFree(port))
        {
            Console.WriteLine($"  note  orgb probe: port {port} is busy on this machine; skipping the self-listener probe test");
            t.Check(true, "orgb probe: skipped, port busy");
            return;
        }

        using var server = new OpenRgbServer(new StubOrgbHost());
        int bound = server.Start(listenOnLan: false, port: port);
        if (bound != port)
        {
            // Free a moment ago, taken now: a race with something else on the
            // machine, not a bug here.
            Console.WriteLine($"  note  orgb probe: port {port} was taken between the probe and the bind; skipping");
            t.Check(true, "orgb probe: skipped, port taken");
            return;
        }

        // The raw probe says "something answers": true, and correct.
        t.Check(OpenRgbClient.IsServerUp(port: port), "orgb probe: the raw TCP probe sees our server");
        // The bridge's question is "is there an OpenRGB backend": no.
        t.Check(!OpenRgbManager.IsServerUp(), "orgb probe: OpenRgbManager.IsServerUp is false for our own SDK server");
        // And the bridge must not proxy our own devices back to ourselves.
        var bridged = OpenRgbLink.DetectAll();
        t.Check(bridged.Count == 0, "orgb probe: DetectAll bridges nothing from our own server");

        server.Stop();
        t.Check(!OpenRgbClient.IsServerUp(port: port), "orgb probe: after Stop nothing answers on the port");
    }

    /*---------------- (c) Logitech: covered only when claimed ----------------*/
    static void LogitechCoveredOnlyWhenClaimed(Harness t)
    {
        t.Section("(c) Logitech: covered only when claimed");
        LogitechG403.ClearClaimed();
        try
        {
            var g403 = RemoteDevice("G403 HERO", @"HID: \\?\hid#vid_046d&pid_c08f&mi_01&col01#8&2b4f1e1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");
            var g502 = RemoteDevice("G502", @"HID: \\?\hid#vid_046d&pid_c539&mi_02#{4d1e55b2-f16f-11cf-88cb-001111000030}");
            var strafe = RemoteDevice("Strafe MK.2", @"HID: \\?\hid#vid_1b1c&pid_1b48&mi_01#{4d1e55b2-f16f-11cf-88cb-001111000030}");

            t.Check(!OpenRgbLink.IsNativelyCovered(g403), "orgb logi: a Logitech device is NOT covered when nothing native claimed it");
            t.Check(OpenRgbLink.IsNativelyCovered(strafe), "orgb logi: the Corsair Strafe is still covered by the fixed table");

            LogitechG403.NoteClaimed(0xC08F);
            t.Check(LogitechG403.ClaimedProductIds.Contains(0xC08F), "orgb logi: the claim is in the snapshot");
            t.Check(OpenRgbLink.IsNativelyCovered(g403), "orgb logi: the claimed PID is covered");
            t.Check(!OpenRgbLink.IsNativelyCovered(g502), "orgb logi: a different Logitech PID is still free for the bridge");
            t.Check(OpenRgbLink.IsNativelyCovered(strafe), "orgb logi: the Corsair rule is unaffected by the Logitech set");

            LogitechG403.ClearClaimed();
            t.Check(!OpenRgbLink.IsNativelyCovered(g403), "orgb logi: clearing the pass frees it again");
        }
        finally
        {
            LogitechG403.ClearClaimed();   // never leak a fake claim into later tests
        }
    }

    /*---------------- (d) Stop waits for in-flight callbacks ----------------*/

    /// <summary>A host whose PushExternal parks until the test says go, so the
    /// test can hold a client thread INSIDE a host callback deterministically:
    /// no sleeping and hoping.</summary>
    sealed class BlockingHost : IOpenRgbHost
    {
        readonly List<IRgbDevice> _devices = new();
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        public volatile bool Exited;
        /// <summary>When set, PushExternal calls Stop on it (the "host reacts
        /// by stopping the server" case) instead of parking.</summary>
        public OpenRgbServer? StopFromCallback;
        public volatile bool StopReturned;
        public long StopTookMs = -1;

        public void Add(IRgbDevice d) => _devices.Add(d);
        public IReadOnlyList<IRgbDevice> Devices => _devices;
        public IReadOnlyList<Rgb> ColorsOf(IRgbDevice device) => new Rgb[device.LedCount];
        public void BeginExternal(IRgbDevice device) { }
        public void EndExternal(IRgbDevice device) { }
        public void ResetExternal() { }

        public void PushExternal(IRgbDevice device, int offset, IReadOnlyList<Rgb> colors)
        {
            Entered.Set();
            if (StopFromCallback is { } s)
            {
                long t0 = Environment.TickCount64;
                s.Stop();
                StopTookMs = Environment.TickCount64 - t0;
                StopReturned = true;
                return;
            }
            Release.Wait();
            Exited = true;
        }
    }

    static void StopWaitsForCallbacks(Harness t)
    {
        t.Section("(d) Stop waits for in-flight callbacks");
        var host = new BlockingHost();
        host.Add(new FakeZonedDevice { Name = "Board", Zones2 = new[] { ("Header", 3) } });
        var server = new OpenRgbServer(host);
        int port = server.Start(listenOnLan: false, port: FreePort());
        t.Check(port > 0, "orgb stop-wait: server started");

        using var client = OpenRgbClient.Connect("127.0.0.1", port);
        client.SetCustomMode(0);
        client.UpdateLeds(0, new[] { new Rgb(1, 2, 3), new Rgb(4, 5, 6), new Rgb(7, 8, 9) });
        t.Check(host.Entered.Wait(3000), "orgb stop-wait: the client thread is inside PushExternal");

        // Stop on another thread: it must block while the callback is running.
        bool exitedBeforeStopReturned = false;
        var stop = Task.Run(() => { server.Stop(); exitedBeforeStopReturned = host.Exited; });
        // 150 ms is well inside the 500 ms budget: if Stop has returned by now
        // it did not wait at all.
        t.Check(!stop.Wait(150), "orgb stop-wait: Stop is still waiting while the callback is in flight");

        host.Release.Set();
        t.Check(stop.Wait(3000), "orgb stop-wait: Stop returns once the callback finishes");
        t.Check(exitedBeforeStopReturned, "orgb stop-wait: the callback had finished before Stop returned");
        t.Check(!OpenRgbServer.IsOwnListener(port), "orgb stop-wait: and the port is unregistered");
    }

    static void StopGivesUpAfterBudget(Harness t)
    {
        t.Section("Stop gives up after its budget");
        var host = new BlockingHost();
        host.Add(new FakeZonedDevice { Name = "Board", Zones2 = new[] { ("Header", 1) } });
        var server = new OpenRgbServer(host);
        int port = server.Start(listenOnLan: false, port: FreePort());

        using var client = OpenRgbClient.Connect("127.0.0.1", port);
        client.SetCustomMode(0);
        client.UpdateLeds(0, new[] { new Rgb(9, 9, 9) });
        t.Check(host.Entered.Wait(3000), "orgb stop-budget: the client thread is inside PushExternal");

        // Never released: Stop must give up after its budget rather than hang
        // the caller (in the app, the UI thread) behind a stuck callback.
        long t0 = Environment.TickCount64;
        var stop = Task.Run(server.Stop);
        bool returned = stop.Wait(5000);
        long took = Environment.TickCount64 - t0;
        t.Check(returned, "orgb stop-budget: Stop returns even though the callback never finishes");
        t.Check(took >= 300, $"orgb stop-budget: it did wait first ({took} ms)");
        t.Check(took < 3000, $"orgb stop-budget: but not for long ({took} ms)");

        host.Release.Set();   // let the parked thread out so it does not outlive the test
    }

    static void StopFromInsideCallbackReturns(Harness t)
    {
        t.Section("Stop called from inside a host callback returns");
        var host = new BlockingHost();
        host.Add(new FakeZonedDevice { Name = "Board", Zones2 = new[] { ("Header", 1) } });
        var server = new OpenRgbServer(host);
        host.StopFromCallback = server;
        int port = server.Start(listenOnLan: false, port: FreePort());

        using var client = OpenRgbClient.Connect("127.0.0.1", port);
        client.SetCustomMode(0);
        client.UpdateLeds(0, new[] { new Rgb(3, 3, 3) });
        t.Check(host.Entered.Wait(3000), "orgb stop-self: the client thread is inside PushExternal");

        // Stop called ON the client thread must skip joining itself: it returns
        // at once, not after burning the whole budget, and never deadlocks.
        bool returned = SpinUntil(() => host.StopReturned, 3000);
        t.Check(returned, "orgb stop-self: Stop called from a host callback returns");
        t.Check(host.StopTookMs >= 0 && host.StopTookMs < 400, $"orgb stop-self: without waiting on its own thread ({host.StopTookMs} ms)");
        t.Check(!OpenRgbServer.IsOwnListener(port), "orgb stop-self: the port is unregistered");
    }

    /*---------------- helpers ----------------*/

    static OpenRgbClient.DeviceInfo RemoteDevice(string name, string location) =>
        new(0, 5, name, "", "", "", "", location, Array.Empty<OpenRgbClient.ZoneInfo>(), 2, new uint[2]);

    /// <summary>A port the OS says is free right now. Bind on 0, read it back,
    /// release it; the tiny window before the server re-binds it is the same
    /// one every ephemeral-port test lives with.</summary>
    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static bool PortIsFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    static bool SpinUntil(Func<bool> until, int timeoutMs)
    {
        long end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end) { if (until()) return true; Thread.Sleep(10); }
        return until();
    }
}

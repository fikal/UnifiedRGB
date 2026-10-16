using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnifiedRgb.App;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Net;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The three items the 2026-10 fix pass left open: the Chroma   |
| REST origin policy, the Chroma pipe's slots, and the pump    |
| LCD's memory of backgrounds that did not load.               |
|                                                              |
| As in FixMisc, the servers here run on a spare port and a    |
| pipe name of their own: the running app holds :54235 and     |
| UnifiedRgbChroma, and nothing here may touch either. The LCD |
| section runs on an STA against a controller with a fake      |
| panel that is never started.                                 |
\*-----------------------------------------------------------*/
static class RemainingServersSuite
{
    public static void Run(Harness t)
    {
        OriginPolicyTable(t);
        OriginPolicyEndToEnd(t);
        IdlePipeClientGivesUpItsSlot(t);
        BusyPipeClientsKeepTheirSlots(t);
        SilentPipeClientsGoFirst(t);

        Exception? failure = null;
        var sta = new Thread(() =>
        {
            try
            {
                LcdRefusedBackgroundIsRememberedPerFile(t);
                LcdChangedFileIsJudgedAfresh(t);
                LcdUnreadableBackgroundIsRetriedSoon(t);
            }
            catch (Exception ex) { failure = ex; }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        if (failure != null) throw new Exception("RemainingServers STA section failed", failure);
    }

    /*-------------------- Chroma REST: who may drive the lights --------------------*/

    // (origin, served?) - null is "no Origin header", the native clients.
    static readonly (string? Origin, bool Served, string Who)[] Origins =
    {
        (null,                                               true,  "a native client (no Origin)"),
        ("chrome-extension://abcdefghijklmnopabcdefghijklmnop", true,  "a Chrome/Edge extension (and NW.js)"),
        ("moz-extension://2b3c4d5e-1111-2222-3333-444455556666", true, "a Firefox extension"),
        ("safari-web-extension://ABCDEF01-2345-6789-ABCD-EF0123456789", true, "a Safari extension"),
        ("overwolf-extension://lkjhgfdsaqwertyuiop",         true,  "an Overwolf app"),
        ("app://game",                                       true,  "an Electron/NW.js app:// page"),
        ("file://",                                          true,  "an Electron file:// page"),
        ("http://localhost:3000",                            true,  "a page served by a local process"),
        ("https://localhost",                                true,  "https on localhost"),
        ("http://127.0.0.1:8080",                            true,  "127.0.0.1"),
        ("http://[::1]:5000",                                true,  "[::1]"),
        ("HTTP://LOCALHOST:1",                               true,  "localhost in capitals"),
        ("https://example.invalid",                          false, "a web page"),
        ("http://evil.example:54235",                        false, "a web page claiming our port"),
        ("null",                                             false, "the opaque origin a sandboxed frame forces"),
        ("NULL",                                             false, "the opaque origin in capitals"),
        ("http://localhost.evil.example",                    false, "a host that only starts with localhost"),
        ("http://127.0.0.1.nip.io",                          false, "a host that only starts with 127.0.0.1"),
        ("http://loopback",                                  false, "a host named loopback (Uri.IsLoopback says yes)"),
        ("http://10.0.0.5",                                  false, "a page on the LAN"),
        ("http://0.0.0.0",                                   false, "0.0.0.0"),
        ("http://localhost@evil.example",                    false, "credentials in front of another host"),
        ("http://localhost/page",                            false, "a path (an origin has none)"),
        ("https://example.invalid, http://localhost",        false, "two origins folded into one header"),
        ("http://localhost, https://example.invalid",        false, "two origins, the local one first"),
        ("http://local host",                                false, "a space in the host"),
        ("localhost",                                        false, "no scheme"),
        ("://localhost",                                     false, "an empty scheme"),
        ("1http://localhost",                                false, "a scheme starting with a digit"),
        ("http://",                                          false, "no host"),
    };

    static void OriginPolicyTable(Harness t)
    {
        t.Section("Chroma REST: the origin policy, row by row");
        foreach (var (origin, served, who) in Origins)
            t.Equal(served, ChromaRestServer.OriginAllowed(origin), $"{who}: {origin ?? "(none)"}");
        // Values HttpClient would not put on the wire.
        t.Check(!ChromaRestServer.OriginAllowed(""), "an empty Origin is refused");
        t.Check(!ChromaRestServer.OriginAllowed(" "), "a blank Origin is refused");
        t.Check(!ChromaRestServer.OriginAllowed("http://localhost\r\nX-Forged: 1"), "control characters are refused");
        t.Check(!ChromaRestServer.OriginAllowed("http://lоcalhost"), "a non-ASCII look-alike host is refused (browsers send punycode)");
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>C2 follow-up: the first fix refused every request that carried
    /// an Origin, which also shut out extensions, NW.js/Electron games and
    /// Overwolf apps that had always worked. Each row goes through the real
    /// listener and is judged by what reaches the feed, not just the status.</summary>
    static void OriginPolicyEndToEnd(Harness t)
    {
        t.Section("Chroma REST: extensions and local apps are served, web pages are not");
        int port = FreePort();
        if (!ChromaRestServer.Start(port))
        {
            t.Check(false, $"the REST server could not listen on spare port {port}");
            return;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string frameUrl = $"http://localhost:{port}/razer/chromasdk/1/keyboard";
            int Put(string? origin, int colorref)
            {
                var req = new HttpRequestMessage(HttpMethod.Put, frameUrl)
                {
                    Content = new StringContent($"{{\"effect\":\"CHROMA_STATIC\",\"param\":{{\"color\":{colorref}}}}}",
                        System.Text.Encoding.UTF8, "text/plain"),
                };
                if (origin != null) req.Headers.TryAddWithoutValidation("Origin", origin);
                return (int)http.SendAsync(req).GetAwaiter().GetResult().StatusCode;
            }
            static Rgb Colour(int colorref) => new((byte)(colorref & 0xFF), (byte)((colorref >> 8) & 0xFF), (byte)((colorref >> 16) & 0xFF));

            for (int i = 0; i < Origins.Length; i++)
            {
                var (origin, served, who) = Origins[i];
                int before = 0x200000 | (i + 1), sent = 0x804000 | (i + 1);
                t.Equal(200, Put(null, before), $"{who}: the native frame ahead of it is served");
                int status = Put(origin, sent);
                var shown = ChromaFeed.Sample(0.5f, 0.5f);
                if (served)
                    t.Check(status == 200 && shown == Colour(sent),
                        $"{who} ({origin ?? "no Origin"}) is served and its colour reaches the lights (got {status}, showing {shown})");
                else
                    t.Check(status == 403 && shown == Colour(before),
                        $"{who} ({origin}) gets 403 and its colour never reaches the lights (got {status}, showing {shown})");
            }

            // An extension's init gets its session like any native client.
            var init = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/razer/chromasdk")
            { Content = new StringContent("{\"title\":\"extension\"}") };
            init.Headers.TryAddWithoutValidation("Origin", "chrome-extension://abcdefghijklmnopabcdefghijklmnop");
            var reply = http.SendAsync(init).GetAwaiter().GetResult();
            t.Check((int)reply.StatusCode == 200 && reply.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("sessionid"),
                "an extension's init is answered with a session");
        }
        finally { ChromaRestServer.Stop(); }
    }

    /*-------------------- Chroma pipe: slots --------------------*/

    // Frames tagged by client: the first cell's red channel is the sender's id.
    static byte[] Frame(byte id) => new byte[] { 1, 1, 0, 1, 0, id, 0, 0, 0 };

    static bool Send(NamedPipeClientStream c, byte id)
    {
        try { c.Write(Frame(id)); c.Flush(); return true; }
        catch (IOException) { return false; }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }   // never connected
    }

    static int From(ConcurrentQueue<byte> seen, byte id) => seen.Count(x => x == id);

    sealed class PipeRig : IDisposable
    {
        public readonly string Name = "UnifiedRgbChromaTest-" + Guid.NewGuid().ToString("N");
        public readonly ConcurrentQueue<byte> Seen = new();
        public readonly ChromaPipeServer Server;
        readonly List<NamedPipeClientStream> _clients = new();

        public PipeRig(int idleEvictMs, int unprovenEvictMs = ChromaFeed.UnprovenEvictMs)
        {
            // A long first-frame deadline, so only eviction is in play here.
            Server = new ChromaPipeServer(Name, 10_000, maxClients: 2, (_, g, _, _) => Seen.Enqueue(g[0].R), idleEvictMs, unprovenEvictMs);
            Server.Start();
        }

        public NamedPipeClientStream Connect()
        {
            var c = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            c.Connect(3000);
            lock (_clients) _clients.Add(c);
            return c;
        }

        public void Dispose()
        {
            Server.Dispose();
            lock (_clients) foreach (var c in _clients) c.Dispose();
        }
    }

    /// <summary>D8 follow-up: a client that sent ONE valid frame counted as a
    /// real host and kept its slot for ever, so sixteen of them locked
    /// Wallpaper Engine out as surely as sixteen silent ones. With every slot
    /// taken, a newcomer now gets the slot of whoever has gone longest
    /// without a frame, once that is past the idle threshold.</summary>
    static void IdlePipeClientGivesUpItsSlot(Harness t)
    {
        t.Section("Chroma pipe: a client that sent one frame and went quiet gives its slot to a newcomer");
        const int idle = 600;
        using var rig = new PipeRig(idle);
        var x = rig.Connect();
        t.Check(Send(x, 1), "client 1 sends one frame");
        Thread.Sleep(250);
        var y = rig.Connect();
        t.Check(Send(y, 2), "client 2 sends one frame, a little later");
        t.Check(WaitUntil(() => rig.Seen.Count == 2 && rig.Server.ClientCount == 2, 2000), "both frames arrive and both slots are held");

        var early = rig.Connect();
        t.Check(WaitUntil(() => !Send(early, 9), 2000), "a newcomer while both have just sent is refused (neither is idle yet)");
        t.Equal(0, From(rig.Seen, 9), "...and nothing it sent was read");

        Thread.Sleep(idle + 300);   // both now idle past the threshold, client 1 the longer
        var w = rig.Connect();
        t.Check(Send(w, 3) && WaitUntil(() => From(rig.Seen, 3) == 1, 2000),
            "once they have been quiet past the threshold, a newcomer is admitted and served (was: refused for ever)");
        t.Check(WaitUntil(() => !Send(x, 1), 2000), "...in the slot of client 1, the one idle longest, whose pipe is closed");
        t.Check(Send(y, 2) && WaitUntil(() => From(rig.Seen, 2) == 2, 2000), "client 2, idle for less time, keeps its slot and is still served");
        t.Equal(2, rig.Server.ClientCount, "the slot count never goes past the cap");

        // What the shim does next: its write found the pipe closed, so it
        // reopens on its next frame, and gets in once a slot has gone quiet.
        Thread.Sleep(idle + 300);
        var back = rig.Connect();
        t.Check(Send(back, 1) && WaitUntil(() => From(rig.Seen, 1) == 2, 2000),
            "the evicted host reconnecting on its next frame is served again");

        string log = ReadShared(Log.FilePath);
        t.Check(log.Contains("dropped the client", StringComparison.Ordinal), "the eviction is logged");
    }

    /// <summary>The other half: whatever arrives, a client that keeps sending
    /// frames is never the one dropped.</summary>
    static void BusyPipeClientsKeepTheirSlots(Harness t)
    {
        t.Section("Chroma pipe: clients sending frames are never evicted, however long they have been connected");
        const int idle = 600;
        using var rig = new PipeRig(idle);
        var a = rig.Connect();
        var b = rig.Connect();
        using var stopA = new CancellationTokenSource();
        using var stopB = new CancellationTokenSource();
        int aFailed = 0, bFailed = 0;
        Thread Pump(NamedPipeClientStream c, byte id, CancellationToken stop, Action failed)
        {
            var th = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (!Send(c, id)) { failed(); return; }
                    Thread.Sleep(25);
                }
            }) { IsBackground = true };
            th.Start();
            return th;
        }
        var pa = Pump(a, 1, stopA.Token, () => Interlocked.Increment(ref aFailed));
        var pb = Pump(b, 2, stopB.Token, () => Interlocked.Increment(ref bFailed));
        try
        {
            t.Check(WaitUntil(() => From(rig.Seen, 1) > 0 && From(rig.Seen, 2) > 0, 2000), "two hosts streaming frames hold both slots");
            Thread.Sleep(idle + 300);   // connected for longer than the threshold, but never quiet for it
            var c = rig.Connect();
            t.Check(WaitUntil(() => !Send(c, 9), 2000), "a newcomer is refused while both are sending");
            t.Check(Volatile.Read(ref aFailed) == 0 && Volatile.Read(ref bFailed) == 0, "...and neither streaming host was dropped for it");

            // One host stops (Wallpaper Engine paused behind a fullscreen game).
            stopA.Cancel();
            pa.Join(2000);
            Thread.Sleep(idle + 300);
            var d = rig.Connect();
            t.Check(Send(d, 4) && WaitUntil(() => From(rig.Seen, 4) == 1, 2000), "once one host has gone quiet past the threshold, a newcomer takes its slot");
            t.Check(WaitUntil(() => !Send(a, 1), 2000), "...the quiet one's, its pipe closed");
            int beforeB = From(rig.Seen, 2);
            t.Check(WaitUntil(() => From(rig.Seen, 2) > beforeB + 5, 2000) && Volatile.Read(ref bFailed) == 0,
                "the host still streaming was never touched");
        }
        finally
        {
            stopA.Cancel(); stopB.Cancel();
            pa.Join(2000); pb.Join(2000);
        }
    }

    /// <summary>Verifier V2 (P3-f): silent connections re-opened as each was
    /// dropped kept every slot under the 30 s idle threshold for good. A real
    /// shim sends its frame in the same call that connects, so a connection
    /// still without one after a moment is the first to give its slot up -
    /// before any real host, however quiet that host has gone.</summary>
    static void SilentPipeClientsGoFirst(Harness t)
    {
        t.Section("Chroma pipe: a connection that never sent a frame gives its slot up first");
        const int silentAfter = 300;
        using var rig = new PipeRig(idleEvictMs: 60_000, unprovenEvictMs: silentAfter);
        var host = rig.Connect();
        t.Check(Send(host, 1) && WaitUntil(() => From(rig.Seen, 1) == 1, 2000), "a real host sends its frame");
        var silent = rig.Connect();
        t.Check(WaitUntil(() => rig.Server.ClientCount == 2, 2000), "a silent connection takes the other slot");
        Thread.Sleep(silentAfter + 300);
        var newcomer = rig.Connect();
        t.Check(Send(newcomer, 3) && WaitUntil(() => From(rig.Seen, 3) == 1, 2000),
            "past the short silent limit a newcomer is admitted (the host is far from its 60 s idle limit)");
        t.Check(WaitUntil(() => !Send(silent, 9), 2000), "...in the silent connection's slot, its pipe closed");
        t.Check(Send(host, 1) && WaitUntil(() => From(rig.Seen, 1) == 2, 2000), "...and the real host keeps its slot and is still served");
        t.Equal(2, rig.Server.ClientCount, "the slot count never goes past the cap");
    }

    static string ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd();
        }
        catch (IOException) { return ""; }   // no log yet: the check that reads it fails and says so
    }

    /*-------------------- pump LCD: backgrounds that did not load --------------------*/

    static LcdController Panel() =>
        new(new UnifiedRgb.Core.Devices.ThermalrightLcd(new FakeHid())) { On = false, BackgroundStatEvery = TimeSpan.Zero };

    static string WritePng(string dir, string name, int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 40; px[i + 1] = 120; px[i + 2] = 200; px[i + 3] = 255; }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4)));
        string path = Path.Combine(dir, name);
        using (var fs = File.Create(path)) enc.Save(fs);
        return path;
    }

    /// <summary>A few hundred bytes of JPEG whose frame header claims
    /// 60000x60000: refused for its size before a pixel is decoded.</summary>
    static string WriteBomb(string dir, string name)
    {
        var jpeg = new JpegBitmapEncoder();
        jpeg.Frames.Add(BitmapFrame.Create(BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgr32, null, new byte[8 * 8 * 4], 8 * 4)));
        byte[] bomb;
        using (var ms = new MemoryStream()) { jpeg.Save(ms); bomb = ms.ToArray(); }
        int sof = -1;
        for (int i = 0; i + 1 < bomb.Length && sof < 0; i++) if (bomb[i] == 0xFF && bomb[i + 1] == 0xC0) sof = i;
        if (sof < 0) throw new InvalidOperationException("the encoded JPEG has no baseline frame header to forge");
        bomb[sof + 5] = 60000 >> 8; bomb[sof + 6] = 60000 & 0xFF;
        bomb[sof + 7] = 60000 >> 8; bomb[sof + 8] = 60000 & 0xFF;
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bomb);
        return path;
    }

    static string WriteJunk(string dir, string name, int length)
    {
        var bytes = new byte[length];
        bytes[0] = 0x89; bytes[1] = (byte)'P'; bytes[2] = (byte)'N'; bytes[3] = (byte)'G';
        for (int i = 4; i < length; i++) bytes[i] = (byte)i;
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    static int Remembered(LcdController lcd) =>
        (typeof(LcdController).GetField("_bgFailures", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lcd)
            as System.Collections.ICollection)?.Count ?? -1;

    /// <summary>The failure memory held ONE file and was cleared whenever any
    /// other background loaded, so a scene sequence alternating with a
    /// refused file decoded it again, and logged it again, at every step.</summary>
    static void LcdRefusedBackgroundIsRememberedPerFile(Harness t)
    {
        t.Section("pump LCD: a refused background in a scene sequence is decoded once, not at every step");
        string dir = TempDir();
        using var lcd = Panel();
        // A retry back-off of next to nothing: a failure wrongly taken for a
        // passing one (a locked file) would be decoded again at every step.
        lcd.BackgroundRetryFirst = TimeSpan.FromMilliseconds(1);
        try
        {
            string good = WritePng(dir, "good.png", 64, 48);
            string bomb = WriteBomb(dir, "bomb.jpg");
            string junk = WriteJunk(dir, "junk.png", 40);

            int bombDecodes = 0, junkDecodes = 0, goodShown = 0, problemsRight = 0;
            const int steps = 5;
            for (int i = 0; i < steps; i++)
            {
                int before = lcd.BackgroundDecodes;
                lcd.Design.BackgroundImagePath = bomb;
                bool bombRight = lcd.Background == null && lcd.BackgroundProblem?.Contains("60000x60000") == true;
                bombDecodes += lcd.BackgroundDecodes - before;

                lcd.Design.BackgroundImagePath = good;
                bool goodRight = lcd.Background != null && lcd.BackgroundProblem == null;
                if (lcd.Background != null) goodShown++;

                before = lcd.BackgroundDecodes;
                lcd.Design.BackgroundImagePath = junk;
                bool junkRight = lcd.Background == null && lcd.BackgroundProblem?.Contains("could not be decoded") == true;
                junkDecodes += lcd.BackgroundDecodes - before;

                lcd.Design.BackgroundImagePath = good;
                goodRight &= lcd.Background != null && lcd.BackgroundProblem == null;
                if (bombRight && goodRight && junkRight) problemsRight++;
            }
            t.Equal(1, bombDecodes, $"the refused file is decoded once over {steps} rounds of the sequence (was: every step)");
            t.Equal(1, junkDecodes, "...and so is one that will not decode");
            t.Equal(steps, goodShown, "the good background between them shows every time");
            t.Equal(steps, problemsRight, "the designer's problem line follows the background on show: the refusal, nothing, the decode error, nothing");

            // Waiting does not earn a permanent failure another decode.
            Thread.Sleep(50);
            int was = lcd.BackgroundDecodes;
            lcd.Design.BackgroundImagePath = junk; _ = lcd.Background;
            lcd.Design.BackgroundImagePath = bomb; _ = lcd.Background;
            t.Equal(was, lcd.BackgroundDecodes, "an undecodable or refused file is not retried on a timer, only when it changes");

            // The problem goes with the background.
            lcd.Design.BackgroundImagePath = null;
            t.Check(lcd.Background == null && lcd.BackgroundProblem == null, "removing a refused background clears its problem (was: left on the designer)");
            lcd.Design.BackgroundImagePath = bomb;
            was = lcd.BackgroundDecodes;
            t.Check(lcd.Background == null && lcd.BackgroundProblem?.Contains("60000x60000") == true && lcd.BackgroundDecodes == was,
                "putting it back shows the problem again, from memory");
            lcd.Design.BackgroundImagePath = Path.Combine(dir, "not-there.png");
            t.Check(lcd.Background == null && lcd.BackgroundProblem == null, "a missing file shows nothing and reports nothing");

            // Bounded: a sequence through many failing files.
            for (int i = 0; i < 40; i++)
            {
                lcd.Design.BackgroundImagePath = WriteJunk(dir, $"junk{i}.png", 30 + i);
                _ = lcd.Background;
            }
            int held = Remembered(lcd);
            t.Check(held > 0 && held <= 16, $"the memory is bounded ({held} of 40 failing files remembered)");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>The memory is per version of a file: its size and write time.
    /// A change to either is a new file to judge.</summary>
    static void LcdChangedFileIsJudgedAfresh(Harness t)
    {
        t.Section("pump LCD: a refused background that changes is tried again");
        string dir = TempDir();
        using var lcd = Panel();
        try
        {
            string path = WriteJunk(dir, "scene.png", 40);
            var written = File.GetLastWriteTimeUtc(path);
            lcd.Design.BackgroundImagePath = path;
            t.Check(lcd.Background == null, "an undecodable file shows nothing");
            int tried = lcd.BackgroundDecodes;
            _ = lcd.Background;
            t.Equal(tried, lcd.BackgroundDecodes, "...and is not decoded again while unchanged");

            // Re-written with another length but the same write time (a tool
            // that preserves timestamps, or a coarse clock).
            WriteJunk(dir, "scene.png", 80);
            File.SetLastWriteTimeUtc(path, written);
            _ = lcd.Background;
            t.Equal(tried + 1, lcd.BackgroundDecodes, "a new size alone earns a new decode");

            File.SetLastWriteTimeUtc(path, written.AddMinutes(1));
            _ = lcd.Background;
            t.Equal(tried + 2, lcd.BackgroundDecodes, "so does a new write time alone");

            File.Copy(WritePng(dir, "fixed.png", 32, 24), path, overwrite: true);
            File.SetLastWriteTimeUtc(path, written.AddMinutes(2));
            t.Check(lcd.Background != null && lcd.BackgroundProblem == null, "re-saved as a good image, it shows and the problem clears");
            t.Equal(0, Remembered(lcd), "...and is forgotten as a failure");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>A file that could not be READ (an editor holding it while it
    /// saves) was put on the same back-off as a broken one, growing to an hour;
    /// before the fix pass it was a flat 30 s. Now it is retried soon, never
    /// more than 30 s apart.</summary>
    static void LcdUnreadableBackgroundIsRetriedSoon(Harness t)
    {
        t.Section("pump LCD: a background that is only locked is retried within 30 s");
        var cap = TimeSpan.FromSeconds(30);
        var first = TimeSpan.FromSeconds(2);
        t.Equal(first, LcdController.RetryDelay(1, first), "the first retry waits the first step");
        bool capped = true, rising = true;
        var prev = TimeSpan.Zero;
        for (int n = 1; n <= 1000; n++)
        {
            var d = LcdController.RetryDelay(n, first);
            capped &= d <= cap;
            rising &= d >= prev;
            prev = d;
        }
        t.Check(capped && rising, "the back-off grows and never passes 30 s, however many failures (was: up to an hour)");
        t.Equal(cap, LcdController.RetryDelay(6, first), "...reaching 30 s after a few failures");

        string dir = TempDir();
        using var lcd = Panel();
        lcd.BackgroundRetryFirst = TimeSpan.FromMilliseconds(800);
        try
        {
            string path = WritePng(dir, "locked.png", 64, 48);
            string other = WritePng(dir, "other.png", 32, 24);
            var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try
            {
                lcd.Design.BackgroundImagePath = path;
                t.Check(lcd.Background == null && lcd.BackgroundProblem?.Contains("could not be read") == true,
                    $"a locked file shows nothing and says it could not be read ({lcd.BackgroundProblem})");
                int tried = lcd.BackgroundDecodes;
                for (int i = 0; i < 3; i++)
                {
                    _ = lcd.Background;
                    lcd.Design.BackgroundImagePath = other; _ = lcd.Background;
                    lcd.Design.BackgroundImagePath = path; _ = lcd.Background;
                }
                int otherDecodes = 3;
                t.Equal(tried + otherDecodes, lcd.BackgroundDecodes, "inside its back-off it is not tried again, even between other backgrounds");

                Thread.Sleep(1000);
                int beforeRetry = lcd.BackgroundDecodes;
                _ = lcd.Background;
                t.Equal(beforeRetry + 1, lcd.BackgroundDecodes, "once the back-off has run it is tried again");
            }
            finally { hold.Dispose(); }

            Thread.Sleep(1800);   // past the second step of the back-off (twice the first)
            t.Check(lcd.Background != null && lcd.BackgroundProblem == null, "unlocked, the next try shows it and the problem clears");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}

using System.IO;
using System.Text;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Net;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The wire and the socket: everything about talking OpenRGB.   |
|                                                              |
| The first half is format work with no IO at all. It builds a |
| v1 controller blob byte for byte and parses it back, checks  |
| the bounds handling of the matrix a real server sends, and   |
| covers the two small pieces of housekeeping that sit beside  |
| the protocol, the client-name sanitiser and the detector     |
| config file we rewrite for the user.                         |
|                                                              |
| The second half stands up a real OpenRgbServer on a loopback |
| port and drives it with our own client, because framing,     |
| version negotiation and the ownership handoff only exist     |
| once two ends are actually connected. Those sections are the |
| slowest in the project: they bind sockets and then wait on   |
| work finishing on other threads, which is why the runner     |
| puts this suite near the end of the order rather than making |
| every fast suite queue behind it.                            |
\*-----------------------------------------------------------*/
static class NetSuite
{
    public static void Run(Harness t)
    {
        t.Section("OpenRGB v1 device blob parsing");
        {
            // Build a synthetic v1 controller blob byte-for-byte, mirroring the wire
            // format the client parses: this doubles as documentation of the format.
            var b = new List<byte>();
            void U16(int v) { b.Add((byte)(v & 0xFF)); b.Add((byte)(v >> 8 & 0xFF)); }
            void I32(int v) { b.AddRange(BitConverter.GetBytes(v)); }
            void Str(string s) { var raw = Encoding.ASCII.GetBytes(s + "\0"); U16(raw.Length); b.AddRange(raw); }

            I32(0);                       // placeholder for duplicate size u32
            I32(5);                       // type = keyboard
            Str("Test Keyboard");
            Str("TestVendor");            // vendor (v1+)
            Str("desc"); Str("1.0"); Str("SER123"); Str("HID: vid_1234&pid_5678");
            U16(1);                       // one mode
            I32(0);                       // active mode
            Str("Direct");
            for (int i = 0; i < 9; i++) I32(0);   // value..color_mode (9 u32 fields)
            U16(2); I32(0x00FF00FF); I32(0);      // 2 mode colors
            U16(2);                       // two zones
            Str("Matrix Zone"); I32(2);   // type matrix
            I32(0); I32(6); I32(6);       // leds min/max/count
            U16(1);                       // matrix present
            I32(2); I32(3);               // h=2, w=3
            for (uint i = 0; i < 6; i++) I32((int)i);
            Str("Linear Zone"); I32(1);
            I32(0); I32(4); I32(4);
            U16(0);                       // no matrix
            U16(10);                      // 10 leds
            for (int i = 0; i < 10; i++) { Str($"LED {i}"); I32(i); }
            U16(10);                      // 10 colors
            for (int i = 0; i < 10; i++) I32(0x00123456);
            var blob = b.ToArray();
            BitConverter.GetBytes(blob.Length).CopyTo(blob, 0);

            var d = OpenRgbClient.ParseDevice(7, blob);
            t.Equal(7, d.Index, "blob index");
            t.Equal(5, d.Type, "blob type");
            t.Equal("Test Keyboard", d.Name, "blob name");
            t.Equal("TestVendor", d.Vendor, "blob vendor");
            t.Equal("HID: vid_1234&pid_5678", d.Location, "blob location");
            t.Equal(2, d.Zones.Count, "blob zone count");
            t.Equal("Matrix Zone", d.Zones[0].Name, "blob zone 0 name");
            t.Equal(3, d.Zones[0].MatrixW, "blob matrix width");
            t.Equal(2, d.Zones[0].MatrixH, "blob matrix height");
            t.Check(d.Zones[0].Matrix != null && d.Zones[0].Matrix!.Length == 6, "blob matrix cells");
            t.Equal(4, d.Zones[1].LedCount, "blob zone 1 leds");
            t.Check(d.Zones[1].Matrix == null, "blob zone 1 no matrix");
            t.Equal(10, d.LedCount, "blob led count");
            t.Equal(10, d.Colors.Length, "blob colors");
            t.Equal(0x56u, d.Colors[0] & 0xFF, "blob color R byte");
        }

        t.Section("OpenRgbDevice.BuildPositions bounds (#30)");
        {
            var zone = new OpenRgbClient.ZoneInfo("Keys", 2, 6, 3, 2, new uint[] { 0, 1, 0x80000000, 0xFFFFFFFF, 4, 0x7FFFFFFF });
            var info = new OpenRgbClient.DeviceInfo(0, 5, "Fake", "V", "", "", "", "loc", new[] { zone }, 6, new uint[6]);
            OpenRgbDevice? dev = null;
            bool threw = false;
            try { dev = new OpenRgbDevice(null!, info); } catch (Exception) { threw = true; }
            t.Check(!threw && dev != null, "OpenRgbDevice ctor survives out-of-range matrix cells (SetCustomMode NRE is caught)");
            if (dev != null)
            {
                t.Equal(6, dev.LedPositions!.Count, "positions sized to the LED count");
                t.Check(dev.LedPositions[0] == new LedPos(0, 0) && dev.LedPositions[1] == new LedPos(0.5f, 0) && dev.LedPositions[4] == new LedPos(0.5f, 1),
                    "in-range matrix cells are positioned");
                t.Check(dev.LedPositions[2] == default && dev.LedPositions[3] == default && dev.LedPositions[5] == default,
                    "sentinel / negative-as-uint cells are skipped");
                t.Equal(1.5f, dev.PreviewAspect, "aspect from the first matrix zone");
                t.Equal("Fake (OpenRGB)", dev.Name, "bridged name suffix");
                t.Equal(6, dev.Zones[0].Count, "zone count preserved");
            }
            var neg = new OpenRgbClient.DeviceInfo(1, 4, "Neg", "", "", "", "", "",
                new[] { new OpenRgbClient.ZoneInfo("Z", 1, -5, 0, 0, null), new OpenRgbClient.ZoneInfo("Y", 1, 3, 0, 0, null) }, 3, new uint[3]);
            threw = false;
            try { dev = new OpenRgbDevice(null!, neg); } catch (Exception) { threw = true; }
            t.Check(!threw && dev != null && dev.Zones[0].Count == 0 && dev.Zones[1].Offset == 0 && dev.Zones[1].Count == 3,
                "negative server LedCount clamps to 0 and does not poison the next zone's offset");
            t.Check(dev != null && dev.LedPositions!.Count == 3 && dev.LedPositions[2] == new LedPos(1f, 0.5f), "linear zone positions after a clamped zone");
        }

        t.Section("OpenRgbServer.CleanClientName (S3)");
        {
            string forged = OpenRgbServer.CleanClientName("evil\r\n09-07 12:00:00 ERR name");
            t.Check(!forged.Contains('\r') && !forged.Contains('\n'),
                "a name cannot forge a second log line");
            t.Equal("evil  09-07 12:00:00 ERR name", forged,
                "the control characters become spaces, so the forged text stays visible on one line");
            t.Equal("a b", OpenRgbServer.CleanClientName("a\tb"), "tabs become spaces");
            t.Equal("unnamed", OpenRgbServer.CleanClientName(""), "empty falls back to unnamed");
            t.Equal("unnamed", OpenRgbServer.CleanClientName("\0\0"), "NULs alone fall back to unnamed");
            t.Equal("unnamed", OpenRgbServer.CleanClientName("\r\n"), "control-only falls back to unnamed");
            t.Equal(64, OpenRgbServer.CleanClientName(new string('x', 500)).Length, "a huge name is capped");
            t.Equal("Home Assistant", OpenRgbServer.CleanClientName("  Home Assistant\0"),
                "an ordinary name is untouched");
        }

        t.Section("OpenRgbDetectorConfig (#145)");
        {
            string dir = TempDir();
            try
            {
                string file = Path.Combine(dir, "OpenRGB.json");
                File.WriteAllText(file, "{\"Detectors\":{\"detectors\":{\"A\":true,\"B\":false,\"C\":true}}}");
                t.Check(OpenRgbDetectorConfig.Enabled(dir).SequenceEqual(new[] { "A", "C" }), "Enabled lists the true detectors");
                t.Check(OpenRgbDetectorConfig.Edit(dir, d => OpenRgbDetectorConfig.Set(d, new[] { "A" }, false)), "Edit writes when Set changed a value");
                t.Check(OpenRgbDetectorConfig.Enabled(dir).SequenceEqual(new[] { "C" }), "the edit landed on disk");
                t.Check(!OpenRgbDetectorConfig.Edit(dir, d => OpenRgbDetectorConfig.Set(d, new[] { "A" }, false)), "Edit is a no-op when nothing changes");
                t.Check(!File.Exists(file + ".tmp"), "detector edit leaves no .tmp");
                t.Check(OpenRgbDetectorConfig.Edit(dir, d => OpenRgbDetectorConfig.Set(d, new[] { "A", "Z" }, true)), "Set adds an unknown name");
                t.Check(OpenRgbDetectorConfig.Enabled(dir).SequenceEqual(new[] { "A", "C", "Z" }), "new detector entries persist");
                t.Check(File.ReadAllText(file).Contains("\"B\": false"), "untouched entries survive the rewrite (indented)");

                string missing = Path.Combine(dir, "fresh");
                Directory.CreateDirectory(missing);
                t.Check(!OpenRgbDetectorConfig.Edit(missing, d => OpenRgbDetectorConfig.Set(d, new[] { "X" }, false)) && !File.Exists(Path.Combine(missing, "OpenRGB.json")),
                    "Edit on a missing file without createIfMissing writes nothing");
                t.Check(OpenRgbDetectorConfig.Edit(missing, d => OpenRgbDetectorConfig.Set(d, new[] { "X" }, false), createIfMissing: true)
                      && File.Exists(Path.Combine(missing, "OpenRGB.json")), "createIfMissing builds the skeleton");
                t.Check(OpenRgbDetectorConfig.Enabled(missing).Count == 0 && File.ReadAllText(Path.Combine(missing, "OpenRGB.json")).Contains("\"X\": false"),
                    "skeleton carries the disabled entry");
                t.Equal(0, OpenRgbDetectorConfig.Enabled(Path.Combine(dir, "nowhere")).Count, "Enabled on a missing file is empty");

                File.WriteAllText(file, "{\"Detectors\":{}}");
                t.Check(OpenRgbDetectorConfig.Enabled(dir).Count == 0, "Enabled on a missing section is empty");
                t.Check(!OpenRgbDetectorConfig.Edit(dir, d => true), "Edit on a missing section without createIfMissing is a no-op");
                File.WriteAllText(file, "{not json");
                t.Check(OpenRgbDetectorConfig.Enabled(dir).Count == 0, "Enabled on malformed json is empty, not a throw");
                bool threw = false;
                try { OpenRgbDetectorConfig.Edit(dir, d => true); } catch (Exception) { threw = true; }
                t.Check(threw, "Edit on malformed json throws (callers own the log line)");
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }
        }

        t.Section("OpenRGB SDK server (#f7)");
        {
            // The header is the only thing separating a real client from whatever else
            // finds the port.
            var head = new byte[OpenRgbProtocol.HeaderBytes];
            OpenRgbProtocol.WriteHeader(head, 3, OpenRgbProtocol.PktUpdateLeds, 42);
            var read = OpenRgbProtocol.ReadHeader(head);
            t.Check(read is { Device: 3, PacketId: 1050, Size: 42 }, "orgb: header round-trips");
            t.Equal((byte)'O', head[0], "orgb: magic");
            head[1] = (byte)'X';
            t.Check(OpenRgbProtocol.ReadHeader(head) == null, "orgb: a bad magic is rejected");
            t.Check(OpenRgbProtocol.ReadHeader(new byte[4]) == null, "orgb: a short header is rejected");

            // Colors are 0x00BBGGRR on the wire.
            t.Equal(0x00FF5030u, OpenRgbProtocol.ToWire(new Rgb(0x30, 0x50, 0xFF)), "orgb: color packs as BGR");
            t.Equal(new Rgb(0x30, 0x50, 0xFF), OpenRgbProtocol.FromWire(0x00FF5030u), "orgb: and unpacks again");

            t.Equal(0, OpenRgbProtocol.DeviceTypeOf(DeviceType.Motherboard), "orgb: motherboard is 0");
            t.Equal(1, OpenRgbProtocol.DeviceTypeOf(DeviceType.Dram), "orgb: dram is 1");
            t.Equal(5, OpenRgbProtocol.DeviceTypeOf(DeviceType.Keyboard), "orgb: keyboard is 5");
            t.Equal(3, OpenRgbProtocol.DeviceTypeOf(DeviceType.Fan), "orgb: fans read as coolers");

            // The blob has to be the exact inverse of the parser we already ship, so
            // round-trip it through that rather than re-asserting the layout by hand.
            var zoned = new FakeZonedDevice
            {
                Name = "Test Board",
                Zones2 = new[] { ("Header 1", 8), ("Header 2", 4), ("Logo", 1) },
            };
            var frame = new Rgb[13];
            for (int i = 0; i < frame.Length; i++) frame[i] = new Rgb((byte)(i * 5), (byte)(i * 3), (byte)i);

            var blob = OpenRgbProtocol.WriteDevice(zoned, frame, 1);
            var parsed = OpenRgbClient.ParseDevice(0, blob);
            t.Equal("Test Board", parsed.Name, "orgb: name survives");
            t.Equal("Test", parsed.Vendor, "orgb: vendor survives");
            t.Equal(13, parsed.LedCount, "orgb: led count survives");
            t.Equal(3, parsed.Zones.Count, "orgb: zone count survives");
            t.Equal("Header 1", parsed.Zones[0].Name, "orgb: zone name survives");
            t.Equal(8, parsed.Zones[0].LedCount, "orgb: zone size survives");
            t.Equal(1, parsed.Zones[1].Type, "orgb: a multi-led zone is linear");
            t.Equal(0, parsed.Zones[2].Type, "orgb: a single-led zone is single");
            t.Equal(13, parsed.Colors.Length, "orgb: a color per led");
            t.Equal(OpenRgbProtocol.ToWire(frame[7]), parsed.Colors[7], "orgb: the colors are the frame");

            // v0 has no vendor field. Writing the v1 blob to a v0 client would shift
            // every string after the name by one field.
            var v0 = OpenRgbProtocol.WriteDevice(zoned, frame, 0);
            t.Check(v0.Length < blob.Length, "orgb: the v0 blob omits the vendor string");

            // A device whose zones do not tile its LEDs would let a client write past
            // the end of one, so it is collapsed to a single zone instead.
            var ragged = new FakeZonedDevice { Name = "Ragged", Zones2 = new[] { ("Partial", 3) }, Leds = 10 };
            var rzones = OpenRgbProtocol.ZonesOf(ragged);
            t.Equal(1, rzones.Count, "orgb: zones that do not cover the device collapse to one");
            t.Equal(10, rzones[0].Count, "orgb: and that one covers everything");

            var none = new FakeZonedDevice { Name = "Bare", Zones2 = Array.Empty<(string, int)>(), Leds = 4 };
            t.Equal(1, OpenRgbProtocol.ZonesOf(none).Count, "orgb: a device with no zones still gets one");
        }

        t.Section("SDK client handoff (#f7)");
        {
            // Writing claims a device; the claim lapses on silence or disconnect.
            var own = new ExternalOwnership<string>(silenceSeconds: 5);
            t.Check(own.Claim("board", client: 1, now: 0), "handoff: the first write claims the device");
            t.Check(!own.Claim("board", client: 1, now: 1), "handoff: the same client writing again does not re-claim");
            t.Check(own.IsOwned("board"), "handoff: and it is owned");
            t.Equal(1, own.OwnerOf("board"), "handoff: by that client");

            // Still writing: the claim holds well past the silence window.
            t.Equal(0, own.Expire(4).Count, "handoff: a live client keeps its device");
            own.Claim("board", 1, 4);
            t.Equal(0, own.Expire(8).Count, "handoff: writing again pushes the deadline out");

            // Gone quiet: released, once.
            var lapsed = own.Expire(9.1);
            t.Equal(1, lapsed.Count, "handoff: silence releases the device");
            t.Equal("board", lapsed[0], "handoff: the right one");
            t.Check(!own.IsOwned("board"), "handoff: and it is free again");
            t.Equal(0, own.Expire(100).Count, "handoff: a released device is not released twice");

            // Disconnect drops everything that client held, without waiting.
            own.Claim("board", 1, 10);
            own.Claim("ram", 1, 10);
            own.Claim("keeb", 2, 10);
            t.Equal(2, own.ReleaseClient(1).Count, "handoff: a disconnect frees only that client's devices");
            t.Check(own.IsOwned("keeb"), "handoff: the other client keeps its own");
            t.Equal(2, own.OwnerOf("keeb"), "handoff: still owned by client 2");

            // Last writer wins: there is no way to tell the loser it lost.
            t.Check(!own.Claim("keeb", 3, 11), "handoff: a takeover is not a fresh claim");
            t.Equal(3, own.OwnerOf("keeb"), "handoff: but the new client owns it");
            t.Equal(0, own.ReleaseClient(2).Count, "handoff: the old owner has nothing left to free");

            // Why a takeover must not read as a fresh claim: the caller saves the
            // user's lighting on a true and restores it on the matching release. The
            // old owner's disconnect frees nothing, so a second true would never be
            // balanced and the lighting would never come back at all.
            var pair = new ExternalOwnership<string>(silenceSeconds: 5);
            int taken = 0;
            if (pair.Claim("ram", 1, 0)) taken++;
            if (pair.Claim("ram", 2, 1)) taken++;      // client 2 takes it from client 1
            t.Equal(1, taken, "handoff: one device taken once, however many clients pass it around");
            t.Equal(0, pair.ReleaseClient(1).Count, "handoff: the displaced client frees nothing");
            t.Equal(1, pair.ReleaseClient(2).Count, "handoff: the holder frees it");
            t.Check(!pair.IsOwned("ram"), "handoff: and it is free, so the lighting comes back");

            t.Equal(1, own.ReleaseAll().Count, "handoff: a rescan frees everything");
            t.Equal(0, own.Count, "handoff: leaving nothing owned");
        }

        t.Section("SDK server end to end (#f7)");
        {
            // Our own client against our own server over a real loopback socket. This
            // is the part unit tests cannot reach: framing, version negotiation, and a
            // write actually arriving as the colors that were sent.
            var host = new StubOrgbHost();
            host.Add(new FakeZonedDevice { Name = "Board", Zones2 = new[] { ("Header 1", 4), ("Logo", 1) } });
            host.Add(new FakeZonedDevice { Name = "Sticks", Zones2 = new[] { ("Stick", 8) } });

            using var server = new OpenRgbServer(host, silenceSeconds: 0.4);
            int port = server.Start(listenOnLan: false, port: 27423);
            t.Equal(27423, port, "orgb e2e: the server bound the port it was given");

            using (var client = OpenRgbClient.Connect("127.0.0.1", port))
            {
                t.Equal(1u, client.ServerVersion, "orgb e2e: negotiated protocol 1");
                t.Equal(2, client.GetControllerCount(), "orgb e2e: both devices are listed");

                var dev = client.GetControllerData(0);
                t.Equal("Board", dev.Name, "orgb e2e: the device name arrives");
                t.Equal(5, dev.LedCount, "orgb e2e: with its led count");
                t.Equal(2, dev.Zones.Count, "orgb e2e: and its zones");
                t.Equal("Header 1", dev.Zones[0].Name, "orgb e2e: named correctly");

                // A whole-device write.
                client.SetCustomMode(0);
                var want = new[] { new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255),
                                   new Rgb(10, 20, 30), new Rgb(40, 50, 60) };
                client.UpdateLeds(0, want);
                t.Check(host.WaitForWrite("Board"), "orgb e2e: the write arrives");
                t.Equal(1, host.BeginCount("Board"), "orgb e2e: and claims the device once");
                var got = host.LastWrite("Board");
                t.Equal(0, got.Offset, "orgb e2e: a full write starts at zero");
                t.Equal(5, got.Colors.Count, "orgb e2e: all five leds");
                t.Equal(new Rgb(0, 0, 255), got.Colors[2], "orgb e2e: the colors survive the wire");

                // A zone write lands at that zone's offset, not at zero.
                host.Reset();
                client.UpdateZoneLeds(0, 1, new[] { new Rgb(9, 9, 9) });
                t.Check(host.WaitForWrite("Board"), "orgb e2e: the zone write arrives");
                t.Equal(4, host.LastWrite("Board").Offset, "orgb e2e: at the zone's offset");
                t.Equal(1, host.BeginCount("Board"), "orgb e2e: still one claim, not a second");

                // Writing again keeps the claim alive rather than restoring underneath.
                t.Equal(0, host.EndCount("Board"), "orgb e2e: nothing restored while the client writes");
            }

            // The client disconnected: the device goes back to the user without waiting
            // out the silence timer.
            t.Check(host.WaitForEnd("Board"), "orgb e2e: disconnecting restores the lighting");

            // A rescan drops every claim at once WITHOUT an EndExternal each, because
            // the device instances are gone. The host has to be told, or it waits for
            // releases that never come and never restores the user's lighting again.
            t.Equal(0, host.ResetCount, "orgb e2e: nothing reset yet");
            server.DeviceListChanged();
            t.Equal(1, host.ResetCount, "orgb e2e: a rescan unwinds the takeover");
        }

        t.Section("SDK server: single-LED writes and malformed traffic");
        {
            // Hand-built packets, because the in-house client has no single-LED
            // call and never sends anything malformed - which is exactly what
            // left both paths untested. This port is what other software talks
            // to, so "the server survives and the next client still works" is
            // the property.
            var host = new StubOrgbHost();
            host.Add(new FakeZonedDevice { Name = "Board", Zones2 = new[] { ("Header 1", 4), ("Logo", 1) } });
            using var server = new OpenRgbServer(host, silenceSeconds: 0.4);
            int port = server.Start(listenOnLan: false, port: 0);
            t.Check(port > 0, "orgb raw: the server bound a port");

            static System.Net.Sockets.NetworkStream Open(int port)
            {
                var tcp = new System.Net.Sockets.TcpClient();
                tcp.Connect(System.Net.IPAddress.Loopback, port);
                tcp.NoDelay = true;
                var s = tcp.GetStream();
                s.ReadTimeout = 3000;
                return s;
            }
            static void Send(System.Net.Sockets.NetworkStream s, uint device, uint packet, byte[] payload)
            {
                var buf = new byte[OpenRgbProtocol.HeaderBytes + payload.Length];
                OpenRgbProtocol.WriteHeader(buf, device, packet, payload.Length);
                payload.CopyTo(buf, OpenRgbProtocol.HeaderBytes);
                s.Write(buf, 0, buf.Length);
            }
            static void SendHeaderOnly(System.Net.Sockets.NetworkStream s, uint device, uint packet, int declaredSize)
            {
                var buf = new byte[OpenRgbProtocol.HeaderBytes];
                OpenRgbProtocol.WriteHeader(buf, device, packet, declaredSize);
                s.Write(buf, 0, buf.Length);
            }
            // A ControllerCount round trip proves the connection is alive and served.
            static bool Answers(System.Net.Sockets.NetworkStream s)
            {
                try
                {
                    Send(s, 0, OpenRgbProtocol.PktControllerCount, Array.Empty<byte>());
                    var header = new byte[OpenRgbProtocol.HeaderBytes];
                    int got = 0;
                    while (got < header.Length) { int n = s.Read(header, got, header.Length - got); if (n <= 0) return false; got += n; }
                    var parsed = OpenRgbProtocol.ReadHeader(header);
                    if (parsed is not { PacketId: OpenRgbProtocol.PktControllerCount, Size: 4 }) return false;
                    var body = new byte[4]; got = 0;
                    while (got < 4) { int n = s.Read(body, got, 4 - got); if (n <= 0) return false; got += n; }
                    return BitConverter.ToInt32(body) == 1;
                }
                catch (IOException) { return false; }
            }
            static bool Closed(System.Net.Sockets.NetworkStream s)
            {
                // A dropped client sees end-of-stream (or a reset) on its next read.
                try { return s.Read(new byte[1], 0, 1) == 0; }
                catch (IOException) { return true; }
            }

            // The single-LED packet is 8 bytes - i32 index, u32 colour - with NO
            // length prefix (RGBController_Network::UpdateSingleLED, openrgb-python).
            using (var s = Open(port))
            {
                var color = new Rgb(12, 34, 56);
                var payload = new byte[8];
                BitConverter.GetBytes(2).CopyTo(payload, 0);
                BitConverter.GetBytes(OpenRgbProtocol.ToWire(color)).CopyTo(payload, 4);
                Send(s, 0, OpenRgbProtocol.PktUpdateSingleLed, payload);
                t.Check(host.WaitForWrite("Board"), "orgb raw: an 8-byte single-LED write arrives");
                var w = host.LastWrite("Board");
                t.Equal(2, w.Offset, "orgb raw: at that LED index");
                t.Equal(1, w.Colors.Count, "orgb raw: one colour");
                t.Equal(color, w.Colors[0], "orgb raw: the colour");
                t.Equal(1, host.BeginCount("Board"), "orgb raw: and it claims the device");
                // Out of range is ignored, not a crash.
                host.Reset();
                BitConverter.GetBytes(99).CopyTo(payload, 0);
                Send(s, 0, OpenRgbProtocol.PktUpdateSingleLed, payload);
                t.Check(Answers(s), "orgb raw: an out-of-range LED index is ignored and the connection lives");
                t.Equal(-1, host.LastWrite("Board").Offset, "orgb raw: ...with no write");
            }

            // A declared size of two gigabytes: the client is dropped, nothing allocated.
            using (var s = Open(port))
            {
                SendHeaderOnly(s, 0, OpenRgbProtocol.PktUpdateLeds, int.MaxValue);
                t.Check(Closed(s), "orgb raw: a client asking for a 2 GB packet is dropped");
            }
            // A count that exceeds the payload is ignored.
            using (var s = Open(port))
            {
                host.Reset();
                var p = new byte[4 + 2 + 4];
                BitConverter.GetBytes(p.Length).CopyTo(p, 0);
                BitConverter.GetBytes((ushort)500).CopyTo(p, 4);   // 500 colours claimed, one present
                Send(s, 0, OpenRgbProtocol.PktUpdateLeds, p);
                t.Check(Answers(s), "orgb raw: an over-declared colour count is ignored, connection lives");
                t.Equal(-1, host.LastWrite("Board").Offset, "orgb raw: ...with no write");
            }
            // A device index past the list, and an unknown packet id.
            using (var s = Open(port))
            {
                host.Reset();
                var p = new byte[4 + 2 + 4];
                BitConverter.GetBytes(p.Length).CopyTo(p, 0);
                BitConverter.GetBytes((ushort)1).CopyTo(p, 4);
                Send(s, 99, OpenRgbProtocol.PktUpdateLeds, p);
                Send(s, 0, 9999, new byte[4]);
                t.Check(Answers(s), "orgb raw: device 99 and packet 9999 are ignored, connection lives");
                t.Equal(-1, host.LastWrite("Board").Offset, "orgb raw: ...with no write");
            }
            // Not speaking the protocol at all.
            using (var s = Open(port))
            {
                s.Write(System.Text.Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"));
                t.Check(Closed(s), "orgb raw: a non-protocol client is dropped");
            }
            // The client cap: the seventeenth idle connection is refused, the
            // sixteen before it are still served, and a client after they all
            // leave gets its answer.
            {
                var idle = new List<System.Net.Sockets.NetworkStream>();
                try
                {
                    for (int i = 0; i < 17; i++) idle.Add(Open(port));
                    bool settled = false;
                    for (int i = 0; i < 100 && !settled; i++) { settled = server.ClientCount == 16; Thread.Sleep(10); }
                    t.Equal(16, server.ClientCount, "orgb raw: sixteen clients are served, the seventeenth is refused");
                    t.Check(Answers(idle[0]), "orgb raw: the first of them is still answered");
                }
                finally { foreach (var s in idle) s.Dispose(); }
                bool empty = false;
                for (int i = 0; i < 200 && !empty; i++) { empty = server.ClientCount == 0; Thread.Sleep(10); }
                t.Equal(0, server.ClientCount, "orgb raw: and they are all released on disconnect");
                using var s2 = Open(port);
                t.Check(Answers(s2), "orgb raw: a new client after the storm is served");
            }
        }
    }
}

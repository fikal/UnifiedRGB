using System.IO;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Input;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The driver suite: real protocol bytes driven over the        |
| FakeHid transport, with no hardware attached anywhere.       |
|                                                              |
| Each block builds a real driver over the fake and pins two   |
| things. First, the exact bytes it puts on the wire for a     |
| known input, so a refactor cannot silently change a          |
| protocol that was decoded from a USB capture. Second, what   |
| the driver does when the device refuses a packet.            |
|                                                              |
| That second half is why these sections sit together: the     |
| refusal paths are the ones that have historically produced   |
| the most field bugs. A refused packet cached as sent leaves  |
| the device on its old colour with nothing in the log to say  |
| so, and before this seam existed it could not be tested at   |
| all.                                                         |
\*-----------------------------------------------------------*/
static class DevicesSuite
{
    public static void Run(Harness t)
    {
        t.Section("HID usage -> VK map");
        {
            t.Equal((int)'A', HidUsageVk.ToVk(0x04), "usage A");
            t.Equal((int)'Z', HidUsageVk.ToVk(0x1D), "usage Z");
            t.Equal((int)'1', HidUsageVk.ToVk(0x1E), "usage 1");
            t.Equal((int)'0', HidUsageVk.ToVk(0x27), "usage 0");
            t.Equal(0x0D, HidUsageVk.ToVk(0x28), "usage Enter");
            t.Equal(0x20, HidUsageVk.ToVk(0x2C), "usage Space");
            t.Equal(0x70, HidUsageVk.ToVk(0x3A), "usage F1");
            t.Equal(0x7B, HidUsageVk.ToVk(0x45), "usage F12");
            t.Equal(0x25, HidUsageVk.ToVk(0x50), "usage Left");
            t.Equal(0x60, HidUsageVk.ToVk(0x62), "usage Num0");
            t.Equal(0x69, HidUsageVk.ToVk(0x61), "usage Num9");
            t.Equal(0xA0, HidUsageVk.ToVk(0xE1), "usage LShift");
            t.Equal(0x5C, HidUsageVk.ToVk(0xE7), "usage RWin");
            t.Equal(-1, HidUsageVk.ToVk(0xF0), "unknown usage = -1");
            t.Equal(-1, HidUsageVk.ToVk(0x00), "usage 0 = -1");
        }

        t.Section("LianLiWireless.LoadLayout (#1)");
        {
            // Real config path (LoadLayout reads AppPaths.Config): the user's file, if
            // any, is preserved byte-for-byte around the test.
            string path = AppPaths.Config("lianli-layout.json");
            byte[]? orig = File.Exists(path) ? File.ReadAllBytes(path) : null;
            try
            {
                File.WriteAllText(path, "{\"order\":[3,0,1,2],\"breaks\":[3,0,9,3]}");
                var (order, breaks) = LianLiWireless.LoadLayout(4);
                t.Check(order.SequenceEqual(new[] { 3, 0, 1, 2 }), "LoadLayout honours the saved order");
                t.Check(breaks.SequenceEqual(new[] { 3 }), "LoadLayout keeps a single-slot trailing group (break at 3 of 4) and drops 0/out-of-range/duplicates");
                var (o6, b6) = LianLiWireless.LoadLayout(6);
                t.Check(o6.SequenceEqual(Enumerable.Range(0, 6)) && b6.Length == 0, "LoadLayout falls back to identity on a fan-count mismatch");
                File.WriteAllText(path, "{\"order\":[0,0,1,2]}");
                t.Check(LianLiWireless.LoadLayout(4).Order.SequenceEqual(Enumerable.Range(0, 4)), "LoadLayout rejects a non-permutation order");
                File.WriteAllText(path, "{not json");
                t.Check(LianLiWireless.LoadLayout(4).Order.SequenceEqual(Enumerable.Range(0, 4)), "LoadLayout falls back to identity on malformed json");
            }
            finally
            {
                if (orig != null) File.WriteAllBytes(path, orig);
                else File.Delete(path);
            }
        }

        t.Section("DetectionNotes: why a device is missing");
        {
            DetectionNotes.Clear();
            t.Equal(0, DetectionNotes.Current.Count, "notes start empty");

            DetectionNotes.Report("RazerHid", "Razer Basilisk", BlockReason.HeldByOtherSoftware,
                "opened for feature reports only", "close Synapse");
            t.Equal(1, DetectionNotes.Current.Count, "a blocked device is recorded");

            // A detector that retries must not stack the same complaint.
            DetectionNotes.Report("RazerHid", "Razer Basilisk", BlockReason.HeldByOtherSoftware,
                "opened for feature reports only", "close Synapse");
            t.Equal(1, DetectionNotes.Current.Count, "the same complaint twice is one entry");

            // A DIFFERENT reason about the same device is genuinely new information.
            DetectionNotes.Report("RazerHid", "Razer Basilisk", BlockReason.NeedsAdministrator, "x", null);
            t.Equal(2, DetectionNotes.Current.Count, "a different reason for one device is kept");

            var b = DetectionNotes.Current[0];
            t.Equal("another program has it", b.ReasonText, "the reason reads as a sentence, not an enum");
            t.Check(b.ToString().Contains("Razer Basilisk") && b.ToString().Contains("close Synapse"),
                "the one-line form carries the device and the fix");

            // A rescan describes the CURRENT state, so a fixed problem must disappear
            // rather than linger on screen.
            DetectionNotes.Clear();
            t.Equal(0, DetectionNotes.Current.Count, "a fresh scan clears the previous reasons");

            t.Equal("needs administrator",
                new BlockedDevice("f", "w", BlockReason.NeedsAdministrator, "d", null).ReasonText,
                "elevation reads plainly");
            t.Equal("a driver is missing",
                new BlockedDevice("f", "w", BlockReason.DriverMissing, "d", null).ReasonText,
                "a missing driver reads plainly");
        }

        t.Section("LogitechG403: a refused write is not cached (B4)");
        {
            LogitechG403.RetryAfterFailMs = 0;   // retry at once, or this test sleeps five seconds
            LogitechG403.MinFrameGapMs = 0;      // frames fire back to back here; the pacer has its own test
            var hid = new FakeHid();
            for (int i = 0; i < 40; i++) hid.Replies.Enqueue(FakeHid.HidppReply(14));
            var mouse = new LogitechG403(hid, dev: 0xFF, rgbIdx: 14, feature: 0x8070,
                                         clusterEffect: new byte[] { 1, 1 }, name: "G403");
            var red = new Rgb(255, 0, 0);

            // Write 1 is the software-control claim; write 2 is cluster 0's colour.
            hid.Accept = (n, _) => n != 2;
            mouse.SetColors(new[] { red, red });
            t.Equal(3, hid.Writes.Count, "frame 1: the claim and both cluster writes went out");
            var claim = hid.Writes[0];
            t.Check(claim[0] == 0x11 && claim[1] == 0xFF && claim[2] == 14 && claim[3] == (0x80 | 0x07),
                "HID++ long report to the RGB feature: software-control fn 0x80 with sw id 7");
            var fx = hid.Writes[1];
            t.Check(fx[3] == (0x30 | 0x07) && fx[4] == 0 && fx[5] == 1 && fx[6] == 255 && fx[7] == 0 && fx[8] == 0,
                "SET_EFFECT: cluster, static effect index, then R G B");
            t.Equal(0, (int)fx[16], "streamed frames do not carry the persist byte");

            hid.Accept = null;
            int before = hid.Writes.Count;
            mouse.SetColors(new[] { red, red });
            // The old code had recorded cluster 0 as sent, so this frame was a no-op
            // and the wheel stayed on its previous colour for good.
            t.Equal(before + 2, hid.Writes.Count,
                "an identical frame re-sends the refused cluster (behind a fresh claim) and dedups the one that landed");
            t.Check(hid.Writes[^1][3] == (0x30 | 0x07) && hid.Writes[^1][4] == 0, "...and that re-send is cluster 0");

            before = hid.Writes.Count;
            mouse.SetColors(new[] { red, red });
            t.Equal(before, hid.Writes.Count, "once both landed, the identical frame is fully deduped");
            LogitechG403.RetryAfterFailMs = 5000;
            LogitechG403.MinFrameGapMs = 33;
        }

        t.Section("LogitechG403: a refused persist is retried, a landed one is not");
        {
            var hid = new FakeHid();
            for (int i = 0; i < 40; i++) hid.Replies.Enqueue(FakeHid.HidppReply(14));
            var mouse = new LogitechG403(hid, 0xFF, 14, 0x8070, new byte[] { 1, 1 }, "G403");
            var red = new Rgb(255, 0, 0);
            LogitechG403.MinFrameGapMs = 0;

            // claim(1), cluster 0(2), cluster 1(3), then the commits: cluster 0(4), cluster 1(5)
            hid.Accept = (n, _) => n != 4;
            mouse.SetColors(new[] { red, red }, persist: true);
            t.Equal(5, hid.Writes.Count, "a static apply streams both clusters then commits both");
            t.Check(hid.Writes[3][4] == 0 && hid.Writes[3][16] == 0x01, "write 4 is cluster 0's commit (persist byte set)");

            hid.Accept = null;
            int before = hid.Writes.Count;
            mouse.SetColors(new[] { red, red }, persist: true);
            t.Equal(before + 1, hid.Writes.Count, "colours unchanged: only the refused commit is retried");
            t.Check(hid.Writes[^1][4] == 0 && hid.Writes[^1][16] == 0x01, "...for cluster 0");

            before = hid.Writes.Count;
            mouse.SetColors(new[] { red, red }, persist: true);
            t.Equal(before, hid.Writes.Count, "a cluster already committed is not written to flash again");
            LogitechG403.MinFrameGapMs = 33;
        }

        t.Section("LogitechG403: lighting traffic is paced to ~30 Hz");
        {
            // The mouse's one microcontroller services the sensor at 1000 Hz and every
            // HID++ request we send. Under an animated effect the engine offers a
            // changed frame at 60 fps, which for two clusters is up to 120 exchanges a
            // second, around the clock. Frames closer than the gap wait so a
            // one-shot command is delivered even if the engine has stopped.
            var hid = new FakeHid();
            for (int i = 0; i < 40; i++) hid.Replies.Enqueue(FakeHid.HidppReply(14));
            var mouse = new LogitechG403(hid, 0xFF, 14, 0x8070, new byte[] { 1, 1 }, "G403");
            var red = new Rgb(255, 0, 0); var blue = new Rgb(0, 0, 255);

            var paced = System.Diagnostics.Stopwatch.StartNew();
            mouse.SetColors(new[] { red, red });
            int afterFirst = hid.Writes.Count;
            t.Check(afterFirst > 0, "the first frame goes out");
            mouse.SetColors(new[] { blue, blue });             // immediately: inside the gap
            t.Check(hid.Writes.Count > afterFirst, "a changed frame inside the gap is delivered");
            t.Check(paced.ElapsedMilliseconds >= 32, "consecutive frames preserve the 33 ms pacing gap");
            int afterBlue = hid.Writes.Count;
            mouse.SetColors(new[] { blue, blue });             // the engine offers it again
            t.Equal(afterBlue, hid.Writes.Count, "the delivered frame remains deduplicated");

            int beforePersist = hid.Writes.Count;
            mouse.SetColors(new[] { blue, blue }, persist: true);   // a static apply, right away
            t.Check(hid.Writes.Count > beforePersist, "a persist (static apply) is never paced: it commits at once");
        }

        t.Section("ThermalrightLcd: the frame format, and a refused report (B5)");
        {
            var hid = new FakeHid();
            var lcd = new ThermalrightLcd(hid);
            var frame = new byte[ThermalrightLcd.FrameBytes];
            Array.Fill(frame, (byte)0xEE);
            frame[0] = 0xAB; frame[1] = 0xCD;
            lcd.ShowFrame(frame);

            t.Equal(301, hid.Writes.Count, "a frame is 301 reports: a 20-byte header plus 153600 pixel bytes in 512-byte chunks");
            t.Check(hid.Writes.All(w => w.Length == 513), "every report is 513 bytes: report id 0 plus 512 of data");
            var r0 = hid.Writes[0];
            t.Check(r0[0] == 0 && r0[1] == 0xDA && r0[2] == 0xDB && r0[3] == 0xDC && r0[4] == 0xDD,
                "the header opens with DA DB DC DD");
            t.Check(r0[9] == 240 && r0[10] == 0 && r0[11] == 0x40 && r0[12] == 0x01, "width 240 and height 320, little endian");
            t.Check(r0[17] == 0x00 && r0[18] == 0x58 && r0[19] == 0x02 && r0[20] == 0x00, "pixel byte count 153600, little endian");
            t.Check(r0[21] == 0xAB && r0[22] == 0xCD, "pixels begin right after the 20-byte header");
            var tail = hid.Writes[300];
            t.Check(tail[1] == 0xEE && tail[20] == 0xEE, "the last report carries the final 20 pixel bytes");
            t.Check(tail[21] == 0 && tail[512] == 0, "...and is zero padded, not left holding the previous chunk");

            hid.Writes.Clear();
            hid.Accept = (n, _) => n != 2;
            bool threw = false;
            try { lcd.ShowFrame(frame); } catch (IOException) { threw = true; }
            t.Check(threw, "a refused report aborts the frame with an IOException the stream loop can act on");
            t.Equal(2, hid.Writes.Count, "and nothing after the refused report is sent");

            hid.Writes.Clear(); hid.Accept = null; threw = false;
            try { lcd.ShowFrame(new byte[100]); } catch (ArgumentException) { threw = true; }
            t.Check(threw && hid.Writes.Count == 0, "a frame that is not exactly 240x320 RGB565 is refused before a byte goes out");
        }

        t.Section("SayoDevice: the packet, byte for byte");
        {
            var hid = new FakeHid();
            var pad = new SayoDevice(hid, outLen: 64, name: "Sayo");
            pad.SetColors(new[] { new Rgb(10, 20, 30) });
            t.Equal(1, hid.Writes.Count, "one packet per colour change");
            var pk = hid.Writes[0];
            t.Equal(64, pk.Length, "sized to the interface's output report");
            t.Check(pk[0] == 0x21 && pk[1] == 0x12, "report id 0x21, magic 0x12");
            t.Check(pk[2] == 0x8C && pk[3] == 0xA8,
                "checksum: 16-bit little-endian word sum from 0x1221 over the payload (0xA88C for 10,20,30)");
            t.Check(pk[4] == 0x1C && pk[5] == 0x00 && pk[6] == 0x11 && pk[7] == 0x00, "payload header 1C 00 11 00");
            t.Equal((byte)0xC0, pk[24], "mode byte: speed 3, static colour, static mode");
            t.Check(pk[28] == 10 && pk[29] == 20 && pk[30] == 30, "the colour rides at payload offset 24 in R G B order");

            pad.SetColors(new[] { new Rgb(10, 20, 30) });
            t.Equal(1, hid.Writes.Count, "an identical colour is deduped");
            hid.Accept = (_, _) => false;
            pad.SetColors(new[] { new Rgb(1, 2, 3) });
            t.Equal(2, hid.Writes.Count, "a new colour is sent");
            hid.Accept = null;
            pad.SetColors(new[] { new Rgb(1, 2, 3) });
            t.Equal(3, hid.Writes.Count, "a colour whose write was refused is sent again, not cached");
        }

        t.Section("CorsairStrafeMk2: init sequence and frame framing");
        {
            var hid = new FakeHid();
            var kb = new CorsairStrafeMk2(hid);   // the constructor runs the init sequence
            t.Equal(8, hid.Writes.Count, "init: firmware query, special-function, lighting-control, key-map header, 4 identifier packets");
            t.Check(hid.Writes.All(w => w.Length == 65), "65-byte reports");
            t.Check(hid.Writes[1][1] == 0x07 && hid.Writes[1][2] == 0x04 && hid.Writes[1][3] == 0x02, "special function control 07 04 02");
            t.Check(hid.Writes[2][1] == 0x07 && hid.Writes[2][2] == 0x05 && hid.Writes[2][3] == 0x02 && hid.Writes[2][5] == 0x03,
                "lighting control 07 05 02 .. 03 = software mode");
            t.Check(hid.Writes[3][1] == 0x07 && hid.Writes[3][2] == 0x05 && hid.Writes[3][3] == 0x08 && hid.Writes[3][5] == 0x01,
                "key-map header 07 05 08 .. 01");
            t.Check(hid.Writes[4][1] == 0x07 && hid.Writes[4][2] == 0x40 && hid.Writes[4][3] == 0x1E && hid.Writes[4][6] == 0xC0,
                "identifier packets 07 40 1E carrying 0xC0 per key");

            hid.Writes.Clear();
            var allRed = Enumerable.Repeat(new Rgb(255, 0, 0), kb.LedCount).ToArray();
            kb.SetColors(allRed);
            t.Equal(12, hid.Writes.Count, "a frame is 3 channels x (3 stream packets + 1 commit)");
            for (int ch = 0; ch < 3; ch++)
            {
                var s0 = hid.Writes[ch * 4]; var s1 = hid.Writes[ch * 4 + 1]; var s2 = hid.Writes[ch * 4 + 2]; var c = hid.Writes[ch * 4 + 3];
                t.Check(s0[1] == 0x7F && s0[2] == 1 && s0[3] == 60 && s1[2] == 2 && s1[3] == 60 && s2[2] == 3 && s2[3] == 24,
                    $"channel {ch + 1}: streams of 60, 60 and 24 bytes");
                t.Check(c[1] == 0x07 && c[2] == 0x28 && c[3] == ch + 1 && c[4] == 3, $"channel {ch + 1}: commit 07 28 {ch + 1} 03");
            }
            t.Check(hid.Writes[3][5] == 1 && hid.Writes[7][5] == 1 && hid.Writes[11][5] == 2,
                "red and green commits carry finish=1; blue carries finish=2, which latches the frame");
            bool redLit = hid.Writes.Take(3).Any(w => w.Skip(5).Any(b => b == 0xFF));
            bool othersDark = hid.Writes.Skip(4).Take(3).Concat(hid.Writes.Skip(8).Take(3)).All(w => w.Skip(5).All(b => b == 0));
            t.Check(redLit && othersDark, "an all-red frame lights the red channel only");

            kb.SetColors(allRed);
            t.Equal(12, hid.Writes.Count, "an identical frame is deduped");

            var allBlue = Enumerable.Repeat(new Rgb(0, 0, 255), kb.LedCount).ToArray();
            hid.Accept = (_, _) => false;
            kb.SetColors(allBlue);
            int after = hid.Writes.Count;
            t.Equal(24, after, "every packet of a frame is still attempted when one is refused - a half-latched frame is worse");
            hid.Accept = null;
            kb.SetColors(allBlue);
            t.Check(hid.Writes.Count > after, "a frame whose packets were refused is sent again, not cached");
        }

        t.Section("SteelSeriesApex: init, direct-lighting packet, hand-back");
        {
            var hid = new FakeHid();
            var kb = new SteelSeriesApex(hid, featureLen: 643, outputLen: 65, name: "SteelSeries Apex");
            t.Equal(1, hid.Features.Count, "init: one feature report");
            t.Check(hid.Features[0].Length == 643 && hid.Features[0][1] == 0x4B, "direct-lighting mode 0x4B");

            var frame = new Rgb[kb.LedCount];
            frame[0] = new Rgb(1, 2, 3); frame[1] = new Rgb(4, 5, 6);
            kb.SetColors(frame);
            t.Equal(2, hid.Features.Count, "one feature report per changed frame");
            var f = hid.Features[1];
            t.Check(f[1] == 0x40 && f[2] == kb.LedCount, "direct packet 0x40 with the key count");
            t.Check(f[3] == 0x04 && f[4] == 1 && f[5] == 2 && f[6] == 3, "key 0 is HID usage 0x04 (A) followed by its colour, R G B");
            t.Check(f[7] == 0x05 && f[8] == 4 && f[9] == 5 && f[10] == 6, "key 1 follows four bytes later");

            kb.SetColors(frame);
            t.Equal(2, hid.Features.Count, "an identical frame is deduped");
            hid.AcceptFeature = (_, _) => false;
            frame[0] = new Rgb(9, 9, 9);
            kb.SetColors(frame);
            t.Equal(3, hid.Features.Count, "a changed frame is sent");
            hid.AcceptFeature = null;
            kb.SetColors(frame);
            t.Equal(4, hid.Features.Count, "a frame whose report was refused is sent again, not cached");

            kb.Dispose();
            t.Check(hid.Writes.Count == 1 && hid.Writes[0][1] == 0x41, "Dispose hands the keyboard back to its onboard profile (0x41)");
            t.Check(hid.IsDisposed, "...and closes the handle");
        }

        t.Section("RazerHid: the report layout, byte for byte");
        // openrazer's report layout: 90 wire bytes behind report id 0. This is
        // the primitive the two replay sections below build their frames on.
        {
            var r = RazerHid.NewReport(0x1F, 0x00, 0x81, 0x02);
            t.Check(r.Length == 91 && r[0] == 0x00, "razer report: 91 bytes, report id 0");
            t.Check(r[2] == 0x1F && r[6] == 0x02 && r[7] == 0x00 && r[8] == 0x81, "razer report: tid/size/class/cmd at wire 1/5/6/7");
            RazerHid.Seal(r);
            byte crc = 0; for (int i = 3; i <= 88; i++) crc ^= r[i];
            t.Check(r[89] == crc && r[90] == 0, "razer report: crc = XOR of wire bytes 2..87 at wire 88, reserved 0");

            var colors = Enumerable.Range(0, 13).Select(i => new Rgb((byte)i, (byte)(i * 2), (byte)(i * 3))).ToArray();
            var f = RazerHid.CustomFrameReport(0x1F, 0, 0, 12, colors, 0);
            t.Check(f[6] == 5 + 39 && f[7] == 0x0F && f[8] == 0x03, "razer custom frame: class 0F cmd 03, size 5 + 3n");
            t.Check(f[9] == 0 && f[10] == 0 && f[11] == 0 && f[12] == 0 && f[13] == 12, "razer custom frame: row 0, cols 0..12");
            t.Check(f[14] == 0 && f[14 + 3 * 12] == 12 && f[15 + 3 * 12] == 24 && f[16 + 3 * 12] == 36, "razer custom frame: RGB triplets from args[5]");
            var chunk = RazerHid.CustomFrameReport(0x1F, 2, 25, 30, Enumerable.Repeat(Rgb.Red, 31).ToArray(), 25);
            t.Check(chunk[11] == 2 && chunk[12] == 25 && chunk[13] == 30 && chunk[6] == 5 + 18, "razer custom frame: a later chunk carries its own row/start/stop");

            var d = RazerHid.DpiReport(0x1F, 1600, 800);
            t.Check(d[7] == 0x04 && d[8] == 0x05 && d[6] == 7 && d[9] == 0x01, "razer dpi: class 04 cmd 05, VARSTORE");
            t.Check(d[10] == 0x06 && d[11] == 0x40 && d[12] == 0x03 && d[13] == 0x20, "razer dpi: X/Y big-endian");
            t.Check(RazerHid.DpiReport(0x1F, 5, 99999)[11] == 100 && RazerHid.DpiReport(0x1F, 5, 99999)[12] == 0xAF, "razer dpi: clamped to 100..45000");

            var stages = new (int, int)[] { (400, 400), (800, 800), (1600, 1600), (3200, 3200), (6400, 6400) };
            var s = RazerHid.DpiStagesReport(0x1F, 3, stages);
            t.Check(s[7] == 0x04 && s[8] == 0x06 && s[6] == 0x26 && s[10] == 3 && s[11] == 5, "razer dpi stages: class 04 cmd 06, active 3 of 5");
            t.Check(s[12] == 1 && s[12 + 7] == 2 && s[12 + 28] == 5, "razer dpi stages: 7-byte entries numbered 1..5");
            var back = RazerHid.DecodeDpiStages(s.AsSpan(9, 80));
            t.Check(back.Active == 3 && back.Stages.Length == 5 && back.Stages[2] == (1600, 1600) && back.Stages[4] == (6400, 6400), "razer dpi stages: encode/decode round-trip");
            t.Check(RazerHid.DpiStagesReport(0x1F, 9, stages.Take(2).ToArray())[10] == 2, "razer dpi stages: active clamped to the count");

            t.Check(RazerHid.PollingHz(0x01) == 1000 && RazerHid.PollingHz(0x02) == 500 && RazerHid.PollingHz(0x08) == 125 && RazerHid.PollingHz(0x40) == 0, "razer polling: code -> Hz");
            t.Check(RazerHid.PollingCode(1000) == 0x01 && RazerHid.PollingCode(500) == 0x02 && RazerHid.PollingCode(125) == 0x08 && RazerHid.PollingCode(2000) == 0, "razer polling: Hz -> code");

            var pad = RazerHid.PadPositionsFor(20);
            t.Check(pad.Length == 20 && pad.All(p => p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1), "razer pad: n perimeter positions inside the unit box");
            t.Check(pad[0] == new LedPos(0, 0) && pad[5].Y == 0 && pad[10].X > 0.99f && pad.Distinct().Count() == 20, "razer pad: clockwise from the top-left corner, all distinct");
            var (guess, src) = RazerHid.ResolveCount(0x0FFF, null);
            t.Check(guess == 20 && src == "guessed", "razer pad: no config, no probe -> 20 guessed");
            t.Check(RazerHid.ResolveCount(0x0FFF, 19) == (19, "probed") && RazerHid.ResolveCount(0x0FFF, 999).Count == RazerHid.MaxLeds, "razer pad: probe wins over the guess and is capped");
        }

        t.Section("RazerHid: Chris's HyperFlux V2, replayed from his bundle");
        {
            // Built from a real support bundle (2026-09-06): HyperFlux V2 pad 1532:00CF,
            // the control collection usage 0x0001/0x0002 with a 91-byte feature
            // report, Synapse's elevation service holding every Razer device so the
            // open came back ACCESS_DENIED. The fake below answers the way a pad with
            // no mouse awake on it does. Nothing here needs the hardware, which is
            // the point: the driver's whole bring-up runs against a bundle.
            const byte ST_OK = 0x02, ST_FAIL = 0x03, ST_UNSUPPORTED = 0x05;
            const int ARGS = 9;
            const int PadLeds = 20;

            // The pad's firmware, in one function: echo the command, say OK, and
            // fill in what each command asks for.
            byte[]? Pad(byte[] req)
            {
                byte tid = req[2], cls = req[7], cmd = req[8];
                if (tid != 0x1F) return null;                        // only one transaction id is alive
                var r = new byte[91];
                r[1] = ST_OK; r[7] = cls; r[8] = cmd;
                if (cls == 0x00 && cmd == 0x81) { r[ARGS] = 1; r[ARGS + 1] = 5; }           // firmware v1.5
                else if (cls == 0x00 && cmd == 0x82)                                         // serial
                    for (int i = 0; i < 22; i++) r[ARGS + i] = (byte)('A' + i % 26);
                else if (cls == 0x04 && cmd == 0x85) r[1] = ST_UNSUPPORTED;                 // no DPI: a pad, not a mouse
                else if (cls == 0x0F && cmd == 0x03 && req[ARGS + 4] >= PadLeds) r[1] = ST_FAIL;   // a column past the strip
                return r;
            }

            var opened = new List<FakeHid>();
            FakeHid Open() { var h = new FakeHid { Respond = Pad, FeatureOnly = true }; opened.Add(h); return h; }

            var found = RazerHid.OpenPad(Open);
            t.Equal(1, found.Count, "the pad is found with no mouse paired: one device");
            var pad = found[0];
            t.Equal("Razer HyperFlux V2 pad", pad.Name, "identified as the pad, not as a mouse");
            t.Equal(PadLeds, pad.LedCount, "the strip length comes from the frame probe (columns refused from 20 on)");
            t.Check(opened.Count >= 2, "the probe handle and the device's own handle are separate opens");
            t.Check(opened[0].IsDisposed, "the probe handle is closed once enumeration is done");

            // FeatureOnly = the handle Synapse leaves us. The whole protocol is
            // feature reports, so it must not matter.
            var colors = Enumerable.Range(0, PadLeds).Select(i => new Rgb((byte)i, 0, (byte)(255 - i))).ToArray();
            var hid = opened[^1];
            int before = hid.Features.Count;
            pad.SetColors(colors);
            var frames = hid.Features.Skip(before).Where(f => f[7] == 0x0F && f[8] == 0x03).ToList();
            t.Equal(1, frames.Count, "20 LEDs fit one custom-frame report (25 per packet)");
            var f = frames[0];
            t.Check(f[0] == 0 && f[2] == 0x1F && f[6] == 5 + 3 * PadLeds, "report id 0, transaction 0x1F, data size 5 + 3n");
            t.Check(f[ARGS + 2] == 0 && f[ARGS + 3] == 0 && f[ARGS + 4] == PadLeds - 1, "row 0, columns 0..19");
            t.Check(f[ARGS + 5] == 0 && f[ARGS + 7] == 255 && f[ARGS + 5 + 3 * 19] == 19 && f[ARGS + 7 + 3 * 19] == 236,
                "colours in R G B order, first and last LED where they should be");
            byte crc = 0; for (int i = 2; i < 88; i++) crc ^= f[1 + i];
            t.Equal(crc, f[89], "the report is sealed with the XOR of wire bytes 2..87");
            t.Check(hid.Writes.Count == 0, "nothing goes through output reports, so a feature-only handle is enough");

            before = hid.Features.Count;
            pad.SetColors(colors);
            t.Equal(before, hid.Features.Count, "an identical frame is deduped");
            pad.Dispose();
        }

        t.Section("RazerHid: nobody home on the pad");
        {
            // The other thing Chris's log could have said: the pad enumerates but no
            // transaction id answers (mouse asleep, nothing paired). That must be
            // "no device", quietly - not an exception, not a phantom entry.
            var found = RazerHid.OpenPad(() => new FakeHid { Respond = _ => null });
            t.Equal(0, found.Count, "a pad that answers nothing yields no device");
        }

        t.Section("RazerHid: a known mouse over a feature-only handle");
        {
            const byte ST_OK = 0x02;
            const int ARGS = 9;
            byte[] Mouse(byte[] req)
            {
                var r = new byte[91];
                r[1] = ST_OK; r[7] = req[7]; r[8] = req[8];
                if (req[7] == 0x00 && req[8] == 0x81) { r[ARGS] = 2; r[ARGS + 1] = 1; }   // fw v2.1
                return r;
            }
            var hid = new FakeHid { Respond = Mouse, FeatureOnly = true };
            var mouse = RazerHid.OpenKnown(() => hid, 0x00AA, RazerHid.BasiliskV3Pro);
            t.Check(mouse != null, "a known mouse opens over a handle another program holds");
            t.Equal("v2.1", mouse!.Firmware, "the firmware version is read at open");
            t.Equal(13, mouse.LedCount, "Basilisk V3 Pro: 13 LEDs");

            int before = hid.Features.Count;
            mouse.SetColors(Enumerable.Repeat(new Rgb(255, 0, 0), 13).ToArray());
            var frame = hid.Features.Skip(before).First(f => f[7] == 0x0F && f[8] == 0x03);
            t.Check(frame[6] == 5 + 3 * 13 && frame[ARGS + 4] == 12 && frame[ARGS + 5] == 255 && frame[ARGS + 6] == 0,
                "one 13-LED custom frame, all red");
            mouse.Dispose();
            t.Check(hid.IsDisposed, "Dispose closes the handle");
        }

        t.Section("DeviceManager: the registration rule");
        {
            // The family name - what the per-device disable list, the detect log and
            // the blocked-device notes all key on - is derived REFLECTIVELY from the
            // factory delegate's declaring type. Register a driver through a lambda
            // or a helper class and it works, but can never be disabled, and is
            // logged under a compiler-generated name. This is the rule in code form.
            var all = ((IEnumerable<Delegate>)DeviceManager.Factories).Concat(DeviceManager.MultiFactories).ToList();
            var names = all.Select(d => d.Method.DeclaringType!.Name).ToList();
            t.Check(all.Count >= 11, $"the factory tables are populated ({all.Count})");
            t.Check(names.Count == names.Distinct().Count(), "every detector family has a distinct name");
            t.Check(all.All(d => d.Method.IsStatic), "every factory is a static method");
            t.Check(all.All(d => !d.Method.DeclaringType!.Name.Contains('<')), "no factory is a lambda (compiler-generated declaring type)");
            t.Check(all.All(d => d.Method.DeclaringType!.Namespace!.StartsWith("UnifiedRgb.Core")),
                "every factory lives on a class in the Core assembly");
        }

        t.Section("GigabyteIt5711.NormalizeOrder (#31)");
        {
            t.Equal("RGB", GigabyteIt5711.NormalizeOrder(" rgb ", 1), "NormalizeOrder trims and upper-cases");
            t.Equal("BGR", GigabyteIt5711.NormalizeOrder("Bgr", 2), "NormalizeOrder mixed case");
            t.Equal("GBR", GigabyteIt5711.NormalizeOrder("GBR", 3), "NormalizeOrder known order passes through");
            t.Equal("GRB", GigabyteIt5711.NormalizeOrder(null, 4), "NormalizeOrder null -> GRB");
            t.Equal("GRB", GigabyteIt5711.NormalizeOrder("", 1), "NormalizeOrder empty -> GRB");
            t.Equal("GRB", GigabyteIt5711.NormalizeOrder("xyz", 1), "NormalizeOrder unknown -> GRB (warned)");
            t.Equal("GRB", GigabyteIt5711.NormalizeOrder("RGBW", 1), "NormalizeOrder four-channel string -> GRB");
        }
    }
}

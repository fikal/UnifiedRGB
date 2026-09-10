using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| GigabyteIt5711 over a FakeHid: the refused-frame and         |
| partial-zone paths. Every packet the board would see is a   |
| 64-byte feature report with byte 1 naming the command:      |
|   0x34 LED counts, 0x32 effect-disable mask (direct-mode    |
|   setup); 0x20+n / 0x90+(n-8) static-effect packet for      |
|   effect index n; 0x28 apply; 0x58/0x59/0x62/0x63 per-LED   |
|   stream for ARGB header 1/2/3/4.                           |
| The board is built on the default hardware.json (headers 2  |
| and 4 as 8-LED GRB fan rings; the harness's config dir is   |
| a temp root, so HardwareConfig.Load writes those defaults   |
| there on first use).                                        |
|                                                             |
| ENE (EneDram) has no fixture here: PawnSmbus's constructor  |
| is private protected and takes a real PawnIO handle, and    |
| its I/O methods are not virtual, so there is nothing for    |
| the harness to stand in for.                                |
\*-----------------------------------------------------------*/
static class GigabyteDriverSuite
{
    const byte CMD_LED_COUNT = 0x34, CMD_EFFECT_MASK = 0x32, CMD_APPLY = 0x28;

    static bool IsStream(byte[] p) => p[1] is 0x58 or 0x59 or 0x62 or 0x63;
    static bool IsEffect(byte[] p) => p[1] is (>= 0x20 and <= 0x27) or (>= 0x90 and <= 0x9A);
    static bool IsSetup(byte[] p) => p[1] is CMD_LED_COUNT or CMD_EFFECT_MASK;

    /// <summary>The stream byte for the header behind a fan zone, found by
    /// sending a frame and looking at what came out, so the test does not
    /// hard-code which header hardware.json put first.</summary>
    static byte StreamIdOf(FakeHid hid, GigabyteIt5711 board, RgbZone fan)
    {
        hid.Features.Clear();
        var probe = new Rgb[board.LedCount];
        probe[fan.Offset] = new Rgb(1, 2, 3);
        board.SetColors(probe);
        var pkt = hid.Features.First(p => IsStream(p) && p[2] == 0 && p[3] == 0 && p[5 + 1] == 1);   // GRB: G,R,B -> R sits at +1
        return pkt[1];
    }

    static (FakeHid Hid, GigabyteIt5711 Board, RgbZone Fan) Fresh()
    {
        var hid = new FakeHid();
        var board = new GigabyteIt5711(hid, 0x5711);
        var fan = board.Zones.First(z => z.IsFan);   // Run() has already verified one exists
        hid.Features.Clear();   // drop the constructor's ResetController reports
        return (hid, board, fan);
    }

    static Rgb[] Solid(int n, Rgb c) { var a = new Rgb[n]; Array.Fill(a, c); return a; }

    public static void Run(Harness t)
    {
        t.Section("fixture: the default board has a fan zone");
        using (var probe = new GigabyteIt5711(new FakeHid(), 0x5711))
        {
            bool hasFan = probe.Zones.Any(z => z.IsFan);
            t.Check(hasFan, "gigabyte: the default hardware.json yields at least one fan (ARGB) zone");
            if (!hasFan) return;   // every block below needs one; a NRE here would take the harness down
        }

        /*---------------- (a) every report refused, then the same frame ----------------*/
        {
            t.Section("(a) every report refused, then the same frame");
            var (hid, board, fan) = Fresh();
            var red = Solid(board.LedCount, Rgb.Red);

            hid.AcceptFeature = (_, _) => false;
            board.SetColors(red);
            t.Check(hid.Features.Count > 0, "gigabyte(a): a refused frame still tried to write");
            t.Check(!hid.Features.Any(IsStream), "gigabyte(a): nothing is streamed while direct-mode setup is refused");

            hid.AcceptFeature = null;
            hid.Features.Clear();
            board.SetColors(red);
            t.Check(hid.Features.Count > 0, "gigabyte(a): the identical frame is re-sent after a refusal (not deduped)");
            t.Check(hid.Features.Any(p => p[1] == CMD_LED_COUNT) && hid.Features.Any(p => p[1] == CMD_EFFECT_MASK),
                "gigabyte(a): direct-mode setup is retried");
            int statics = board.Zones.Count(z => !z.IsFan);
            t.Check(hid.Features.Count(IsEffect) == statics, $"gigabyte(a): every static zone's effect packet goes out ({statics})");
            t.Check(hid.Features.Any(p => p[1] == CMD_APPLY), "gigabyte(a): the apply packet goes out");
            t.Check(hid.Features.Any(IsStream), "gigabyte(a): the fan header is streamed");

            // Setup accepted, color + apply refused: the case the old code
            // cached. Effect packets and apply must both re-send.
            hid.AcceptFeature = (_, p) => IsSetup(p);
            hid.Features.Clear();
            board.SetColors(Solid(board.LedCount, Rgb.Green));
            t.Check(hid.Features.Count(IsEffect) == statics, "gigabyte(a): with setup done, every effect packet was attempted and refused");
            t.Check(!hid.Features.Any(IsSetup), "gigabyte(a): an accepted direct-mode setup is not repeated");

            hid.AcceptFeature = null;
            hid.Features.Clear();
            board.SetColors(Solid(board.LedCount, Rgb.Green));
            t.Check(hid.Features.Count(IsEffect) == statics, "gigabyte(a): refused effect packets re-send on the identical frame");
            t.Check(hid.Features.Count(p => p[1] == CMD_APPLY) == 1, "gigabyte(a): refused apply re-sends on the identical frame");
            t.Check(hid.Features.Any(IsStream), "gigabyte(a): refused stream re-sends on the identical frame");

            // Only the apply refused: effect packets landed (deduped from now
            // on) but the board shows nothing until an apply gets through.
            hid.AcceptFeature = (_, p) => p[1] != CMD_APPLY;
            hid.Features.Clear();
            board.SetColors(Solid(board.LedCount, Rgb.Blue));
            t.Check(hid.Features.Count(IsEffect) == statics, "gigabyte(a): effect packets accepted with apply refused");

            hid.AcceptFeature = null;
            hid.Features.Clear();
            board.SetColors(Solid(board.LedCount, Rgb.Blue));
            t.Check(!hid.Features.Any(IsEffect), "gigabyte(a): accepted effect packets are deduped");
            t.Check(hid.Features.Count(p => p[1] == CMD_APPLY) == 1, "gigabyte(a): a lost apply is re-sent even though no static changed");

            hid.Features.Clear();
            board.SetColors(Solid(board.LedCount, Rgb.Blue));
            t.Check(hid.Features.Count == 0, "gigabyte(a): once everything landed, the identical frame sends nothing");
            board.Dispose();
        }

        /*---------------- (b) one LED inside a fan ring ----------------*/
        {
            t.Section("(b) one LED inside a fan ring");
            var (hid, board, fan) = Fresh();
            byte streamId = StreamIdOf(hid, board, fan);
            board.SetColors(Solid(board.LedCount, Rgb.Red));
            hid.Features.Clear();

            board.SetZone(fan.Offset + 1, new[] { Rgb.Blue });
            var streams = hid.Features.Where(p => p[1] == streamId).ToList();
            t.Check(streams.Count == 1, "gigabyte(b): a one-LED SetZone inside the ring re-streams that header once");
            if (streams.Count == 1)
            {
                var p = streams[0];
                t.Check(p[2] == 0 && p[3] == 0 && p[4] == fan.Count * 3, "gigabyte(b): the whole ring is streamed from LED 0");
                bool ok = true;
                for (int i = 0; i < fan.Count; i++)
                {
                    int o = 5 + i * 3;
                    // GRB wire order: (G, R, B).
                    var expect = i == 1 ? (0, 0, 255) : (0, 255, 0);
                    if (((int)p[o], (int)p[o + 1], (int)p[o + 2]) != expect) ok = false;
                }
                t.Check(ok, "gigabyte(b): LED 1 is blue in GRB order and the other LEDs keep their red");
            }
            t.Check(!hid.Features.Any(p => IsStream(p) && p[1] != streamId), "gigabyte(b): other fan headers are not re-streamed");

            // The shadow persists: repeating the same one-LED write is deduped.
            hid.Features.Clear();
            board.SetZone(fan.Offset + 1, new[] { Rgb.Blue });
            t.Check(hid.Features.Count == 0, "gigabyte(b): an unchanged partial write sends nothing");
            board.Dispose();
        }

        /*---------------- (c) a fan-zone SetZone leaves the statics alone ----------------*/
        {
            t.Section("(c) a fan-zone SetZone leaves the statics alone");
            var (hid, board, fan) = Fresh();
            board.SetColors(Solid(board.LedCount, Rgb.Red));
            hid.Features.Clear();

            board.SetZone(fan.Offset, Solid(fan.Count, Rgb.Green));
            t.Check(hid.Features.Any(IsStream), "gigabyte(c): the fan zone is streamed");
            t.Check(!hid.Features.Any(IsEffect), "gigabyte(c): no static-effect packet is re-sent");
            t.Check(!hid.Features.Any(p => p[1] == CMD_APPLY), "gigabyte(c): no apply packet without a static change");
            board.Dispose();
        }

        /*---------------- (d) direct-mode setup refused once ----------------*/
        {
            t.Section("(d) direct-mode setup refused once");
            var (hid, board, _) = Fresh();
            var red = Solid(board.LedCount, Rgb.Red);

            hid.AcceptFeature = (_, p) => p[1] != CMD_LED_COUNT;
            board.SetColors(red);
            t.Check(hid.Features.Any(p => p[1] == CMD_LED_COUNT), "gigabyte(d): the LED-count packet was attempted");

            hid.AcceptFeature = null;
            hid.Features.Clear();
            board.SetColors(red);
            t.Check(hid.Features.Any(p => p[1] == CMD_LED_COUNT) && hid.Features.Any(p => p[1] == CMD_EFFECT_MASK),
                "gigabyte(d): a refused direct-mode init is retried on the next write");
            t.Check(hid.Features.Any(IsStream), "gigabyte(d): and the frame is streamed once init lands");

            hid.Features.Clear();
            board.SetColors(red);
            t.Check(!hid.Features.Any(IsSetup), "gigabyte(d): a successful init is not repeated");

            // The second half of init refused (mask after a good count) is
            // retried too - both packets, since _directInit latches on the pair.
            hid.AcceptFeature = (_, p) => p[1] != CMD_EFFECT_MASK;
            board.SetHardwareStatic(Rgb.Black);   // forces re-init on the next frame
            hid.Features.Clear();
            board.SetColors(red);
            t.Check(hid.Features.Any(p => p[1] == CMD_EFFECT_MASK), "gigabyte(d): the effect-mask packet was attempted");

            hid.AcceptFeature = null;
            hid.Features.Clear();
            board.SetColors(red);
            t.Check(hid.Features.Any(p => p[1] == CMD_LED_COUNT) && hid.Features.Any(p => p[1] == CMD_EFFECT_MASK),
                "gigabyte(d): a refused effect-mask packet re-runs the whole init");
            board.Dispose();
        }

        t.Section("partial stream failure followed by the last successful color");
        var original = HardwareConfig.Load();
        try
        {
            new HardwareConfig
            {
                GigabyteArgbHeaders = new() { new() { Header = 2, Leds = 30, Name = "Test strip" } },
            }.Save();
            var hid = new FakeHid();
            using var board = new GigabyteIt5711(hid, 0x5711);
            var fan = board.Zones.First(z => z.IsFan);
            board.SetZone(fan.Offset, Solid(fan.Count, Rgb.Red));

            // A 30-LED header takes two reports. The first blue chunk lands,
            // then the second is refused: the hardware no longer matches red.
            hid.Features.Clear();
            hid.AcceptFeature = (_, p) => !IsStream(p) || p[2] == 0;
            board.SetZone(fan.Offset, Solid(fan.Count, Rgb.Blue));
            t.Equal(2, hid.Features.Count(IsStream), "partial stream: both blue chunks attempted");

            hid.AcceptFeature = null;
            hid.Features.Clear();
            board.SetZone(fan.Offset, Solid(fan.Count, Rgb.Red));
            var repair = hid.Features.Where(IsStream).ToList();
            t.Equal(2, repair.Count, "partial stream: returning to red repairs both chunks");
            t.Check(repair.Count == 2 && repair.All(p =>
                Enumerable.Range(0, p[4] / 3).All(i =>
                    p[5 + i * 3] == 0 && p[6 + i * 3] == 255 && p[7 + i * 3] == 0)),
                "partial stream: repair sends red in GRB order");

            hid.Features.Clear();
            board.SetZone(fan.Offset, Solid(fan.Count, Rgb.Red));
            t.Equal(0, hid.Features.Count, "partial stream: a successful repair restores dedup");
        }
        finally { original.Save(); }
    }
}

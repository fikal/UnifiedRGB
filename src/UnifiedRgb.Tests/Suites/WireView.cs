using System.Buffers.Binary;
using UnifiedRgb.Core.Sensors;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The WireView Pro II sensor frame: 100 bytes, no framing and  |
| no checksum, so the decoder is the only thing standing       |
| between a desynced serial read and a 60 kW reading on the    |
| Cooling pane. Three things get pinned here:                  |
|                                                              |
|  - a REAL frame captured from the device (idle RTX 5090,     |
|    probes unplugged), so the offsets are proven against      |
|    hardware and not just against the spec we wrote them from;|
|  - a synthetic frame whose every pin differs, so a field     |
|    read from the neighbouring pin's slot cannot pass;        |
|  - the sanity gate: padding, fan, Vdd and pin bounds, each   |
|    of which a corrupt frame has tripped in the field.        |
\*-----------------------------------------------------------*/
static class WireViewSuite
{
    // Captured 2026-10-14 from a WireView Pro II, firmware v5 (build 20260902).
    internal const string CapturedHex =
        "09011A0118FC18FC4B0D0000C52E0000A8010000D4130000C22E000053010000D90F0000BF2E00006B010000F8100000" +
        "C22E00004C010000860F0000DF2E000055010000FB0F0000BE2E00007B010000B7110000DD65000082080000C62E000000000000";

    public static void Run(Harness t)
    {
        t.Section("Captured frame");
        {
            var r = WireView.Parse(Convert.FromHexString(CapturedHex));
            t.Check(r != null, "captured: a real frame passes the sanity gate");
            if (r != null)
            {
                t.Equal(26.5, r.OnboardInC, "captured: adapter-in temperature (0.1 °C units)");
                t.Equal(28.2, r.OnboardOutC, "captured: adapter-out temperature");
                t.Check(r.External1C == null && r.External2C == null, "captured: unplugged probes (-100.0 °C) read as absent");
                t.Equal(11.973, r.Pins[0].Volts, "captured: pin 1 voltage");
                t.Equal(0.424, r.Pins[0].Amps, "captured: pin 1 current");
                t.Equal(5.076, r.Pins[0].Watts, "captured: pin 1 power");
                t.Equal(11.999, r.Pins[4].Volts, "captured: pin 5 voltage");
                t.Equal(0.379, r.Pins[5].Amps, "captured: pin 6 current");
                t.Equal(26.077, r.Watts, "captured: total power is the device's own");
                t.Equal(2.178, r.Amps, "captured: total current");
                t.Equal(11.974, r.Volts, "captured: average voltage");
                t.Equal((int?)600, r.PsuCapWatts, "captured: cap enum 0 = a 600 W cable");
                t.Equal(WireViewFault.None, r.Faults, "captured: no active faults");
                t.Equal(0, r.FanPercent, "captured: adapter fan off");
            }
        }

        t.Section("Synthetic frame: every field in its own slot");
        {
            var pins = Enumerable.Range(0, 6).Select(i => ((short)(12000 + i), (uint)(1000 * (i + 1)))).ToArray();
            var r = WireView.Parse(Frame(pins, temps: new short[] { 355, 400, 612, 2001 }, cap: 2,
                faults: (ushort)WireViewFault.CurrentImbalance, log: (ushort)(WireViewFault.CurrentImbalance | WireViewFault.OverPower)));
            t.Check(r != null, "synthetic: a well-formed frame decodes");
            if (r != null)
            {
                for (int i = 0; i < 6; i++)
                {
                    t.Equal((12000 + i) / 1000.0, r.Pins[i].Volts, $"synthetic: pin {i + 1} voltage from its own slot");
                    t.Equal(i + 1.0, r.Pins[i].Amps, $"synthetic: pin {i + 1} current from its own slot");
                }
                t.Equal(61.2, r.External1C, "synthetic: probe 1 reads when plugged in");
                t.Equal(null, r.External2C, "synthetic: 200.1 °C is out of range = absent");
                t.Equal((int?)300, r.PsuCapWatts, "synthetic: cap enum 2 = 300 W");
                t.Equal(WireViewFault.CurrentImbalance, r.Faults, "synthetic: active fault mask");
                t.Check(r.FaultLog.HasFlag(WireViewFault.OverPower), "synthetic: fault log is its own field");
            }
            t.Equal(null, WireView.Parse(Frame(pins, cap: 4))?.PsuCapWatts, "synthetic: an unknown cap enum is no rating, not a guess");
            var negative = WireView.Parse(Frame(new[] { ((short)-500, 0u) }));
            t.Equal(-0.5, negative?.Pins[0].Volts, "synthetic: pin voltage is signed (a slightly negative idle pin is real)");
        }

        t.Section("Sanity gate");
        {
            var ok = new[] { ((short)12000, 8000u) };
            t.Check(WireView.Parse(Frame(ok)) != null, "gate: the baseline passes");
            t.Check(WireView.Parse(Frame(ok).AsSpan(0, 99)) == null, "gate: a short read is refused");
            t.Check(WireView.Parse(Frame(ok, pad1: 1)) == null, "gate: non-zero padding after the fan byte = desynced");
            t.Check(WireView.Parse(Frame(ok, pad2: 1)) == null, "gate: non-zero padding after the cap byte = desynced");
            t.Check(WireView.Parse(Frame(ok, fan: 101)) == null, "gate: fan above 100 %");
            t.Check(WireView.Parse(Frame(ok, vdd: 6001)) == null, "gate: Vdd above 6 V");
            t.Check(WireView.Parse(Frame(new[] { ((short)20001, 1000u) })) == null, "gate: pin above 20 V");
            t.Check(WireView.Parse(Frame(new[] { ((short)-1001, 1000u) })) == null, "gate: pin below -1 V");
            t.Check(WireView.Parse(Frame(new[] { ((short)12000, 60001u) })) == null, "gate: pin above 60 A");
        }

        t.Section("Vendor data");
        {
            t.Equal((WireView.State.Connected, (string?)"WireView Pro II"), WireView.Identify(0xEF, 0x05), "identify: Pro II");
            t.Equal((WireView.State.Connected, (string?)"WireView Pro II Noctua Edition"), WireView.Identify(0xEF, 0x06), "identify: Noctua shares the format");
            t.Equal(WireView.State.Unsupported, WireView.Identify(0xEF, 0x07).Kind, "identify: WireView II has another frame");
            t.Equal(WireView.State.Unsupported, WireView.Identify(0xEF, 0x08).Kind, "identify: Phanteks edition likewise");
            t.Equal(WireView.State.NotFound, WireView.Identify(0xEF, 0x09).Kind, "identify: unknown product is not ours");
            t.Equal(WireView.State.NotFound, WireView.Identify(0x00, 0x05).Kind, "identify: another vendor's STM32 is not ours");
        }

        t.Section("Fault names");
        {
            t.Check(!WireView.Describe(WireViewFault.None).Any(), "faults: none = no text");
            var all = WireView.Describe((WireViewFault)0x3F).ToList();
            t.Equal(6, all.Count, "faults: every defined bit has a name");
            t.Equal("over-current on a wire", all[0], "faults: the per-wire trip leads (the 12VHPWR failure mode)");
        }
    }

    /// <summary>A sensor frame built field by field, as the device lays it out.
    /// Unlisted pins are zero; totals are derived like the firmware does.</summary>
    static byte[] Frame((short Mv, uint Ma)[] pins, short[]? temps = null, ushort vdd = 3300, byte fan = 0,
        byte cap = 0, ushort faults = 0, ushort log = 0, byte pad1 = 0, byte pad2 = 0)
    {
        var f = new byte[WireView.FrameSize];
        temps ??= new short[] { 300, 320, -1000, -1000 };
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(i * 2), temps[i]);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(8), vdd);
        f[10] = fan; f[11] = pad1;
        uint totalMw = 0, totalMa = 0; long sumMv = 0;
        for (int i = 0; i < 6; i++)
        {
            var (mv, ma) = i < pins.Length ? pins[i] : ((short)0, 0u);
            uint mw = (uint)Math.Max(0, mv * (long)ma / 1000);
            var p = f.AsSpan(12 + i * 12);
            BinaryPrimitives.WriteInt16LittleEndian(p, mv);
            BinaryPrimitives.WriteUInt32LittleEndian(p[4..], ma);
            BinaryPrimitives.WriteUInt32LittleEndian(p[8..], mw);
            totalMw += mw; totalMa += ma; sumMv += mv;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(84), totalMw);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(88), totalMa);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(92), (ushort)Math.Max(0, sumMv / 6));
        f[94] = cap; f[95] = pad2;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(96), faults);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(98), log);
        return f;
    }
}

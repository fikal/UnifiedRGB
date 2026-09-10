using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The Lian Li tinyuz LZ codec: what every baked animation      |
| frame is compressed with before it goes out over RF.         |
|                                                              |
| A codec is only ever as good as its round-trip, so every     |
| case here decodes back to the exact input before it is       |
| allowed to claim a compression ratio. The scratch-cache      |
| section guards the 4096B boundary where the encoder switches |
| between a cached working buffer and a fresh one, which is    |
| the kind of detail that silently corrupts one size of        |
| payload and no other.                                        |
\*-----------------------------------------------------------*/

static class CodecSuite
{
    public static void Run(Harness t)
    {
        t.Section("tinyuz LZ codec round-trip + compression");
        {
            bool RoundTrips(byte[] raw, string name)
            {
                var comp = UnifiedRgb.Core.Devices.LianLiTinyuz.Encode(raw);
                var back = UnifiedRgb.Core.Devices.LianLiTinyuz.Decode(comp);
                bool ok = back.Length == raw.Length;
                for (int i = 0; ok && i < raw.Length; i++) ok = back[i] == raw[i];
                t.Check(ok, $"tinyuz round-trip {name} ({raw.Length}B -> {comp.Length}B)");
                return ok;
            }

            RoundTrips(System.Array.Empty<byte>(), "empty");
            RoundTrips(new byte[] { 42 }, "single byte");
            RoundTrips(new byte[] { 1, 2, 3 }, "three literals");
            RoundTrips(new byte[] { 7, 7, 7, 7, 7, 7, 7, 7, 7, 7 }, "RLE run");

            // Deterministic pseudo-random (no Random - banned in scripts/tests here).
            var rnd = new byte[2000];
            uint s = 0x12345678;
            for (int i = 0; i < rnd.Length; i++) { s = s * 1664525 + 1013904223; rnd[i] = (byte)(s >> 24); }
            RoundTrips(rnd, "incompressible random");

            // A far back-reference (> BigPosForLen = 2687) exercises the +1-len path.
            var far = new byte[6000];
            for (int i = 0; i < 200; i++) far[i] = (byte)(i * 3);
            for (int i = 0; i < 200; i++) far[5000 + i] = (byte)(i * 3);   // repeats block from offset 0
            RoundTrips(far, "far match >2687");

            // Realistic baked fan animation: 64 frames x 176 LEDs x 3, a hue slowly
            // rotating - consecutive frames nearly identical, big flat runs per frame.
            const int frames = 64, leds = 176;
            var anim = new byte[frames * leds * 3];
            for (int f = 0; f < frames; f++)
                for (int l = 0; l < leds; l++)
                {
                    int baseHue = (f * 4 + l / 8 * 20) % 256;   // coarse bands, slow drift
                    int o = (f * leds + l) * 3;
                    anim[o] = (byte)baseHue; anim[o + 1] = (byte)(255 - baseHue); anim[o + 2] = 40;
                }
            if (RoundTrips(anim, "64-frame fan animation"))
            {
                var comp = UnifiedRgb.Core.Devices.LianLiTinyuz.Encode(anim);
                t.Check(comp.Length < anim.Length / 3,
                    $"fan animation compresses hard ({anim.Length}B -> {comp.Length}B, want < {anim.Length / 3})");
            }
        }

        t.Section("tinyuz scratch-cache threshold (#4)");
        {
            byte[] Pseudo(int n, uint seed)
            {
                // Semi-compressible: a drifting byte pattern with repeated blocks, so
                // both literal and match paths run.
                var b = new byte[n];
                uint s = seed;
                for (int i = 0; i < n; i++)
                {
                    if ((i / 64) % 3 == 2 && i >= 128) { b[i] = b[i - 128]; continue; }
                    s = s * 1664525 + 1013904223;
                    b[i] = (byte)((s >> 24) & 0x3F);
                }
                return b;
            }
            var small = Pseudo(300, 7);
            var smallBefore = LianLiTinyuz.Encode(small);
            foreach (int n in new[] { 4096, 4097 })
            {
                var raw = Pseudo(n, 0xC0FFEE + (uint)n);
                var a = LianLiTinyuz.Encode(raw);
                var b = LianLiTinyuz.Encode(raw);
                t.Check(a.AsSpan().SequenceEqual(b), $"tinyuz {n}B encodes identically twice on one thread ({a.Length}B)");
                t.Check(LianLiTinyuz.Decode(a).AsSpan().SequenceEqual(raw), $"tinyuz {n}B round-trips");
            }
            // The cached (small) path after a fresh-scratch (big) encode: caches were cleared correctly.
            var smallAfter = LianLiTinyuz.Encode(small);
            t.Check(smallBefore.AsSpan().SequenceEqual(smallAfter), "tinyuz cached path is unchanged after a >4096B encode");
            t.Check(LianLiTinyuz.Decode(smallAfter).AsSpan().SequenceEqual(small), "tinyuz small buffer round-trips after the big one");
        }
    }
}

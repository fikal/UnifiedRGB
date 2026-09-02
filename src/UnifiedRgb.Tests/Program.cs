using System.IO;
using System.Collections.ObjectModel;
using System.Text;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Input;
using UnifiedRgb.Core.Net;
using UnifiedRgb.Core.Sensors;

/*-----------------------------------------------------------*\
| UnifiedRGB test runner — zero dependencies (the product's   |
| no-NuGet rule extends here). Pure logic only: color math,   |
| key maps, wire-format parsing, atomic file writes.          |
| Run: dotnet run --project src/UnifiedRgb.Tests              |
| Exit code = number of failures.                             |
\*-----------------------------------------------------------*/

int passed = 0, failed = 0;

void Check(bool cond, string name)
{
    if (cond) { passed++; return; }
    failed++;
    Console.WriteLine($"  FAIL  {name}");
}

void Equal<T>(T expected, T actual, string name)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{name}: expected {expected}, got {actual}");

/*---------------- Rgb hex parsing ----------------*/
{
    Check(Rgb.TryFromHex("FF8000", out var c1) && c1 == new Rgb(255, 128, 0), "TryFromHex plain");
    Check(Rgb.TryFromHex("#00ff00", out var c2) && c2 == new Rgb(0, 255, 0), "TryFromHex # + lowercase");
    Check(Rgb.TryFromHex(" 0000FF ", out var c3) && c3 == new Rgb(0, 0, 255), "TryFromHex whitespace");
    Check(!Rgb.TryFromHex("", out _), "TryFromHex empty rejected");
    Check(!Rgb.TryFromHex(null, out _), "TryFromHex null rejected");
    Check(!Rgb.TryFromHex("FF80", out _), "TryFromHex partial rejected");
    Check(!Rgb.TryFromHex("GGGGGG", out _), "TryFromHex non-hex rejected");
    Check(!Rgb.TryFromHex("FF8000AA", out _), "TryFromHex too long rejected");
    Equal("#FF8000", new Rgb(255, 128, 0).ToString(), "Rgb.ToString");
    Check(Rgb.TryFromHex(new Rgb(12, 34, 56).ToString(), out var rt) && rt == new Rgb(12, 34, 56),
        "ToString/TryFromHex roundtrip");
    bool threw = false;
    try { Rgb.FromHex("nope"); } catch (FormatException) { threw = true; }
    Check(threw, "FromHex throws FormatException on junk");
}

/*---------------- HSV color math ----------------*/
{
    Equal(new Rgb(255, 0, 0), ColorUtil.HsvToRgb(0, 1, 1), "HSV 0 = red");
    Equal(new Rgb(0, 255, 0), ColorUtil.HsvToRgb(120, 1, 1), "HSV 120 = green");
    Equal(new Rgb(0, 0, 255), ColorUtil.HsvToRgb(240, 1, 1), "HSV 240 = blue");
    Equal(ColorUtil.HsvToRgb(30, 1, 1), ColorUtil.HsvToRgb(390, 1, 1), "HSV wraps at 360");
    Equal(ColorUtil.HsvToRgb(30, 1, 1), ColorUtil.HsvToRgb(-330, 1, 1), "HSV negative wraps");
    Equal(new Rgb(0, 0, 0), ColorUtil.HsvToRgb(180, 1, 0), "HSV v=0 = black");
    var grey = ColorUtil.HsvToRgb(300, 0, 0.5);
    Check(grey.R == grey.G && grey.G == grey.B, "HSV s=0 = grey");
}

/*---------------- HID usage -> VK map ----------------*/
{
    Equal((int)'A', HidUsageVk.ToVk(0x04), "usage A");
    Equal((int)'Z', HidUsageVk.ToVk(0x1D), "usage Z");
    Equal((int)'1', HidUsageVk.ToVk(0x1E), "usage 1");
    Equal((int)'0', HidUsageVk.ToVk(0x27), "usage 0");
    Equal(0x0D, HidUsageVk.ToVk(0x28), "usage Enter");
    Equal(0x20, HidUsageVk.ToVk(0x2C), "usage Space");
    Equal(0x70, HidUsageVk.ToVk(0x3A), "usage F1");
    Equal(0x7B, HidUsageVk.ToVk(0x45), "usage F12");
    Equal(0x25, HidUsageVk.ToVk(0x50), "usage Left");
    Equal(0x60, HidUsageVk.ToVk(0x62), "usage Num0");
    Equal(0x69, HidUsageVk.ToVk(0x61), "usage Num9");
    Equal(0xA0, HidUsageVk.ToVk(0xE1), "usage LShift");
    Equal(0x5C, HidUsageVk.ToVk(0xE7), "usage RWin");
    Equal(-1, HidUsageVk.ToVk(0xF0), "unknown usage = -1");
    Equal(-1, HidUsageVk.ToVk(0x00), "usage 0 = -1");
}

/*---------------- OpenRGB v1 device blob parsing ----------------*/
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
    Equal(7, d.Index, "blob index");
    Equal(5, d.Type, "blob type");
    Equal("Test Keyboard", d.Name, "blob name");
    Equal("TestVendor", d.Vendor, "blob vendor");
    Equal("HID: vid_1234&pid_5678", d.Location, "blob location");
    Equal(2, d.Zones.Count, "blob zone count");
    Equal("Matrix Zone", d.Zones[0].Name, "blob zone 0 name");
    Equal(3, d.Zones[0].MatrixW, "blob matrix width");
    Equal(2, d.Zones[0].MatrixH, "blob matrix height");
    Check(d.Zones[0].Matrix != null && d.Zones[0].Matrix!.Length == 6, "blob matrix cells");
    Equal(4, d.Zones[1].LedCount, "blob zone 1 leds");
    Check(d.Zones[1].Matrix == null, "blob zone 1 no matrix");
    Equal(10, d.LedCount, "blob led count");
    Equal(10, d.Colors.Length, "blob colors");
    Equal(0x56u, d.Colors[0] & 0xFF, "blob color R byte");
}

/*---------------- SafeFile atomic writes ----------------*/
{
    string dir = Path.Combine(Path.GetTempPath(), "unifiedrgb-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string f = Path.Combine(dir, "state.json");
    SafeFile.WriteAllText(f, "first");
    Equal("first", File.ReadAllText(f), "SafeFile create");
    SafeFile.WriteAllText(f, "second");
    Equal("second", File.ReadAllText(f), "SafeFile replace");
    Check(!File.Exists(f + ".tmp"), "SafeFile leaves no temp file");
    Directory.Delete(dir, recursive: true);
}

/*---------------- Fan duty math ----------------*/
{
    Equal((byte)0, UnifiedRgb.Core.Sensors.IteSuperIo.DutyByte(0), "DutyByte 0%");
    Equal((byte)255, UnifiedRgb.Core.Sensors.IteSuperIo.DutyByte(100), "DutyByte 100%");
    Equal((byte)128, UnifiedRgb.Core.Sensors.IteSuperIo.DutyByte(50), "DutyByte 50% rounds");
    Equal((byte)76, UnifiedRgb.Core.Sensors.IteSuperIo.DutyByte(30), "DutyByte floor value (banker's rounding)");
    Equal((byte)255, UnifiedRgb.Core.Sensors.IteSuperIo.DutyByte(150), "DutyByte clamps high");
    Equal((byte)0, UnifiedRgb.Core.Sensors.IteSuperIo.DutyByte(-5), "DutyByte clamps low");
}

/*---------------- Fan curve interpolation ----------------*/
{
    var c = new UnifiedRgb.Core.Sensors.FanCurve("t", UnifiedRgb.Core.Sensors.TempSource.Cpu,
        new UnifiedRgb.Core.Sensors.CurvePoint[]
        { new(30, 30), new(50, 50), new(80, 100) });
    Equal(30, c.DutyAt(10), "curve below first point clamps");
    Equal(30, c.DutyAt(30), "curve at first point");
    Equal(40, c.DutyAt(40), "curve midpoint interpolates");
    Equal(50, c.DutyAt(50), "curve at knee");
    Equal(75, c.DutyAt(65), "curve interpolates second segment");
    Equal(100, c.DutyAt(80), "curve at last point");
    Equal(100, c.DutyAt(95), "curve above last point clamps");

    var quiet = UnifiedRgb.Core.Sensors.FanCurve.Preset_("Quiet");
    Check(quiet.DutyAt(20) == 30 && quiet.DutyAt(90) == 100, "Quiet preset spans floor..100");
    var gpuQuiet = UnifiedRgb.Core.Sensors.FanCurve.Preset_("Quiet", floor: 0);
    Check(gpuQuiet.DutyAt(20) == 0 && gpuQuiet.DutyAt(45) == 0, "GPU Quiet is fan-stop when idle");
    Check(gpuQuiet.DutyAt(90) == 100, "GPU Quiet still reaches 100 hot");
    Check(gpuQuiet.MatchesPreset(), "floored preset still matches itself");
    Check(UnifiedRgb.Core.Sensors.FanCurve.Preset_("Full").DutyAt(20) == 100, "Full preset is 100 everywhere");
    Check(quiet.MatchesPreset(), "unedited preset matches");
    quiet.Points[0] = new UnifiedRgb.Core.Sensors.CurvePoint(20, 35);
    Check(!quiet.MatchesPreset(), "edited preset no longer matches");
}

/*---------------- tinyuz LZ codec round-trip + compression ----------------*/
{
    bool RoundTrips(byte[] raw, string name)
    {
        var comp = UnifiedRgb.Core.Devices.LianLiTinyuz.Encode(raw);
        var back = UnifiedRgb.Core.Devices.LianLiTinyuz.Decode(comp);
        bool ok = back.Length == raw.Length;
        for (int i = 0; ok && i < raw.Length; i++) ok = back[i] == raw[i];
        Check(ok, $"tinyuz round-trip {name} ({raw.Length}B -> {comp.Length}B)");
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
        Check(comp.Length < anim.Length / 3,
            $"fan animation compresses hard ({anim.Length}B -> {comp.Length}B, want < {anim.Length / 3})");
    }
}

/*---------------- LivePalette (render-thread palette view) ----------------*/
{
    var src = new ObservableCollection<Rgb> { new(1, 1, 1), new(2, 2, 2), new(3, 3, 3) };
    var live = new LivePalette(src);
    Equal(3, live.Count, "LivePalette tracks initial count");
    Equal(new Rgb(2, 2, 2), live[1], "LivePalette indexes the snapshot");
    var snap = live.Snapshot;
    src.Clear();
    Equal(0, live.Count, "LivePalette follows Clear()");
    Equal(3, snap.Length, "an older snapshot is immutable");
    Equal(new Rgb(255, 255, 255), live[0], "empty palette reads white, never throws");
    src.Add(new Rgb(9, 9, 9));
    Equal(new Rgb(9, 9, 9), live[5], "out-of-range index clamps to the last color (stale Count from a longer snapshot)");
    Equal(new Rgb(9, 9, 9), live[0], "in-range index after rebuild");
}

/*---------------- ChromaFeed frame publish ----------------*/
{
    ChromaFeed.PushGrid(new[] { new Rgb(10, 0, 0), new Rgb(0, 10, 0), new Rgb(0, 0, 10) }, 1, 3);
    Check(ChromaFeed.Active, "PushGrid marks the feed active");
    Equal(new Rgb(10, 0, 0), ChromaFeed.Sample(0.05f, 0.5f), "Sample left cell");
    Equal(new Rgb(0, 0, 10), ChromaFeed.Sample(0.95f, 0.5f), "Sample right cell");
    Equal(new Rgb(0, 0, 10), ChromaFeed.Sample(5f, -3f), "Sample clamps out-of-range coordinates");
    ChromaFeed.PushGrid(new[] { new Rgb(7, 7, 7) }, 6, 22);   // dims larger than the grid: rejected
    Equal(new Rgb(10, 0, 0), ChromaFeed.Sample(0.05f, 0.5f), "undersized grid is rejected (dims/grid published atomically)");
    ChromaFeed.PushGrid(new[] { new Rgb(7, 7, 7) }, 1, 1);
    Equal(new Rgb(7, 7, 7), ChromaFeed.Sample(0.9f, 0.9f), "1x1 static frame samples everywhere");
}

/*---------------- FanCurve hardening ----------------*/
{
    var curve = new FanCurve { Points = null! };
    Equal(0, curve.Points.Count, "null Points (hand-edited fan-config.json) becomes empty");
    Equal(0, curve.DutyAt(50), "empty curve yields 0 duty instead of throwing");
    var json = System.Text.Json.JsonSerializer.Deserialize<FanCurve>("{\"Preset\":\"x\",\"Points\":null}");
    Check(json != null && json.Points.Count == 0, "JSON null Points round-trips to empty");
}

/*---------------- Embedded PawnIO modules ----------------*/
{
    Check(UnifiedRgb.Core.Native.PawnIO.ReadEmbeddedModule("SmbusPIIX4.bin") is { Length: > 100 }, "PIIX4 module embedded");
    Check(UnifiedRgb.Core.Native.PawnIO.ReadEmbeddedModule("SmbusI801.bin") is { Length: > 100 }, "I801 module embedded");
    Check(UnifiedRgb.Core.Native.PawnIO.ReadEmbeddedModule("nope.bin") == null, "unknown module is null, not a throw");
}

/*---------------- Authenticode (installer signature gate) ----------------*/
{
    // Our own (unsigned) assembly must never pass, whatever subject is asked for.
    string self = typeof(UnifiedRgb.Core.Rgb).Assembly.Location;
    Check(!UnifiedRgb.Core.Native.Authenticode.IsSignedBy(self, "CN=namazso.eu", out var why) && why.Length > 0,
        $"unsigned assembly rejected ({why})");
    // A signed system binary passes the trust check but fails the publisher pin.
    string kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
    Check(!UnifiedRgb.Core.Native.Authenticode.IsSignedBy(kernel32, "CN=namazso.eu", out var who) && who.Contains("expected"),
        $"wrong publisher rejected ({who})");
    // The real PawnIO library, when installed on this machine, satisfies the pin.
    string pawn = @"C:\Program Files\PawnIO\PawnIOLib.dll";
    if (File.Exists(pawn))
        Check(UnifiedRgb.Core.Native.Authenticode.IsSignedBy(pawn, "CN=namazso.eu", out var ok), $"PawnIOLib.dll accepted ({ok})");
}

Console.WriteLine($"{passed} passed, {failed} failed");
return failed;

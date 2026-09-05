using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Automation;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Input;
using UnifiedRgb.Core.Native;
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

/*===========================================================*\
| Review 2026-09-02 fix coverage. Each block names the fix    |
| report ids it pins (review-2026-09-02/fix-report.json).     |
\*===========================================================*/

// Shared helpers for the blocks below.
static bool WaitUntil(Func<bool> cond, int timeoutMs)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; Thread.Sleep(5); }
    return cond();
}
static LedPos[] Line(int n)
{
    var p = new LedPos[n];
    for (int i = 0; i < n; i++) p[i] = new LedPos(n <= 1 ? 0.5f : i / (float)(n - 1), 0.5f);
    return p;
}
static LedPos[] Grid(int w, int h)
{
    var p = new LedPos[w * h];
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            p[y * w + x] = new LedPos(x / (float)(w - 1), y / (float)(h - 1));
    return p;
}
static bool SameWithin(Rgb[] a, Rgb[] b, int lsb)
{
    for (int i = 0; i < a.Length; i++)
        if (Math.Abs(a[i].R - b[i].R) > lsb || Math.Abs(a[i].G - b[i].G) > lsb || Math.Abs(a[i].B - b[i].B) > lsb) return false;
    return true;
}
static string TempDir()
{
    string d = Path.Combine(Path.GetTempPath(), "unifiedrgb-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(d);
    return d;
}

/*---------------- SafeFile: parent dirs, no BOM, failure cleanup (#16 #20 #159 #162) ----------------*/
{
    string root = TempDir();
    try
    {
        string nested = Path.Combine(root, "a", "b", "x.json");
        SafeFile.WriteAllText(nested, "{}");
        Check(File.Exists(nested) && File.ReadAllText(nested) == "{}", "SafeFile creates missing parent directories");
        Check(!File.Exists(nested + ".tmp"), "SafeFile nested write leaves no .tmp");

        string utf = Path.Combine(root, "utf.txt");
        SafeFile.WriteAllText(utf, "héllo ☃");
        var bytes = File.ReadAllBytes(utf);
        Check(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "SafeFile writes UTF-8 without a BOM");
        Equal("héllo ☃", File.ReadAllText(utf), "SafeFile non-ASCII text round-trips");
        SafeFile.WriteAllText(utf, "second");
        Equal("second", File.ReadAllText(utf), "SafeFile second write replaces the first");
        Check(!File.Exists(utf + ".tmp"), "SafeFile replace leaves no .tmp");

        // The target is an existing DIRECTORY: the rename must fail, and the
        // half-written .tmp must not be left behind for the next save.
        string asDir = Path.Combine(root, "i-am-a-dir");
        Directory.CreateDirectory(asDir);
        bool threw = false;
        try { SafeFile.WriteAllText(asDir, "x"); } catch (Exception) { threw = true; }
        Check(threw, "SafeFile throws when the target cannot be replaced");
        Check(!File.Exists(asDir + ".tmp"), "SafeFile failure deletes its .tmp");
    }
    finally { try { Directory.Delete(root, recursive: true); } catch { } }

    _ = AppPaths.ConfigDir;   // forces the cctor
    Check(Directory.Exists(AppPaths.ConfigDir), "AppPaths cctor creates ConfigDir");
    Check(Directory.Exists(AppPaths.LocalDir), "AppPaths cctor creates LocalDir (fan-config.json home)");
    Equal(Path.Combine(AppPaths.LocalDir, "fan-config.json"), AppPaths.Local("fan-config.json"), "AppPaths.Local joins under LocalDir");
}

/*---------------- Authenticode exact-RDN pin (#36 #37 #77) ----------------*/
{
    string kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
    Check(Authenticode.IsSignedBy(kernel32, "CN=Microsoft Windows", out var d1), $"exact CN accepted ({d1})");
    Check(Authenticode.IsSignedBy(kernel32, "O=Microsoft Corporation", out var d2), $"other RDN type (O=) accepted ({d2})");
    Check(Authenticode.IsSignedBy(kernel32, "cn=microsoft windows", out _), "RDN type/value compare is case-insensitive");
    Check(!Authenticode.IsSignedBy(kernel32, "CN=Microsoft Win", out var d3) && d3.Contains("expected"), $"CN prefix substring refused ({d3})");
    Check(!Authenticode.IsSignedBy(kernel32, "CN=Windows", out _), "CN suffix substring refused");
    Check(!Authenticode.IsSignedBy(kernel32, "O=Microsoft Windows", out _), "value must live in the pinned RDN type");
    Check(!Authenticode.IsSignedBy(kernel32, "Microsoft", out _), "a pin without '=' never matches");
    Check(!Authenticode.IsSignedBy(kernel32, "=Microsoft Windows", out _), "a pin with an empty type never matches");
    Check(!Authenticode.IsSignedBy(Path.Combine(Environment.SystemDirectory, "no-such-file-xyz.dll"), "CN=Microsoft Windows", out var d4) && d4.Length > 0,
        "missing file is refused with a reason");

    // #37/#77: the WINTRUST_FILE_INFO path copy is now released; hammer all three
    // exits (trusted+match, trusted+mismatch, untrusted) and require stable results.
    string self = typeof(Rgb).Assembly.Location;
    bool stable = true;
    for (int i = 0; i < 15 && stable; i++)
        stable = Authenticode.IsSignedBy(kernel32, "CN=Microsoft Windows", out _)
              && !Authenticode.IsSignedBy(kernel32, "CN=nobody", out _)
              && !Authenticode.IsSignedBy(self, "CN=Microsoft Windows", out _);
    Check(stable, "repeated IsSignedBy is stable across match / mismatch / untrusted paths");
}

/*---------------- IteSuperIo dead fan-control block removed (#140 #87 #86) ----------------*/
{
    var t = typeof(IteSuperIo);
    var any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
    foreach (var name in new[] { "SetFanDutyPercent", "ReadPwmRaw", "WritePwmRaw", "RestoreFan", "RestoreAllFans", "ForceRestore", "IsFanOverridden" })
        Check(t.GetMethod(name, any) == null, $"IteSuperIo.{name} is gone");
    Check(t.GetProperty("FanCount", any) == null && t.GetProperty("SavedPwm", any) == null, "IteSuperIo.FanCount/SavedPwm are gone");
    Check(t.GetMethod("ReadEcRaw", any) != null && t.GetMethod("WriteEcRaw", any) != null && t.GetMethod("DumpLdns", any) != null,
        "IteSuperIo raw probe primitives kept for the CLI");
}

/*---------------- Embedded PawnIO modules, single copy (#151 #12) ----------------*/
{
    foreach (var m in new[] { "LpcIO.bin", "AMDFamily17.bin", "IsaBridgeEC.bin" })
        Check(PawnIO.ReadEmbeddedModule(m) is { Length: > 100 }, $"{m} embedded in Core");
    int copies = typeof(PawnIO).Assembly.GetManifestResourceNames().Count(n => n.EndsWith("SmbusI801.bin"));
    Equal(1, copies, "exactly one SmbusI801.bin manifest resource in Core");
}

/*---------------- FanCurve.Clone contract (#82 #112) ----------------*/
{
    var src = new FanCurve("Custom", TempSource.Cpu, new CurvePoint[] { new(60, 80), new(30, 30) }, floor: 20);
    var cl = src.Clone();
    Check(!ReferenceEquals(src, cl) && !ReferenceEquals(src.Points, cl.Points), "Clone yields a distinct instance and Points list");
    Check(cl.Preset == "Custom" && cl.Source == TempSource.Cpu && cl.Floor == 20 && cl.Points.Count == 2, "Clone copies preset/source/floor/points");
    cl.Points[0] = new CurvePoint(10, 10);
    Equal(30, src.Points[0].TempC, "mutating the clone leaves the source untouched");
    var unsorted = new FanCurve { Points = new List<CurvePoint> { new(80, 100), new(20, 20) } };
    Equal(20, unsorted.Clone().Points[0].TempC, "Clone re-sorts hand-edited points by temperature");
}

/*---------------- tinyuz scratch-cache threshold (#4) ----------------*/
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
        Check(a.AsSpan().SequenceEqual(b), $"tinyuz {n}B encodes identically twice on one thread ({a.Length}B)");
        Check(LianLiTinyuz.Decode(a).AsSpan().SequenceEqual(raw), $"tinyuz {n}B round-trips");
    }
    // The cached (small) path after a fresh-scratch (big) encode: caches were cleared correctly.
    var smallAfter = LianLiTinyuz.Encode(small);
    Check(smallBefore.AsSpan().SequenceEqual(smallAfter), "tinyuz cached path is unchanged after a >4096B encode");
    Check(LianLiTinyuz.Decode(smallAfter).AsSpan().SequenceEqual(small), "tinyuz small buffer round-trips after the big one");
}

// --- Razer HID wire format (openrazer's report layout: 90 wire bytes behind report id 0) ---
{
    var r = RazerHid.NewReport(0x1F, 0x00, 0x81, 0x02);
    Check(r.Length == 91 && r[0] == 0x00, "razer report: 91 bytes, report id 0");
    Check(r[2] == 0x1F && r[6] == 0x02 && r[7] == 0x00 && r[8] == 0x81, "razer report: tid/size/class/cmd at wire 1/5/6/7");
    RazerHid.Seal(r);
    byte crc = 0; for (int i = 3; i <= 88; i++) crc ^= r[i];
    Check(r[89] == crc && r[90] == 0, "razer report: crc = XOR of wire bytes 2..87 at wire 88, reserved 0");

    var colors = Enumerable.Range(0, 13).Select(i => new Rgb((byte)i, (byte)(i * 2), (byte)(i * 3))).ToArray();
    var f = RazerHid.CustomFrameReport(0x1F, 0, 0, 12, colors, 0);
    Check(f[6] == 5 + 39 && f[7] == 0x0F && f[8] == 0x03, "razer custom frame: class 0F cmd 03, size 5 + 3n");
    Check(f[9] == 0 && f[10] == 0 && f[11] == 0 && f[12] == 0 && f[13] == 12, "razer custom frame: row 0, cols 0..12");
    Check(f[14] == 0 && f[14 + 3 * 12] == 12 && f[15 + 3 * 12] == 24 && f[16 + 3 * 12] == 36, "razer custom frame: RGB triplets from args[5]");
    var chunk = RazerHid.CustomFrameReport(0x1F, 2, 25, 30, Enumerable.Repeat(Rgb.Red, 31).ToArray(), 25);
    Check(chunk[11] == 2 && chunk[12] == 25 && chunk[13] == 30 && chunk[6] == 5 + 18, "razer custom frame: a later chunk carries its own row/start/stop");

    var d = RazerHid.DpiReport(0x1F, 1600, 800);
    Check(d[7] == 0x04 && d[8] == 0x05 && d[6] == 7 && d[9] == 0x01, "razer dpi: class 04 cmd 05, VARSTORE");
    Check(d[10] == 0x06 && d[11] == 0x40 && d[12] == 0x03 && d[13] == 0x20, "razer dpi: X/Y big-endian");
    Check(RazerHid.DpiReport(0x1F, 5, 99999)[11] == 100 && RazerHid.DpiReport(0x1F, 5, 99999)[12] == 0xAF, "razer dpi: clamped to 100..45000");

    var stages = new (int, int)[] { (400, 400), (800, 800), (1600, 1600), (3200, 3200), (6400, 6400) };
    var s = RazerHid.DpiStagesReport(0x1F, 3, stages);
    Check(s[7] == 0x04 && s[8] == 0x06 && s[6] == 0x26 && s[10] == 3 && s[11] == 5, "razer dpi stages: class 04 cmd 06, active 3 of 5");
    Check(s[12] == 1 && s[12 + 7] == 2 && s[12 + 28] == 5, "razer dpi stages: 7-byte entries numbered 1..5");
    var back = RazerHid.DecodeDpiStages(s.AsSpan(9, 80));
    Check(back.Active == 3 && back.Stages.Length == 5 && back.Stages[2] == (1600, 1600) && back.Stages[4] == (6400, 6400), "razer dpi stages: encode/decode round-trip");
    Check(RazerHid.DpiStagesReport(0x1F, 9, stages.Take(2).ToArray())[10] == 2, "razer dpi stages: active clamped to the count");

    Check(RazerHid.PollingHz(0x01) == 1000 && RazerHid.PollingHz(0x02) == 500 && RazerHid.PollingHz(0x08) == 125 && RazerHid.PollingHz(0x40) == 0, "razer polling: code -> Hz");
    Check(RazerHid.PollingCode(1000) == 0x01 && RazerHid.PollingCode(500) == 0x02 && RazerHid.PollingCode(125) == 0x08 && RazerHid.PollingCode(2000) == 0, "razer polling: Hz -> code");

    var pad = RazerHid.PadPositionsFor(20);
    Check(pad.Length == 20 && pad.All(p => p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1), "razer pad: n perimeter positions inside the unit box");
    Check(pad[0] == new LedPos(0, 0) && pad[5].Y == 0 && pad[10].X > 0.99f && pad.Distinct().Count() == 20, "razer pad: clockwise from the top-left corner, all distinct");
    var (guess, src) = RazerHid.ResolveCount(0x0FFF, null);
    Check(guess == 20 && src == "guessed", "razer pad: no config, no probe -> 20 guessed");
    Check(RazerHid.ResolveCount(0x0FFF, 19) == (19, "probed") && RazerHid.ResolveCount(0x0FFF, 999).Count == RazerHid.MaxLeds, "razer pad: probe wins over the guess and is capped");
}

/*---------------- LianLiWireless.LoadLayout (#1) ----------------*/
{
    // Real config path (LoadLayout reads AppPaths.Config): the user's file, if
    // any, is preserved byte-for-byte around the test.
    string path = AppPaths.Config("lianli-layout.json");
    byte[]? orig = File.Exists(path) ? File.ReadAllBytes(path) : null;
    try
    {
        File.WriteAllText(path, "{\"order\":[3,0,1,2],\"breaks\":[3,0,9,3]}");
        var (order, breaks) = LianLiWireless.LoadLayout(4);
        Check(order.SequenceEqual(new[] { 3, 0, 1, 2 }), "LoadLayout honours the saved order");
        Check(breaks.SequenceEqual(new[] { 3 }), "LoadLayout keeps a single-slot trailing group (break at 3 of 4) and drops 0/out-of-range/duplicates");
        var (o6, b6) = LianLiWireless.LoadLayout(6);
        Check(o6.SequenceEqual(Enumerable.Range(0, 6)) && b6.Length == 0, "LoadLayout falls back to identity on a fan-count mismatch");
        File.WriteAllText(path, "{\"order\":[0,0,1,2]}");
        Check(LianLiWireless.LoadLayout(4).Order.SequenceEqual(Enumerable.Range(0, 4)), "LoadLayout rejects a non-permutation order");
        File.WriteAllText(path, "{not json");
        Check(LianLiWireless.LoadLayout(4).Order.SequenceEqual(Enumerable.Range(0, 4)), "LoadLayout falls back to identity on malformed json");
    }
    finally
    {
        if (orig != null) File.WriteAllBytes(path, orig);
        else File.Delete(path);
    }
}

/*---------------- OpenRgbDevice.BuildPositions bounds (#30) ----------------*/
{
    var zone = new OpenRgbClient.ZoneInfo("Keys", 2, 6, 3, 2, new uint[] { 0, 1, 0x80000000, 0xFFFFFFFF, 4, 0x7FFFFFFF });
    var info = new OpenRgbClient.DeviceInfo(0, 5, "Fake", "V", "", "", "", "loc", new[] { zone }, 6, new uint[6]);
    OpenRgbDevice? dev = null;
    bool threw = false;
    try { dev = new OpenRgbDevice(null!, info); } catch (Exception) { threw = true; }
    Check(!threw && dev != null, "OpenRgbDevice ctor survives out-of-range matrix cells (SetCustomMode NRE is caught)");
    if (dev != null)
    {
        Equal(6, dev.LedPositions!.Count, "positions sized to the LED count");
        Check(dev.LedPositions[0] == new LedPos(0, 0) && dev.LedPositions[1] == new LedPos(0.5f, 0) && dev.LedPositions[4] == new LedPos(0.5f, 1),
            "in-range matrix cells are positioned");
        Check(dev.LedPositions[2] == default && dev.LedPositions[3] == default && dev.LedPositions[5] == default,
            "sentinel / negative-as-uint cells are skipped");
        Equal(1.5f, dev.PreviewAspect, "aspect from the first matrix zone");
        Equal("Fake (OpenRGB)", dev.Name, "bridged name suffix");
        Equal(6, dev.Zones[0].Count, "zone count preserved");
    }
    var neg = new OpenRgbClient.DeviceInfo(1, 4, "Neg", "", "", "", "", "",
        new[] { new OpenRgbClient.ZoneInfo("Z", 1, -5, 0, 0, null), new OpenRgbClient.ZoneInfo("Y", 1, 3, 0, 0, null) }, 3, new uint[3]);
    threw = false;
    try { dev = new OpenRgbDevice(null!, neg); } catch (Exception) { threw = true; }
    Check(!threw && dev != null && dev.Zones[0].Count == 0 && dev.Zones[1].Offset == 0 && dev.Zones[1].Count == 3,
        "negative server LedCount clamps to 0 and does not poison the next zone's offset");
    Check(dev != null && dev.LedPositions!.Count == 3 && dev.LedPositions[2] == new LedPos(1f, 0.5f), "linear zone positions after a clamped zone");
}

/*---------------- AudioBars band bucketing at X = 1.0 (#110 #72) ----------------*/
{
    // No capture is required: hue depends only on X, so the checks below hold
    // whatever the analyzer's levels are.
    int n = 132;
    var pos = Line(n);   // includes exactly X = 1.0f
    var buf = new Rgb[n];
    bool threw = false;
    try { new AudioBars().Render(buf, pos, 1.0, 1.0, default); } catch (Exception) { threw = true; }
    Check(!threw, "AudioBars X=1.0 buckets to the last band, not one past it");
    Check(buf[n - 1].G == 0 && buf[n - 1].B == 0, "AudioBars rightmost LED is a pure-red hue whatever the level");
    Check(buf[0].R == 0, "AudioBars leftmost LED carries no red (hue 230)");
    Check(((IEffect)new AudioBars()).LiveInput && !((IEffect)new AudioBars()).Bakeable, "AudioBars: live input, not bakeable");
    Check(((IEffect)new AudioPulse()).LiveInput && !((IEffect)new AudioPulse()).Bakeable, "AudioPulse: live input, not bakeable");
}

/*---------------- Reactive effects: flags, empty render, speed sign (#73 #71 #70 #72) ----------------*/
{
    IEffect kf = new KeyFade(), kr = new KeyRipple();
    Check(!kf.Bakeable && kf.LiveInput, "KeyFade: not bakeable, live input");
    Check(!kr.Bakeable && kr.LiveInput, "KeyRipple: not bakeable, live input");
    var pat = new PatternEffect { Motion = PatternMotion.AudioPulse };
    Check(((IEffect)pat).LiveInput && !pat.Bakeable, "PatternEffect audio motion is live and not bakeable");
    pat.Motion = PatternMotion.AudioLevel;
    Check(((IEffect)pat).LiveInput, "PatternEffect AudioLevel is live");
    pat.Motion = PatternMotion.Rotate;
    Check(!((IEffect)pat).LiveInput && pat.Bakeable, "PatternEffect rotate is not live and bakeable");
    Check(!((IEffect)new RainbowWave()).LiveInput && ((IEffect)new RainbowWave()).Bakeable, "IEffect.LiveInput defaults to false");
    Check(!((IEffect)new TempGlow()).Bakeable && !((IEffect)new ChromaSync()).Bakeable, "sensor/feed effects stay non-bakeable");

    // Unmapped device, no key events: ripple is black, fade is the resting
    // glow, and the sign of speed changes nothing (Reverse encodes as -speed).
    int n = 4096;
    var pos = Line(n);
    var a = new Rgb[n]; var b = new Rgb[n];
    var red = new Rgb(255, 0, 0);
    kr.Render(a, pos, 1.0, 2.0, red);
    Check(a.All(c => c == default), "KeyRipple with no presses renders black");
    kr.Render(b, pos, 1.0, -2.0, red);
    Check(a.AsSpan().SequenceEqual(b), "KeyRipple frame is identical at speed +2 and -2");
    kf.Render(a, pos, 1.0, 2.0, red);
    Check(a.All(c => c == ColorUtil.Scale(red, 0.06)), "KeyFade with no presses renders the 6% resting glow");
    kf.Render(b, pos, 1.0, -2.0, red);
    Check(a.AsSpan().SequenceEqual(b), "KeyFade frame is identical at speed +2 and -2");
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < 1000; i++) kr.Render(a, pos, 1.0 + i * 0.001, 1.0, red);
    Check(sw.ElapsedMilliseconds < 2000, $"KeyRipple 1000 empty frames on 4096 LEDs stay cheap ({sw.ElapsedMilliseconds} ms)");
}

/*---------------- Step-clock wrap (#68) ----------------*/
{
    Equal(123456, Fx.Step(123456.7), "Fx.Step truncates");
    Equal(0, Fx.Step(Fx.StepWrap), "Fx.Step wraps to 0 at StepWrap");
    Equal(3, Fx.Step(Fx.StepWrap * 7 + 3.9), "Fx.Step wraps a multi-million value");
    Check(Fx.Step(2.2e9 * 1.8) >= 0 && Fx.Step(2.2e9 * 1.8) < 1_000_000, "Fx.Step stays in range past int.MaxValue");
    Check(Math.Abs(Fx.Frac((3.5e9 + 0.25) % Fx.StepWrap) - Fx.Frac(3.5e9 + 0.25)) < 1e-9, "Frac is unchanged by the integer wrap");
    // Every step-clock effect must still animate at t = 2.2e9 (an (int) cast of
    // the raw product saturated there and froze the pattern).
    var pos = Grid(8, 8);
    var red = new Rgb(255, 0, 0);
    foreach (IEffect fx in new IEffect[] { new Disco(), new Electric(), new Starfield(), new CandyBox(), new ColorfulMeteor(), new ColorCycle() })
    {
        var a = new Rgb[64]; var b = new Rgb[64];
        fx.Render(a, pos, 2.2e9, 1.0, red);
        fx.Render(b, pos, 2.2e9 + 1.0, 1.0, red);
        Check(!a.AsSpan().SequenceEqual(b), $"{fx.Name} still animates at t = 2.2e9");
    }
}

/*---------------- Baked loops close on their period (#67) ----------------*/
{
    var pos = Grid(8, 8);
    var bc = new Rgb(200, 90, 30);
    foreach (IEffect fx in new IEffect[] { new StackOutline(), new Waterfall(), new Orbit(), new TideFx(), new Police(), new Fire() })
        foreach (double speed in new[] { 1.0, 2.5 })
        {
            double loop = fx.LoopSeconds(speed);
            Check(loop >= 1.5 / speed - 1e-9 && loop <= 12.0 / speed + 1e-9, $"{fx.Name} LoopSeconds({speed}) = {loop:F3} inside the baker's clamp scaled by speed");
            var a = new Rgb[64]; var b = new Rgb[64];
            fx.Render(a, pos, 0.37, speed, bc);
            fx.Render(b, pos, 0.37 + loop, speed, bc);
            Check(SameWithin(a, b, 1), $"{fx.Name} frame at t0 equals frame at t0 + LoopSeconds({speed})");
        }
    // Baked at speed 1 these must land inside the 1.5..12 s clamp outright.
    foreach (IEffect fx in new IEffect[] { new StackOutline(), new Waterfall(), new Orbit(), new TideFx(), new Police(), new Fire() })
        Check(fx.LoopSeconds(1.0) >= 1.5 && fx.LoopSeconds(1.0) <= 12.0, $"{fx.Name} LoopSeconds(1) = {fx.LoopSeconds(1.0):F3} in 1.5..12");
}

/*---------------- PatternEffect gradient == PaletteFx.Sample (#143) ----------------*/
{
    var pal = new[] { new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255) };
    int n = 32;
    var pos = new LedPos[n];
    for (int i = 0; i < n; i++)
    {
        double a = 2 * Math.PI * i / n;
        pos[i] = new LedPos((float)(0.5 + 0.5 * Math.Cos(a)), (float)(0.5 + 0.5 * Math.Sin(a)));
    }
    foreach (double density in new[] { 1.0, 2.0 })
    {
        var pe = new PatternEffect { Color = PatternColor.Gradient, Motion = PatternMotion.Static, Palette = pal, Density = density };
        var buf = new Rgb[n];
        pe.Render(buf, pos, 3.3, 1.0, default);
        bool ok = true;
        for (int i = 0; i < n && ok; i++)
        {
            double u = Fx.Frac(Math.Atan2(pos[i].Y - 0.5, pos[i].X - 0.5) / (Math.PI * 2.0) + 1.0);
            ok = buf[i] == PaletteFx.Sample(pal, Fx.Frac(u * density));
        }
        Check(ok, $"static ring gradient (density {density}) samples PaletteFx.Sample at the ring coordinate");
    }
    Equal(new Rgb(255, 255, 255), PaletteFx.Sample(Array.Empty<Rgb>(), 0.3), "PaletteFx.Sample of an empty palette is white");
    Equal(pal[0], PaletteFx.Sample(pal, 1.0), "PaletteFx.Sample wraps u = 1 back to the first color");
}

/*---------------- EffectEngine: ChannelsFor / replace / stop (#75 #74 #123) ----------------*/
{
    var engine = new EffectEngine();
    var d1 = new FakeDevice { Name = "D1", LedCount = 4 };
    var d2 = new FakeDevice { Name = "D2", LedCount = 4 };
    var d3 = new FakeDevice { Name = "D3", LedCount = 4 };
    var fx = new CountingEffect();
    var red = new Rgb(255, 0, 0);
    var c1 = engine.Start(d1, 0, 2, new Rgb[4], fx, 1, red);
    var c2 = engine.Start(d2, 0, 4, new Rgb[4], fx, 1, red);
    var c3 = engine.Start(d1, 2, 2, new Rgb[4], fx, 1, red);
    var list = engine.ChannelsFor(d1);
    Check(list.Count == 2 && ReferenceEquals(list[0], c1) && ReferenceEquals(list[1], c3), "ChannelsFor returns the device's channels in insertion order");
    Equal(0, engine.ChannelsFor(d3).Count, "ChannelsFor on a device with no channels is empty");
    Check(ReferenceEquals(engine.FindExact(d1, 2, 2), c3) && engine.FindExact(d1, 0, 4) == null, "FindExact matches the exact range only");
    var c4 = engine.Start(d1, 1, 2, new Rgb[4], fx, 1, red);   // overlaps both d1 channels
    Check(!c1.IsRunning && !c3.IsRunning && c4.IsRunning, "Start stops every overlapping channel");
    Check(engine.ChannelsFor(d1).Count == 1 && ReferenceEquals(engine.ChannelsFor(d1)[0], c4), "replaced channels leave the list");
    Check(c2.IsRunning && engine.ChannelsFor(d2).Count == 1, "another device's channel is untouched");
    engine.StopAll();
    Check(!c2.IsRunning && !c4.IsRunning && engine.ChannelsFor(d1).Count == 0 && engine.ChannelsFor(d2).Count == 0, "StopAll clears every channel");

    // A worker whose device write outlasts the 300 ms join must never START a
    // write after StopRange returned (pre-write Running re-check).
    var slow = new FakeDevice { Name = "Slow", LedCount = 2, WriteDelayMs = 400 };
    engine.Start(slow, 0, 2, new Rgb[2], fx, 1, red);
    Check(WaitUntil(() => slow.WriteCount > 0, 2000), "slow device receives its first frame");
    Thread.Sleep(30);
    engine.StopRange(slow, 0, 2);
    long stopped = Stopwatch.GetTimestamp();
    Thread.Sleep(600);
    Check(slow.Writes.All(w => w.Start <= stopped), "no engine write starts after StopRange returned");
    Equal(0, engine.ChannelsFor(slow).Count, "StopRange removed the channel");
}

/*---------------- EffectEngine: InvalidateBase re-snapshots the live static frame (#69) ----------------*/
{
    var engine = new EffectEngine();
    var dev = new FakeDevice { Name = "Base", LedCount = 2 };
    var frame = new Rgb[2];
    var fx = new CountingEffect();
    var red = new Rgb(255, 0, 0); var blue = new Rgb(0, 0, 255);
    engine.Start(dev, 0, 1, frame, fx, 1, red);
    Check(WaitUntil(() => dev.WriteCount > 0, 2000), "first composed frame lands");
    var first = dev.Last;
    Check(first != null && first[0] == red && first[1] == default, "channel slice over the (black) static base");
    frame[1] = blue;                     // edit the LIVE static frame (a static pick on the sibling zone)
    engine.InvalidateBase(dev);
    Check(WaitUntil(() => dev.Last is { } l && l[1] == blue && l[0] == red, 2500), "InvalidateBase re-copies the base within the 1 s keepalive");
    engine.StopAll();
}

/*---------------- EffectEngine: LiveInput bypasses the idle throttle (#72) ----------------*/
{
    var engine = new EffectEngine();
    var live = new CountingEffect { Live = true };
    var idle = new CountingEffect { Live = false };
    var d1 = new FakeDevice { Name = "Live", LedCount = 2 };
    var d2 = new FakeDevice { Name = "Idle", LedCount = 2 };
    engine.Start(d1, 0, 2, new Rgb[2], live, 1, new Rgb(1, 2, 3));
    engine.Start(d2, 0, 2, new Rgb[2], idle, 1, new Rgb(1, 2, 3));
    Thread.Sleep(2000);
    engine.StopAll();
    int l = live.Renders, i = idle.Renders;
    Check(l >= 55, $"LiveInput effect keeps rendering at full rate while static ({l} renders in 2 s)");
    Check(i <= 55, $"non-live static effect drops to the 10 fps check loop ({i} renders in 2 s)");
    Check(l > i, $"live renders ({l}) exceed throttled renders ({i})");
}

/*---------------- ChromaFeed keyboard / ChromaLink slots + Touch (#54) ----------------*/
{
    var kb = new[] { new Rgb(7, 7, 7) };
    var cl = new[] { new Rgb(10, 20, 30), new Rgb(11, 21, 31), new Rgb(12, 22, 32), new Rgb(13, 23, 33), new Rgb(14, 24, 34) };
    ChromaFeed.PushGrid(kb, 1, 1);
    ChromaFeed.PushGrid(cl, 1, 5, type: 2);
    Equal(kb[0], ChromaFeed.Sample(0.05f, 0.5f), "a fresh keyboard grid wins over a ChromaLink push");
    Thread.Sleep(1100);                                  // keyboard stamp goes stale
    ChromaFeed.PushGrid(cl, 1, 5, type: 2);
    Equal(cl[0], ChromaFeed.Sample(0.05f, 0.5f), "stale keyboard yields to the ChromaLink grid (left)");
    Equal(cl[4], ChromaFeed.Sample(0.95f, 0.5f), "ChromaLink 1x5 samples across X (right)");
    Equal(cl[2], ChromaFeed.Sample(0.5f, 0.9f), "ChromaLink 1x5 ignores Y");
    ChromaFeed.Touch();
    Check(ChromaFeed.Active && ChromaFeed.Sample(0.05f, 0.5f) == cl[0], "Touch keeps the feed active without touching a grid");
    ChromaFeed.PushGrid(new[] { new Rgb(9, 9, 9) }, 1, 1);
    Equal(new Rgb(9, 9, 9), ChromaFeed.Sample(0.05f, 0.5f), "a new keyboard frame takes over again");
    ChromaFeed.PushGrid(new[] { new Rgb(1, 1, 1) }, 2, 3, type: 2);   // undersized: rejected
    Equal(new Rgb(9, 9, 9), ChromaFeed.Sample(0.05f, 0.5f), "undersized ChromaLink grid is rejected");
}

/*---------------- LogBudget + REST title sanitising (#135) ----------------*/
{
    var b = new LogBudget(5);
    int allowed = 0;
    for (int i = 0; i < 10; i++) if (b.Allow()) allowed++;
    Equal(5, allowed, "LogBudget(5) allows exactly 5 in a minute");
    Check(!b.Allow(), "LogBudget refuses after the budget");
    Check(new LogBudget(1).Allow(), "a fresh budget allows its first line");

    byte[] body = Encoding.UTF8.GetBytes("{\"title\":\"a\\n09-02 12:00:00 ERR forged\\ttab\\r\"}");
    string t = ChromaRestServer.AppTitle(body, body.Length);
    Check(!t.Contains('\n') && !t.Contains('\r') && !t.Contains('\t') && t.StartsWith("a 09-02"), $"AppTitle strips control characters ('{t}')");
    byte[] longBody = Encoding.UTF8.GetBytes("{\"title\":\"" + new string('x', 200) + "\"}");
    Equal(64, ChromaRestServer.AppTitle(longBody, longBody.Length).Length, "AppTitle caps at 64 chars");
    Equal("?", ChromaRestServer.AppTitle(null, 0), "AppTitle null body -> ?");
    Equal("?", ChromaRestServer.AppTitle(Encoding.UTF8.GetBytes("{garbage"), 8), "AppTitle malformed json -> ?");
    Equal("?", ChromaRestServer.AppTitle(Encoding.UTF8.GetBytes("{\"title\":\"\"}"), 12), "AppTitle empty title -> ?");
    Equal("?", ChromaRestServer.AppTitle(Encoding.UTF8.GetBytes("{\"other\":1}"), 11), "AppTitle missing title -> ?");
    byte[] padded = Encoding.UTF8.GetBytes("{\"title\":\"ok\"}XXXXXXXX");
    Equal("ok", ChromaRestServer.AppTitle(padded, 14), "AppTitle parses only the first len bytes");
}

/*---------------- UpdateClient.SizeOf tolerant parse (#23) ----------------*/
{
    long S(string json) { using var d = JsonDocument.Parse(json); return UpdateClient.SizeOf(d.RootElement); }
    Equal(123L, S("{\"size\":123}"), "SizeOf integral number");
    Equal(5_000_000_000L, S("{\"size\":5000000000}"), "SizeOf 64-bit number");
    Equal(0L, S("{\"size\":\"123\"}"), "SizeOf string -> 0");
    Equal(0L, S("{\"size\":null}"), "SizeOf null -> 0");
    Equal(0L, S("{\"size\":1.5}"), "SizeOf non-integral -> 0");
    Equal(0L, S("{}"), "SizeOf missing -> 0");
    Equal(0L, S("{\"size\":true}"), "SizeOf bool -> 0");
}

/*---------------- GigabyteIt5711.NormalizeOrder (#31) ----------------*/
{
    Equal("RGB", GigabyteIt5711.NormalizeOrder(" rgb ", 1), "NormalizeOrder trims and upper-cases");
    Equal("BGR", GigabyteIt5711.NormalizeOrder("Bgr", 2), "NormalizeOrder mixed case");
    Equal("GBR", GigabyteIt5711.NormalizeOrder("GBR", 3), "NormalizeOrder known order passes through");
    Equal("GRB", GigabyteIt5711.NormalizeOrder(null, 4), "NormalizeOrder null -> GRB");
    Equal("GRB", GigabyteIt5711.NormalizeOrder("", 1), "NormalizeOrder empty -> GRB");
    Equal("GRB", GigabyteIt5711.NormalizeOrder("xyz", 1), "NormalizeOrder unknown -> GRB (warned)");
    Equal("GRB", GigabyteIt5711.NormalizeOrder("RGBW", 1), "NormalizeOrder four-channel string -> GRB");
}

/*---------------- MemoryTrimmer handle hygiene (#22) ----------------*/
{
    using var me = Process.GetCurrentProcess();
    me.Refresh();
    int before = me.HandleCount;
    for (int i = 0; i < 10; i++) MemoryTrimmer.Trim();
    me.Refresh();
    int after = me.HandleCount;
    Check(after - before < 10, $"Trim x10 leaks no process handles ({before} -> {after})");
    try
    {
        string? lastMem = null;
        using (var fs = new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var rd = new StreamReader(fs))
            for (string? line; (line = rd.ReadLine()) != null;)
                if (line.Contains("[memory]")) lastMem = line;
        Check(lastMem != null && lastMem.Contains("working set trimmed"), $"last [memory] log line reports success ({lastMem})");
    }
    catch (Exception ex) { Console.WriteLine($"  (skip) log tail unreadable: {ex.Message}"); }
}

/*---------------- DiagnosticReport.Ps stdout/stderr merge (#24) ----------------*/
{
    Equal("ok", DiagnosticReport.Ps("'ok'"), "Ps plain output");
    string r = DiagnosticReport.Ps("'ok'; Write-Error 'boom'");
    Check(r.StartsWith("ok") && r.Contains("(errors:") && r.Contains("boom"), $"Ps keeps partial stdout and appends stderr ({r.Replace("\r\n", " / ")})");
    Check(!r.Contains("(errors: \r") && !r.Contains("(errors: \n"), "Ps collapses stderr line breaks");
}

/*---------------- OpenRgbDetectorConfig (#145) ----------------*/
{
    string dir = TempDir();
    try
    {
        string file = Path.Combine(dir, "OpenRGB.json");
        File.WriteAllText(file, "{\"Detectors\":{\"detectors\":{\"A\":true,\"B\":false,\"C\":true}}}");
        Check(OpenRgbDetectorConfig.Enabled(dir).SequenceEqual(new[] { "A", "C" }), "Enabled lists the true detectors");
        Check(OpenRgbDetectorConfig.Edit(dir, d => OpenRgbDetectorConfig.Set(d, new[] { "A" }, false)), "Edit writes when Set changed a value");
        Check(OpenRgbDetectorConfig.Enabled(dir).SequenceEqual(new[] { "C" }), "the edit landed on disk");
        Check(!OpenRgbDetectorConfig.Edit(dir, d => OpenRgbDetectorConfig.Set(d, new[] { "A" }, false)), "Edit is a no-op when nothing changes");
        Check(!File.Exists(file + ".tmp"), "detector edit leaves no .tmp");
        Check(OpenRgbDetectorConfig.Edit(dir, d => OpenRgbDetectorConfig.Set(d, new[] { "A", "Z" }, true)), "Set adds an unknown name");
        Check(OpenRgbDetectorConfig.Enabled(dir).SequenceEqual(new[] { "A", "C", "Z" }), "new detector entries persist");
        Check(File.ReadAllText(file).Contains("\"B\": false"), "untouched entries survive the rewrite (indented)");

        string missing = Path.Combine(dir, "fresh");
        Directory.CreateDirectory(missing);
        Check(!OpenRgbDetectorConfig.Edit(missing, d => OpenRgbDetectorConfig.Set(d, new[] { "X" }, false)) && !File.Exists(Path.Combine(missing, "OpenRGB.json")),
            "Edit on a missing file without createIfMissing writes nothing");
        Check(OpenRgbDetectorConfig.Edit(missing, d => OpenRgbDetectorConfig.Set(d, new[] { "X" }, false), createIfMissing: true)
              && File.Exists(Path.Combine(missing, "OpenRGB.json")), "createIfMissing builds the skeleton");
        Check(OpenRgbDetectorConfig.Enabled(missing).Count == 0 && File.ReadAllText(Path.Combine(missing, "OpenRGB.json")).Contains("\"X\": false"),
            "skeleton carries the disabled entry");
        Equal(0, OpenRgbDetectorConfig.Enabled(Path.Combine(dir, "nowhere")).Count, "Enabled on a missing file is empty");

        File.WriteAllText(file, "{\"Detectors\":{}}");
        Check(OpenRgbDetectorConfig.Enabled(dir).Count == 0, "Enabled on a missing section is empty");
        Check(!OpenRgbDetectorConfig.Edit(dir, d => true), "Edit on a missing section without createIfMissing is a no-op");
        File.WriteAllText(file, "{not json");
        Check(OpenRgbDetectorConfig.Enabled(dir).Count == 0, "Enabled on malformed json is empty, not a throw");
        bool threw = false;
        try { OpenRgbDetectorConfig.Edit(dir, d => true); } catch (Exception) { threw = true; }
        Check(threw, "Edit on malformed json throws (callers own the log line)");
    }
    finally { try { Directory.Delete(dir, recursive: true); } catch { } }
}

/*---------------- ColorUtil HSV round-trip lattice (#142) ----------------*/
{
    bool ok = true;
    int worst = 0;
    for (int r = 0; r < 256 && ok; r += 15)
        for (int g = 0; g < 256 && ok; g += 15)
            for (int b = 0; b < 256 && ok; b += 15)
            {
                var c = new Rgb((byte)r, (byte)g, (byte)b);
                var (h, s, v) = ColorUtil.RgbToHsv(c);
                var back = ColorUtil.HsvToRgb(h, s, v);
                int d = Math.Max(Math.Abs(back.R - c.R), Math.Max(Math.Abs(back.G - c.G), Math.Abs(back.B - c.B)));
                worst = Math.Max(worst, d);
                if (d > 1) ok = false;
            }
    Check(ok, $"RgbToHsv/HsvToRgb round-trips within 1 per channel on the 18^3 lattice (worst {worst})");
    Check(ColorUtil.RgbToHsv(new Rgb(0, 0, 0)) == (0, 0, 0), "black -> (0,0,0)");
    Check(ColorUtil.RgbToHsv(new Rgb(0, 0, 255)).H == 240, "blue hue is 240");
}

/*---------------- Self-update swap script: redirect-before-echo (#163) ----------------*/
{
    string dir = TempDir();
    try
    {
        // `>"file" echo ok %n%` is the form the swap .bat must use: with a
        // single-digit n the old `echo ok %n%>"file"` expands to
        // `echo ok 2>"file"` - a STDERR redirect - and the result file stays
        // empty (cmd only reads a lone digit before `>` as a handle).
        File.WriteAllText(Path.Combine(dir, "r.bat"),
            "@echo off\r\nset n=12\r\n>\"%~dp0r.txt\" echo ok %n%\r\nset n=2\r\n>\"%~dp0r3.txt\" echo ok %n%\r\necho ok %n%>\"%~dp0r2.txt\"\r\n");
        var psi = new ProcessStartInfo("cmd.exe", $"/c \"{Path.Combine(dir, "r.bat")}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        Check(p.WaitForExit(10000), "swap-script probe finishes");
        Equal("ok 12", File.ReadAllText(Path.Combine(dir, "r.txt")).Trim(), "redirect-first form writes a two-digit result");
        Equal("ok 2", File.ReadAllText(Path.Combine(dir, "r3.txt")).Trim(), "redirect-first form writes a single-digit result");
        Check(File.Exists(Path.Combine(dir, "r2.txt")) && File.ReadAllText(Path.Combine(dir, "r2.txt")).Trim().Length == 0,
            "control: echo-first form loses a single-digit result to a stderr redirect");
    }
    finally { try { Directory.Delete(dir, recursive: true); } catch { } }
}

/*---------------- Repo-file invariants: app.manifest + chroma shims (#100 #8 #13) ----------------*/
{
    string? repo = null;
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && repo == null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "src", "UnifiedRgb.App", "app.manifest"))) repo = d.FullName;
    if (repo == null) Console.WriteLine("  (skip) repo root not found from the test binary");
    else
    {
        // #100: SupportService's elevation relaunch was deleted because the app
        // always runs elevated - the manifest must keep saying so.
        var man = XDocument.Load(Path.Combine(repo, "src", "UnifiedRgb.App", "app.manifest"));
        var level = man.Descendants().FirstOrDefault(e => e.Name.LocalName == "requestedExecutionLevel")?.Attribute("level")?.Value;
        Equal("requireAdministrator", level, "app.manifest requests administrator (IsAdmin() is always true in-app)");

        string shim32 = Path.Combine(repo, "native", "chroma-shim", "RzChromaSDK.dll");
        string shim64 = Path.Combine(repo, "native", "chroma-shim", "RzChromaSDK64.dll");
        static bool HasWide(byte[] bytes, string s)
            => Encoding.Unicode.GetString(bytes).Contains(s) || Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1).Contains(s);
        if (File.Exists(shim32))
        {
            var b = File.ReadAllBytes(shim32);
            Check(HasWide(b, "RzChromaSDK_real.dll") && !HasWide(b, "RzChromaSDK64_real.dll"), "32-bit shim loads only the 32-bit backup name");
            var vi = FileVersionInfo.GetVersionInfo(shim32);
            Equal("RzChromaSDK.dll", vi.OriginalFilename, "32-bit shim OriginalFilename");
            Equal("UnifiedRGB Chroma Shim", vi.ProductName, "32-bit shim ProductName (IsOurs pin)");
        }
        if (File.Exists(shim64))
        {
            var b = File.ReadAllBytes(shim64);
            Check(HasWide(b, "RzChromaSDK64_real.dll") && !HasWide(b, "RzChromaSDK_real.dll"), "64-bit shim loads only the 64-bit backup name");
            var vi = FileVersionInfo.GetVersionInfo(shim64);
            Equal("RzChromaSDK64.dll", vi.OriginalFilename, "64-bit shim OriginalFilename");
            Equal("UnifiedRGB Chroma Shim", vi.ProductName, "64-bit shim ProductName (IsOurs pin)");
        }
    }
}

/*---------------- SensorRule persisted shape (#f1) ----------------*/
{
    // The rules live in settings.json, so the field names are a contract with
    // files already on disk. An older build strips what it does not know, so a
    // rule must also survive being read back with fields missing.
    var r = new SensorRule { Source = "Board:Fan #2", Above = false, Threshold = 42.5, ClearMargin = 2, HoldSeconds = 7, Profile = "Cool", Enabled = false };
    string json = JsonSerializer.Serialize(r);
    foreach (var field in new[] { "Source", "Above", "Threshold", "ClearMargin", "HoldSeconds", "Profile", "Enabled" })
        Check(json.Contains($"\"{field}\""), $"SensorRule json carries {field}");

    var back = JsonSerializer.Deserialize<SensorRule>(json)!;
    Equal(r.Source, back.Source, "SensorRule round-trip: source");
    Equal(r.Above, back.Above, "SensorRule round-trip: direction");
    Equal(r.Threshold, back.Threshold, "SensorRule round-trip: threshold");
    Equal(r.HoldSeconds, back.HoldSeconds, "SensorRule round-trip: hold");
    Equal(r.Enabled, back.Enabled, "SensorRule round-trip: enabled");

    // Defaults must be sane for a rule an older build wrote back stripped.
    var bare = JsonSerializer.Deserialize<SensorRule>("{\"Profile\":\"X\"}")!;
    Equal(SensorSources.CpuTemp, bare.Source, "SensorRule default source");
    Check(bare.Above, "SensorRule defaults to above");
    Check(bare.Enabled, "SensorRule defaults to enabled");
    Check(bare.ClearMargin > 0 && bare.HoldSeconds > 0, "SensorRule defaults cannot chatter");
}

/*---------------- Sensor rules: hysteresis + hold (#f1) ----------------*/
{
    // Rule: fire at or above 85, release below 82, both after a 5 s hold.
    var rule = new SensorRule { Source = SensorSources.CpuTemp, Above = true, Threshold = 85, ClearMargin = 3, HoldSeconds = 5, Profile = "Alert" };
    var st = default(SensorRuleState);

    // Below the line: nothing pending, nothing active.
    st = SensorRuleEvaluator.Step(rule, 70, st, 0);
    Check(!st.Active && st.SinceSeconds == null, "sensor: cold value is inactive");

    // Over the line, but the hold has not elapsed.
    st = SensorRuleEvaluator.Step(rule, 86, st, 10);
    Check(!st.Active && st.SinceSeconds == 10, "sensor: hold starts, not yet active");
    st = SensorRuleEvaluator.Step(rule, 86, st, 14);
    Check(!st.Active, "sensor: still holding at 4 s");
    st = SensorRuleEvaluator.Step(rule, 86, st, 15);
    Check(st.Active && st.SinceSeconds == null, "sensor: fires once the hold elapses");

    // Inside the hysteresis band: stays on.
    st = SensorRuleEvaluator.Step(rule, 84, st, 20);
    Check(st.Active, "sensor: 84 is inside the band, stays on");
    st = SensorRuleEvaluator.Step(rule, 83, st, 25);
    Check(st.Active, "sensor: 83 is inside the band, stays on");

    // Past the margin, held, then released.
    st = SensorRuleEvaluator.Step(rule, 81, st, 30);
    Check(st.Active && st.SinceSeconds == 30, "sensor: release hold starts");
    st = SensorRuleEvaluator.Step(rule, 81, st, 35);
    Check(!st.Active, "sensor: releases after the hold");

    // A null reading (no PawnIO) is always inactive and forgets the hold.
    var pending = new SensorRuleState(false, 100);
    Check(!SensorRuleEvaluator.Step(rule, null, pending, 101).Active, "sensor: null reading is inactive");
    Check(SensorRuleEvaluator.Step(rule, null, pending, 101).SinceSeconds == null, "sensor: null reading clears the hold");
}

/*---------------- Sensor rules: the flapping case (#f1) ----------------*/
{
    // The acceptance case: oscillating 84/86 either side of an 85 rule must
    // never toggle, because the value never clears the margin.
    var rule = new SensorRule { Threshold = 85, ClearMargin = 3, HoldSeconds = 5, Profile = "Alert" };
    var st = default(SensorRuleState);
    bool everActive = false;
    for (int i = 0; i < 200; i++)
    {
        st = SensorRuleEvaluator.Step(rule, i % 2 == 0 ? 86 : 84, st, i * 2.0);
        everActive |= st.Active;
    }
    Check(!everActive, "sensor: 84/86 flapping never fires (hold resets)");

    // Once genuinely hot it fires, and then the same flapping cannot drop it.
    var st2 = default(SensorRuleState);
    for (int i = 0; i < 5; i++) st2 = SensorRuleEvaluator.Step(rule, 90, st2, i * 2.0);
    Check(st2.Active, "sensor: sustained heat fires");
    bool everCleared = false;
    for (int i = 0; i < 200; i++)
    {
        st2 = SensorRuleEvaluator.Step(rule, i % 2 == 0 ? 86 : 84, st2, 100 + i * 2.0);
        everCleared |= !st2.Active;
    }
    Check(!everCleared, "sensor: 84/86 flapping never releases (inside the band)");
}

/*---------------- Sensor rules: below-threshold direction (#f1) ----------------*/
{
    // "At or below 15%", e.g. a battery rule. Releases above 15 + margin.
    var rule = new SensorRule { Source = "Battery:Mouse", Above = false, Threshold = 15, ClearMargin = 5, HoldSeconds = 0, Profile = "Low" };
    var st = default(SensorRuleState);
    st = SensorRuleEvaluator.Step(rule, 15, st, 0);
    Check(st.Active, "sensor: below-rule fires at the threshold");
    st = SensorRuleEvaluator.Step(rule, 18, st, 1);
    Check(st.Active, "sensor: below-rule holds inside the band");
    st = SensorRuleEvaluator.Step(rule, 21, st, 2);
    Check(!st.Active, "sensor: below-rule releases past the margin");
}

/*---------------- Sensor rules: FirstActive picks by list order (#f1) ----------------*/
{
    var rules = new List<SensorRule>
    {
        new() { Source = SensorSources.CpuTemp, Threshold = 80, Profile = "First", Enabled = false },
        new() { Source = SensorSources.GpuTemp, Threshold = 80, Profile = "Second" },
        new() { Source = SensorSources.Hottest, Threshold = 80, Profile = "Third" },
        new() { Source = SensorSources.CpuTemp, Threshold = 80, Profile = "Gone" },
    };
    var states = new[]
    {
        new SensorRuleState(true, null), new SensorRuleState(true, null),
        new SensorRuleState(true, null), new SensorRuleState(true, null),
    };
    var values = new double?[] { 90, 91, 92, 93 };

    var hit = SensorRuleEvaluator.FirstActive(rules, states, values);
    Equal("Second", hit?.Profile, "sensor: disabled rule is skipped, next wins");

    // A rule pointing at a deleted profile is skipped, not applied blank.
    var only = new List<SensorRule> { rules[3] };
    var hit2 = SensorRuleEvaluator.FirstActive(only, new[] { new SensorRuleState(true, null) },
        new double?[] { 93 }, name => name != "Gone");
    Check(hit2 == null, "sensor: rule with a deleted profile is skipped");

    // Nothing active at all.
    Check(SensorRuleEvaluator.FirstActive(rules, new SensorRuleState[4], values) == null,
        "sensor: no active rule yields no hit");
    Check(SensorRuleEvaluator.FirstActive(null, states, values) == null, "sensor: null rule list is safe");
}

/*---------------- Sensor sources: labels, units, poll gating (#f1) ----------------*/
{
    Equal("CPU temp", SensorSources.Label(SensorSources.CpuTemp), "source label: cpu temp");
    Equal("°C", SensorSources.Unit(SensorSources.CpuTemp), "source unit: cpu temp");
    Equal("%", SensorSources.Unit(SensorSources.GpuLoad), "source unit: gpu load");
    Equal("MB Temp #3", SensorSources.Label(SensorSources.BoardPrefix + "Temperature #3"), "source label: board temp is short and marked MB");
    Equal("CPU Fan", SensorSources.Label(SensorSources.FanPrefix + "CPU Fan"), "source label: fan name stands alone");

    // Only the sources that need the expensive sweep should ask for it.
    Check(!SensorSources.NeedsFullSweep(SensorSources.CpuTemp), "gating: cpu temp uses the cheap touch");
    Check(!SensorSources.NeedsFullSweep(SensorSources.Hottest), "gating: hottest uses the cheap touch");
    Check(SensorSources.NeedsFullSweep(SensorSources.GpuLoad), "gating: gpu load needs the full sweep");
    Check(SensorSources.NeedsFullSweep(SensorSources.FanPrefix + "CPU Fan"), "gating: fan rpm needs the full sweep");

    Equal("MB Temp #1 (IT87952E)", SensorSources.Label(SensorSources.BoardPrefix + "Temperature #1 (IT87952E)"),
        "source label: second-chip sensor keeps its qualifier");

    var hit = new SensorHit(SensorSources.CpuTemp, "Alert", 87.4, 85, true);
    Equal("CPU temp 87°C at or above 85°C", hit.Describe(), "sensor hit describes itself");
}

/*---------------- App rule matching (#f1) ----------------*/
{
    var rules = new List<AutomationRule>
    {
        new() { Process = "", Profile = "Blank" },
        new() { Process = "cs2.exe", Profile = "Game" },
        new() { Process = "chrome", Profile = "Browse" },
    };
    Equal("Game", AutomationRule.Match(rules, "cs2"), "app match: .exe suffix tolerated");
    Equal("Browse", AutomationRule.Match(rules, "chrome"), "app match: plain name");
    Equal("Browse", AutomationRule.Match(rules, "CHROME"), "app match: case insensitive");
    Check(AutomationRule.Match(rules, "notepad") == null, "app match: no rule");
    Check(AutomationRule.Match(rules, null) == null, "app match: null process");
    Check(AutomationRule.Match(null, "cs2") == null, "app match: null rules");
    // A half-filled rule must never swallow every process.
    Check(AutomationRule.Match(new List<AutomationRule> { new() { Process = "", Profile = "X" } }, "anything") == null,
        "app match: blank rule matches nothing");
}

/*---------------- Schedules: window math (#f2) ----------------*/
{
    // Monday is bit 0. A window that ends before it starts runs overnight and
    // belongs to the day it STARTED.
    ScheduleRule Night(int days = 0x7F) => new() { Start = "23:00", End = "07:00", Days = days };
    ScheduleRule Evening(int days = 0x1F) => new() { Start = "18:00", End = "20:00", Days = days, Action = ScheduleAction.Profile, Profile = "Evening" };

    var monday = new DateTime(2026, 9, 7);      // a Monday
    var tuesday = new DateTime(2026, 9, 8);
    var saturday = new DateTime(2026, 9, 12);

    Equal(0, ScheduleRule.BitOf(DayOfWeek.Monday), "days: Monday is bit 0");
    Equal(6, ScheduleRule.BitOf(DayOfWeek.Sunday), "days: Sunday is bit 6");
    Equal(5, ScheduleRule.BitOf(DayOfWeek.Saturday), "days: Saturday is bit 5");

    // Same-day window.
    Check(ScheduleRule.InWindow(Evening(), monday.AddHours(19)), "window: inside a weekday evening");
    Check(!ScheduleRule.InWindow(Evening(), monday.AddHours(17)), "window: before it opens");
    Check(!ScheduleRule.InWindow(Evening(), monday.AddHours(20)), "window: the end is exclusive");
    Check(!ScheduleRule.InWindow(Evening(), saturday.AddHours(19)), "window: not on a day it does not run");

    // Overnight window, the acceptance case.
    Check(ScheduleRule.InWindow(Night(), monday.AddHours(23.5)), "overnight: open on the evening it starts");
    Check(ScheduleRule.InWindow(Night(), tuesday.AddHours(1)), "overnight: still open after midnight");
    Check(!ScheduleRule.InWindow(Night(), tuesday.AddHours(8)), "overnight: closed after the end time");

    // 01:00 Tuesday belongs to MONDAY's window, so only Monday's bit matters.
    int mondayOnly = 1 << 0, tuesdayOnly = 1 << 1;
    Check(ScheduleRule.InWindow(Night(mondayOnly), tuesday.AddHours(1)), "overnight: 01:00 Tue runs on Monday's bit");
    Check(!ScheduleRule.InWindow(Night(tuesdayOnly), tuesday.AddHours(1)), "overnight: Tuesday's bit does not cover 01:00 Tue");
    Check(ScheduleRule.InWindow(Night(tuesdayOnly), tuesday.AddHours(23.5)), "overnight: Tuesday's bit covers Tue evening");

    // Degenerate and disabled rules never open.
    Check(!ScheduleRule.InWindow(new ScheduleRule { Start = "12:00", End = "12:00" }, monday.AddHours(12)), "window: zero length never opens");
    Check(!ScheduleRule.InWindow(new ScheduleRule { Start = "bad", End = "07:00" }, monday.AddHours(1)), "window: unparseable time never opens");
    Check(!ScheduleRule.InWindow(Night(0), monday.AddHours(23.5)), "window: no days selected never opens");

    // IsActive folds in Enabled and the idle wait.
    var idle = Night(); idle.IdleOnly = true;
    Check(!ScheduleRule.IsActive(idle, monday.AddHours(23.5), 60, 600), "active: idle-only waits while you are here");
    Check(ScheduleRule.IsActive(idle, monday.AddHours(23.5), 700, 600), "active: idle-only fires once away");
    var offRule = Night(); offRule.Enabled = false;
    Check(!ScheduleRule.IsActive(offRule, monday.AddHours(23.5), 0, 600), "active: a disabled rule never fires");
}

/*---------------- Schedules: next change and description (#f2) ----------------*/
{
    var monday = new DateTime(2026, 9, 7, 12, 0, 0);
    var evening = new ScheduleRule { Start = "18:00", End = "20:00", Days = 0x1F, Action = ScheduleAction.Profile, Profile = "Evening" };
    var night = new ScheduleRule { Start = "23:00", End = "07:00", Days = 0x7F };

    var next = ScheduleRule.NextChange(new[] { night, evening }, monday);
    Check(next != null && next.Value.When == monday.Date.AddHours(18), "next: the sooner of two windows wins");
    Check(next != null && ReferenceEquals(next.Value.Rule, evening), "next: reports which rule it is");

    // Past today's start, it rolls to the next day the rule runs.
    var late = new DateTime(2026, 9, 11, 21, 0, 0);      // Friday evening, after 18:00
    var afterFri = ScheduleRule.NextChange(new[] { evening }, late);
    Equal(new DateTime(2026, 9, 14, 18, 0, 0), afterFri?.When ?? default, "next: weekday rule skips the weekend");

    Check(ScheduleRule.NextChange(Array.Empty<ScheduleRule>(), monday) == null, "next: nothing scheduled");
    var disabled = new ScheduleRule { Enabled = false, Start = "18:00", End = "20:00" };
    Check(ScheduleRule.NextChange(new[] { disabled }, monday) == null, "next: disabled rules are ignored");

    Equal("Every day", ScheduleRule.DaysText(0x7F), "days text: every day");
    Equal("Weekdays", ScheduleRule.DaysText(0x1F), "days text: weekdays");
    Equal("Weekends", ScheduleRule.DaysText(0x60), "days text: weekends");
    Equal("Mon Wed Fri", ScheduleRule.DaysText(0b0010101), "days text: a custom set");
    Equal("Every day 23:00 to 07:00, lights off", ScheduleRule.Describe(night), "describe: a lights-off window");
    Equal("Weekdays 18:00 to 20:00, apply Evening", ScheduleRule.Describe(evening), "describe: a profile window");
}

/*---------------- Schedules: day bits round-trip (#f2) ----------------*/
{
    // The editor toggles seven check boxes; the file stores one int.
    var r = new ScheduleRule { Days = 0 };
    r.Mon = true; r.Wed = true; r.Sun = true;
    Equal(0b1000101, r.Days, "day bits: setting flags builds the mask");
    Check(r.Mon && r.Wed && r.Sun && !r.Tue && !r.Sat, "day bits: reading flags back");
    r.Mon = false;
    Equal(0b1000100, r.Days, "day bits: clearing a flag");

    // Days is what persists; the flags are view sugar and must not be written.
    string json = JsonSerializer.Serialize(new ScheduleRule { Days = 0x1F, Start = "18:00", End = "20:00", Action = ScheduleAction.Profile, Profile = "Evening", IdleOnly = true });
    foreach (var f in new[] { "Days", "Start", "End", "Action", "Profile", "IdleOnly", "Enabled" })
        Check(json.Contains($"\"{f}\""), $"ScheduleRule json carries {f}");
    foreach (var f in new[] { "Mon", "Tue", "IsProfileAction" })
        Check(!json.Contains($"\"{f}\""), $"ScheduleRule json omits the view-only {f}");

    var back = JsonSerializer.Deserialize<ScheduleRule>(json)!;
    Equal(0x1F, back.Days, "ScheduleRule round-trip: days");
    Equal(ScheduleAction.Profile, back.Action, "ScheduleRule round-trip: action");
    Equal("Evening", back.Profile, "ScheduleRule round-trip: profile");
    Check(back.IdleOnly, "ScheduleRule round-trip: idle only");

    var bare = JsonSerializer.Deserialize<ScheduleRule>("{}")!;
    Check(bare.Enabled && bare.Days == 0x7F, "ScheduleRule defaults: enabled, every day");
    Equal(ScheduleAction.LightsOff, bare.Action, "ScheduleRule defaults to lights off");
}

/*---------------- Automation precedence (#f1) ----------------*/
{
    var appRules = new List<AutomationRule> { new() { Process = "cs2", Profile = "Game" } };
    var hit = new SensorHit(SensorSources.CpuTemp, "Alert", 90, 85, true);

    AutomationInputs Make(bool locked = false, bool off = false, bool sensor = false,
                          bool app = false, bool schedProfile = false, bool lockOff = true) => new()
    {
        Locked = locked,
        LockLightsOff = lockOff,
        ScheduleOff = off ? new ScheduleHit("07:00", null) : null,
        ScheduleProfile = schedProfile ? new ScheduleHit("22:00", "Evening") : null,
        SchedulePaused = false,
        ScheduleWaitingIdle = false,
        ScheduleEnd = "07:00",
        AppSwitchEnabled = true,
        ForegroundProcess = app ? "cs2" : "notepad",
        ForegroundIsSelf = false,
        AppRules = appRules,
        Sensor = sensor ? hit : null,
        SensorUnavailable = null,
    };

    Equal(AutomationMode.Base, AutomationDecision.Resolve(Make()).Mode, "precedence: nothing yields Base");
    Equal(AutomationMode.App, AutomationDecision.Resolve(Make(app: true)).Mode, "precedence: app rule");
    Equal(AutomationMode.ScheduleProfile, AutomationDecision.Resolve(Make(schedProfile: true)).Mode, "precedence: scheduled profile");
    Equal(AutomationMode.ScheduleProfile, AutomationDecision.Resolve(Make(schedProfile: true, app: true)).Mode, "precedence: scheduled profile beats app");
    Equal(AutomationMode.Sensor, AutomationDecision.Resolve(Make(sensor: true)).Mode, "precedence: sensor alone");
    Equal(AutomationMode.Sensor, AutomationDecision.Resolve(Make(sensor: true, app: true)).Mode, "precedence: sensor beats app");
    Equal(AutomationMode.Sensor, AutomationDecision.Resolve(Make(sensor: true, schedProfile: true)).Mode, "precedence: sensor beats a scheduled profile");
    Equal(AutomationMode.ScheduleOff, AutomationDecision.Resolve(Make(off: true, sensor: true, app: true)).Mode, "precedence: scheduled dark beats sensor");
    Equal(AutomationMode.Locked, AutomationDecision.Resolve(Make(locked: true, off: true, sensor: true, app: true)).Mode, "precedence: locked beats all");

    // The winning profile travels with the mode.
    Equal("Alert", AutomationDecision.Resolve(Make(sensor: true, app: true)).Profile, "precedence: sensor profile wins");
    Equal("Game", AutomationDecision.Resolve(Make(app: true)).Profile, "precedence: app profile applies");
    Equal("Evening", AutomationDecision.Resolve(Make(schedProfile: true, app: true)).Profile, "precedence: scheduled profile applies");
    Check(AutomationDecision.Resolve(Make(off: true)).Profile == null, "precedence: scheduled dark carries no profile");

    // Unlocking with the sensor still hot lands in Sensor, not Base.
    Equal(AutomationMode.Sensor, AutomationDecision.Resolve(Make(locked: false, sensor: true)).Mode,
        "precedence: unlock into a live sensor rule");

    // Lock without the lights-off setting is not an override at all.
    Equal(AutomationMode.App, AutomationDecision.Resolve(Make(locked: true, app: true, lockOff: false)).Mode,
        "precedence: locked without lights-off is ignored");
}

/*---------------- Automation night window + status (#f1) ----------------*/
{
    // The caller decides whether a window is open; these are the four shapes
    // it can hand in.
    AutomationInputs Sched(bool off, bool paused = false, bool waiting = false) => new()
    {
        Locked = false, LockLightsOff = true,
        ScheduleOff = off ? new ScheduleHit("07:00", null) : null,
        ScheduleProfile = null, SchedulePaused = paused, ScheduleWaitingIdle = waiting,
        ScheduleEnd = "07:00",
        AppSwitchEnabled = false, ForegroundProcess = null, ForegroundIsSelf = false,
        AppRules = null, Sensor = null, SensorUnavailable = null,
    };
    Equal(AutomationMode.ScheduleOff, AutomationDecision.Resolve(Sched(true)).Mode, "schedule: open window turns the lights off");
    Equal(AutomationMode.Base, AutomationDecision.Resolve(Sched(false, waiting: true)).Mode, "schedule: waiting for idle leaves the lights on");
    Equal(AutomationMode.Base, AutomationDecision.Resolve(Sched(false, paused: true)).Mode, "schedule: an override keeps the lights on");

    Check(AutomationDecision.Resolve(Sched(true)).Status.Contains("07:00"), "schedule status names the end time");
    Check(AutomationDecision.Resolve(Sched(false, paused: true)).Status.Contains("paused"), "schedule status explains the override");
    Check(AutomationDecision.Resolve(Sched(false, waiting: true)).Status.Contains("idle"), "schedule status explains the idle wait");

    // The sensor status carries the numbers behind the decision.
    var withSensor = new AutomationInputs
    {
        Locked = false, LockLightsOff = true, ScheduleOff = null, ScheduleProfile = null,
        SchedulePaused = false, ScheduleWaitingIdle = false, ScheduleEnd = "",
        AppSwitchEnabled = false, ForegroundProcess = null, ForegroundIsSelf = false,
        AppRules = null, Sensor = new SensorHit(SensorSources.CpuTemp, "Alert", 87, 85, true),
        SensorUnavailable = null,
    };
    Check(AutomationDecision.Resolve(withSensor).Status.Contains("87°C"), "sensor status shows the reading");
    Check(AutomationDecision.Resolve(withSensor).Status.Contains("'Alert'"), "sensor status names the profile");

    // No reading is explained rather than silently doing nothing.
    var missing = new AutomationInputs
    {
        Locked = false, LockLightsOff = true, ScheduleOff = null, ScheduleProfile = null,
        SchedulePaused = false, ScheduleWaitingIdle = false, ScheduleEnd = "",
        AppSwitchEnabled = false, ForegroundProcess = null, ForegroundIsSelf = false,
        AppRules = null, Sensor = null, SensorUnavailable = SensorSources.CpuTemp,
    };
    Check(AutomationDecision.Resolve(missing).Status.Contains("PawnIO"), "missing sensor status mentions PawnIO");
    Equal(AutomationMode.Base, AutomationDecision.Resolve(missing).Mode, "missing sensor does not change the mode");
}

/*---------------- No em dashes in automation status copy (#f1) ----------------*/
{
    // House style, and these strings are user-visible.
    var seen = new List<string>();
    for (int i = 0; i < 4; i++)
    {
        var x = new AutomationInputs
        {
            Locked = false, LockLightsOff = true,
            ScheduleOff = i == 0 ? new ScheduleHit("07:00", null) : null,
            ScheduleProfile = null, SchedulePaused = i == 1, ScheduleWaitingIdle = i == 2,
            ScheduleEnd = "07:00",
            AppSwitchEnabled = true, ForegroundProcess = i == 3 ? "notepad" : null,
            ForegroundIsSelf = false, AppRules = null, Sensor = null, SensorUnavailable = null,
        };
        seen.Add(AutomationDecision.Resolve(x).Status);
    }
    Check(seen.TrueForAll(t => !t.Contains('\u2014')), "automation status copy has no em dashes");
}

Console.WriteLine($"{passed} passed, {failed} failed");
return failed;

/*---------------- test doubles (must follow the top-level statements) ----------------*/

/// <summary>Non-zone device that records every full-frame write with its
/// start timestamp; WriteDelayMs simulates a slow HID/USB write.</summary>
sealed class FakeDevice : IRgbDevice
{
    public string Name { get; init; } = "Fake";
    public string Vendor => "Test";
    public DeviceType Type => DeviceType.Other;
    public int LedCount { get; init; } = 2;
    public IReadOnlyList<RgbZone> Zones => new[] { new RgbZone { Name = "All", Offset = 0, Count = LedCount } };
    public int WriteDelayMs { get; init; }
    public readonly List<(long Start, Rgb[] Frame)> Writes = new();
    public int WriteCount { get { lock (Writes) return Writes.Count; } }
    public Rgb[]? Last { get { lock (Writes) return Writes.Count == 0 ? null : Writes[^1].Frame; } }
    public void SetColors(IReadOnlyList<Rgb> colors)
    {
        long start = Stopwatch.GetTimestamp();
        if (WriteDelayMs > 0) Thread.Sleep(WriteDelayMs);
        lock (Writes) Writes.Add((start, colors.ToArray()));
    }
    public void Dispose() { }
}

/// <summary>Constant-colour effect with a render counter and a settable
/// LiveInput flag (drives the engine's idle-throttle branch).</summary>
sealed class CountingEffect : IEffect
{
    public string Name => "Counting";
    public bool UsesBaseColor => true;
    public bool Live { get; init; }
    public bool LiveInput => Live;
    int _renders;
    public int Renders => Volatile.Read(ref _renders);
    public void Render(Rgb[] buf, LedPos[] pos, double t, double speed, Rgb bc)
    {
        Interlocked.Increment(ref _renders);
        Array.Fill(buf, bc);
    }
}

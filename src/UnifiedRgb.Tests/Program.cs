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
using UnifiedRgb.Core.Games;
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

/*---------------- Snap guides, shared by both editors (#f9) ----------------*/
{
    // Lines to snap to: the surface's edges and centre, plus each item's.
    var lines = SnapGuides.Lines(1000, new[] { (100.0, 50.0) });
    Check(lines.Contains(0) && lines.Contains(500) && lines.Contains(1000), "snap: the surface offers its edges and centre");
    Check(lines.Contains(100) && lines.Contains(125) && lines.Contains(150), "snap: and each item its edges and centre");

    // An item's leading edge pulls to a nearby line.
    var (v, line) = SnapGuides.Snap(97, 40, lines);
    Equal(100.0, v, "snap: the leading edge pulls into line");
    Equal(100.0, line, "snap: and reports what it met");

    // So does its centre and its trailing edge.
    var (byCentre, _) = SnapGuides.Snap(478, 40, lines);
    Equal(480.0, byCentre, "snap: the centre pulls too");
    var (byTrailing, _) = SnapGuides.Snap(63, 40, lines);
    Equal(60.0, byTrailing, "snap: and the trailing edge");

    // Far from anything, nothing moves. This is what lets you place something
    // deliberately a few units off an edge.
    var (free, noLine) = SnapGuides.Snap(300, 40, lines);
    Equal(300.0, free, "snap: nothing near means nothing moves");
    Check(noLine == null, "snap: and no guide is drawn");

    // Just outside the threshold is still free; just inside pulls.
    Equal(100.0 + SnapGuides.Threshold + 1, SnapGuides.Snap(100 + SnapGuides.Threshold + 1, 10, new List<double> { 100 }).Value,
          "snap: outside the threshold is left alone");
    Equal(100.0, SnapGuides.Snap(100 + SnapGuides.Threshold - 1, 10, new List<double> { 100 }).Value,
          "snap: inside it pulls");

    // The closest line wins when several are in range.
    Equal(100.0, SnapGuides.Snap(101, 10, new List<double> { 100, 130 }).Value,
          "snap: the nearer of two lines wins");
    Equal(130.0, SnapGuides.Snap(129, 10, new List<double> { 100, 130 }).Value,
          "snap: whichever side it is on");
    // Dead heats resolve to the last line offered rather than arbitrarily:
    // it is not a meaningful choice, but it is a repeatable one.
    var (tied, which) = SnapGuides.Snap(102, 10, new List<double> { 100, 104 });
    Equal(104.0, tied, "snap: a dead heat resolves the same way every time");
    Check(which != null, "snap: and still reports a line");

    double delta;
    Check(SnapGuides.Nearest(new List<double>(), new[] { 5.0 }, out delta) == null, "snap: no lines, no snap");
    Equal(0.0, delta, "snap: and no movement");
}

/*---------------- Whole-desk canvas: effects carry across (#f9) ----------------*/
{
    // The point of the feature: two devices side by side on the desk get
    // coordinates that continue from one into the other, so one wave crosses
    // both instead of restarting. Without the canvas each device spans 0..1 on
    // its own and the wave starts over.
    var layout = new CanvasLayout { Enabled = true, Width = 1000, Height = 1000 };
    layout.Items.Add(new CanvasItem { Device = "Left", X = 0, Y = 400, W = 400, H = 200 });
    layout.Items.Add(new CanvasItem { Device = "Right", X = 600, Y = 400, W = 400, H = 200 });

    var left = new FakeDevice { Name = "Left", LedCount = 10 };
    var right = new FakeDevice { Name = "Right", LedCount = 10 };

    var l = CanvasMapper.Positions(left, 0, 10, layout)!;
    var r = CanvasMapper.Positions(right, 0, 10, layout)!;

    // Every LED of the left device is left of every LED of the right one.
    Check(l[^1].X < r[0].X, "desk: the left device ends before the right one begins");
    Check(l[0].X < l[^1].X, "desk: and runs left to right itself");

    // Without the canvas both span the same 0..1, which is exactly the
    // restarting behaviour the desk view fixes.
    var plainLeft = EffectEngine.ZonePositions(left, 0, 10);
    var plainRight = EffectEngine.ZonePositions(right, 0, 10);
    Check(Math.Abs(plainLeft[0].X - plainRight[0].X) < 1e-6,
          "desk: without a canvas both devices start at the same coordinate");
    Check(Math.Abs(plainLeft[^1].X - plainRight[^1].X) < 1e-6,
          "desk: and end at the same one, which is why a wave restarts");

    // A device placed further right maps further right: the ordering is the
    // arrangement, not the device list.
    layout.ItemFor("Left")!.X = 600;
    layout.ItemFor("Right")!.X = 0;
    var swappedL = CanvasMapper.Positions(left, 0, 10, layout)!;
    var swappedR = CanvasMapper.Positions(right, 0, 10, layout)!;
    Check(swappedR[^1].X < swappedL[0].X, "desk: moving a device moves where the effect reaches it");
}

/*---------------- Whole-desk canvas: zones keep their place (#f9) ----------------*/
{
    // ZonePositions renormalizes a range to its own bounding box, which is
    // right for a per-device effect: a zone should fill its own span. It is
    // wrong for the desk, where it would stretch every zone across the whole
    // device rectangle so two zones rendered at the same phase, which is the
    // restarting the desk view exists to stop.
    var device = new FakeDevice { Name = "Strip", LedCount = 10 };

    var firstHalf = EffectEngine.ZonePositions(device, 0, 5);
    var secondHalf = EffectEngine.ZonePositions(device, 5, 5);
    Check(Math.Abs(firstHalf[0].X - secondHalf[0].X) < 1e-6,
          "zones: renormalized, both halves start at the same coordinate");

    var firstDev = EffectEngine.DevicePositions(device, 0, 5);
    var secondDev = EffectEngine.DevicePositions(device, 5, 5);
    Check(firstDev[^1].X < secondDev[0].X,
          "zones: in device coordinates the first half ends before the second begins");
    Check(firstDev[0].X < 0.01f, "zones: and the first starts at the device's own start");
    Check(secondDev[^1].X > 0.99f, "zones: and the second ends at its end");

    // Which is what the canvas mapping uses, so two zones of one device land on
    // different parts of its rectangle on the desk.
    var layout = new CanvasLayout { Enabled = true, Width = 1000, Height = 1000 };
    layout.Items.Add(new CanvasItem { Device = "Strip", X = 0, Y = 0, W = 1000, H = 100 });
    var deskFirst = CanvasMapper.Positions(device, 0, 5, layout)!;
    var deskSecond = CanvasMapper.Positions(device, 5, 5, layout)!;
    Check(deskFirst[^1].X < deskSecond[0].X, "zones: and they stay apart on the desk");

    // A whole-device range is unaffected either way.
    var whole = CanvasMapper.Positions(device, 0, 10, layout)!;
    Equal(10, whole.Length, "zones: a whole-device range still maps every led");
    Check(whole[0].X < whole[^1].X, "zones: running the length of its rectangle");
}

/*---------------- Led overrides reach the engine (#f9) ----------------*/
{
    // ZonePositions is what every channel renders against, so an override has
    // to win there or the feature does nothing.
    var device = new FakeDevice { Name = "Strip", LedCount = 6 };
    var before = EffectEngine.ZonePositions(device, 0, 6);

    var layout = new CanvasLayout { Enabled = false, Width = 1000, Height = 1000 };
    layout.Items.Add(new CanvasItem
    {
        Device = "Strip",
        LedLayout = new LedLayoutOverride { Shape = "grid", Cols = 3, Rows = 2 },
    });
    var previous = CanvasLayout.Current;
    CanvasLayout.Current = layout;
    try
    {
        var after = EffectEngine.ZonePositions(device, 0, 6);
        // A 3x2 grid is two rows, so the Y coordinates now differ; the flat
        // fallback had them all on one line.
        Check(Math.Abs(before[0].Y - before[5].Y) < 1e-6, "override: the fallback is flat");
        Check(Math.Abs(after[0].Y - after[5].Y) > 0.5, "override: the grid is not");
        // And it applies with the canvas OFF: fixing a shape is useful on its own.
        Check(!layout.Enabled, "override: with the desk switched off");
    }
    finally { CanvasLayout.Current = previous; }

    // Back to normal once the override is gone.
    var plain = EffectEngine.ZonePositions(device, 0, 6);
    Check(Math.Abs(plain[0].Y - plain[5].Y) < 1e-6, "override: removing it restores the fallback");
}

/*---------------- Whole-desk canvas: mapping (#f9) ----------------*/
{
    // A 100x100 device at (100,100) on a 1000x1000 desk: local (0,0) is its
    // top-left corner, so it lands at desk 0.1,0.1.
    var item = new CanvasItem { Device = "D", X = 100, Y = 100, W = 100, H = 100 };
    bool Near(LedPos a, double x, double y) => Math.Abs(a.X - x) < 1e-5 && Math.Abs(a.Y - y) < 1e-5;

    Check(Near(CanvasMapper.Map(new LedPos(0, 0), item, 1000, 1000), 0.1, 0.1), "canvas: top-left corner");
    Check(Near(CanvasMapper.Map(new LedPos(1, 1), item, 1000, 1000), 0.2, 0.2), "canvas: bottom-right corner");
    Check(Near(CanvasMapper.Map(new LedPos(0.5f, 0.5f), item, 1000, 1000), 0.15, 0.15), "canvas: the middle");

    // Rotation turns the device's layout inside its rectangle. 90 clockwise
    // sends the top-left corner to the top-right.
    var r90 = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, Rotation = 90 };
    Check(Near(CanvasMapper.Map(new LedPos(0, 0), r90, 1000, 1000), 1, 0), "canvas: 90 sends top-left to top-right");
    Check(Near(CanvasMapper.Map(new LedPos(1, 0), r90, 1000, 1000), 1, 1), "canvas: and top-right to bottom-right");

    var r180 = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, Rotation = 180 };
    Check(Near(CanvasMapper.Map(new LedPos(0, 0), r180, 1000, 1000), 1, 1), "canvas: 180 sends top-left to bottom-right");

    var r270 = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, Rotation = 270 };
    Check(Near(CanvasMapper.Map(new LedPos(0, 0), r270, 1000, 1000), 0, 1), "canvas: 270 sends top-left to bottom-left");

    // Four 90s are a full turn.
    var full = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, Rotation = 360 };
    Check(Near(CanvasMapper.Map(new LedPos(0.25f, 0.75f), full, 1000, 1000), 0.25, 0.75), "canvas: 360 is no rotation");
    var negative = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, Rotation = -90 };
    Check(Near(CanvasMapper.Map(new LedPos(0, 0), negative, 1000, 1000), 0, 1), "canvas: -90 is the same as 270");
    var junk = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, Rotation = 45 };
    Check(Near(CanvasMapper.Map(new LedPos(0.25f, 0.75f), junk, 1000, 1000), 0.25, 0.75), "canvas: a rotation we do not do is no rotation");

    var flipX = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, FlipX = true };
    Check(Near(CanvasMapper.Map(new LedPos(0, 0.25f), flipX, 1000, 1000), 1, 0.25), "canvas: flipX mirrors left to right");
    var flipY = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, FlipY = true };
    Check(Near(CanvasMapper.Map(new LedPos(0.25f, 0), flipY, 1000, 1000), 0.25, 1), "canvas: flipY mirrors top to bottom");

    // Flip happens BEFORE rotation. Doing it after would mirror a different
    // axis than the button the user pressed.
    var both = new CanvasItem { X = 0, Y = 0, W = 1000, H = 1000, FlipX = true, Rotation = 90 };
    Check(Near(CanvasMapper.Map(new LedPos(0, 0), both, 1000, 1000), 1, 1), "canvas: flip is applied before rotation");

    // A device the desk does not know about renders as it always has.
    var layout = new CanvasLayout { Enabled = true, Width = 1000, Height = 1000 };
    layout.Items.Add(new CanvasItem { Device = "Known", X = 0, Y = 0, W = 500, H = 500 });
    var known = new FakeDevice { Name = "Known", LedCount = 4 };
    var unknown = new FakeDevice { Name = "Stranger", LedCount = 4 };
    Check(CanvasMapper.Positions(unknown, 0, 4, layout) == null, "canvas: an unplaced device falls back");
    Check(CanvasMapper.Positions(known, 0, 4, layout) != null, "canvas: a placed device maps");

    // Off means off: byte-identical to the old behaviour is the whole promise.
    layout.Enabled = false;
    Check(CanvasMapper.Positions(known, 0, 4, layout) == null, "canvas: disabled falls back");
    Check(CanvasMapper.Positions(known, 0, 4, null) == null, "canvas: no layout at all falls back");
    layout.Enabled = true;

    // A device in the left half of the desk maps into the left half, which is
    // what makes a wave carry from one device to the next.
    var mapped = CanvasMapper.Positions(known, 0, 4, layout)!;
    Equal(4, mapped.Length, "canvas: one position per led");
    foreach (var q in mapped)
        Check(q.X >= 0 && q.X <= 0.5f && q.Y >= 0 && q.Y <= 0.5f, "canvas: it lands inside its own rectangle");
}

/*---------------- Whole-desk canvas: led layouts (#f9) ----------------*/
{
    bool Near(LedPos a, double x, double y) => Math.Abs(a.X - x) < 1e-5 && Math.Abs(a.Y - y) < 1e-5;

    var strip = CanvasMapper.FromOverride(new LedLayoutOverride { Shape = "strip" }, 5)!;
    Equal(5, strip.Length, "layout: a strip has one position per led");
    Check(Near(strip[0], 0, 0.5), "layout: strip starts at the left");
    Check(Near(strip[4], 1, 0.5), "layout: and ends at the right");
    Check(Near(strip[2], 0.5, 0.5), "layout: evenly spaced");

    var one = CanvasMapper.FromOverride(new LedLayoutOverride { Shape = "strip" }, 1)!;
    Check(Near(one[0], 0.5, 0.5), "layout: a single led sits in the middle, not at an edge");

    var ring = CanvasMapper.FromOverride(new LedLayoutOverride { Shape = "ring" }, 4)!;
    Equal(4, ring.Length, "layout: a ring has one position per led");
    Check(Near(ring[0], 0.5, 0), "layout: a ring starts at the top");
    Check(Near(ring[1], 1, 0.5), "layout: and runs clockwise");
    Check(Near(ring[2], 0.5, 1), "layout: through the bottom");
    Check(Near(ring[3], 0, 0.5), "layout: and back up the left");

    // Serpentine: every other row is wired backwards, so led 3 of a 3-wide
    // grid sits under led 2, not under led 0.
    var straight = CanvasMapper.FromOverride(
        new LedLayoutOverride { Shape = "grid", Cols = 3, Rows = 2 }, 6)!;
    Check(Near(straight[0], 0, 0), "layout: grid starts top-left");
    Check(Near(straight[2], 1, 0), "layout: across the first row");
    Check(Near(straight[3], 0, 1), "layout: then back to the left on the next");

    var snake = CanvasMapper.FromOverride(
        new LedLayoutOverride { Shape = "grid", Cols = 3, Rows = 2, Serpentine = true }, 6)!;
    Check(Near(snake[2], 1, 0), "layout: serpentine first row is the same");
    Check(Near(snake[3], 1, 1), "layout: but the second row starts where the first ended");
    Check(Near(snake[5], 0, 1), "layout: and ends where it would have started");

    // A description that cannot hold the LEDs is refused rather than half
    // applied: a wrong layout is worse than the fallback.
    Check(CanvasMapper.FromOverride(new LedLayoutOverride { Shape = "grid", Cols = 2, Rows = 2 }, 9) == null,
          "layout: a grid too small for the leds is refused");
    Check(CanvasMapper.FromOverride(new LedLayoutOverride { Shape = "spiral" }, 4) == null,
          "layout: an unknown shape is refused");
    Check(CanvasMapper.FromOverride(null, 4) == null, "layout: no override is no override");
    Check(CanvasMapper.FromOverride(new LedLayoutOverride { Shape = "strip" }, 0) == null,
          "layout: a device with no leds is refused");
}

/*---------------- Whole-desk canvas: layout file (#f9) ----------------*/
{
    var layout = new CanvasLayout { Enabled = true, Width = 1600, Height = 900 };
    var devices = new IRgbDevice[]
    {
        new FakeDevice { Name = "Board", LedCount = 50 },
        new FakeDevice { Name = "Keeb", LedCount = 116 },
    };
    layout.AutoArrange(devices);
    Equal(2, layout.Items.Count, "canvas: every device gets a place");

    // Running it again must not shuffle a desk the user has arranged.
    var moved = layout.ItemFor("Board")!;
    moved.X = 42; moved.Y = 43;
    layout.AutoArrange(devices);
    Equal(2, layout.Items.Count, "canvas: arranging again adds nothing");
    Equal(42.0, layout.ItemFor("Board")!.X, "canvas: and leaves a placed device alone");

    // A new device turning up later gets a place without disturbing the rest.
    layout.AutoArrange(new IRgbDevice[] { new FakeDevice { Name = "Fans", LedCount = 80 } });
    Equal(3, layout.Items.Count, "canvas: a new device is placed");
    Equal(42.0, layout.ItemFor("Board")!.X, "canvas: the others do not move");

    // Everything lands on the desk, never off the edge.
    foreach (var it in layout.Items)
    {
        Check(it.X >= 0 && it.Y >= 0, "canvas: nothing is placed above or left of the desk");
        Check(it.X + it.W <= layout.Width + 0.001, "canvas: nothing hangs off the right");
        Check(it.Y + it.H <= layout.Height + 0.001, "canvas: nothing hangs off the bottom");
    }

    // A device that has gone away keeps its entry, in case it comes back.
    layout.AutoArrange(new IRgbDevice[] { new FakeDevice { Name = "Keeb", LedCount = 116 } });
    Check(layout.ItemFor("Fans") != null, "canvas: an absent device keeps its place");

    // The file: a round trip has to keep every field, including the override.
    layout.ItemFor("Fans")!.LedLayout = new LedLayoutOverride
    { Shape = "grid", Cols = 8, Rows = 10, Serpentine = true };
    layout.ItemFor("Keeb")!.Rotation = 270;
    layout.ItemFor("Keeb")!.FlipY = true;

    string json = JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true });
    var back = JsonSerializer.Deserialize<CanvasLayout>(json)!;
    Equal(layout.Items.Count, back.Items.Count, "canvas: the items survive a round trip");
    Equal(270, back.ItemFor("Keeb")!.Rotation, "canvas: rotation survives");
    Check(back.ItemFor("Keeb")!.FlipY, "canvas: flip survives");
    Equal(8, back.ItemFor("Fans")!.LedLayout!.Cols, "canvas: the led override survives");
    Check(back.ItemFor("Fans")!.LedLayout!.Serpentine, "canvas: including serpentine");
    Equal(42.0, back.ItemFor("Board")!.X, "canvas: and the positions");

    // An older file, written before any of this existed.
    var old = JsonSerializer.Deserialize<CanvasLayout>("{}")!;
    Check(!old.Enabled, "canvas: an empty file is a disabled canvas");
    Equal(0, old.Items.Count, "canvas: with nothing placed");
    Equal(1600, old.Width, "canvas: and a default desk size");

    // A clone must not share items with the original: the editor's undo
    // depends on snapshots that do not move when the live layout does.
    var clone = layout.Clone();
    clone.ItemFor("Board")!.X = 999;
    Equal(42.0, layout.ItemFor("Board")!.X, "canvas: a clone is independent");
}

/*---------------- CS2 game state (#f8) ----------------*/
{
    // A payload shaped like the real thing: keys taken from a maintained CS2
    // GSI library, not guessed.
    string Live(string bomb = "", string phase = "live", int health = 100, int flashed = 0,
                string activity = "playing", string weapons = "") =>
        "{" +
        "\"provider\":{\"name\":\"Counter-Strike: Global Offensive\",\"appid\":730}," +
        "\"map\":{\"mode\":\"competitive\",\"name\":\"de_mirage\",\"phase\":\"live\",\"round\":7}," +
        "\"round\":{\"phase\":\"" + phase + "\"" + (bomb.Length > 0 ? ",\"bomb\":\"" + bomb + "\"" : "") + "}," +
        "\"player\":{\"steamid\":\"7656119\",\"name\":\"ryan\",\"team\":\"CT\",\"activity\":\"" + activity + "\"," +
        "\"state\":{\"health\":" + health + ",\"armor\":95,\"helmet\":true,\"flashed\":" + flashed +
        ",\"smoked\":0,\"burning\":0,\"money\":3200,\"round_kills\":2,\"round_killhs\":1,\"equip_value\":4700}" +
        (weapons.Length > 0 ? ",\"weapons\":" + weapons : "") + "}," +
        "\"auth\":{\"token\":\"secret123\"}}";

    var s1 = GsiParser.Parse(Live(), "secret123");
    Check(s1 != null, "cs2: a live payload parses");
    Equal(100, s1!.Health, "cs2: health");
    Equal(95, s1.Armor, "cs2: armor");
    Equal(3200, s1.Money, "cs2: money");
    Equal(2, s1.RoundKills, "cs2: round kills");
    Check(s1.Playing, "cs2: playing");
    Check(s1.Team == Team.CT, "cs2: team");
    Check(s1.Phase == RoundPhase.Live, "cs2: round phase");
    Check(s1.Bomb == BombState.None, "cs2: no bomb");

    // The token is the only thing stopping another local program posting fake
    // states, so a wrong one is a rejection, not a warning.
    Check(GsiParser.Parse(Live(), "wrong") == null, "cs2: a bad token is rejected");
    Check(GsiParser.Parse(Live(), "") != null, "cs2: no expected token means no check");
    Check(GsiParser.Parse("not json at all", "secret123") == null, "cs2: junk is rejected");
    Check(GsiParser.Parse("[1,2,3]", "") == null, "cs2: a non-object is rejected");

    // Partial payloads are normal: the game sends only what changed.
    var partial = GsiParser.Parse("{\"round\":{\"phase\":\"freezetime\"}}", "");
    Check(partial != null, "cs2: a partial payload parses");
    Check(partial!.Phase == RoundPhase.FreezeTime, "cs2: with what it does carry");
    Equal(0, partial.Health, "cs2: and defaults for what it does not");

    // A field arriving as the wrong type must not throw on the render path.
    var wrongType = GsiParser.Parse("{\"player\":{\"state\":{\"health\":\"100\"}}}", "");
    Check(wrongType != null, "cs2: a wrongly typed field does not throw");
    Equal(0, wrongType!.Health, "cs2: it just reads as missing");

    Check(GsiParser.Parse(Live(bomb: "planted"), "")!.Bomb == BombState.Planted, "cs2: bomb planted");
    Check(GsiParser.Parse(Live(bomb: "defused"), "")!.Bomb == BombState.Defused, "cs2: bomb defused");
    Check(GsiParser.Parse(Live(bomb: "exploded"), "")!.Bomb == BombState.Exploded, "cs2: bomb exploded");
    Check(GsiParser.Parse(Live(phase: "over"), "")!.Phase == RoundPhase.Over, "cs2: round over");
    Check(!GsiParser.Parse(Live(activity: "menu"), "")!.Playing, "cs2: in the menu is not playing");

    var dead = GsiParser.Parse(Live(health: 0), "");
    Equal(0, dead!.Health, "cs2: a dead player reads zero health");

    // Weapons arrive as an object keyed weapon_0.., and the slot numbers are
    // not stable, so the active one is found by state.
    string rifle = "{\"weapon_0\":{\"name\":\"weapon_knife\",\"type\":\"Knife\",\"state\":\"holstered\"}," +
                   "\"weapon_1\":{\"name\":\"weapon_ak47\",\"type\":\"Rifle\",\"ammo_clip\":7," +
                   "\"ammo_clip_max\":30,\"ammo_reserve\":60,\"state\":\"active\"}}";
    var armed = GsiParser.Parse(Live(weapons: rifle), "");
    Equal(7, armed!.AmmoClip, "cs2: the active weapon's clip");
    Equal(30, armed.AmmoClipMax, "cs2: and its capacity");
    Check(Math.Abs(armed.AmmoFraction - 7.0 / 30.0) < 1e-9, "cs2: ammo fraction");

    // A knife has no magazine: it must not read as out of ammo, or every knife
    // round would sit there pulsing a low-ammo warning.
    string knifeOnly = "{\"weapon_0\":{\"name\":\"weapon_knife\",\"type\":\"Knife\",\"state\":\"active\"}}";
    var knife = GsiParser.Parse(Live(weapons: knifeOnly), "");
    Equal(-1, knife!.AmmoClip, "cs2: a knife reports no magazine");
    Equal(1.0, knife.AmmoFraction, "cs2: so nothing warns about it");

    var noWeapons = GsiParser.Parse(Live(), "");
    Equal(1.0, noWeapons!.AmmoFraction, "cs2: no weapon section is not low ammo");

    Equal(255, GsiParser.Parse(Live(flashed: 255), "")!.Flashed, "cs2: flash amount");

    // The config the game reads.
    string cfg = GsiConfig.Build("http://localhost:27180", "abc123");
    Check(cfg.Contains("http://localhost:27180"), "cs2 cfg: the uri");
    Check(cfg.Contains("abc123"), "cs2 cfg: the token");
    Check(cfg.Contains("\"player_state\""), "cs2 cfg: asks for player state");
    Check(cfg.Contains("\"player_weapons\""), "cs2 cfg: asks for weapons");
    Check(cfg.Contains("\"round\""), "cs2 cfg: asks for the round");
    Check(cfg.Contains("\"heartbeat\""), "cs2 cfg: asks for a heartbeat, which is what detects the game closing");
    Equal("gamestate_integration_unifiedrgb.cfg", GsiConfig.FileName, "cs2 cfg: the file name the game looks for");

    // libraryfolders.vdf escapes its backslashes.
    Equal(@"E:\SteamLibrary", GsiConfig.PathFromVdfLine("\t\t\"path\"\t\t\"E:\\\\SteamLibrary\""),
          "cs2: a library path is unescaped");
    Check(GsiConfig.PathFromVdfLine("\t\t\"apps\"") == null, "cs2: other lines are ignored");
    Check(GsiConfig.PathFromVdfLine("") == null, "cs2: an empty line is ignored");

    // Tokens should differ per machine.
    Check(GsiServer.NewToken() != GsiServer.NewToken(), "cs2: tokens are not shared between installs");
    Equal(16, GsiServer.NewToken().Length, "cs2: token length");
}

/*---------------- CS2 listener end to end (#f8) ----------------*/
{
    // POST to the real listener the way the game does.
    using var gsi = new GsiServer();
    int port = gsi.Start("tok-e2e", preferredPort: 27581);
    Check(port > 0, "cs2 e2e: the listener bound a port");
    Check(!gsi.Connected, "cs2 e2e: nothing has posted yet");

    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    string payload = "{\"round\":{\"phase\":\"live\",\"bomb\":\"planted\"}," +
                     "\"player\":{\"activity\":\"playing\",\"team\":\"T\",\"state\":{\"health\":42}}," +
                     "\"auth\":{\"token\":\"tok-e2e\"}}";
    var reply = http.PostAsync($"http://localhost:{port}/",
                    new System.Net.Http.StringContent(payload)).GetAwaiter().GetResult();
    Equal(200, (int)reply.StatusCode, "cs2 e2e: the game gets its 200 back");

    bool arrived = false;
    for (int i = 0; i < 200 && !arrived; i++) { arrived = gsi.State.Health == 42; Thread.Sleep(10); }
    Check(arrived, "cs2 e2e: the state arrives");
    Check(gsi.Connected, "cs2 e2e: and the game counts as connected");
    Check(gsi.State.Bomb == BombState.Planted, "cs2 e2e: with the bomb state");
    Check(gsi.State.Team == Team.T, "cs2 e2e: and the team");

    // A post signed with the wrong token changes nothing.
    string forged = "{\"player\":{\"state\":{\"health\":1}},\"auth\":{\"token\":\"nope\"}}";
    http.PostAsync($"http://localhost:{port}/", new System.Net.Http.StringContent(forged))
        .GetAwaiter().GetResult();
    Thread.Sleep(150);
    Equal(42, gsi.State.Health, "cs2 e2e: a forged post is ignored");
}

/*---------------- OpenRGB SDK server (#f7) ----------------*/
{
    // The header is the only thing separating a real client from whatever else
    // finds the port.
    var head = new byte[OpenRgbProtocol.HeaderBytes];
    OpenRgbProtocol.WriteHeader(head, 3, OpenRgbProtocol.PktUpdateLeds, 42);
    var read = OpenRgbProtocol.ReadHeader(head);
    Check(read is { Device: 3, PacketId: 1050, Size: 42 }, "orgb: header round-trips");
    Equal((byte)'O', head[0], "orgb: magic");
    head[1] = (byte)'X';
    Check(OpenRgbProtocol.ReadHeader(head) == null, "orgb: a bad magic is rejected");
    Check(OpenRgbProtocol.ReadHeader(new byte[4]) == null, "orgb: a short header is rejected");

    // Colors are 0x00BBGGRR on the wire.
    Equal(0x00FF5030u, OpenRgbProtocol.ToWire(new Rgb(0x30, 0x50, 0xFF)), "orgb: color packs as BGR");
    Equal(new Rgb(0x30, 0x50, 0xFF), OpenRgbProtocol.FromWire(0x00FF5030u), "orgb: and unpacks again");

    Equal(0, OpenRgbProtocol.DeviceTypeOf(DeviceType.Motherboard), "orgb: motherboard is 0");
    Equal(1, OpenRgbProtocol.DeviceTypeOf(DeviceType.Dram), "orgb: dram is 1");
    Equal(5, OpenRgbProtocol.DeviceTypeOf(DeviceType.Keyboard), "orgb: keyboard is 5");
    Equal(3, OpenRgbProtocol.DeviceTypeOf(DeviceType.Fan), "orgb: fans read as coolers");

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
    Equal("Test Board", parsed.Name, "orgb: name survives");
    Equal("Test", parsed.Vendor, "orgb: vendor survives");
    Equal(13, parsed.LedCount, "orgb: led count survives");
    Equal(3, parsed.Zones.Count, "orgb: zone count survives");
    Equal("Header 1", parsed.Zones[0].Name, "orgb: zone name survives");
    Equal(8, parsed.Zones[0].LedCount, "orgb: zone size survives");
    Equal(1, parsed.Zones[1].Type, "orgb: a multi-led zone is linear");
    Equal(0, parsed.Zones[2].Type, "orgb: a single-led zone is single");
    Equal(13, parsed.Colors.Length, "orgb: a color per led");
    Equal(OpenRgbProtocol.ToWire(frame[7]), parsed.Colors[7], "orgb: the colors are the frame");

    // v0 has no vendor field. Writing the v1 blob to a v0 client would shift
    // every string after the name by one field.
    var v0 = OpenRgbProtocol.WriteDevice(zoned, frame, 0);
    Check(v0.Length < blob.Length, "orgb: the v0 blob omits the vendor string");

    // A device whose zones do not tile its LEDs would let a client write past
    // the end of one, so it is collapsed to a single zone instead.
    var ragged = new FakeZonedDevice { Name = "Ragged", Zones2 = new[] { ("Partial", 3) }, Leds = 10 };
    var rzones = OpenRgbProtocol.ZonesOf(ragged);
    Equal(1, rzones.Count, "orgb: zones that do not cover the device collapse to one");
    Equal(10, rzones[0].Count, "orgb: and that one covers everything");

    var none = new FakeZonedDevice { Name = "Bare", Zones2 = Array.Empty<(string, int)>(), Leds = 4 };
    Equal(1, OpenRgbProtocol.ZonesOf(none).Count, "orgb: a device with no zones still gets one");
}

/*---------------- SDK client handoff (#f7) ----------------*/
{
    // Writing claims a device; the claim lapses on silence or disconnect.
    var own = new ExternalOwnership<string>(silenceSeconds: 5);
    Check(own.Claim("board", client: 1, now: 0), "handoff: the first write claims the device");
    Check(!own.Claim("board", client: 1, now: 1), "handoff: the same client writing again does not re-claim");
    Check(own.IsOwned("board"), "handoff: and it is owned");
    Equal(1, own.OwnerOf("board"), "handoff: by that client");

    // Still writing: the claim holds well past the silence window.
    Equal(0, own.Expire(4).Count, "handoff: a live client keeps its device");
    own.Claim("board", 1, 4);
    Equal(0, own.Expire(8).Count, "handoff: writing again pushes the deadline out");

    // Gone quiet: released, once.
    var lapsed = own.Expire(9.1);
    Equal(1, lapsed.Count, "handoff: silence releases the device");
    Equal("board", lapsed[0], "handoff: the right one");
    Check(!own.IsOwned("board"), "handoff: and it is free again");
    Equal(0, own.Expire(100).Count, "handoff: a released device is not released twice");

    // Disconnect drops everything that client held, without waiting.
    own.Claim("board", 1, 10);
    own.Claim("ram", 1, 10);
    own.Claim("keeb", 2, 10);
    Equal(2, own.ReleaseClient(1).Count, "handoff: a disconnect frees only that client's devices");
    Check(own.IsOwned("keeb"), "handoff: the other client keeps its own");
    Equal(2, own.OwnerOf("keeb"), "handoff: still owned by client 2");

    // Last writer wins: there is no way to tell the loser it lost.
    Check(!own.Claim("keeb", 3, 11), "handoff: a takeover is not a fresh claim");
    Equal(3, own.OwnerOf("keeb"), "handoff: but the new client owns it");
    Equal(0, own.ReleaseClient(2).Count, "handoff: the old owner has nothing left to free");

    // Why a takeover must not read as a fresh claim: the caller saves the
    // user's lighting on a true and restores it on the matching release. The
    // old owner's disconnect frees nothing, so a second true would never be
    // balanced and the lighting would never come back at all.
    var pair = new ExternalOwnership<string>(silenceSeconds: 5);
    int taken = 0;
    if (pair.Claim("ram", 1, 0)) taken++;
    if (pair.Claim("ram", 2, 1)) taken++;      // client 2 takes it from client 1
    Equal(1, taken, "handoff: one device taken once, however many clients pass it around");
    Equal(0, pair.ReleaseClient(1).Count, "handoff: the displaced client frees nothing");
    Equal(1, pair.ReleaseClient(2).Count, "handoff: the holder frees it");
    Check(!pair.IsOwned("ram"), "handoff: and it is free, so the lighting comes back");

    Equal(1, own.ReleaseAll().Count, "handoff: a rescan frees everything");
    Equal(0, own.Count, "handoff: leaving nothing owned");
}

/*---------------- SDK server end to end (#f7) ----------------*/
{
    // Our own client against our own server over a real loopback socket. This
    // is the part unit tests cannot reach: framing, version negotiation, and a
    // write actually arriving as the colors that were sent.
    var host = new StubOrgbHost();
    host.Add(new FakeZonedDevice { Name = "Board", Zones2 = new[] { ("Header 1", 4), ("Logo", 1) } });
    host.Add(new FakeZonedDevice { Name = "Sticks", Zones2 = new[] { ("Stick", 8) } });

    using var server = new OpenRgbServer(host, silenceSeconds: 0.4);
    int port = server.Start(listenOnLan: false, port: 27423);
    Equal(27423, port, "orgb e2e: the server bound the port it was given");

    using (var client = OpenRgbClient.Connect("127.0.0.1", port))
    {
        Equal(1u, client.ServerVersion, "orgb e2e: negotiated protocol 1");
        Equal(2, client.GetControllerCount(), "orgb e2e: both devices are listed");

        var dev = client.GetControllerData(0);
        Equal("Board", dev.Name, "orgb e2e: the device name arrives");
        Equal(5, dev.LedCount, "orgb e2e: with its led count");
        Equal(2, dev.Zones.Count, "orgb e2e: and its zones");
        Equal("Header 1", dev.Zones[0].Name, "orgb e2e: named correctly");

        // A whole-device write.
        client.SetCustomMode(0);
        var want = new[] { new Rgb(255, 0, 0), new Rgb(0, 255, 0), new Rgb(0, 0, 255),
                           new Rgb(10, 20, 30), new Rgb(40, 50, 60) };
        client.UpdateLeds(0, want);
        Check(host.WaitForWrite("Board"), "orgb e2e: the write arrives");
        Equal(1, host.BeginCount("Board"), "orgb e2e: and claims the device once");
        var got = host.LastWrite("Board");
        Equal(0, got.Offset, "orgb e2e: a full write starts at zero");
        Equal(5, got.Colors.Count, "orgb e2e: all five leds");
        Equal(new Rgb(0, 0, 255), got.Colors[2], "orgb e2e: the colors survive the wire");

        // A zone write lands at that zone's offset, not at zero.
        host.Reset();
        client.UpdateZoneLeds(0, 1, new[] { new Rgb(9, 9, 9) });
        Check(host.WaitForWrite("Board"), "orgb e2e: the zone write arrives");
        Equal(4, host.LastWrite("Board").Offset, "orgb e2e: at the zone's offset");
        Equal(1, host.BeginCount("Board"), "orgb e2e: still one claim, not a second");

        // Writing again keeps the claim alive rather than restoring underneath.
        Equal(0, host.EndCount("Board"), "orgb e2e: nothing restored while the client writes");
    }

    // The client disconnected: the device goes back to the user without waiting
    // out the silence timer.
    Check(host.WaitForEnd("Board"), "orgb e2e: disconnecting restores the lighting");

    // A rescan drops every claim at once WITHOUT an EndExternal each, because
    // the device instances are gone. The host has to be told, or it waits for
    // releases that never come and never restores the user's lighting again.
    Equal(0, host.ResetCount, "orgb e2e: nothing reset yet");
    server.DeviceListChanged();
    Equal(1, host.ResetCount, "orgb e2e: a rescan unwinds the takeover");
}

/*---------------- Exit behaviors (#f6) ----------------*/
{
    // Stored in hardware.json, so it has to survive a round trip by NAME.
    var opts = new JsonSerializerOptions { WriteIndented = true };
    var saved = new ExitBehavior { Mode = ExitMode.Effect, ColorHex = "3050FF", Effect = "Rainbow" };
    string json = JsonSerializer.Serialize(saved, opts);
    Check(json.Contains("\"Effect\""), "exit: the mode is written as a name, not a number");
    var back = JsonSerializer.Deserialize<ExitBehavior>(json)!;
    Equal(ExitMode.Effect.ToString(), back.Mode.ToString(), "exit: mode round-trips");
    Equal("3050FF", back.ColorHex, "exit: color round-trips");
    Equal("Rainbow", back.Effect, "exit: effect name round-trips");

    // An older config, written before this feature existed, has no behaviors.
    var none = JsonSerializer.Deserialize<ExitBehavior>("{}")!;
    Equal(ExitMode.KeepLast.ToString(), none.Mode.ToString(), "exit: the default is today's behavior");

    var board = new FakeHardwareDevice { Name = "Board", ExitCaps = HardwareExitCaps.Static };
    var stick = new FakeHardwareDevice
    {
        Name = "Stick",
        ExitCaps = HardwareExitCaps.Static | HardwareExitCaps.Effects,
        HardwareEffects = new[] { "Breathing", "Rainbow" },
    };
    var keeb = new FakeHardwareDevice { Name = "Keeb", ExitCaps = HardwareExitCaps.ReturnToHardware };
    var plain = new FakeDevice { Name = "Plain" };

    // Nothing configured, and KeepLast, both send nothing at all: a device the
    // user never touched must not be written to on the way out.
    Check(HardwareExit.Apply(board, null) == null, "exit: no config sends nothing");
    Check(HardwareExit.Apply(board, new ExitBehavior { Mode = ExitMode.KeepLast }) == null,
          "exit: keep-last sends nothing");
    Check(board.StaticSet == null, "exit: and the device was not touched");

    HardwareExit.Apply(board, new ExitBehavior { Mode = ExitMode.Static, ColorHex = "3050FF" });
    Equal(Rgb.FromHex("3050FF"), board.StaticSet, "exit: static sends the chosen color");
    HardwareExit.Apply(board, new ExitBehavior { Mode = ExitMode.Off });
    Equal(Rgb.Black, board.StaticSet, "exit: off is static black");

    // Asking a device for something it cannot do leaves it alone rather than
    // throwing on the way out of the process.
    Check(HardwareExit.Apply(board, new ExitBehavior { Mode = ExitMode.Effect, Effect = "Rainbow" }) == null,
          "exit: an effect on a static-only device does nothing");
    Check(HardwareExit.Apply(keeb, new ExitBehavior { Mode = ExitMode.Static, ColorHex = "FFFFFF" }) == null,
          "exit: a static on a handback-only device does nothing");
    Check(HardwareExit.Apply(plain, new ExitBehavior { Mode = ExitMode.Static }) == null,
          "exit: a device with no firmware modes does nothing");

    HardwareExit.Apply(stick, new ExitBehavior { Mode = ExitMode.Effect, Effect = "rainbow", ColorHex = "FF0000" });
    Check(stick.EffectSet is { Name: "Rainbow" }, "exit: effect names match case-insensitively");
    Equal(Rgb.FromHex("FF0000"), stick.EffectSet!.Value.Color, "exit: the effect gets its color");

    // An effect renamed or dropped by a driver update must not silently become
    // a different one.
    stick.EffectSet = null;
    Check(HardwareExit.Apply(stick, new ExitBehavior { Mode = ExitMode.Effect, Effect = "Gone" }) == null,
          "exit: an effect the device no longer has does nothing");
    Check(stick.EffectSet == null, "exit: and nothing was sent instead");

    HardwareExit.Apply(keeb, new ExitBehavior { Mode = ExitMode.ReturnToHardware });
    Equal(1, keeb.HandbackCount, "exit: handback reaches the device");

    // What the dropdown offers comes from the device.
    Equal(1, HardwareExit.Choices(plain).Count, "exit: an unsupported device offers only keep-last");
    var boardChoices = HardwareExit.Choices(board);
    Equal(3, boardChoices.Count, "exit: static-only offers keep-last, static and off");
    Equal("Keeps its last colors", HardwareExit.Label(boardChoices[0]), "exit: keep-last is listed first");
    Equal(5, HardwareExit.Choices(stick).Count, "exit: static plus two effects");
    Equal(2, HardwareExit.Choices(keeb).Count, "exit: keep-last and the saved profile");
    Check(HardwareExit.NeedsColor(new ExitBehavior { Mode = ExitMode.Static }), "exit: static needs a color");
    Check(!HardwareExit.NeedsColor(new ExitBehavior { Mode = ExitMode.Off }), "exit: off does not");

    // Gigabyte: the hardware-static path sends the SAME static-effect packet
    // the per-frame path does, so pin the layout once. Colors go out B,G,R.
    var pkt = new byte[GigabyteIt5711.PacketBytes];
    GigabyteIt5711.FillZoneEffect(pkt, 5, new Rgb(0x30, 0x50, 0xFF), null);
    Equal(0xCC, pkt[0], "gigabyte: report id");
    Equal(0x25, pkt[1], "gigabyte: zone 5 is register 0x20 + 5");
    Equal(0x20, pkt[2], "gigabyte: zone bitmask low byte");
    Equal(0x00, pkt[3], "gigabyte: zone bitmask high byte");
    Equal(1, pkt[11], "gigabyte: static effect");
    Equal(0xFF, pkt[12], "gigabyte: full brightness");
    Equal(0xFF, pkt[14], "gigabyte: blue first");
    Equal(0x50, pkt[15], "gigabyte: then green");
    Equal(0x30, pkt[16], "gigabyte: then red");

    // Zone 9 and up move to the second register block.
    GigabyteIt5711.FillZoneEffect(pkt, 9, Rgb.Black, null);
    Equal(0x91, pkt[1], "gigabyte: zone 9 is register 0x90 + 1");

    // A streamed header is addressed by effect index on the way out, not by
    // the header number it streams on. Confusing the two would light the
    // wrong output.
    Equal(5, GigabyteIt5711.EffectIndexOfHeader(1), "gigabyte: header 1 is effect 5");
    Equal(8, GigabyteIt5711.EffectIndexOfHeader(4), "gigabyte: header 4 is effect 8");

    // ENE: the effect colour window is 15 bytes and REG_DIRECT sits directly
    // after it, so a sixth LED would write straight into the direct and mode
    // registers. This pins the cap to the hardware layout rather than to a
    // number someone remembered.
    Equal(0x8021, EneDram.REG_MODE, "ene: mode register");
    // The last byte the capped write touches stays below REG_DIRECT...
    Check(EneDram.REG_COLORS_EFFECT + 3 * EneDram.EFFECT_COLOR_LEDS - 1 < EneDram.REG_DIRECT,
          "ene: the capped effect color write stays inside its own window");
    // ...and one more LED would not: its third byte lands on REG_MODE itself,
    // which is the whole reason the cap is not the stick's LED count.
    Check(EneDram.REG_COLORS_EFFECT + 3 * (EneDram.EFFECT_COLOR_LEDS + 1) - 1 >= EneDram.REG_MODE,
          "ene: a sixth LED would write into the direct and mode registers");
}

/*---------------- Now-playing line (#f5) ----------------*/
{
    Equal("Radiohead · Karma Police", NowPlayingText.Compose("Radiohead", "Karma Police"),
          "now playing: artist and title");
    Equal("Karma Police", NowPlayingText.Compose(null, "Karma Police"), "now playing: title only");
    Equal("Radiohead", NowPlayingText.Compose("Radiohead", ""), "now playing: artist only");
    Equal("", NowPlayingText.Compose(null, null), "now playing: nothing playing is empty");
    Equal("", NowPlayingText.Compose("   ", " "), "now playing: whitespace is nothing");
    Equal("A · B", NowPlayingText.Compose("  A  ", "  B  "), "now playing: ends are trimmed");

    // A separator that reads as part of a name is worse than none. No dashes.
    Check(!NowPlayingText.Separator.Contains('-') && !NowPlayingText.Separator.Contains('—'),
          "now playing: the separator is not a dash");

    Equal("abc", NowPlayingText.Ellipsize("abc", 3), "ellipsis: what fits is left alone");
    Equal("abc…", NowPlayingText.Ellipsize("abcdef", 4), "ellipsis: cut to the budget");
    Equal("ab…", NowPlayingText.Ellipsize("ab cdef", 4), "ellipsis: no space stranded before it");
    Equal("…", NowPlayingText.Ellipsize("abcdef", 1), "ellipsis: a budget of one");
    Equal("", NowPlayingText.Ellipsize("abcdef", 0), "ellipsis: no budget at all");

    // A podcast episode title should never reach the typesetter whole.
    var epic = NowPlayingText.Compose("Some Podcast", new string('x', 500));
    Equal(NowPlayingText.MaxChars, epic.Length, "now playing: a huge title is capped");
    Check(epic.EndsWith('…'), "now playing: and marked as cut");
}

/*---------------- Razer battery decode (#f4) ----------------*/
{
    // A reply buffer: [1] = status, arguments[0] at index 9, so the byte both
    // battery commands answer in (arguments[1]) is index 10.
    static byte[] Reply(byte status, byte arg1)
    {
        var r = new byte[91];
        r[1] = status; r[10] = arg1;
        return r;
    }
    const byte OK = 0x02, TIMEOUT = 0x04, UNSUPPORTED = 0x05;

    // Razer answers 0..255, not a percentage.
    Equal(100, RazerHid.ScaleCharge(255), "battery: 255 is a full charge");
    Equal(50, RazerHid.ScaleCharge(128), "battery: half way");
    Equal(10, RazerHid.ScaleCharge(26), "battery: a tenth");
    Equal(25, RazerHid.ScaleCharge(64), "battery: a quarter");

    var half = RazerHid.DecodeBattery(Reply(OK, 128), Reply(OK, 1));
    Check(half is { Percent: 50, Charging: true }, "battery: level and charging flag decode");

    var off = RazerHid.DecodeBattery(Reply(OK, 200), Reply(OK, 0));
    Check(off is { Percent: 78, Charging: false }, "battery: off the charger");

    // The level is the useful half; a missing charging reply is not fatal.
    var noChg = RazerHid.DecodeBattery(Reply(OK, 255), null);
    Check(noChg is { Percent: 100, Charging: false }, "battery: no charging reply still reads the level");

    // A mouse that is merely asleep must not read as flat, or a low-battery
    // rule would fire every night.
    Check(RazerHid.DecodeBattery(Reply(TIMEOUT, 128), Reply(OK, 0)) == null,
          "battery: a sleeping mouse reads null, not 0%");
    Check(RazerHid.DecodeBattery(Reply(UNSUPPORTED, 0), null) == null,
          "battery: firmware without the command reads null");
    Check(RazerHid.DecodeBattery(null, null) == null, "battery: no reply reads null");
    Check(RazerHid.DecodeBattery(Reply(OK, 0), null) == null,
          "battery: a raw 0 is 'no battery', not a flat one");
    Check(RazerHid.DecodeBattery(new byte[4], null) == null, "battery: a short reply reads null");
}

/*---------------- Battery as a rule source (#f4) ----------------*/
{
    const string src = SensorSources.BatteryPrefix + "Razer Basilisk V3 Pro";
    Equal("Razer Basilisk V3 Pro battery", SensorSources.Label(src), "battery: source reads as a name");
    Equal("%", SensorSources.Unit(src), "battery: measured in percent");
    Check(!SensorSources.NeedsFullSweep(src),
          "battery: pushed by the poller, so it must not wake the full sweep");
    Check(!SensorSources.NeedsHub(src), "battery: wakes no sweep at all");
    Check(SensorSources.NeedsHub(SensorSources.CpuTemp), "battery: temps still need the hub");
    Check(SensorSources.NeedsHub(SensorSources.FanPrefix + "Fan #1"), "battery: fans still need the hub");

    SensorHub.PublishBatteries(new[]
    {
        new SensorHub.BatteryLevel("Razer Basilisk V3 Pro", 42, false),
    });
    Equal(42.0, SensorSources.Read(src), "battery: a rule reads the published charge");
    Check(SensorSources.Read(SensorSources.BatteryPrefix + "Nothing") == null,
          "battery: an unknown device reads null");

    // Low-battery rule: below 15%, hold cleared.
    var rule = new SensorRule { Source = src, Above = false, Threshold = 15, HoldSeconds = 0, Profile = "Low battery" };
    var state = new SensorRuleState(false, null);
    state = SensorRuleEvaluator.Step(rule, 42, state, 1);
    Check(!state.Active, "battery: 42% does not trip a 15% rule");
    state = SensorRuleEvaluator.Step(rule, 12, state, 1);
    Check(state.Active, "battery: 12% trips it");
    SensorHub.PublishBatteries(Array.Empty<SensorHub.BatteryLevel>());
}

/*---------------- UndoStack (#f3) ----------------*/
{
    var h = new UndoStack<string>(capacity: 3);
    Check(!h.CanUndo && !h.CanRedo, "undo: empty to start");
    Check(h.Undo("now") == null, "undo: nothing to undo returns null");
    Check(h.Redo("now") == null, "undo: nothing to redo returns null");

    // Push records the state BEFORE each change; the argument to Undo is the
    // state as it is now, so redo can come back to it.
    h.Push("a");            // about to go a -> b
    h.Push("b");            // about to go b -> c
    Check(h.CanUndo && !h.CanRedo, "undo: pushes are undoable, nothing to redo yet");

    Equal("b", h.Undo("c"), "undo: steps back one");
    Equal("a", h.Undo("b"), "undo: steps back again");
    Check(!h.CanUndo && h.CanRedo, "undo: exhausted, redo available");
    Check(h.Undo("a") == null, "undo: past the start returns null");

    Equal("b", h.Redo("a"), "redo: steps forward one");
    Equal("c", h.Redo("b"), "redo: steps forward again");
    Check(!h.CanRedo && h.CanUndo, "redo: exhausted, undo available");

    // A new edit after undoing abandons the future.
    h.Undo("c");
    Check(h.CanRedo, "undo: redo exists after stepping back");
    h.Push("x");
    Check(!h.CanRedo, "undo: a new edit clears redo");

    // Capacity drops the OLDEST entry, so recent history always survives.
    var cap = new UndoStack<int>(capacity: 3);
    for (int i = 1; i <= 5; i++) cap.Push(i);
    Equal(3, cap.Count, "undo: bounded at capacity");
    Equal(5, cap.Undo(6), "undo: newest entry kept");
    Equal(4, cap.Undo(5), "undo: second newest kept");
    Equal(3, cap.Undo(4), "undo: third newest kept");
    Check(cap.Undo(3) == 0, "undo: the oldest two fell off");

    var cleared = new UndoStack<string>();
    cleared.Push("a"); cleared.Undo("b");
    cleared.Clear();
    Check(!cleared.CanUndo && !cleared.CanRedo, "undo: Clear empties both sides");

    // The view binds to CanUndo/CanRedo, so changes have to be announced.
    int fired = 0;
    var watched = new UndoStack<string>();
    watched.Changed += () => fired++;
    watched.Push("a"); watched.Undo("b"); watched.Redo("a"); watched.Clear();
    Equal(4, fired, "undo: every mutation raises Changed");
}

/*---------------- UndoStack round-trips a design snapshot (#f3) ----------------*/
{
    // What the designer actually stores: whole-design JSON.
    string before = "{\"BgX\":10,\"BgY\":20,\"Elements\":[]}";
    string after = "{\"BgX\":99,\"BgY\":20,\"Elements\":[]}";
    var h = new UndoStack<string>();
    h.Push(before);
    Equal(before, h.Undo(after), "undo: a design snapshot comes back intact");
    Equal(after, h.Redo(before), "redo: the newer design comes back intact");
}

/*---------------- UndoStack: a drag is one undo step (#f3) ----------------*/
{
    // The reported bug: a drag that pauses part way through (lining an element
    // up against a snap guide) used to split into several undo steps, so one
    // Ctrl+Z only walked back part of the move. Mouse down to mouse up is one
    // entry, however many moves and however long it takes.
    var h = new UndoStack<string>();
    h.BeginGesture("x=0");
    Check(h.InGesture, "gesture: mouse down opens a gesture");
    for (int x = 1; x <= 40; x++) h.GestureEdit();     // every mouse move
    h.EndGesture();
    Equal(1, h.Count, "gesture: forty moves are one undo step");
    Equal("x=0", h.Undo("x=40"), "gesture: undo returns to where the drag began");

    // A click that selects but moves nothing must not leave a dead step behind,
    // because Push clears redo and the user would silently lose their redo.
    var sel = new UndoStack<string>();
    sel.Push("a");
    sel.Undo("b");                                     // redo now holds "b"
    Check(sel.CanRedo, "gesture: redo available before the click");
    sel.BeginGesture("a");
    sel.EndGesture();                                  // no GestureEdit: nothing moved
    Equal(0, sel.Count, "gesture: a click that moves nothing records nothing");
    Check(sel.CanRedo, "gesture: a click that moves nothing keeps redo alive");

    // Two separate drags are two separate steps.
    var two = new UndoStack<string>();
    two.BeginGesture("p0"); two.GestureEdit(); two.GestureEdit(); two.EndGesture();
    two.BeginGesture("p1"); two.GestureEdit(); two.EndGesture();
    Equal(2, two.Count, "gesture: each drag is its own step");
    Equal("p1", two.Undo("p2"), "gesture: undo walks back one drag at a time");
    Equal("p0", two.Undo("p1"), "gesture: and then the one before it");

    // Edits outside a gesture are the caller's business, not the stack's.
    var loose = new UndoStack<string>();
    loose.GestureEdit();
    Equal(0, loose.Count, "gesture: an edit with no gesture open records nothing");

    // Capture lost to an Alt+Tab ends the gesture; the next drag is new.
    var lost = new UndoStack<string>();
    lost.BeginGesture("a"); lost.GestureEdit();
    lost.EndGesture();
    Check(!lost.InGesture, "gesture: EndGesture closes it");
    lost.BeginGesture("b"); lost.GestureEdit(); lost.EndGesture();
    Equal(2, lost.Count, "gesture: a drag after a lost capture is its own step");
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
    // Overlapping windows: dark wins outright, and the profile takes over only
    // once the dark window closes.
    Equal(AutomationMode.ScheduleOff, AutomationDecision.Resolve(Make(off: true, schedProfile: true)).Mode, "precedence: scheduled dark beats a scheduled profile");
    Check(AutomationDecision.Resolve(Make(off: true, schedProfile: true)).Profile == null, "precedence: an overlapped profile is not applied");
    Equal(AutomationMode.ScheduleProfile, AutomationDecision.Resolve(Make(off: false, schedProfile: true)).Mode, "precedence: the profile takes over when the dark closes");
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
/// <summary>A device with firmware modes, recording what it was told to do on
/// the way out. Caps and effect list are settable so one class covers a board
/// (static only), a DRAM stick (static + effects) and a keyboard (handback).</summary>
sealed class FakeHardwareDevice : IRgbDevice, IHardwareModes
{
    public string Name { get; init; } = "FakeHw";
    public string Vendor => "Test";
    public DeviceType Type => DeviceType.Other;
    public int LedCount => 1;
    public IReadOnlyList<RgbZone> Zones => new[] { new RgbZone { Name = "All", Offset = 0, Count = 1 } };
    public void SetColors(IReadOnlyList<Rgb> colors) { }
    public void Dispose() { }

    public HardwareExitCaps ExitCaps { get; init; } = HardwareExitCaps.Static;
    public IReadOnlyList<string> HardwareEffects { get; init; } = Array.Empty<string>();

    public Rgb? StaticSet;
    public (string Name, Rgb? Color)? EffectSet;
    public int HandbackCount;

    public void SetHardwareStatic(Rgb color) => StaticSet = color;
    public void SetHardwareEffect(string name, Rgb? color) => EffectSet = (name, color);
    public void ReturnToHardware() => HandbackCount++;
}

/// <summary>A device with declared zones, for the SDK blob tests.</summary>
sealed class FakeZonedDevice : IRgbDevice
{
    public string Name { get; init; } = "Zoned";
    public string Vendor => "Test";
    public DeviceType Type => DeviceType.Motherboard;
    public (string Name, int Count)[] Zones2 { get; init; } = Array.Empty<(string, int)>();
    public int? Leds { get; init; }
    public int LedCount => Leds ?? Zones2.Sum(z => z.Count);
    public IReadOnlyList<RgbZone> Zones
    {
        get
        {
            var list = new List<RgbZone>();
            int off = 0;
            foreach (var (n, c) in Zones2) { list.Add(new RgbZone { Name = n, Offset = off, Count = c }); off += c; }
            return list;
        }
    }
    public void SetColors(IReadOnlyList<Rgb> colors) { }
    public void Dispose() { }
}

/// <summary>An IOpenRgbHost that records what the server asked it to do.</summary>
sealed class StubOrgbHost : IOpenRgbHost
{
    readonly List<IRgbDevice> _devices = new();
    readonly object _lock = new();
    readonly Dictionary<string, int> _begins = new();
    readonly Dictionary<string, int> _ends = new();
    readonly Dictionary<string, (int Offset, IReadOnlyList<Rgb> Colors)> _writes = new();

    public void Add(IRgbDevice d) => _devices.Add(d);
    public IReadOnlyList<IRgbDevice> Devices => _devices;
    public IReadOnlyList<Rgb> ColorsOf(IRgbDevice device) => new Rgb[device.LedCount];

    public void BeginExternal(IRgbDevice device)
    {
        lock (_lock) _begins[device.Name] = _begins.GetValueOrDefault(device.Name) + 1;
    }
    public void PushExternal(IRgbDevice device, int offset, IReadOnlyList<Rgb> colors)
    {
        lock (_lock) _writes[device.Name] = (offset, colors);
    }
    public void EndExternal(IRgbDevice device)
    {
        lock (_lock) _ends[device.Name] = _ends.GetValueOrDefault(device.Name) + 1;
    }
    public int ResetCount;
    public void ResetExternal() { lock (_lock) ResetCount++; }

    public int BeginCount(string name) { lock (_lock) return _begins.GetValueOrDefault(name); }
    public int EndCount(string name) { lock (_lock) return _ends.GetValueOrDefault(name); }
    public (int Offset, IReadOnlyList<Rgb> Colors) LastWrite(string name)
    {
        lock (_lock) return _writes.TryGetValue(name, out var w) ? w : (-1, Array.Empty<Rgb>());
    }
    public void Reset() { lock (_lock) _writes.Clear(); }

    // The server answers on its own threads, so the test waits rather than
    // assuming the reply has landed by the time the call returns.
    public bool WaitForWrite(string name) => Wait(() => LastWrite(name).Offset >= 0);
    public bool WaitForEnd(string name) => Wait(() => EndCount(name) > 0);

    static bool Wait(Func<bool> until)
    {
        for (int i = 0; i < 200; i++) { if (until()) return true; Thread.Sleep(10); }
        return false;
    }
}

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

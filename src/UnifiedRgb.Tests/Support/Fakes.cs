using System.Diagnostics;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Devices;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Native;
using UnifiedRgb.Core.Net;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Test doubles: the fake hardware and hosts every suite drives |
| instead of the real thing.                                   |
|                                                              |
| FakeHid is the important one - it is what lets a real driver |
| be tested without its device: the exact bytes it puts on the |
| wire for a given color, and what it does when the device    |
| refuses a packet, which is the path that has produced the    |
| most field bugs and the one that was untestable before it.   |
\*-----------------------------------------------------------*/
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
    // Canned success: this fake stands in for hardware that always takes what
    // it is given, so the exit-path tests exercise the decision, not delivery.
    public bool SetColors(IReadOnlyList<Rgb> colors) => true;
    public void Dispose() { }

    public HardwareExitCaps ExitCaps { get; init; } = HardwareExitCaps.Static;
    public IReadOnlyList<string> HardwareEffects { get; init; } = Array.Empty<string>();

    public Rgb? StaticSet;
    public (string Name, Rgb? Color)? EffectSet;
    public int HandbackCount;

    public bool SetHardwareStatic(Rgb color) { StaticSet = color; return true; }
    public bool SetHardwareEffect(string name, Rgb? color) { EffectSet = (name, color); return true; }
    public bool ReturnToHardware() { HandbackCount++; return true; }
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
    public bool SetColors(IReadOnlyList<Rgb> colors) => true;   // canned success
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
    public bool SetColors(IReadOnlyList<Rgb> colors)
    {
        long start = Stopwatch.GetTimestamp();
        if (WriteDelayMs > 0) Thread.Sleep(WriteDelayMs);
        lock (Writes) Writes.Add((start, colors.ToArray()));
        return true;   // canned success: this fake models a device that never refuses
    }
    public void Dispose() { }
}

/// <summary>A HID device made of lists. Records every report a driver sends,
/// answers its reads from a queue, and fails whichever writes a test says to.
///
/// This is what lets a real driver be tested without the hardware - the exact
/// bytes it puts on the wire for a given color, and what it does when the
/// device refuses a packet, which is the path that has produced the most
/// field bugs and the one that was untestable until now.</summary>
sealed class FakeHid : UnifiedRgb.Core.Native.IHidTransport
{
    public readonly List<byte[]> Writes = new();
    public readonly List<byte[]> Features = new();
    public readonly Queue<byte[]> Replies = new();
    /// <summary>Called for every Write with its 1-based ordinal; return false
    /// to refuse that report. Null accepts everything.</summary>
    public Func<int, byte[], bool>? Accept;
    /// <summary>Same for feature reports.</summary>
    public Func<int, byte[], bool>? AcceptFeature;
    /// <summary>For request/reply protocols carried on feature reports (Razer):
    /// when Replies is empty, the answer to GetFeature is computed from the
    /// LAST report sent with SetFeature, so a reply can echo the command it is
    /// for. Return null to answer nothing.</summary>
    public Func<byte[], byte[]?>? Respond;
    public byte[]? LastFeature;
    public bool FeatureOnly { get; set; }
    public bool IsDisposed { get; private set; }
    public int WriteTimeoutMs { get; set; } = 400;
    public int Reads;

    public bool Write(byte[] report)
    {
        if (IsDisposed || FeatureOnly) return false;
        var copy = (byte[])report.Clone();
        Writes.Add(copy);
        return Accept?.Invoke(Writes.Count, copy) ?? true;
    }

    public int Read(byte[] buffer, int timeoutMs)
    {
        Reads++;
        if (IsDisposed || Replies.Count == 0) return 0;
        var r = Replies.Dequeue();
        int n = Math.Min(r.Length, buffer.Length);
        Array.Copy(r, buffer, n);
        return n;
    }

    public bool SetFeature(byte[] report)
    {
        if (IsDisposed) return false;
        var copy = (byte[])report.Clone();
        Features.Add(copy);
        LastFeature = copy;
        return AcceptFeature?.Invoke(Features.Count, copy) ?? true;
    }

    public bool GetFeature(byte[] report)
    {
        if (IsDisposed) return false;
        byte[]? r = Replies.Count > 0 ? Replies.Dequeue()
                  : Respond != null && LastFeature != null ? Respond(LastFeature) : null;
        if (r == null) return false;
        Array.Copy(r, report, Math.Min(r.Length, report.Length));
        return true;
    }

    public bool GetInputReport(byte[] report) => GetFeature(report);
    public void Dispose() => IsDisposed = true;

    /// <summary>A HID++ long reply that Query will accept: echoes the feature
    /// index and software id so the driver's matcher takes it.</summary>
    public static byte[] HidppReply(byte featIdx, byte swId = 0x07)
    {
        var r = new byte[20];
        r[0] = 0x11; r[1] = 0xFF; r[2] = featIdx; r[3] = swId;
        return r;
    }
}

/// <summary>Constant-color effect with a render counter and a settable
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

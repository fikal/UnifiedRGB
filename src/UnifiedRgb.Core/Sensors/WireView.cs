using System.Buffers.Binary;
using Microsoft.Win32;
using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core.Sensors;

/*-----------------------------------------------------------*\
| Thermal Grizzly WireView Pro II: an inline 12V-2x6 adapter  |
| that measures every pin of the GPU power connector. It is a |
| USB CDC serial port (STM32 VCP, 0483:5740) speaking a bare  |
| protocol: one command byte in, a fixed-size little-endian   |
| struct out, no framing, no checksum.                        |
|                                                             |
|   0x01 vendor data  3 bytes  [vendor 0xEF][product][fw]     |
|   0x04 sensors    100 bytes  see Parse                      |
|                                                             |
| Layouts as documented by the open-source wireview-hwmon     |
| Linux driver (github.com/emaspa/wireview-hwmon, the same    |
| structs Thermal Grizzly's own client uses). Read-only: we   |
| never send config, screen, NVM or bootloader commands.      |
|                                                             |
| Lifecycle like SensorHub: the Cooling pane Touch()es it;    |
| a 1 s timer polls while touched and CLOSES THE PORT when    |
| nobody has looked for a few seconds. A COM port is          |
| exclusive, so holding it all day would lock the vendor app  |
| and HWiNFO out of the device for no benefit.                |
\*-----------------------------------------------------------*/
public static class WireView
{
    public enum State { NotFound, Busy, Unsupported, Connected }

    /// <summary>What discovery last found, as ONE value: three separate fields
    /// let a reader pair a new state with the previous device's name.</summary>
    public sealed record Info(State State, string? Edition, int Firmware);

    const string UsbId = "VID_0483&PID_5740";   // generic STM32 VCP: verified by the vendor query
    const byte CmdVendorData = 0x01, CmdSensorValues = 0x04;
    const byte VendorThermalGrizzly = 0xEF;
    internal const int FrameSize = 100;
    const int PollMs = 1000;
    const int IdleCloseSeconds = 5;
    // A reply arrives in ~1 ms; this only bounds a dead or still-booting device.
    // The reference driver waits a full second, and a WireView that has only
    // just enumerated (replug, resume, the vendor app letting go) can take most
    // of that to answer its first query.
    const int IoTimeoutMs = 1000;
    const int FailuresBeforeClose = 3;            // consecutive bad polls before the port is reopened
    const int SilentTriesBeforeBackoff = 3;       // then a silent stranger is asked once a minute
    const long SilentBackoffMs = 60_000;
    const int BadConnectsBeforeUnsupported = 3;   // links in a row that never produced a sane frame

    // Two locks: _gate guards the timer and is only ever held for a few
    // instructions (Touch runs on the UI thread); _io guards the port and the
    // discovery memory, and is held across serial I/O, which a dead device
    // stretches to the timeout. Never take _gate while holding _io.
    static readonly object _gate = new(), _io = new();
    static Timer? _timer;
    static long _lastTouchTicks;
    static int _ticking;
    static ISerialQuery? _link;
    static string? _port;
    static int _failures, _goodFrames;
    static bool _shutdown;

    // What discovery has learned, per COM name. Pruned to the ports present at
    // every scan, so an unplug (or a different gadget later given that name)
    // starts clean - a session-long blacklist hid a real WireView that was
    // merely slow to answer after a replug until the app restarted.
    static readonly HashSet<string> _notOurs = new(StringComparer.OrdinalIgnoreCase);   // answered as another device
    static readonly Dictionary<string, (int Tries, long RetryAtMs)> _silent = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, (string Name, int Fw)> _unsupported = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, int> _badConnects = new(StringComparer.OrdinalIgnoreCase);

    // Ports that have answered as a WireView, this run or an earlier one. Only
    // these count as Busy when another program holds them: 0483:5740 is the
    // generic STM32 ID, and some other gadget open in its own tool must not
    // put up a card saying the WireView is. Persisted so a vendor app that
    // grabbed the port before we ever saw it still reads as Busy, not absent.
    // A known port is also never backed off or written off: it is a WireView.
    static HashSet<string>? _known;
    static string KnownFile => AppPaths.Local("wireview-ports.txt");
    static HashSet<string> Known => _known ??= LoadKnown();

    static HashSet<string> LoadKnown()
    {
        try { return new(File.ReadAllLines(KnownFile).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase); }
        catch { return new(StringComparer.OrdinalIgnoreCase); }   // none yet
    }

    static void Remember(string port)
    {
        if (!Known.Add(port)) return;
        try { SafeFile.WriteAllText(KnownFile, string.Join("\n", Known)); }
        catch (Exception ex) { Log.Warn("wireview", $"could not save {KnownFile}: {ex.Message}"); }
    }

    // Seams for the harness: where candidate ports come from, and how one is
    // opened (port, or null plus the Win32 error).
    static readonly Func<List<string>> RealFindPorts = CandidatePorts;
    static readonly Func<string, (ISerialQuery? Port, int Error)> RealOpenPort = port =>
    {
        var link = SerialLink.Open(port, 115200, IoTimeoutMs, out int err);
        return (link, err);
    };
    static Func<List<string>> _findPorts = RealFindPorts;
    static Func<string, (ISerialQuery? Port, int Error)> _openPort = RealOpenPort;

    static object? _latest;   // WireViewReading, boxed-reference publish (atomic swap)
    static Info _info = new(State.NotFound, null, 0);
    static volatile bool _scanned;

    /// <summary>The newest sane frame while connected; null otherwise.</summary>
    public static WireViewReading? Latest => (WireViewReading?)Volatile.Read(ref _latest);
    /// <summary>State, edition and firmware read together.</summary>
    public static Info Current => Volatile.Read(ref _info);
    public static State Status => Current.State;
    /// <summary>"WireView Pro II" / "... Noctua Edition", or the unsupported
    /// device's name when <see cref="Status"/> is Unsupported.</summary>
    public static string? Edition => Current.Edition;
    public static int Firmware => Current.Firmware;
    /// <summary>False until the first port scan has run: until then a
    /// NotFound <see cref="Status"/> means "not looked yet", not "absent".</summary>
    public static bool HasScanned => _scanned;

    /// <summary>Called by each reader on every refresh: keeps the poller (and
    /// the port) alive. Never blocks; the first poll runs on the timer thread.</summary>
    public static void Touch()
    {
        Interlocked.Exchange(ref _lastTouchTicks, DateTime.UtcNow.Ticks);
        lock (_gate)
        {
            if (_shutdown || _timer != null) return;
            _timer = new Timer(_ => Tick(), null, 0, PollMs);
        }
    }

    /// <summary>Let go of the port (the pane left the screen). Never blocks the
    /// caller, which is the UI thread: the in-flight tick - a discovery pass
    /// can be a few seconds of timeouts - is waited out on the pool, and the
    /// port closed there. A Touch arriving meanwhile keeps the link.</summary>
    public static void Release()
    {
        Timer? t;
        lock (_gate) { t = _timer; _timer = null; }
        _ = Task.Run(() =>
        {
            Drain(t);
            lock (_io) { if (Volatile.Read(ref _timer) == null) CloseLink(); }
        });
    }

    /// <summary>App exit: stop polling, close the port, refuse any later
    /// Touch. Bounded: a tick stuck in a driver call past the drain must not
    /// hold the exit up; process teardown closes the handle regardless.</summary>
    public static void Shutdown()
    {
        Timer? t;
        lock (_gate) { _shutdown = true; t = _timer; _timer = null; }
        Drain(t);
        if (Monitor.TryEnter(_io, 3000))
            try { CloseLink(); } finally { Monitor.Exit(_io); }
    }

    /// <summary>Stop a timer and wait (bounded) for its in-flight callback.
    /// DisposeAsync, NOT Dispose(WaitHandle): that form signals the handle when
    /// the callback returns, and once this wait has given up and closed it,
    /// the signal throws on the pool - process-fatal. SensorHub.Drain was
    /// bitten by exactly that.</summary>
    static void Drain(Timer? t)
    {
        if (t == null) return;
        try { t.DisposeAsync().AsTask().Wait(2000); }
        catch (AggregateException) { }   // the callback's own failure, already logged by Tick
    }

    static void Tick()
    {
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;
        try { TickCore(); }
        catch (Exception ex) { Log.Occasional("wireview-tick", "wireview", () => $"poll failed: {ex.Message}"); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    static void TickCore()
    {
        bool idle;
        lock (_gate)
        {
            if (_timer == null) return;   // Release()/Shutdown() is draining this tick
            // Read under the lock: a Touch landing between an earlier read and
            // this lock found the timer alive and returned, and the stale time
            // then closed the port in front of the pane that had just asked.
            var since = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastTouchTicks), DateTimeKind.Utc);
            idle = since.TotalSeconds > IdleCloseSeconds;
            if (idle) { _timer.Dispose(); _timer = null; }
        }
        lock (_io)
        {
            // Nobody looking: give the port back. A Touch racing this starts a
            // fresh timer, which reconnects within a tick or two.
            if (idle) { CloseLink(); return; }
            Poll();
        }
    }

    /// <summary>One poll: connect if needed, then read a frame. Under _io.</summary>
    static void Poll()
    {
        if (_link == null)
        {
            bool ok = Connect();
            _scanned = true;
            if (!ok) return;
        }

        Span<byte> frame = stackalloc byte[FrameSize];
        var reading = _link!.Query(CmdSensorValues, frame) ? Parse(frame) : null;
        if (reading != null)
        {
            _failures = 0;
            if (_goodFrames++ == 0) _badConnects.Remove(_port!);
            Volatile.Write(ref _latest, reading);
            return;
        }
        // A desynced or short read: the next poll's purge realigns. Several in
        // a row = unplugged or wedged; reopen from scratch.
        if (++_failures < FailuresBeforeClose) return;
        string port = _port!;
        if (_goodFrames == 0 && (_badConnects[port] = _badConnects.GetValueOrDefault(port) + 1) >= BadConnectsBeforeUnsupported)
        {
            // It answers as a Pro II but not one frame has passed the sanity
            // gate, link after link: a firmware whose frame differs from the
            // documented one. Reconnecting forever would toggle DTR every few
            // seconds and leave the card on "Connecting…" for good.
            var was = Current;
            string name = $"{was.Edition ?? "WireView"} (unrecognised sensor format)";
            _unsupported[port] = (name, was.Firmware);
            Log.Warn("wireview", $"{port}: {BadConnectsBeforeUnsupported} connections without one valid sensor frame - treating it as unsupported");
            CloseLink();
            SetState(State.Unsupported, name, was.Firmware, port);
            return;
        }
        Log.Occasional("wireview-badpolls", "wireview", $"{port}: {FailuresBeforeClose} bad polls in a row - closing, will look again");
        CloseLink();
    }

    /// <summary>Find, open and verify a WireView. Under _io.</summary>
    static bool Connect()
    {
        var ports = _findPorts();
        Forget(ports);
        long now = Environment.TickCount64;
        bool sawBusy = false;
        (string Port, string Name, int Fw)? unsupported = null;
        Span<byte> vd = stackalloc byte[3];
        foreach (var port in ports)
        {
            if (_notOurs.Contains(port)) continue;
            // Known to be a model we cannot read: say so, but keep looking - a
            // Pro II on another port still wins - and do not reopen it every
            // second, which would lock the vendor app out of it each time.
            if (_unsupported.TryGetValue(port, out var un)) { unsupported ??= (port, un.Name, un.Fw); continue; }
            bool known = Known.Contains(port);
            if (!known && _silent.TryGetValue(port, out var s) && s.Tries >= SilentTriesBeforeBackoff && now < s.RetryAtMs)
                continue;

            var (link, err) = _openPort(port);
            if (link == null)
            {
                // ERROR_ACCESS_DENIED: another program holds it.
                if (err == 5 && known) sawBusy = true;
                continue;
            }
            if (!link.Query(CmdVendorData, vd))
            {
                link.Dispose();
                if (!known)
                {
                    int tries = (_silent.TryGetValue(port, out var t) ? t.Tries : 0) + 1;
                    _silent[port] = (tries, now + SilentBackoffMs);
                    if (tries == SilentTriesBeforeBackoff)
                        Log.Info("wireview", $"{port}: 0483:5740 port did not answer the vendor query - asking it once a minute from now on");
                }
                continue;
            }
            _silent.Remove(port);
            var (kind, name) = Identify(vd[0], vd[1]);
            if (kind == State.NotFound)
            {
                link.Dispose();
                // A port that has answered as a WireView before and now answers
                // garbage is a desynced read, not a different device: ask again
                // next scan rather than writing it off.
                if (!known)
                {
                    _notOurs.Add(port);
                    Log.Info("wireview", $"{port}: answered {vd[0]:X2} {vd[1]:X2} - not a WireView, leaving it alone while it stays plugged in");
                }
                continue;
            }
            Remember(port);
            if (kind == State.Unsupported)
            {
                link.Dispose();
                _unsupported[port] = (name!, vd[2]);
                unsupported ??= (port, name!, vd[2]);
                continue;
            }
            _link = link; _port = port; _failures = 0; _goodFrames = 0;
            SetState(State.Connected, name, vd[2], port);
            return true;
        }
        if (unsupported is { } u) SetState(State.Unsupported, u.Name, u.Fw, u.Port);
        else SetState(sawBusy ? State.Busy : State.NotFound, null, 0, null);
        return false;
    }

    /// <summary>Drop what discovery learned about ports that are gone. Under _io.</summary>
    static void Forget(List<string> present)
    {
        var here = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        _notOurs.IntersectWith(here);
        foreach (var gone in _silent.Keys.Where(p => !here.Contains(p)).ToList()) _silent.Remove(gone);
        foreach (var gone in _unsupported.Keys.Where(p => !here.Contains(p)).ToList()) _unsupported.Remove(gone);
        foreach (var gone in _badConnects.Keys.Where(p => !here.Contains(p)).ToList()) _badConnects.Remove(gone);
    }

    static void SetState(State s, string? edition, int fw, string? port)
    {
        var was = Current;
        if (s == was.State && edition == was.Edition && fw == was.Firmware) return;
        Volatile.Write(ref _info, new Info(s, edition, fw));
        Log.Info("wireview", s switch
        {
            State.Connected => $"{edition} on {port}, firmware v{fw}",
            State.Unsupported => $"{edition} on {port} - its sensor format is not supported",
            State.Busy => "the WireView's port is open in another program",
            _ => "no WireView found",
        });
    }

    /// <summary>Under _io. Drops the reading so readers never show a stale
    /// frame as live. The state is left as last seen: only Connect() decides
    /// it, so reopening the pane shows the card at once ("Connecting…") instead
    /// of hiding it until the first poll, and an unplug reads NotFound on the
    /// very next scan.</summary>
    static void CloseLink()
    {
        _link?.Dispose();
        _link = null;
        _port = null;
        _failures = 0;
        _goodFrames = 0;
        Volatile.Write(ref _latest, null);
    }

    /// <summary>Harness only: back to a fresh process with the given seams
    /// (null = the real registry scan / serial open) and known-port list.</summary>
    internal static void ResetForTests(Func<List<string>>? findPorts = null,
        Func<string, (ISerialQuery? Port, int Error)>? openPort = null, IEnumerable<string>? known = null)
    {
        Shutdown();
        lock (_gate) _shutdown = false;
        lock (_io)
        {
            _notOurs.Clear(); _silent.Clear(); _unsupported.Clear(); _badConnects.Clear();
            _known = new(known ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            _findPorts = findPorts ?? RealFindPorts;
            _openPort = openPort ?? RealOpenPort;
            Volatile.Write(ref _info, new Info(State.NotFound, null, 0));
            Volatile.Write(ref _latest, null);
            _scanned = false;
        }
    }

    /// <summary>Harness only: one poll on the caller's thread, no timer.</summary>
    internal static void PollForTests() { lock (_io) Poll(); }

    /// <summary>COM ports of plugged-in 0483:5740 devices. SERIALCOMM lists
    /// only ports whose device is present now; the USB enum tree maps each
    /// instance to its port name.</summary>
    static List<string> CandidatePorts()
    {
        var found = new List<string>();
        using var comm = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if (comm == null) return found;
        var present = new HashSet<string>(comm.GetValueNames().Select(n => comm.GetValue(n) as string)
            .OfType<string>(), StringComparer.OrdinalIgnoreCase);
        if (present.Count == 0) return found;
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb == null) return found;
        foreach (var id in usb.GetSubKeyNames())
        {
            if (!id.StartsWith(UsbId, StringComparison.OrdinalIgnoreCase)) continue;
            using var idKey = usb.OpenSubKey(id);
            if (idKey == null) continue;
            foreach (var inst in idKey.GetSubKeyNames())
            {
                using var p = idKey.OpenSubKey(inst + @"\Device Parameters");
                if (p?.GetValue("PortName") is string port && present.Contains(port) && !found.Contains(port))
                    found.Add(port);
            }
        }
        return found;
    }

    /// <summary>Classify a vendor-data reply. Products 5/6 share one frame
    /// layout; 7/8 (WireView II, Phanteks) answer the same query but send a
    /// different frame.</summary>
    internal static (State Kind, string? Name) Identify(byte vendor, byte product)
    {
        if (vendor != VendorThermalGrizzly) return (State.NotFound, null);
        return product switch
        {
            0x05 => (State.Connected, "WireView Pro II"),
            0x06 => (State.Connected, "WireView Pro II Noctua Edition"),
            0x07 => (State.Unsupported, "WireView II"),
            0x08 => (State.Unsupported, "WireView II Phanteks Edition"),
            _ => (State.NotFound, null),
        };
    }

    /*-----------------------------------------------------*\
    | Sensor frame, 100 bytes, Pack=4 little-endian:        |
    |   0  i16 temp[4]      0.1 °C (in, out, ext1, ext2)    |
    |   8  u16 vdd          mV                              |
    |  10  u8  fan          %        11  u8 pad (0)         |
    |  12  6 x pin, 12 bytes each:                          |
    |        i16 mV, u16 pad, u32 mA, u32 mW                |
    |  84  u32 total mW     88  u32 total mA                |
    |  92  u16 avg mV       94  u8 psu cap  95 u8 pad (0)   |
    |  96  u16 faults       98  u16 fault log               |
    \*-----------------------------------------------------*/

    // Sanity bounds, from the reference driver: a desynced read corrupts
    // arbitrary fields, and real frames always have zero padding, fan <= 100 %
    // and physically possible pins (-1..20 V, <= 60 A; the contact is rated 9.5 A).
    const int PinMvMin = -1000, PinMvMax = 20000;
    const uint PinMaMax = 60000;
    const int VddMvMax = 6000;

    static readonly int[] PsuCapWatts = { 600, 450, 300, 150 };

    /// <summary>Decode a sensor frame, or null when it is short or fails the
    /// sanity checks (the caller keeps the previous reading).</summary>
    internal static WireViewReading? Parse(ReadOnlySpan<byte> f)
    {
        if (f.Length < FrameSize) return null;
        int fan = f[10];
        ushort vdd = BinaryPrimitives.ReadUInt16LittleEndian(f[8..]);
        if (fan > 100 || f[11] != 0 || f[95] != 0 || vdd > VddMvMax) return null;

        var pins = new WireViewPin[6];
        for (int i = 0; i < 6; i++)
        {
            var p = f[(12 + i * 12)..];
            short mv = BinaryPrimitives.ReadInt16LittleEndian(p);
            uint ma = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
            uint mw = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
            if (mv < PinMvMin || mv > PinMvMax || ma > PinMaMax) return null;
            pins[i] = new WireViewPin(mv / 1000.0, ma / 1000.0, mw / 1000.0);
        }

        static double? Temp(short raw) => raw is < -400 or > 2000 ? null : raw / 10.0;   // unplugged probes read -100.0
        byte cap = f[94];
        return new WireViewReading(
            pins,
            Watts: BinaryPrimitives.ReadUInt32LittleEndian(f[84..]) / 1000.0,
            Amps: BinaryPrimitives.ReadUInt32LittleEndian(f[88..]) / 1000.0,
            Volts: BinaryPrimitives.ReadUInt16LittleEndian(f[92..]) / 1000.0,
            OnboardInC: Temp(BinaryPrimitives.ReadInt16LittleEndian(f)),
            OnboardOutC: Temp(BinaryPrimitives.ReadInt16LittleEndian(f[2..])),
            External1C: Temp(BinaryPrimitives.ReadInt16LittleEndian(f[4..])),
            External2C: Temp(BinaryPrimitives.ReadInt16LittleEndian(f[6..])),
            FanPercent: fan,
            PsuCapWatts: cap < PsuCapWatts.Length ? PsuCapWatts[cap] : null,
            Faults: (WireViewFault)BinaryPrimitives.ReadUInt16LittleEndian(f[96..]),
            FaultLog: (WireViewFault)BinaryPrimitives.ReadUInt16LittleEndian(f[98..]));
    }

    /// <summary>Human names for a fault mask, most serious first.</summary>
    public static IEnumerable<string> Describe(WireViewFault faults)
    {
        if (faults.HasFlag(WireViewFault.WireOverCurrent)) yield return "over-current on a wire";
        if (faults.HasFlag(WireViewFault.OverCurrent)) yield return "total over-current";
        if (faults.HasFlag(WireViewFault.OverPower)) yield return "over-power";
        if (faults.HasFlag(WireViewFault.CurrentImbalance)) yield return "current imbalance";
        if (faults.HasFlag(WireViewFault.SensorOverTemp)) yield return "probe over-temperature";
        if (faults.HasFlag(WireViewFault.ChipOverTemp)) yield return "adapter over-temperature";
    }
}

/// <summary>Fault bits as the device reports them (active and logged masks).
/// The device does not say WHICH wire tripped WireOverCurrent.</summary>
[Flags]
public enum WireViewFault : ushort
{
    None = 0,
    ChipOverTemp = 1 << 0,
    SensorOverTemp = 1 << 1,
    OverCurrent = 1 << 2,
    WireOverCurrent = 1 << 3,
    OverPower = 1 << 4,
    CurrentImbalance = 1 << 5,
}

public readonly record struct WireViewPin(double Volts, double Amps, double Watts);

/// <summary>One sensor frame. Totals are the device's own (what its screen
/// shows). Temps are null for an unplugged probe; PsuCapWatts is the cable
/// rating the GPU's sense pins advertise.</summary>
public sealed record WireViewReading(
    WireViewPin[] Pins,
    double Watts, double Amps, double Volts,
    double? OnboardInC, double? OnboardOutC, double? External1C, double? External2C,
    int FanPercent, int? PsuCapWatts,
    WireViewFault Faults, WireViewFault FaultLog);

using System.IO;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Audio;
using UnifiedRgb.Core.Devices;

// Ctrl+C is cooperative: the hardware probes below park fans at raw duties and
// release the board's vendor fan control, restoring in `finally` blocks - which
// a hard console termination skips. Every probe delay goes through Sleep(),
// which throws on Ctrl+C so the finally blocks run before we exit.
var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); Console.WriteLine("\nCtrl+C - restoring hardware state..."); };
void Sleep(int ms) { if (cancel.Token.WaitHandle.WaitOne(ms)) throw new OperationCanceledException(); }
// Probe output goes to the console AND the log: the elevated probes may run
// where the console isn't visible. One helper; each probe picks its log tag.
Action<string> Probe(string tag) => m => { Console.WriteLine(m); UnifiedRgb.Core.Log.Info(tag, m); };
try
{
// --openrgb [echo]: OpenRGB bridge test. Ensures a server is running (downloads
// + launches the bundled build if needed), enumerates devices, applies the
// native-coverage skip rules, and with "echo" verifies the write path by
// re-sending each bridged device's CURRENT colors and reading them back
// (zero visible change, full protocol round-trip).
if (args.Length >= 1 && args[0] == "--openrgb")
{
    bool echo = args.Length >= 2 && args[1] == "echo";
    Console.WriteLine("Ensuring an OpenRGB server is available...");
    bool up = UnifiedRgb.Core.Net.OpenRgbManager.EnsureRunningAsync(s => Console.WriteLine($"  {s}"))
        .GetAwaiter().GetResult();
    if (!up) { Console.WriteLine("No server - aborting."); return; }

    using var orgb = UnifiedRgb.Core.Net.OpenRgbClient.Connect();
    int count = orgb.GetControllerCount();
    Console.WriteLine($"Server protocol {orgb.ServerVersion}, {count} device(s):");
    for (int i = 0; i < count; i++)
    {
        var d = orgb.GetControllerData(i);
        Console.WriteLine($"  [{i}] {d.Name}");
        Console.WriteLine($"      type={d.Type} zones={d.Zones.Count} leds={Math.Max(d.LedCount, d.Colors.Length)}");
        Console.WriteLine($"      loc={d.Location}");
        if (echo && d.Colors.Length > 0)
        {
            var cur = d.Colors.Select(c => new Rgb((byte)(c & 0xFF), (byte)(c >> 8 & 0xFF), (byte)(c >> 16 & 0xFF))).ToArray();
            orgb.UpdateLeds(i, cur);
            var back = orgb.GetControllerData(i);
            bool match = back.Colors.SequenceEqual(d.Colors);
            Console.WriteLine($"      echo write+readback: {(match ? "OK" : "MISMATCH")} ({d.Colors.Length} colors)");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Bridge view (after native-coverage skips):");
    var bridged = UnifiedRgb.Core.Net.OpenRgbLink.DetectAll();
    foreach (var b in bridged)
        Console.WriteLine($"  + {b.Name}  ({b.Type}, {b.LedCount} LEDs, {b.Zones.Count} zone(s))");
    Console.WriteLine($"  bridged={bridged.Count}, skipped-as-native={UnifiedRgb.Core.Net.OpenRgbLink.LastSkipped.Count}: " +
                      string.Join(", ", UnifiedRgb.Core.Net.OpenRgbLink.LastSkipped));
    UnifiedRgb.Core.Net.OpenRgbLink.Shutdown();

    // "dedup": turn the skipped devices' detectors off in the managed config,
    // restart the bundled instance, and confirm OpenRGB stops touching them.
    if (args.Contains("dedup") && UnifiedRgb.Core.Net.OpenRgbLink.LastSkipped.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Disabling detectors for natively-driven hardware and restarting...");
        bool changed = UnifiedRgb.Core.Net.OpenRgbManager.DisableDetectors(UnifiedRgb.Core.Net.OpenRgbLink.LastSkipped);
        Console.WriteLine($"  config changed: {changed}");
        if (changed && UnifiedRgb.Core.Net.OpenRgbManager.Restart(s => Console.WriteLine($"  {s}")))
        {
            using var orgb2 = UnifiedRgb.Core.Net.OpenRgbClient.Connect();
            int after = orgb2.GetControllerCount();
            Console.WriteLine($"  after restart: {after} device(s) detected by OpenRGB");
        }
    }
    return;
}

// --staticfade: probe which effect-packet timing bytes disable the RGB Fusion
// firmware's smooth fade between static colors. Cycles red/blue on the
// chipset accent (led 10) with several field variants; watch which one SNAPS.
if (args.Length >= 1 && args[0] == "--staticfade")
{
    var mobo = GigabyteIt5711.TryOpen();
    if (mobo == null) { Console.WriteLine("Motherboard not found."); return; }
    var variants = new (string Name, (int, byte)[]? T)[]
    {
        ("A: current (all timing zero)", null),
        ("B: byte17=1",                  new (int, byte)[] { (17, 1) }),
        ("C: bytes17-24=1,0 pairs",      new (int, byte)[] { (17, 1), (19, 1), (21, 1), (23, 1) }),
        ("D: byte13=0xFF",               new (int, byte)[] { (13, 0xFF) }),
        ("E: bytes17-18=0xFF",           new (int, byte)[] { (17, 0xFF), (18, 0xFF) }),
    };
    foreach (var (name, t) in variants)
    {
        Console.WriteLine($">> variant {name} — watch the CHIPSET accent for 6s (red/blue @1Hz)");
        for (int i = 0; i < 6; i++)
        {
            mobo.SendZoneEffect(10, i % 2 == 0 ? Rgb.Red : Rgb.Blue, t);
            mobo.ApplyNow();
            Sleep(1000);
        }
    }
    Console.WriteLine("done - which variant snapped instantly?");
    mobo.Dispose();
    return;
}

// --pump [samples]: sample the Thermalright pump's identify reply looking for
// live telemetry (pump RPM / coolant temp). ONLY sends the known-safe identify
// command — never fuzz unknown commands at hardware (field lesson: a discovery
// query bricked a Lian Li receiver). Run with the app closed (it owns the LCD).
if (args.Length >= 1 && args[0] == "--pump")
{
    // The shipped exe is UnifiedRGB-v<x.y.z>.exe, not UnifiedRgb.App: match the
    // prefix (and skip ourselves - this CLI shares it).
    bool appRunning;
    {
        var procs = System.Diagnostics.Process.GetProcesses();
        try
        {
            appRunning = procs.Any(p => p.Id != Environment.ProcessId
                && p.ProcessName.StartsWith("UnifiedRgb", StringComparison.OrdinalIgnoreCase));
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }
    if (appRunning)
    { Console.WriteLine("Close UnifiedRGB first — it owns the pump LCD stream."); return; }
    int count = args.Length >= 2 && int.TryParse(args[1], out var k) ? k : 10;
    // Zero samples would reach samples.Min() on an empty list; reject before
    // the LCD handle is even opened.
    if (count < 1) { Console.WriteLine("sample count must be >= 1"); Environment.ExitCode = 2; return; }
    using var lcd = UnifiedRgb.Core.Devices.ThermalrightLcd.TryOpen();
    if (lcd == null) { Console.WriteLine("pump LCD not found"); return; }
    var samples = new List<byte[]>();
    for (int i = 0; i < count; i++)
    {
        var r = lcd.RawReply(64);
        samples.Add(r);
        Console.WriteLine($"{i,2}: {Convert.ToHexString(r)}");
        if (i + 1 < count) Sleep(1500);
    }
    int len = samples.Min(s => s.Length);
    if (len == 0) { Console.WriteLine("no reply bytes"); return; }
    var changing = Enumerable.Range(0, len)
        .Where(b => samples.Select(s => s[b]).Distinct().Count() > 1).ToList();
    Console.WriteLine(changing.Count == 0
        ? "reply is STATIC across samples — no live telemetry in the identify reply"
        : $"changing byte offsets: {string.Join(", ", changing)}");
    foreach (var b in changing)
        Console.WriteLine($"  [{b}]: {string.Join(" ", samples.Select(s => s[b].ToString("X2")))}");
    for (int b = 0; b + 1 < len; b++)
    {
        var vals = samples.Select(s => s[b] | (s[b + 1] << 8)).ToList();
        if (vals.All(v => v is > 1500 and < 4500) && vals.Distinct().Count() > 1)
            Console.WriteLine($"  pump-rpm candidate LE16 @[{b}]: {string.Join(",", vals)}");
    }
    for (int b = 0; b < len; b++)
    {
        var vals = samples.Select(s => (int)s[b]).ToList();
        if (vals.All(v => v is > 20 and < 70))
            Console.WriteLine($"  coolant-temp candidate u8 @[{b}]: {string.Join(",", vals)}");
    }
    return;
}

// --fanctl [fanIndex]: fan-control write probe (elevated). Tests the PWM duty
// scheme on ONE fan — default index 1 = Fan 2 / SYS_FAN1, the case fan, NEVER
// the pump — by stepping raw register values and watching the tach respond.
// Original value restores in finally no matter what.
if (args.Length >= 1 && args[0] == "--fanctl")
{
    var Say = Probe("fanctl-probe");
    int fan = args.Length >= 2 && int.TryParse(args[1], out var f) ? f : 1;
    var chips = UnifiedRgb.Core.Sensors.IteSuperIo.OpenAll();
    var sio = chips.FirstOrDefault();
    if (sio == null) { Say("no ITE chip (needs admin)"); return; }
    try
    {
        int Rpm() { try { return sio.Read().FanRpm[fan] ?? -1; } catch { return -1; } }
        // Candidate registers on this family: ctrl 0x15-0x17/0x88-0x8A (bit7 =
        // SmartFan auto for fans 1-3), separate duty regs 0x63/0x6B/0x73 for
        // fans 1-3 (linux it87 "newer autopwm" layout).
        byte[] ctrlRegs = { 0x15, 0x16, 0x17, 0x88, 0x89, 0x8A };
        byte[] dutyRegs = { 0x63, 0x6B, 0x73 };
        if ((uint)fan >= (uint)ctrlRegs.Length) { Say($"fan index must be 0-{ctrlRegs.Length - 1}"); return; }
        byte ctrl = ctrlRegs[fan];
        int origCtrl = sio.ReadEcRaw(ctrl);
        int origDuty = fan < dutyRegs.Length ? sio.ReadEcRaw(dutyRegs[fan]) : -1;
        if (origCtrl < 0) { Say($"fan {fan + 1}: ctrl read failed"); return; }
        Say($"fan {fan + 1}: ctrl 0x{ctrl:X2}=0x{origCtrl:X2}, duty-reg 0x{(fan < dutyRegs.Length ? dutyRegs[fan] : 0):X2}={(origDuty < 0 ? "n/a" : $"0x{origDuty:X2}")}, rpm {Rpm()}");
        try
        {
            // Recipe A: write duty straight into the ctrl reg (bit7 cleared).
            foreach (byte v in new byte[] { 0x40, 0x7F })
            {
                sio.WriteEcRaw(ctrl, v);
                Sleep(4500);
                Say($"  A: ctrl<-0x{v:X2} -> rpm {Rpm()}  (ctrl reads 0x{sio.ReadEcRaw(ctrl):X2})");
            }
            // Recipe B: 8-bit values with bit7 set (plain 0-255 duty if the
            // reg is a direct duty register on this chip).
            sio.WriteEcRaw(ctrl, 0xFF);
            Sleep(4500);
            Say($"  B: ctrl<-0xFF -> rpm {Rpm()}  (ctrl reads 0x{sio.ReadEcRaw(ctrl):X2})");
            // Recipe C: bit7 clear in ctrl selects software mode, duty lives
            // in the separate duty reg.
            if (fan < dutyRegs.Length && origDuty >= 0)
            {
                sio.WriteEcRaw(ctrl, (byte)(origCtrl & 0x7F));
                sio.WriteEcRaw(dutyRegs[fan], 0x40);
                Sleep(4500);
                Say($"  C: ctrl&0x7F + duty<-0x40 -> rpm {Rpm()}  (duty reads 0x{sio.ReadEcRaw(dutyRegs[fan]):X2})");
                sio.WriteEcRaw(dutyRegs[fan], 0xFF);
                Sleep(4500);
                Say($"  C: duty<-0xFF -> rpm {Rpm()}");
            }

            // Recipe D (Gigabyte): the firmware EC re-drives the fans, so
            // nothing above moves a fan until vendor control is released via
            // the ECIO interface on the secondary chip.
            var second = chips.Skip(1).FirstOrDefault();
            if (second != null)
            {
                var ec = new UnifiedRgb.Core.Sensors.GigabyteEcio(second);
                Say($"  D: ECIO status raw 0x{second.PioInb(0x3F4):X2}, version {ec.ControllerVersion}, vendor control {ec.VendorControlEnabled?.ToString() ?? "n/a"}");
                if (ec.VendorControlEnabled == true)
                {
                    try
                    {
                        if (ec.SetVendorControl(false))
                        {
                            sio.WriteEcRaw(ctrl, 0x40);
                            Sleep(4500);
                            Say($"  D: vendor OFF + ctrl<-0x40 -> rpm {Rpm()}");
                            sio.WriteEcRaw(ctrl, 0xFF);
                            Sleep(4500);
                            Say($"  D: ctrl<-0xFF -> rpm {Rpm()}");
                        }
                        else Say("  D: vendor-disable write failed");
                    }
                    finally
                    {
                        sio.WriteEcRaw(ctrl, (byte)origCtrl);
                        ec.SetVendorControl(true);
                        Sleep(1500);
                        Say($"  D: restored + vendor ON -> rpm {Rpm()}");
                    }
                }
            }
            else Say("  D: no secondary chip — ECIO path not applicable");
        }
        finally
        {
            sio.WriteEcRaw(ctrl, (byte)origCtrl);
            if (fan < dutyRegs.Length && origDuty >= 0) sio.WriteEcRaw(dutyRegs[fan], (byte)origDuty);
            Sleep(4500);
            Say($"restored ctrl 0x{origCtrl:X2}{(origDuty >= 0 ? $", duty 0x{origDuty:X2}" : "")} -> rpm {Rpm()}");
        }
        Say("done");
    }
    finally { foreach (var c in chips) c.Dispose(); }
    return;
}

// --lhmfan [index]: prove LibreHardwareMonitor sees + controls the board fans
// (elevated). Lists them, then drives the given index (default 1 = case fan,
// NOT the pump) to 100% then back to auto.
if (args.Length >= 1 && args[0] == "--lhmfan")
{
    var Say = Probe("lhmfan-probe");
    using var lhm = UnifiedRgb.Core.Sensors.LhmFans.TryOpen();
    if (lhm == null) { Say("LHM found no motherboard fans/temps (needs admin)"); return; }
    void Dump(string tag)
    {
        lhm.Refresh();
        var f = lhm.Fans;
        Say($"-- {tag} --");
        for (int i = 0; i < f.Count; i++)
            Say($"  [{i}] {f[i].Name}: {f[i].CurrentRpm?.ToString() ?? "-"} RPM  control={(f[i].CanControl ? "yes" : "no")}");
        foreach (var t in lhm.Temps) Say($"  temp {t.Name}: {t.Value?.ToString("0") ?? "-"} C");
    }
    Dump("detected");
    int idx = args.Length >= 2 && int.TryParse(args[1], out var k) ? k : 1;
    // Wide duty range + long settle so a real change is unmistakable vs a
    // fan's inertia/ramp. Only the target fan's RPM is what matters.
    int Rpm() { lhm.Refresh(); return lhm.Fans[idx].CurrentRpm ?? -1; }
    if (idx < lhm.Fans.Count && lhm.Fans[idx].CanControl)
    {
        Say($"[{idx}] {lhm.Fans[idx].Name} baseline {Rpm()} RPM");
        lhm.SetDuty(idx, 100); Sleep(12000); Say($"  100% -> {Rpm()} RPM");
        lhm.SetDuty(idx, 20);  Sleep(12000); Say($"   20% -> {Rpm()} RPM");
        lhm.SetDuty(idx, 100); Sleep(12000); Say($"  100% -> {Rpm()} RPM");
        lhm.Restore(idx);      Sleep(8000);  Say($"  auto -> {Rpm()} RPM");
    }
    else Say($"index {idx} not controllable");
    Say("done");
    return;
}

// --gbecdump [startHex] [count]: dump the Gigabyte MMIO window(s) so we can
// eyeball where the fan-control block lives (elevated).
if (args.Length >= 1 && args[0] == "--gbecdump")
{
    int start = args.Length >= 2 ? Convert.ToInt32(args[1], 16) : 0x800;
    int count = args.Length >= 3 ? int.Parse(args[2]) : 0x300;
    Console.WriteLine($"dumping 0x{start:X}..0x{start + count:X} — see [gbec-dump] in the log");
    UnifiedRgb.Core.Sensors.GigabyteIsaBridge.DumpWindows(start, count);
    return;
}

// --fanmap: write 100% to each PWM register in turn and watch which tach
// responds — maps PWM channel -> physical fan (elevated). 100% is safe on
// every header, pump included; each register restores before the next.
if (args.Length >= 1 && args[0] == "--fanmap")
{
    var Say = Probe("fanmap-probe");
    var chips = UnifiedRgb.Core.Sensors.IteSuperIo.OpenAll();
    var sio = chips.FirstOrDefault();
    if (sio == null) { Say("no ITE chip (needs admin)"); return; }
    try
    {
        string Rpms() { var r = sio.Read(); return string.Join(", ", r.FanRpm.Select(f => f?.ToString() ?? "-")); }
        byte[] pwmRegs = { 0x15, 0x16, 0x17, 0x88, 0x89, 0x8A };
        Say($"baseline rpms: [{Rpms()}]");

        // Gigabyte firmware owns the fans until told otherwise — take over
        // via the ISA-bridge MMIO path, sweep, then hand everything back.
        using var bridge = UnifiedRgb.Core.Sensors.GigabyteIsaBridge.TryOpen();
        Say(bridge == null
            ? "isa-bridge: not available on this board"
            : $"isa-bridge: vendor control = {bridge.VendorControlEnabled?.ToString() ?? "unreadable"}");
        bool tookOver = false;
        try
        {
            if (bridge != null && bridge.VendorControlEnabled == true)
            {
                tookOver = bridge.SetVendorControl(false);
                Say($"takeover: {(tookOver ? "OK — we own the fans" : "FAILED")}");
            }
            for (int i = 0; i < pwmRegs.Length; i++)
            {
                int orig = sio.ReadEcRaw(pwmRegs[i]);
                if (orig < 0) { Say($"pwm[{i}] 0x{pwmRegs[i]:X2}: read failed"); continue; }
                sio.WriteEcRaw(pwmRegs[i], 0xFF);
                // Sleep() throws on Ctrl+C: restore THIS register in a finally.
                // The outer finally only hands vendor control back, which on a
                // board without the ISA bridge re-drives nothing.
                try
                {
                    Sleep(4000);
                    Say($"pwm[{i}] 0x{pwmRegs[i]:X2} (was 0x{orig:X2}) at 0xFF -> [{Rpms()}]");
                }
                finally { sio.WriteEcRaw(pwmRegs[i], (byte)orig); }
                Sleep(2500);
            }
            Say($"all regs restored -> [{Rpms()}]");
        }
        finally
        {
            if (tookOver) bridge!.SetVendorControl(true);
        }
        Sleep(2000);
        Say($"vendor control restored -> [{Rpms()}]");
        Say("done");
    }
    finally { foreach (var c in chips) c.Dispose(); }
    return;
}

// --sioldn: read-only dump of every logical device's activate bit + BARs on
// both Super-I/O chips, plus a whitelist test of the Gigabyte ECIO ports on
// each handle (elevated).
if (args.Length >= 1 && args[0] == "--sioldn")
{
    var Say = Probe("sioldn-probe");
    var chips = UnifiedRgb.Core.Sensors.IteSuperIo.OpenAll();
    if (chips.Count == 0) { Say("no ITE chips (needs admin)"); return; }
    try
    {
        foreach (var c in chips)
        {
            Say($"chip 0x{c.ChipId:X4}: ECIO 0x3F4 via this handle -> {(c.PioInb(0x3F4) is int s and >= 0 ? $"0x{s:X2}" : "denied")}");
            foreach (var (ldn, act, b0, b1) in c.DumpLdns())
                if (act > 0 || (b0 is > 0x100 and < 0xFFFF) || (b1 is > 0x100 and < 0xFFFF))
                    Say($"  ldn 0x{ldn:X2}: active={act} bar0=0x{b0:X4} bar1=0x{b1:X4}");
        }
        Say("done");
    }
    finally { foreach (var c in chips) c.Dispose(); }
    return;
}

// --gpufan0: why doesn't level 0 stop the fans? Dump cooler state, try
// level 0 manual, level 30 manual, then restore auto (elevated for writes).
if (args.Length >= 1 && args[0] == "--gpufan0")
{
    var g0 = UnifiedRgb.Core.Native.NvApi.EnumGpus().FirstOrDefault();
    if (g0.Handle == IntPtr.Zero) { Console.WriteLine("no NVIDIA GPU"); return; }
    void Dump(string tag) => Console.WriteLine($"-- {tag} --\n  {UnifiedRgb.Core.Native.NvApi.DebugFanState(g0.Handle)}");
    Dump("initial");
    try
    {
        Console.WriteLine($"set level=0 mode=manual: rc={UnifiedRgb.Core.Native.NvApi.DebugSetFanLevel(g0.Handle, 0, 1)}");
        Sleep(8000);
        Dump("after 0% (8s)");
        Console.WriteLine($"set level=30 mode=manual: rc={UnifiedRgb.Core.Native.NvApi.DebugSetFanLevel(g0.Handle, 30, 1)}");
        Sleep(6000);
        Dump("after 30% (6s)");
    }
    finally
    {
        Console.WriteLine($"restore auto: rc={UnifiedRgb.Core.Native.NvApi.DebugSetFanLevel(g0.Handle, 0, 0)}");
        Sleep(4000);
        Dump("auto");
    }
    return;
}

// --gpufanctl: prove GPU fan control (no elevation): 70% -> read -> auto.
if (args.Length >= 1 && args[0] == "--gpufanctl")
{
    var g = UnifiedRgb.Core.Native.NvApi.EnumGpus().FirstOrDefault();
    if (g.Handle == IntPtr.Zero) { Console.WriteLine("no NVIDIA GPU"); return; }
    string Rpms() => string.Join(", ", UnifiedRgb.Core.Native.NvApi.GetGpuFanRpms(g.Handle) ?? Array.Empty<int>());
    Console.WriteLine($"{g.Name}: controllable={UnifiedRgb.Core.Native.NvApi.CanControlGpuFans(g.Handle)}");
    Console.WriteLine($"baseline: [{Rpms()}]");
    try
    {
        Console.WriteLine($"set 70%: {UnifiedRgb.Core.Native.NvApi.SetGpuFanDuty(g.Handle, 70)}");
        Sleep(6000);
        Console.WriteLine($"at 70%: [{Rpms()}]");
        Console.WriteLine($"set 35%: {UnifiedRgb.Core.Native.NvApi.SetGpuFanDuty(g.Handle, 35)}");
        Sleep(6000);
        Console.WriteLine($"at 35%: [{Rpms()}]");
    }
    finally
    {
        Console.WriteLine($"restore auto: {UnifiedRgb.Core.Native.NvApi.RestoreGpuFanAuto(g.Handle)}");
        Sleep(5000);
        Console.WriteLine($"auto: [{Rpms()}]");
    }
    return;
}

// --gpu: dump GPU temp + per-fan RPMs via NvAPI (no elevation needed).
if (args.Length >= 1 && args[0] == "--gpu")
{
    foreach (var g in UnifiedRgb.Core.Native.NvApi.EnumGpus())
    {
        var fans = UnifiedRgb.Core.Native.NvApi.GetGpuFanRpms(g.Handle);
        Console.WriteLine($"{g.Name}: temp={UnifiedRgb.Core.Native.NvApi.GetGpuTemperature(g.Handle)?.ToString() ?? "-"} °C, " +
            $"load={UnifiedRgb.Core.Native.NvApi.GetGpuLoad(g.Handle)?.ToString() ?? "-"}%, " +
            $"volt={UnifiedRgb.Core.Native.NvApi.GetGpuCoreVoltage(g.Handle)?.ToString("0.000") ?? "-"} V ({UnifiedRgb.Core.Native.NvApi.DebugVoltScan(g.Handle)}), " +
            $"fans=[{(fans == null ? "none" : string.Join(", ", fans.Select(f => $"{f} rpm")))}]");
    }
    return;
}

// --superio: exercise the production ITE Super-I/O path and dump readings
// (results to the log too, since this needs elevation and the console may
// not be visible).
if (args.Length >= 1 && args[0] == "--superio")
{
    var Say = Probe("superio-probe");

    // First: raw enumerate BOTH Super-I/O slots — some boards (Gigabyte
    // especially) put extra fan headers on a second ITE chip at 0x4E.
    {
        var blob = typeof(UnifiedRgb.Core.Sensors.IteSuperIo).Assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith("LpcIO.bin"))
            .Select(n => { using var s = typeof(UnifiedRgb.Core.Sensors.IteSuperIo).Assembly.GetManifestResourceStream(n)!; var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); })
            .FirstOrDefault();
        using var raw = blob != null ? UnifiedRgb.Core.Native.PawnIO.LoadModule(blob) : null;
        if (raw != null)
        {
            var none = Array.Empty<ulong>();
            for (ulong slot = 0; slot < 2; slot++)
            {
                if (raw.Execute("ioctl_select_slot", new[] { slot }, none) < 0) { Say($"slot {slot}: select failed"); continue; }
                ulong port = slot == 0 ? 0x2Eul : 0x4Eul;
                byte last = slot == 0 ? (byte)0x55 : (byte)0xAA;
                bool ok = true;
                foreach (byte b in new byte[] { 0x87, 0x01, 0x55, last })
                    if (raw.Execute("ioctl_pio_outb", new ulong[] { port, b }, none) < 0) { ok = false; break; }
                if (!ok) { Say($"slot {slot}: enter-config failed"); continue; }
                var o = new ulong[1];
                int hi = raw.Execute("ioctl_superio_inb", new ulong[] { 0x20 }, o) >= 0 ? (int)(o[0] & 0xFF) : -1;
                int lo = raw.Execute("ioctl_superio_inb", new ulong[] { 0x21 }, o) >= 0 ? (int)(o[0] & 0xFF) : -1;
                Say($"slot {slot}: chip id 0x{((hi & 0xFF) << 8) | (lo & 0xFF):X4}");
                raw.Execute("ioctl_superio_outb", new ulong[] { 0x02, 0x02 }, none);
            }
        }
    }

    var chips = UnifiedRgb.Core.Sensors.IteSuperIo.OpenAll();
    if (chips.Count == 0) { Say("OpenAll found nothing — see [superio] log lines above"); return; }
    try
    {
        Say($"opened {chips.Count} chip(s): {string.Join(", ", chips.Select(c => $"0x{c.ChipId:X4}"))}");
        for (int sweep = 0; sweep < 3; sweep++)
        {
            foreach (var sio in chips)
            {
                var r = sio.Read();
                Say($"sweep {sweep} chip 0x{sio.ChipId:X4}: temps [{string.Join(", ", r.TempsC.Select(t => t?.ToString("0") ?? "-"))}] °C");
                Say($"         fans  [{string.Join(", ", r.FanRpm.Select(f => f?.ToString() ?? "-"))}] rpm");
                Say($"         duty  [{string.Join(", ", r.FanDutyPct.Select(d => d?.ToString() ?? "-"))}] %");
            }
            Sleep(1200);
        }
        Say("done");
    }
    finally { foreach (var sio in chips) sio.Dispose(); }
    return;
}

// --keys [seconds]: low-level keyboard hook smoke test. Prints press/release
// events (VK codes) as they happen.
if (args.Length >= 1 && args[0] == "--keys")
{
    int seconds = args.Length >= 2 ? int.Parse(args[1]) : 8;
    Console.WriteLine($"Watching keys for {seconds}s (type something!)...");
    var ev = new UnifiedRgb.Core.Input.KeyboardTap.KeyEvent[64];
    var seen = new HashSet<(int, double)>();
    var until = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < until)
    {
        UnifiedRgb.Core.Input.KeyboardTap.Touch();
        int n = UnifiedRgb.Core.Input.KeyboardTap.Snapshot(ev);
        for (int i = 0; i < n; i++)
            if (seen.Add((ev[i].Vk, ev[i].Down)))
                Console.WriteLine($"  vk=0x{ev[i].Vk:X2} down@{ev[i].Down:0.00}");
        Sleep(50);
    }
    return;
}

// --audio [seconds]: WASAPI loopback + FFT smoke test. Prints live band bars
// for whatever is playing on the default output device.
if (args.Length >= 1 && args[0] == "--audio")
{
    int seconds = args.Length >= 2 ? int.Parse(args[1]) : 8;
    Console.WriteLine($"Capturing system audio for {seconds}s (play something!)...");
    var until = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < until)
    {
        AudioAnalyzer.Touch();
        var sb = new System.Text.StringBuilder();
        for (int b = 0; b < AudioAnalyzer.BandCount; b++)
            sb.Append(" .:-=+*#%@"[Math.Clamp((int)(AudioAnalyzer.Band(b) * 9.99), 0, 9)]);
        Console.WriteLine($"[{sb}]  level={AudioAnalyzer.Level:0.00} bass={AudioAnalyzer.Bass:0.00}");
        Sleep(150);
    }
    return;
}

// --argb <header 1-4> <ledCount> <RRGGBB>: light the first ledCount LEDs on a
// header via per-LED direct streaming (used to find the AIO's header + count).
if (args.Length == 4 && args[0] == "--argb")
{
    var mobo = GigabyteIt5711.TryOpen();
    if (mobo == null) { Console.WriteLine("Motherboard not found."); return; }
    int header = int.Parse(args[1]), count = int.Parse(args[2]);
    var color = Rgb.FromHex(args[3]);
    var frame = Enumerable.Repeat(color, count).ToList();
    Console.WriteLine($"Streaming {count} LEDs of {color} to ARGB header {header}...");
    mobo.SetHeaderLeds(header, frame);
    mobo.Dispose();
    return;
}

// --argbwalk <header> <ledCount>: light LEDs one-per-color so you can count them.
if (args.Length == 3 && args[0] == "--argbwalk")
{
    var mobo = GigabyteIt5711.TryOpen();
    if (mobo == null) { Console.WriteLine("Motherboard not found."); return; }
    int header = int.Parse(args[1]), count = int.Parse(args[2]);
    var frame = new List<Rgb>();
    var palette = new[] { Rgb.Red, Rgb.Green, Rgb.Blue, Rgb.White, new Rgb(255,128,0) };
    for (int i = 0; i < count; i++) frame.Add(palette[i % palette.Length]);
    Console.WriteLine($"Streaming {count} rainbow-index LEDs to ARGB header {header} (count the color groups)...");
    mobo.SetHeaderLeds(header, frame);
    mobo.Dispose();
    return;
}

// --argball: full direct setup, distinct color per header (H1 red, H2 green,
// H3 blue, H4 white) to find which header the AIO fans are on.
if (args.Length >= 1 && args[0] == "--argball")
{
    var mobo = GigabyteIt5711.TryOpen();
    if (mobo == null) { Console.WriteLine("Motherboard not found."); return; }
    Console.WriteLine("H1=red H2=green H3=blue H4=white (30 LEDs each)...");
    mobo.TestAllHeaders(30, new[] { Rgb.Red, Rgb.Green, Rgb.Blue, Rgb.White });
    mobo.Dispose();
    return;
}

// --argbblocks <header> <total>: color LEDs in blocks of 10 (red, green, blue,
// yellow, cyan, white, ...) to read off LEDs-per-fan and total count.
if (args.Length == 3 && args[0] == "--argbblocks")
{
    var mobo = GigabyteIt5711.TryOpen();
    if (mobo == null) { Console.WriteLine("Motherboard not found."); return; }
    int header = int.Parse(args[1]), total = int.Parse(args[2]);
    var blocks = new[] { Rgb.Red, Rgb.Green, Rgb.Blue, Rgb.FromHex("FFFF00"),
                         Rgb.FromHex("00FFFF"), Rgb.White, Rgb.FromHex("FF8000"), Rgb.FromHex("FF00FF") };
    var frame = new List<Rgb>();
    for (int i = 0; i < total; i++) frame.Add(blocks[(i / 10) % blocks.Length]);
    Console.WriteLine($"Header {header}: blocks of 10 -> red,green,blue,yellow,cyan,white,orange,magenta");
    mobo.SetHeaderLeds(header, frame);
    mobo.Dispose();
    return;
}

// --argbrange <header> <total> <start> <end>: light LEDs [start,end) white,
// the rest off. Isolates exactly which physical LEDs map to which fan.
if (args.Length == 5 && args[0] == "--argbrange")
{
    var mobo = GigabyteIt5711.TryOpen();
    if (mobo == null) { Console.WriteLine("Motherboard not found."); return; }
    int header = int.Parse(args[1]), total = int.Parse(args[2]);
    int start = int.Parse(args[3]), end = int.Parse(args[4]);
    var frame = new List<Rgb>();
    for (int i = 0; i < total; i++) frame.Add(i >= start && i < end ? Rgb.White : Rgb.Black);
    Console.WriteLine($"Header {header}: LEDs {start}..{end - 1} WHITE, rest off (total {total})");
    mobo.SetHeaderLeds(header, frame);
    mobo.Dispose();
    return;
}

// --lcd <rawfile>: push a raw 240x320 RGB565 frame (153,600 bytes) to the pump screen.
if (args.Length == 2 && args[0] == "--lcd")
{
    var lcd = ThermalrightLcd.TryOpen();
    if (lcd == null) { Console.WriteLine("Thermalright LCD (0416:5302) not found."); return; }
    var raw = File.ReadAllBytes(args[1]);   // RGB565, 240x320 = 153600 bytes
    Console.WriteLine($"Sending {raw.Length}-byte RGB565 frame x5 to re-sync + display...");
    for (int i = 0; i < 5; i++)
    {
        byte pm = lcd.Handshake();
        lcd.ShowFrame(raw);
        Console.WriteLine($"  pass {i + 1}: handshake pm={pm}");
        Sleep(300);
    }
    lcd.Dispose();
    return;
}

// --gpurgb [RRGGBB]: detect the MSI GPU over NvAPI I2C and set a color.
// (Was a second "--gpu" handler - unreachable behind the telemetry dump above.)
if (args.Length >= 1 && args[0] == "--gpurgb")
{
    var gpu = UnifiedRgb.Core.Devices.MsiGpu.TryOpen();
    if (gpu == null) { Console.WriteLine("MSI GPU not found (NvAPI/I2C probe failed)."); return; }
    Console.WriteLine($"Detected: {gpu.Name}");
    if (args.Length > 1)
    {
        var col = Rgb.FromHex(args[1]);
        Console.WriteLine($"Setting GPU to {col}...");
        gpu.SetColors(new[] { col });
        Console.WriteLine("Done.");
    }
    return;
}

// --ram [RRGGBB]: detect ENE DRAM sticks over PawnIO SMBus and set a color.
if (args.Length >= 1 && args[0] == "--ram")
{
    Console.WriteLine($"PawnIO available: {UnifiedRgb.Core.Native.PawnIO.IsAvailable}");
    var sticks = UnifiedRgb.Core.Devices.EneDram.DetectAll();
    Console.WriteLine($"Detected {sticks.Count} ENE DRAM stick(s).");
    foreach (var s in sticks)
        Console.WriteLine($"  {s.Name} - {s.LedCount} LEDs");
    if (args.Length > 1 && args[1] == "diag")
    {
        foreach (var s in sticks.OfType<UnifiedRgb.Core.Devices.EneDram>())
            Console.WriteLine(s.Diagnose());
        return;
    }
    if (sticks.Count > 0 && args.Length > 1)
    {
        var col = Rgb.FromHex(args[1]);
        Console.WriteLine($"Setting sticks to {col}...");
        foreach (var s in sticks) s.SetAll(col);
        Console.WriteLine("Done.");
    }
    return;
}

// --cputemp: read the AMD Zen CPU temperature via the signed PawnIO module.
if (args.Length >= 1 && args[0] == "--cputemp")
{
    Console.WriteLine($"PawnIO available: {UnifiedRgb.Core.Native.PawnIO.IsAvailable}");
    Console.WriteLine($"Diagnose: {UnifiedRgb.Core.Sensors.RyzenCpuTemperature.Diagnose()}");
    using var t = UnifiedRgb.Core.Sensors.RyzenCpuTemperature.TryCreate();
    if (t == null) { Console.WriteLine("Could not create Ryzen temp reader."); return; }
    for (int i = 0; i < 5; i++)
    {
        Console.WriteLine($"  CPU (Tctl): {t.ReadCelsius():0.0} °C");
        Sleep(500);
    }
    return;
}

// --lcdinfo: dump the pump's identify/telemetry reply to look for a temp field.
if (args.Length >= 1 && args[0] == "--lcdinfo")
{
    var lcd = ThermalrightLcd.TryOpen();
    if (lcd == null) { Console.WriteLine("Thermalright LCD not found."); return; }
    for (int pass = 0; pass < 4; pass++)
    {
        var rx = lcd.RawReply(40);
        Console.Write($"reply[{pass}]:");
        for (int i = 0; i < rx.Length; i++) Console.Write($" {i}={rx[i]}");
        Console.WriteLine();
        Sleep(700);
    }
    lcd.Dispose();
    return;
}

// --mouse <RRGGBB>: diagnose the G403 discovery and set a color.
if (args.Length >= 1 && args[0] == "--mouse")
{
    var mouse = LogitechG403.TryOpen();
    if (mouse == null) { Console.WriteLine("G403 not found."); return; }
    Console.WriteLine("G403: " + mouse.DiagnosticInfo());
    var col = args.Length > 1 ? Rgb.FromHex(args[1]) : Rgb.FromHex("FF00FF");
    Console.WriteLine($"Setting mouse to {col}...");
    mouse.SetColors(new[] { col });
    mouse.Dispose();
    return;
}

// --scan: probe the motherboard's ARGB headers for connected LED segments.
if (args.Length > 0 && args[0] == "--scan")
{
    var mobo = GigabyteIt5711.TryOpen();
    if (mobo == null) { Console.WriteLine("Motherboard not found."); return; }
    Console.WriteLine("Controller: " + mobo.DiagnosticInfo());
    Console.WriteLine("Scanning ARGB headers (takes a few seconds)...");
    var scans = mobo.ScanArgbHeaders();
    if (scans.Count == 0) Console.WriteLine("No addressable segments detected on any header.");
    foreach (var s in scans)
        Console.WriteLine($"  ARGB Header {s.Header}: {s.Segments} segment(s), " +
                          $"{s.TotalLeds} LEDs total  [{string.Join(", ", s.SegmentLeds)}]");
    mobo.Dispose();
    return;
}

// UnifiedRgb CLI test harness.
//   (no args)          list detected devices
//   <RRGGBB>           set every LED on every device to that color
//   <deviceIndex> <hex> set one device

// Every --option handler returned above, so a --token reaching here is either
// unknown or a known option with the wrong argument count (the exact-arity
// handlers such as --argb/--lcd simply don't match on a miscount). Reject it
// here, before the generic path opens every device and reports "Done.".
if (args.Length > 0 && args[0].StartsWith("--"))
{ Console.WriteLine($"Unknown option or wrong argument count: {string.Join(' ', args)}"); Environment.ExitCode = 2; return; }

using var manager = new DeviceManager();
manager.DetectAll();

if (manager.Devices.Count == 0)
{
    Console.WriteLine("No supported devices detected.");
    return;
}

Console.WriteLine($"Detected {manager.Devices.Count} device(s):");
for (int i = 0; i < manager.Devices.Count; i++)
{
    var d = manager.Devices[i];
    Console.WriteLine($"  [{i}] {d.Name} ({d.Vendor}, {d.Type}) - {d.LedCount} LEDs, {d.Zones.Count} zone(s)");
    foreach (var z in d.Zones)
        Console.WriteLine($"        zone '{z.Name}' @ {z.Offset} x{z.Count}");
}

if (args.Length == 0) return;

if (args.Length == 1)
{
    if (!Rgb.TryFromHex(args[0], out var color)) { Console.WriteLine($"Not a color: {args[0]} (want RRGGBB)"); Environment.ExitCode = 2; return; }
    Console.WriteLine($"Setting all devices to {color}...");
    foreach (var d in manager.Devices) d.SetAll(color);
}
else if (args.Length == 2 && int.TryParse(args[0], out int idx))
{
    if (idx < 0 || idx >= manager.Devices.Count) { Console.WriteLine($"No device [{idx}]"); Environment.ExitCode = 2; return; }
    if (!Rgb.TryFromHex(args[1], out var color)) { Console.WriteLine($"Not a color: {args[1]} (want RRGGBB)"); Environment.ExitCode = 2; return; }
    Console.WriteLine($"Setting [{idx}] {manager.Devices[idx].Name} to {color}...");
    manager.Devices[idx].SetAll(color);
}

Console.WriteLine("Done.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
    Environment.ExitCode = 130;
}

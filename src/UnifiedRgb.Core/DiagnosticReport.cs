using System.Diagnostics;
using Microsoft.Win32;
using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Core;

/// <summary>Collects the full hardware survey (system/DIMMs/GPUs/RGB software/
/// HID/USB/NvAPI probes/SMBus sweep) used by both the standalone diagnostic
/// tool and the app's Settings → Support page. Read-only throughout.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class DiagnosticReport
{
    public static string Collect(Action<string>? progress = null)
    {
        var sb = new System.Text.StringBuilder();
        void Say(string line = "") { sb.AppendLine(line); }
        void Section(string name) { progress?.Invoke(name); }

        bool admin = IsAdmin();
        Say("==============================================");
        Say(" UnifiedRGB Diagnostic Report");
        Say($" {DateTime.Now:yyyy-MM-dd HH:mm}   admin={admin}");
        Say($" exe: {Environment.ProcessPath}");   // update swaps need this path writable
        Say("==============================================");

        // Auto-triage: surface the usual snags up top so a glance at the report
        // says what's wrong, instead of eyeballing the whole survey.
        Section("issues");
        Say();
        Say("--- DETECTED ISSUES ---");
        var issues = new List<string>();
        if (!admin)
            issues.Add("Not elevated — CPU temp, RAM RGB and motherboard fan control need admin. Turn on 'Start with Windows' (runs elevated), or relaunch as admin.");
        try
        {
            if (!PawnIO.IsAvailable)
                issues.Add("PawnIO driver NOT installed — CPU temperature, RAM lighting and motherboard fans won't show. Install it in Settings → Sensor driver.");
        }
        catch { }
        try
        {
            string[] conflicts = { "icue", "corsair", "signalrgb", "synapse", "razer", "armourycrate",
                                   "aura", "lconnect", "steelseriesgg", "openrgb" };
            var fighting = ConflictingProcesses(conflicts);
            if (fighting.Count > 0)
                issues.Add($"Vendor RGB software running ({string.Join(", ", fighting)}) — it can grab a device before UnifiedRGB and hold it; close it if a device won't respond.");
        }
        catch { }
        string exePath = Environment.ProcessPath ?? "";
        if (exePath.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase) || exePath.Contains("(1)"))
            issues.Add($"Running from a Downloads copy ({exePath}) — updates replace THIS file; keep it in one stable spot so self-updates stick.");
        if (exePath.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase))
            issues.Add("Running a Debug build — self-updates are skipped for dev builds.");
        Say(issues.Count == 0 ? "(none detected — the basics look healthy)"
                              : string.Join("\r\n", issues.Select(i => "• " + i)));

        Section("system");
        Say();
        Say("--- SYSTEM ---");
        try
        {
            Say($"OS: {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            Say($"CPU: {cpu?.GetValue("ProcessorNameString")}");
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            Say($"Board: {bios?.GetValue("BaseBoardManufacturer")} {bios?.GetValue("BaseBoardProduct")}");
            Say($"System: {bios?.GetValue("SystemManufacturer")} {bios?.GetValue("SystemProductName")}  BIOS {bios?.GetValue("BIOSVersion")}");
        }
        catch (Exception ex) { Say($"(system info failed: {ex.Message})"); }

        Section("memory");
        Say();
        Say("--- MEMORY (DIMMs) ---");
        Say(Ps("Get-CimInstance Win32_PhysicalMemory | ForEach-Object { '{0} | {1} | {2} GB | {3} MT/s' -f $_.Manufacturer, $_.PartNumber.Trim(), ($_.Capacity/1GB), $_.Speed }"));

        Section("gpus");
        Say();
        Say("--- GPUs (Windows) ---");
        Say(Ps("Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name + '  [' + $_.PNPDeviceID + ']' }"));

        Section("rgb software");
        Say();
        Say("--- RGB SOFTWARE RUNNING (potential conflicts) ---");
        string[] rgbSoftware = { "icue", "corsair", "signalrgb", "openrgb", "armoury", "lightingservice",
                                 "mystic", "dragoncenter", "lconnect", "razer", "synapse", "aura", "trcc",
                                 "lianli", "nzxt", "wraith", "polychrome", "steelseries" };
        var running = ProcessNames()
            .Where(n => rgbSoftware.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Distinct().OrderBy(n => n).ToList();
        Say(running.Count == 0 ? "(none detected)" : string.Join(", ", running));

        Section("hid devices");
        Say();
        Say("--- HID DEVICES (all) ---");
        try
        {
            var all = HidNative.FindAll()
                .GroupBy(h => (h.VendorId, h.ProductId))
                .OrderBy(g => g.Key.VendorId).ThenBy(g => g.Key.ProductId);
            foreach (var g in all)
            {
                string name = g.Select(h => h.Product).FirstOrDefault(s => s.Length > 0) ?? "";
                string mfr = g.Select(h => h.Manufacturer).FirstOrDefault(s => s.Length > 0) ?? "";
                Say($"{g.Key.VendorId:X4}:{g.Key.ProductId:X4}  {mfr} {name}".TrimEnd());
                foreach (var h in g)
                    Say($"    usagePage=0x{h.UsagePage:X4} usage=0x{h.Usage:X4} in={h.InputLength} out={h.OutputLength} feat={h.FeatureLength}");
            }
        }
        catch (Exception ex) { Say($"(HID enumeration failed: {ex.Message})"); }

        // Read-only probe of every Razer control collection on every known
        // transaction id: which id a pad/dongle answers on, and with whose
        // firmware/serial, is exactly what a new-device report has to tell us.
        Section("razer");
        Say();
        Say("--- RAZER (HID vendor protocol, read-only probe) ---");
        try { Say(Devices.RazerHid.ProbeAll()); }
        catch (Exception ex) { Say($"(Razer probe failed: {ex.Message})"); }

        Section("usb devices");
        Say();
        Say("--- USB DEVICES (PnP) ---");
        Say(Ps("Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPDeviceID -like 'USB\\VID*' } | ForEach-Object { $_.PNPDeviceID + '  ' + $_.Name } | Sort-Object -Unique"));

        Section("nvidia");
        Say();
        Say("--- NVIDIA (NvAPI) ---");
        try
        {
            var gpus = NvApi.EnumGpus();
            if (gpus.Count == 0) Say("(no NVIDIA GPU / driver)");
            foreach (var (handle, name) in gpus)
            {
                Say($"GPU: {name}");
                var b = new byte[1];
                Say($"    I2C 0x68 (MSI ITE) read probe:   {(NvApi.I2CRead(handle, 0x68, 0x22, b) ? "RESPONDS" : "no")}");
                Say($"    I2C 0x67 (ENE/ASUS) read probe:  {(NvApi.I2CRead(handle, 0x67, 0x00, b) ? "RESPONDS" : "no")}");
            }
        }
        catch (Exception ex) { Say($"(NvAPI failed: {ex.Message})"); }

        Section("openrgb");
        Say();
        Say("--- OPENRGB SERVER ---");
        try
        {
            if (!Net.OpenRgbClient.IsServerUp())
            {
                Say("(no OpenRGB SDK server on 127.0.0.1:6742)");
            }
            else
            {
                using var orgb = Net.OpenRgbClient.Connect();
                int count = orgb.GetControllerCount();
                Say($"Server up (protocol {orgb.ServerVersion}), {count} device(s):");
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var d = orgb.GetControllerData(i);
                        Say($"  [{i}] {d.Name}  type={d.Type} leds={Math.Max(d.LedCount, d.Colors.Length)}  loc={d.Location}");
                    }
                    catch (Exception ex) { Say($"  [{i}] (read failed: {ex.Message})"); }
                }
            }
        }
        catch (Exception ex) { Say($"(OpenRGB probe failed: {ex.Message})"); }
        // The bundled instance's own log: when it crashes mid-detection, the
        // last lines name the device it died on.
        if (Net.OpenRgbManager.ReadServerLogTail() is string orgbLog)
        {
            Say();
            Say("--- BUNDLED OPENRGB LOG (tail) ---");
            Say(orgbLog);
        }

        // Windows' own crash records: the faulting module names the culprit
        // definitively (no log-reading tea leaves).
        Section("crash records");
        Say();
        Say("--- CRASH RECORDS (OpenRGB / UnifiedRGB, last 3 days) ---");
        Say(Ps("Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Application Error'; StartTime=(Get-Date).AddDays(-3)} -MaxEvents 200 -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'OpenRGB|UnifiedR' } | Select-Object -First 5 | ForEach-Object { $m = $_.Message -replace '[\\r\\n]+', ' | '; if ($m.Length -gt 600) { $m = $m.Substring(0, 600) }; $_.TimeCreated.ToString('MM-dd HH:mm:ss') + '  ' + $m }"));

        Section("smbus");
        Say();
        Say("--- SMBUS (RAM RGB) ---");
        if (!admin)
        {
            Say("(skipped: needs admin)");
        }
        else if (!PawnIO.IsAvailable)
        {
            Say("PawnIO driver: NOT INSTALLED (needed for RAM RGB; installer: https://pawnio.eu)");
        }
        else
        {
            Say("PawnIO driver: installed");
            // Guarded like every other section: opening the bus can throw
            // (a vendor tool holding Global\Access_SMBUS.HTP.Method with a DACL
            // that refuses us), and that used to discard the whole report.
            try
            {
                using var bus = PawnSmbus.TryOpenAny();
                if (bus == null)
                {
                    Say("Chipset SMBus: no module loaded (unsupported chipset?)");
                }
                else
                {
                    Say($"Chipset SMBus: {bus.ChipsetName}");
                    var responding = new List<byte>();
                    for (byte addr = 0x08; addr <= 0x77; addr++)
                    {
                        try { if (bus.ReadByte(addr) >= 0) responding.Add(addr); }
                        catch { }
                        Thread.Sleep(2);
                    }
                    Say($"Responding addresses: {(responding.Count == 0 ? "(none)" : string.Join(" ", responding.Select(a => $"0x{a:X2}")))}");
                    Say("  hints: 0x50-0x57=SPD (normal), 0x70-0x77/0x67/0x39-0x3D=possible ENE RGB, 0x18-0x1F=Corsair DRAM");
                }
            }
            catch (Exception ex) { Say($"(SMBus probe failed: {ex.Message})"); }
        }

        Section("superio");
        Say();
        Say("--- MOTHERBOARD FANS (ITE Super-I/O via PawnIO) ---");
        if (!admin)
            Say("(skipped: needs admin)");
        else if (!PawnIO.IsAvailable)
            Say("PawnIO driver: NOT INSTALLED (board fans use this when LHM's ring0 driver is blocked)");
        else
        {
            var chips = new List<Sensors.IteSuperIo>();
            try
            {
                chips = Sensors.IteSuperIo.OpenAll();
                if (chips.Count == 0)
                    Say("no ITE Super-I/O found");
                else
                    foreach (var chip in chips)
                    {
                        var r = chip.Read();
                        Say($"ITE 0x{chip.ChipId:X4}:");
                        for (int i = 0; i < r.FanRpm.Length; i++)
                            if (r.FanRpm[i] is int rpm)
                                Say($"    fan {i + 1}: {rpm} RPM (duty {(r.FanDutyPct[i]?.ToString() ?? "?")}%)");
                        for (int i = 0; i < r.TempsC.Length; i++)
                            if (r.TempsC[i] is double t)
                                Say($"    temp {i + 1}: {t:0.#} C");
                    }
            }
            catch (Exception ex) { Say($"(Super-I/O probe failed: {ex.Message})"); }
            finally { foreach (var chip in chips) { try { chip.Dispose(); } catch { } } }
        }

        Say();
        Say("=============== END OF REPORT ===============");
        return sb.ToString();
    }

    public static bool IsAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Process names with the Process objects disposed (each one holds
    /// a handle until finalization otherwise; ~750 per snapshot).</summary>
    static List<string> ProcessNames()
    {
        var procs = Process.GetProcesses();
        try { return procs.Select(p => p.ProcessName).ToList(); }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    /// <summary>Running process names matching a conflict keyword, minus the
    /// OpenRGB server the app launches itself: a USER-installed OpenRGB is the
    /// classic device-fight case and must be flagged, the bundled one runs
    /// from our own LocalAppData tree (OpenRgbManager's install root) and its
    /// status has a section of its own. Told apart by image path; a path we
    /// cannot read (an elevated instance seen from a non-elevated Diag run) is
    /// not flagged - the informational RGB SOFTWARE list still names it.</summary>
    static List<string> ConflictingProcesses(string[] conflicts)
    {
        string bundleDir = AppPaths.Local("openrgb");
        var hits = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string n = p.ProcessName;
                if (!conflicts.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                if (n.Contains("openrgb", StringComparison.OrdinalIgnoreCase))
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (path == null || path.StartsWith(bundleDir, StringComparison.OrdinalIgnoreCase)) continue;
                }
                hits.Add(n);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return hits.Distinct().OrderBy(n => n).ToList();
    }

    // Internal (not private) so the console harness can pin the stdout/stderr merge.
    internal static string Ps(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell", Arguments = $"-NoProfile -Command \"{command}\"",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            // Read asynchronously so the timeout is real: ReadToEnd() blocked
            // until PowerShell exited, so WaitForExit(20000) never got to time
            // out and a hung Get-WinEvent hung the whole report collection.
            // Both streams must be draining before the wait, or a full pipe
            // stalls PowerShell. stderr is captured too: a failed CIM query
            // used to leave a blank section indistinguishable from "none".
            var output = p.StandardOutput.ReadToEndAsync();
            var errors = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(20000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return "(timed out after 20 s)";
            }
            string text = output.GetAwaiter().GetResult().Trim();
            string err = errors.GetAwaiter().GetResult().Trim();
            if (err.Length > 0)
            {
                err = System.Text.RegularExpressions.Regex.Replace(err, @"\s*[\r\n]+\s*", " | ");
                if (err.Length > 300) err = err[..300] + "...";
                text += (text.Length > 0 ? "\r\n" : "") + $"(errors: {err})";
            }
            else if (text.Length == 0 && p.ExitCode != 0)
                text = $"(exit code {p.ExitCode}, no output)";
            return text;
        }
        catch (Exception ex) { return $"(failed: {ex.Message})"; }
    }
}

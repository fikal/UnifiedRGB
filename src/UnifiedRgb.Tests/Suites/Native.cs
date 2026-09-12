using System.Diagnostics;
using System.IO;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Native;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The native seam: the PawnIO driver modules embedded in Core, |
| the Authenticode gate that decides whether a native library  |
| is allowed to load at all, and the two native-adjacent       |
| utilities that trim the working set and shell out to         |
| powershell for a diagnostic report.                          |
|                                                              |
| These sections belong together because they are the only     |
| ones that depend on the machine outside the process: a       |
| signed system binary to pin a publisher against, a real      |
| process handle count, a real powershell. Grouping them keeps |
| that dependency visible in one file instead of scattered     |
| through the suite, so a failure on an unusual machine is     |
| easy to recognise for what it is.                            |
\*-----------------------------------------------------------*/
static class NativeSuite
{
    public static void Run(Harness t)
    {
        t.Section("Embedded PawnIO modules");
        {
            t.Check(UnifiedRgb.Core.Native.PawnIO.ReadEmbeddedModule("SmbusPIIX4.bin") is { Length: > 100 }, "PIIX4 module embedded");
            t.Check(UnifiedRgb.Core.Native.PawnIO.ReadEmbeddedModule("SmbusI801.bin") is { Length: > 100 }, "I801 module embedded");
            t.Check(UnifiedRgb.Core.Native.PawnIO.ReadEmbeddedModule("nope.bin") == null, "unknown module is null, not a throw");
        }

        t.Section("Authenticode (installer signature gate)");
        {
            // Our own (unsigned) assembly must never pass, whatever subject is asked for.
            string self = typeof(UnifiedRgb.Core.Rgb).Assembly.Location;
            t.Check(!UnifiedRgb.Core.Native.Authenticode.IsSignedBy(self, "CN=namazso.eu", out var why) && why.Length > 0,
                $"unsigned assembly rejected ({why})");
            // A signed system binary passes the trust check but fails the publisher pin.
            string kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
            t.Check(!UnifiedRgb.Core.Native.Authenticode.IsSignedBy(kernel32, "CN=namazso.eu", out var who) && who.Contains("expected"),
                $"wrong publisher rejected ({who})");
            // The real PawnIO library, when installed on this machine, satisfies the pin.
            string pawn = @"C:\Program Files\PawnIO\PawnIOLib.dll";
            if (File.Exists(pawn))
                t.Check(UnifiedRgb.Core.Native.Authenticode.IsSignedBy(pawn, "CN=namazso.eu", out var ok), $"PawnIOLib.dll accepted ({ok})");
        }

        t.Section("Authenticode exact-RDN pin (#36 #37 #77)");
        {
            string kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
            t.Check(Authenticode.IsSignedBy(kernel32, "CN=Microsoft Windows", out var d1), $"exact CN accepted ({d1})");
            t.Check(Authenticode.IsSignedBy(kernel32, "O=Microsoft Corporation", out var d2), $"other RDN type (O=) accepted ({d2})");
            t.Check(Authenticode.IsSignedBy(kernel32, "cn=microsoft windows", out _), "RDN type/value compare is case-insensitive");
            t.Check(!Authenticode.IsSignedBy(kernel32, "CN=Microsoft Win", out var d3) && d3.Contains("expected"), $"CN prefix substring refused ({d3})");
            t.Check(!Authenticode.IsSignedBy(kernel32, "CN=Windows", out _), "CN suffix substring refused");
            t.Check(!Authenticode.IsSignedBy(kernel32, "O=Microsoft Windows", out _), "value must live in the pinned RDN type");
            t.Check(!Authenticode.IsSignedBy(kernel32, "Microsoft", out _), "a pin without '=' never matches");
            t.Check(!Authenticode.IsSignedBy(kernel32, "=Microsoft Windows", out _), "a pin with an empty type never matches");
            t.Check(!Authenticode.IsSignedBy(Path.Combine(Environment.SystemDirectory, "no-such-file-xyz.dll"), "CN=Microsoft Windows", out var d4) && d4.Length > 0,
                "missing file is refused with a reason");

            // #37/#77: the WINTRUST_FILE_INFO path copy is now released; hammer all three
            // exits (trusted+match, trusted+mismatch, untrusted) and require stable results.
            string self = typeof(Rgb).Assembly.Location;
            bool stable = true;
            for (int i = 0; i < 15 && stable; i++)
                stable = Authenticode.IsSignedBy(kernel32, "CN=Microsoft Windows", out _)
                      && !Authenticode.IsSignedBy(kernel32, "CN=nobody", out _)
                      && !Authenticode.IsSignedBy(self, "CN=Microsoft Windows", out _);
            t.Check(stable, "repeated IsSignedBy is stable across match / mismatch / untrusted paths");
        }

        t.Section("Embedded PawnIO modules, single copy (#151 #12)");
        {
            foreach (var m in new[] { "LpcIO.bin", "AMDFamily17.bin", "IsaBridgeEC.bin" })
                t.Check(PawnIO.ReadEmbeddedModule(m) is { Length: > 100 }, $"{m} embedded in Core");
            int copies = typeof(PawnIO).Assembly.GetManifestResourceNames().Count(n => n.EndsWith("SmbusI801.bin"));
            t.Equal(1, copies, "exactly one SmbusI801.bin manifest resource in Core");
        }

        t.Section("MemoryTrimmer handle hygiene (#22)");
        {
            using var me = Process.GetCurrentProcess();
            me.Refresh();
            int before = me.HandleCount;
            for (int i = 0; i < 10; i++) MemoryTrimmer.Trim();
            me.Refresh();
            int after = me.HandleCount;
            t.Check(after - before < 10, $"Trim x10 leaks no process handles ({before} -> {after})");
            try
            {
                string? lastMem = null;
                using (var fs = new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var rd = new StreamReader(fs))
                    for (string? line; (line = rd.ReadLine()) != null;)
                        if (line.Contains("[memory]")) lastMem = line;
                t.Check(lastMem != null && lastMem.Contains("working set trimmed"), $"last [memory] log line reports success ({lastMem})");
            }
            catch (Exception ex) { t.Skip($"memory trim: log tail unreadable: {ex.Message}"); }
        }

        t.Section("DiagnosticReport.Ps stdout/stderr merge (#24)");
        {
            t.Equal("ok", DiagnosticReport.Ps("'ok'"), "Ps plain output");
            string r = DiagnosticReport.Ps("'ok'; Write-Error 'boom'");
            t.Check(r.StartsWith("ok") && r.Contains("(errors:") && r.Contains("boom"), $"Ps keeps partial stdout and appends stderr ({r.Replace("\r\n", " / ")})");
            t.Check(!r.Contains("(errors: \r") && !r.Contains("(errors: \n"), "Ps collapses stderr line breaks");
        }
    }
}

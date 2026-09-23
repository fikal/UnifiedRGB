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

        ProtectedFolders(t);
        DesktopLaunch(t);
    }

    /// <summary>The bundled OpenRGB lives in a folder only administrators can
    /// write, or it is not used. In LocalAppData it was a binary any process of
    /// the user could replace and the elevated app would run (2026-09-23
    /// review, finding 1). Half of this can only be proven by an elevated
    /// harness; the other half is that an unelevated one fails closed.</summary>
    static void ProtectedFolders(Harness t)
    {
        t.Section("ProtectedFolder: administrators only, or not at all");
        string root = Path.Combine(Isolation.Root, "protected");
        bool ok = ProtectedFolder.Ensure(root, out string? why);
        if (!DesktopProcess.IsElevated)
        {
            t.Check(!ok && !string.IsNullOrEmpty(why), $"an unelevated process cannot make one, and says why ({why})");
            t.Check(!ProtectedFolder.IsProtected(root, out string? no) && no != null, $"...and whatever it left does not read as protected ({no})");
            t.Skip("the rest of ProtectedFolder needs an elevated harness");
            return;
        }

        t.Check(ok, $"an elevated process makes the folder ({why})");
        t.Check(ProtectedFolder.IsProtected(root, out string? check), $"...and it reads back as protected ({check})");
        t.Check(ProtectedFolder.Ensure(root, out why), $"a second call is idempotent ({why})");

        // What is made inside inherits the rules: the bundle's exe and DLLs,
        // and the config folder OpenRGB loads plugins from.
        string inner = Path.Combine(root, "config", "plugins");
        Directory.CreateDirectory(inner);
        string file = Path.Combine(inner, "planted.dll");
        File.WriteAllText(file, "not really");
        var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
        var fileAcl = new FileInfo(file).GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
        bool usersCanWrite = false, usersCanRead = false;
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in fileAcl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        {
            if (!rule.IdentityReference.Equals(users) || rule.AccessControlType != System.Security.AccessControl.AccessControlType.Allow) continue;
            if ((rule.FileSystemRights & (System.Security.AccessControl.FileSystemRights.Write | System.Security.AccessControl.FileSystemRights.Delete)) != 0) usersCanWrite = true;
            if ((rule.FileSystemRights & System.Security.AccessControl.FileSystemRights.ReadAndExecute) != 0) usersCanRead = true;
        }
        t.Check(!usersCanWrite, "a file made two levels down cannot be written by ordinary users");
        t.Check(usersCanRead, "...but can be read and run by them, which is all OpenRGB's own files need");

        // A folder somebody else made under the name is thrown away, contents
        // and all. Simulated by handing the folder to the ordinary user SID -
        // the owner a non-elevated process would have left on it.
        string planted = Path.Combine(Isolation.Root, "planted");
        Directory.CreateDirectory(planted);
        File.WriteAllText(Path.Combine(planted, "OpenRGB.exe"), "an impostor");
        using (var me = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var acl = new DirectoryInfo(planted).GetAccessControl();
            acl.SetOwner(me.User!);
            new DirectoryInfo(planted).SetAccessControl(acl);
        }
        t.Check(!ProtectedFolder.IsProtected(planted, out string? theirs) && theirs!.Contains("owned"), $"a folder the user owns is not protected ({theirs})");
        t.Check(ProtectedFolder.Ensure(planted, out why), $"Ensure takes it over ({why})");
        t.Check(!File.Exists(Path.Combine(planted, "OpenRGB.exe")), "...by removing what was planted in it, not by keeping it");
        t.Check(ProtectedFolder.IsProtected(planted, out _), "...and what stands now is administrators-only");
    }

    /// <summary>Wallpaper Engine's control command is started with the desktop
    /// shell's token, never this process's - the executable is found by
    /// following the user's own registry and library files, which any process
    /// of the user can rewrite, and started elevated it would run as
    /// administrator (2026-09-23 review, finding 2). The command line the token
    /// launch takes is proven against Windows' own argv splitter.</summary>
    static void DesktopLaunch(Harness t)
    {
        t.Section("DesktopProcess: arguments round-trip through Windows' own splitter");
        foreach (var args in new[]
        {
            new[] { "-control", "openProfile", "-profile", "Night Sky" },
            new[] { "say \"hi\"", "C:\\my dir\\", "C:\\plain\\path" },
            new[] { "a\\\\b", "tab\there", "", "trailing\\", "\\\"weird\\\"" },
        })
        {
            string line = "x.exe " + DesktopProcess.QuoteArguments(args);
            var back = SplitCommandLine(line).Skip(1).ToArray();
            t.Check(back.SequenceEqual(args), $"{line} splits back into {args.Length} argument(s) unchanged");
        }
        t.Equal("-profile \"Night Sky\"", DesktopProcess.QuoteArguments(new[] { "-profile", "Night Sky" }), "a name with a space is one quoted argument");
        t.Equal("C:\\plain\\path", DesktopProcess.QuoteArguments(new[] { "C:\\plain\\path" }), "a path without spaces is left alone");

        t.Section("DesktopProcess: a launch with the desktop token, or none");
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        bool started = DesktopProcess.Start(cmd, new[] { "/d", "/c", "exit 0" }, null, out string? why);
        if (DesktopProcess.IsElevated)
            t.Check(started || (why != null && why.Contains("elevated")), $"an elevated process starts a program as the desktop user ({why ?? "started"})");
        else
            t.Check(!started && !string.IsNullOrEmpty(why), $"an unelevated process is refused with a reason rather than a throw ({why})");
    }

    static string[] SplitCommandLine(string line)
    {
        IntPtr argv = CommandLineToArgvW(line, out int argc);
        if (argv == IntPtr.Zero) return Array.Empty<string>();
        try
        {
            var result = new string[argc];
            for (int i = 0; i < argc; i++)
                result[i] = System.Runtime.InteropServices.Marshal.PtrToStringUni(
                    System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return result;
        }
        finally { LocalFree(argv); }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);
}

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using UnifiedRgb.Core;
using UnifiedRgb.Core.Effects;
using UnifiedRgb.Core.Net;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| What leaves the machine when a user hits Send in Support.    |
|                                                              |
| A diagnostic bundle gets dragged into a public GitHub issue, |
| so redaction is the section that matters most here. The trap |
| it guards is the one the old blind substring replace fell    |
| into: an account name that happens to be a substring of the  |
| app's own vocabulary, where a user called Ian silently       |
| rewrote every mention of Lian Li. Names have to go when they |
| stand alone and stay when they do not, and a device serial   |
| tail has to go while the VID and PID a maintainer needs to   |
| read the bundle stay.                                        |
|                                                              |
| The rest of the file is the machinery around that send. The  |
| log budget and the REST title sanitiser decide what is       |
| allowed into the log the bundle then carries, and the title  |
| arrives from another process, so it is capped and stripped   |
| of the control characters that would otherwise forge a log   |
| line. UpdateClient.SizeOf parses a field from a server reply |
| and has to answer 0 rather than throw for anything it does   |
| not recognise, because a failed parse on the update path is  |
| what a support bundle gets opened about.                     |
|                                                              |
| The last two sections assert against files in the repo       |
| rather than against code. They belong with the bundle        |
| because they are the same kind of claim: the swap script's   |
| redirect-before-echo form, the manifest's elevation request  |
| and the chroma shims' identity are all things a support      |
| report would be read against, and all three have silently    |
| regressed before. They skip themselves when the repo is not  |
| reachable from the test binary.                              |
\*-----------------------------------------------------------*/
static class SupportBundleSuite
{
    public static void Run(Harness t)
    {
        t.Section("LogBudget + REST title sanitising (#135)");
        {
            var b = new LogBudget(5);
            int allowed = 0;
            for (int i = 0; i < 10; i++) if (b.Allow()) allowed++;
            t.Equal(5, allowed, "LogBudget(5) allows exactly 5 in a minute");
            t.Check(!b.Allow(), "LogBudget refuses after the budget");
            t.Check(new LogBudget(1).Allow(), "a fresh budget allows its first line");

            byte[] body = Encoding.UTF8.GetBytes("{\"title\":\"a\\n09-02 12:00:00 ERR forged\\ttab\\r\"}");
            string title = ChromaRestServer.AppTitle(body, body.Length);
            t.Check(!title.Contains('\n') && !title.Contains('\r') && !title.Contains('\t') && title.StartsWith("a 09-02"), $"AppTitle strips control characters ('{title}')");
            byte[] longBody = Encoding.UTF8.GetBytes("{\"title\":\"" + new string('x', 200) + "\"}");
            t.Equal(64, ChromaRestServer.AppTitle(longBody, longBody.Length).Length, "AppTitle caps at 64 chars");
            t.Equal("?", ChromaRestServer.AppTitle(null, 0), "AppTitle null body -> ?");
            t.Equal("?", ChromaRestServer.AppTitle(Encoding.UTF8.GetBytes("{garbage"), 8), "AppTitle malformed json -> ?");
            t.Equal("?", ChromaRestServer.AppTitle(Encoding.UTF8.GetBytes("{\"title\":\"\"}"), 12), "AppTitle empty title -> ?");
            t.Equal("?", ChromaRestServer.AppTitle(Encoding.UTF8.GetBytes("{\"other\":1}"), 11), "AppTitle missing title -> ?");
            byte[] padded = Encoding.UTF8.GetBytes("{\"title\":\"ok\"}XXXXXXXX");
            t.Equal("ok", ChromaRestServer.AppTitle(padded, 14), "AppTitle parses only the first len bytes");
        }

        t.Section("UpdateClient.SizeOf tolerant parse (#23)");
        {
            long S(string json) { using var d = JsonDocument.Parse(json); return UpdateClient.SizeOf(d.RootElement); }
            t.Equal(123L, S("{\"size\":123}"), "SizeOf integral number");
            t.Equal(5_000_000_000L, S("{\"size\":5000000000}"), "SizeOf 64-bit number");
            t.Equal(0L, S("{\"size\":\"123\"}"), "SizeOf string -> 0");
            t.Equal(0L, S("{\"size\":null}"), "SizeOf null -> 0");
            t.Equal(0L, S("{\"size\":1.5}"), "SizeOf non-integral -> 0");
            t.Equal(0L, S("{}"), "SizeOf missing -> 0");
            t.Equal(0L, S("{\"size\":true}"), "SizeOf bool -> 0");
        }

        t.Section("Self-update swap script: redirect-before-echo (#163)");
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
                t.Check(p.WaitForExit(10000), "swap-script probe finishes");
                t.Equal("ok 12", File.ReadAllText(Path.Combine(dir, "r.txt")).Trim(), "redirect-first form writes a two-digit result");
                t.Equal("ok 2", File.ReadAllText(Path.Combine(dir, "r3.txt")).Trim(), "redirect-first form writes a single-digit result");
                t.Check(File.Exists(Path.Combine(dir, "r2.txt")) && File.ReadAllText(Path.Combine(dir, "r2.txt")).Trim().Length == 0,
                    "control: echo-first form loses a single-digit result to a stderr redirect");
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }
        }

        t.Section("Repo-file invariants: app.manifest + chroma shims (#100 #8 #13)");
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
                t.Equal("requireAdministrator", level, "app.manifest requests administrator (IsAdmin() is always true in-app)");

                string shim32 = Path.Combine(repo, "native", "chroma-shim", "RzChromaSDK.dll");
                string shim64 = Path.Combine(repo, "native", "chroma-shim", "RzChromaSDK64.dll");
                static bool HasWide(byte[] bytes, string s)
                    => Encoding.Unicode.GetString(bytes).Contains(s) || Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1).Contains(s);
                if (File.Exists(shim32))
                {
                    var b = File.ReadAllBytes(shim32);
                    t.Check(HasWide(b, "RzChromaSDK_real.dll") && !HasWide(b, "RzChromaSDK64_real.dll"), "32-bit shim loads only the 32-bit backup name");
                    var vi = FileVersionInfo.GetVersionInfo(shim32);
                    t.Equal("RzChromaSDK.dll", vi.OriginalFilename, "32-bit shim OriginalFilename");
                    t.Equal("UnifiedRGB Chroma Shim", vi.ProductName, "32-bit shim ProductName (IsOurs pin)");
                }
                if (File.Exists(shim64))
                {
                    var b = File.ReadAllBytes(shim64);
                    t.Check(HasWide(b, "RzChromaSDK64_real.dll") && !HasWide(b, "RzChromaSDK_real.dll"), "64-bit shim loads only the 64-bit backup name");
                    var vi = FileVersionInfo.GetVersionInfo(shim64);
                    t.Equal("RzChromaSDK64.dll", vi.OriginalFilename, "64-bit shim OriginalFilename");
                    t.Equal("UnifiedRGB Chroma Shim", vi.ProductName, "64-bit shim ProductName (IsOurs pin)");
                }
            }
        }

        t.Section("Diagnostic bundle redaction");
        {
            // These bundles get dragged into public GitHub issues, so what comes out
            // matters as much as what goes in. The name rule is called directly with a
            // chosen name, because Scrub reads the real account name of whoever runs
            // the tests and that cannot be pinned.
            string Boundary(string text, string name)
            {
                string copy = text;
                Redaction.ReplaceName(ref copy, name, "<user>");
                return copy;
            }

            // The trap the old blind substring replace fell into: a real account name
            // that happens to be a substring of this app's own vocabulary.
            t.Equal("Lian Li SL-Infinity", Boundary("Lian Li SL-Infinity", "Ian"),
                  "redact: a user called Ian does not rewrite Lian Li");
            t.Equal("NZXT CAM is running", Boundary("NZXT CAM is running", "Cam"),
                  "redact: nor Cam rewrite NZXT CAM");
            t.Equal("--- SMBUS (RAM RGB) ---", Boundary("--- SMBUS (RAM RGB) ---", "Ram"),
                  "redact: nor Ram eat the RAM section");
            t.Equal("session start", Boundary("session start", "Art"),
                  "redact: nor Art eat 'start'");
            t.Equal("Samsung SSD", Boundary("Samsung SSD", "Sam"), "redact: nor Sam eat Samsung");

            // It still has to actually redact the name when it stands alone.
            t.Equal("hello <user> there", Boundary("hello Chris there", "Chris"), "redact: a real match still goes");
            t.Equal(@"C:\Users\<user>\Desktop", Boundary(@"C:\Users\Chris\Desktop", "chris"),
                  "redact: case-insensitively, and inside a path");

            // Device serial tails. The VID and PID are what anyone diagnosing needs;
            // the tail is the device's own serial, and on some adapters a MAC.
            string Tail(string text) => System.Text.RegularExpressions.Regex.Replace(
                text, @"(\b(?:USB|HID|BTHENUM|BTHLE)\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&\w+)*\\)\S+", "$1<instance>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            t.Equal(@"USB\VID_0B05&PID_190E\<instance>", Tail(@"USB\VID_0B05&PID_190E\00E04C239987"),
                  "redact: a usb serial tail goes");
            t.Equal(@"USB\VID_1532&PID_00CF&MI_01\<instance>  Razer HyperFlux V2 Wireless Charging System",
                  Tail(@"USB\VID_1532&PID_00CF&MI_01\9&2036339A&0&0001  Razer HyperFlux V2 Wireless Charging System"),
                  "redact: the interface number stays, the instance goes, the name stays");
            t.Equal(@"HID\VID_046D&PID_C08F\<instance>", Tail(@"HID\VID_046D&PID_C08F\7&334B221F&0&0000"),
                  "redact: HID ids too");
            t.Equal("no ids here", Tail("no ids here"), "redact: ordinary text is untouched");

            // Scrub itself: a bundle with nothing identifying in it comes back
            // unchanged, with no banner claiming otherwise.
            t.Equal("nothing to see", Redaction.Scrub("nothing to see"), "redact: a clean bundle is left alone");
            t.Equal("", Redaction.Scrub(""), "redact: empty is empty");

            // And when it does redact, it says so, so a maintainer reading the bundle
            // knows a gap is deliberate rather than a device that failed to report.
            string scrubbed = Redaction.Scrub(@"path USB\VID_1532&PID_00CF\ABCDEF0123");
            t.Check(scrubbed.StartsWith("[redacted before saving:"), "redact: it says what it took out");
            t.Check(scrubbed.Contains("<instance>"), "redact: and it took it out");
        }
    }
}

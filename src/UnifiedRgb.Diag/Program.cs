using System.Diagnostics;
using UnifiedRgb.Core;

/*-----------------------------------------------------------*\
| UnifiedRGB Diagnostic                                       |
|                                                             |
| Double-click, approve the admin prompt (optional), and the  |
| report is written next to this exe + your Desktop, with an  |
| offer to send it straight to the developer. Everything it   |
| does is READ-ONLY: it enumerates devices and probes a few   |
| well-known read registers; it never changes device state.   |
|                                                             |
| All collection logic lives in Core.DiagnosticReport so the  |
| app's Settings -> Support page produces the same report.    |
\*-----------------------------------------------------------*/

const string ReportName = "UnifiedRGB-Diagnostic.txt";

// Primary output lives NEXT TO THE EXE — same file no matter which account
// the elevation runs under. (Desktop copy is best-effort at the end.)
string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
string primaryPath = Path.Combine(exeDir, ReportName);

/*-----------------------------------------------------------*\
| Self-elevate (once). Declining just skips the SMBus scan.   |
\*-----------------------------------------------------------*/
if (!DiagnosticReport.IsAdmin() && !args.Contains("--no-elevate"))
{
    Console.WriteLine("Requesting administrator rights (needed only for the RAM/SMBus scan)...");
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath, Arguments = "--no-elevate",
            UseShellExecute = true, Verb = "runas",
        };
        Process.Start(psi);
        return;   // elevated copy takes over
    }
    catch { Console.WriteLine("Continuing without admin (SMBus/RAM scan will be skipped)."); }
}

Console.WriteLine("Collecting... this takes 15-30 seconds.");
Console.WriteLine($"Report file: {primaryPath}");
Console.WriteLine();

string report;
try
{
    report = DiagnosticReport.Collect(section =>
    {
        Console.WriteLine($"  ... {section}");
        // Best-effort partial file so a crash mid-run still leaves a marker.
        try { File.WriteAllText(primaryPath, $"(collecting: {section} — if this text remains, collection crashed here)"); } catch { }
    });
}
catch (Exception ex)
{
    report = "!!! DIAGNOSTIC CRASHED !!!\r\n" + ex;
}

Console.WriteLine();
Console.WriteLine(report);

/*-----------------------------------------------------------*\
| Write the report: next to the exe (always, regardless of    |
| which account the elevation ran under) + a Desktop copy.    |
\*-----------------------------------------------------------*/
// Only report paths that were actually written: exe-adjacent writes fail from
// Program Files / read-only shares, and the old text claimed success anyway.
var saved = new List<string>();
try { File.WriteAllText(primaryPath, report); saved.Add(primaryPath); } catch { }
try
{
    string desktopCopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ReportName);
    File.WriteAllText(desktopCopy, report);
    saved.Add(desktopCopy);
}
catch { }

Console.WriteLine();
Console.WriteLine("==============================================");
if (saved.Count == 0) Console.WriteLine(">>> COULD NOT SAVE the report anywhere (copy it from the window above).");
else foreach (var p in saved) Console.WriteLine($">>> Report saved to: {p}");
Console.WriteLine("==============================================");
if (report.StartsWith("!!! DIAGNOSTIC CRASHED") || saved.Count == 0) Environment.ExitCode = 1;

// Public builds have no support endpoint: the upload prompt could only ever
// answer "Could not send", so don't ask.
bool send = false;
if (Backend.Configured)
{
    Console.WriteLine();
    Console.WriteLine("Send this report to the developer automatically?");
    Console.Write("Press ENTER to send, or S to skip: ");
    send = true;
    try
    {
        var key = Console.ReadKey();
        Console.WriteLine();
        if (key.Key == ConsoleKey.S) send = false;
    }
    catch { }
}

if (send)
{
    Console.WriteLine("Sending...");
    var (ok, msg) = SupportUpload.SendAsync("diag", report, null, "diag").GetAwaiter().GetResult();
    Console.WriteLine(ok ? ">>> Sent - all done, you can close this window."
                         : $">>> Could not send ({msg}). Please send the file above instead.");
}
else if (Backend.Configured)
{
    Console.WriteLine(">>> Skipped - send the file above manually if needed.");
}
else
{
    Console.WriteLine(">>> Attach the saved file to a GitHub issue (Settings -> Support in the app does the same).");
}
Console.WriteLine("Press any key to close.");
try { Console.ReadKey(); } catch { }

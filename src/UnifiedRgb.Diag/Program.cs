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
try { File.WriteAllText(primaryPath, report); } catch { }
string? desktopCopy = null;
try
{
    desktopCopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ReportName);
    File.WriteAllText(desktopCopy, report);
}
catch { desktopCopy = null; }

Console.WriteLine();
Console.WriteLine("==============================================");
Console.WriteLine($">>> Report saved to: {primaryPath}");
if (desktopCopy != null) Console.WriteLine($">>> Also copied to:  {desktopCopy}");
Console.WriteLine("==============================================");
Console.WriteLine();
Console.WriteLine("Send this report to the developer automatically?");
Console.Write("Press ENTER to send, or S to skip: ");
bool send = true;
try
{
    var key = Console.ReadKey();
    Console.WriteLine();
    if (key.Key == ConsoleKey.S) send = false;
}
catch { }

if (send)
{
    Console.WriteLine("Sending...");
    var (ok, msg) = SupportUpload.SendAsync("diag", report, null, "diag").GetAwaiter().GetResult();
    Console.WriteLine(ok ? ">>> Sent - all done, you can close this window."
                         : $">>> Could not send ({msg}). Please send the file above instead.");
}
else
{
    Console.WriteLine(">>> Skipped - send the file above manually if needed.");
}
Console.WriteLine("Press any key to close.");
try { Console.ReadKey(); } catch { }

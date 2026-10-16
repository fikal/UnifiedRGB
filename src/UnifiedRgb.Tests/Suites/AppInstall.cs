using System.Diagnostics;
using System.IO;
using UnifiedRgb.App.Services;
using UnifiedRgb.Core;
using static UnifiedRgb.Tests.TestHelpers;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The installed copy behind "Start with Windows" and the logon |
| task that runs it (AppInstall). Nothing here registers a     |
| task or writes under Program Files: the install folder is    |
| pointed at a scratch folder, and Task Scheduler is only read |
| (a task name that does not exist).                           |
\*-----------------------------------------------------------*/
static class AppInstallSuite
{
    public static void Run(Harness t)
    {
        Decisions(t);
        TaskXml(t);
        OldTaskIsOutdated(t);
        InstalledCopy(t);
        TaskSchedulerRead(t);
        EntryPoint(t);
    }

    static void Decisions(Harness t)
    {
        t.Section("install: what a launch does before WPF starts");
        var D = AppInstall.Decide;
        const AppInstall.StartupAction Run = AppInstall.StartupAction.Run, Relaunch = AppInstall.StartupAction.RelaunchForRuntime,
            HandOff = AppInstall.StartupAction.HandOff, Refuse = AppInstall.StartupAction.Refuse;
        //        packaged installed trusted relaunched taskRunsIt exists installedNewer
        t.Equal(Run, D(false, false, false, false, true, true, false), "a development build always runs where it is");
        t.Equal(Relaunch, D(true, true, false, false, true, true, false),
            "the installed copy, its DLLs unpacked anywhere but the admin-only folder, restarts once to get there");
        t.Equal(Run, D(true, true, true, false, true, true, false), "...and runs once they really are there");
        t.Equal(Run, D(true, true, true, true, true, true, false), "...whatever the marker says");
        t.Equal(Refuse, D(true, true, false, true, true, true, false),
            "restarted and STILL not there: it refuses to run (the marker alone used to let it run from %TEMP%, and the user's environment can set it)");
        t.Equal(HandOff, D(true, false, false, false, true, true, false),
            "a downloaded copy hands over to the installed one the logon task runs");
        t.Equal(Run, D(true, false, false, false, true, true, true),
            "...unless the installed one is NEWER: an older build launched by hand runs itself, so a rollback is possible");
        t.Equal(Run, D(true, false, false, false, false, true, false),
            "...and not when the task does not run it (Start with Windows off, or an older build's task)");
        t.Equal(Run, D(true, false, false, false, true, false, false), "...nor when the installed copy is missing");

        const string inst = @"C:\Program Files\UnifiedRGB", rt = @"C:\Program Files\UnifiedRGB\runtime";
        var R = AppInstall.RuntimeTrusted;
        t.Check(R(@"C:\Program Files\UnifiedRGB\runtime\UnifiedRGB\ELFutsivJHFf\;", inst, rt),
            "the runtime unpacked under the admin-only folder (the host's own list, trailing separator and all) is trusted");
        t.Check(!R(@"C:\Users\x\AppData\Local\Temp\.net\UnifiedRGB\ELFutsivJHFf\;", inst, rt), "...%TEMP%\\.net is not");
        t.Check(!R(@"C:\Program Files\UnifiedRGB\runtime\UnifiedRGB\a\;C:\Users\x\evil\", inst, rt), "...nor a list with anything outside the install folder in it");
        t.Check(!R(@"C:\Program Files\UnifiedRGB\runtime-evil\x\", inst, rt), "...nor a sibling whose name merely starts the same");
        t.Check(!R(null, inst, rt) && !R("", inst, rt), "...nor no list at all");

        var v125 = new Version(1, 2, 5);
        t.Check(AppInstall.ShouldCopy(v125, null), "no installed copy: install");
        t.Check(AppInstall.ShouldCopy(new Version(1, 2, 6), v125), "a newer download refreshes the installed copy");
        t.Check(!AppInstall.ShouldCopy(v125, v125), "the same version is left alone");
        t.Check(!AppInstall.ShouldCopy(new Version(1, 2, 4), v125), "an older download never replaces a newer installed copy on its own");

        t.Check(AppInstall.SamePath(@"C:\Program Files\UnifiedRGB\UnifiedRGB.exe", @"c:\program files\unifiedrgb\UNIFIEDRGB.EXE"), "paths compare case-insensitively");
        t.Check(!AppInstall.SamePath("", @"C:\x.exe") && !AppInstall.SamePath("\"\"", @"C:\x.exe"), "an empty or junk task command matches nothing (and does not throw)");

        t.Section("install: the environment's .NET runtime settings are not passed on");
        foreach (string name in new[] { "DOTNET_STARTUP_HOOKS", "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER_PATH", "COR_PROFILER", "COMPlus_EnableDiagnostics", "dotnet_gcServer" })
            t.Check(AppInstall.IsRuntimeKnob(name), $"{name} is stripped");
        foreach (string name in new[] { "PATH", "TEMP", "UNIFIEDRGB_RELAUNCHED", "CORPORATE", "DOTNETX" })
            t.Check(!AppInstall.IsRuntimeKnob(name), $"{name} is left alone");

        t.Section("install: an automatic check never moves a development build's task");
        const string installed = @"C:\Program Files\UnifiedRGB\UnifiedRGB.exe";
        t.Check(!AppInstall.ShouldRepoint(true, @"C:\Users\ryanb\source\repos\UnifiedRgb\src\UnifiedRgb.App\bin\Debug\net10.0-windows10.0.22621.0\UnifiedRgb.App.exe", installed),
            "a task on a Debug tree's exe stays there (launching a release build once used to take it over)");
        t.Check(!AppInstall.ShouldRepoint(true, @"C:\src\UnifiedRgb.App\bin\Release\net10.0\win-x64\publish\UnifiedRgb.App.exe", installed), "...a Release tree's too");
        t.Check(AppInstall.ShouldRepoint(true, @"C:\Users\x\Downloads\UnifiedRGB-v1.2.5.exe", installed), "a task on a downloaded exe moves to the installed copy");
        t.Check(!AppInstall.ShouldRepoint(true, installed, installed), "...one already on it stays");
        t.Check(!AppInstall.ShouldRepoint(false, @"C:\Users\x\Downloads\UnifiedRGB-v1.2.5.exe", installed), "...and a development build never moves anything");
    }

    static void TaskXml(Harness t)
    {
        t.Section("install: the logon task's settings are spelled out");
        string command = @"C:\Users\Zoë & Co's\UnifiedRGB <test>\UnifiedRGB.exe";
        string xml = AppInstall.BuildTaskXml(command, "S-1-5-21-1-2-3-1001");
        var shape = AppInstall.TaskShape.Parse(xml);
        t.Equal(command, shape.Command, "the command survives XML escaping (ampersand, apostrophe, angle brackets, non-ASCII)");
        t.Equal("HighestAvailable", shape.RunLevel, "elevated, so the sensors work at logon without a prompt");
        t.Equal(4, shape.Priority, "normal priority (the default 7 is below normal: effects and the fan loop starved under load)");
        t.Equal("PT0S", shape.TimeLimit, "no run limit (the default stops the task after 72 hours, with no fan handback)");
        t.Check(!shape.NoStartOnBattery && !shape.StopOnBattery, "starts on battery and keeps running when a laptop is unplugged");
        t.Equal(null, shape.Outdated(command), "a task built now is current");
        t.Check(shape.Outdated(@"C:\Program Files\UnifiedRGB\UnifiedRGB.exe") is string why && why.Contains("runs", StringComparison.Ordinal),
            "a task that runs another exe is outdated");
        t.Check(xml.Contains("<Arguments>--autostart</Arguments>", StringComparison.Ordinal), "the login launch passes --autostart (opens to the tray)");
        t.Check(xml.Contains("<UserId>S-1-5-21-1-2-3-1001</UserId>", StringComparison.Ordinal)
                && System.Text.RegularExpressions.Regex.Matches(xml, "<UserId>").Count == 2,
            "both the principal and the logon trigger name the user (a trigger without one fires for any user's logon)");
    }

    /// <summary>What `schtasks /Create /SC ONLOGON /RL HIGHEST` made, exported
    /// from a real machine on 2026-10-15.</summary>
    const string SchtasksDefaultXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-21-3714047671-2432026803-708365378-1001</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <IdleSettings>
              <Duration>PT10M</Duration>
              <WaitTimeout>PT1H</WaitTimeout>
              <StopOnIdleEnd>true</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
          </Settings>
          <Triggers><LogonTrigger><StartBoundary>2026-08-18T08:43:00</StartBoundary></LogonTrigger></Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>C:\Users\someone\Downloads\UnifiedRGB-v1.2.5.exe</Command>
              <Arguments>--autostart</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    static void OldTaskIsOutdated(Harness t)
    {
        t.Section("install: a task an older build made is brought up to date");
        // With its UTF-16 declaration, as the COM API hands the text back.
        var shape = AppInstall.TaskShape.Parse(SchtasksDefaultXml);
        string? why = shape.Outdated(@"C:\Users\someone\Downloads\UnifiedRGB-v1.2.5.exe");
        t.Check(why != null && why.Contains("priority 7", StringComparison.Ordinal), $"schtasks' default priority 7 is flagged ({why})");
        t.Check(why != null && why.Contains("PT72H", StringComparison.Ordinal), "...the implied 72-hour limit too");
        t.Check(why != null && why.Contains("no start on battery", StringComparison.Ordinal) && why.Contains("stop on battery", StringComparison.Ordinal),
            "...and both battery rules");
        t.Check(shape.Outdated(@"C:\Program Files\UnifiedRGB\UnifiedRGB.exe") is string w2 && w2.Contains(@"Downloads\UnifiedRGB-v1.2.5.exe", StringComparison.Ordinal),
            "a release build's task that still runs the downloaded exe moves to the installed copy");
    }

    static void InstalledCopy(Harness t)
    {
        t.Section("install: the installed copy is made from the held image, in an admin-only folder, never through a link");
        string saved = AppInstall.InstallDir;
        var savedEnsure = AppInstall.EnsureFolder;
        string root = TempDir();
        try
        {
            // ProtectedFolder's own rules, owned by this (unelevated) user: the
            // whole production path, junction handling included.
            var mine = MinePolicy();
            AppInstall.EnsureFolder = p => (ProtectedFolder.Ensure(p, mine, out string? why), why);
            AppInstall.InstallDir = Path.Combine(root, "UnifiedRGB");
            var fake = new MemoryStream(new byte[] { (byte)'M', (byte)'Z', 1, 2, 3, 4, 5 });
            t.Check(AppInstall.EnsureInstalledCopy(fake, new Version(1, 2, 5)), "a fresh install succeeds");
            t.Check(File.Exists(AppInstall.InstalledExe) && File.ReadAllBytes(AppInstall.InstalledExe).AsSpan().SequenceEqual(fake.ToArray()),
                "...with exactly the bytes of the held image");
            t.Check(ProtectedFolder.Verify(AppInstall.InstallDir, mine, out string? folderWhy),
                $"...in a folder made with its own ACL, not the one Program Files would hand down ({folderWhy ?? "ok"})");
            t.Equal(0, Directory.GetFiles(AppInstall.InstallDir, "*.tmp").Length, "...and no temporary file left beside it");

            // A real versioned PE as the installed copy: this suite's own App
            // assembly carries the app's FileVersion.
            string versioned = typeof(AppInstall).Assembly.Location;
            File.Copy(versioned, AppInstall.InstalledExe, overwrite: true);
            var installed = Version.Parse(FileVersionInfo.GetVersionInfo(versioned).FileVersion!);
            byte[] before = File.ReadAllBytes(AppInstall.InstalledExe);
            t.Check(AppInstall.EnsureInstalledCopy(new MemoryStream(new byte[] { 9, 9, 9 }), new Version(0, 0, 1)),
                "an older running build is told the (newer) installed copy may run");
            t.Check(File.ReadAllBytes(AppInstall.InstalledExe).AsSpan().SequenceEqual(before), $"...and leaves v{installed} in place");
            var older = new MemoryStream(new byte[] { 6, 6, 6 });
            t.Check(AppInstall.EnsureInstalledCopy(older, new Version(0, 0, 1), force: true) &&
                    File.ReadAllBytes(AppInstall.InstalledExe).AsSpan().SequenceEqual(older.ToArray()),
                "the Start with Windows checkbox installs THIS build even over a newer one (the rollback path)");
            File.Copy(versioned, AppInstall.InstalledExe, overwrite: true);
            var newer = new MemoryStream(new byte[] { 7, 7, 7 });
            t.Check(AppInstall.EnsureInstalledCopy(newer, new Version(installed.Major + 1, 0, 0)) &&
                    File.ReadAllBytes(AppInstall.InstalledExe).AsSpan().SequenceEqual(newer.ToArray()),
                "a newer running build replaces it");

            // A junction where the install folder should be is moved aside, and
            // the install lands in a real folder - never in what it pointed at.
            string elsewhere = Path.Combine(root, "elsewhere");
            Directory.CreateDirectory(elsewhere);
            AppInstall.InstallDir = Path.Combine(root, "linked");
            using (var mk = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                       $"/d /c mklink /J \"{AppInstall.InstallDir}\" \"{elsewhere}\"") { CreateNoWindow = true, UseShellExecute = false }))
                mk?.WaitForExit(10_000);
            if (!Directory.Exists(AppInstall.InstallDir)) t.Skip("install: could not create a junction on this machine");
            else
            {
                t.Check(AppInstall.EnsureInstalledCopy(new MemoryStream(new byte[] { 1 }), new Version(9, 9, 9)), "an install over a planted junction still succeeds...");
                t.Check((File.GetAttributes(AppInstall.InstallDir) & FileAttributes.ReparsePoint) == 0, "...in a real folder made in its place");
                t.Equal(0, Directory.GetFiles(elsewhere).Length, "...and nothing lands in the folder the junction pointed at");
            }
        }
        finally
        {
            AppInstall.InstallDir = saved;
            AppInstall.EnsureFolder = savedEnsure;
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // a scratch folder (the moved-aside junction can refuse); the OS temp cleanup takes it
        }
    }

    /// <summary>The app's folder rules, owned by THIS user: the harness is not
    /// elevated, and only an elevated process may make Administrators the owner.</summary>
    static ProtectedFolder.Policy MinePolicy()
    {
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
        return new ProtectedFolder.Policy(me.User!, system, admins, me.User!);
    }

    static void TaskSchedulerRead(Harness t)
    {
        t.Section("install: Task Scheduler is read through its COM API");
        string name = "UnifiedRGB-harness-absent-" + Guid.NewGuid().ToString("N");
        try
        {
            t.Equal(null, AppInstall.ReadTaskXml(name), "a task that does not exist reads as null, not an exception");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            t.Skip($"install: Task Scheduler is not reachable here ({ex.Message})");
        }
    }

    static void EntryPoint(Harness t)
    {
        t.Section("install: Program.Main runs before WPF");
        if (SourceOf("src/UnifiedRgb.App/UnifiedRgb.App.csproj") is not string csproj
            || SourceOf("src/UnifiedRgb.App/Program.cs") is not string program)
        {
            t.Skip("install: repo sources not found from the test binary");
            return;
        }
        t.Check(csproj.Contains("<StartupObject>UnifiedRgb.App.Program</StartupObject>", StringComparison.Ordinal),
            "the App project starts at Program.Main, not WPF's generated App.Main");
        int handOff = program.IndexOf("AppInstall.HandOffBeforeStartup()", StringComparison.Ordinal);
        int app = program.IndexOf("App.Main()", StringComparison.Ordinal);
        t.Check(handOff >= 0 && app > handOff, "...which decides on a hand-off before it starts WPF");
    }
}

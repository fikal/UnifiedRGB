using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using UnifiedRgb.Core;

namespace UnifiedRgb.App.Services;

/*-----------------------------------------------------*\
| Where a released build runs from at logon.             |
|                                                        |
| "Start with Windows" is an ELEVATED logon task, so the |
| exe it names runs as administrator with no prompt. A   |
| release is one portable exe, usually left in           |
| Downloads, where every process of the user can replace |
| it - and .NET unpacks the exe's native DLLs (WPF's     |
| wpfgfx and friends) under %TEMP%\.net, which is just   |
| as writable and is reused without any check. Either    |
| way, anything running as the user got administrator at |
| the next logon. So the task runs an installed copy     |
| under Program Files instead, and that copy runs only   |
| with its DLLs unpacked into its own admin-only folder  |
| (it restarts itself once to get there). A launch of    |
| the downloaded exe hands over to the installed copy    |
| (refreshing it first when the download is newer), so   |
| there is one app; an OLDER download runs itself, so a  |
| rollback is always possible.                           |
|                                                        |
| What this cannot close: the logon task's process gets  |
| the user's environment, and the .NET runtime reads a   |
| profiler from it (CORECLR_ENABLE_PROFILING / _PATH)    |
| before any of our code runs. Startup hooks are off in  |
| the csproj, every runtime knob is stripped from what   |
| this process passes on, and one found at startup is    |
| logged - but only a native launcher with a clean       |
| environment would keep a profiler out of the first     |
| process.                                               |
|                                                        |
| Development builds (not single-file) are never         |
| installed: the task keeps naming the exe it was        |
| created with, and only its settings are corrected.     |
\*-----------------------------------------------------*/
public static class AppInstall
{
    public const string TaskName = "UnifiedRGB";
    const string ExtractVar = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";
    const string RelaunchedVar = "UNIFIEDRGB_RELAUNCHED";
    const string HandOffVar = "UNIFIEDRGB_HANDOFF_FROM";
    const string NotesVar = "UNIFIEDRGB_HANDOFF_NOTES";

    /// <summary>Program Files: a non-administrator cannot create anything
    /// directly under it, so the folder cannot be pre-planted before the first
    /// install. Settable for the harness only.</summary>
    internal static string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UnifiedRGB");
    internal static string InstalledExe => Path.Combine(InstallDir, "UnifiedRGB.exe");
    internal static string RuntimeDir => Path.Combine(InstallDir, "runtime");

    /// <summary>Makes a folder administrators-only (ProtectedFolder: created
    /// with its ACL, never through a link, owner and DACL verified) - not left
    /// to what it inherits from Program Files, whose CREATOR OWNER entry hands
    /// files to the user's own account under the "object creator" owner
    /// policy. The harness swaps in the same check owned by itself.</summary>
    internal static Func<string, (bool Ok, string? Why)> EnsureFolder =
        path => (ProtectedFolder.Ensure(path, out string? why), why);

    /// <summary>A released build: one self-contained single-file exe, whose
    /// assemblies have no file of their own. Debug and `dotnet run` trees do.</summary>
#pragma warning disable IL3000 // empty in a single-file bundle, which is exactly what this asks
    public static bool IsPackaged => string.IsNullOrEmpty(typeof(AppInstall).Assembly.Location);
#pragma warning restore IL3000

    public static bool RunningInstalled =>
        IsPackaged && Environment.ProcessPath is string p && SamePath(p, InstalledExe);

    internal static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }   // not a path at all: not ours
    }

    static bool IsUnder(string path, string dir)
    {
        try
        {
            string full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            return full.StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }   // not a path at all: not under anything
    }

    /// <summary>A development tree's exe (Debug or Release under bin\): what a
    /// task names when Ryan runs the app from source. Never re-pointed
    /// automatically - launching a release build once used to move the task
    /// off his dev build for good.</summary>
    internal static bool IsDevBuildPath(string path) =>
        path.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);

    /*---------------- log lines from before the session log is open ----------------*/

    // The log's first write rotates the file and starts a session. Before the
    // single-instance check that must not happen: a launch beside a running
    // instance would split that instance's log. Held here, written by LogStartup
    // (or handed to the process this one hands over to).
    static readonly List<(bool Warn, string Text)> _early = new();
    static bool _logOpen;

    static void Say(bool warn, string text)
    {
        lock (_early)
        {
            if (!_logOpen) { _early.Add((warn, text)); return; }
        }
        if (warn) Log.Warn("install", text);
        else Log.Info("install", text);
    }

    /*---------------- the environment ----------------*/

    /// <summary>Settings the .NET runtime reads from the environment: startup
    /// hooks, profilers, diagnostics output paths, GC and JIT knobs. A process
    /// of the user can put any of them in HKCU\Environment, and the logon
    /// task's process - administrator - inherits it.</summary>
    internal static bool IsRuntimeKnob(string name) =>
        name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Strip every runtime knob from this process's environment, so no
    /// process it starts (the restart, the update script's relaunch, OpenRGB,
    /// Wallpaper Engine) inherits one - except the extraction folder when it
    /// already names the admin-only one. Returns what was removed.</summary>
    static List<string> ScrubRuntimeKnobs()
    {
        var removed = new List<string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is not string name || !IsRuntimeKnob(name)) continue;
            if (name.Equals(ExtractVar, StringComparison.OrdinalIgnoreCase) && e.Value is string v && SamePath(v, RuntimeDir)) continue;
            Environment.SetEnvironmentVariable(name, null);
            removed.Add(name);
        }
        return removed;
    }

    /*---------------- before WPF: hand-off and runtime relaunch ----------------*/

    internal enum StartupAction { Run, RelaunchForRuntime, HandOff, Refuse }

    /// <summary>What this process does before WPF starts. Pure, for the harness.
    /// <paramref name="runtimeTrusted"/>: the host really is loading our native
    /// DLLs from the admin-only folder - read from where it unpacked them, not
    /// from any marker the environment could carry.</summary>
    internal static StartupAction Decide(bool packaged, bool runningInstalled, bool runtimeTrusted, bool relaunched,
        bool taskRunsInstalledCopy, bool installedExists, bool installedIsNewer)
    {
        if (!packaged) return StartupAction.Run;
        if (runningInstalled)
        {
            if (runtimeTrusted) return StartupAction.Run;
            // Restarted once and STILL not unpacked where it should be: refuse
            // rather than load the DLLs from anywhere else, and never loop.
            return relaunched ? StartupAction.Refuse : StartupAction.RelaunchForRuntime;
        }
        // An older build launched by hand runs itself: handing it to the newer
        // installed copy made a rollback impossible while Start with Windows
        // was on (a release that crashed at startup kept the user out of the
        // very checkbox that would turn it off).
        if (installedIsNewer) return StartupAction.Run;
        return taskRunsInstalledCopy && installedExists ? StartupAction.HandOff : StartupAction.Run;
    }

    /// <summary>The host's native search folders, as it set them up for this
    /// process: all inside the install folder, at least one of them inside the
    /// runtime folder.</summary>
    internal static bool RuntimeTrusted(string? nativeSearchDirectories, string installDir, string runtimeDir)
    {
        var dirs = (nativeSearchDirectories ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return dirs.Length > 0
            && dirs.Any(d => IsUnder(d, runtimeDir))
            && dirs.All(d => SamePath(d, installDir) || IsUnder(d, installDir));
    }

    static string? NativeSearchDirectories => AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string;

    /// <summary>A newer download refreshes the installed copy; an older one
    /// never replaces it on its own (only the Start with Windows checkbox does).</summary>
    internal static bool ShouldCopy(Version running, Version? installed) => installed == null || running > installed;

    /// <summary>The running exe, opened at startup and held for the life of the
    /// process with no write or delete sharing. The installed copy is made from
    /// THIS stream, never by reopening the path: a running exe can be renamed,
    /// so a process of the user could otherwise slip its own file in at our
    /// path just before the copy and have it installed as administrator. Held,
    /// it can no longer be renamed or replaced.</summary>
    static FileStream? _self;

    /// <summary>True = this process should exit (another took over, or it
    /// refused to run). Runs before WPF; a failure to hand over runs the app
    /// from where it is, a failure to get the runtime into the admin-only
    /// folder does not run it at all.</summary>
    public static bool HandOffBeforeStartup()
    {
        if (!IsPackaged || Environment.ProcessPath is not string self) return false;
        var knobs = ScrubRuntimeKnobs();
        if (knobs.Count > 0)
            Say(true, $"the environment carried .NET runtime settings ({string.Join(", ", knobs)}); removed for every process this one starts. "
                      + "A profiler named there runs inside this one before any of its code - if you did not set these, remove them from your user environment variables");
        bool runningInstalled = RunningInstalled;
        if (!runningInstalled)
        {
            try { _self = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // something else holds it open for writing; EnsureInstalledCopy then refuses to copy
        }
        bool relaunched = Environment.GetEnvironmentVariable(RelaunchedVar) == "1";
        bool trusted = runningInstalled && RuntimeTrusted(NativeSearchDirectories, InstallDir, RuntimeDir);
        bool taskRunsInstalled = !runningInstalled && TaskCommand() is string cmd && SamePath(cmd, InstalledExe);
        Version? installed = runningInstalled ? null : FileVersionOf(InstalledExe);
        bool installedIsNewer = installed != null && installed > AppInfo.Version;
        switch (Decide(true, runningInstalled, trusted, relaunched, taskRunsInstalled, installed != null, installedIsNewer))
        {
            case StartupAction.RelaunchForRuntime:
                return StartInstalled(handOffFrom: null) || Refuse("it could not restart itself with its runtime in " + RuntimeDir);
            case StartupAction.Refuse:
                return Refuse($"after restarting, its native runtime still loads from {NativeSearchDirectories ?? "(unknown)"}, not {RuntimeDir}");
            case StartupAction.HandOff:
                if (ShouldCopy(AppInfo.Version, installed) && !EnsureInstalledCopy(force: false))
                    return false;   // could not refresh it (typically it is running): run this newer build rather than hand over to the old one
                return StartInstalled(handOffFrom: self);
            default:
                return false;
        }
    }

    /// <summary>The installed copy will not run with its DLLs anywhere but the
    /// admin-only folder: say so (log and a box - at logon nothing else would
    /// show it) and exit.</summary>
    static bool Refuse(string why)
    {
        string text = $"UnifiedRGB did not start: {why}. Starting anyway would load its DLLs from a folder other programs can change.";
        lock (_early) _logOpen = true;   // the session ends here: this is its only record
        Log.Error("install", text);
        MessageBoxW(IntPtr.Zero, text + $"\n\nDetails: {Log.FilePath}", "UnifiedRGB", 0x10 /* MB_ICONERROR */);
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>Start the installed copy with this process's arguments, its
    /// native DLLs to be unpacked under RuntimeDir, and the marker that ends
    /// the relaunch chain. Elevated, as we are: no prompt. The environment it
    /// gets is this process's, already stripped of runtime knobs.</summary>
    static bool StartInstalled(string? handOffFrom)
    {
        try
        {
            if (!EnsureFolders(runtime: true)) return false;
            var psi = new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = InstallDir };
            foreach (string a in Environment.GetCommandLineArgs().Skip(1)) psi.ArgumentList.Add(a);
            psi.Environment[ExtractVar] = RuntimeDir;
            psi.Environment[RelaunchedVar] = "1";
            if (handOffFrom != null) psi.Environment[HandOffVar] = handOffFrom;
            lock (_early)
                if (_early.Count > 0)
                    psi.Environment[NotesVar] = string.Join("\n", _early.Select(n => (n.Warn ? "!" : "") + n.Text));
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception ex)
        {
            Say(true, $"could not start the installed copy at {InstalledExe}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The first lines of the session that took over, once the log is
    /// open (App.OnStartup, after the single-instance check): what happened
    /// before it, here or in the process that handed over. Also clears the
    /// markers so a process started later (the update script's relaunch) is
    /// judged on its own.</summary>
    public static void LogStartup()
    {
        // The process that handed over first, then this one's own.
        var lines = new List<(bool Warn, string Text)>();
        if (Environment.GetEnvironmentVariable(NotesVar) is string notes)
            foreach (string line in notes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                lines.Add((line.StartsWith('!'), line.TrimStart('!')));
        lock (_early) { _logOpen = true; lines.AddRange(_early); _early.Clear(); }
        string? from = Environment.GetEnvironmentVariable(HandOffVar);
        if (from != null) Log.Info("install", $"started by {from}; running the installed copy");
        foreach (var (warn, text) in lines)
            if (warn) Log.Warn("install", text); else Log.Info("install", text);
        if (RunningInstalled) Log.Info("install", $"running from {InstallDir}, native runtime unpacked under {RuntimeDir}");
        Environment.SetEnvironmentVariable(HandOffVar, null);
        Environment.SetEnvironmentVariable(RelaunchedVar, null);
        Environment.SetEnvironmentVariable(NotesVar, null);
    }

    /*---------------- the installed copy ----------------*/

    static Version? FileVersionOf(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out var v)
                ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
        }
        catch (IOException) { return null; }   // unreadable = treat as absent; the copy then replaces it
    }

    static bool EnsureFolders(bool runtime)
    {
        var (ok, why) = EnsureFolder(InstallDir);
        if (ok && runtime) (ok, why) = EnsureFolder(RuntimeDir);
        if (!ok) Say(true, $"{(runtime ? RuntimeDir : InstallDir)} could not be made administrators-only ({why}); not installing or running from it");
        return ok;
    }

    /// <summary>Install or refresh the copy under Program Files from the held
    /// image of this exe. <paramref name="force"/> (the Start with Windows
    /// checkbox): install THIS build whatever the installed version - the way
    /// to roll the logon copy back. Otherwise only a newer build replaces it.
    /// True when the installed copy is then one the caller may run: this build
    /// (forced), or this build or newer.</summary>
    internal static bool EnsureInstalledCopy(bool force)
    {
        if (_self == null)
        {
            Say(true, "this exe could not be held open, so it is not copied: the logon task keeps running what it ran");
            return !force && FileVersionOf(InstalledExe) is Version v && v >= AppInfo.Version;
        }
        return EnsureInstalledCopy(_self, AppInfo.Version, force);
    }

    internal static bool EnsureInstalledCopy(Stream source, Version running, bool force = false)
    {
        string? tmp = null;
        try
        {
            if (!EnsureFolders(runtime: false)) return false;
            Version? installed = FileVersionOf(InstalledExe);
            bool copy = force ? installed != running : ShouldCopy(running, installed);
            if (!copy) return true;
            // Copied beside the target and renamed over it: a half-written exe
            // is never what the logon task finds.
            tmp = Path.Combine(InstallDir, $"UnifiedRGB.exe.{Guid.NewGuid():N}.tmp");
            using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                lock (source)
                {
                    source.Position = 0;
                    source.CopyTo(dst);
                }
            }
            File.Move(tmp, InstalledExe, overwrite: true);
            tmp = null;
            Say(false, $"installed v{running.ToString(3)} to {InstalledExe}" + (installed != null ? $" (was v{installed.ToString(3)})" : ""));
            return true;
        }
        catch (Exception ex)
        {
            // Typically the installed copy is running (a newer download launched
            // beside it). Keep what is there; its own updater moves it on.
            Say(true, $"could not install to {InstalledExe}: {ex.Message}");
            return !force && FileVersionOf(InstalledExe) is Version v && v >= running;
        }
        finally
        {
            if (tmp != null)
                try { File.Delete(tmp); }
                catch (IOException) { }   // nothing else to do; the next install writes a fresh name
        }
    }

    static void RemoveInstall()
    {
        if (RunningInstalled || !Directory.Exists(InstallDir)) return;
        try
        {
            // Not through a link: the folder's own attributes, not its target's.
            if ((File.GetAttributes(InstallDir) & FileAttributes.ReparsePoint) != 0) return;
            Directory.Delete(InstallDir, recursive: true);
            Log.Info("install", $"removed {InstallDir} (Start with Windows is off)");
        }
        catch (Exception ex) { Log.Warn("install", $"could not remove {InstallDir}: {ex.Message}"); }
    }

    /// <summary>Each release unpacks into its own folder under RuntimeDir; keep
    /// only the one this process unpacked to.</summary>
    static void SweepOldRuntimes()
    {
        if (!RunningInstalled) return;
        try
        {
            string? current = (NativeSearchDirectories ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(d => d.TrimEnd('\\'))
                .FirstOrDefault(d => IsUnder(d, RuntimeDir));
            if (current == null) return;
            string? appDir = Path.GetDirectoryName(current);
            if (appDir == null || !SamePath(Path.GetDirectoryName(appDir) ?? "", RuntimeDir)) return;
            foreach (string dir in Directory.EnumerateDirectories(appDir))
                if (!SamePath(dir, current))
                    try { Directory.Delete(dir, recursive: true); }
                    catch (Exception ex) { Log.Warn("install", $"old runtime {dir} left in place: {ex.Message}"); }
        }
        catch (Exception ex) { Log.Warn("install", $"runtime sweep skipped: {ex.Message}"); }
    }

    /*---------------- the logon task ----------------*/

    /// <summary>Task Scheduler through its COM API rather than schtasks.exe:
    /// schtasks writes XML to a pipe in the console's single-byte code page
    /// (a non-ASCII path in a user name came back mangled), and creating a
    /// task from XML would mean a file the elevated schtasks reads from a
    /// folder the user can write.</summary>
    static dynamic RootFolder()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available");
        dynamic svc = Activator.CreateInstance(type)!;
        svc.Connect();
        return svc.GetFolder("\\");
    }

    const int ErrorFileNotFound = unchecked((int)0x80070002);

    /// <summary>The COM binder turns the HRESULT into whichever exception maps
    /// to it (FileNotFoundException for this one), sometimes wrapped.</summary>
    static bool IsNoSuchTask(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
            if (ex.HResult == ErrorFileNotFound) return true;
        return false;
    }

    /// <summary>The task's XML, or null when there is no such task.</summary>
    internal static string? ReadTaskXml(string name = TaskName)
    {
        try { return (string)RootFolder().GetTask(name).Xml; }
        catch (Exception ex) when (IsNoSuchTask(ex)) { return null; }
    }

    static string? TaskCommand()
    {
        try { return ReadTaskXml() is string xml ? TaskShape.Parse(xml).Command.Trim('"') : null; }
        catch (Exception) { return null; }   // unreadable task: treated as no hand-off target; the app runs from here
    }

    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>The logon task, settings spelled out. `schtasks /Create /SC
    /// ONLOGON` left every one at its default, and the defaults are wrong for
    /// a tray app that drives fans: below-normal priority (7), so effects and
    /// the fan loop starved under a game's load; a 72-hour run limit, after
    /// which Task Scheduler stops it with no fan handback; and no start on
    /// battery, plus a stop on unplug.</summary>
    internal static string BuildTaskXml(string command, string userSid)
    {
        XElement E(string name, params object[] content) => new(Ns + name, content);
        var task = E("Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo",
                E("Description", "Starts UnifiedRGB at logon, elevated, so its sensors and fan control work without a prompt.")),
            E("Triggers",
                E("LogonTrigger", E("Enabled", "true"), E("UserId", userSid))),
            E("Principals",
                E("Principal", new XAttribute("id", "Author"),
                    E("UserId", userSid),
                    E("LogonType", "InteractiveToken"),
                    E("RunLevel", "HighestAvailable"))),
            E("Settings",
                E("MultipleInstancesPolicy", "IgnoreNew"),
                E("DisallowStartIfOnBatteries", "false"),
                E("StopIfGoingOnBatteries", "false"),
                E("AllowHardTerminate", "true"),
                E("StartWhenAvailable", "false"),
                E("RunOnlyIfNetworkAvailable", "false"),
                E("IdleSettings", E("StopOnIdleEnd", "false"), E("RestartOnIdle", "false")),
                E("AllowStartOnDemand", "true"),
                E("Enabled", "true"),
                E("Hidden", "false"),
                E("RunOnlyIfIdle", "false"),
                E("WakeToRun", "false"),
                E("ExecutionTimeLimit", "PT0S"),
                E("Priority", "4")),
            E("Actions", new XAttribute("Context", "Author"),
                E("Exec", E("Command", command), E("Arguments", "--autostart"))));
        return task.ToString();
    }

    /// <summary>The parts of an existing task that decide whether it needs
    /// re-registering, with the schema's defaults for anything left out.</summary>
    internal sealed record TaskShape(string Command, string RunLevel, int Priority, string TimeLimit,
        bool NoStartOnBattery, bool StopOnBattery)
    {
        internal static TaskShape Parse(string xml)
        {
            var doc = XDocument.Parse(xml);
            string? Get(string name) => doc.Descendants(Ns + name).FirstOrDefault()?.Value.Trim();
            return new TaskShape(
                Command: Get("Command") ?? "",
                RunLevel: Get("RunLevel") ?? "LeastPrivilege",
                Priority: int.TryParse(Get("Priority"), out int pr) ? pr : 7,
                TimeLimit: Get("ExecutionTimeLimit") ?? "PT72H",
                NoStartOnBattery: !string.Equals(Get("DisallowStartIfOnBatteries"), "false", StringComparison.OrdinalIgnoreCase),
                StopOnBattery: !string.Equals(Get("StopIfGoingOnBatteries"), "false", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Why this task is not the one BuildTaskXml makes for
        /// <paramref name="command"/>; null when it is.</summary>
        internal string? Outdated(string command)
        {
            var why = new List<string>();
            if (!SamePath(Command.Trim('"'), command)) why.Add($"runs {Command}");
            if (RunLevel != "HighestAvailable") why.Add($"run level {RunLevel}");
            if (Priority != 4) why.Add($"priority {Priority}");
            if (TimeLimit != "PT0S") why.Add($"time limit {TimeLimit}");
            if (NoStartOnBattery) why.Add("no start on battery");
            if (StopOnBattery) why.Add("stop on battery");
            return why.Count == 0 ? null : string.Join(", ", why);
        }
    }

    /// <summary>Which exe the task should run after an automatic check (not the
    /// checkbox). A release build moves a task that runs a downloaded exe to
    /// the installed copy; a task that runs a development tree's exe is left
    /// on it. Pure, for the harness.</summary>
    internal static bool ShouldRepoint(bool packaged, string current, string installedExe) =>
        packaged && !IsDevBuildPath(current) && !SamePath(current, installedExe);

    static void RegisterTask(string command)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("no user SID");
        const int CreateOrUpdate = 6, InteractiveToken = 3;
        RootFolder().RegisterTask(TaskName, BuildTaskXml(command, sid), CreateOrUpdate, null, null, InteractiveToken, null);
    }

    public static bool IsAutoStartEnabled()
    {
        try { return ReadTaskXml() != null; }
        catch (Exception ex)
        {
            Log.Warn("autostart", $"could not read the logon task: {ex.Message}");
            return false;
        }
    }

    // Every change to the task and the install runs under this, and a toggle
    // applies the LATEST request: each tick of the checkbox starts its own
    // background run, and an enable that finished after a later disable used
    // to leave the task registered under an unticked box.
    static readonly object _taskGate = new();
    static int _wanted;   // 1 = on, 0 = off: the last request made

    /// <summary>Create or remove the logon task. False (logged) when Task
    /// Scheduler refused, or a later toggle overtook this one, so the checkbox
    /// re-reads the task's real state.</summary>
    public static bool SetAutoStart(bool enable)
    {
        Volatile.Write(ref _wanted, enable ? 1 : 0);
        lock (_taskGate)
        {
            bool want = Volatile.Read(ref _wanted) == 1;
            return ApplyAutoStart(want) && want == enable;
        }
    }

    static bool ApplyAutoStart(bool enable)
    {
        try
        {
            if (!enable)
            {
                if (ReadTaskXml() != null) RootFolder().DeleteTask(TaskName, 0);
                // Not while running from it: it goes on a later launch of the
                // downloaded exe (ReconcileAutostart, with no task).
                RemoveInstall();
                return true;
            }
            string command;
            if (IsPackaged)
            {
                // The checkbox installs THIS build, older or newer: the way to
                // move the logon copy back to the build the user is running.
                if (!RunningInstalled && !EnsureInstalledCopy(force: true)) return false;
                command = InstalledExe;
            }
            else command = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            RegisterTask(command);
            Log.Info("autostart", $"logon task registered for {command}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("autostart", $"logon task {(enable ? "create" : "delete")} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>At startup, off the UI thread: bring a task made by an older
    /// build up to date. A release build moves a task that runs a downloaded
    /// exe to the installed copy; every task gets its settings corrected and
    /// keeps the exe otherwise. Without a task, a leftover install is removed.</summary>
    public static void ReconcileAutostart()
    {
        lock (_taskGate)
        {
            try
            {
                string? xml = ReadTaskXml();
                if (xml == null)
                {
                    if (IsPackaged) RemoveInstall();
                    return;
                }
                var shape = TaskShape.Parse(xml);
                string command = shape.Command.Trim('"');
                if (ShouldRepoint(IsPackaged, command, InstalledExe) && (RunningInstalled || EnsureInstalledCopy(force: false)))
                    command = InstalledExe;
                if (shape.Outdated(command) is string why)
                {
                    RegisterTask(command);
                    Log.Info("autostart", $"logon task updated ({why}) -> {command}");
                }
            }
            catch (Exception ex) { Log.Warn("autostart", $"logon task check failed: {ex.Message}"); }
        }
        SweepOldRuntimes();
    }
}
